using System;
using DynamicWind.WindSystem;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind.TerrainSystem;

/// <summary>
/// Terrain-driven thermal pressure: warm, sun-exposed dry land creates low pressure (rising air), always.
/// Water's sign is SEASONAL: warm-season air over cool water is stabilizing → high pressure (sinking air,
/// the marine-layer/sea-breeze pattern); cold-season air over relatively warm water is destabilizing → low
/// pressure (real nor'easters, polar lows, autumn/winter lake-effect systems) — both are genuine synoptic
/// phenomena. Computed from the world's terrain (seabed) height + climate/season at a position, modulated
/// by time of day. The water pressure contribution scales with water DEPTH (sea level − seabed), so
/// shallow coasts read near-neutral and deep oceans read strong. Same code runs on server and client;
/// far/ungenerated columns return 0 (neutral).
///
/// Day/night: land keeps a substantial baseline at night (stored ground heat) but peaks at solar noon;
/// water's pressure magnitude is STRONGER at night regardless of sign (the air/water thermal contrast is
/// sharpest once the land stops radiating).
///
/// Sign convention matches <see cref="WindField"/>: + = low pressure, − = high pressure.
/// </summary>
internal static class TerrainThermal
{
    private const double Amplitude = 2.6;       // master strength of the terrain pressure signal
    private const double WaterFullDepth = 20.0; // blocks of depth for full pressure contribution
    private const double SummerPeakRel = 0.59725; // matches WindFieldSimulation.SummerPeakRel

    public static double PressureAt(IWorldAccessor world, double x, double z, double totalHours)
    {
        int ix = (int)x;
        int iz = (int)z;

        var pos = new BlockPos(ix, 0, iz, 0);
        // Solid terrain (seabed under water); excludes the water column itself.
        int terrainY = world.BlockAccessor.GetTerrainMapheightAt(pos);
        if (terrainY <= 0) return 0.0; // ungenerated/void → neutral

        int seaLevel = world.SeaLevel;
        double depth = seaLevel - terrainY;
        double diurnal = DiurnalFactor(world); // 0 at night, 1 at solar noon

        if (depth > 0)
        {
            // Water: sign follows the season (see class doc), scaled by depth (shallow → weak, deep →
            // strong), continuously blended so shoulder seasons taper toward neutral instead of a hard
            // switch. MORE pronounced (either sign) at night, once the land stops radiating.
            double depthFactor = depth / WaterFullDepth;
            if (depthFactor > 1) depthFactor = 1;
            double seasonWarmth = SeasonWarmthAt(world, pos); // +1 midsummer, −1 midwinter, 0 if season not ready
            double sign = -seasonWarmth; // summer (+warmth) → high (−); winter (−warmth) → low (+)
            double nightBoost = 1.0 + 0.6 * (1.0 - diurnal); // 1.0 at noon → 1.6 at night
            return sign * (0.6 * depthFactor) * Amplitude * nightBoost;
        }

        // Land: dry, sun-exposed ground heats most; wet/forested less. Warmer + sunnier = stronger low.
        pos.Y = terrainY;
        ClimateCondition cc = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, totalHours / 24.0);
        if (cc == null) return 0.0;

        double aridity = 1.0 - GameMath.Clamp(cc.Rainfall, 0f, 1f);
        double tempFactor = GameMath.Clamp((cc.Temperature + 5.0) / 30.0, 0.0, 1.0); // -5..25°C → 0..1
        double coeff = 0.2 + 0.8 * aridity;                       // low pressure (positive)
        // Stored heat: a solid 0.45 floor at night (ground stays warm) rising to full at solar noon — so
        // land thermals still drive pressure overnight, just less than under the midday sun.
        double mag = (0.45 + 0.55 * diurnal) * (0.3 + 0.7 * tempFactor);
        return coeff * mag * Amplitude;
    }

    /// <summary>True if (x, z) is water (positive sea-level-relative depth) — the same terrain-height check
    /// <see cref="PressureAt"/>'s water branch uses, but independent of the pressure VALUE's sign, since
    /// water can now yield either sign depending on season. Used to restrict locally-formed mobile pressure
    /// cells (thermal seeds / heat-cold-waves in <c>WindFieldSimulation</c>) to water/ocean sites without
    /// inferring land-vs-water from a sign that's no longer reliable. There is no ocean-vs-lake distinction
    /// anywhere in this codebase — this only checks depth relative to sea level — so "over water" is the
    /// closest available proxy for "over ocean". Uncached (unlike <see cref="PressureAt"/>/ThermalCache):
    /// called only a handful of times per rate-limited seed attempt, not on the wind hot path.</summary>
    public static bool IsWater(IWorldAccessor world, double x, double z)
    {
        var pos = new BlockPos((int)x, 0, (int)z, 0);
        int terrainY = world.BlockAccessor.GetTerrainMapheightAt(pos);
        if (terrainY <= 0) return false; // ungenerated/void — treat as not-water
        return world.SeaLevel - terrainY > 0;
    }

    /// <summary>0 at night, peaks at ~solar noon (sine over the daylight half).</summary>
    private static double DiurnalFactor(IWorldAccessor world)
    {
        double hour = world.Calendar.HourOfDay; // 0..24
        double s = Math.Sin((hour - 6.0) / 12.0 * Math.PI);
        return s < 0 ? 0 : s;
    }

    /// <summary>Same cosine seasonal-warmth signal <c>WindFieldSimulation</c> uses for its own independent
    /// purposes (birth air-mass temperature, heat/cold-wave season pick): +1 at that hemisphere's midsummer,
    /// −1 at midwinter. Mutates <paramref name="pos"/>'s Y transiently (GetSeasonRel only needs X/Z, but the
    /// convention elsewhere in this mod is to pass Y = sea level) and restores it before returning, since
    /// callers in <see cref="PressureAt"/> still need the original object afterward.</summary>
    private static double SeasonWarmthAt(IWorldAccessor world, BlockPos pos)
    {
        try
        {
            int savedY = pos.Y;
            pos.Y = world.SeaLevel;
            float seasonRel = world.Calendar.GetSeasonRel(pos);
            pos.Y = savedY;
            return Math.Cos(2.0 * Math.PI * (seasonRel - SummerPeakRel));
        }
        catch
        {
            return 0.0; // season/hemisphere not ready yet — neutral (no seasonal water-sign bias)
        }
    }
}
