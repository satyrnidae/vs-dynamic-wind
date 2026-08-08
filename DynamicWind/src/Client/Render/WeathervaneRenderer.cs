using DynamicWind.Blocks;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Renders the rotating upper portion (the arrow) of a weathervane, easing its yaw each frame toward the
/// current wind direction so it points DOWNWIND (away from the wind source). The static base + compass
/// letters are baked into the block model; only the arrow is drawn here, rotated about the block's
/// vertical centre axis.
/// </summary>
public class WeathervaneRenderer : VaneRendererBase
{
    private readonly BlockEntityWeathervane be;
    private MeshRef? meshref;
    private float currentYaw;
    private readonly TurnSqueakSound turnSqueak;

    public WeathervaneRenderer(ICoreClientAPI capi, BlockEntityWeathervane be) : base(capi, be.Pos.ToVec3d())
    {
        this.be = be;
        turnSqueak = new TurnSqueakSound(capi, pos);
    }

    public void SetMesh(MeshRef mesh) => meshref = mesh;

    public override void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (meshref == null || meshref.Disposed) return;

        if (!capi.IsGamePaused)
        {
            currentYaw = VaneTurnRate.EaseYaw(currentYaw, be.GetTargetYaw(), be.GetWindSpeed(), dt, turnSqueak);
        }

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        IStandardShaderProgram prog = BeginRender();
        prog.ModelMatrix = BuildYawMatrix(cam, currentYaw).Values;
        capi.Render.RenderMesh(meshref);
        EndRender(prog);
    }

    public override void Dispose()
    {
        base.Dispose();
        meshref?.Dispose();
        meshref = null;
    }
}
