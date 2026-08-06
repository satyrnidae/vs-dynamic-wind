using System;
using System.Collections.Generic;
using DynamicWind.Network;
using DynamicWind.PressureSystem;
using DynamicWind.Utils;
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
    public double BaseStrength = 0.7;
    /// <summary>Maximum meander of the base jetstream angle, in radians.</summary>
    public double MeanderAmplitude = 0.9;
    /// <summary>Spatial/temporal frequency multiplier for the jetstream meander (higher = the bearing
    /// wiggles over shorter N/S distances and faster in time).</summary>
    public double MeanderFrequency = 2.0;
    /// <summary>Inflow/outflow tilt of cyclonic circulation, in radians (~20°).</summary>
    public double CyclonicInflowAngle = 0.35;
    /// <summary>Multiplier on each pressure cell's induced circulation (tangential + radial flow).
    /// &gt;1 makes the wind swirl around lows/highs more strongly.</summary>
    public double CirculationStrength = 2.5;

    /// <summary>
    /// Seasonal meridional (north/south) bias factor in [-1, 1] applied to the base jetstream:
    /// &gt;0 tilts the westerly toward +Z (south), &lt;0 toward -Z (north). Computed server-side from
    /// the season + hemisphere (poleward in summer, equatorward in winter) and synced to clients -
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

    /// <summary>Guards every read and mutation of <see cref="Cells"/>: item-physics entities may query
    /// wind (and therefore iterate <see cref="Cells"/>) off the main thread (same concern as
    /// <see cref="TerrainSystem.ThermalCache"/>), while the server tick / network receive / render /
    /// command-handler threads all read or mutate it too. <c>lock</c> is reentrant per-thread, so nested
    /// calls that already hold this lock (e.g. WindAt's gradient sampling calling PressureAt) are safe.</summary>
    public readonly object CellsLock = new();

    /// <summary>
    /// Optional terrain-driven near-surface TEMPERATURE anomaly (°C) at (x, z, totalHours) - see
    /// <see cref="TerrainSystem.TerrainThermal"/>. Set per side (server uses world data; client samples its
    /// loaded world). + = warmer than ambient (sun-warmed land, or relatively warm water in the cold
    /// season), - = cooler. Drives the sea-breeze wind term in <see cref="WindAt"/> and the climate
    /// temperature shift in <c>DynamicWindModCommon.OnGetClimate</c>. Deliberately NOT part of
    /// <see cref="PressureAt"/>: pressure is purely synoptic (moving cells + altitude lapse).
    /// </summary>
    public Func<double, double, double, double>? TerrainTempFunc;

    /// <summary>
    /// Optional terrain surface height (blocks) at (x, z). Server: Algernons Terrain Sampler; client:
    /// rain-map height for loaded chunks. Drives orographic deflection/slowdown in <see cref="WindAt"/>
    /// and <see cref="WaterProximityAt"/>.
    /// </summary>
    public Func<double, double, double>? TerrainHeightFunc;

    /// <summary>
    /// Optional terrain-noise humidity baseline at (x, z, totalHours) - worldgen rainfall noise (+ a small
    /// forest-density nudge), see <see cref="TerrainSystem.TerrainHumidity"/>. Set per side. Blended into
    /// <see cref="HumidityAt"/>.
    /// </summary>
    public Func<double, double, double, double>? TerrainHumidityFunc;

    /// <summary>Strength of down-gradient (high→low) pressure flow added to the wind. 0 = off.</summary>
    public double GradientWindScale = 150.0;

    // Humidity overlay strengths (config-driven, see HumidityAt).
    public double HumidityWaterProximityStrength = 0.25;
    public double HumidityPressureStrength = 0.15;
    public double HumidityNoiseAmplitude = 0.1;

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
    /// <summary>Fractional pressure drop reached at the build ceiling (0.5 = -50%), toward LOW (+).</summary>
    public double AltitudeMaxDrop = 0.5;

    /// <summary>World Z of the map's N/S midline - set per side from MapSizeZ / 2, same on both sides
    /// since it's derived from world geometry, not synced. Vintage Story's REAL climate latitude
    /// (<see cref="LatitudeFunc"/>) is a repeating triangle wave, not a single equator/pole pair at the
    /// map edges, so this is only a crude fallback for <see cref="PolewardZSign"/> when
    /// <see cref="LatitudeFunc"/> hasn't been wired up yet (e.g. very early startup).</summary>
    public double MapCenterZ = 128.0;

    /// <summary>
    /// Optional hook onto the game's real climate latitude at world Z (<c>Calendar.OnGetLatitude</c>):
    /// +1 at a north pole, -1 at a south pole, 0 at an equator. Set per side once the calendar is ready.
    /// Vintage Story's latitude is a repeating triangle wave (period <c>4 x polarEquatorDistance</c>, a
    /// world-config value, phase-shifted by a per-seed offset) - poles and equators alternate repeatedly
    /// across the map, NOT a single equator at the map's centre with poles at its edges. See
    /// <see cref="PolewardZSign"/>.
    /// </summary>
    public Func<double, double>? LatitudeFunc;

    /// <summary>Z step used to numerically differentiate <see cref="LatitudeFunc"/> in
    /// <see cref="PolewardZSign"/> - cheap since the real latitude curve is piecewise-linear, so any
    /// small step off an exact fold point (a pole or equator crossing) gives the correct local slope.</summary>
    private const double LatitudeSampleDist = 8.0;

    /// <summary>
    /// +1 if the LOCAL poleward direction (away from the equator, toward whichever pole is nearer) at
    /// world Z <paramref name="z"/> is +Z, else -1. Found from the sign of <see cref="LatitudeFunc"/>'s
    /// local slope in |latitude| - NOT from a fixed hemisphere, since Vintage Story's real latitude
    /// alternates north/south repeatedly across the map (see <see cref="LatitudeFunc"/>), so which way is
    /// "poleward" flips every <c>2 x polarEquatorDistance</c> blocks rather than once at a map midline.
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
    /// Strength of the sea-breeze/land-breeze term in <see cref="WindAt"/>: wind blows TOWARD warmer air
    /// (into the thermal low), i.e. up the <see cref="TerrainTempFunc"/> gradient, scaled by this. A
    /// thermally direct circulation driven from the temperature field itself rather than from a
    /// terrain-pinned pressure baseline. 0 disables it.
    /// </summary>
    public double SeaBreezeStrength = 1.0;

    /// <summary>|velocity| (blocks/hour) below which a low is treated as "stationary" rather than a
    /// moving open-wave system - shared by <see cref="FrontRegimeBiasAt"/> and
    /// <see cref="DynamicWind.PressureSystem.FrontComputer"/> (which renders the closed encircling
    /// contour for exactly this regime), so both agree on the same threshold.</summary>
    public const double StationarySpeed = 60.0;

    /// <summary>Tilt (radians) of a moving low's warm/cold front bias angle off the pure travel/anti-travel
    /// axis toward its real-world side - shared by <see cref="DynamicWind.PressureSystem.FrontComputer"/>'s
    /// actual dual front lines and <see cref="MovingFrontAngles"/> (storm-forcing/weather-intensity bias),
    /// so the weather always lines up with what's actually drawn instead of drifting out of sync with it
    /// (as the old poleward/equatorward-only convention did once fronts switched to leading/trailing the
    /// low's own direction of travel).</summary>
    public const double WaveFrontTiltAngle = 20.0 * Math.PI / 180.0;

    /// <summary>Leading (warm) and trailing (cold) bias angles for a MOVING open-wave low - see
    /// <see cref="WaveFrontTiltAngle"/>. The single source of truth for this split, called by both
    /// <see cref="DynamicWind.PressureSystem.FrontComputer"/> (to draw the two front lines) and this
    /// class's own <see cref="FrontRegimeBiasAt"/> (to weight precipitation-type intensity toward the same
    /// two edges), so they can never drift out of sync with each other again.</summary>
    public void MovingFrontAngles(double velX, double velZ, double z, out double warmAngle, out double coldAngle)
    {
        double travelAngle = Math.Atan2(velZ, velX);
        double poleSign = PolewardZSign(z);
        warmAngle = travelAngle + poleSign * WaveFrontTiltAngle;
        // The trailing axis is travelAngle + PI, so tilting the cold front equatorward means ADDING
        // poleSign * tilt here, not subtracting it: subtracting rotates the reversed direction back
        // toward the SAME side the warm front already occupies (travel=0, poleSign=+1 gives 160deg ->
        // (-0.94, +0.34), poleward), collapsing the comma shape and cancelling the regime split's Z term.
        coldAngle = travelAngle + Math.PI + poleSign * WaveFrontTiltAngle;
    }

    private const double GradSampleDist = 48.0;
    private const double GradMaxWind = 1.5;

    // Sea breeze. Sampled wider than the pressure gradient: a coastline's thermal contrast is a
    // terrain-scale feature and a 48-block probe pair straddles too little of it to read the transition.
    // Gain converts a NORMALISED anomaly gradient (per block, after dividing by TerrainThermal.Amplitude)
    // into wind units - a typical coast runs the full contrast over a few hundred blocks, i.e. ~0.005/block,
    // so this lands a strong shoreline breeze near the cap without pinning ordinary terrain there.
    private const double SeaBreezeSampleDist = 96.0;
    private const double SeaBreezeGain = 150.0;
    private const double SeaBreezeMaxWind = 1.0;

    // Directional steering: a pressure cell's rotational influence should keep bending the AMBIENT wind's
    // DIRECTION well beyond the (narrower) range at which it still meaningfully pushes extra SPEED - real
    // large-scale steering flow around a system reaches much farther than its local wind-speed footprint.
    // Implemented as a magnitude-preserving blend of the wind bearing toward each cell's circulation
    // direction, weighted by a WIDER falloff than the one gating the additive speed push below, so it
    // never adds speed on its own - only reorients whatever speed the rest of the field already produced.
    private const double DirectionalFalloffFactor = 0.2;   // <1 widens the steering falloff vs. the speed falloff (exp(-norm2 * factor))
    private const double DirectionalSteeringStrength = 1.2; // weighted influence -> bearing-blend fraction
    private const double MaxDirectionalBlend = 0.6;         // cap on how far the bearing can be pulled per cell pass
    private const double OroGradDist = 24.0;   // terrain gradient sample spacing
    private const double OroSlopeSens = 3.0;   // slope (blocks/block) → climb factor
    private const double OroWakeProbe = 48.0;  // how far upwind to look for a sheltering rise
    private const double OroWakeMinRise = 8.0; // upwind terrain must be this much higher to count
    private const double OroWakeScale = 40.0;  // rise over this → full wake

    // Elliptical-footprint math (falloff/gradient sampling against a cell's along-stream/cross-stream
    // axes) is shared with WindFieldSimulation via EllipseGeometry.Norm2/Radial/RadiusToward.

    /// <summary>
    /// Slow jet-streak multiplier in [1-<see cref="JetNoiseAmplitude"/>, 1+…]: a multiplied sinusoidal
    /// layout (a slow X wave x a slow Z wave) that evolves slowly over time, giving the jetstream faster
    /// and slower "streaks" on top of the seasonal drift and bearing meander. Mild and never zero.
    /// </summary>
    public double JetStreak(double x, double z, double totalHours)
    {
        double sx = Math.Sin(x * 0.0006 + totalHours * 0.008);
        double sz = Math.Sin(z * 0.0006 + totalHours * 0.006);
        return 1.0 + JetNoiseAmplitude * sx * sz;
    }

    /// <summary>
    /// Precipitation gate from local pressure (+ = low, - = high): a high clears the sky (→0) and a low
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
    /// own speed from something other than this vector's magnitude - see
    /// <see cref="DynamicWindModCommon.OnGetWindSpeed"/> - can still apply the terrain slowdown.
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
    /// <c>out</c> overload above). <paramref name="includeTerrainGradient"/> controls whether the
    /// down-gradient term (below) reads the full field (cells + the ever-present terrain-thermal
    /// baseline - the default, correct for direction/visuals everywhere) or cell pressure alone (pass
    /// <c>false</c> - used by <see cref="DynamicWindModCommon.ComputeWindSpeed"/> to isolate genuine
    /// pressure-cell-driven excess speed from the terrain sea-breeze term, which is real and drives
    /// direction, but shouldn't itself count as "front wind" and add speed near every coastline/terrain
    /// contrast regardless of whether any pressure system is nearby).</summary>
    public Vec2d WindAt(double x, double z, double totalHours, int excludeCell, out double orographicFactor,
        bool includeTerrainGradient = true)
        => WindAtCore(x, z, totalHours, excludeCell, out orographicFactor, includeTerrainGradient, false, out _);

    /// <summary>
    /// One-pass variant for <see cref="DynamicWindModCommon.ComputeWindSpeed"/>, which needs BOTH the full
    /// wind vector (direction, terrain gradient included) and the magnitude of the cells-only field (to
    /// measure front-driven excess speed). Those two differ ONLY in the down-gradient term's pressure
    /// source, so calling <see cref="WindAt(double,double,double,int,out double,bool)"/> twice re-walked the
    /// cell list under the lock, redid the steering blend, and ran the whole orographic step a second time
    /// (only to divide its factor straight back out) on the mod's hottest path - every
    /// <c>GetWindSpeedAt</c>, i.e. per dropped item, per weather particle, per foliage sample, per windmill
    /// tick. <paramref name="cellsOnlyPreOroMagnitude"/> is taken BEFORE the orographic step, matching what
    /// dividing the second call's magnitude by its own orographic factor used to approximate.
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours, out double orographicFactor,
        out double cellsOnlyPreOroMagnitude)
        => WindAtCore(x, z, totalHours, -1, out orographicFactor, true, true, out cellsOnlyPreOroMagnitude);

    private Vec2d WindAtCore(double x, double z, double totalHours, int excludeCell, out double orographicFactor,
        bool includeTerrainGradient, bool wantCellsOnlyMagnitude, out double cellsOnlyPreOroMagnitude)
    {
        double wx, wz;
        cellsOnlyPreOroMagnitude = 0;
        lock (CellsLock)
        {
            // Base jetstream: due-East flow with a slow meandering bearing, tilted N/S by the season
            // (poleward in summer → -Z, equatorward in winter → +Z; sign is in SeasonalBiasZ).
            double meander = MeanderAmplitude * Meander(x, z, totalHours, MeanderFrequency);
            double angle = meander + SeasonalBiasMaxAngle * SeasonalBiasZ;
            wx = BaseStrength * Math.Cos(angle);
            wz = BaseStrength * Math.Sin(angle);

            double cosIn = Math.Cos(CyclonicInflowAngle);
            double sinIn = Math.Sin(CyclonicInflowAngle);
            double elapsed = totalHours - AnchorHours;

            double dirTargetX = 0, dirTargetZ = 0, dirWeightSum = 0;

            for (int i = 0; i < Cells.Count; i++)
            {
                if (i == excludeCell) continue;
                PressureCell cell = Cells[i];
                double cellX = cell.X + cell.VelX * elapsed;
                double cellZ = cell.Z + cell.VelZ * elapsed;
                double dx = x - cellX;
                double dz = z - cellZ;

                double norm2 = EllipseGeometry.Norm2(cell, dx, dz);
                // Directional falloff is deliberately wider than the speed falloff below (see
                // DirectionalFalloffFactor), so this is the gate that actually gets checked first - a
                // position can be irrelevant for steering (skip) while still, in principle, closer than
                // that; the narrower speed falloff below still applies its own independent check.
                double dirFalloff = Math.Exp(-norm2 * DirectionalFalloffFactor);
                if (dirFalloff < 1e-4) continue;

                // Radial unit (center → sample, generalized to the cell's elliptical footprint) and tangent.
                // With +X=East, +Z=South (north-up map view), (rz, -rx) is the COUNTER-clockwise tangent on
                // screen - so a low (s>0) circulates CCW and a high (s<0) circulates CW, matching the
                // (northern-hemisphere) real-world convention.
                EllipseGeometry.Radial(cell, dx, dz, out double rx, out double rz);
                double tx = rz;
                double tz = -rx;

                // sideX/Z = cos(inflow)*tangent - sin(inflow)*radial - the (unscaled) direction a low's
                // circulation pulls the wind toward at this point (cyclonic: CCW tangent + inflow); a high
                // pulls the opposite way, applied via cell.Strength's sign below.
                double sideX = cosIn * tx - sinIn * rx;
                double sideZ = cosIn * tz - sinIn * rz;

                // Speed push: the narrower (original) falloff - a cell's actual contribution to wind SPEED
                // still fades quickly with distance, unlike the wider steering effect above.
                double falloff = Math.Exp(-norm2);
                if (falloff >= 1e-4)
                {
                    double scale = cell.Strength * falloff * CirculationStrength;
                    wx += scale * sideX;
                    wz += scale * sideZ;
                }

                // Directional steering: accumulate a weighted target bearing (this cell's own circulation
                // direction, sign-only - magnitude-independent) using the WIDE falloff, blended into the
                // final bearing (magnitude-preserving) once after the loop.
                double sideLen = Math.Sqrt(sideX * sideX + sideZ * sideZ);
                if (sideLen > 1e-9)
                {
                    double dirWeight = dirFalloff * Math.Abs(cell.Strength);
                    double sgn = Math.Sign(cell.Strength);
                    dirTargetX += dirWeight * sgn * sideX / sideLen;
                    dirTargetZ += dirWeight * sgn * sideZ / sideLen;
                    dirWeightSum += dirWeight;
                }
            }

            // Apply the directional steering blend to whatever bearing the jetstream+circulation sum has
            // so far - a pure rotation-toward-target that preserves the CURRENT magnitude exactly, so this
            // only reorients the flow, never adds speed on its own (see DirectionalFalloffFactor remarks).
            // Applied before the pressure-gradient term below, which is a separate physical mechanism
            // (down-gradient bleed) left untouched by this circulation-only steering.
            if (dirWeightSum > 1e-6)
            {
                double dtx = dirTargetX / dirWeightSum, dtz = dirTargetZ / dirWeightSum;
                double dtLen = Math.Sqrt(dtx * dtx + dtz * dtz);
                double curLen = Math.Sqrt(wx * wx + wz * wz);
                if (dtLen > 1e-9 && curLen > 1e-9)
                {
                    dtx /= dtLen; dtz /= dtLen;
                    double curX = wx / curLen, curZ = wz / curLen;
                    double blend = Math.Min(MaxDirectionalBlend, dirWeightSum * DirectionalSteeringStrength);
                    double newX = curX * (1.0 - blend) + dtx * blend;
                    double newZ = curZ * (1.0 - blend) + dtz * blend;
                    double newLen = Math.Sqrt(newX * newX + newZ * newZ);
                    if (newLen > 1e-9)
                    {
                        wx = newX / newLen * curLen;
                        wz = newZ / newLen * curLen;
                    }
                }
            }

            // 3. Down-gradient flow: air bleeds from high toward low pressure (+∇P points to lows).
            // Pressure is purely synoptic now (moving cells; the altitude lapse is vertical-only and so has
            // no horizontal gradient), which makes this term identical for the full and the cells-only
            // field - what separates them is the SEA BREEZE below, gated by includeTerrainGradient.
            if (GradientWindScale > 0)
            {
                double d = GradSampleDist;
                double gx = (CellPressureAt(x + d, z, totalHours, excludeCell) - CellPressureAt(x - d, z, totalHours, excludeCell)) / (2 * d);
                double gz = (CellPressureAt(x, z + d, totalHours, excludeCell) - CellPressureAt(x, z - d, totalHours, excludeCell)) / (2 * d);
                double gwx = GradientWindScale * gx;
                double gwz = GradientWindScale * gz;
                double gm = Math.Sqrt(gwx * gwx + gwz * gwz);
                if (gm > GradMaxWind) { double s = GradMaxWind / gm; gwx *= s; gwz *= s; }
                wx += gwx;
                wz += gwz;
            }

            // Cells-only magnitude is taken HERE - after the synoptic terms, before the sea breeze - since
            // the sea breeze is exactly the ever-present terrain contribution that must not count as
            // "front" wind (see the one-pass WindAt overload and DynamicWindModCommon.ComputeWindSpeed).
            if (wantCellsOnlyMagnitude) cellsOnlyPreOroMagnitude = Math.Sqrt(wx * wx + wz * wz);

            // 4. Sea breeze / land breeze - a thermally DIRECT circulation, driven straight off the terrain
            // temperature field rather than through a terrain-pinned pressure baseline: surface air flows
            // toward warmer air (into the thermal low), i.e. UP the temperature gradient. Warm-season
            // daytime coast → water is cooler → flow runs inland (sea breeze); at night, and through the
            // cold season, TerrainThermal's sign flips and this reverses into a land breeze for free.
            // The gradient is normalised by TerrainThermal.Amplitude so the gain stays meaningful if that
            // field's °C scale is retuned.
            if (includeTerrainGradient && SeaBreezeStrength > 0 && TerrainTempFunc != null)
            {
                double d = SeaBreezeSampleDist;
                double inv = 1.0 / (2 * d * TerrainSystem.TerrainThermal.Amplitude);
                double tgx = (TerrainTempFunc(x + d, z, totalHours) - TerrainTempFunc(x - d, z, totalHours)) * inv;
                double tgz = (TerrainTempFunc(x, z + d, totalHours) - TerrainTempFunc(x, z - d, totalHours)) * inv;
                double bwx = SeaBreezeStrength * SeaBreezeGain * tgx;
                double bwz = SeaBreezeStrength * SeaBreezeGain * tgz;
                double bm = Math.Sqrt(bwx * bwx + bwz * bwz);
                if (bm > SeaBreezeMaxWind) { double s = SeaBreezeMaxWind / bm; bwx *= s; bwz *= s; }
                wx += bwx;
                wz += bwz;
            }
        }

        // 4. Orographic interaction with terrain - doesn't touch Cells, so runs outside the lock.
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

    /// <summary>
    /// Radial pressure falloff for one cell, from <see cref="EllipseGeometry.Norm2"/> (1.0 on the cell's own
    /// elliptical boundary). <c>exp(-sqrt(norm2))</c>, NOT the gaussian <c>exp(-norm2)</c> the circulation
    /// terms use: both agree exactly at the boundary (e⁻¹ ≈ 0.37) but this one has a far fatter tail, which
    /// is what makes a system's pressure differential still MEAN something out where its front lines are
    /// actually drawn. A front sits at <c>radius x reach</c> with reach growing to <c>MaxFrontReach</c> = 3,
    /// i.e. norm2 = 9 - under a gaussian that is e⁻⁹ ≈ 0.0001, four orders of magnitude down, so pressure
    /// (and anything derived from it: weather coupling, isobars, map tint, gradient wind) was effectively
    /// dead along a mature front. Here it is e⁻³ ≈ 0.05, ~500x stronger.
    ///
    /// The reach ↔ level identity <c>FrontComputer</c> relies on follows this function: the contour at
    /// <c>radius x reach</c> sits at <c>|Strength| * PressureFalloff(reach²)</c> = <c>|Strength| * e^-reach</c>.
    /// Keep the two in step if this is ever retuned.
    /// </summary>
    public static double PressureFalloff(double norm2) => Math.Exp(-Math.Sqrt(norm2));

    /// <summary>
    /// Accumulated wind drift of the cloud/precipitation texture (blocks) at <paramref name="totalHours"/>,
    /// extrapolated linearly from the last synced snapshot exactly as cell positions are. Server-integrated
    /// (see <c>WindFieldSimulation.AccumulateCloudDrift</c>) and synced, so both sides agree - which they
    /// must, or the Doppler overlay would show rain where none falls.
    ///
    /// Used by <c>WeatherCoupling</c>'s GetRainCloudness prefix to offset the sample position, making
    /// vanilla's precipitation noise advect with the real wind. Vanilla's own field barely moves: its noise
    /// is sampled at <c>posX/18 + totalDays*18</c> with no Z term at all, so the pattern slides due WEST at
    /// a hardcoded 324 blocks/day and can never move north/south, regardless of wind - ~30x slower than
    /// cells advect (AdvectionSpeed 400 blocks/HOUR) and in a fixed direction.
    /// </summary>
    public void CloudDriftAt(double totalHours, out double driftX, out double driftZ)
    {
        double elapsed = totalHours - CloudDriftHours;
        driftX = CloudDriftX + CloudDriftVelX * elapsed;
        driftZ = CloudDriftZ + CloudDriftVelZ * elapsed;
    }

    /// <summary>Accumulated cloud drift as of <see cref="CloudDriftHours"/>, and its current rate - see
    /// <see cref="CloudDriftAt"/>. Carries its OWN timestamp rather than riding on
    /// <see cref="AnchorHours"/>, since the cell anchor moves whenever a cell spawns/expires and the drift
    /// integral must not be re-based by that. The snapshot re-bases it to the anchor on the wire.</summary>
    public double CloudDriftX, CloudDriftZ, CloudDriftVelX, CloudDriftVelZ, CloudDriftHours;

    /// <summary>Signed pressure at a position (sum of cell contributions). + = low, - = high.</summary>
    public double PressureAt(double x, double z, double totalHours) => PressureAt(x, z, totalHours, -1);

    /// <summary>
    /// Pressure from the moving cells ALONE (no terrain thermal term). Used where a deterministic,
    /// side-independent value is required - the precipitation gate (so the gate never re-enters the
    /// climate/thermal path) and the front-blob contour (so fronts derive purely from the synced cells
    /// and persist across reloads exactly as the cells do). + = low, - = high.
    /// </summary>
    public double CellPressureAt(double x, double z, double totalHours) => CellPressureAt(x, z, totalHours, -1);

    /// <summary>
    /// As <see cref="CellPressureAt(double,double,double)"/> but optionally excluding one cell index -
    /// the "background" field induced by every OTHER system. Used by FrontComputer's open-curve march to
    /// find how strongly a nearby neighbour pulls a low's own front toward/around it, independent of the
    /// low's own (locally dominant, radially symmetric) contribution.
    /// </summary>
    public double CellPressureAt(double x, double z, double totalHours, int excludeCell)
    {
        lock (CellsLock)
        {
            double p = 0;
            double elapsed = totalHours - AnchorHours;
            for (int i = 0; i < Cells.Count; i++)
            {
                if (i == excludeCell) continue;
                PressureCell cell = Cells[i];
                double dx = x - (cell.X + cell.VelX * elapsed);
                double dz = z - (cell.Z + cell.VelZ * elapsed);
                p += cell.Strength * PressureFalloff(EllipseGeometry.Norm2(cell, dx, dz));
            }
            return p;
        }
    }

    /// <summary>
    /// Falloff+<see cref="PressureCell.StormLikelihood"/>-weighted share of nearby lows' influence at a
    /// position, bucketed by which weather REGIME each contributing low is currently in - Occluded (its
    /// cold front has caught its warm front), Stationary (near-zero own velocity), or Moving (split
    /// Warm/Cold by real geography, same leading/trailing convention <see cref="MovingFrontAngles"/> uses
    /// for the front lines themselves). Meant to answer "what KIND of front is this" for intensity
    /// weighting (see WeatherCoupling's per-cell weather-noise texture), not to trace the
    /// precise spatial extent of the storm band itself.
    /// </summary>
    public struct FrontRegimeBias
    {
        public double Cold;
        public double Warm;
        public double Occluded;
        public double Stationary;
    }

    /// <summary>Half-width (x a low's own <see cref="PressureCell.Radius"/>) of the smooth Warm/Cold
    /// blend band in <see cref="FrontRegimeBiasAt"/>, centred on the low's own latitude line - a plain
    /// sign test there switched the weather-intensity multiplier discontinuously right at that line,
    /// reading as an unrealistically sharp jump between stormy and calm weather. <c>Math.Tanh</c> over
    /// this width gives a gradual apportionment between the two instead.</summary>
    private const double RegimeBlendWidthFactor = 0.6;

    /// <summary>See <see cref="FrontRegimeBias"/>. Real fronts trail a LOW; highs contribute nothing.</summary>
    public FrontRegimeBias FrontRegimeBiasAt(double x, double z, double totalHours)
    {
        lock (CellsLock)
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
                double falloff = Math.Exp(-EllipseGeometry.Norm2(c, dx, dz));
                if (falloff < 1e-4) continue;

                // Weighted by the cell's own pressure strength too (same pattern as CellTemperatureAt) -
                // otherwise a barely-significant low sitting at the same distance as an intense one would
                // sway the Cold/Warm/Occluded/Stationary wind-character mix just as much, decorrelating the
                // chosen gust multiplier from how strong the system actually pushing the wind is.
                double weight = falloff * c.StormLikelihood * Math.Max(0.2, Math.Abs(c.Strength));

                if (totalHours < c.OccludedUntilTotalHours) { result.Occluded += weight; continue; }

                double speed = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);
                if (speed < StationarySpeed) { result.Stationary += weight; continue; }

                // Moving open-wave low: warm = leading, cold = trailing - the SAME MovingFrontAngles axes
                // FrontComputer's dual front lines and StormBiasAt use, apportioned smoothly by how much
                // closer this position projects toward one axis than the other (Tanh over a band straddling
                // the midline between them) rather than switched abruptly.
                MovingFrontAngles(c.VelX, c.VelZ, cz, out double warmAngle, out double coldAngle);
                double warmAlong = dx * Math.Cos(warmAngle) + dz * Math.Sin(warmAngle);
                double coldAlong = dx * Math.Cos(coldAngle) + dz * Math.Sin(coldAngle);
                double blendWidth = Math.Max(1.0, c.Radius) * RegimeBlendWidthFactor;
                double warmFrac = 0.5 * (1.0 + Math.Tanh((warmAlong - coldAlong) / blendWidth));
                result.Warm += weight * warmFrac;
                result.Cold += weight * (1.0 - warmFrac);
            }
            return result;
        }
    }

    /// <summary>
    /// Falloff-weighted blend of nearby cells' intrinsic <see cref="PressureCell.AirMassTemp"/> in
    /// [-1, 1] - a cold low and a warm high can both exist, so this is deliberately independent of
    /// pressure sign. Weighted by each cell's footprint falloff x |Strength| (stronger systems dominate
    /// their own airmass more). Returns 0 (neutral) where no cell has meaningful influence. Used by
    /// DynamicWindModCommon.OnGetClimate to shift real temperature toward the local air mass's character.
    /// </summary>
    public double CellTemperatureAt(double x, double z, double totalHours)
    {
        lock (CellsLock)
        {
            double wSum = 0, tSum = 0;
            double elapsed = totalHours - AnchorHours;
            for (int i = 0; i < Cells.Count; i++)
            {
                PressureCell cell = Cells[i];
                double dx = x - (cell.X + cell.VelX * elapsed);
                double dz = z - (cell.Z + cell.VelZ * elapsed);
                double falloff = Math.Exp(-EllipseGeometry.Norm2(cell, dx, dz));
                if (falloff < 1e-4) continue;
                double w = falloff * Math.Max(0.05, Math.Abs(cell.Strength));
                wSum += w;
                tSum += w * cell.AirMassTemp;
            }
            return wSum > 1e-6 ? tSum / wSum : 0.0;
        }
    }

    // Water-proximity ring-sample radii (blocks), nearest first - mirrors the style of the orographic
    // consts above (named, tuned by feel) rather than a per-block flood search, which would be far too
    // expensive on the same hot path WindAt runs on. A ring with water anywhere on it counts as "found
    // at this radius"; closer rings dominate the falloff below.
    private static readonly double[] WaterProximityRadii = { 32.0, 96.0, 200.0 };
    private static readonly double[] WaterProximityWeights = { 1.0, 0.6, 0.3 };
    private const int WaterProximitySamplesPerRing = 8;

    /// <summary>
    /// 0..1 "closeness to open water" at (x, z), sampled from the already-wired <see cref="TerrainHeightFunc"/>
    /// against <see cref="SeaLevel"/> - no per-block flood search. Samples a small ring of points at a few
    /// increasing radii; a ring with any sample below sea level contributes its weight (nearer rings weigh
    /// more), so coasts/lakes/wide rivers register without needing exact distance-to-shore. 0 if
    /// <see cref="TerrainHeightFunc"/> isn't wired up yet.
    /// </summary>
    public double WaterProximityAt(double x, double z)
    {
        Func<double, double, double>? h = TerrainHeightFunc;
        if (h == null) return 0.0;

        double best = 0.0;
        for (int r = 0; r < WaterProximityRadii.Length; r++)
        {
            double radius = WaterProximityRadii[r];
            bool found = false;
            for (int s = 0; s < WaterProximitySamplesPerRing; s++)
            {
                double angle = 2.0 * Math.PI * s / WaterProximitySamplesPerRing;
                double sx = x + radius * Math.Cos(angle);
                double sz = z + radius * Math.Sin(angle);
                if (h(sx, sz) < SeaLevel) { found = true; break; }
            }
            if (found && WaterProximityWeights[r] > best) best = WaterProximityWeights[r];
        }
        return best;
    }

    // Fine noise overlay salt: offsets so HumidityAt's Meander call doesn't correlate 1:1 with the
    // jetstream's own Meander(x, z, totalHours, MeanderFrequency) call.
    private const double HumidityNoiseSpatialSalt = 1.7;
    private const double HumidityNoiseTimeSalt = 0.35;
    private const double HumidityNoiseFreq = 1.3;

    /// <summary>
    /// Humidity at (x, z, totalHours) in [0, 1]: a terrain-noise baseline (worldgen rainfall map, via
    /// <see cref="TerrainHumidityFunc"/>) plus a water-proximity boost (<see cref="WaterProximityAt"/>),
    /// a weather-system overlay (lows converge moist air and bring storms, highs are dry subsidence - the
    /// same <see cref="PressureAt(double,double,double)"/> sign convention as everywhere else: + low / -
    /// high), and a fine deterministic noise term reusing <see cref="Meander"/> at a different scale/time
    /// salt than the jetstream's own call. Deterministic from already-synced state, same as
    /// <see cref="PressureAt(double,double,double)"/>/<see cref="CellTemperatureAt"/> - no separate network
    /// sync needed.
    /// </summary>
    public double HumidityAt(double x, double z, double totalHours)
    {
        double baseline = TerrainHumidityFunc?.Invoke(x, z, totalHours) ?? 0.5;
        double waterBoost = WaterProximityAt(x, z);
        double pressureTerm = PressureAt(x, z, totalHours);
        if (pressureTerm > 1.5) pressureTerm = 1.5; else if (pressureTerm < -1.5) pressureTerm = -1.5;
        double noise = Meander(x * HumidityNoiseSpatialSalt, z * HumidityNoiseSpatialSalt, totalHours * HumidityNoiseTimeSalt, HumidityNoiseFreq);

        double h = baseline
            + HumidityWaterProximityStrength * waterBoost
            + HumidityPressureStrength * pressureTerm
            + HumidityNoiseAmplitude * noise;

        return h < 0.0 ? 0.0 : (h > 1.0 ? 1.0 : h);
    }

    /// <summary>
    /// As <see cref="PressureAt(double,double,double)"/> but optionally excluding one cell index (used
    /// when advecting that cell on the background flow). Horizontal pressure is now purely the moving
    /// cells - the terrain thermal signal is a TEMPERATURE field (<see cref="TerrainTempFunc"/>) driving
    /// the sea breeze directly in <see cref="WindAt"/>, not a terrain-pinned pressure baseline.
    /// </summary>
    private double PressureAt(double x, double z, double totalHours, int excludeCell)
        => CellPressureAt(x, z, totalHours, excludeCell);

    /// <summary>
    /// Signed pressure including the vertical lapse: real air pressure falls with altitude, read here
    /// as "more low" (+). Below sea level there is no change; at the build ceiling it reaches
    /// +<see cref="AltitudeMaxDrop"/>. This term has no horizontal gradient, so it never alters wind -
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
