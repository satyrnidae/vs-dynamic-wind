using System;
using System.Collections.Generic;
using System.Reflection;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// Synoptic weather-front overlay (world map only, not the minimap). Draws the classic surface-analysis
/// symbols derived from the synced pressure field by <see cref="FrontComputer"/>: an "H"/"L" letter at
/// each pressure-system centre, and a tiled contour of warm/cold/stationary front symbols traced
/// around each system — the full loop for a near-stationary system, or just the leading half (the
/// system's active edge) for a moving one, each tile individually rotated to the local contour
/// tangent so the line reads as contiguous even where it curves. Toggle via the map legend.
/// </summary>
public class FrontMapLayer : MapLayer
{
    private const double RecomputeInterval = 0.5;
    private const float TilePx = 14f;     // on-screen length of one front segment tile
    private const float MarkerPx = 26f;   // on-screen size of an H/L letter
    private const double CurveAmpFactor = 0.06; // × front length, curvature amplitude

    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private int texWarm, texCold, texStationary, texHigh, texLow;
    private readonly Matrixf mvMat = new();
    private static readonly FieldInfo? fSnapToPlayer = AccessTools.Field(typeof(GuiElementMap), "snapToPlayer");
    private static readonly Vec4f White = new(1f, 1f, 1f, 1f);

    private readonly List<FrontMarker> markers = new();
    private readonly List<FrontLine> lines = new();
    private double accum;
    private bool haveCache;

    public FrontMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => "Weather fronts";
    public override string LayerGroupCode => "dynamicwindfronts";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        capi = (ICoreClientAPI)api;
        quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        texWarm = Tex("front-warm");
        texCold = Tex("front-cold");
        texStationary = Tex("front-stationary");
        texHigh = Tex("pressure-system-high");
        texLow = Tex("pressure-system-low");
        Active = false; // off by default; toggle in the map legend
    }

    private int Tex(string name) => capi!.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/" + name + ".png"));

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null) return;
        if (fSnapToPlayer?.GetValue(mapElem) is bool snap && snap) return; // world map only

        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null) return;

        accum += dt;
        if (!haveCache || accum >= RecomputeInterval)
        {
            accum = 0;
            FrontComputer.Compute(field, capi.World.Calendar.TotalHours, markers, lines);
            haveCache = true;
        }

        ElementBounds b = mapElem.Bounds;
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double spanX = vb.X2 - vb.X1, spanZ = vb.Z2 - vb.Z1;
        if (spanX < 1e-6 || spanZ < 1e-6) return;
        double scaleX = b.InnerWidth / spanX;  // px per block
        double scaleZ = b.InnerHeight / spanZ;
        double th = capi.World.Calendar.TotalHours;

        IShaderProgram shader = capi.Render.GetEngineShader((EnumShaderProgram)17);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("applyColor", 0);
        shader.Uniform("noTexture", 0f);
        shader.Uniform("rgbaIn", White);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

        // Fronts: a near-STATIONARY system encircles itself with a full closed loop of stationary
        // symbols (facing outward); a MOVING system instead renders only the leading half of that
        // same contour — its active edge, where precipitation forms — as a tiled arc of cold (high) /
        // warm (low) symbols following the local tangent. Geometry comes from FrontComputer
        // (deterministic, cells-only); both cases share the same march-to-contour Radii.
        for (int li = 0; li < lines.Count; li++)
        {
            FrontLine f = lines[li];
            if (f.Radii == null || f.Radii.Length == 0) continue;
            double[] radii = f.Radii;
            bool stationary = f.Type == FrontType.Stationary;

            double rMean = 0;
            for (int k = 0; k < radii.Length; k++) rMean += radii[k];
            rMean /= radii.Length;
            double rPx = rMean * scaleX;
            double curveAmpL = CurveAmpFactor * rMean;

            double thetaStart, thetaSpan;
            if (stationary)
            {
                thetaStart = 0;
                thetaSpan = 2 * Math.PI;
            }
            else
            {
                thetaStart = f.LeadAngle - Math.PI / 2.0;
                thetaSpan = Math.PI; // leading half only
            }

            // Walk the contour finely (world space -> screen space) and tile it by ACTUAL on-screen
            // arc length, not by uniform angle steps. A uniform-theta split under/over-spaces tiles
            // wherever the traced contour bulges away from the mean radius (elongated systems, the
            // Curve() wobble) — real arc length is what has to stay tiled with no gaps, regardless of
            // zoom or how stretched the front is.
            double roughLenPx = rPx * thetaSpan; // cheap estimate, just to size the fine sampling
            int nFine = (int)Math.Clamp(Math.Ceiling(roughLenPx / (TilePx * 0.5)), 32, 3000);

            float[] sxs = new float[nFine + 1];
            float[] sys = new float[nFine + 1];
            double[] cum = new double[nFine + 1];
            for (int j = 0; j <= nFine; j++)
            {
                double theta = thetaStart + (double)j / nFine * thetaSpan;
                ArcPoint(f, radii, curveAmpL, theta, (theta - thetaStart) * rMean, th, out double wx, out double wz);
                ToScreen(b, vb, wx, wz, out sxs[j], out sys[j]); // ignore visibility flag; still valid off-screen coords
                if (j > 0)
                {
                    double dx = sxs[j] - sxs[j - 1], dy = sys[j] - sys[j - 1];
                    cum[j] = cum[j - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
            }
            double totalLenPx = cum[nFine];
            if (totalLenPx < 1e-3) continue;

            int minTiles = stationary ? 8 : 4;
            int nT = (int)Math.Clamp(Math.Ceiling(totalLenPx / TilePx), minTiles, 3000);
            double step = totalLenPx / nT;

            int tex = stationary ? texStationary : f.Type == FrontType.Warm ? texWarm : texCold;
            shader.BindTexture2D("tex2d", tex, 0);

            float halfH = TilePx * 0.5f;
            float halfW = TilePx * 0.25f;
            int seg = 0;
            for (int i = 0; i < nT; i++)
            {
                double targetDist = (i + 0.5) * step;
                while (seg < nFine - 1 && cum[seg + 1] < targetDist) seg++;
                double segLen = cum[seg + 1] - cum[seg];
                double t = segLen > 1e-9 ? (targetDist - cum[seg]) / segLen : 0;
                float sx = (float)(sxs[seg] + (sxs[seg + 1] - sxs[seg]) * t);
                float sy = (float)(sys[seg] + (sys[seg + 1] - sys[seg]) * t);

                // Cull tiles well outside the visible map element (same tolerance as ToScreen used).
                float fx = (float)((sx - b.renderX) / b.InnerWidth);
                float fy = (float)((sy - b.renderY) / b.InnerHeight);
                if (fx < -0.05f || fx > 1.05f || fy < -0.05f || fy > 1.05f) continue;

                float dsx = sxs[seg + 1] - sxs[seg];
                float dsy = sys[seg + 1] - sys[seg];

                float angle;
                if (stationary)
                {
                    // Symbols face outward, radially.
                    double theta = thetaStart + (seg + t) / nFine * thetaSpan;
                    angle = (float)Math.Atan2(Math.Sin(theta), Math.Cos(theta));
                }
                else
                {
                    // Symbols follow the contour's local (screen-space) tangent, so the icon
                    // orientation reads as a contiguous line even where it curves.
                    angle = (float)Math.Atan2(-dsx, dsy);
                }

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

    /// <summary>A point on a front's contour at angle theta (world space), with the slow per-tile
    /// curvature wobble (<see cref="FrontComputer.Curve"/>) added on top of the traced radius.</summary>
    private static void ArcPoint(FrontLine f, double[] radii, double curveAmpL, double theta, double u, double th, out double wx, out double wz)
    {
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        double rr = FrontComputer.RadiusAt(radii, theta) + curveAmpL * FrontComputer.Curve(u, f.Seed, th);
        wx = f.CenterX + rr * ct;
        wz = f.CenterZ + rr * st;
    }

    /// <summary>World (x,z) → screen px within the map element; false if outside the visible bounds.</summary>
    private static bool ToScreen(ElementBounds b, Cuboidd vb, double wx, double wz, out float sx, out float sy)
    {
        double fx = (wx - vb.X1) / (vb.X2 - vb.X1);
        double fz = (wz - vb.Z1) / (vb.Z2 - vb.Z1);
        sx = (float)(b.renderX + fx * b.InnerWidth);
        sy = (float)(b.renderY + fz * b.InnerHeight);
        return fx >= -0.05 && fx <= 1.05 && fz >= -0.05 && fz <= 1.05;
    }

    public override void Dispose()
    {
        quad?.Dispose();
        quad = null;
    }
}
