using System;
using DynamicWind.Blocks;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Renders the rotating parts of a rotorvane: the arrow/mast assembly eases its yaw toward the wind
/// bearing exactly like <see cref="WeathervaneRenderer"/>, while the 12-bladed spinner additionally spins
/// about its own hub axis (the shape's local X axis at the spinner locator) at a rate driven by wind
/// speed. The spinner's blades sweep the shape's local Y-Z plane (see rotorvane.json — each blade is
/// placed via <c>rotationX</c>), so spinning it is a second RotateX applied about the hub pivot BEFORE the
/// shared yaw, rather than composed with it.
/// </summary>
public class RotorvaneRenderer : IRenderer
{
    // Real fan blades have rotational inertia — easing the spinner's angular speed toward its raw target
    // instead of snapping to it every frame keeps gusty/noisy wind readings (and GetDirectionalMod, which
    // depends on the assembly's still-swinging yaw) from making it visibly jerk speed up/down or flicker
    // direction.
    private const float SpinSpeedEaseRatePerSecond = 2f;

    private readonly ICoreClientAPI capi;
    private readonly BlockEntityRotorvane be;
    private readonly Vec3d pos;
    private readonly Vec3f spinnerPivot;
    private readonly Matrixf modelMat = new();
    private MeshRef? assemblyMeshref;
    private MeshRef? spinnerMeshref;
    private float currentYaw;
    private float spinAngle;
    private float currentSpinSpeed;
    private readonly TurnSqueakSound turnSqueak;

    public double RenderOrder => 0.5;
    public int RenderRange => 48;

    /// <summary>The assembly's current eased yaw (radians); read by <see cref="BlockEntityRotorvane"/> to
    /// derive the facing-direction spin/pitch modulator from how far it still lags the true wind bearing.</summary>
    public float CurrentYaw => currentYaw;

    public RotorvaneRenderer(ICoreClientAPI capi, BlockEntityRotorvane be, Vec3f spinnerPivot)
    {
        this.capi = capi;
        this.be = be;
        this.spinnerPivot = spinnerPivot;
        pos = be.Pos.ToVec3d();
        turnSqueak = new TurnSqueakSound(capi, pos);
    }

    public void SetMeshes(MeshRef assemblyMesh, MeshRef spinnerMesh)
    {
        assemblyMeshref = assemblyMesh;
        spinnerMeshref = spinnerMesh;
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (assemblyMeshref == null || assemblyMeshref.Disposed) return;
        if (spinnerMeshref == null || spinnerMeshref.Disposed) return;

        if (!capi.IsGamePaused)
        {
            float target = be.GetTargetYaw();
            if (!float.IsNaN(target))
            {
                float delta = GameMath.AngleRadDistance(currentYaw, target);
                float rate = VaneTurnRate.Compute(be.GetWindSpeed());
                currentYaw += delta * Math.Min(1f, dt * rate); // smooth swing, faster in stronger wind
            }
            turnSqueak.Update(currentYaw, target);

            float targetSpinSpeed = be.GetSpinSpeed();
            currentSpinSpeed += (targetSpinSpeed - currentSpinSpeed) * Math.Min(1f, dt * SpinSpeedEaseRatePerSecond);
            spinAngle += currentSpinSpeed * dt;
            spinAngle = GameMath.Mod(spinAngle, GameMath.TWOPI);
        }

        IRenderAPI rpi = capi.Render;
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        rpi.GlDisableCullFace();
        rpi.GlToggleBlend(true);

        IStandardShaderProgram prog = rpi.PreparedStandardShader((int)pos.X, (int)pos.Y, (int)pos.Z);
        prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

        prog.ModelMatrix = modelMat
            .Identity()
            .Translate(pos.X - cam.X + 0.5, pos.Y - cam.Y, pos.Z - cam.Z + 0.5)
            .RotateY(currentYaw)
            .Translate(-0.5, 0, -0.5)
            .Values;
        rpi.RenderMesh(assemblyMeshref);

        prog.ModelMatrix = modelMat
            .Identity()
            .Translate(pos.X - cam.X + 0.5, pos.Y - cam.Y, pos.Z - cam.Z + 0.5)
            .RotateY(currentYaw)
            .Translate(-0.5, 0, -0.5)
            .Translate(spinnerPivot.X, spinnerPivot.Y, spinnerPivot.Z)
            .RotateX(spinAngle)
            .Translate(-spinnerPivot.X, -spinnerPivot.Y, -spinnerPivot.Z)
            .Values;
        rpi.RenderMesh(spinnerMeshref);

        prog.Stop();
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        assemblyMeshref?.Dispose();
        assemblyMeshref = null;
        spinnerMeshref?.Dispose();
        spinnerMeshref = null;
    }
}
