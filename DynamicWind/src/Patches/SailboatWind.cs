using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.Entities;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Harmony patch making sailboats respond to wind direction and speed. Vanilla <see cref="EntityBoat"/>
/// SeatsToMotion adds a constant sail force regardless of wind; our postfix multiplies that vanilla
/// baseline by <c>1 + windBoost + angleBoost</c> - windBoost from local wind speed alone, angleBoost from
/// how the trimmed SAIL (see <see cref="EntityBehaviorSailTrim"/>) sits relative to the wind alone (not
/// boat travel direction), further blended toward <see cref="SailCrosswindEfficiency"/> near full-feather
/// trim (see <see cref="CrosswindBlendedAngleBoost"/>) - falling back to the older hull-heading-vs-wind
/// curve when <c>SailboatSailAngleEnabled</c> is off.
///
/// Patched on the base type (subclasses inherit); also patches an overriding EntityShipwrightBoat if that
/// mod is present. Applied on BOTH sides - EntityBoat runs SeatsToMotion from OnRenderFrame client-side
/// for prediction and from OnGameTick server-side, and the two must agree or the boat visibly rubber-bands.
/// </summary>
internal static class SailboatWind
{
    #region Constants

    // Former DynamicWindConfig tunables, now fixed constants (see DynamicWindConfig's class remarks).
    private const float SailboatTailwindMaxBoost = 0.5f;
    private const float SailboatHeadwindMaxPenalty = 0.1f;

    // Wind-speed term: windBoost = f*Slope - Base, f = windSpeed/WindReference.Speed (unclamped
    // above 1 - a storm keeps extrapolating the boost upward, only floored at f=0).
    private const float SailboatWindBoostSlope = 0.4f;
    private const float SailboatWindBoostBase = 0.15f;

    // Sail-angle term: +SailboatAngleBoostMax at pure tailwind, -SailboatAngleBoostMax at pure headwind,
    // 0 at pure crosswind. The headwind->crosswind half additionally overshoots to a "tacking" peak via
    // SailboatTackBonusCoefficient (see AngleBoost below) - swiveling off dead headwind rewards trim before
    // the boost settles back to 0 at crosswind, matching how a helmsman actually recovers ground upwind.
    private const float SailboatAngleBoostMax = 0.25f;
    private const float SailboatTackBonusCoefficient = 1.05f;

    private const float SailCrosswindEfficiency = 0f;
    private const float SailboatTackBoostAngleDeg = 45f; // where the crosswind falloff begins

    #endregion

    #region Harmony Patch Entry Point

    public static void Apply(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo baseTarget = AccessTools.Method(typeof(EntityBoat), "SeatsToMotion");
            if (baseTarget == null)
            {
                logger.Warning("[dynamicwind] EntityBoat.SeatsToMotion not found; sailboats keep vanilla behaviour.");
                return;
            }
            var postfix = new HarmonyMethod(typeof(SailboatWind), nameof(Postfix));
            harmony.Patch(baseTarget, postfix: postfix);

            // Optional: a third-party boat that OVERRIDES SeatsToMotion needs its own patch.
            Type? shipwright = AccessTools.TypeByName("Vintagestory.GameContent.EntityShipwrightBoat");
            if (shipwright != null)
            {
                MethodInfo? swTarget = AccessTools.DeclaredMethod(shipwright, "SeatsToMotion");
                if (swTarget != null) harmony.Patch(swTarget, postfix: postfix);
            }
            logger.Notification("[dynamicwind] Sailboat wind response enabled.");
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch sailboat motion; sailboats keep vanilla behaviour: {0}", e);
        }
    }

    private static void Postfix(EntityBoat __instance, ref Vec2d __result)
    {
        EnumAppSide side = __instance.World.Side;
        DynamicWindConfig? cfg = DynamicWindRuntime.ConfigFor(side);
        WindField? field = DynamicWindRuntime.FieldFor(side);
        if (cfg == null || field == null || !cfg.SailboatEnabled) return;

        // Alternate trim control (Ctrl+A/D repurposing the tiller's own turn keys) - checked regardless of
        // furl state, same as the bracket-key path, since trim works in all three sail positions.
        if (cfg.SailboatSailAngleEnabled) HandleCtrlTrim(__instance, ref __result);

        // Only when under sail.
        if (__instance.WatchedAttributes.GetInt("sailPosition", 0) <= 0) return;

        Vec3d pos = __instance.Pos.XYZ;
        // GetWindSpeedAt, NOT field.WindAt: WindAt is the raw directional multiplier field (magnitude
        // ~BaseStrength, ~0.7, even in dead calm - it's what STEERS the wind, not how hard it blows).
        // GetWindSpeedAt runs the full OnGetWindSpeed hook (vanilla's own calm/storm noise + front excess +
        // orographic + occlusion), the actual speed everything else in this mod (particles, foliage,
        // windmills, vanes) reads. Using WindAt here made a well-trimmed sail thrust the boat even at a
        // genuine 0% displayed wind speed, since WindAt's magnitude never actually reaches zero.
        Vec3d w3 = __instance.World.BlockAccessor.GetWindSpeedAt(pos);
        Vec2d w = new Vec2d(w3.X, w3.Z);
        double len = w.Length();

        // Travel direction = -viewVector (boat moves opposite its view vector for ForwardSpeed > 0). Still
        // needed as the reference frame the sail normal rotates away from, even though thrust no longer
        // depends on travel direction directly.
        Vec3f vv = __instance.Pos.GetViewVector();
        double tx = -vv.X;
        double tz = -vv.Z;
        double tl = Math.Sqrt(tx * tx + tz * tz);
        if (tl < 1e-6) return;
        tx /= tl;
        tz /= tl;

        // Dead calm: 0% wind is no longer automatically "no thrust" (windBoost alone already applies its
        // -15% floor at f=0), so a well-tacked sail still creeps in dead calm rather than stopping outright.
        // wx/wz = 0 makes cosGamma (and so angleBoost) harmlessly neutral instead of dividing by zero.
        double wx = len < 1e-6 ? 0 : w.X / len;
        double wz = len < 1e-6 ? 0 : w.Y / len;

        // Vanilla zeroes paddleForce whenever the sails are up, so with sailPosition > 0 the whole of
        // __result.X IS the sail term - scaling it outright is a complete substitution, not a nudge.
        __result.X *= cfg.SailboatSailAngleEnabled
            ? TrimmedFactor(__instance, tx, tz, wx, wz, len)
            : LegacyFactor(tx, tz, wx, wz);
    }

    #endregion

    #region Trim Control Input

    /// <summary>
    /// Trim control: holding Ctrl repurposes the tiller's own turn keys (A/D -> <c>EntityControls.Left
    /// /Right</c>) to swing the sail instead of the hull. Reads <c>EntityBehaviorSeatable.ControllingControls</c>
    /// - the same synced, server-authoritative control state vanilla's own SeatsToMotion already used just
    /// above to compute <paramref name="__result"/>'s turn term - rather than a dedicated hotkey/packet, so
    /// this needs no extra network traffic and works identically on both sides. Must run in the SAME
    /// postfix that produced the turn term: suppressing it and redirecting Left/Right to the sail have to
    /// happen together, or the hull would keep spinning under Ctrl while the sail also swings. This is the
    /// sole trim input path - an earlier bracket-key hotkey + packet path was removed once this was
    /// confirmed working.
    /// </summary>
    private static void HandleCtrlTrim(EntityBoat boat, ref Vec2d __result)
    {
        EntityBehaviorSeatable? seatable = boat.GetBehavior<EntityBehaviorSeatable>();
        EntityControls? controls = seatable?.ControllingControls;
        EntityBehaviorSailTrim? trim = boat.GetBehavior<EntityBehaviorSailTrim>();
        if (controls == null || trim == null) return;

        if (!controls.CtrlKey)
        {
            trim.SetTrimIntent(0);
            return;
        }

        __result.Y = 0; // suppress vanilla's own turn term while Ctrl repurposes A/D for trim
        sbyte dir = 0;
        if (controls.Left) dir--;
        if (controls.Right) dir++;
        trim.SetTrimIntent(dir);
    }

    #endregion

    #region Wind and Angle Boost Calculation

    /// <summary>
    /// Thrust multiplier on vanilla's constant sail force: <c>1 + windBoost + angleBoost</c>, the two terms
    /// entirely independent of each other and of boat travel direction.
    ///
    /// <c>windBoost</c> is linear in wind speed alone (<see cref="WindSpeedFraction"/>, unclamped above
    /// 100% so a storm keeps extrapolating the boost upward): -15% in dead calm, +25% at a "100%" reference
    /// wind, floored only at zero speed (can't blow negative).
    ///
    /// <c>angleBoost</c> is a function of <c>cosGamma</c> alone - the alignment between the wind and the
    /// sail's own normal (<see cref="SailNormal"/>), independent of where the boat is actually heading:
    /// +25% at pure tailwind, -25% at pure headwind, 0% at pure crosswind. The crosswind->tailwind half is a
    /// plain ramp between those two anchors. The headwind->crosswind half is NOT a plain ramp: swiveling off
    /// dead headwind rewards trim with a "tacking" bonus that peaks near +15% - well above both of that
    /// half's own endpoints - before settling back down to 0% at crosswind, via a parabola
    /// (<c>K*s*(1-s)</c>) that vanishes at both ends by construction regardless of <c>K</c>, so the -25%
    /// headwind floor and the 0% crosswind point are exact regardless of how the peak is tuned.
    ///
    /// Beyond <see cref="SailboatTackBoostAngleDeg"/> trim, the angle term itself is further blended toward
    /// <see cref="SailCrosswindEfficiency"/> - see <see cref="CrosswindBlendedAngleBoost"/>.
    /// </summary>
    private static double TrimmedFactor(EntityBoat boat, double tx, double tz, double wx, double wz, double windSpeed)
    {
        double trimAngleDeg = boat.GetBehavior<EntityBehaviorSailTrim>()?.CurrentAngleDeg ?? 0f;
        (double nx, double nz) = SailNormal(tx, tz, trimAngleDeg);
        double cosGamma = GameMath.Clamp(wx * nx + wz * nz, -1.0, 1.0);
        return FactorForCosGamma(cosGamma, windSpeed, trimAngleDeg);
    }

    /// <summary>
    /// The whole trimmed-sail thrust curve as a pure function of <c>cosGamma</c> (wind-to-sail alignment,
    /// see <see cref="SailNormal"/>) and wind speed, factored out of <see cref="TrimmedFactor"/> so the
    /// live diagnostics graph (<see cref="Evaluate"/>, <c>Client/Gui/GuiDialogSailGraph</c>) plots the
    /// EXACT function driving thrust rather than a hand-copied duplicate that could silently drift out of
    /// sync with it.
    ///
    /// Deliberately does NOT scale by <c>sailPosition</c> - vanilla's OWN base sail-force term
    /// (`EntityBoat.SeatsToMotion`, before this postfix ever runs) already differs between half and full
    /// sail: it's linear in `sailPosition` (`num4 * dt * sailPosition * 1.5f` - half gets 1x, full gets
    /// 2x), so `__result.X` going into <see cref="Postfix"/> already carries that distinction. An earlier
    /// version of this factor ALSO scaled by `sailPosition / 2.0` on top of that - double-applying the
    /// halving, so half sail ended up delivering roughly a QUARTER of full sail's thrust instead of
    /// vanilla's own intended half. Removing it lets this factor scale vanilla's already-correct baseline
    /// uniformly, regardless of sail position. <c>sailPosition == 0</c> (furled) is a pure on/off gate
    /// handled entirely by the caller (<see cref="Postfix"/> returns before reaching here at all) - not a
    /// scaling case this function needs to represent.
    ///
    /// Takes <c>trimAngleDeg</c> so its angle term can route through <see cref="CrosswindBlendedAngleBoost"/>
    /// rather than the raw <see cref="AngleBoost"/> curve directly.
    /// </summary>
    internal static double FactorForCosGamma(double cosGamma, double windSpeed, double trimAngleDeg) =>
        1.0 + WindBoost(windSpeed) + CrosswindBlendedAngleBoost(cosGamma, trimAngleDeg);

    /// <summary>Wind-speed term alone (see <see cref="TrimmedFactor"/>'s remarks) - independent of
    /// <c>cosGamma</c>, so on the diagnostics graph this plots as a flat line that only moves up/down as
    /// wind speed changes, letting a bug in this term (vs. the angle term below) be told apart at a
    /// glance.</summary>
    internal static double WindBoost(double windSpeed) =>
        WindSpeedFraction(windSpeed) * SailboatWindBoostSlope - SailboatWindBoostBase;

    /// <summary>Sail-angle term alone (see <see cref="TrimmedFactor"/>'s remarks) - independent of wind
    /// speed, so on the diagnostics graph this is the fixed curve shape (tailwind ramp, headwind floor,
    /// tacking bump) regardless of how hard the wind is blowing.</summary>
    internal static double AngleBoost(double cosGamma)
    {
        if (cosGamma >= 0) return SailboatAngleBoostMax * cosGamma;
        double s = -cosGamma; // 0 at crosswind, 1 at dead headwind
        return -SailboatAngleBoostMax * s + SailboatTackBonusCoefficient * s * (1.0 - s);
    }

    /// <summary>The angle term actually used for thrust - <see cref="AngleBoost"/>'s raw cosGamma curve,
    /// blended toward <see cref="SailCrosswindEfficiency"/> as |trimAngleDeg| moves from
    /// <see cref="SailboatTackBoostAngleDeg"/> out to full feather (90 deg, the geometric trim maximum).
    /// Below <see cref="SailboatTackBoostAngleDeg"/> this returns AngleBoost unmodified; square-root blend
    /// above it.</summary>
    internal static double CrosswindBlendedAngleBoost(double cosGamma, double trimAngleDeg)
    {
        double raw = AngleBoost(cosGamma);
        double mag = Math.Abs(trimAngleDeg);
        if (mag <= SailboatTackBoostAngleDeg) return raw;
        double t = Math.Sqrt((mag - SailboatTackBoostAngleDeg) / (90.0 - SailboatTackBoostAngleDeg));
        return raw + t * (SailCrosswindEfficiency - raw);
    }

    #endregion

    #region Diagnostics Snapshot (Evaluate)

    /// <summary>Live snapshot of the sail-thrust model for a mounted boat - a general-purpose readout, not
    /// just for diagnostics: both the trim graph (<c>.dwind sailgraph</c>) AND
    /// <c>EntityBehaviorSailTrim.UpdateBillow</c> read it as the single source of truth for "how well is
    /// this sail currently catching the wind." Mirrors <see cref="Postfix"/>'s own geometry (wind fetch,
    /// travel direction) exactly so callers can never disagree with what's actually driving the boat.
    /// <c>WindMult</c>/<c>TrimMult</c> are the two RAW terms (see <see cref="WindBoost"/>/
    /// <see cref="CrosswindBlendedAngleBoost"/>), without the <c>1.0 +</c> baseline <see cref="FactorForCosGamma"/> applies
    /// for actual thrust - the raw bonus a system adds/subtracts. Note this is unaffected by
    /// <c>sailPosition</c> either way: <c>FactorForCosGamma</c> itself no longer scales by it, since
    /// vanilla's own base sail-force term already differs by sail position before this factor ever runs
    /// (see <see cref="FactorForCosGamma"/>'s remarks) - so there was never a scaling term here to strip
    /// out in the first place.</summary>
    internal readonly record struct SailState(bool Valid, double WindSpeed, double CosGamma, double WindMult, double TrimMult, int SailPosition, bool AngleModel)
    {
        public double CombinedMult => WindMult + TrimMult;
    }

    internal static SailState Evaluate(EntityBoat boat)
    {
        EnumAppSide side = boat.World.Side;
        DynamicWindConfig? cfg = DynamicWindRuntime.ConfigFor(side);
        WindField? field = DynamicWindRuntime.FieldFor(side);
        if (cfg == null || field == null || !cfg.SailboatEnabled) return default;

        Vec3d pos = boat.Pos.XYZ;

        // Speed for WindMult comes from GetWindSpeedAt (the real, can-genuinely-be-0 displayed speed) -
        // same source Postfix uses for the actual thrust's speed term.
        Vec3d w3 = boat.World.BlockAccessor.GetWindSpeedAt(pos);
        double len = new Vec2d(w3.X, w3.Z).Length();

        // Direction for cosGamma/TrimMult comes from field.WindAt instead - the raw directional field,
        // whose magnitude never actually reaches zero (see Postfix's own remarks on this). Trim bonus is a
        // pure function of wind-to-sail ANGLE and must read the same in dead calm as in a gale; sourcing
        // direction from GetWindSpeedAt's vector (as an earlier version of this did) meant a low-but-real
        // wind speed sample rounded its own direction toward (0,0), which zeroed cosGamma - and so
        // TrimMult - independent of trim or heading, exactly the "trim reads permanently zero" symptom.
        double totalHours = boat.World.Calendar.TotalHours;
        Vec2d dir = field.WindAt(pos.X, pos.Z, totalHours);
        double dirLen = dir.Length();
        if (dirLen < 1e-6) return default;
        double wx = dir.X / dirLen;
        double wz = dir.Y / dirLen;

        Vec3f vv = boat.Pos.GetViewVector();
        double tx = -vv.X;
        double tz = -vv.Z;
        double tl = Math.Sqrt(tx * tx + tz * tz);
        if (tl < 1e-6) return default;
        tx /= tl;
        tz /= tl;

        int sailPosition = boat.WatchedAttributes.GetInt("sailPosition", 0);

        if (!cfg.SailboatSailAngleEnabled)
        {
            return new SailState(true, len, tx * wx + tz * wz, 0, 0, sailPosition, false);
        }

        double trimAngleDeg = boat.GetBehavior<EntityBehaviorSailTrim>()?.CurrentAngleDeg ?? 0f;
        (double nx, double nz) = SailNormal(tx, tz, trimAngleDeg);
        double cosGamma = GameMath.Clamp(wx * nx + wz * nz, -1.0, 1.0);
        return new SailState(true, len, cosGamma, WindBoost(len), CrosswindBlendedAngleBoost(cosGamma, trimAngleDeg), sailPosition, true);
    }

    #endregion

    #region Shared Helpers

    /// <summary>Sail normal = hull forward axis rotated by the given trim angle about Y. Zero trim points
    /// the sail dead ahead; the sign convention matches <see cref="EntityBehaviorSailTrim.CurrentAngleDeg"/>
    /// (+ = trimmed to starboard). Callers fetch <c>CurrentAngleDeg</c> themselves (falling back to 0 for an
    /// unpatched or third-party boat whose sail cannot swing) since they also need the raw angle for
    /// <see cref="CrosswindBlendedAngleBoost"/>.</summary>
    private static (double nx, double nz) SailNormal(double tx, double tz, double trimAngleDeg)
    {
        double angle = trimAngleDeg * GameMath.DEG2RAD;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        return (tx * cos - tz * sin, tx * sin + tz * cos);
    }

    /// <summary>Unclamped wind-speed fraction for the thrust model's windBoost term - keeps extrapolating
    /// above 1.0 so a storm's wind keeps boosting thrust rather than plateauing at the "100%" reference
    /// speed. Floored at 0 (wind speed can't be negative).</summary>
    internal static double WindSpeedFraction(double windSpeed)
    {
        return Math.Max(0.0, windSpeed / WindReference.Speed);
    }

    /// <summary>Pre-trim behaviour: thrust from hull heading vs wind alone. Kept for
    /// <c>SailboatSailAngleEnabled = false</c> so existing worlds can opt out of the new mechanic.</summary>
    private static double LegacyFactor(double tx, double tz, double wx, double wz)
    {
        double align = tx * wx + tz * wz; // [-1, 1]
        return 1.0 + (align >= 0
            ? SailboatTailwindMaxBoost * align
            : SailboatHeadwindMaxPenalty * align);
    }

    #endregion
}
