using System;
using DynamicWind.Config;
using DynamicWind.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Entities;

/// <summary>
/// Makes a sailboat's yard trimmable: the sail swings from 90deg to port, through square-astern, to 90deg
/// to starboard, and the thrust the boat gets from the wind follows from how the wind meets the SAIL rather
/// than merely the hull (see <c>Patches/SailboatWind</c>).
///
/// The target angle lives in <c>WatchedAttributes</c>, so it is server-authoritative, synced to every
/// client and persisted with the boat for free. Both sides then ease their own copy toward that one
/// target at the same configured rate, which keeps them converged without streaming the angle each tick.
///
/// The swing itself is driven through a real shape <c>Animation</c> (<see cref="SwingAnimCode"/>, added by
/// <c>patches/boat-sailed-shape.json</c>), scrubbed via <c>RunningAnimation.CurrentFrame</c> - exactly the
/// technique vanilla itself uses for the weathervane flag. The mast/yard/sail-root swing itself, and the
/// billow effect, are hand-authored in the modelling tool (<c>tools/keyframegen/starting-point.json</c> is
/// the checked-in export); <c>tools/keyframegen</c> only bakes the clew ropes' aim-and-stretch solve on top
/// of that, since the authoring tool has no way to track a fixed hull anchor point through the swing.
///
/// Trim works in ALL THREE sail positions - the source model includes clew ropes for the furled sail too.
/// Trimming a furled sail produces no thrust, but that falls out for free: vanilla contributes no sail
/// force at all at <c>sailPosition == 0</c>.
/// </summary>
public class EntityBehaviorSailTrim : EntityBehavior
{
    #region Constants and Fields

    public const string Code = "dynamicwindsailtrim";
    public const string SailAngleAttr = "dynamicwind:sailAngle";

    /// <summary>Shape animation code (see <c>boat-sailed-shape.json</c>) for the mast/yard/sail-root swing
    /// plus the generated clew-rope keyframes. 180 frames (0..179), piecewise-linear in swing angle: frame
    /// 0 = 90deg (full port), frame 89 = 0deg (dead ahead), frame 179 = -90deg (full starboard) - see
    /// <see cref="FrameForAngle"/>.</summary>
    public const string SwingAnimCode = "rotatesail";

    /// <summary>Shape animation code (see <c>boat-sailed-shape.json</c>) for the sail-billow effect - two
    /// endpoint keyframes (0 = flat/rest, 99 = full billow) since the billow rotations are a plain linear
    /// function of intensity. Disjoint element set from <see cref="SwingAnimCode"/> (sail panels vs.
    /// mast/yard/ropes), so the two animations run concurrently without conflict - same pattern vanilla
    /// itself uses to run several animations on one entity at once.</summary>
    public const string BillowAnimCode = "billowsail";
    private const float BillowMaxFrame = 99f;

    private const float BillowEaseRatePerSec = 1.5f;

    private const float SailboatTrimStepDeg = 1.0f;
    private const float SailboatSelectionBoxTrimStepDeg = SailboatTrimStepDeg * 6; // selection boxes fire about once every six frames

    // SelectionBox and keyboard input share this rate so trim speed feels the same regardless of input method.
    private const float SailboatTrimRateDegPerSec = SailboatTrimStepDeg * 30f; // 15deg/sec at 30 ticks/sec, same as the old ClewRopeRig

    private bool warnedMissing;

    /// <summary>Server-side trim intent: -1 to port, 0 idle, +1 to starboard. Set by the input packet.</summary>
    private sbyte trimIntent;

    #endregion

    public EntityBehaviorSailTrim(Entity entity) : base(entity) { }

    #region Properties

    public override string PropertyName() => Code;

    /// <summary>Eased sail angle in degrees, + = trimmed to starboard. What the thrust model reads.</summary>
    public float CurrentAngleDeg { get; private set; }

    /// <summary>Eased 0..1 billow intensity - how full the sail looks. Computed independently on both
    /// sides from the same deterministic wind field + <see cref="CurrentAngleDeg"/>, same as the rest of
    /// this mod's client/server split, so no extra network traffic is needed for it.</summary>
    public float BillowIntensity { get; private set; }

    /// <summary>The sail-thrust model's state for this boat, refreshed once per <see cref="OnGameTick"/> -
    /// <see cref="UpdateBillow"/> reads it, and so does the <c>.dwind sailgraph</c> HUD when open, rather
    /// than each independently re-running <c>SailboatWind.Evaluate</c>'s full <c>GetWindSpeedAt</c> call
    /// (the whole <c>OnGetWindSpeed</c> hook chain - front excess, orographic, occlusion - not a cheap
    /// read) on their own schedule. Default/invalid until the first gated tick below runs. Its
    /// <c>WindMult</c>/<c>TrimMult</c> fields are <c>SailboatWind</c>'s own names for what CLAUDE.md calls
    /// "windBoost"/"angleBoost" - same two terms, named per-file.</summary>
    internal SailboatWind.SailState LastState { get; private set; }

    public float TargetAngleDeg
    {
        get => entity.WatchedAttributes.GetFloat(SailAngleAttr);
        private set => entity.WatchedAttributes.SetFloat(SailAngleAttr, value);
    }

    // Exposed for `/dwind sail`, which needs to see each link of the chain to localise a fault.
    public sbyte TrimIntent => trimIntent;
    public bool SwingAnimationActive =>
        entity.AnimManager?.Animator != null && entity.AnimManager.IsAnimationActive(SwingAnimCode);

    private DynamicWindConfig? Config => DynamicWindRuntime.ConfigFor(entity.World.Side);

    #endregion

    #region Lifecycle

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        // Start already trimmed rather than swinging up from square on every load/chunk-reload.
        CurrentAngleDeg = TargetAngleDeg;
        entity.WatchedAttributes.RegisterModifiedListener("sailPosition", OnSailPositionChanged);
    }

    // EntityBoat.OnTesselation clones the shape and prunes the two inactive sail branches
    // (RemoveElementByName) itself, entirely outside the engine's `shapeIsCloned` out-param that
    // Entity.OnTesselation uses to decide whether to rebuild the animator fresh (AnimationManager.
    // LoadAnimator) or reuse a cached one (AnimationCache.LoadAnimatorCached, keyed only by
    // `entity.Code + shape base path` - NOT by sailPosition). Since EntityBoat's own clone never flips
    // that flag, every re-tessellation after the FIRST ever hits the cache and reuses whichever sail
    // branch's RootPoses/RootElems got cached first - elements unique to any other branch (our
    // per-branch clew ropes, billow's panel elements) then never receive a pose at all and render at raw
    // rest geometry. Force-clearing the cache on every sailPosition change makes the next tessellation
    // take the LoadAnimator (non-cached, correct) path instead.

    private void OnSailPositionChanged()
    {
        if (entity.Api != null) AnimationCache.ClearCache(entity.Api, entity);
    }

    #endregion

    #region Angle Easing and Sync

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);

        DynamicWindConfig? cfg = Config;
        if (cfg == null || !cfg.SailboatEnabled || !cfg.SailboatSailAngleEnabled) return;

        if (entity.World.Side == EnumAppSide.Server && trimIntent != 0)
        {
            SetTarget(GameMath.Clamp(
                TargetAngleDeg + trimIntent * SailboatTrimRateDegPerSec * dt,
                -90, 90));
        }

        Ease(dt);
        LastState = entity is EntityBoat boat ? SailboatWind.Evaluate(boat) : default;
        UpdateBillow(dt);
        Pose();
    }

    private void Ease(float dt)
    {
        float target = GameMath.Clamp(TargetAngleDeg, -90, 90);
        float step = SailboatTrimRateDegPerSec * dt;
        float delta = target - CurrentAngleDeg;
        CurrentAngleDeg += Math.Abs(delta) <= step ? delta : Math.Sign(delta) * step;
    }

    #endregion

    #region Sail Billow

    /// <summary>
    /// Target for <see cref="UpdateBillow"/>: a direct readout of <see cref="SailboatWind.SailState.CombinedMult"/>
    /// (both terms), so wind speed still matters - a becalmed but well-trimmed sail is a real, distinct
    /// situation from a filled one, unlike the old <c>lift * SpeedFactor(windSpeed)</c> calc. 0%
    /// <c>CombinedMult</c> or below shows no billow; <c>BillowMaxMult</c> (45%, 5 points below the model's
    /// realistic ~50% ceiling so a maxed-out sail still visibly reads as "fully filled") shows a fully
    /// filled sail. Mapped through <c>sqrt</c> rather than linearly, since most of a modestly-trimmed
    /// sail's usable range sits in the bottom third of [0, 45%] - a linear map made those low-mult sails
    /// look almost imperceptibly different from luffing; <c>sqrt</c> front-loads visible growth into that
    /// low range and tapers off near the max. Zero whenever furled (vanilla contributes no sail force at
    /// <c>sailPosition == 0</c> either) or the legacy heading model is active (no per-angle mult to read).
    /// </summary>
    private const float BillowMaxMult = 0.45f;

    private void UpdateBillow(float dt)
    {
        float target = 0f;
        if (entity is EntityBoat boat && boat.WatchedAttributes.GetInt("sailPosition", 0) > 0
            && LastState.Valid && LastState.AngleModel)
        {
            float fraction = GameMath.Clamp((float)LastState.CombinedMult / BillowMaxMult, 0, 1);
            target = (float)Math.Sqrt(fraction);
        }

        float step = BillowEaseRatePerSec * dt;
        float delta = target - BillowIntensity;
        BillowIntensity += Math.Abs(delta) <= step ? delta : Math.Sign(delta) * step;
    }

    #endregion

    #region Animation Pose Scrubbing

    /// <summary>Flips which physical side <see cref="CurrentAngleDeg"/>'s sign swings the sail toward.
    /// <c>CurrentAngleDeg</c> is defined as "+ = trimmed to starboard" by <see cref="SailboatWind"/>'s own
    /// convention; the source model's swing animation defines frame 0 as full port (+90deg) and frame 179
    /// as full starboard (-90deg) - i.e. the model's own angle parameter runs the OPPOSITE sign from
    /// <c>CurrentAngleDeg</c>. Flip this if the sail is ever found visually swinging to the wrong side
    /// in-game.</summary>
    private const bool MirrorTrimDirection = true;

    /// <summary>Maps <see cref="CurrentAngleDeg"/> (-90 port .. +90 starboard) to a frame on
    /// <see cref="SwingAnimCode"/>, the exact inverse of that animation's own three authored keyframes
    /// (frame 0 = +90, frame 89 = 0, frame 179 = -90) - see <c>tools/keyframegen/Program.cs</c>'s
    /// <c>AngleForFrame</c>, which this must stay the algebraic inverse of.</summary>
    private static float FrameForAngle(float angleDeg)
    {
        float modelAngle = MirrorTrimDirection ? -angleDeg : angleDeg;
        return modelAngle >= 0f
            ? (90f - modelAngle) * (89f / 90f)
            : 89f - modelAngle;
    }

    /// <summary>
    /// Scrubs the baked swing animation to the current angle. Safe to call every tick on both sides -
    /// scrubbing <c>CurrentFrame</c> has no ordering dependency on the animator's own per-frame blend,
    /// unlike a live pose write, so it doesn't matter whether this runs before or after that frame's blend,
    /// only before the next one.
    /// </summary>
    private void Pose()
    {
        // AnimationManager.GetAnimationState/StartAnimation/IsAnimationActive dereference Animator with no
        // null check of their own, and Animator stays null until the entity's first tessellation - an
        // early OnGameTick (right after spawn/chunk load) would otherwise crash here.
        IAnimationManager? animManager = entity.AnimManager;
        if (animManager?.Animator == null) return;

        // GetAnimationState looks up the animator's static table of every animation the SHAPE defines, so
        // it returns non-null forever once the animator exists - regardless of whether this animation is
        // actually active. Checking that for "already started" (as an earlier version of this did) means
        // StartAnimation only ever fires once, on the very first tick, and never again if the animation
        // is ever deactivated (e.g. a re-tessellation rebuilds the animator with fresh, inactive
        // RunningAnimation objects). IsAnimationActive checks the real active-state flag instead - this is
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

        state.CurrentFrame = FrameForAngle(CurrentAngleDeg);
        state.BlendedWeight = 1f;
        state.EasingFactor = 1f;

        // Billow runs as a second, independent animation on a disjoint element set (sail panels, not
        // mast/yard/ropes) - no mirroring needed, it's a plain 0..1 intensity, not a signed swing angle.
        if (!animManager.IsAnimationActive(BillowAnimCode))
        {
            animManager.StartAnimation(BillowAnimCode);
        }
        RunningAnimation? billowState = animManager.GetAnimationState(BillowAnimCode);
        if (billowState != null)
        {
            billowState.CurrentFrame = BillowIntensity * BillowMaxFrame;
            billowState.BlendedWeight = 1f;
            billowState.EasingFactor = 1f;
        }
    }

    #endregion

    #region Trim Intent Input

    /// <summary>
    /// Writes the synced target, skipping no-op writes. <c>SyncedTreeAttribute.SetFloat</c> calls
    /// <c>MarkPathDirty</c> on every call, so assigning unconditionally each tick would re-broadcast the
    /// angle and re-fire modified listeners even while the sail sits pinned against its +/-limit stop with
    /// the key still held.
    /// </summary>
    private void SetTarget(float value)
    {
        if (Math.Abs(value - TargetAngleDeg) < 0.01f) return;
        TargetAngleDeg = value;
    }

    /// <summary>
    /// Server-side: set the held trim direction for this boat, from <c>SailboatWind.HandleCtrlTrim</c>
    /// (Ctrl+A/D repurposing the tiller's own turn keys - the only input path now, after the earlier
    /// bracket-key hotkeys/packet were removed once this was confirmed working).
    /// </summary>
    public void SetTrimIntent(sbyte dir) => trimIntent = (sbyte)Math.Sign(dir);

    /// <summary>Server-side: nudge the sail by one configured step, as the clew-knot boxes do. Uses the
    /// selection-box step (not the keyboard's per-tick <see cref="SailboatTrimStepDeg"/>) since
    /// <see cref="OnInteract"/> only fires once every few ticks, not every tick - sized so, combined
    /// with <see cref="Ease"/>'s constant-rate catch-up, both input methods converge on the same
    /// effective trim speed.</summary>
    public void StepTrim(int dir)
    {
        SetTarget(GameMath.Clamp(TargetAngleDeg + Math.Sign(dir) * SailboatSelectionBoxTrimStepDeg,
            -90, 90));
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

    /// <summary>-1 if the player is looking at the port sheet's knot, +1 for starboard, 0 for neither.</summary>
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

    #endregion
}
