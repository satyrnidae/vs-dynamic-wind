using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>
/// Minimap / world-map overlay that draws wind direction as a fixed 32×32 grid of arrows across the
/// map element. Each arrow samples the real wind (<c>GetWindSpeedAt</c>) at the world midpoint of its
/// cell, at the player's Y level, and is rotated to the wind bearing. Because the grid is fixed in
/// screen space, zooming the map out makes each arrow cover more world distance.
///
/// The arrow's length (its vertical axis) scales with wind speed: ~10% at 1% speed up to 100% at full
/// speed (base footprint = half a grid cell); cells with no wind are hidden. Samples are recomputed a
/// few times per second and cached, so the per-frame cost stays low even with 1024 cells.
/// </summary>
public class WindArrowMapLayer : MapLayer
{
    public const int Grid = 32;
    private const double RefSpeed = 1.0;       // wind speed treated as "100%"
    private const double RecomputeInterval = 0.15; // seconds between resamples

    private ICoreClientAPI? capi;
    private MeshRef? quadModel;
    private int arrowTexId;
    private readonly Matrixf mvMat = new();
    private readonly Vec4f white = new(1f, 1f, 1f, 1f);

    // Per-cell cache (index = row*Grid + col).
    private readonly float[] cAngle = new float[Grid * Grid];
    private readonly float[] cVScale = new float[Grid * Grid];
    private readonly bool[] cVisible = new bool[Grid * Grid];
    private double accum;
    private bool haveCache;

    public WindArrowMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => "Wind direction";
    public override string LayerGroupCode => "dynamicwind";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        capi = (ICoreClientAPI)api;
        quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        arrowTexId = capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/winddir.png"));
        Active = true; // shown by default; the map legend can still toggle it
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quadModel == null) return;

        accum += dt;
        if (!haveCache || accum >= RecomputeInterval)
        {
            accum = 0;
            Resample(mapElem);
            haveCache = true;
        }

        ElementBounds b = mapElem.Bounds;
        double cellW = b.InnerWidth / Grid;
        double cellH = b.InnerHeight / Grid;
        float baseHalf = (float)(0.25 * Math.Min(cellW, cellH)); // 0.5× cell footprint, half-extent
        if (baseHalf < 0.5f) return; // too small to see
        float renderX = (float)b.renderX;
        float renderY = (float)b.renderY;

        IShaderProgram shader = capi.Render.GetEngineShader((EnumShaderProgram)17);
        shader.Uniform("rgbaIn", white);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("applyColor", 0);
        shader.Uniform("noTexture", 0f);
        shader.BindTexture2D("tex2d", arrowTexId, 0);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

        for (int row = 0; row < Grid; row++)
        {
            for (int col = 0; col < Grid; col++)
            {
                int idx = row * Grid + col;
                if (!cVisible[idx]) continue;

                float px = renderX + (float)((col + 0.5) * cellW);
                float py = renderY + (float)((row + 0.5) * cellH);

                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(px, py, 60f)
                    .RotateZ(cAngle[idx])
                    .Scale(baseHalf, baseHalf * cVScale[idx], 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quadModel);
            }
        }
    }

    /// <summary>Resample wind at each cell's world midpoint (player Y) and cache angle + length scale.</summary>
    private void Resample(GuiElementMap mapElem)
    {
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double playerY = capi!.World.Player?.Entity?.Pos.Y ?? capi.World.SeaLevel;
        var pos = new Vec3d();

        for (int row = 0; row < Grid; row++)
        {
            double fz = (row + 0.5) / Grid;
            double wz = vb.Z1 + fz * (vb.Z2 - vb.Z1);
            for (int col = 0; col < Grid; col++)
            {
                double fx = (col + 0.5) / Grid;
                double wx = vb.X1 + fx * (vb.X2 - vb.X1);
                int idx = row * Grid + col;

                pos.Set(wx, playerY, wz);
                Vec3d w = capi.World.BlockAccessor.GetWindSpeedAt(pos);
                double speed = Math.Sqrt(w.X * w.X + w.Z * w.Z);
                if (speed < 0.005) { cVisible[idx] = false; continue; }

                double f = speed / RefSpeed;
                if (f > 1) f = 1;
                double vfrac = 0.1 + 0.9 * f; // 1% speed → ~10% length, 100% → 100%
                if (vfrac > 1) vfrac = 1;

                // Arrow art points up (north, screen -Y). Map +X=East=screen right, +Z=South=screen down.
                // Rotate model up (0,-1) onto the wind screen vector (w.X, w.Z).
                cAngle[idx] = (float)Math.Atan2(w.X, -w.Z);
                cVScale[idx] = (float)vfrac;
                cVisible[idx] = true;
            }
        }
    }

    public override void Dispose()
    {
        quadModel?.Dispose();
        quadModel = null;
    }
}
