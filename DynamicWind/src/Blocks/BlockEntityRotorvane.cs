using System;
using System.Text;
using DynamicWind.Client.Render;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DynamicWind.Blocks;

/// <summary>
/// Rotorvane block entity: a weathervane variant whose "rotating" assembly carries a 12-bladed fan
/// (<c>spinner</c>) in addition to the usual arrow. The static post + compass letters (<c>stationary</c>)
/// are baked into the block model as usual. The arrow + mast (<c>rotating</c> minus <c>spinner</c>) turn
/// to face the wind exactly like <see cref="BlockEntityWeathervane"/>; the <c>spinner</c> is tesselated
/// separately so <see cref="RotorvaneRenderer"/> can additionally spin it about its own hub axis at a rate
/// driven by wind speed, independent of the yaw that points the assembly downwind.
/// </summary>
public class BlockEntityRotorvane : BlockEntity
{
    // See BlockEntityWeathervane for the "name/*" vs exact-name selective-tesselation semantics.
    private static readonly string[] BaseElements = { "stationary/*" };
    // Rotating assembly minus the spinner (tesselated and spun separately).
    private static readonly string[] AssemblyElements = { "rotating/topper", "rotating/rod", "rotating/arrow/*" };
    private static readonly string[] SpinnerElements = { "rotating/spinner/*" };

    // Element coordinates are relative to their immediate parent's rotationOrigin (each zero-size locator
    // is a new local frame, like a bone hierarchy), so the spinner's ABSOLUTE hub position is its own
    // rotationOrigin ([5.375, 4.15, 0]) plus its parent "rotating" locator's rotationOrigin ([8, 8, 8]) =
    // [13.375, 12.15, 8], normalized (/16) to the 0-1 block-space the mesh/renderer matrices use. The
    // spinner's own un-accumulated origin would spin the fan about a point near the block's corner.
    private static readonly Vec3f SpinnerPivot = new(13.375f / 16f, 12.15f / 16f, 8f / 16f);

    // "100% wind speed" reference for the sound's volume/pitch curve, matching WindArrowMapLayer's own
    // RefSpeed convention (field magnitude at calm baseline jetstream flow), so the sound hits full
    // intended loudness/pitch at the same wind strength the map overlay calls "100%".
    private const double RefSpeed = 1.0;
    private const float MaxVolume = 0.25f; // sound asset is authored for 100% wind speed at full volume
    private const float MinPitch = 0.6f;
    private const float MaxPitch = 1.4f;
    private const int SoundUpdateMs = 150;

    // Former DynamicWindConfig tunable, now a fixed constant (see DynamicWindConfig's class remarks).
    private const float RotorvaneSpeedMult = 24f;
    // Raw wind speed (and GetDirectionalMod, which depends on the still-swinging assembly yaw) is noisy
    // tick-to-tick, especially in gusts - easing pitch and volume toward target instead of snapping keeps
    // the whirring from warbling.
    private const float PitchEaseRatePerSecond = 2f;
    private const float VolumeEaseRatePerSecond = 2f;

    private MeshData? baseMesh;
    private RotorvaneRenderer? renderer;
    private ILoadedSound? sound;
    private ICoreClientAPI? capi;
    private float currentPitch = MinPitch;
    private float currentVolume;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi)
        {
            this.capi = capi;
            BuildMeshesAndRenderer(capi);
            StartSound(capi);
            RegisterGameTickListener(UpdateSound, SoundUpdateMs);
        }
    }

    #region Sound

    private void StartSound(ICoreClientAPI capi)
    {
        sound = capi.World.LoadSound(new SoundParams
        {
            Location = new AssetLocation("dynamicwind", "sounds/rotorvane.ogg"),
            ShouldLoop = true,
            Position = Pos.ToVec3f().Add(0.5f, 0.5f, 0.5f),
            DisposeOnFinish = false,
            Volume = 0f,
            Range = 16f,
        });
        sound.Start();
    }

    /// <summary>Volume/pitch track wind strength continuously rather than starting/stopping the loop, so a
    /// calm spell just fades it to silence instead of an audible restart click.</summary>
    private void UpdateSound(float dt)
    {
        if (sound == null || sound.IsDisposed) return;

        if (capi != null && capi.IsGamePaused)
        {
            if (!sound.IsPaused) sound.Pause();
            return;
        }
        if (sound.IsPaused) sound.Start(); // resume

        double frac = GetWindSpeed() / RefSpeed;
        if (frac > 1) frac = 1;

        float dtSeconds = SoundUpdateMs / 1000f;

        float targetVolume = (float)(MaxVolume * frac);
        currentVolume += (targetVolume - currentVolume) * Math.Min(1f, dtSeconds * VolumeEaseRatePerSecond);
        sound.SetVolume(currentVolume);

        float targetPitch = (float)(MinPitch + (MaxPitch - MinPitch) * frac) * Math.Abs(GetDirectionalMod());
        currentPitch += (targetPitch - currentPitch) * Math.Min(1f, dtSeconds * PitchEaseRatePerSecond);
        sound.SetPitch(currentPitch);
    }

    private void StopSound()
    {
        if (sound == null) return;
        sound.Stop();
        sound.Dispose();
        sound = null;
    }

    #endregion

    #region Mesh and renderer setup

    private void BuildMeshesAndRenderer(ICoreClientAPI capi)
    {
        IAsset asset = capi.Assets.TryGet(new AssetLocation("dynamicwind", "shapes/block/rotorvane.json"));
        if (asset == null) return;
        Shape shape = asset.ToObject<Shape>();
        ITexPositionSource tex = capi.Tesselator.GetTextureSource(Block);

        capi.Tesselator.TesselateShape("rotorvane-base", shape, out baseMesh, tex, null, 0, 0, 0, null, BaseElements);
        capi.Tesselator.TesselateShape("rotorvane-assembly", shape, out MeshData assemblyMesh, tex, null, 0, 0, 0, null, AssemblyElements);
        capi.Tesselator.TesselateShape("rotorvane-spinner", shape, out MeshData spinnerMesh, tex, null, 0, 0, 0, null, SpinnerElements);

        renderer = new RotorvaneRenderer(capi, this, SpinnerPivot);
        renderer.SetMeshes(capi.Render.UploadMesh(assemblyMesh), capi.Render.UploadMesh(spinnerMesh));
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "dynamicwind-rotorvane");

        MarkDirty(true); // re-tesselate now that baseMesh is ready (so the assembly/spinner aren't baked statically)
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (baseMesh == null) return false; // not ready yet -> let the full shape bake once
        mesher.AddMeshData(baseMesh);
        return true; // skip default (excludes the rotating assembly + spinner from the static mesh)
    }

    #endregion

    #region Wind query API

    /// <summary>Yaw (radians) for the assembly to point downwind; NaN when calm/occluded (hold position).</summary>
    public float GetTargetYaw()
    {
        if (Api == null) return float.NaN;
        if (IsOccluded()) return float.NaN;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        return VaneWind.TargetYawFor(w);
    }

    /// <summary>Angular speed (radians/s) the spinner should turn at: wind strength scaled by how well the
    /// assembly's current facing catches the wind (see <see cref="GetDirectionalMod"/>) - head-on toward
    /// the wind spins it forward at full rate, facing away reverses it.</summary>
    public float GetSpinSpeed()
    {
        if (Api == null) return 0f;

        float speed = (float)GetWindSpeed() * RotorvaneSpeedMult;
        return speed * GetDirectionalMod();
    }

    /// <summary>Cosine of the angle between the assembly's current (eased) yaw and the yaw it's swinging
    /// toward: +1 when fully caught up and facing the wind head-on, 0 when perpendicular (stalled), -1 when
    /// still facing the opposite way (spins backwards) - same head-on/perpendicular/reversed convention the
    /// windmill rotor patch uses. 1 (no modulation) when calm/occluded, since speed is already 0 then.</summary>
    private float GetDirectionalMod()
    {
        if (renderer == null) return 1f;
        float target = GetTargetYaw();
        if (float.IsNaN(target)) return 1f;
        return GameMath.Cos(GameMath.AngleRadDistance(renderer.CurrentYaw, target));
    }

    /// <summary>True directional wind magnitude at this block (0 when calm or occluded); shared by the
    /// spinner's angular speed, the sound's volume/pitch curve, and <see cref="RotorvaneRenderer"/>'s
    /// yaw-easing rate (<see cref="Client.Render.VaneTurnRate"/>).</summary>
    public double GetWindSpeed()
    {
        if (Api == null || IsOccluded()) return 0.0;
        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        return VaneWind.SpeedOf(w);
    }

    /// <summary>Same cave/undergroundWindmills gate windmill rotors and the plain weathervane arrow use.</summary>
    private bool IsOccluded() => VaneWind.IsOccluded(Api!, Pos);

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        if (Api == null) return;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        dsc.AppendLine(Lang.Get("dynamicwind:blockinfo-windheading", WindCompass.GetName(w.X, w.Z)));
        dsc.AppendLine(Lang.Get("dynamicwind:blockinfo-windspeed", (int)Math.Round(GetWindSpeed() / RefSpeed * 100)));
    }

    #endregion

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        renderer?.Dispose();
        renderer = null;
        StopSound();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        renderer?.Dispose();
        renderer = null;
        StopSound();
    }
}
