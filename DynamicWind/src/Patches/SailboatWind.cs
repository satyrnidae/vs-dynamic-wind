using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.Entities;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Harmony patch making sailboats respond to wind direction. Vanilla <see cref="EntityBoat"/>
/// SeatsToMotion adds a constant sail force regardless of wind; our postfix replaces that force with one
/// derived from how the wind meets the trimmed SAIL (see <see cref="EntityBehaviorSailTrim"/>), falling
/// back to the older hull-heading-vs-wind curve when <c>SailboatSailAngleEnabled</c> is off.
///
/// Patched on the base type (subclasses inherit); also patches an overriding EntityShipwrightBoat if that
/// mod is present. Applied on BOTH sides — EntityBoat runs SeatsToMotion from OnRenderFrame client-side
/// for prediction and from OnGameTick server-side, and the two must agree or the boat visibly rubber-bands.
/// </summary>
internal static class SailboatWind
{
    // Former DynamicWindConfig tunables, now fixed constants (see DynamicWindConfig's class remarks).
    private const float SailboatTailwindMaxBoost = 0.5f;
    private const float SailboatBeamReachMaxBoost = 0.3f;
    private const float SailboatHeadwindMaxPenalty = 0.1f;
    private const float SailboatMinThrust = 0.5f;
    private const float SailboatWindSpeedInfluence = 0.6f;
    private const float SailboatReferenceWindSpeed = 1.0f;

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

        // Alternate trim control (Ctrl+A/D repurposing the tiller's own turn keys) — checked regardless of
        // furl state, same as the bracket-key path, since trim works in all three sail positions.
        if (cfg.SailboatSailAngleEnabled) HandleCtrlTrim(__instance, ref __result);

        // Only when under sail.
        if (__instance.WatchedAttributes.GetInt("sailPosition", 0) <= 0) return;

        Vec3d pos = __instance.Pos.XYZ;
        Vec2d w = field.WindAt(pos.X, pos.Z, __instance.World.Calendar.TotalHours);
        double len = w.Length();
        if (len < 1e-6) return;

        // Travel direction = −viewVector (boat moves opposite its view vector for ForwardSpeed > 0).
        Vec3f vv = __instance.Pos.GetViewVector();
        double tx = -vv.X;
        double tz = -vv.Z;
        double tl = Math.Sqrt(tx * tx + tz * tz);
        if (tl < 1e-6) return;

        tx /= tl;
        tz /= tl;
        double wx = w.X / len;
        double wz = w.Y / len;

        // Vanilla zeroes paddleForce whenever the sails are up, so with sailPosition > 0 the whole of
        // __result.X IS the sail term — scaling it outright is a complete substitution, not a nudge.
        __result.X *= cfg.SailboatSailAngleEnabled
            ? TrimmedFactor(__instance, tx, tz, wx, wz, len)
            : LegacyFactor(tx, tz, wx, wz);
    }

    /// <summary>
    /// Alternate trim control: holding Ctrl repurposes the tiller's own turn keys (A/D → <c>EntityControls
    /// .Left/Right</c>) to swing the sail instead of the hull, so trimming doesn't need a second pair of
    /// hands off the steering keys — the bracket-key hotkeys (<c>SailTrimInput</c>) keep working
    /// independently alongside this. Reads <c>EntityBehaviorSeatable.ControllingControls</c> — the same
    /// synced, server-authoritative control state vanilla's own SeatsToMotion already used just above to
    /// compute <paramref name="__result"/>'s turn term — rather than a new hotkey/packet, so this needs no
    /// extra network traffic and works identically on both sides. Must run in the SAME postfix that
    /// produced the turn term: suppressing it and redirecting Left/Right to the sail have to happen
    /// together, or the hull would keep spinning under Ctrl while the sail also swings.
    /// </summary>
    private static void HandleCtrlTrim(EntityBoat boat, ref Vec2d __result)
    {
        EntityBehaviorSeatable? seatable = boat.GetBehavior<EntityBehaviorSeatable>();
        EntityControls? controls = seatable?.ControllingControls;
        EntityBehaviorSailTrim? trim = boat.GetBehavior<EntityBehaviorSailTrim>();
        if (controls == null || trim == null) return;

        if (!controls.CtrlKey)
        {
            trim.SetCtrlTrimIntent(false, 0);
            return;
        }

        __result.Y = 0; // suppress vanilla's own turn term while Ctrl repurposes A/D for trim
        sbyte dir = 0;
        if (controls.Left) dir--;
        if (controls.Right) dir++;
        trim.SetCtrlTrimIntent(true, dir);
    }

    /// <summary>
    /// Thrust from how the wind meets the SAIL. The sail's normal points dead ahead at zero trim and
    /// swings with the yard, so two dot products describe the whole thing: how squarely the wind hits the
    /// sail's front face, and how much of that push actually points where the boat is going. Feathered
    /// (±90°) or backwinded therefore falls to <c>SailboatMinThrust</c> rather than to nothing, so a
    /// badly trimmed boat still creeps home instead of dying in the water.
    ///
    /// On top of that base curve (which already floors at feathered/backwinded and ceilings at a squared
    /// sail running dead before the wind), a second term rewards trimming for actual aerodynamic LIFT
    /// rather than pure drag: <c>lift</c> near 0.5 — the sail bisecting the angle to the wind, roughly a
    /// beam reach — acts as an airfoil, whereas <c>lift</c> near 0 (luffing) or 1 (square to the wind, like
    /// a parachute) generates none. <c>4·lift·(1−lift)</c> is the parabola peaking at exactly that midpoint
    /// and vanishing at both ends, so it adds a beam-reach bonus without disturbing the existing floor or
    /// ceiling — a well-trimmed reach beats a badly-trimmed one, but dead tailwind with a squared sail
    /// still gives the single highest number, matching how these boats actually sail fastest downwind.
    /// </summary>
    private static double TrimmedFactor(EntityBoat boat, double tx, double tz, double wx, double wz, double windSpeed)
    {
        (double lift, double drive) = SailAlignment(boat, tx, tz, wx, wz);
        double trim = lift * drive;
        double beamBonus = 4.0 * lift * (1.0 - lift) * drive;

        double speedFactor = SpeedFactor(windSpeed);
        double best = 1.0 + SailboatTailwindMaxBoost;

        double factor = SailboatMinThrust
            + (best - SailboatMinThrust) * trim * speedFactor
            + SailboatBeamReachMaxBoost * beamBonus * speedFactor;

        // Half sail should catch roughly half the wind, not the same as full sail. Furled (sailPosition 0)
        // never reaches here — the caller already gates the whole thrust replacement on sailPosition > 0.
        int sailPosition = boat.WatchedAttributes.GetInt("sailPosition");
        return factor * sailPosition; // sailPosition is 0..2 so no boost at furl, 1x at half, 2x at full.
    }

    /// <summary>
    /// How squarely the wind hits the sail's front face (<c>lift</c>, 0..1) and how much of that push
    /// points where the boat is going (<c>drive</c>, 0..1) — the two dot products <see cref="TrimmedFactor"/>
    /// builds thrust from. Also the "how full does the sail look" signal <c>EntityBehaviorSailTrim</c>
    /// drives its billow animation from (via <c>lift</c> alone — billow is about how the wind fills the
    /// sail, not whether that happens to push the boat where it's going).
    /// </summary>
    internal static (double lift, double drive) SailAlignment(EntityBoat boat, double tx, double tz, double wx, double wz)
    {
        // No behaviour → an unpatched or third-party boat whose sail cannot swing; treat it as square.
        double angle = (boat.GetBehavior<EntityBehaviorSailTrim>()?.CurrentAngleDeg ?? 0f) * GameMath.DEG2RAD;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        // Sail normal = travel direction rotated by the trim angle about Y.
        double nx = tx * cos - tz * sin;
        double nz = tx * sin + tz * cos;

        double lift = GameMath.Clamp(wx * nx + wz * nz, 0, 1);   // wind on the sail's front face
        double drive = GameMath.Clamp(nx * tx + nz * tz, 0, 1);  // ...pushing us forward rather than sideways
        return (lift, drive);
    }

    /// <summary>How much wind MAGNITUDE (rather than just alignment) scales into the result, referenced
    /// against <see cref="SailboatReferenceWindSpeed"/>.</summary>
    internal static double SpeedFactor(double windSpeed)
    {
        double spd = GameMath.Clamp(windSpeed / SailboatReferenceWindSpeed, 0, 1);
        return (1.0 - SailboatWindSpeedInfluence) + SailboatWindSpeedInfluence * spd;
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
}
