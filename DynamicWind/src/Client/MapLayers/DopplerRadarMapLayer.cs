using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// "Doppler radar" map overlay. Samples the precipitation noise field
/// (<see cref="WeatherSystemBase.GetPrecipitation(double,double,double,double)"/>) on a grid anchored to
/// WORLD coordinates (a fixed block step, not a fixed screen grid like the wind arrows), within a radar
/// range around the player — so rain cells sit on their true world location and scale with map zoom.
/// The vanilla precipitation noise provides the organic TEXTURE, but the PRESSURE DIFFERENTIAL drives it:
/// the noise is gated by the local pressure from the synced wind field, so storms appear inside lows and
/// the sky clears under highs (the radar is client-side, so it reads the client pressure field directly —
/// the server-side rainfall coupling never reaches a client-side GetPrecipitation query).
/// Colours ramp green → yellow → orange → red with a CONCAVE-UP (power) response: light rain and the
/// outer fringe of a storm stay green, and only the high-precip storm core ramps through yellow/orange to
/// red — so the overlay reads as "mostly green, red only at the epicentre" instead of painting the whole
/// rain field hot. (An earlier logarithmic curve did the opposite — it lifted faint rain up toward red,
/// turning entire cloud areas red — which is why this is a power curve, not a log.) Renders on world map +
/// minimap; toggle in the map legend.
/// </summary>
public class DopplerRadarMapLayer : MapLayer
{
    private const double WorldStep = 48.0;      // base world size of a radar cell, blocks
    private const double RadarRange = 1200.0;   // blocks around the player to scan
    private const int MaxCells = 1600;
    private const double RecomputeInterval = 0.4;
    private const float MinPrecip = 0.04f;
    private const double RampFullPrecip = 0.9;  // precip level mapped to the red (storm-core) end
    private const double RampExponent = 2.0;    // >1 keeps light/edge rain green; only intense precip → red

    private static readonly Vec4f Green = Rgb(0x3D, 0xFF, 0x87);
    private static readonly Vec4f Yellow = Rgb(0xFF, 0xFB, 0x11);
    private static readonly Vec4f Orange = Rgb(0xFF, 0x90, 0x28);
    private static readonly Vec4f Red = Rgb(0xFF, 0x3F, 0x26);

    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private int whiteTexId;
    private WeatherSystemBase? weather;
    private readonly Matrixf mvMat = new();

    private struct Cell { public double X, Z; public Vec4f Color; }
    private readonly List<Cell> cells = new();
    private double cellStep = WorldStep;
    private double accum;
    private bool haveCache;

    public DopplerRadarMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => "Doppler radar";
    public override string LayerGroupCode => "dynamicwindradar";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        capi = (ICoreClientAPI)api;
        quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        whiteTexId = capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/doppler-cell.png"));
        weather = capi.ModLoader.GetModSystem<WeatherSystemClient>();
        Active = false;
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null || weather == null) return;

        accum += dt;
        if (!haveCache || accum >= RecomputeInterval)
        {
            accum = 0;
            Resample(mapElem);
            haveCache = true;
        }
        if (cells.Count == 0) return;

        ElementBounds b = mapElem.Bounds;
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double spanX = vb.X2 - vb.X1, spanZ = vb.Z2 - vb.Z1;
        if (spanX < 1e-6 || spanZ < 1e-6) return;
        double scaleX = b.InnerWidth / spanX, scaleZ = b.InnerHeight / spanZ;
        float halfW = (float)(cellStep * 0.5 * scaleX);
        float halfH = (float)(cellStep * 0.5 * scaleZ);

        IShaderProgram shader = capi.Render.GetEngineShader((EnumShaderProgram)17);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("applyColor", 0);
        shader.Uniform("noTexture", 0f);
        shader.BindTexture2D("tex2d", whiteTexId, 0);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

        for (int i = 0; i < cells.Count; i++)
        {
            Cell c = cells[i];
            if (!ToScreen(b, vb, c.X, c.Z, out float sx, out float sy)) continue;
            shader.Uniform("rgbaIn", c.Color);
            mvMat.Set(capi.Render.CurrentModelviewMatrix)
                .Translate(sx, sy, 58f)
                .Scale(halfW, halfH, 0f);
            shader.UniformMatrix("modelViewMatrix", mvMat.Values);
            capi.Render.RenderMesh(quad);
        }
    }

    private void Resample(GuiElementMap mapElem)
    {
        cells.Clear();
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double playerX = capi!.World.Player?.Entity?.Pos.X ?? 0;
        double playerZ = capi.World.Player?.Entity?.Pos.Z ?? 0;
        double playerY = capi.World.Player?.Entity?.Pos.Y ?? capi.World.SeaLevel;

        // Only scan where data exists: the view ∩ a radar range around the player.
        double minX = Math.Max(vb.X1, playerX - RadarRange), maxX = Math.Min(vb.X2, playerX + RadarRange);
        double minZ = Math.Max(vb.Z1, playerZ - RadarRange), maxZ = Math.Min(vb.Z2, playerZ + RadarRange);
        if (maxX <= minX || maxZ <= minZ) return;

        // Fixed world step, coarsened only if the area would exceed the cell budget (far zoom-out).
        double step = WorldStep;
        while ((maxX - minX) / step * ((maxZ - minZ) / step) > MaxCells) step *= 2;
        cellStep = step;

        double startX = Math.Floor(minX / step) * step;
        double startZ = Math.Floor(minZ / step) * step;
        double th = capi.World.Calendar.TotalDays;

        // Precipitation is gated by the pressure field at its source — the WeatherCoupling patch on
        // GetRainCloudness — so GetPrecipitation already reflects the moving lows/highs. We sample it
        // straight, with no extra drift or gate: that guarantees the radar matches the clouds and the
        // actual rain (which read the same gated value), and the pattern still moves because the gating
        // pressure cells advect with the wind.
        for (double wx = startX; wx < maxX; wx += step)
        {
            double cx = wx + step * 0.5;
            for (double wz = startZ; wz < maxZ; wz += step)
            {
                double cz = wz + step * 0.5;
                float precip = weather!.GetPrecipitation(cx, playerY, cz, th);
                if (precip < MinPrecip) continue;

                // Concave-up response: normalise to the storm-core precip level, then raise to a power >1
                // so light rain and storm fringes stay near 0 (green) and only high precip climbs toward 1
                // (red). t = (p / full)^k, clamped.
                double norm = precip / RampFullPrecip;
                if (norm > 1) norm = 1;
                double t = Math.Pow(norm, RampExponent);
                cells.Add(new Cell { X = cx, Z = cz, Color = RampColor((float)t, (float)(0.3 + 0.5 * t)) });
            }
        }
    }

    private static bool ToScreen(ElementBounds b, Cuboidd vb, double wx, double wz, out float sx, out float sy)
    {
        double fx = (wx - vb.X1) / (vb.X2 - vb.X1);
        double fz = (wz - vb.Z1) / (vb.Z2 - vb.Z1);
        sx = (float)(b.renderX + fx * b.InnerWidth);
        sy = (float)(b.renderY + fz * b.InnerHeight);
        return fx >= -0.05 && fx <= 1.05 && fz >= -0.05 && fz <= 1.05;
    }

    private static Vec4f RampColor(float t, float alpha)
    {
        Vec4f c;
        if (t < 1f / 3f) c = Lerp(Green, Yellow, t * 3f);
        else if (t < 2f / 3f) c = Lerp(Yellow, Orange, (t - 1f / 3f) * 3f);
        else c = Lerp(Orange, Red, (t - 2f / 3f) * 3f);
        return new Vec4f(c.R, c.G, c.B, alpha);
    }

    private static Vec4f Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
    private static Vec4f Lerp(Vec4f a, Vec4f b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    public override void Dispose()
    {
        quad?.Dispose();
        quad = null;
    }
}
