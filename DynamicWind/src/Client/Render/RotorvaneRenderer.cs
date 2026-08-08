using System;
using DynamicWind.Blocks;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Renders the rotating parts of a rotorvane: the arrow/mast assembly eases its yaw toward the wind
/// bearing exactly like <see cref="WeathervaneRenderer"/>, while the 12-bladed spinner additionally spins
/// about its own hub axis (the shape's local X axis at the spinner locator) at a rate driven by wind
/// speed. The spinner's blades sweep the shape's local Y-Z plane (see rotorvane.json - each blade is
/// placed via <c>rotationX</c>), so spinning it is a second RotateX applied about the hub pivot BEFORE the
/// shared yaw, rather than composed with it.
/// </summary>
public class RotorvaneRenderer : VaneRendererBase
{
    // Eased, not snapped, so gusty wind readings don't make the spinner visibly jerk speed up/down.
    private const float SpinSpeedEaseRatePerSecond = 2f;

    private readonly BlockEntityRotorvane be;
    private readonly Vec3f spinnerPivot;
    private MeshRef? assemblyMeshref;
    private MeshRef? spinnerMeshref;
    private float currentYaw;
    private float spinAngle;
    private float currentSpinSpeed;
    private readonly TurnSqueakSound turnSqueak;

    /// <summary>The assembly's current eased yaw (radians); read by <see cref="BlockEntityRotorvane"/> to
    /// derive the facing-direction spin/pitch modulator from how far it still lags the true wind bearing.</summary>
    public float CurrentYaw => currentYaw;

    public RotorvaneRenderer(ICoreClientAPI capi, BlockEntityRotorvane be, Vec3f spinnerPivot) : base(capi, be.Pos.ToVec3d())
    {
        this.be = be;
        this.spinnerPivot = spinnerPivot;
        turnSqueak = new TurnSqueakSound(capi, pos);
    }

    public void SetMeshes(MeshRef assemblyMesh, MeshRef spinnerMesh)
    {
        assemblyMeshref = assemblyMesh;
        spinnerMeshref = spinnerMesh;
    }

    public override void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (assemblyMeshref == null || assemblyMeshref.Disposed) return;
        if (spinnerMeshref == null || spinnerMeshref.Disposed) return;

        if (!capi.IsGamePaused)
        {
            currentYaw = VaneTurnRate.EaseYaw(currentYaw, be.GetTargetYaw(), be.GetWindSpeed(), dt, turnSqueak);

            float targetSpinSpeed = be.GetSpinSpeed();
            currentSpinSpeed += (targetSpinSpeed - currentSpinSpeed) * Math.Min(1f, dt * SpinSpeedEaseRatePerSecond);
            spinAngle += currentSpinSpeed * dt;
            spinAngle = GameMath.Mod(spinAngle, GameMath.TWOPI);
        }

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        IStandardShaderProgram prog = BeginRender();

        prog.ModelMatrix = BuildYawMatrix(cam, currentYaw).Values;
        capi.Render.RenderMesh(assemblyMeshref);

        prog.ModelMatrix = BuildYawMatrix(cam, currentYaw)
            .Translate(spinnerPivot.X, spinnerPivot.Y, spinnerPivot.Z)
            .RotateX(spinAngle)
            .Translate(-spinnerPivot.X, -spinnerPivot.Y, -spinnerPivot.Z)
            .Values;
        capi.Render.RenderMesh(spinnerMeshref);

        EndRender(prog);
    }

    public override void Dispose()
    {
        base.Dispose();
        assemblyMeshref?.Dispose();
        assemblyMeshref = null;
        spinnerMeshref?.Dispose();
        spinnerMeshref = null;
    }
}
