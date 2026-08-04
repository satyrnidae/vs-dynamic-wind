using System;
using DynamicWind.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Entities;

/// <summary>
/// Makes a sailboat's yard trimmable: the sail swings from 90° to port, through square-astern, to 90° to
/// starboard, and the thrust the boat gets from the wind follows from how the wind meets the SAIL rather
/// than merely the hull (see <c>Patches/SailboatWind</c>).
///
/// The target angle lives in <c>WatchedAttributes</c>, so it is server-authoritative, synced to every
/// client and persisted with the boat for free. Both sides then ease their own copy toward that one
/// target at the same configured rate, which keeps them converged without streaming the angle each tick.
///
/// The swing itself is driven through a real shape <c>Animation</c> (<see cref="SwingAnimCode"/>, added by
/// <c>patches/boat-sailed-shape.json</c>), scrubbed via <c>RunningAnimation.CurrentFrame</c> — exactly the
/// technique vanilla itself uses for the weathervane flag. Writing <c>ElementPose.degOff*</c> directly
/// (the first approach tried here) silently does nothing for any element that only inherits its mesh's
/// joint id from an ancestor rather than owning one itself, which the mast, the yard AND the clew ropes all
/// do by default — the animation-keyframe path is what actually earns an element its own joint. The
/// keyframes bake in the mast/yard swing AND the clew-rope sheets' exact aim-and-stretch solve (every 1° of
/// trim, both sides, all three sail positions) — see <c>tools/keyframegen</c> for the offline generator
/// that produced them from the same geometry <c>ClewRopeRig</c> used to compute at runtime.
///
/// Trim works in ALL THREE sail positions — the mod's shape patch adds the clew ropes vanilla's furled
/// sail lacks. Trimming a furled sail produces no thrust, but that falls out for free: vanilla contributes
/// no sail force at all at <c>sailPosition == 0</c>.
/// </summary>
public class EntityBehaviorSailTrim : EntityBehavior
{
    public const string Code = "dynamicwindsailtrim";
    public const string SailAngleAttr = "dynamicwind:sailAngle";

    /// <summary>Shape animation code (see <c>boat-sailed-shape.json</c>) keyframed at ±90° across 181
    /// frames (one per degree); frame = angle + 90. Covers MastORIGIN/Mast7's swing/counter-swing and both
    /// clew sheets' baked rotate+stretch, for all three sail positions at once (elements absent from
    /// whichever branch is currently tessellated are simply not resolved against, per the engine's own
    /// per-element keyframe lookup).</summary>
    public const string SwingAnimCode = "sailtrim";

    private bool warnedMissing;

    /// <summary>Server-side trim intent: −1 to port, 0 idle, +1 to starboard. Set by the input packet.</summary>
    private sbyte trimIntent;

    public EntityBehaviorSailTrim(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    /// <summary>Eased sail angle in degrees, + = trimmed to starboard. What the thrust model reads.</summary>
    public float CurrentAngleDeg { get; private set; }

    public float TargetAngleDeg
    {
        get => entity.WatchedAttributes.GetFloat(SailAngleAttr);
        private set => entity.WatchedAttributes.SetFloat(SailAngleAttr, value);
    }

    // Exposed for `/dynamicwind sail`, which needs to see each link of the chain to localise a fault.
    public sbyte TrimIntent => trimIntent;
    public bool SwingAnimationActive =>
        entity.AnimManager?.Animator != null && entity.AnimManager.IsAnimationActive(SwingAnimCode);

    private DynamicWindConfig? Config => DynamicWindRuntime.ConfigFor(entity.World.Side);

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        // Start already trimmed rather than swinging up from square on every load/chunk-reload.
        CurrentAngleDeg = TargetAngleDeg;
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);

        DynamicWindConfig? cfg = Config;
        if (cfg == null || !cfg.SailboatEnabled || !cfg.SailboatSailAngleEnabled) return;

        if (entity.World.Side == EnumAppSide.Server && trimIntent != 0)
        {
            float limit = cfg.SailboatMaxTrimAngleDeg;
            SetTarget(GameMath.Clamp(
                TargetAngleDeg + trimIntent * cfg.SailboatSwingRateDegPerSec * dt, -limit, limit));
        }

        Ease(dt, cfg);
        Pose();
    }

    private void Ease(float dt, DynamicWindConfig cfg)
    {
        float target = GameMath.Clamp(TargetAngleDeg, -cfg.SailboatMaxTrimAngleDeg, cfg.SailboatMaxTrimAngleDeg);
        float step = cfg.SailboatSwingRateDegPerSec * dt;
        float delta = target - CurrentAngleDeg;
        CurrentAngleDeg += Math.Abs(delta) <= step ? delta : Math.Sign(delta) * step;
    }

    /// <summary>
    /// Scrubs the baked swing animation to the current angle. Safe to call every tick on both sides —
    /// unlike the old <c>ClewRopeRig</c> pose write, this has no ordering dependency on the animator's own
    /// per-frame blend, so it no longer needs a head-controller hook to win a race against it.
    /// </summary>
    private void Pose()
    {
        // AnimationManager.GetAnimationState/StartAnimation/IsAnimationActive dereference Animator with no
        // null check of their own, and Animator stays null until the entity's first tessellation — an
        // early OnGameTick (right after spawn/chunk load) would otherwise crash here.
        IAnimationManager? animManager = entity.AnimManager;
        if (animManager?.Animator == null) return;

        // GetAnimationState looks up the animator's static table of every animation the SHAPE defines, so
        // it returns non-null forever once the animator exists — regardless of whether this animation is
        // actually active. Checking that for "already started" (as an earlier version of this did) means
        // StartAnimation only ever fires once, on the very first tick, and never again if the animation
        // is ever deactivated (e.g. a re-tessellation rebuilds the animator with fresh, inactive
        // RunningAnimation objects). IsAnimationActive checks the real active-state flag instead — this is
        // the exact pattern vanilla's own EntityBoat.OnRenderFrame uses to keep the weathervane running.
        if (!animManager.IsAnimationActive(SwingAnimCode))
        {
            animManager.StartAnimation(SwingAnimCode);
        }
        RunningAnimation? state = animManager.GetAnimationState(SwingAnimCode);
        if (state == null)
        {
            if (!warnedMissing)
            {
                warnedMissing = true;
                entity.World.Logger.Warning(
                    "[dynamicwind] Sail trim animation '{0}' not found on {1}; the sail will not swing. " +
                    "Is the boat shape patch loaded?", SwingAnimCode, entity.Code);
            }
            return;
        }
        warnedMissing = false;

        // Mirrored: the keyframes were baked with MastORIGIN's rotationY equal to (frame - 90) directly,
        // which turned out to swing the visual sail normal opposite to what SailboatWind's cos/sin
        // convention assumes for the same signed CurrentAngleDeg (facing the wind read as feathered and
        // vice versa). Requesting the mirror frame (90 - angle instead of 90 + angle) flips mast, yard AND
        // both clew sheets together and consistently — the sheets were baked per-frame against whatever
        // the mast's rotation was AT that frame, not against an assumed port/starboard meaning, so the
        // mirrored frame is still fully self-consistent geometry, just for the correct-facing angle.
        state.CurrentFrame = 90f - CurrentAngleDeg;
        state.BlendedWeight = 1f;
        state.EasingFactor = 1f;
    }

    /// <summary>
    /// Writes the synced target, skipping no-op writes. <c>SyncedTreeAttribute.SetFloat</c> calls
    /// <c>MarkPathDirty</c> on every call, so assigning unconditionally each tick would re-broadcast the
    /// angle and re-fire modified listeners even while the sail sits pinned against its ±limit stop with
    /// the key still held.
    /// </summary>
    private void SetTarget(float value)
    {
        if (Math.Abs(value - TargetAngleDeg) < 0.01f) return;
        TargetAngleDeg = value;
    }

    /// <summary>Server-side: set the held trim direction for this boat.</summary>
    public void SetTrimIntent(sbyte dir) => trimIntent = (sbyte)Math.Sign(dir);

    /// <summary>Server-side: nudge the sail by one configured step, as the clew-knot boxes do.</summary>
    public void StepTrim(int dir)
    {
        DynamicWindConfig? cfg = Config;
        if (cfg == null) return;
        float limit = cfg.SailboatMaxTrimAngleDeg;
        SetTarget(GameMath.Clamp(TargetAngleDeg + Math.Sign(dir) * cfg.SailboatTrimStepDeg, -limit, limit));
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition,
                                    EnumInteractMode mode, ref EnumHandling handled)
    {
        base.OnInteract(byEntity, itemslot, hitPosition, mode, ref handled);
        if (mode != EnumInteractMode.Interact) return;

        DynamicWindConfig? cfg = Config;
        if (cfg == null || !cfg.SailboatEnabled || !cfg.SailboatSailAngleEnabled) return;

        int dir = SheetDirection(byEntity);
        if (dir == 0) return;

        // Mutate server-side only; the WatchedAttribute syncs the result back to every client.
        if (entity.World.Side == EnumAppSide.Server) StepTrim(dir);
        handled = EnumHandling.PreventSubsequent;
    }

    public override WorldInteraction[]? GetInteractionHelp(IClientWorldAccessor world, EntitySelection es,
                                                           IClientPlayer player, ref EnumHandling handled)
    {
        DynamicWindConfig? cfg = Config;
        if (cfg == null || !cfg.SailboatEnabled || !cfg.SailboatSailAngleEnabled) return null;

        EntityBehaviorSelectionBoxes? boxes = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
        if (boxes == null) return null;

        if (boxes.IsAPCode(es, SheetLAP))
        {
            return new[] { Help("dynamicwind:sailboat-trimport") };
        }
        if (boxes.IsAPCode(es, SheetRAP))
        {
            return new[] { Help("dynamicwind:sailboat-trimstarboard") };
        }
        return null;
    }

    private static WorldInteraction Help(string langCode) => new()
    {
        ActionLangCode = langCode,
        MouseButton = EnumMouseButton.Right,
    };

    /// <summary>−1 if the player is looking at the port sheet's knot, +1 for starboard, 0 for neither.</summary>
    private int SheetDirection(EntityAgent byEntity)
    {
        EntityBehaviorSelectionBoxes? boxes = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
        EntitySelection? es = (byEntity as EntityPlayer)?.EntitySelection;
        if (boxes == null || es == null) return 0;
        // IsAPCode is safe for these two specifically: it skips selection box index 0, and the mod's
        // entity patch APPENDS both codes to the vanilla list, so neither can ever land there.
        if (boxes.IsAPCode(es, SheetLAP)) return -1;
        if (boxes.IsAPCode(es, SheetRAP)) return 1;
        return 0;
    }

    private const string SheetLAP = "SheetLAP";
    private const string SheetRAP = "SheetRAP";
}
