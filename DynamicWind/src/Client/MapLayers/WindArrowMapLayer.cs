using System;
using DynamicWind.Config;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// World-map overlay (full map only, NOT the minimap HUD) drawing wind direction as a fixed 24x24 grid of
/// arrows. Each arrow samples once per cell, at its world midpoint (player Y): vanilla's raw wind noise via
/// <c>WeatherSystemBase.WeatherDataSlowAccess.GetWindSpeed</c>, fed through
/// <see cref="DynamicWindModCommon.ComputeWindSpeed"/> with <c>applyOcclusion: false</c> - the same true
/// directional speed <c>GetWindSpeedAt</c> gives, minus the indoor/underground occlusion check (room-registry
/// flood fill + light lookups), which is meaningless at this scale (a regional overview arrow isn't
/// "indoors") and is the single biggest per-sample cost once many of the 576 cells land in loaded chunks.
/// Requires chunk-loaded map region data (<see cref="RequireChunkLoaded"/>), same as the underlying vanilla
/// wind-speed reader.
///
/// Arrow length scales logarithmically with wind speed so low speeds stay legible (base footprint = half a
/// grid cell); cells with no wind are hidden. Samples are recomputed a few times per second and cached, so
/// per-frame cost stays low.
/// </summary>
public class WindArrowMapLayer : DynamicWindWorldMapLayerBase
{
    #region Grid Setup

    public const int Grid = 24;
    private const double RefSpeed = 1.0;       // wind speed treated as "100%"

    private DynamicWindConfig? cfg;
    private ICoreClientAPI? capi;
    private WeatherSystemBase? weather;
    private MeshRef? quadModel;
    private int arrowTexId;
    private readonly Matrixf mvMat = new();

    // Pressure tint endpoints (arrow art is greyscale; rgbaIn multiplies it).
    private static readonly Vec4f White = new(1f, 1f, 1f, 1f);
    private static readonly Vec4f LowColor = FrontColors.WarmCrimson; // shared with warm-front rendering
    private static readonly Vec4f HighColor = FrontColors.ColdBlue; // shared with cold-front rendering

    // Per-cell cache (index = row*Grid + col), sampled over the world-view bounds at the time of the
    // last Resample. The render grid itself is screen-fixed; between resamples we re-map each fixed
    // screen slot onto the nearest cell of THIS cached world grid (see NearestCachedIndex), so panning
    // the map shifts which cached measurement each stationary arrow shows, at full frame rate.
    private readonly float[] cAngle = new float[Grid * Grid];
    private readonly float[] cVScale = new float[Grid * Grid];
    private readonly Vec4f[] cColor = new Vec4f[Grid * Grid];
    private readonly bool[] cVisible = new bool[Grid * Grid];
    private Cuboidd cachedVb = new();

    public WindArrowMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => Lang.Get("game:maplayer-dynamicwind");
    public override string LayerGroupCode => "dynamicwind";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => true;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        cfg = DynamicWindRuntime.ClientConfig;
        capi = (ICoreClientAPI)api;
        weather = capi.ModLoader.GetModSystem<WeatherSystemClient>();
        quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        arrowTexId = capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/wind-direction.png"));
        RestoreActiveState(cfg?.WindDirectionMapLayerVisibleByDefault ?? false); // hidden by default; the map legend can still toggle it
    }

    #endregion

    #region Rendering

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quadModel == null) return;

        // Only on the full world map, not the minimap HUD (which snaps to the player).
        if (IsMinimap(mapElem)) return;

        TickResample(dt, mapElem);

        ElementBounds b = mapElem.Bounds;
        double cellW = b.InnerWidth / Grid;
        double cellH = b.InnerHeight / Grid;
        float baseHalf = (float)(0.25 * Math.Min(cellW, cellH)); // 0.5x cell footprint, half-extent
        if (baseHalf < 0.5f) return; // too small to see
        float renderX = (float)b.renderX;
        float renderY = (float)b.renderY;

        IShaderProgram shader = BindStandardShader(capi, arrowTexId);

        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double cvbSpanX = cachedVb.X2 - cachedVb.X1, cvbSpanZ = cachedVb.Z2 - cachedVb.Z1;

        for (int row = 0; row < Grid; row++)
        {
            for (int col = 0; col < Grid; col++)
            {
                // The screen slot itself never moves; only which cached sample it shows does. Map this
                // fixed slot's CURRENT world position back into the world grid as of the last resample,
                // and snap to that grid's nearest cell - this is what makes measurements drift between
                // arrows as the map is panned, without the arrows themselves relocating.
                int idx = NearestCachedIndex(row, col, vb, cvbSpanX, cvbSpanZ);
                if (!cVisible[idx]) continue;

                float px = renderX + (float)((col + 0.5) * cellW);
                float py = renderY + (float)((row + 0.5) * cellH);

                shader.Uniform("rgbaIn", cColor[idx]); // pressure tint (multiplies greyscale art)
                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(px, py, 60f)
                    .RotateZ(cAngle[idx])
                    .Scale(baseHalf, baseHalf * cVScale[idx], 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quadModel);
            }
        }
    }

    #endregion

    #region Sampling and Caching

    /// <summary>Fixed screen slot (row,col) -> nearest cell index in the world grid cached at the last
    /// Resample. If the cached bounds are degenerate (first frame), falls back to the same slot.</summary>
    private int NearestCachedIndex(int row, int col, Cuboidd vb, double cvbSpanX, double cvbSpanZ)
    {
        if (cvbSpanX < 1e-6 || cvbSpanZ < 1e-6) return row * Grid + col;

        double wx = vb.X1 + (col + 0.5) / Grid * (vb.X2 - vb.X1);
        double wz = vb.Z1 + (row + 0.5) / Grid * (vb.Z2 - vb.Z1);

        double oldFx = (wx - cachedVb.X1) / cvbSpanX;
        double oldFz = (wz - cachedVb.Z1) / cvbSpanZ;

        int oldCol = (int)Math.Clamp(Math.Round(oldFx * Grid - 0.5), 0, Grid - 1);
        int oldRow = (int)Math.Clamp(Math.Round(oldFz * Grid - 0.5), 0, Grid - 1);
        return oldRow * Grid + oldCol;
    }

    /// <summary>Resample wind at each cell's world midpoint (player Y) and cache angle + length scale.
    /// Exactly one wind-speed sample per arrow.</summary>
    protected override void Resample(GuiElementMap mapElem)
    {
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        WindField? field = DynamicWindRuntime.ClientField;
        DynamicWindModCommon? mod = DynamicWindRuntime.ClientModSystem;
        if (field == null || mod == null || weather == null) return;

        cachedVb = vb;
        double playerY = capi!.World.Player?.Entity?.Pos.Y ?? capi.World.SeaLevel;
        double th = capi.World.Calendar.TotalHours;
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
                double vsSpeed = weather.WeatherDataSlowAccess.GetWindSpeed(pos);
                Vec3d w = mod.ComputeWindSpeed(pos, vsSpeed, th, applyOcclusion: false);
                double speed = Math.Sqrt(w.X * w.X + w.Z * w.Z);
                if (speed < 0.005) { cVisible[idx] = false; continue; }

                double f = speed / RefSpeed;
                if (f > 1) f = 1;
                // Logarithmic length so low wind speeds stay legible (linear made them too small <50%).
                double g = Math.Log(1 + 9 * f) / Math.Log(10); // 0..1, boosts the low end
                double vfrac = 0.1 + 0.9 * g;
                if (vfrac > 1) vfrac = 1;

                // Arrow art points up (north, screen -Y). Map +X=East=screen right, +Z=South=screen down.
                // Rotate model up (0,-1) onto the wind screen vector (w.X, w.Z is world Z).
                cAngle[idx] = (float)Math.Atan2(w.X, -w.Z);
                cVScale[idx] = (float)vfrac;

                // Pressure tint: low (+) -> crimson, high (-) -> sky blue, neutral -> white. The map is a
                // horizontal slice, so we use the 2D field WITHOUT the vertical altitude lapse (which is
                // a per-Y effect - adding it here just washes every tile by a constant playerY offset).
                double p = field?.PressureAt(wx, wz, th) ?? 0.0;
                float t = (float)Math.Min(1.0, Math.Abs(p));
                cColor[idx] = Lerp(White, p >= 0 ? LowColor : HighColor, t);

                cVisible[idx] = true;
            }
        }
    }

    #endregion

    public override void Dispose()
    {
        quadModel?.Dispose();
        quadModel = null;
    }
}
