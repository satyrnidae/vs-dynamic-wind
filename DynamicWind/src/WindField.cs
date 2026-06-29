using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// A moving pressure system. Positive <see cref="Strength"/> = low pressure (cyclonic, draws wind
/// inward and circulates counter-clockwise); negative = high pressure (anticyclonic, pushes wind
/// outward, clockwise). Cells advect roughly west-to-east on the jetstream and expire after a while.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class PressureCell
{
    public double X;
    public double Z;
    /// <summary>+ = low pressure, - = high pressure. Magnitude scales the induced wind speed.</summary>
    public float Strength;
    /// <summary>Influence radius in blocks (gaussian falloff).</summary>
    public float Radius;
    public double VelX;
    public double VelZ;
    public double BornTotalHours;
    public double DiesTotalHours;

    // Computed; excluded from protobuf (ImplicitFields.AllPublic would otherwise try to deserialize
    // into this read-only property and throw).
    [ProtoIgnore]
    public bool IsLow => Strength > 0f;
}

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

    // Altitude pressure lapse: real air pressure falls with height. Sampled in the Y-aware
    // PressureAt overload only (a vertical-only term, so it adds no horizontal wind gradient).
    public double SeaLevel = 110;
    public double WorldHeight = 256;
    /// <summary>Fractional pressure drop reached at the build ceiling (0.5 = −50%), toward LOW (+).</summary>
    public double AltitudeMaxDrop = 0.5;

    /// <summary>
    /// How much of the raw terrain thermal field stays as a static (terrain-pinned) baseline in
    /// <see cref="PressureAt"/>. The remainder is meant to be expressed as mobile, dispersing cells
    /// seeded by the server sim, so the pressure differential moves with the wind. 1 = fully static.
    /// </summary>
    public double ThermalStaticScale = 1.0;

    private const double GradSampleDist = 48.0;
    private const double GradMaxWind = 1.5;
    private const double OroGradDist = 24.0;   // terrain gradient sample spacing
    private const double OroSlopeSens = 3.0;   // slope (blocks/block) → climb factor
    private const double OroWakeProbe = 48.0;  // how far upwind to look for a sheltering rise
    private const double OroWakeMinRise = 8.0; // upwind terrain must be this much higher to count
    private const double OroWakeScale = 40.0;  // rise over this → full wake

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
    public Vec2d WindAt(double x, double z, double totalHours) => WindAt(x, z, totalHours, -1);

    /// <summary>
    /// As <see cref="WindAt(double,double,double)"/> but optionally excluding one cell index from both
    /// the circulation sum and the pressure gradient. Used by the server sim to advect a cell on the
    /// <em>background</em> flow (everything but its own contribution), avoiding self-advection.
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours, int excludeCell)
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
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < 1e-3) dist = 1e-3;

            double norm = dist / cell.Radius;
            double falloff = Math.Exp(-norm * norm); // gaussian
            if (falloff < 1e-4) continue;

            // Radial unit (center → sample) and tangent. With +X=East, +Z=South (north-up map view),
            // (rz, -rx) is the COUNTER-clockwise tangent on screen — so a low (s>0) circulates CCW and a
            // high (s<0) circulates CW, matching the (northern-hemisphere) real-world convention.
            double rx = dx / dist;
            double rz = dz / dist;
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
        if (OrographicEnabled && TerrainHeightFunc != null)
        {
            ApplyOrographic(ref wx, ref wz, x, z);
        }

        return new Vec2d(wx, wz);
    }

    /// <summary>
    /// Terrain interaction: wind climbing into a slope slows (stagnation) and curves along the contour
    /// around the obstacle; wind sheltered by higher terrain upwind slows in the lee wake.
    /// </summary>
    private void ApplyOrographic(ref double wx, ref double wz, double x, double z)
    {
        double spd = Math.Sqrt(wx * wx + wz * wz);
        if (spd < 1e-4) return;
        double dirx = wx / spd;
        double dirz = wz / spd;

        Func<double, double, double> H = TerrainHeightFunc!;
        double d = OroGradDist;
        double h = H(x, z);
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
    }

    /// <summary>Signed pressure at a position (sum of cell contributions). + = low, - = high.</summary>
    public double PressureAt(double x, double z, double totalHours) => PressureAt(x, z, totalHours, -1);

    /// <summary>
    /// Pressure from the moving cells ALONE (no terrain thermal term). Used where a deterministic,
    /// side-independent value is required — the precipitation gate (so the gate never re-enters the
    /// climate/thermal path) and the front-blob contour (so fronts derive purely from the synced cells
    /// and persist across reloads exactly as the cells do). + = low, − = high.
    /// </summary>
    public double CellPressureAt(double x, double z, double totalHours)
    {
        double p = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell cell = Cells[i];
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double norm2 = (dx * dx + dz * dz) / (cell.Radius * cell.Radius);
            p += cell.Strength * Math.Exp(-norm2);
        }
        return p;
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
            double dist2 = dx * dx + dz * dz;
            double norm2 = dist2 / (cell.Radius * cell.Radius);
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
