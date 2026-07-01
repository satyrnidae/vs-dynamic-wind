using System;
using DynamicWind.Client.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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
    // Rotating assembly minus the spinner (which is tesselated and spun separately).
    private static readonly string[] AssemblyElements = { "rotating/topper", "rotating/rod", "rotating/arrow/*" };
    private static readonly string[] SpinnerElements = { "rotating/spinner/*" };

    // Element coordinates in this shape are relative to their immediate parent's rotationOrigin (each
    // zero-size locator establishes a new local frame for its children, like a bone hierarchy), so the
    // spinner's ABSOLUTE hub position is its own rotationOrigin ([5.375, 4.15, 0]) plus its parent
    // "rotating" locator's rotationOrigin ([8, 8, 8]) = [13.375, 12.15, 8], normalized (/16) to the 0-1
    // block-space the tesselated mesh and renderer matrices use. Using the spinner's own (un-accumulated)
    // origin here spins the fan about a point near the block's corner instead of its hub.
    private static readonly Vec3f SpinnerPivot = new(13.375f / 16f, 12.15f / 16f, 8f / 16f);

    private MeshData? baseMesh;
    private RotorvaneRenderer? renderer;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) BuildMeshesAndRenderer(capi);
    }

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

    /// <summary>Yaw (radians) for the assembly to point downwind; NaN when calm/occluded (hold position).</summary>
    public float GetTargetYaw()
    {
        if (Api == null) return float.NaN;
        if (IsOccluded()) return float.NaN;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        if (w.X * w.X + w.Z * w.Z < 1e-8) return float.NaN; // calm
        return (float)(Math.Atan2(w.X, w.Z) + Math.PI / 2.0);
    }

    /// <summary>Angular speed (radians/s) the spinner should turn at, driven purely by wind strength.</summary>
    public float GetSpinSpeed()
    {
        if (Api == null || IsOccluded()) return 0f;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        double speed = Math.Sqrt(w.X * w.X + w.Z * w.Z);
        return (float)speed * RotorvaneRenderer.SpinSpeedFactor;
    }

    /// <summary>Same cave/undergroundWindmills gate windmill rotors and the plain weathervane arrow use.</summary>
    private bool IsOccluded()
    {
        return Api!.World.BlockAccessor.GetLightLevel(Pos, EnumLightLevelType.OnlySunLight) < 5
            && Api.World.Config.GetString("undergroundWindmills", "false") != "true";
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (baseMesh == null) return false; // not ready yet → let the full shape bake once
        mesher.AddMeshData(baseMesh);
        return true; // skip default (excludes the rotating assembly + spinner from the static mesh)
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        renderer?.Dispose();
        renderer = null;
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        renderer?.Dispose();
        renderer = null;
    }
}
