using System.Text;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DynamicWind.Blocks;

/// <summary>
/// Shared plumbing for the weathervane/rotorvane block entities. Both bake a static "stationary" post +
/// compass-letter mesh into the block model and hand a separate "rotating" mesh off to a client-side
/// <see cref="IRenderer"/> that eases its yaw toward the current wind bearing (shared math lives in
/// <see cref="WindSystem.VaneWind"/> - see that class for the underground/undergroundWindmills occlusion
/// gate both vanes honour). Subclasses supply the shape asset, the debug/registration names used for
/// tesselation, and how to build their own renderer - which may tesselate additional meshes beyond the
/// shared base (e.g. the rotorvane's spinner) - via <see cref="CreateRenderer"/>.
/// </summary>
public abstract class BlockEntityVaneBase : BlockEntity
{
    // "name/*" includes the element AND its whole subtree. An exact name matches only that element and
    // passes an EMPTY child filter (VS ShapeTesselator.SelectiveMatch), dropping children - and these
    // groups are zero-size locators whose geometry lives entirely in their children. Both vane shapes use
    // "stationary" for their static post + compass letters, baked once into the block model.
    protected static readonly string[] BaseElements = { "stationary/*" };

    protected MeshData? baseMesh;
    protected IRenderer? renderer;

    /// <summary>Shape asset for this vane, e.g. <c>shapes/block/weathervane.json</c>.</summary>
    protected abstract AssetLocation ShapeLocation { get; }

    /// <summary>Tesselator debug name for the static base mesh.</summary>
    protected abstract string BaseMeshDebugName { get; }

    /// <summary>Name this vane's renderer is registered under (<see cref="IEventAPI.RegisterRenderer"/>).</summary>
    protected abstract string RendererRegistrationName { get; }

    /// <summary>Tesselates whatever additional (rotating) meshes this vane needs and builds its renderer.
    /// Called after the shared base mesh has already been tesselated.</summary>
    protected abstract IRenderer CreateRenderer(ICoreClientAPI capi, Shape shape, ITexPositionSource tex);

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) BuildMeshesAndRenderer(capi);
    }

    private void BuildMeshesAndRenderer(ICoreClientAPI capi)
    {
        IAsset asset = capi.Assets.TryGet(ShapeLocation);
        if (asset == null) return;
        Shape shape = asset.ToObject<Shape>();
        ITexPositionSource tex = capi.Tesselator.GetTextureSource(Block);

        capi.Tesselator.TesselateShape(BaseMeshDebugName, shape, out baseMesh, tex, null, 0, 0, 0, null, BaseElements);

        renderer = CreateRenderer(capi, shape, tex);
        capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, RendererRegistrationName);

        MarkDirty(true); // re-tesselate now that baseMesh is ready (so the rotating parts aren't baked statically)
    }

    /// <summary>Yaw (radians) for the rotating assembly to point downwind; NaN when calm/occluded (hold position).</summary>
    public float GetTargetYaw()
    {
        if (Api == null) return float.NaN;
        // Like windmill rotors (BEBehaviorWindmillRotor.CheckWindSpeed): a vane buried in a cave or
        // otherwise not sky-exposed catches no wind, so it holds still. Gate on sun light level (< 5),
        // honouring the same `undergroundWindmills` world-config escape hatch windmills use.
        if (VaneWind.IsOccluded(Api, Pos)) return float.NaN;

        Vec3d w = Api.World.BlockAccessor.GetWindSpeedAt(Pos.ToVec3d());
        return VaneWind.TargetYawFor(w);
    }

    /// <summary>True directional wind magnitude at this block (0 when calm or occluded); drives how fast
    /// the renderer eases the assembly toward its target yaw (see <see cref="Client.Render.VaneTurnRate"/>)
    /// - same cave/undergroundWindmills gate as <see cref="GetTargetYaw"/>.</summary>
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
        if (baseMesh == null) return false; // not ready yet -> let the full shape bake once
        mesher.AddMeshData(baseMesh);
        return true; // skip default (excludes the rotating parts from the static mesh)
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        DisposeRendererAndExtras();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        DisposeRendererAndExtras();
    }

    private void DisposeRendererAndExtras()
    {
        renderer?.Dispose();
        renderer = null;
        OnDisposeExtras();
    }

    /// <summary>Hook for subclass-only teardown beyond the shared renderer dispose (e.g. rotorvane's sound loop).</summary>
    protected virtual void OnDisposeExtras() { }
}
