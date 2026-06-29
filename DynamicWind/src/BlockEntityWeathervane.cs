using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Weathervane block entity. The static post + compass letters (<c>stalk</c>, <c>nsbone</c>,
/// <c>webone</c>) are baked into the block model; the <c>upperportion</c> (arrow) is rendered by
/// <see cref="WeathervaneRenderer"/>, rotated to point downwind. The arrow points north (−Z) at yaw 0,
/// matching the model. Wind is sampled via the directional <c>GetWindSpeedAt</c> hook.
/// </summary>
public class BlockEntityWeathervane : BlockEntity
{
    // "name/*" includes the element AND its whole subtree. An exact name matches only that element and
    // passes an EMPTY child filter (VS ShapeTesselator.SelectiveMatch), dropping children — and the
    // compass/arrow groups are zero-size locators whose geometry lives entirely in their children.
    private static readonly string[] BaseElements = { "stalk", "nsbone/*", "webone/*" };
    private static readonly string[] ArrowElements = { "upperportion/*" };

    private MeshData? baseMesh;
    private WeathervaneRenderer? renderer;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) BuildMeshesAndRenderer(capi);
    }

    private void BuildMeshesAndRenderer(ICoreClientAPI capi)
    {
        IAsset asset = capi.Assets.TryGet(new AssetLocation("dynamicwind", "shapes/blocks/weathervane.json"));
        if (asset == null) return;
        Shape shape = asset.ToObject<Shape>();
        ITexPositionSource tex = capi.Tesselator.GetTextureSource(Block);

        capi.Tesselator.TesselateShape("weathervane-base", shape, out baseMesh, tex, null, 0, 0, 0, null, BaseElements);
        capi.Tesselator.TesselateShape("weathervane-arrow", shape, out MeshData arrowMesh, tex, null, 0, 0, 0, null, ArrowElements);

        renderer = new WeathervaneRenderer(capi, this);
        renderer.SetMesh(capi.Render.UploadMesh(arrowMesh));
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "dynamicwind-weathervane");

        MarkDirty(true); // re-tesselate now that baseMesh is ready (so the arrow isn't baked statically)
    }

    /// <summary>Yaw (radians) for the arrow to point downwind; NaN when calm (hold position).</summary>
    public float GetTargetYaw()
    {
        if (Api == null) return float.NaN;
        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        if (w.X * w.X + w.Z * w.Z < 1e-8) return float.NaN; // calm
        // Arrow points downwind (away from the source). Model arrow = north (−Z) at yaw 0.
        return (float)Math.Atan2(-w.X, -w.Z);
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (baseMesh == null) return false; // not ready yet → let the full shape bake once
        mesher.AddMeshData(baseMesh);
        return true; // skip default (excludes the rotating arrow from the static mesh)
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
