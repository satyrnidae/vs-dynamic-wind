using System;
using System.Collections.Generic;
using DynamicWind.Config;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// Synoptic weather-front overlay (world map only, not the minimap). Draws the classic surface-analysis
/// symbols derived from the synced pressure field by <see cref="FrontComputer"/>: an "H"/"L" letter at
/// each pressure-system centre, and a tiled contour of warm/cold/stationary front symbols traced
/// around each system — the full loop for a near-stationary/occluded system, or two open curves
/// (warm on the poleward side, cold on the equatorward side) marched outward from a moving system's
/// centre, each tile individually rotated to the local contour tangent so the line reads as
/// contiguous even where it curves. Toggle via the map legend.
/// </summary>
public class FrontMapLayer : DynamicWindWorldMapLayerBase
{
    private const float TilePx = 32f;     // on-screen length of one front segment tile
    private const float MarkerPx = 48f;   // on-screen size of an H/L letter
    private const double CurveAmpFactor = 0.06; // × front length, curvature amplitude

    private DynamicWindConfig? cfg;
    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private int texWarm, texCold, texStationary, texOccluded, texColdOcclusion, texWarmOcclusion, texHigh, texLow;
    private readonly Matrixf mvMat = new();
    private static readonly Vec4f White = new(1f, 1f, 1f, 1f);

    private readonly List<FrontMarker> markers = new();
    private readonly List<FrontLine> lines = new();
    private readonly List<LineScratch> scratch = new();

    private sealed class LineScratch
    {
        public float[] Sxs = Array.Empty<float>();
        public float[] Sys = Array.Empty<float>();
        public float[] Fas = Array.Empty<float>();
        public double[] CumulativePx = Array.Empty<double>();

        public void EnsureCapacity(int n)
        {
            if (Sxs.Length >= n) return;
            Sxs = new float[n];
            Sys = new float[n];
            Fas = new float[n];
            CumulativePx = new double[n];
        }
    }

    public FrontMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => Lang.Get("game:maplayer-dynamicwindfronts");
    public override string LayerGroupCode => "dynamicwindfronts";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        cfg = DynamicWindRuntime.ClientConfig;
        capi = (ICoreClientAPI)api;
        quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        texWarm = Tex("front-warm");
        texCold = Tex("front-cold");
        texStationary = Tex("front-stationary");
        texOccluded = Tex("front-occluded");
        texColdOcclusion = Tex("front-cold-occluded");
        texWarmOcclusion = Tex("front-warm-occluded");
        texHigh = Tex("pressure-system-high");
        texLow = Tex("pressure-system-low");
        Active = cfg?.FrontMapLayerVisibleByDefault ?? false; // off by default; toggle in the map legend
    }

    private int Tex(string name) => capi!.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/" + name + ".png"));

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null) return;
        if (IsMinimap(mapElem)) return; // world map only

        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null) return;

        TickResample(dt, mapElem);

        ElementBounds b = mapElem.Bounds;
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double spanX = vb.X2 - vb.X1, spanZ = vb.Z2 - vb.Z1;
        if (spanX < 1e-6 || spanZ < 1e-6) return;
        double scaleX = b.InnerWidth / spanX;  // px per block
        double scaleZ = b.InnerHeight / spanZ;
        double th = capi.World.Calendar.TotalHours;

        IShaderProgram shader = BindStandardShader(capi, texWarm);
        shader.Uniform("rgbaIn", White);

        // Fronts: every front (Warm/Cold from a moving low's poleward/equatorward sides, Stationary
        // toward its nearest neighbour, Occluded/ColdOcclusion/WarmOcclusion toward whatever its cold
        // front caught) is an open curve marched from the low — see FrontComputer. Sampled uniformly over
        // t in [0,1] via FrontComputer.SampleAt. Geometry comes from FrontComputer (deterministic, cells-only).
        for (int li = 0; li < lines.Count; li++)
        {
            FrontLine f = lines[li];
            if (f.Points.Length == 0) continue;

            double curveLen = FrontComputer.CurveLength(f);
            if (curveLen < 1e-3) continue;
            double charLen = curveLen / 4.0;
            double curveAmpL = CurveAmpFactor * charLen;
            double lenPx = curveLen * scaleX;

            // Walk the curve finely (world space -> screen space) and tile it by ACTUAL on-screen
            // arc length, not by uniform parameter steps. A uniform-t split under/over-spaces tiles
            // wherever the curve bulges away from its mean spacing (elongated systems, the Curve()
            // wobble) — real arc length is what has to stay tiled with no gaps, regardless of zoom or
            // how stretched the front is.
            int nFine = (int)Math.Clamp(Math.Ceiling(lenPx / (TilePx * 0.5)), 32, 3000);

            while (scratch.Count <= li) scratch.Add(new LineScratch());
            LineScratch sc = scratch[li];
            sc.EnsureCapacity(nFine + 1);
            float[] sxs = sc.Sxs, sys = sc.Sys, fas = sc.Fas;
            double[] cumPx = sc.CumulativePx;
            for (int j = 0; j <= nFine; j++)
            {
                double t = (double)j / nFine;
                FrontComputer.SampleAt(f, t, curveAmpL, th, out double wx, out double wz, out double fa);
                fas[j] = (float)fa;
                ToScreen(b, vb, wx, wz, out sxs[j], out sys[j]); // ignore visibility flag; still valid off-screen coords
                if (j > 0)
                {
                    double dx = sxs[j] - sxs[j - 1], dy = sys[j] - sys[j - 1];
                    cumPx[j] = cumPx[j - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
            }
            double totalLenPx = cumPx[nFine];
            if (totalLenPx < 1e-3) continue;

            int nT = (int)Math.Clamp(Math.Ceiling(totalLenPx / TilePx), 4, 3000);
            double step = totalLenPx / nT;

            int tex = f.Type switch
            {
                FrontType.Stationary => texStationary,
                FrontType.Occluded => texOccluded,
                FrontType.ColdOcclusion => texColdOcclusion,
                FrontType.WarmOcclusion => texWarmOcclusion,
                FrontType.Warm => texWarm,
                _ => texCold
            };
            shader.BindTexture2D("tex2d", tex, 0);

            float halfH = TilePx * 0.5f;
            float halfW = TilePx * 0.25f;
            int seg = 0;
            for (int i = 0; i < nT; i++)
            {
                double targetDist = (i + 0.5) * step;
                while (seg < nFine - 1 && cumPx[seg + 1] < targetDist) seg++;
                double segLen = cumPx[seg + 1] - cumPx[seg];
                double segT = segLen > 1e-9 ? (targetDist - cumPx[seg]) / segLen : 0;
                float sx = (float)(sxs[seg] + (sxs[seg + 1] - sxs[seg]) * segT);
                float sy = (float)(sys[seg] + (sys[seg + 1] - sys[seg]) * segT);

                // Cull tiles well outside the visible map element (same tolerance as ToScreen used).
                float fx = (float)((sx - b.renderX) / b.InnerWidth);
                float fy = (float)((sy - b.renderY) / b.InnerHeight);
                if (fx < -0.05f || fx > 1.05f || fy < -0.05f || fy > 1.05f) continue;

                // Symbols face outward, radially, for a full loop; for a Warm/Cold open curve they face
                // whichever tangent-perpendicular best matches the low's own direction of travel (see
                // FrontComputer.ChooseFacing) — stays correct even where a JOIN curve bends sharply.
                float angle = AngleMath.AngleLerp(fas[seg], fas[seg + 1], (float)segT);

                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(sx, sy, 62f)
                    .RotateZ(angle)
                    .Scale(halfW, halfH, 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quad);
            }
        }

        // H / L letters.
        float mHalf = MarkerPx * 0.5f;
        for (int mi = 0; mi < markers.Count; mi++)
        {
            FrontMarker m = markers[mi];
            if (!ToScreen(b, vb, m.X, m.Z, out float sx, out float sy)) continue;
            shader.BindTexture2D("tex2d", m.IsHigh ? texHigh : texLow, 0);
            mvMat.Set(capi.Render.CurrentModelviewMatrix)
                .Translate(sx, sy, 63f)
                .Scale(mHalf, mHalf, 0f);
            shader.UniformMatrix("modelViewMatrix", mvMat.Values);
            capi.Render.RenderMesh(quad);
        }
    }

    protected override void Resample(GuiElementMap mapElem)
    {
        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null || capi == null) return;
        FrontComputer.Compute(field, capi.World.Calendar.TotalHours, markers, lines);
    }

    public override void Dispose()
    {
        quad?.Dispose();
        quad = null;
    }
}
