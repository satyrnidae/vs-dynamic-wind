using System;
using System.Collections.Generic;
using DynamicWind.Config;
using DynamicWind.PressureSystem;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// Synoptic weather-front overlay (world map only, not the minimap). Draws the classic surface-analysis
/// symbols derived from the synced pressure field by <see cref="FrontComputer"/>: an "H"/"L" letter for
/// each pressure system (at its true centre when that's on-screen; otherwise clamped to the system's OWN
/// elliptical edge toward the viewport — <see cref="Utils.EllipseGeometry.RadiusToward(double,double,double,double,double)"/>,
/// the same boundary a front's own leading-edge arc traces — never drawn at all if even that edge is
/// off-screen, since cell radii are now large enough that a system's centre routinely sits off the visible
/// map, and the icon must never read as further "ahead" of the system than its actual influence reaches),
/// drawn BEHIND everything else so it never covers the front geometry it labels, and a procedurally drawn
/// contour around each system — a single connecting line for a near-stationary/occluded system, or (a
/// moving open-wave low) BOTH a warm front leading ahead of its travel direction and a cold front trailing
/// behind it, each tilted toward its real-world side (warm poleward, cold equatorward — see
/// <see cref="FrontComputer"/>) so together they read as the classic Norwegian-cyclone "comma" shape.
/// Each front is drawn as two independent
/// passes: a continuous stroke that bridges every true fine-sampled segment of the curve (so the line
/// always follows the curve exactly, however sharply it bends — no more angle-lerped icon tiles
/// gapping/overlapping at sharp bends), and a periodic decoration pass (triangles for cold, hemicircles for
/// warm, alternating for stationary/generic-occluded) stamped at even on-screen arc-length spacing, both
/// colored per <see cref="FrontColors"/>. Also draws isobars (<see cref="IsobarRing"/>, from the same
/// `FrontComputer.Compute` call) as faint closed contour loops behind everything else — real-chart-style
/// constant-pressure rings around EVERY significant cell (highs included, unlike fronts), tinted the same
/// low/high colors as the wind arrows. Toggle via the map legend.
/// </summary>
public class FrontMapLayer : DynamicWindWorldMapLayerBase
{
    private const float TilePx = 32f;     // on-screen spacing between decorations (and stroke/decoration color alternation)
    private const float MarkerPx = 48f;   // on-screen size of an H/L letter
    private const float LineWidthPx = 4f;       // stroke width
    private const float SymbolLengthPx = 20f;   // how far a pip/hemicircle bump pokes out from the line
    private const float SymbolWidthPx = 16f;    // pip/hemicircle bump's width along the line
    private const float IsobarLineWidthPx = 2f; // isobar stroke width — thinner than a front, background context
    private const float IsobarBaseAlpha = 0.4f; // innermost-ring opacity
    private const float IsobarAlphaStep = 0.04f; // opacity reduction per ring outward from the innermost
    private const float IsobarMinAlpha = 0.12f;

    private DynamicWindConfig? cfg;
    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private MeshRef? triMesh;
    private MeshRef? semiMesh;
    private int texWhite, texHigh, texLow;
    private readonly Matrixf mvMat = new();
    private static readonly Vec4f White = new(1f, 1f, 1f, 1f);

    private readonly List<FrontMarker> markers = new();
    private readonly List<FrontLine> lines = new();
    private readonly List<IsobarRing> isobars = new();
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
        triMesh = capi.Render.UploadMesh(ShapeMeshUtil.GetTriangle());
        semiMesh = capi.Render.UploadMesh(ShapeMeshUtil.GetSemicircle());
        texWhite = Tex("front-line-white");
        texHigh = Tex("pressure-system-high");
        texLow = Tex("pressure-system-low");
        RestoreActiveState(cfg?.FrontMapLayerVisibleByDefault ?? false); // off by default; toggle in the map legend
    }

    private int Tex(string name) => capi!.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/" + name + ".png"));

    /// <summary>Picks the stroke/decoration color and decoration shape for a given front type at a given
    /// decoration tile index (used to alternate Stationary/generic-Occluded fronts between cold- and
    /// warm-styled segments).</summary>
    private static void GetFrontStyle(FrontType type, int tileIndex, out Vec4f color, out bool useSemicircle)
    {
        switch (type)
        {
            case FrontType.Cold:
                color = FrontColors.ColdBlue;
                useSemicircle = false;
                break;
            case FrontType.Warm:
                color = FrontColors.WarmCrimson;
                useSemicircle = true;
                break;
            case FrontType.ColdOcclusion:
                color = FrontColors.OccludedPurple;
                useSemicircle = false;
                break;
            case FrontType.WarmOcclusion:
                color = FrontColors.OccludedPurple;
                useSemicircle = true;
                break;
            case FrontType.Stationary:
                useSemicircle = (tileIndex % 2) != 0;
                color = useSemicircle ? FrontColors.WarmCrimson : FrontColors.ColdBlue;
                break;
            default: // FrontType.Occluded — generic/unset occlusion kind
                useSemicircle = (tileIndex % 2) != 0;
                color = FrontColors.OccludedPurple;
                break;
        }
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null || triMesh == null || semiMesh == null) return;
        if (IsMinimap(mapElem)) return; // world map only

        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null) return;

        TickResample(dt, mapElem);

        ElementBounds b = mapElem.Bounds;
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double spanX = vb.X2 - vb.X1, spanZ = vb.Z2 - vb.Z1;
        if (spanX < 1e-6 || spanZ < 1e-6) return;
        double scaleX = b.InnerWidth / spanX;  // px per block

        IShaderProgram shader = BindStandardShader(capi, texWhite);

        // Isobars: drawn first/furthest back — faint background context, never competing with the front
        // lines/markers/decorations drawn after them. Each ring is already a closed polyline
        // (FrontComputer.BuildIsobarRing); no fine-sampling pass needed like the front lines (no added
        // wobble to resolve — see FrontComputer's remarks — and a closed ellipse doesn't need arc-length
        // tiling for decorations), so segments are drawn directly between the ring's own points.
        for (int ri = 0; ri < isobars.Count; ri++)
        {
            IsobarRing ring = isobars[ri];
            Vec2d[] pts = ring.Points;
            if (pts.Length < 2) continue;

            Vec4f baseColor = ring.IsLow ? FrontColors.WarmCrimson : FrontColors.ColdBlue;
            float alpha = Math.Max(IsobarMinAlpha, IsobarBaseAlpha - ring.RingIndex * IsobarAlphaStep);
            Vec4f color = new(baseColor.R, baseColor.G, baseColor.B, alpha);
            shader.Uniform("rgbaIn", color);

            for (int j = 0; j < pts.Length - 1; j++)
            {
                ToScreen(b, vb, pts[j].X, pts[j].Y, out float sx0, out float sy0); // still valid off-screen coords
                ToScreen(b, vb, pts[j + 1].X, pts[j + 1].Y, out float sx1, out float sy1);

                float fx0 = (float)((sx0 - b.renderX) / b.InnerWidth), fy0 = (float)((sy0 - b.renderY) / b.InnerHeight);
                float fx1 = (float)((sx1 - b.renderX) / b.InnerWidth), fy1 = (float)((sy1 - b.renderY) / b.InnerHeight);
                bool onscreen = (fx0 >= -0.05f && fx0 <= 1.05f && fy0 >= -0.05f && fy0 <= 1.05f)
                    || (fx1 >= -0.05f && fx1 <= 1.05f && fy1 >= -0.05f && fy1 <= 1.05f);
                if (!onscreen) continue;

                double dxp = sx1 - sx0, dyp = sy1 - sy0;
                double segLenPx = Math.Sqrt(dxp * dxp + dyp * dyp);
                if (segLenPx < 1e-6) continue;

                float msx = (float)((sx0 + sx1) * 0.5), msy = (float)((sy0 + sy1) * 0.5);
                float tanAngle = (float)Math.Atan2(dyp, dxp);
                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(msx, msy, 60.5f)
                    .RotateZ(tanAngle)
                    .Scale((float)(segLenPx * 0.5), IsobarLineWidthPx * 0.5f, 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quad);
            }
        }

        // H / L letters, drawn BEHIND the front lines/decorations below. With cell radii now large enough
        // to routinely push a system's true centre off the visible map, an off-screen marker falls back to
        // the system's OWN elliptical edge (toward the viewport) — the same boundary a front's own
        // leading-edge arc traces (FrontComputer.BuildLeadingEdgeArc) — rather than an arbitrary on-screen
        // position: the icon must never read as further "ahead" (advected past the player) than the
        // system's actual influence has reached. If even that edge is off-screen, the marker isn't drawn
        // at all. Drawing markers first, under the front geometry, also keeps this fallback placement from
        // ever covering the very front pips/strokes it's meant to be labelling.
        shader.Uniform("rgbaIn", White);
        float mHalf = MarkerPx * 0.5f;
        for (int mi = 0; mi < markers.Count; mi++)
        {
            FrontMarker m = markers[mi];
            bool onscreen = ToScreen(b, vb, m.X, m.Z, out float sx, out float sy);
            if (!onscreen)
            {
                double vcx = (vb.X1 + vb.X2) * 0.5, vcz = (vb.Z1 + vb.Z2) * 0.5;
                double dx = vcx - m.X, dz = vcz - m.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist < 1e-6) continue;
                double edgeR = EllipseGeometry.RadiusToward(m.Radius, m.RadiusMinor, m.Orientation, dx, dz);
                double ex = m.X + dx / dist * edgeR, ez = m.Z + dz / dist * edgeR;
                if (!ToScreen(b, vb, ex, ez, out sx, out sy)) continue; // system's own edge is still off-screen
            }
            shader.BindTexture2D("tex2d", m.IsHigh ? texHigh : texLow, 0);
            mvMat.Set(capi.Render.CurrentModelviewMatrix)
                .Translate(sx, sy, 60f)
                .Scale(mHalf, mHalf, 0f);
            shader.UniformMatrix("modelViewMatrix", mvMat.Values);
            capi.Render.RenderMesh(quad);
        }
        shader.BindTexture2D("tex2d", texWhite, 0); // restore for the stroke/decoration passes below

        // Fronts: every front (Warm/Cold from a moving low's poleward/equatorward sides, Stationary
        // toward its nearest neighbour, Occluded/ColdOcclusion/WarmOcclusion toward whatever its cold
        // front caught) is an open curve marched from the low — see FrontComputer. Geometry comes from
        // FrontComputer (deterministic, cells-only); this layer only decides how to draw it.
        for (int li = 0; li < lines.Count; li++)
        {
            FrontLine f = lines[li];
            if (f.Points.Length == 0) continue;

            double curveLen = FrontComputer.CurveLength(f);
            if (curveLen < 1e-3) continue;
            double lenPx = curveLen * scaleX;

            // Walk the curve finely (world space -> screen space) so the on-screen polyline stays smooth
            // at any zoom level and decorations (Pass B) can be placed at even ACTUAL on-screen arc-length
            // spacing regardless of how the underlying (now purely elliptical, no added wobble — see
            // FrontComputer.BuildLeadingEdgeArc) control points happen to be distributed. The stroke pass
            // below draws directly between these fine samples, so it always tracks the true curve exactly.
            int nFine = (int)Math.Clamp(Math.Ceiling(lenPx / (TilePx * 0.5)), 32, 3000);

            while (scratch.Count <= li) scratch.Add(new LineScratch());
            LineScratch sc = scratch[li];
            sc.EnsureCapacity(nFine + 1);
            float[] sxs = sc.Sxs, sys = sc.Sys, fas = sc.Fas;
            double[] cumPx = sc.CumulativePx;
            for (int j = 0; j <= nFine; j++)
            {
                double t = (double)j / nFine;
                FrontComputer.SampleAt(f, t, out double wx, out double wz, out double fa);
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

            // Pass A: continuous stroke. One thin quad bridges each true fine segment exactly, so the
            // line always follows the curve regardless of how sharply it bends.
            for (int j = 0; j < nFine; j++)
            {
                double dxp = sxs[j + 1] - sxs[j], dyp = sys[j + 1] - sys[j];
                double segLenPx = Math.Sqrt(dxp * dxp + dyp * dyp);
                if (segLenPx < 1e-6) continue;

                double fx0 = (sxs[j] - b.renderX) / b.InnerWidth, fy0 = (sys[j] - b.renderY) / b.InnerHeight;
                double fx1 = (sxs[j + 1] - b.renderX) / b.InnerWidth, fy1 = (sys[j + 1] - b.renderY) / b.InnerHeight;
                bool onscreen = (fx0 >= -0.05 && fx0 <= 1.05 && fy0 >= -0.05 && fy0 <= 1.05)
                    || (fx1 >= -0.05 && fx1 <= 1.05 && fy1 >= -0.05 && fy1 <= 1.05);
                if (!onscreen) continue;

                double midDist = (cumPx[j] + cumPx[j + 1]) * 0.5;
                int tileIdx = (int)Math.Clamp(midDist / step, 0, nT - 1);
                GetFrontStyle(f.Type, tileIdx, out Vec4f segColor, out _);

                float msx = (float)((sxs[j] + sxs[j + 1]) * 0.5);
                float msy = (float)((sys[j] + sys[j + 1]) * 0.5);
                float tanAngle = (float)Math.Atan2(dyp, dxp);

                shader.Uniform("rgbaIn", segColor);
                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(msx, msy, 61.5f)
                    .RotateZ(tanAngle)
                    .Scale((float)(segLenPx * 0.5), LineWidthPx * 0.5f, 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quad);
            }

            // Pass B: periodic decoration (triangle/hemicircle pips) evenly spaced by ACTUAL on-screen
            // arc length. Always drawn exactly perpendicular to the on-screen line AT the symbol's own
            // midpoint — the angle comes straight from that segment's screen-space tangent (same source
            // Pass A's stroke uses), not interpolated between the two bracketing fine samples' facing
            // angles, which could drift off 90° wherever the curve bends between them. The tangent's two
            // possible perpendiculars are disambiguated by whichever is closer to the (interpolated)
            // FrontComputer.ChooseFacing direction, preserving the correct "outward"/travel-direction
            // sense — for a full loop that's outward; for a Warm/Cold open curve it's whichever
            // tangent-perpendicular best matches the low's own direction of travel.
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

                float screenTangentAngle = (float)Math.Atan2(sys[seg + 1] - sys[seg], sxs[seg + 1] - sxs[seg]);
                float perp1 = screenTangentAngle + GameMath.PIHALF;
                float perp2 = screenTangentAngle - GameMath.PIHALF;
                float facingRef = AngleMath.AngleLerp(fas[seg], fas[seg + 1], (float)segT);
                float angle = Math.Abs(GameMath.AngleRadDistance(perp1, facingRef)) <= Math.Abs(GameMath.AngleRadDistance(perp2, facingRef))
                    ? perp1 : perp2;

                GetFrontStyle(f.Type, i, out Vec4f symColor, out bool useSemicircle);
                shader.Uniform("rgbaIn", symColor);
                // The triangle pokes out further than it's wide (anisotropic scale reads fine for a
                // pointed pip); the hemicircle must stay round, so it gets a uniform radius instead —
                // scaling it anisotropically stretched it into an oblong lens along the travel direction.
                float symScaleX = useSemicircle ? SymbolWidthPx * 0.5f : SymbolLengthPx;
                float symScaleY = SymbolWidthPx * 0.5f;
                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(sx, sy, 62f)
                    .RotateZ(angle)
                    .Scale(symScaleX, symScaleY, 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(useSemicircle ? semiMesh : triMesh);
            }
        }
    }

    protected override void Resample(GuiElementMap mapElem)
    {
        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null || capi == null) return;
        FrontComputer.Compute(field, capi.World.Calendar.TotalHours, markers, lines, isobars);
    }

    public override void Dispose()
    {
        quad?.Dispose();
        quad = null;
        triMesh?.Dispose();
        triMesh = null;
        semiMesh?.Dispose();
        semiMesh = null;
    }
}
