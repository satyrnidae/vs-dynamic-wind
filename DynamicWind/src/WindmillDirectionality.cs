using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace DynamicWind;

/// <summary>
/// Server Harmony patch giving windmills directional response. Vanilla
/// <see cref="BEBehaviorWindmillRotor"/>.CheckWindSpeed sets a scalar <c>windSpeed</c> (strength only).
/// Our postfix scales it by |cos θ| between the wind and the rotor axle (<c>ownFacing</c>) — parallel =
/// full power, perpendicular = stalled — and records the SIGN of the alignment: when the wind comes from
/// behind the rotor (cos θ &lt; 0) the mill should turn backwards.
///
/// The engine can't reverse from a negative windSpeed alone (<see cref="BEBehaviorMPRotor"/>.GetTorque
/// does <c>Max(0, …)</c>, so a negative target just stalls). So we keep windSpeed as the positive torque
/// magnitude and instead NEGATE the rotor's torque in a GetTorque postfix when it's wind-reversed, which
/// drives the mechanical network the other way.
/// </summary>
internal static class WindmillDirectionality
{
    private static FieldInfo? fWindSpeed;
    private static FieldInfo? fOwnFacing;
    // Per-rotor "wind is hitting the back" flag, shared between the two patches.
    private static readonly ConditionalWeakTable<BEBehaviorMPRotor, StrongBox<bool>> reversed = new();

    public static void Apply(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo checkWind = AccessTools.Method(typeof(BEBehaviorWindmillRotor), "CheckWindSpeed");
            MethodInfo getTorque = AccessTools.Method(typeof(BEBehaviorMPRotor), "GetTorque");
            fWindSpeed = AccessTools.Field(typeof(BEBehaviorWindmillRotor), "windSpeed");
            fOwnFacing = AccessTools.Field(typeof(BEBehaviorMPRotor), "ownFacing");
            if (checkWind == null || getTorque == null || fWindSpeed == null || fOwnFacing == null)
            {
                logger.Warning("[dynamicwind] Windmill patch target not found; windmills keep vanilla behaviour.");
                return;
            }
            harmony.Patch(checkWind, postfix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(CheckWindSpeedPostfix)));
            harmony.Patch(getTorque, postfix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(GetTorquePostfix)));
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch windmill rotor; windmills keep vanilla behaviour: {0}", e);
        }
    }

    private static void CheckWindSpeedPostfix(BEBehaviorWindmillRotor __instance)
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

        double ax = axle.Normalf.X;
        double az = axle.Normalf.Z;
        double al = Math.Sqrt(ax * ax + az * az);
        if (al < 1e-6) return;

        // SIGNED alignment (no Math.Abs): >0 wind into the rotor's face, <0 wind from behind → reverse.
        double dot = (w.X * ax + w.Y * az) / (len * al); // [-1, 1]
        double mag = Math.Abs(dot);
        if (mag < cfg.WindmillMinEfficiency) mag = cfg.WindmillMinEfficiency;
        if (mag > 1.0) mag = 1.0;

        // windSpeed stays the positive torque magnitude (the engine reverses via the torque sign, below).
        double ws = (double)fWindSpeed.GetValue(__instance)!;
        fWindSpeed.SetValue(__instance, ws * mag);
        reversed.GetOrCreateValue(__instance).Value = dot < 0;
    }

    private static void GetTorquePostfix(BEBehaviorMPRotor __instance, ref float __result)
    {
        if (reversed.TryGetValue(__instance, out StrongBox<bool>? box) && box.Value)
            __result = -__result; // wind on the back → drive the network in reverse
    }
}
