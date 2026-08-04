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
    /// Thrust from how the wind meets the SAIL. The sail's normal points dead ahead at zero trim and
    /// swings with the yard, so two dot products describe the whole thing: how squarely the wind hits the
    /// sail's front face, and how much of that push actually points where the boat is going. Feathered
    /// (±90°) or backwinded therefore falls to <c>SailboatMinThrust</c> rather than to nothing, so a
    /// badly trimmed boat still creeps home instead of dying in the water.
    /// </summary>
    private static double TrimmedFactor(EntityBoat boat, double tx, double tz, double wx, double wz, double windSpeed)
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
        double trim = lift * drive;

        double spd = GameMath.Clamp(windSpeed / SailboatReferenceWindSpeed, 0, 1);
        double speedFactor = (1.0 - SailboatWindSpeedInfluence) + SailboatWindSpeedInfluence * spd;

        double best = 1.0 + SailboatTailwindMaxBoost;
        return SailboatMinThrust + (best - SailboatMinThrust) * trim * speedFactor;
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
