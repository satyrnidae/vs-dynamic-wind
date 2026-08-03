using System;
using System.Collections.Generic;
using Cairo;
using DynamicWind.Config;
using DynamicWind.WindSystem;
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
/// power curve, not a log.) Cells cold enough to probably be snow instead of rain use a separate
/// blue → purple ramp instead — decided by <see cref="EstimateIsSnow"/>, a standalone climate estimate
/// (season + hemisphere + latitude) rather than sampling the real block climate at all: <c>GetClimateAt</c>
/// needs the chunk loaded to give a meaningful (not garbage/default) answer, which the radar's scan range
/// routinely exceeds on a zoomed-out world map, so it's not a usable data source for this at any distance.
/// WORLD MAP ONLY (skips the minimap HUD, like the legend below).
/// A static key (<see cref="BuildLegendTexture"/>, baked once as a Cairo texture) is pinned to the
/// bottom-right corner of the map, drawn above every other overlay this mod adds to the world map so nothing
/// (e.g. the wind arrows) can render over it; toggle the whole layer in the map legend.
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

    // Standalone rain-vs-snow climate estimate (see EstimateIsSnow): a rough real-world temperature curve
    // built purely from season + hemisphere + latitude, with no block/chunk data at all, so it works
    // identically at any distance instead of needing the chunk loaded. Not meant to reproduce vanilla's
    // actual worldgen climate exactly — just "would this probably be cold enough for snow right now".
    private const double SnowEquatorTemp = 27.0;    // °C — approx. average equatorial temperature, all year
    private const double SnowPoleTempSwing = 55.0;  // °C — how much colder the poles average vs. the equator
    private const double SnowSeasonalSwing = 18.0;  // °C — peak seasonal swing at full poleward-ness; tapers to ~0 at the equator
    private const double SnowSummerPeakRel = 0.59725; // matches TerrainThermal/WindFieldSimulation's own SummerPeakRel

    private static readonly Vec4f LightGreen = Rgb(0x86, 0xF4, 0x68);
    private static readonly Vec4f MedGreen = Rgb(0x3C, 0x93, 0x25);
    private static readonly Vec4f DarkGreen = Rgb(0x1C, 0x5D, 0x0A);
    private static readonly Vec4f DarkestGreen = Rgb(0x0E, 0x41, 0x00);
    private static readonly Vec4f Yellow = Rgb(0xFF, 0xFB, 0x11);
    private static readonly Vec4f Orange = Rgb(0xFF, 0x90, 0x28);
    private static readonly Vec4f Red = Rgb(0xFF, 0x3F, 0x26);

    private static readonly Vec4f SkyBlue = Rgb(0x5F, 0xBE, 0xF5);
    private static readonly Vec4f Cerulean = Rgb(0x34, 0x48, 0xCF);
    private static readonly Vec4f LightPurple = Rgb(0x42, 0x20, 0xA4);

    private static readonly Vec4f[] RainStops = { LightGreen, MedGreen, DarkGreen, DarkestGreen, Yellow, Orange, Red };
    private static readonly Vec4f[] SnowStops = { SkyBlue, Cerulean, LightPurple };

    // Legend panel, baked once as a Cairo texture (PlayerMapLayer-style) and re-drawn as a fixed
    // screen-space quad anchored to the bottom-right corner of the map element — a static reference for
    // the RainStops/SnowStops ramps above, so it never needs rebuilding per-frame like the radar cells.
    // No title row (it overlapped the "Rain" label) — height is derived from the label font's real
    // ascent/descent (see BuildLegendTexture) so the gap above the "Rain" label matches the gap below
    // the Light/Heavy snow labels exactly, rather than eyeballed constants.
    private const int LegendWidth = 190;
    private const int LegendMargin = 10;   // px gap from the map element's corner
    private const int LegendPad = 12;      // px inset of the bar/text from the panel edge, top/bottom/sides
    private const int LegendBarWidth = 166;
    private const int LegendBarHeight = 14;
    private const double LegendLabelToBarGap = 6;  // row label baseline -> bar top
    private const double LegendBarToTextGap = 12;  // bar bottom -> endpoint-label baseline
    private const double LegendRowGap = 14;        // bottom of one row's endpoint labels -> next row label's top
    private static readonly Vec4f White = new(1f, 1f, 1f, 1f);

    private DynamicWindConfig? cfg;
    private ICoreClientAPI? capi;
    private MeshRef? quad;
    private int whiteTexId;
    private WeatherSystemBase? weather;
    private readonly Matrixf mvMat = new();
    private LoadedTexture? legendTexture;

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
        RestoreActiveState(cfg?.DopplerRadarMapLayerVisibleByDefault ?? false);
        BuildLegendTexture();
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quad == null || weather == null) return;
        if (IsMinimap(mapElem)) return; // world map only

        TickResample(dt, mapElem);

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

        // Legend: anchored to the bottom-right corner of the map element regardless of pan/zoom (a fixed
        // screen-space overlay, not a world position). Z=65 keeps it drawn above every other overlay this
        // mod puts on the world map (wind arrows at 60, front lines/symbols at 61.5/62) so it's never
        // covered by them.
        if (legendTexture != null)
        {
            shader.Uniform("rgbaIn", White);
            shader.BindTexture2D("tex2d", legendTexture.TextureId, 0);
            float lHalfW = legendTexture.Width * 0.5f;
            float lHalfH = legendTexture.Height * 0.5f;
            float lcx = (float)(b.renderX + b.InnerWidth) - LegendMargin - lHalfW;
            float lcy = (float)(b.renderY + b.InnerHeight) - LegendMargin - lHalfH;
            mvMat.Set(capi.Render.CurrentModelviewMatrix)
                .Translate(lcx, lcy, 65f)
                .Scale(lHalfW, lHalfH, 0f);
            shader.UniformMatrix("modelViewMatrix", mvMat.Values);
            capi.Render.RenderMesh(quad);
        }
    }

    /// <summary>Bakes the legend panel (rain ramp, snow ramp, row/endpoint labels) once into a Cairo
    /// texture — PlayerMapLayer-style — since it's static reference art, not per-frame data. Panel height
    /// is derived from the label font's actual ascent/descent so the gap from the top edge to the top of
    /// the "Rain" label equals the gap from the bottom edge to the bottom of the Light/Heavy snow labels
    /// (both exactly <see cref="LegendPad"/>), rather than tuned by eye.</summary>
    private void BuildLegendTexture()
    {
        if (capi == null) return;

        CairoFont labelFont = CairoFont.WhiteSmallText().WithFontSize(10);
        FontExtents fe = labelFont.GetFontExtents();
        double ascent = fe.Ascent, descent = fe.Descent;

        double rowLabelBaseline1 = LegendPad + ascent;
        double bar1Top = rowLabelBaseline1 + LegendLabelToBarGap;
        double bar1Bottom = bar1Top + LegendBarHeight;
        double text1Baseline = bar1Bottom + LegendBarToTextGap;
        double text1Bottom = text1Baseline + descent;

        double rowLabelBaseline2 = text1Bottom + LegendRowGap + ascent;
        double bar2Top = rowLabelBaseline2 + LegendLabelToBarGap;
        double bar2Bottom = bar2Top + LegendBarHeight;
        double text2Baseline = bar2Bottom + LegendBarToTextGap;
        double text2Bottom = text2Baseline + descent;

        int legendHeight = (int)Math.Ceiling(text2Bottom + LegendPad);

        ImageSurface surface = new(Format.Argb32, LegendWidth, legendHeight);
        Context ctx = new(surface);

        ctx.SetSourceRGBA(0, 0, 0, 0.55);
        ctx.Rectangle(0, 0, LegendWidth, legendHeight);
        ctx.Fill();

        DrawLegendBar(ctx, labelFont, rowLabelBaseline1, bar1Top, text1Baseline, RainStops,
            Lang.Get("dynamicwind:maplegend-doppler-rain"),
            Lang.Get("dynamicwind:maplegend-light"), Lang.Get("dynamicwind:maplegend-storm"));

        DrawLegendBar(ctx, labelFont, rowLabelBaseline2, bar2Top, text2Baseline, SnowStops,
            Lang.Get("dynamicwind:maplegend-doppler-snow"),
            Lang.Get("dynamicwind:maplegend-light"), Lang.Get("dynamicwind:maplegend-heavy"));

        legendTexture?.Dispose();
        legendTexture = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), LegendWidth, legendHeight);
        ctx.Dispose();
        surface.Dispose();
    }

    /// <summary>Draws one legend row at explicit, pre-computed baselines: a row label, a smooth gradient
    /// bar (matching the actual in-radar RampColor interpolation between <paramref name="stops"/>), and a
    /// low/high-end text label under each side of the bar.</summary>
    private static void DrawLegendBar(Context ctx, CairoFont labelFont, double rowLabelBaseline, double barTop,
        double textBaseline, Vec4f[] stops, string rowLabel, string lowText, string highText)
    {
        labelFont.SetupContext(ctx);
        ctx.MoveTo(LegendPad, rowLabelBaseline);
        ctx.ShowText(rowLabel);

        double barX = LegendPad;
        using (LinearGradient grad = new(barX, 0, barX + LegendBarWidth, 0))
        {
            for (int i = 0; i < stops.Length; i++)
            {
                Vec4f s = stops[i];
                grad.AddColorStop((double)i / (stops.Length - 1), new Color(s.R, s.G, s.B, 1.0));
            }
            ctx.SetSource(grad);
            ctx.Rectangle(barX, barTop, LegendBarWidth, LegendBarHeight);
            ctx.Fill();
        }

        labelFont.SetupContext(ctx);
        ctx.MoveTo(barX, textBaseline);
        ctx.ShowText(lowText);
        TextExtents highExtents = labelFont.GetTextExtents(highText);
        ctx.MoveTo(barX + LegendBarWidth - highExtents.Width, textBaseline);
        ctx.ShowText(highText);
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

                bool isSnow = EstimateIsSnow(cx, cz);
                Vec4f color = RampColor(isSnow ? SnowStops : RainStops, (float)t, (float)(0.3 + 0.5 * t));
                cells.Add(new Cell { X = cx, Z = cz, Color = color });
            }
        }
    }

    /// <summary>
    /// Standalone "would this probably be cold enough for snow right now" estimate — season + hemisphere
    /// + closeness to a pole/equator, no block/chunk data at all (see the class remarks for why
    /// <c>GetClimateAt</c> isn't usable here). <see cref="WindField.LatitudeFunc"/> gives real climate
    /// latitude (0 = equator, 1 = pole, already |·|-clamped — see its own callers in
    /// <c>WindFieldSimulation.ComputeAirMassTemp</c>); <c>Calendar.GetSeasonRel</c> is hemisphere-aware on
    /// its own (a Southern-hemisphere position already reads its OWN winter as winter), so no separate
    /// hemisphere flip is needed on top of it. The approximate temperature curve: warm year-round at the
    /// equator, colder toward the poles, with a seasonal swing that itself grows toward the poles (real
    /// climate: the equator barely varies by season; high latitudes swing widely) and peaks at
    /// <see cref="SnowSummerPeakRel"/> (matching <c>TerrainThermal</c>/<c>WindFieldSimulation</c>'s own
    /// seasonal-warmth convention) — snow if that estimate falls below <see cref="SnowThresholdTemp"/>.
    /// </summary>
    private bool EstimateIsSnow(double x, double z)
    {
        WindField? field = DynamicWindRuntime.ClientField;
        double poleward = field?.LatitudeFunc != null ? GameMath.Clamp((float)Math.Abs(field.LatitudeFunc(z)), 0f, 1f) : 0.5f;

        var pos = new BlockPos((int)x, 0, (int)z, 0);
        float seasonRel = capi!.World.Calendar.GetSeasonRel(pos); // hemisphere-aware
        double seasonWarmth = Math.Cos(2.0 * Math.PI * (seasonRel - SnowSummerPeakRel)); // +1 midsummer, -1 midwinter

        double approxTemp = SnowEquatorTemp - poleward * SnowPoleTempSwing + seasonWarmth * SnowSeasonalSwing * poleward;
        return approxTemp < SnowThresholdTemp;
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
        legendTexture?.Dispose();
        legendTexture = null;
    }
}
