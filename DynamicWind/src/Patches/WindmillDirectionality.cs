using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicWind.Config;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace DynamicWind.Patches;

/// <summary>
/// Server Harmony patch giving windmills directional response. Vanilla
/// <see cref="BEBehaviorWindmillRotor"/>.CheckWindSpeed sets its scalar <c>windSpeed</c> from
/// <c>WeatherSystemBase.WeatherDataSlowAccess.GetWindSpeed(pos)</c> — the same raw, undirected vanilla
/// noise source <see cref="Client.Patches.HudClockWindPatch"/> has to work around — which is completely
/// disconnected from this mod's actual wind field (front excess, jetstream noise, orographic slowdown,
/// occlusion). Our postfix OVERWRITES it (not scales it) with the magnitude of the real, hooked wind at
/// this block (<see cref="IBlockAccessor.GetWindSpeedAt"/>, the same value the HUD/particles/etc. see),
/// then applies |cos θ| between that wind and the rotor axle (<c>ownFacing</c>) — parallel = full power,
/// perpendicular = stalled — and records the SIGN of the alignment: when the wind comes from behind the
/// rotor (cos θ &lt; 0) the mill should turn backwards.
///
/// A real windmill has rotational inertia — its sails don't track a gusty/shifting wind instantaneously —
/// so both the wind's SPEED and its ALIGNMENT (direction) are eased per-rotor (<see cref="SmoothState"/>)
/// toward their raw per-tick readings (<see cref="SpeedEaseRatePerSecond"/>) rather than snapped to them,
/// using vanilla's own tick <c>dt</c> (CheckWindSpeed already runs on a 1s <c>RegisterGameTickListener</c>).
/// Reversal is decided from the EASED alignment too, so a shifting wind eases the mill smoothly down through
/// a stall and back up the other way instead of flipping direction outright the instant the raw wind does.
///
/// The engine can't reverse from a negative windSpeed alone (<see cref="BEBehaviorMPRotor"/>.GetTorque
/// does <c>Max(0, …)</c>, so a negative target just stalls). So we keep windSpeed as the positive torque
/// magnitude and instead NEGATE the rotor's torque in a GetTorque postfix when it's wind-reversed, which
/// drives the mechanical network the other way.
/// </summary>
internal static class WindmillDirectionality
{
    // Windmills have real rotational inertia; ease toward each tick's raw wind reading instead of
    // snapping straight to it (see class remarks).
    private const double SpeedEaseRatePerSecond = 0.4;

    private sealed class SmoothState
    {
        public double Len;
        public double Dot;
        public bool Initialized;
    }

    private static FieldInfo? fWindSpeed;
    private static FieldInfo? fOwnFacing;
    private static FieldInfo? fPropagationDir;
    // Per-rotor "wind is hitting the back" flag, shared between the patches.
    private static readonly ConditionalWeakTable<BEBehaviorMPRotor, StrongBox<bool>> reversed = new();
    // Per-rotor eased wind speed/alignment (see SmoothState).
    private static readonly ConditionalWeakTable<BEBehaviorWindmillRotor, SmoothState> smoothing = new();

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
                // Finalizer, not postfix: must restore propagationDir even if GetTorque throws, or the
                // rotor is left permanently wind-reversed.
                finalizer: new HarmonyMethod(typeof(WindmillDirectionality), nameof(GetTorqueFinalizer)));
            logger.Notification("[dynamicwind] Windmill directionality enabled.");
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch windmill rotor; windmills keep vanilla behaviour: {0}", e);
        }
    }

    private static void CheckWindSpeedPostfix(BEBehaviorWindmillRotor __instance, float dt)
    {
        DynamicWindConfig? cfg = DynamicWindRuntime.ServerConfig;
        if (cfg == null || !cfg.WindmillDirectionality) return;
        if (fWindSpeed == null || fOwnFacing == null) return;

        if (fOwnFacing.GetValue(__instance) is not BlockFacing axle) return;
        BlockPos pos = __instance.Blockentity.Pos;
        ICoreAPI api = __instance.Blockentity.Api;

        // Align against the rotor's own facing axis. (The earlier "E/W↔N/S reversed" report was a
        // spin-DIRECTION inversion — a 180° flip — not a 90° axis swap, so we keep ownFacing and flip the
        // sign below instead of rotating the axis.)
        double ax = axle.Normalf.X;
        double az = axle.Normalf.Z;
        double al = Math.Sqrt(ax * ax + az * az);
        if (al < 1e-6) return;

        // The real, hooked wind at this block — front excess, jetstream noise, orographic slowdown,
        // occlusion all already folded in — NOT vanilla's raw, undirected noise (see class remarks).
        Vec3d trueWind = api.World.BlockAccessor.GetWindSpeedAt(pos.ToVec3d());
        double len = Math.Sqrt(trueWind.X * trueWind.X + trueWind.Z * trueWind.Z);
        // SIGNED alignment (no Math.Abs). The sign chooses spin direction; magnitude is the power. Left at
        // 0 when calm — with len also 0 the eased speed just decays toward 0 regardless (see below).
        double dot = len > 1e-6 ? (trueWind.X * ax + trueWind.Z * az) / (len * al) : 0.0; // [-1, 1]

        // Ease both speed and alignment toward this tick's raw reading — a real windmill doesn't track a
        // gusty/shifting wind instantaneously (see class remarks). Snap on the very first reading instead
        // of ramping up from a fake "0" start.
        SmoothState state = smoothing.GetValue(__instance, _ => new SmoothState());
        double ease = Math.Min(1.0, dt * SpeedEaseRatePerSecond);
        if (!state.Initialized) { state.Len = len; state.Dot = dot; state.Initialized = true; }
        else
        {
            state.Len += (len - state.Len) * ease;
            state.Dot += (dot - state.Dot) * ease;
        }

        double mag = Math.Abs(state.Dot);
        if (mag < cfg.WindmillMinEfficiency) mag = cfg.WindmillMinEfficiency;
        if (mag > 1.0) mag = 1.0;

        // windSpeed stays the positive torque MAGNITUDE; reversal is done by flipping the drive direction
        // in GetTorque (so the vanilla speed governor still caps the speed — negating torque does not).
        // This OVERWRITES vanilla's own windSpeed rather than scaling it, since that value never agreed
        // with our field in the first place.
        fWindSpeed.SetValue(__instance, state.Len * mag);

        // Update the wind-reversal state (from the EASED alignment, so a shifting wind eases the mill down
        // through a stall and back up the other way rather than flipping outright) and, on a TRANSITION,
        // reverse the whole mechanical network the way vanilla does: MechanicalNetwork.DirectionHasReversed
        // flips the network's speed sign once on the next network update. Flipping only the rotor's torque
        // sign (the GetTorque prefix below) is NOT enough — the engine's speed integrator clamps
        // |speed| >= 1e-6 and keeps its sign in the braking branch, only flipping sign when
        // |torque - resistance| is tiny. A low-resistance lone windmill coasts through zero and reverses,
        // but a geared assembly (e.g. a large gear, whose big gearedRatio inflates networkResistance) never
        // reaches that branch and would stall at ~0 forever. Flipping the network speed in step with the
        // torque sign keeps the rotor DRIVING (not braking) so the assembly spins up the other way.
        bool nowReversed = state.Dot > 0; // 180° flip vs the naive sign
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

    private static void GetTorqueFinalizer(BEBehaviorMPRotor __instance, object? __state)
    {
        if (__state is BlockFacing dir && fPropagationDir != null)
            fPropagationDir.SetValue(__instance, dir);
    }
}
