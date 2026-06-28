using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace DynamicWind;

/// <summary>
/// Server Harmony patch giving windmills directional response. Vanilla
/// <see cref="BEBehaviorWindmillRotor"/>.CheckWindSpeed sets a scalar <c>windSpeed</c> (strength
/// only). Our postfix multiplies it by the cosine of the angle between the wind and the rotor axle
/// (<c>ownFacing</c>): head-on = full power, perpendicular = stalled, from-behind = negative →
/// the rotor spins backwards. Floored by <see cref="WindmillConfig.MinEfficiency"/>.
/// </summary>
internal static class WindmillDirectionality
{
    private static FieldInfo? fWindSpeed;
    private static FieldInfo? fOwnFacing;

    public static void Apply(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo target = AccessTools.Method(typeof(BEBehaviorWindmillRotor), "CheckWindSpeed");
            fWindSpeed = AccessTools.Field(typeof(BEBehaviorWindmillRotor), "windSpeed");
            fOwnFacing = AccessTools.Field(typeof(BEBehaviorMPRotor), "ownFacing");
            if (target == null || fWindSpeed == null || fOwnFacing == null)
            {
                logger.Warning("[dynamicwind] Windmill patch target not found; windmills keep vanilla behaviour.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(Postfix)));
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch windmill rotor; windmills keep vanilla behaviour: {0}", e);
        }
    }

    private static void Postfix(BEBehaviorWindmillRotor __instance)
    {
        DynamicWindConfig? cfg = DynamicWindRuntime.ServerConfig;
        WindField? field = DynamicWindRuntime.ServerField;
        if (cfg == null || field == null || !cfg.Windmill.Directionality) return;
        if (fWindSpeed == null || fOwnFacing == null) return;

        if (fOwnFacing.GetValue(__instance) is not BlockFacing axle) return;
        BlockPos pos = __instance.Blockentity.Pos;

        Vec2d w = field.WindAt(pos.X, pos.Z, __instance.Blockentity.Api.World.Calendar.TotalHours);
        double len = w.Length();
        if (len < 1e-6) return;

        // Axle horizontal unit vector; dot with wind direction gives signed efficiency.
        double ax = axle.Normalf.X;
        double az = axle.Normalf.Z;
        double al = Math.Sqrt(ax * ax + az * az);
        if (al < 1e-6) return;

        double eff = (w.X * ax + w.Y * az) / (len * al); // [-1, 1]
        if (eff < cfg.Windmill.MinEfficiency) eff = cfg.Windmill.MinEfficiency;
        if (eff > 1.0) eff = 1.0;

        double ws = (double)fWindSpeed.GetValue(__instance)!;
        fWindSpeed.SetValue(__instance, ws * eff);
    }
}
