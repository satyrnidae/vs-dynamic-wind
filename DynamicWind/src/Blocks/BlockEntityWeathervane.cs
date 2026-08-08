using DynamicWind.Client.Render;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace DynamicWind.Blocks;

/// <summary>
/// Weathervane block entity. The static post + compass letters (<c>stalk</c>, <c>nsbone</c>,
/// <c>webone</c>) are baked into the block model; the <c>upperportion</c> (arrow) is rendered by
/// <see cref="WeathervaneRenderer"/>, rotated to point downwind. The arrow points north (-Z) at yaw 0,
/// matching the model. Wind is sampled via the directional <c>GetWindSpeedAt</c> hook. Shared mesh/
/// tesselation/dispose/block-info plumbing lives in <see cref="BlockEntityVaneBase"/>.
/// </summary>
public class BlockEntityWeathervane : BlockEntityVaneBase
{
    // The model has two top-level groups: "stationary" (post + compass letters, baked statically, handled
    // by the base class) and "rotating" (the wind-tracking arrow assembly, drawn by WeathervaneRenderer
    // rotated about the block centre).
    private static readonly string[] ArrowElements = { "rotating/*" };

    protected override AssetLocation ShapeLocation => new("dynamicwind", "shapes/block/weathervane.json");
    protected override string BaseMeshDebugName => "weathervane-base";
    protected override string RendererRegistrationName => "dynamicwind-weathervane";

    protected override IRenderer CreateRenderer(ICoreClientAPI capi, Shape shape, ITexPositionSource tex)
    {
        capi.Tesselator.TesselateShape("weathervane-arrow", shape, out MeshData arrowMesh, tex, null, 0, 0, 0, null, ArrowElements);

        WeathervaneRenderer renderer = new(capi, this);
        renderer.SetMesh(capi.Render.UploadMesh(arrowMesh));
        return renderer;
    }
}
