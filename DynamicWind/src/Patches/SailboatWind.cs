using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Harmony patch making sailboats respond to wind direction. Vanilla <see cref="EntityBoat"/>
/// SeatsToMotion adds a constant sail force regardless of wind. Our postfix scales the forward
/// component by a factor based on alignment between heading and wind: tailwind = +TailwindMaxBoost,
/// crosswind = base, headwind = −HeadwindMaxPenalty. Patched on the base type (subclasses inherit);
/// also patches an overriding EntityShipwrightBoat if that mod is present.
/// </summary>
internal static class SailboatWind
{
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

        double align = (tx * w.X + tz * w.Y) / (len * tl); // [-1, 1]
        double factor = 1.0 + (align >= 0
            ? cfg.SailboatTailwindMaxBoost * align
            : cfg.SailboatHeadwindMaxPenalty * align);

        __result.X *= factor;
    }
}
