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
/// only). Our postfix multiplies it by |cos θ| between the wind and the rotor axle (<c>ownFacing</c>):
/// wind parallel to the axle (from either side) = full power, perpendicular = stalled. Floored by
/// <see cref="DynamicWindConfig.WindmillMinEfficiency"/> so mills needn't stop dead when crosswind.
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
        if (cfg == null || field == null || !cfg.WindmillDirectionality) return;
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

        // Power depends on how parallel the wind is to the axle, regardless of side: |cosθ|. A VS rotor
        // is double-sided, and a negative windSpeed just stalls it, so use the absolute alignment.
        double eff = Math.Abs((w.X * ax + w.Y * az) / (len * al)); // [0, 1]
        if (eff < cfg.WindmillMinEfficiency) eff = cfg.WindmillMinEfficiency;
        if (eff > 1.0) eff = 1.0;

        double ws = (double)fWindSpeed.GetValue(__instance)!;
        fWindSpeed.SetValue(__instance, Math.Abs(ws) * eff);
    }
}
