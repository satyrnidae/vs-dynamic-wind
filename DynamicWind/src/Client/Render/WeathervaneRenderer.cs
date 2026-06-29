using System;
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
public class WeathervaneRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly BlockEntityWeathervane be;
    private readonly Vec3d pos;
    private readonly Matrixf modelMat = new();
    private MeshRef? meshref;
    private float currentYaw;

    public double RenderOrder => 0.5;
    public int RenderRange => 48;

    public WeathervaneRenderer(ICoreClientAPI capi, BlockEntityWeathervane be)
    {
        this.capi = capi;
        this.be = be;
        pos = be.Pos.ToVec3d();
    }

    public void SetMesh(MeshRef mesh) => meshref = mesh;

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (meshref == null || meshref.Disposed) return;

        float target = be.GetTargetYaw();
        if (!float.IsNaN(target))
        {
            float delta = GameMath.AngleRadDistance(currentYaw, target);
            currentYaw += delta * Math.Min(1f, dt * 3f); // smooth swing
        }

        IRenderAPI rpi = capi.Render;
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        rpi.GlDisableCullFace();
        rpi.GlToggleBlend(true);

        IStandardShaderProgram prog = rpi.PreparedStandardShader((int)pos.X, (int)pos.Y, (int)pos.Z);
        prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
        prog.ModelMatrix = modelMat
            .Identity()
            .Translate(pos.X - cam.X + 0.5, pos.Y - cam.Y, pos.Z - cam.Z + 0.5)
            .RotateY(currentYaw)
            .Translate(-0.5, 0, -0.5)
            .Values;
        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
        rpi.RenderMesh(meshref);
        prog.Stop();
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        meshref?.Dispose();
        meshref = null;
    }
}
