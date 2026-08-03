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
/// then scales it by |cos θ| between that wind and the rotor axle (<c>ownFacing</c>) rescaled onto
/// [<see cref="DynamicWindConfig.WindmillMinEfficiency"/>, 1] — parallel = full power, perpendicular = the
/// configured minimum efficiency (NOT a flat floor on the result: the whole curve is remapped, so power
/// still varies continuously across every angle) — and records the SIGN of the alignment: when the wind
/// comes from behind the rotor (cos θ &lt; 0) the mill should turn backwards. Exactly edge-on that sign is
/// numerical noise, so the direction there comes from which SIDE the wind passes on instead
/// (<see cref="PerpendicularDeadBand"/>).
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

    // Edge-on dead band. Exactly perpendicular the alignment cosine is 0 and its SIGN — which is what
    // picks spin direction — is pure numerical noise, so a rotor sitting square to the wind would chatter
    // between forward and reverse on rounding alone. Inside this band the direction instead comes from
    // which SIDE the wind passes on (the cross product): from the rotor's left it turns counter-clockwise,
    // from its right, clockwise. Outside the band the alignment sign rules as before, so head-on and
    // from-behind behaviour is untouched.
    private const double PerpendicularDeadBand = 0.02;

    // A reversal ramps the rotor's own output down through zero BEFORE the network flip is published, then
    // back up to target — sails carry rotational inertia and can't invert instantaneously. Publishing the
    // flip at full power instead would snap a geared assembly from +x to −x speed in a single tick.
    private const double ReversalRampRatePerSecond = 0.5;
    private const double ReversalRampFloor = 0.02; // |ramp| under this counts as "through zero"

    private sealed class SmoothState
    {
        public double Len;
        public double Dot;
        /// <summary>Eased side-of-wind (cross product) — breaks the direction tie when edge-on.</summary>
        public double Cross;
        public bool Initialized;
        /// <summary>Output scale in [0,1]: driven to 0 while a reversal is pending, back to 1 after it lands.</summary>
        public double Ramp = 1.0;
        /// <summary>Spin direction actually published to the network — flips only once <see cref="Ramp"/> reaches 0.</summary>
        public bool Reversed;
        public bool HasDirection;
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
        // Which SIDE the wind passes on (+ = one side, − = the other). Only consulted when the rotor sits
        // essentially edge-on, where `dot`'s sign carries no information — see PerpendicularDeadBand.
        double cross = len > 1e-6 ? (ax * trueWind.Z - az * trueWind.X) / (len * al) : 0.0; // [-1, 1]

        // Ease both speed and alignment toward this tick's raw reading — a real windmill doesn't track a
        // gusty/shifting wind instantaneously (see class remarks). Snap on the very first reading instead
        // of ramping up from a fake "0" start.
        SmoothState state = smoothing.GetValue(__instance, _ => new SmoothState());
        double ease = Math.Min(1.0, dt * SpeedEaseRatePerSecond);
        if (!state.Initialized) { state.Len = len; state.Dot = dot; state.Cross = cross; state.Initialized = true; }
        else
        {
            state.Len += (len - state.Len) * ease;
            state.Dot += (dot - state.Dot) * ease;
            state.Cross += (cross - state.Cross) * ease;
        }

        // WindmillMinEfficiency is the efficiency of a rotor sitting exactly PERPENDICULAR to the wind, so
        // it rescales the alignment curve: |alignment| 0 → MinEfficiency, 1 (head-on) → full. Clamping the
        // magnitude to it instead (max(|dot|, min)) would flatten the whole band below MinEfficiency to one
        // constant — every rotor from perpendicular out to half-aligned turning at identical speed — and
        // would hold full MinEfficiency power right up to the crossing, so the mill snaps into reverse
        // instead of easing down through a stall the way the eased alignment below intends.
        double aligned = Math.Min(1.0, Math.Abs(state.Dot));
        double minEff = GameMath.Clamp(cfg.WindmillMinEfficiency, 0f, 1f);
        double mag = minEff + (1.0 - minEff) * aligned;

        // Which way it wants to turn, from the EASED readings. Normally the alignment sign (180° flip vs
        // the naive sign). Edge-on — where that sign is numerical noise and a square-on rotor would
        // otherwise chatter every tick — the side the wind passes on decides instead.
        bool desiredReversed = Math.Abs(state.Dot) >= PerpendicularDeadBand
            ? state.Dot > 0
            : state.Cross > 0;

        // Reversal is published to the network the way vanilla does it: MechanicalNetwork.DirectionHasReversed
        // flips the network's speed sign once on the next network update. Flipping only the rotor's torque
        // sign (the GetTorque prefix below) is NOT enough — the engine's speed integrator clamps
        // |speed| >= 1e-6 and keeps its sign in the braking branch, only flipping sign when
        // |torque - resistance| is tiny. A low-resistance lone windmill coasts through zero and reverses,
        // but a geared assembly (e.g. a large gear, whose big gearedRatio inflates networkResistance) never
        // reaches that branch and would stall at ~0 forever. Flipping the network speed in step with the
        // torque sign keeps the rotor DRIVING (not braking) so the assembly spins up the other way.
        //
        // The flip is DEFERRED until the rotor's own output has ramped down through zero, then the output
        // accelerates back to target the other way. Publishing it immediately would snap the assembly from
        // full speed to full reverse in one tick — and at perpendicular the magnitude is
        // WindmillMinEfficiency, not zero, so easing the alignment alone never brings it through zero.
        StrongBox<bool> box = reversed.GetOrCreateValue(__instance);
        if (!state.HasDirection)
        {
            state.Reversed = desiredReversed;
            state.HasDirection = true;
            box.Value = desiredReversed;
        }
        else if (desiredReversed != state.Reversed)
        {
            state.Ramp -= dt * ReversalRampRatePerSecond;
            if (state.Ramp <= ReversalRampFloor)
            {
                state.Ramp = 0.0;
                state.Reversed = desiredReversed;
                box.Value = desiredReversed;
                MechanicalNetwork? net = __instance.Network;
                if (net != null) net.DirectionHasReversed = true;
            }
        }
        else if (state.Ramp < 1.0)
        {
            state.Ramp += dt * ReversalRampRatePerSecond;
        }
        state.Ramp = GameMath.Clamp(state.Ramp, 0.0, 1.0);

        // windSpeed stays the positive torque MAGNITUDE; reversal is done by flipping the drive direction
        // in GetTorque (so the vanilla speed governor still caps the speed — negating torque does not).
        // This OVERWRITES vanilla's own windSpeed rather than scaling it, since that value never agreed
        // with our field in the first place.
        fWindSpeed.SetValue(__instance, state.Len * mag * state.Ramp);
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
