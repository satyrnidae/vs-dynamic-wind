using System;
using System.Collections.Generic;
using DynamicWind.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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
/// Colours ramp through several greens (light → heavy rain) before reaching yellow/orange/red, which are
/// reserved for storm-core intensity — matching real doppler radar convention — via a CONCAVE-UP (power)
/// response: light rain and the outer fringe of a storm stay in the greens, and only the high-precip storm
/// core ramps through yellow/orange to red — so the overlay reads as "mostly green, red only at the
/// epicentre" instead of painting the whole rain field hot. (An earlier logarithmic curve did the
/// opposite — it lifted faint rain up toward red, turning entire cloud areas red — which is why this is a
/// power curve, not a log.) Cells cold enough for vanilla to render snow instead of rain (client climate
/// temperature below <see cref="SnowThresholdTemp"/>, matching vanilla's own default snow/rain split) use
/// a separate blue → purple ramp instead. Renders on world map + minimap; toggle in the map legend.
/// </summary>
public class DopplerRadarMapLayer : DynamicWindWorldMapLayerBase
{
    private const double WorldStep = 48.0;      // base world size of a radar cell, blocks
    private const double RadarRange = 1200.0;   // blocks around the player to scan
    private const int MaxCells = 1600;
    private const float MinPrecip = 0.04f;
    private const double RampFullPrecip = 0.9;  // precip level mapped to the red (storm-core) end
    private const double RampExponent = 2.0;    // >1 keeps light/edge rain green; only intense precip → red
    private const float SnowThresholdTemp = 4f; // °C; matches vanilla WeatherDataSnapshot.snowThresholdTemp default

    private static readonly Vec4f LightGreen = Rgb(0x86, 0xF4, 0x68);
    private static readonly Vec4f MedGreen = Rgb(0x3C, 0x93, 0x25);
    private static readonly Vec4f DarkGreen = Rgb(0x1C, 0x5D, 0x0A);
    private static readonly Vec4f DarkestGreen = Rgb(0x0E, 0x41, 0x00);
    private static readonly Vec4f Yellow = Rgb(0xFF, 0xFB, 0x11);
    private static readonly Vec4f Orange = Rgb(0xFF, 0x90, 0x28);
    private static readonly Vec4f Red = Rgb(0xFF, 0x3F, 0x26);

    private static readonly Vec4f LightSnow = Rgb(0x5F, 0xBE, 0xF5);
    private static readonly Vec4f MedSnow = Rgb(0x34, 0x48, 0xCF);
    private static readonly Vec4f DarkSnow = Rgb(0x42, 0x20, 0xA4);

    private static readonly Vec4f[] RainStops = { LightGreen, MedGreen, DarkGreen, DarkestGreen, Yellow, Orange, Red };
    private static readonly Vec4f[] SnowStops = { LightSnow, MedSnow, DarkSnow };

    private DynamicWindConfig? cfg;
    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private int whiteTexId;
    private WeatherSystemBase? weather;
    private readonly Matrixf mvMat = new();

    private struct Cell { public double X, Z; public Vec4f Color; }
    private readonly List<Cell> cells = new();
    private double cellStep = WorldStep;

    public DopplerRadarMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => Lang.Get("game:maplayer-dynamicwindradar");
    public override string LayerGroupCode => "dynamicwindradar";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        cfg = DynamicWindRuntime.ClientConfig;
        capi = (ICoreClientAPI)api;
        quad = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        whiteTexId = capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/doppler-cell.png"));
        weather = capi.ModLoader.GetModSystem<WeatherSystemClient>();
        Active = cfg?.DopplerRadarMapLayerVisibleByDefault ?? false;
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null || weather == null) return;

        TickResample(dt, mapElem);
        if (cells.Count == 0) return;

        ElementBounds b = mapElem.Bounds;
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double spanX = vb.X2 - vb.X1, spanZ = vb.Z2 - vb.Z1;
        if (spanX < 1e-6 || spanZ < 1e-6) return;
        double scaleX = b.InnerWidth / spanX, scaleZ = b.InnerHeight / spanZ;
        float halfW = (float)(cellStep * 0.5 * scaleX);
        float halfH = (float)(cellStep * 0.5 * scaleZ);

        IShaderProgram shader = BindStandardShader(capi, whiteTexId);

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

    protected override void Resample(GuiElementMap mapElem)
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

                var pos = new BlockPos((int)cx, (int)playerY, (int)cz, 0);
                ClimateCondition? cc = capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, th);
                bool isSnow = cc != null && cc.Temperature < SnowThresholdTemp;
                Vec4f color = RampColor(isSnow ? SnowStops : RainStops, (float)t, (float)(0.3 + 0.5 * t));
                cells.Add(new Cell { X = cx, Z = cz, Color = color });
            }
        }
    }

    private static Vec4f RampColor(Vec4f[] stops, float t, float alpha)
    {
        float scaled = t * (stops.Length - 1);
        int i = Math.Min((int)scaled, stops.Length - 2);
        Vec4f c = Lerp(stops[i], stops[i + 1], scaled - i);
        return new Vec4f(c.R, c.G, c.B, alpha);
    }

    private static Vec4f Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    public override void Dispose()
    {
        quad?.Dispose();
        quad = null;
    }
}
