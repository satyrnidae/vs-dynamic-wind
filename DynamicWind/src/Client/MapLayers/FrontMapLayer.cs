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
/// each pressure-system centre, and warm/cold/stationary front lines on the boundary between paired
/// systems, tiled along a smoothly-curving, slowly-evolving line. Toggle via the map legend.
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
        texWarm = Tex("warmfront");
        texCold = Tex("coldfront");
        texStationary = Tex("stationaryfront");
        texHigh = Tex("highpressuresystemcenterletter");
        texLow = Tex("lowpressuresystemcenterletter");
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

        // Front loops: an enclosed contour of front-symbol tiles hugging the low-pressure blob. The
        // radius varies with angle (FrontLine.Radii) so the loop follows the blob's true shape; the
        // symbol at each tile is a warm front on the leading edge (in the system's travel direction),
        // a cold front on the trailing edge, and stationary across the flanks. Symbols face OUTWARD
        // (the triangle/semicircle points away from the low centre, the way the front advances).
        for (int li = 0; li < lines.Count; li++)
        {
            FrontLine f = lines[li];
            double[] radii = f.Radii;
            if (radii == null || radii.Length == 0) continue;

            // Mean radius drives the tile count + wobble scale.
            double rMean = 0;
            for (int k = 0; k < radii.Length; k++) rMean += radii[k];
            rMean /= radii.Length;
            double rPx = rMean * scaleX;
            int nT = (int)Math.Max(8, Math.Min(240, Math.Round(2 * Math.PI * rPx / TilePx)));
            double curveAmp = CurveAmpFactor * rMean;

            for (int i = 0; i < nT; i++)
            {
                double frac = (double)i / nT;
                double theta = frac * 2 * Math.PI;
                double ct = Math.Cos(theta), st = Math.Sin(theta);
                double u = frac * rMean * 2 * Math.PI;          // arc length → organic radius wobble
                double rr = FrontComputer.RadiusAt(radii, theta) + curveAmp * FrontComputer.Curve(u, f.Seed, th);
                double wx = f.CenterX + rr * ct;
                double wz = f.CenterZ + rr * st;
                if (!ToScreen(b, vb, wx, wz, out float sx, out float sy)) continue;

                // Leading edge (outward radial aligned with travel) = warm front; trailing = cold front.
                double facing = ct * f.MoveX + st * f.MoveZ;
                int tex = facing > 0.35 ? texWarm : facing < -0.35 ? texCold : texStationary;

                // Icon "up" (height) runs tangent to the loop; the bump (icon width, +X) points outward.
                float angle = (float)Math.Atan2(st, ct);
                float halfH = TilePx * 0.5f;     // along the loop (icon height 32)
                float halfW = TilePx * 0.25f;    // across (icon width 16) — points radially outward
                shader.BindTexture2D("tex2d", tex, 0);
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
