using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Shared by <see cref="WeathervaneRenderer"/> and <see cref="RotorvaneRenderer"/>: both draw one or more
/// meshes centred on a block position, rotated about the vertical axis by an eased yaw, using the same
/// prepared-standard-shader/blend-state setup. Subclasses drive their own yaw-easing/mesh selection and
/// call <see cref="BeginRender"/>/<see cref="BuildYawMatrix"/>/<see cref="EndRender"/> around their
/// <c>RenderMesh</c> calls.
/// </summary>
public abstract class VaneRendererBase : IRenderer
{
    protected readonly ICoreClientAPI capi;
    protected readonly Vec3d pos;
    protected readonly Matrixf modelMat = new();

    public double RenderOrder => 0.5;
    public int RenderRange => 48;

    protected VaneRendererBase(ICoreClientAPI capi, Vec3d pos)
    {
        this.capi = capi;
        this.pos = pos;
    }

    /// <summary>Sets up the shared blend state and prepared shader for this frame's draw calls.</summary>
    protected IStandardShaderProgram BeginRender()
    {
        IRenderAPI rpi = capi.Render;
        rpi.GlDisableCullFace();
        rpi.GlToggleBlend(true);

        IStandardShaderProgram prog = rpi.PreparedStandardShader((int)pos.X, (int)pos.Y, (int)pos.Z);
        prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
        return prog;
    }

    /// <summary>Translate-rotate-translate matrix centred on this vane's block and rotated by
    /// <paramref name="yaw"/>, relative to the given camera position. Callers may chain further transforms
    /// (e.g. the rotorvane's spinner hub) onto the returned matrix before reading <c>.Values</c>.</summary>
    protected Matrixf BuildYawMatrix(Vec3d cam, float yaw)
    {
        return modelMat
            .Identity()
            .Translate(pos.X - cam.X + 0.5, pos.Y - cam.Y, pos.Z - cam.Z + 0.5)
            .RotateY(yaw)
            .Translate(-0.5, 0, -0.5);
    }

    protected void EndRender(IStandardShaderProgram prog) => prog.Stop();

    public abstract void OnRenderFrame(float dt, EnumRenderStage stage);

    public virtual void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}
