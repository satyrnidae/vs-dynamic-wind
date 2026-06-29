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
    private static FieldInfo? fPropagationDir;
    // Per-rotor "wind is hitting the back" flag, shared between the patches.
    private static readonly ConditionalWeakTable<BEBehaviorMPRotor, StrongBox<bool>> reversed = new();

    public static void Apply(Harmony harmony, ILogger logger)
    {
        try
        {
            MethodInfo checkWind = AccessTools.Method(typeof(BEBehaviorWindmillRotor), "CheckWindSpeed");
            MethodInfo getTorque = AccessTools.Method(typeof(BEBehaviorMPRotor), "GetTorque");
            fWindSpeed = AccessTools.Field(typeof(BEBehaviorWindmillRotor), "windSpeed");
            fOwnFacing = AccessTools.Field(typeof(BEBehaviorMPRotor), "ownFacing");
            fPropagationDir = AccessTools.Field(typeof(BEBehaviorMPBase), "propagationDir");
            if (checkWind == null || getTorque == null || fWindSpeed == null || fOwnFacing == null || fPropagationDir == null)
            {
                logger.Warning("[dynamicwind] Windmill patch target not found; windmills keep vanilla behaviour.");
                return;
            }
            harmony.Patch(checkWind, postfix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(CheckWindSpeedPostfix)));
            harmony.Patch(getTorque,
                prefix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(GetTorquePrefix)),
                postfix: new HarmonyMethod(typeof(WindmillDirectionality), nameof(GetTorquePostfix)));
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

        // Align against the rotor's own facing axis. (The earlier "E/W↔N/S reversed" report was a
        // spin-DIRECTION inversion — a 180° flip — not a 90° axis swap, so we keep ownFacing and flip the
        // sign below instead of rotating the axis.)
        double ax = axle.Normalf.X;
        double az = axle.Normalf.Z;
        double al = Math.Sqrt(ax * ax + az * az);
        if (al < 1e-6) return;

        // SIGNED alignment (no Math.Abs). The sign chooses spin direction; magnitude is the power.
        double dot = (w.X * ax + w.Y * az) / (len * al); // [-1, 1]
        double mag = Math.Abs(dot);
        if (mag < cfg.WindmillMinEfficiency) mag = cfg.WindmillMinEfficiency;
        if (mag > 1.0) mag = 1.0;

        // windSpeed stays the positive torque MAGNITUDE; reversal is done by flipping the drive direction
        // in GetTorque (so the vanilla speed governor still caps the speed — negating torque does not).
        double ws = (double)fWindSpeed.GetValue(__instance)!;
        fWindSpeed.SetValue(__instance, ws * mag);

        // Update the wind-reversal state and, on a TRANSITION, reverse the whole mechanical network the
        // way vanilla does: MechanicalNetwork.DirectionHasReversed flips the network's speed sign once on
        // the next network update. Flipping only the rotor's torque sign (the GetTorque prefix below) is
        // NOT enough — the engine's speed integrator clamps |speed| >= 1e-6 and keeps its sign in the
        // braking branch, only flipping sign when |torque - resistance| is tiny. A low-resistance lone
        // windmill coasts through zero and reverses, but a geared assembly (e.g. a large gear, whose big
        // gearedRatio inflates networkResistance) never reaches that branch and would stall at ~0 forever.
        // Flipping the network speed in step with the torque sign keeps the rotor DRIVING (not braking) so
        // the assembly spins up the other way.
        bool nowReversed = dot > 0; // 180° flip vs the naive sign
        StrongBox<bool> box = reversed.GetOrCreateValue(__instance);
        if (nowReversed != box.Value)
        {
            box.Value = nowReversed;
            MechanicalNetwork? net = __instance.Network;
            if (net != null) net.DirectionHasReversed = true;
        }
    }

    // When the wind is on the rotor's back, flip its network propagation direction for the duration of
    // GetTorque. That flips the `num` sign inside, so the rotor drives the network the other way AND the
    // vanilla speed-governor branch applies in reverse (capping the speed). Restored immediately after.
    private static void GetTorquePrefix(BEBehaviorMPRotor __instance, out object? __state)
    {
        __state = null;
        if (fPropagationDir == null) return;
        if (reversed.TryGetValue(__instance, out StrongBox<bool>? box) && box.Value
            && fPropagationDir.GetValue(__instance) is BlockFacing dir)
        {
            __state = dir;
            fPropagationDir.SetValue(__instance, dir.Opposite);
        }
    }

    private static void GetTorquePostfix(BEBehaviorMPRotor __instance, object? __state)
    {
        if (__state is BlockFacing dir && fPropagationDir != null)
            fPropagationDir.SetValue(__instance, dir);
    }
}
