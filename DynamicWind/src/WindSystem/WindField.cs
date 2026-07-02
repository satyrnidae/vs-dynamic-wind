using System;
using System.Collections.Generic;
using DynamicWind.Network;
using DynamicWind.PressureSystem;
using Vintagestory.API.MathTools;

namespace DynamicWind.WindSystem;

/// <summary>
/// Deterministic wind field shared by server and client. Given the same parameters and cell list
/// (kept in sync via <see cref="WindFieldState"/>), <see cref="WindAt"/> returns identical results
/// on both sides, so clients never need per-block wind streaming.
///
/// Convention: +X = East, +Z = South. The returned vector points in the wind's travel direction;
/// its magnitude is the strength multiplier applied on top of the vanilla scalar wind speed.
/// The base jetstream blows +X (west-to-east) with a slow large-scale meander.
/// </summary>
public class WindField
{
    public double BaseStrength = 1.0;
    /// <summary>Maximum meander of the base jetstream angle, in radians.</summary>
    public double MeanderAmplitude = 0.5;
    /// <summary>Spatial/temporal frequency multiplier for the jetstream meander (higher = the bearing
    /// wiggles over shorter N/S distances and faster in time).</summary>
    public double MeanderFrequency = 1.0;
    /// <summary>Inflow/outflow tilt of cyclonic circulation, in radians (~20°).</summary>
    public double CyclonicInflowAngle = 0.35;
    /// <summary>Multiplier on each pressure cell's induced circulation (tangential + radial flow).
    /// &gt;1 makes the wind swirl around lows/highs more strongly.</summary>
    public double CirculationStrength = 1.0;

    /// <summary>
    /// Seasonal meridional (north/south) bias factor in [−1, 1] applied to the base jetstream:
    /// &gt;0 tilts the westerly toward +Z (south), &lt;0 toward −Z (north). Computed server-side from
    /// the season + hemisphere (poleward in summer, equatorward in winter) and synced to clients —
    /// the value is globally uniform because the hemisphere flip and the half-year season offset cancel.
    /// </summary>
    public double SeasonalBiasZ;
    /// <summary>Maximum N/S tilt of the base jetstream at peak summer/winter, in radians.</summary>
    public double SeasonalBiasMaxAngle;

    /// <summary>Amplitude of the slow jet-streak speed noise. The jetstream baseline speed is multiplied
    /// by 1 ± this, giving faster/slower jet streaks that wander over space and time. 0 = none.</summary>
    public double JetNoiseAmplitude = 0.3;

    /// <summary>
    /// Calendar time (total hours) at which each cell's <see cref="PressureCell.X"/>/<c>Z</c> was the
    /// true position. Effective positions are advected by velocity from this anchor, so a single
    /// snapshot keeps server and client identical between updates.
    /// </summary>
    public double AnchorHours;

    public readonly List<PressureCell> Cells = new();

    /// <summary>
    /// Optional terrain-driven thermal pressure contribution at (x, z, totalHours). Set per side
    /// (server uses world data; client samples its loaded world). + = low (warm land), − = high
    /// (cool water). Added on top of the moving cell pressure in <see cref="PressureAt"/>.
    /// </summary>
    public Func<double, double, double, double>? ThermalPressureFunc;

    /// <summary>
    /// Optional terrain surface height (blocks) at (x, z). Server: Algernons Terrain Sampler; client:
    /// rain-map height for loaded chunks. Drives orographic deflection/slowdown in <see cref="WindAt"/>.
    /// </summary>
    public Func<double, double, double>? TerrainHeightFunc;

    /// <summary>Strength of down-gradient (high→low) pressure flow added to the wind. 0 = off.</summary>
    public double GradientWindScale;

    // Orographic (terrain) interaction.
    public bool OrographicEnabled;
    public double OroSlowdown = 0.5;   // windward speed loss climbing into terrain (0..1)
    public double OroDeflection = 1.0; // how strongly flow curves around obstacles
    public double OroWake = 0.5;       // lee-side (downwind) speed loss in a hill's shadow (0..1)
    public double OroHeightThreshold = 130; // terrain height (blocks) below which orographic effects don't apply

    // Altitude pressure lapse: real air pressure falls with height. Sampled in the Y-aware
    // PressureAt overload only (a vertical-only term, so it adds no horizontal wind gradient).
    public double SeaLevel = 110;
    public double WorldHeight = 256;
    /// <summary>Fractional pressure drop reached at the build ceiling (0.5 = −50%), toward LOW (+).</summary>
    public double AltitudeMaxDrop = 0.5;

    /// <summary>World Z of the map's N/S midline — set per side from MapSizeZ / 2, same on both sides
    /// since it's derived from world geometry, not synced. Vintage Story's REAL climate latitude
    /// (<see cref="LatitudeFunc"/>) is a repeating triangle wave, not a single equator/pole pair at the
    /// map edges, so this is only a crude fallback for <see cref="PolewardZSign"/> when
    /// <see cref="LatitudeFunc"/> hasn't been wired up yet (e.g. very early startup).</summary>
    public double MapCenterZ = 128.0;

    /// <summary>
    /// Optional hook onto the game's real climate latitude at world Z (<c>Calendar.OnGetLatitude</c>):
    /// +1 at a north pole, −1 at a south pole, 0 at an equator. Set per side once the calendar is ready.
    /// Vintage Story's latitude is a repeating triangle wave (period <c>4 × polarEquatorDistance</c>, a
    /// world-config value, phase-shifted by a per-seed offset) — poles and equators alternate repeatedly
    /// across the map, NOT a single equator at the map's centre with poles at its edges. See
    /// <see cref="PolewardZSign"/>.
    /// </summary>
    public Func<double, double>? LatitudeFunc;

    /// <summary>Z step used to numerically differentiate <see cref="LatitudeFunc"/> in
    /// <see cref="PolewardZSign"/> — cheap since the real latitude curve is piecewise-linear, so any
    /// small step off an exact fold point (a pole or equator crossing) gives the correct local slope.</summary>
    private const double LatitudeSampleDist = 8.0;

    /// <summary>
    /// +1 if the LOCAL poleward direction (away from the equator, toward whichever pole is nearer) at
    /// world Z <paramref name="z"/> is +Z, else −1. Found from the sign of <see cref="LatitudeFunc"/>'s
    /// local slope in |latitude| — NOT from a fixed hemisphere, since Vintage Story's real latitude
    /// alternates north/south repeatedly across the map (see <see cref="LatitudeFunc"/>), so which way is
    /// "poleward" flips every <c>2 × polarEquatorDistance</c> blocks rather than once at a map midline.
    /// Falls back to the old map-midline heuristic (via <see cref="MapCenterZ"/>) if
    /// <see cref="LatitudeFunc"/> isn't wired up yet.
    /// </summary>
    public double PolewardZSign(double z)
    {
        if (LatitudeFunc == null) return z < MapCenterZ ? -1.0 : 1.0;
        double m0 = Math.Abs(LatitudeFunc(z));
        double m1 = Math.Abs(LatitudeFunc(z + LatitudeSampleDist));
        return m1 >= m0 ? 1.0 : -1.0;
    }

    /// <summary>
    /// How much of the raw terrain thermal field stays as a static (terrain-pinned) baseline in
    /// <see cref="PressureAt"/>. The remainder is meant to be expressed as mobile, dispersing cells
    /// seeded by the server sim, so the pressure differential moves with the wind. 1 = fully static.
    /// </summary>
    public double ThermalStaticScale = 1.0;

    /// <summary>|velocity| (blocks/hour) below which a low is treated as "stationary" rather than a
    /// moving open-wave system — shared by <see cref="FrontRegimeBiasAt"/>, the storm-bias neighbour
    /// search below, and <see cref="DynamicWind.Client.MapLayers.FrontComputer"/> (which renders the
    /// closed encircling contour for exactly this regime), so all three agree on the same threshold.</summary>
    public const double StationarySpeed = 60.0;

    private const double GradSampleDist = 48.0;
    private const double GradMaxWind = 1.5;
    private const double OroGradDist = 24.0;   // terrain gradient sample spacing
    private const double OroSlopeSens = 3.0;   // slope (blocks/block) → climb factor
    private const double OroWakeProbe = 48.0;  // how far upwind to look for a sheltering rise
    private const double OroWakeMinRise = 8.0; // upwind terrain must be this much higher to count
    private const double OroWakeScale = 40.0;  // rise over this → full wake

    /// <summary>
    /// Elliptical-gaussian normalized squared distance from a cell's centre to a (dx, dz) offset (already
    /// relative to the cell's advected position): 1.0 sits on the 1/e falloff contour. <c>(dx,dz)</c> is
    /// projected onto the cell's along-stream (<see cref="PressureCell.RadiusMinor"/>) and cross-stream
    /// (<see cref="PressureCell.Radius"/>) axes, derived from <see cref="PressureCell.Orientation"/>.
    /// Reduces to the familiar circular <c>dist²/Radius²</c> when <c>RadiusMinor == Radius</c>.
    /// </summary>
    private static double EllipseNorm2(PressureCell cell, double dx, double dz)
    {
        double crossX = Math.Cos(cell.Orientation), crossZ = Math.Sin(cell.Orientation);
        double alongX = -crossZ, alongZ = crossX;
        double along = dx * alongX + dz * alongZ;
        double cross = dx * crossX + dz * crossZ;
        double rMinor = cell.RadiusMinor > 1e-3f ? cell.RadiusMinor : cell.Radius;
        double rMajor = cell.Radius;
        double au = along / rMinor;
        double cu = cross / rMajor;
        return au * au + cu * cu;
    }

    /// <summary>
    /// Outward "radial" unit direction at a (dx, dz) offset from a cell's centre, generalized to the
    /// cell's elliptical footprint: the normalized gradient of <see cref="EllipseNorm2"/>'s squared norm.
    /// Reduces exactly to the circular radial direction <c>(dx,dz)/dist</c> when <c>RadiusMinor == Radius</c>,
    /// so swirl direction follows the ellipse's edge instead of a plain circle once a cell has elongated.
    /// </summary>
    private static void EllipseRadial(PressureCell cell, double dx, double dz, out double nx, out double nz)
    {
        double crossX = Math.Cos(cell.Orientation), crossZ = Math.Sin(cell.Orientation);
        double alongX = -crossZ, alongZ = crossX;
        double along = dx * alongX + dz * alongZ;
        double cross = dx * crossX + dz * crossZ;
        double rMinor = cell.RadiusMinor > 1e-3f ? cell.RadiusMinor : cell.Radius;
        double rMajor = cell.Radius;
        double gx = along / (rMinor * rMinor) * alongX + cross / (rMajor * rMajor) * crossX;
        double gz = along / (rMinor * rMinor) * alongZ + cross / (rMajor * rMajor) * crossZ;
        double glen = Math.Sqrt(gx * gx + gz * gz);
        if (glen < 1e-9) { nx = crossX; nz = crossZ; }
        else { nx = gx / glen; nz = gz / glen; }
    }

    /// <summary>
    /// Slow jet-streak multiplier in [1−<see cref="JetNoiseAmplitude"/>, 1+…]: a multiplied sinusoidal
    /// layout (a slow X wave × a slow Z wave) that evolves slowly over time, giving the jetstream faster
    /// and slower "streaks" on top of the seasonal drift and bearing meander. Mild and never zero.
    /// </summary>
    public double JetStreak(double x, double z, double totalHours)
    {
        double sx = Math.Sin(x * 0.0006 + totalHours * 0.008);
        double sz = Math.Sin(z * 0.0006 + totalHours * 0.006);
        return 1.0 + JetNoiseAmplitude * sx * sz;
    }

    /// <summary>
    /// Precipitation gate from local pressure (+ = low, − = high): a high clears the sky (→0) and a low
    /// opens the tap (→1). Shared by the Doppler radar overlay and the front computer so both treat the
    /// pressure differential as the driver of where precipitation (and thus fronts) appear.
    /// </summary>
    public static double PrecipGate(double pressure)
    {
        double p = pressure < -1.5 ? -1.5 : (pressure > 1.5 ? 1.5 : pressure);
        double g = 0.55 + 0.7 * p;
        return g < 0 ? 0 : (g > 1 ? 1 : g);
    }

    /// <summary>
    /// Slow, deterministic large-scale meander in [-1, 1]. A handful of incommensurate sines gives an
    /// organic wander that is identical on server and client without needing a shared noise seed.
    /// <paramref name="freq"/> scales the spatial/temporal frequency (the N/S term and the time terms
    /// are weighted higher, so the jetstream snakes more across Z and evolves faster over time).
    /// </summary>
    public static double Meander(double x, double z, double totalHours, double freq)
    {
        double a = Math.Sin(x * 0.0005 * freq + totalHours * 0.012 * freq);
        double b = Math.Sin(z * 0.0009 * freq - totalHours * 0.020 * freq);
        double c = Math.Sin((x + z) * 0.0004 * freq + totalHours * 0.010 * freq);
        return (a + b + c) / 3.0;
    }

    /// <summary>
    /// Wind vector at a world position. X/Z components are East/South; the magnitude is the strength
    /// multiplier (≈1.0 in calm jetstream flow, higher near strong lows).
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours) => WindAt(x, z, totalHours, -1, out _);

    /// <summary>
    /// As <see cref="WindAt(double,double,double)"/> but also reports the orographic speed attenuation
    /// (1 = unaffected by terrain, &lt;1 = slowed by windward climb/lee wake) so callers that derive their
    /// own speed from something other than this vector's magnitude — see
    /// <see cref="DynamicWindModCommon.OnGetWindSpeed"/> — can still apply the terrain slowdown.
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours, out double orographicFactor) =>
        WindAt(x, z, totalHours, -1, out orographicFactor);

    /// <summary>
    /// As <see cref="WindAt(double,double,double)"/> but optionally excluding one cell index from both
    /// the circulation sum and the pressure gradient. Used by the server sim to advect a cell on the
    /// <em>background</em> flow (everything but its own contribution), avoiding self-advection.
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours, int excludeCell) =>
        WindAt(x, z, totalHours, excludeCell, out _);

    /// <summary>As <see cref="WindAt(double,double,double,int)"/> plus the orographic factor (see the
    /// <c>out</c> overload above).</summary>
    public Vec2d WindAt(double x, double z, double totalHours, int excludeCell, out double orographicFactor)
    {
        // Base jetstream: due-East flow with a slow meandering bearing, tilted N/S by the season
        // (poleward in summer → −Z, equatorward in winter → +Z; sign is in SeasonalBiasZ).
        double meander = MeanderAmplitude * Meander(x, z, totalHours, MeanderFrequency);
        double angle = meander + SeasonalBiasMaxAngle * SeasonalBiasZ;
        double wx = BaseStrength * Math.Cos(angle);
        double wz = BaseStrength * Math.Sin(angle);

        double cosIn = Math.Cos(CyclonicInflowAngle);
        double sinIn = Math.Sin(CyclonicInflowAngle);
        double elapsed = totalHours - AnchorHours;

        for (int i = 0; i < Cells.Count; i++)
        {
            if (i == excludeCell) continue;
            PressureCell cell = Cells[i];
            double cellX = cell.X + cell.VelX * elapsed;
            double cellZ = cell.Z + cell.VelZ * elapsed;
            double dx = x - cellX;
            double dz = z - cellZ;

            double norm2 = EllipseNorm2(cell, dx, dz);
            double falloff = Math.Exp(-norm2); // elliptical gaussian
            if (falloff < 1e-4) continue;

            // Radial unit (center → sample, generalized to the cell's elliptical footprint) and tangent.
            // With +X=East, +Z=South (north-up map view), (rz, -rx) is the COUNTER-clockwise tangent on
            // screen — so a low (s>0) circulates CCW and a high (s<0) circulates CW, matching the
            // (northern-hemisphere) real-world convention.
            EllipseRadial(cell, dx, dz, out double rx, out double rz);
            double tx = rz;
            double tz = -rx;

            // windCell = s * falloff * (cos(inflow)*tangent - sin(inflow)*radial)
            //   low  (s>0): CCW tangent + inflow toward center  (cyclonic)
            //   high (s<0): CW tangent  + outflow from center   (anticyclonic)
            double scale = cell.Strength * falloff * CirculationStrength;
            wx += scale * (cosIn * tx - sinIn * rx);
            wz += scale * (cosIn * tz - sinIn * rz);
        }

        // 3. Down-gradient flow: air bleeds from high toward low pressure (+∇P points to lows).
        if (GradientWindScale > 0)
        {
            double d = GradSampleDist;
            double gx = (PressureAt(x + d, z, totalHours, excludeCell) - PressureAt(x - d, z, totalHours, excludeCell)) / (2 * d);
            double gz = (PressureAt(x, z + d, totalHours, excludeCell) - PressureAt(x, z - d, totalHours, excludeCell)) / (2 * d);
            double gwx = GradientWindScale * gx;
            double gwz = GradientWindScale * gz;
            double gm = Math.Sqrt(gwx * gwx + gwz * gwz);
            if (gm > GradMaxWind) { double s = GradMaxWind / gm; gwx *= s; gwz *= s; }
            wx += gwx;
            wz += gwz;
        }

        // 4. Orographic interaction with terrain.
        orographicFactor = 1.0;
        if (OrographicEnabled && TerrainHeightFunc != null)
        {
            ApplyOrographic(ref wx, ref wz, x, z, out orographicFactor);
        }

        return new Vec2d(wx, wz);
    }

    /// <summary>
    /// Terrain interaction: wind climbing into a slope slows (stagnation) and curves along the contour
    /// around the obstacle; wind sheltered by higher terrain upwind slows in the lee wake.
    /// </summary>
    private void ApplyOrographic(ref double wx, ref double wz, double x, double z, out double orographicFactor)
    {
        orographicFactor = 1.0;
        double origSpd = Math.Sqrt(wx * wx + wz * wz);
        if (origSpd < 1e-4) return;
        double spd = origSpd;
        double dirx = wx / spd;
        double dirz = wz / spd;

        Func<double, double, double> H = TerrainHeightFunc!;
        double d = OroGradDist;
        double h = H(x, z);
        if (h < OroHeightThreshold) return;
        double hx = (H(x + d, z) - H(x - d, z)) / (2 * d);
        double hz = (H(x, z + d) - H(x, z - d)) / (2 * d);
        double gmag = Math.Sqrt(hx * hx + hz * hz);

        // Windward: climbing into rising terrain → slow + deflect along the contour toward lower ground.
        double along = dirx * hx + dirz * hz; // > 0 = uphill ahead
        if (gmag > 1e-5 && along > 0)
        {
            double climb = along * OroSlopeSens;
            if (climb > 1) climb = 1;

            double cx = -hz / gmag, cz = hx / gmag; // contour (perpendicular to gradient)
            if (dirx * cx + dirz * cz < 0) { cx = -cx; cz = -cz; } // keep generally forward
            double k = OroDeflection * climb;
            double ndx = dirx * (1 - k) + cx * k;
            double ndz = dirz * (1 - k) + cz * k;
            double nl = Math.Sqrt(ndx * ndx + ndz * ndz);
            if (nl > 1e-6) { dirx = ndx / nl; dirz = ndz / nl; }

            spd *= 1.0 - OroSlowdown * climb;
        }

        // Lee wake: terrain upwind significantly higher than here → sheltered, low wind.
        double hUp = H(x - dirx * OroWakeProbe, z - dirz * OroWakeProbe);
        double rise = hUp - h;
        if (rise > OroWakeMinRise)
        {
            double wake = (rise - OroWakeMinRise) / OroWakeScale;
            if (wake > 1) wake = 1;
            spd *= 1.0 - OroWake * wake;
        }

        wx = dirx * spd;
        wz = dirz * spd;
        orographicFactor = spd / origSpd;
    }

    /// <summary>Signed pressure at a position (sum of cell contributions). + = low, - = high.</summary>
    public double PressureAt(double x, double z, double totalHours) => PressureAt(x, z, totalHours, -1);

    /// <summary>
    /// Pressure from the moving cells ALONE (no terrain thermal term). Used where a deterministic,
    /// side-independent value is required — the precipitation gate (so the gate never re-enters the
    /// climate/thermal path) and the front-blob contour (so fronts derive purely from the synced cells
    /// and persist across reloads exactly as the cells do). + = low, − = high.
    /// </summary>
    public double CellPressureAt(double x, double z, double totalHours) => CellPressureAt(x, z, totalHours, -1);

    /// <summary>
    /// As <see cref="CellPressureAt(double,double,double)"/> but optionally excluding one cell index —
    /// the "background" field induced by every OTHER system. Used by FrontComputer's open-curve march to
    /// find how strongly a nearby neighbour pulls a low's own front toward/around it, independent of the
    /// low's own (locally dominant, radially symmetric) contribution.
    /// </summary>
    public double CellPressureAt(double x, double z, double totalHours, int excludeCell)
    {
        double p = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            if (i == excludeCell) continue;
            PressureCell cell = Cells[i];
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double norm2 = EllipseNorm2(cell, dx, dz);
            p += cell.Strength * Math.Exp(-norm2);
        }
        return p;
    }

    // Storm-band narrowing when a low has a real JOIN neighbour on a side — mirrors
    // FrontComputer.MarkerMinStrength/BuildJoinCurve so weather intensifies along the same lines the
    // front map layer draws, rather than a symmetric poleward/equatorward half-cone.
    private const float StormNeighbourMinStrength = 0.15f;
    private const double StormBandWidthFactor = 0.5; // × cell radius

    /// <summary>
    /// Extra storm-likelihood forcing at a position. Real fronts trail a LOW, not a high — a high is
    /// fair-weather subsidence and contributes nothing here. Each low carries both a warm front (its
    /// POLEWARD side, where warm air overruns, advancing poleward) and a cold front (its EQUATORWARD
    /// side, where cold air digs in, advancing equatorward) — real geography, not the system's own
    /// direction of travel — scaled by that cell's own
    /// <see cref="PressureCell.StormLikelihood"/>, with a synergy bonus near the low's centre where the
    /// two sides' influence overlaps (strongest weather sits close to the low itself). When a side has a
    /// real neighbouring low (the same JOIN a front curve would connect to — see
    /// <see cref="DynamicWind.Client.MapLayers.FrontComputer"/>'s BuildJoinCurve), that side's bias is
    /// narrowed into a band hugging the axis toward the neighbour's midpoint instead of spread across the
    /// whole half-plane, so storminess visibly develops ALONG the same connecting line the map draws
    /// rather than just somewhere in that general direction. Independent of
    /// <see cref="PressureAt(double,double,double)"/>; meant to be added into the precipitation gate
    /// scaled by a caller-owned strength knob (see WeatherCoupling).
    /// </summary>
    public double StormBiasAt(double x, double z, double totalHours)
    {
        double warm = 0, cold = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell c = Cells[i];
            if (!c.IsLow || c.StormLikelihood <= 0f) continue; // highs are fair weather; no front bias

            double cx = c.X + c.VelX * elapsed;
            double cz = c.Z + c.VelZ * elapsed;
            double dx = x - cx, dz = z - cz;
            double falloff = Math.Exp(-EllipseNorm2(c, dx, dz));
            if (falloff < 1e-4) continue;

            double poleSign = PolewardZSign(cz);
            double bandWidth = Math.Max(1.0, c.Radius) * StormBandWidthFactor;

            FindSidedLowNeighbourAxis(i, cx, cz, elapsed, totalHours, -poleSign, out double coldAxisX, out double coldAxisZ, out bool coldJoined);
            double coldAlong = dx * coldAxisX + dz * coldAxisZ;
            double coldPerp = dx * -coldAxisZ + dz * coldAxisX;
            double coldBand = coldJoined ? Math.Exp(-(coldPerp * coldPerp) / (bandWidth * bandWidth)) : 1.0;
            cold += Math.Max(0.0, coldAlong) * falloff * c.StormLikelihood * coldBand;

            FindSidedLowNeighbourAxis(i, cx, cz, elapsed, totalHours, poleSign, out double warmAxisX, out double warmAxisZ, out bool warmJoined);
            double warmAlong = dx * warmAxisX + dz * warmAxisZ;
            double warmPerp = dx * -warmAxisZ + dz * warmAxisX;
            double warmBand = warmJoined ? Math.Exp(-(warmPerp * warmPerp) / (bandWidth * bandWidth)) : 1.0;
            warm += Math.Max(0.0, warmAlong) * falloff * c.StormLikelihood * warmBand;
        }
        // Strongest near the low's own centre, where the warm and cold side biases overlap.
        return warm + cold + 2.0 * Math.Min(warm, cold);
    }

    /// <summary>
    /// Finds the nearest OTHER significant, MOVING (open-wave) low strictly on the <paramref
    /// name="poleSign"/> side (pure poleward if +1, equatorward if −1) of low <paramref
    /// name="selfIndex"/> — the same neighbour a front curve would JOIN to (FrontComputer excludes
    /// Stationary/Occluded lows from that search too, since those already draw their own closed loop).
    /// Returns a unit axis toward that neighbour's midpoint with <paramref name="selfIndex"/>'s own
    /// position (<paramref name="joined"/> = true), or the plain poleward/equatorward direction with
    /// <paramref name="joined"/> = false if there's no such neighbour.
    /// </summary>
    private void FindSidedLowNeighbourAxis(int selfIndex, double cx, double cz, double elapsed, double totalHours, double poleSign,
        out double axisX, out double axisZ, out bool joined)
    {
        axisX = 0.0; axisZ = poleSign; // plain poleward/equatorward direction (PolewardZSign is Z-only)
        joined = false;

        int bestIdx = -1;
        double bestDist = double.MaxValue;
        for (int j = 0; j < Cells.Count; j++)
        {
            if (j == selfIndex) continue;
            PressureCell o = Cells[j];
            if (!o.IsLow || Math.Abs(o.Strength) < StormNeighbourMinStrength) continue;
            if (totalHours < o.OccludedUntilTotalHours) continue; // occluded: excluded, same as FrontComputer
            double oSpeed = Math.Sqrt(o.VelX * o.VelX + o.VelZ * o.VelZ);
            if (oSpeed < StationarySpeed) continue; // stationary: excluded, same as FrontComputer
            double ox = o.X + o.VelX * elapsed, oz = o.Z + o.VelZ * elapsed;
            double ddx = ox - cx, ddz = oz - cz;
            if (ddx * axisX + ddz * axisZ <= 0) continue; // not on this side
            double dist = Math.Sqrt(ddx * ddx + ddz * ddz);
            if (dist < bestDist) { bestDist = dist; bestIdx = j; }
        }
        if (bestIdx < 0) return;

        PressureCell nb = Cells[bestIdx];
        double nx = nb.X + nb.VelX * elapsed, nz = nb.Z + nb.VelZ * elapsed;
        double midX = (cx + nx) / 2.0, midZ = (cz + nz) / 2.0;
        double toMidX = midX - cx, toMidZ = midZ - cz;
        double len = Math.Sqrt(toMidX * toMidX + toMidZ * toMidZ);
        if (len < 1e-6) return;
        axisX = toMidX / len; axisZ = toMidZ / len;
        joined = true;
    }

    /// <summary>
    /// Falloff+<see cref="PressureCell.StormLikelihood"/>-weighted share of nearby lows' influence at a
    /// position, bucketed by which weather REGIME each contributing low is currently in — Occluded (its
    /// cold front has caught its warm front), Stationary (near-zero own velocity), or Moving (split
    /// Warm/Cold by real geography, same poleward/equatorward convention as <see cref="StormBiasAt"/>).
    /// Lighter-weight than <see cref="StormBiasAt"/> (a single O(cells) pass, no neighbour-JOIN banding)
    /// since it's meant to answer "what KIND of front is this" for intensity weighting, not to trace the
    /// precise spatial extent of the storm band itself.
    /// </summary>
    public struct FrontRegimeBias
    {
        public double Cold;
        public double Warm;
        public double Occluded;
        public double Stationary;
    }

    /// <summary>Half-width (× a low's own <see cref="PressureCell.Radius"/>) of the smooth Warm/Cold
    /// blend band in <see cref="FrontRegimeBiasAt"/>, centred on the low's own latitude line — a plain
    /// sign test there switched the weather-intensity multiplier discontinuously right at that line,
    /// reading as an unrealistically sharp jump between stormy and calm weather. <c>Math.Tanh</c> over
    /// this width gives a gradual apportionment between the two instead.</summary>
    private const double RegimeBlendWidthFactor = 0.6;

    /// <summary>See <see cref="FrontRegimeBias"/>. Real fronts trail a LOW; highs contribute nothing.</summary>
    public FrontRegimeBias FrontRegimeBiasAt(double x, double z, double totalHours)
    {
        var result = new FrontRegimeBias();
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell c = Cells[i];
            if (!c.IsLow || c.StormLikelihood <= 0f) continue;

            double cx = c.X + c.VelX * elapsed;
            double cz = c.Z + c.VelZ * elapsed;
            double dx = x - cx, dz = z - cz;
            double falloff = Math.Exp(-EllipseNorm2(c, dx, dz));
            if (falloff < 1e-4) continue;

            double weight = falloff * c.StormLikelihood;

            if (totalHours < c.OccludedUntilTotalHours) { result.Occluded += weight; continue; }

            double speed = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);
            if (speed < StationarySpeed) { result.Stationary += weight; continue; }

            // Moving open-wave low: poleward side is the warm front, equatorward the cold front — same
            // convention as StormBiasAt/FrontComputer, but apportioned smoothly across a band straddling
            // the low's own latitude line rather than switched abruptly at it.
            double poleSign = PolewardZSign(cz);
            double blendWidth = Math.Max(1.0, c.Radius) * RegimeBlendWidthFactor;
            double warmFrac = 0.5 * (1.0 + Math.Tanh(dz * poleSign / blendWidth));
            result.Warm += weight * warmFrac;
            result.Cold += weight * (1.0 - warmFrac);
        }
        return result;
    }

    /// <summary>
    /// Falloff-weighted blend of nearby cells' intrinsic <see cref="PressureCell.AirMassTemp"/> in
    /// [-1, 1] — a cold low and a warm high can both exist, so this is deliberately independent of
    /// pressure sign. Weighted by each cell's footprint falloff × |Strength| (stronger systems dominate
    /// their own airmass more). Returns 0 (neutral) where no cell has meaningful influence. Used by
    /// DynamicWindModCommon.OnGetClimate to shift real temperature toward the local air mass's character.
    /// </summary>
    public double CellTemperatureAt(double x, double z, double totalHours)
    {
        double wSum = 0, tSum = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell cell = Cells[i];
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double falloff = Math.Exp(-EllipseNorm2(cell, dx, dz));
            if (falloff < 1e-4) continue;
            double w = falloff * Math.Max(0.05, Math.Abs(cell.Strength));
            wSum += w;
            tSum += w * cell.AirMassTemp;
        }
        return wSum > 1e-6 ? tSum / wSum : 0.0;
    }

    /// <summary>
    /// Falloff-weighted sum of nearby heat-wave/cold-wave highs' <see cref="PressureCell.WaveTemperatureDelta"/>
    /// (°C) — kept separate from <see cref="CellTemperatureAt"/>'s [-1,1] air-mass blend so a rare, strong
    /// heat/cold-wave event's swing isn't diluted by averaging against every other active cell (an ordinary
    /// front sitting nearby would otherwise pull the blend back toward neutral). Summed rather than
    /// averaged: these are rare and spawned clear of other cells (see TrySeedHeatColdWave), so overlap
    /// between two waves is an edge case, not the common path. Used by DynamicWindModCommon.OnGetClimate.
    /// </summary>
    public double HeatColdWaveDeltaAt(double x, double z, double totalHours)
    {
        double sum = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell cell = Cells[i];
            if (!cell.IsHeatColdWave) continue;
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double falloff = Math.Exp(-EllipseNorm2(cell, dx, dz));
            if (falloff < 1e-4) continue;
            sum += falloff * cell.WaveTemperatureDelta;
        }
        return sum;
    }

    /// <summary>
    /// As <see cref="PressureAt(double,double,double)"/> but optionally excluding one cell index (used
    /// when advecting that cell on the background flow). The static terrain thermal baseline is scaled
    /// by <see cref="ThermalStaticScale"/>; the rest of the terrain signal is carried by mobile cells
    /// the server sim seeds from terrain hotspots.
    /// </summary>
    private double PressureAt(double x, double z, double totalHours, int excludeCell)
    {
        double p = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            if (i == excludeCell) continue;
            PressureCell cell = Cells[i];
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double norm2 = EllipseNorm2(cell, dx, dz);
            p += cell.Strength * Math.Exp(-norm2);
        }
        if (ThermalStaticScale > 0)
            p += ThermalStaticScale * (ThermalPressureFunc?.Invoke(x, z, totalHours) ?? 0.0);
        return p;
    }

    /// <summary>
    /// Signed pressure including the vertical lapse: real air pressure falls with altitude, read here
    /// as "more low" (+). Below sea level there is no change; at the build ceiling it reaches
    /// +<see cref="AltitudeMaxDrop"/>. This term has no horizontal gradient, so it never alters wind —
    /// only the displayed/weather-coupled pressure.
    /// </summary>
    public double PressureAt(double x, double y, double z, double totalHours)
        => PressureAt(x, z, totalHours, -1) + AltitudeOffset(y);

    /// <summary>Vertical pressure lapse contribution at height <paramref name="y"/> (0 at/below sea level, +AltitudeMaxDrop at ceiling).</summary>
    public double AltitudeOffset(double y)
    {
        double span = WorldHeight - SeaLevel;
        if (span <= 0) return 0;
        double frac = (y - SeaLevel) / span;
        if (frac <= 0) return 0;
        if (frac > 1) frac = 1;
        return AltitudeMaxDrop * frac; // + = low (lower real pressure aloft)
    }
}
