using System.Text;
using DynamicWind.Client.Render;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DynamicWind.Blocks;

/// <summary>
/// Weathervane block entity. The static post + compass letters (<c>stalk</c>, <c>nsbone</c>,
/// <c>webone</c>) are baked into the block model; the <c>upperportion</c> (arrow) is rendered by
/// <see cref="WeathervaneRenderer"/>, rotated to point downwind. The arrow points north (−Z) at yaw 0,
/// matching the model. Wind is sampled via the directional <c>GetWindSpeedAt</c> hook.
/// </summary>
public class BlockEntityWeathervane : BlockEntity
{
    // "name/*" includes the element AND its whole subtree. An exact name matches only that element and
    // passes an EMPTY child filter (VS ShapeTesselator.SelectiveMatch), dropping children — and these
    // groups are zero-size locators whose geometry lives entirely in their children. The model has two
    // top-level groups: "stationary" (post + compass letters, baked statically) and "rotating" (the
    // wind-tracking arrow assembly, drawn by WeathervaneRenderer rotated about the block centre).
    private static readonly string[] BaseElements = { "stationary/*" };
    private static readonly string[] ArrowElements = { "rotating/*" };

    private MeshData? baseMesh;
    private WeathervaneRenderer? renderer;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) BuildMeshesAndRenderer(capi);
    }

    private void BuildMeshesAndRenderer(ICoreClientAPI capi)
    {
        IAsset asset = capi.Assets.TryGet(new AssetLocation("dynamicwind", "shapes/block/weathervane.json"));
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

    /// <summary>Yaw (radians) for the arrow to point downwind; NaN when calm/occluded (hold position).</summary>
    public float GetTargetYaw()
    {
        if (Api == null) return float.NaN;
        // Like windmill rotors (BEBehaviorWindmillRotor.CheckWindSpeed): a weathervane buried in a cave or
        // otherwise not sky-exposed catches no wind, so it holds still. Gate on sun light level (< 5),
        // honouring the same `undergroundWindmills` world-config escape hatch windmills use.
        if (VaneWind.IsOccluded(Api, Pos)) return float.NaN;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        return VaneWind.TargetYawFor(w);
    }

    /// <summary>True directional wind magnitude at this block (0 when calm or occluded); drives how fast
    /// <see cref="WeathervaneRenderer"/> eases the arrow toward its target yaw (see
    /// <see cref="Client.Render.VaneTurnRate"/>) — same cave/undergroundWindmills gate as
    /// <see cref="GetTargetYaw"/>.</summary>
    public double GetWindSpeed()
    {
        if (Api == null || VaneWind.IsOccluded(Api, Pos)) return 0.0;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        return VaneWind.SpeedOf(w);
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        if (Api == null) return;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        dsc.AppendLine(Lang.Get("dynamicwind:blockinfo-windheading", WindCompass.GetName(w.X, w.Z)));
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
