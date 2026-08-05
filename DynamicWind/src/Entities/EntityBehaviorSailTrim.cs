using System;
using DynamicWind.Config;
using DynamicWind.Patches;
using DynamicWind.WindSystem;
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
/// technique vanilla itself uses for the weathervane flag. The mast/yard/sail-root swing itself, and the
/// billow effect, are hand-authored in the modelling tool (<c>tools/keyframegen/starting-point.json</c> is
/// the checked-in export); <c>tools/keyframegen</c> only bakes the clew ropes' aim-and-stretch solve on top
/// of that, since the authoring tool has no way to track a fixed hull anchor point through the swing.
///
/// Trim works in ALL THREE sail positions — the source model includes clew ropes for the furled sail too.
/// Trimming a furled sail produces no thrust, but that falls out for free: vanilla contributes no sail
/// force at all at <c>sailPosition == 0</c>.
/// </summary>
public class EntityBehaviorSailTrim : EntityBehavior
{
    public const string Code = "dynamicwindsailtrim";
    public const string SailAngleAttr = "dynamicwind:sailAngle";

    /// <summary>Shape animation code (see <c>boat-sailed-shape.json</c>) for the mast/yard/sail-root swing
    /// plus the generated clew-rope keyframes. 180 frames (0..179), piecewise-linear in swing angle: frame
    /// 0 = 90° (full port), frame 89 = 0° (dead ahead), frame 179 = −90° (full starboard) — see
    /// <see cref="FrameForAngle"/>.</summary>
    public const string SwingAnimCode = "rotatesail";

    /// <summary>Shape animation code (see <c>boat-sailed-shape.json</c>) for the sail-billow effect — two
    /// endpoint keyframes (0 = flat/rest, 99 = full billow) since the billow rotations are a plain linear
    /// function of intensity. Disjoint element set from <see cref="SwingAnimCode"/> (sail panels vs.
    /// mast/yard/ropes), so the two animations run concurrently without conflict — same pattern vanilla
    /// itself uses to run several animations on one entity at once.</summary>
    public const string BillowAnimCode = "billowsail";
    private const float BillowMaxFrame = 99f;

    private const float BillowEaseRatePerSec = 1.5f;

    private const float SailboatTrimStepDeg = 1.0f;
    private const float SailboatSelectionBoxTrimStepDeg = SailboatTrimStepDeg * 6; // selection boxes only trigger about once every six frames

    // SelectionBox and keyboard movement should both use the same interpolation rate, so that the player sees the same trim speed regardless of which input method they use.
    private const float SailboatTrimRateDegPerSec = SailboatTrimStepDeg * 30f; // 15°/sec at 30 ticks/sec, same as the old ClewRopeRig

    private bool warnedMissing;

    /// <summary>Server-side trim intent: −1 to port, 0 idle, +1 to starboard. Set by the input packet.</summary>
    private sbyte trimIntent;

    public EntityBehaviorSailTrim(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    /// <summary>Eased sail angle in degrees, + = trimmed to starboard. What the thrust model reads.</summary>
    public float CurrentAngleDeg { get; private set; }

    /// <summary>Eased 0..1 billow intensity — how full the sail looks. Computed independently on both
    /// sides from the same deterministic wind field + <see cref="CurrentAngleDeg"/>, same as the rest of
    /// this mod's client/server split, so no extra network traffic is needed for it.</summary>
    public float BillowIntensity { get; private set; }

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

        // EntityBoat.OnTesselation clones the shape and prunes the two inactive sail branches
        // (RemoveElementByName) itself, entirely outside the engine's `shapeIsCloned` out-param that
        // Entity.OnTesselation uses to decide whether to rebuild the animator fresh (AnimationManager.
        // LoadAnimator) or reuse a cached one (AnimationCache.LoadAnimatorCached, keyed only by
        // `entity.Code + shape base path` — NOT by sailPosition). Since EntityBoat's own clone never flips
        // that flag, every re-tessellation after the FIRST ever hits the cache and reuses whichever sail
        // branch's RootPoses/RootElems got cached first — elements unique to any other branch (our
        // per-branch clew ropes, billow's panel elements) then never receive a pose at all and render at
        // raw rest geometry. Force-clearing the cache on every sailPosition change makes the next
        // tessellation take the LoadAnimator (non-cached, correct) path instead.
        entity.WatchedAttributes.RegisterModifiedListener("sailPosition", OnSailPositionChanged);
    }

    private void OnSailPositionChanged()
    {
        if (entity.Api != null) AnimationCache.ClearCache(entity.Api, entity);
    }

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
        UpdateBillow(dt, cfg);
        Pose();
    }

    private void Ease(float dt)
    {
        float target = GameMath.Clamp(TargetAngleDeg, -90, 90);
        float step = SailboatTrimRateDegPerSec * dt;
        float delta = target - CurrentAngleDeg;
        CurrentAngleDeg += Math.Abs(delta) <= step ? delta : Math.Sign(delta) * step;
    }

    /// <summary>
    /// Eases <see cref="BillowIntensity"/> toward how squarely the wind currently fills the sail
    /// (<c>lift</c> from <see cref="SailAlignment"/>, the same term the thrust model reads), scaled by
    /// wind magnitude via the same <see cref="SailboatWind.SpeedFactor"/> the thrust model uses.
    /// Deliberately <c>lift</c> alone, not <c>lift * drive</c>: billow is about how full the sail looks
    /// from the wind hitting it, independent of whether that push happens to point where the boat is
    /// going — a boat reaching across the wind should still show a full sail. Zero whenever furled
    /// (<c>sailPosition == 0</c>, vanilla contributes no sail force there either) or wind is negligible.
    /// </summary>
    private void UpdateBillow(float dt, DynamicWindConfig cfg)
    {
        float target = 0f;
        WindField? field = DynamicWindRuntime.FieldFor(entity.World.Side);
        if (field != null && entity is EntityBoat boat && boat.WatchedAttributes.GetInt("sailPosition", 0) > 0)
        {
            Vec3d pos = boat.Pos.XYZ;
            Vec2d w = field.WindAt(pos.X, pos.Z, boat.World.Calendar.TotalHours);
            double len = w.Length();
            Vec3f vv = boat.Pos.GetViewVector();
            double tx = -vv.X, tz = -vv.Z;
            double tl = Math.Sqrt(tx * tx + tz * tz);
            if (len >= 1e-6 && tl >= 1e-6)
            {
                tx /= tl; tz /= tl;
                double wx = w.X / len, wz = w.Y / len;
                (double lift, _) = SailboatWind.SailAlignment(boat, tx, tz, wx, wz);
                target = (float)GameMath.Clamp(lift * SailboatWind.SpeedFactor(len), 0, 1);
            }
        }

        float step = BillowEaseRatePerSec * dt;
        float delta = target - BillowIntensity;
        BillowIntensity += Math.Abs(delta) <= step ? delta : Math.Sign(delta) * step;
    }

    /// <summary>Flips which physical side <see cref="CurrentAngleDeg"/>'s sign swings the sail toward.
    /// <c>CurrentAngleDeg</c> is defined as "+ = trimmed to starboard" by <see cref="SailboatWind"/>'s own
    /// convention; the source model's swing animation defines frame 0 as full port (+90°) and frame 179 as
    /// full starboard (−90°) — i.e. the model's own angle parameter runs the OPPOSITE sign from
    /// <c>CurrentAngleDeg</c>. Flip this if the sail is ever found visually swinging to the wrong side
    /// in-game.</summary>
    private const bool MirrorTrimDirection = true;

    /// <summary>Maps <see cref="CurrentAngleDeg"/> (−90 port .. +90 starboard) to a frame on
    /// <see cref="SwingAnimCode"/>, the exact inverse of that animation's own three authored keyframes
    /// (frame 0 = +90, frame 89 = 0, frame 179 = −90) — see <c>tools/keyframegen/Program.cs</c>'s
    /// <c>AngleForFrame</c>, which this must stay the algebraic inverse of.</summary>
    private static float FrameForAngle(float angleDeg)
    {
        float modelAngle = MirrorTrimDirection ? -angleDeg : angleDeg;
        return modelAngle >= 0f
            ? (90f - modelAngle) * (89f / 90f)
            : 89f - modelAngle;
    }

    /// <summary>
    /// Scrubs the baked swing animation to the current angle. Safe to call every tick on both sides —
    /// scrubbing <c>CurrentFrame</c> has no ordering dependency on the animator's own per-frame blend,
    /// unlike a live pose write, so it doesn't matter whether this runs before or after that frame's blend,
    /// only before the next one.
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

        state.CurrentFrame = FrameForAngle(CurrentAngleDeg);
        state.BlendedWeight = 1f;
        state.EasingFactor = 1f;

        // Billow runs as a second, independent animation on a disjoint element set (sail panels, not
        // mast/yard/ropes) — no mirroring needed, it's a plain 0..1 intensity, not a signed swing angle.
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

    /// <summary>Server-side: nudge the sail by one configured step, as the clew-knot boxes do. Uses the
    /// selection-box step (not the keyboard's per-tick <see cref="SailboatTrimStepDeg"/>) since
    /// <see cref="OnInteract"/> only fires once every few ticks, not every tick — sized so, combined
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
