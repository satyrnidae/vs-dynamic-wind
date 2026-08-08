using System;
using DynamicWind.WindSystem;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind.TerrainSystem;

/// <summary>
/// Terrain-driven near-surface TEMPERATURE anomaly, in deg C relative to the local ambient: warm, sun-exposed
/// dry land runs warmer than its surroundings, always. Water's sign is SEASONAL: in the warm season water
/// is COOLER than the land beside it (the marine-layer/sea-breeze pattern); in the cold season relatively
/// warm water is WARMER than the frozen land beside it (real nor'easters, polar lows, autumn/winter
/// lake-effect systems). Computed from the world's terrain (seabed) height + climate/season at a position,
/// modulated by time of day. The water contribution scales with water DEPTH (sea level - seabed), so
/// shallow coasts read near-neutral and deep oceans read strong. Same code runs on server and client;
/// far/ungenerated columns return 0 (neutral).
///
/// Day/night: land keeps a substantial baseline at night (stored ground heat) but peaks at solar noon;
/// water's magnitude is STRONGER at night regardless of sign (the air/water thermal contrast is sharpest
/// once the land stops radiating) - which is what reverses the coastal flow into a night-time land breeze.
///
/// This field is TEMPERATURE, not pressure: it is the physically primary quantity in a thermally direct
/// circulation, and it feeds two consumers - the climate temperature shift in
/// <c>DynamicWindModCommon.OnGetClimate</c>, and the sea-breeze wind term in <see cref="WindField.WindAt"/>,
/// which blows DOWN this gradient's opposite (i.e. toward warmer air, into the thermal low). It deliberately
/// no longer feeds <see cref="WindField.PressureAt"/> at all: pinning a pressure system to the ground was
/// unphysical, and pressure is now purely synoptic (moving cells + altitude lapse). See CLAUDE.md.
///
/// Sign convention: + = warmer than ambient, - = cooler.
/// </summary>
internal static class TerrainThermal
{
    /// <summary>Nominal peak magnitude of the anomaly, in deg C - a strong land/water contrast reads about
    /// this far from ambient. Callers that need a normalised [-1, 1] shape divide by it.</summary>
    public const double Amplitude = 6.0;
    private const double WaterFullDepth = 20.0; // blocks of depth for full contribution
    private const double SummerPeakRel = 0.59725; // matches WindFieldSimulation.SummerPeakRel

    /// <summary>Near-surface temperature anomaly in deg C at (x, z). + = warmer than ambient, - = cooler.</summary>
    public static double TemperatureAnomalyAt(IWorldAccessor world, double x, double z, double totalHours)
    {
        int ix = (int)x;
        int iz = (int)z;

        var pos = new BlockPos(ix, 0, iz, 0);
        // Solid terrain (seabed under water); excludes the water column itself.
        int terrainY = world.BlockAccessor.GetTerrainMapheightAt(pos);
        if (terrainY <= 0) return 0.0; // ungenerated/void -> neutral

        int seaLevel = world.SeaLevel;
        double depth = seaLevel - terrainY;
        double diurnal = DiurnalFactor(world); // 0 at night, 1 at solar noon

        if (depth > 0)
        {
            // Water: sign follows the season (see class doc), scaled by depth (shallow -> weak, deep ->
            // strong), continuously blended so shoulder seasons taper toward neutral instead of a hard
            // switch. MORE pronounced (either sign) at night, once the land stops radiating.
            double depthFactor = depth / WaterFullDepth;
            if (depthFactor > 1) depthFactor = 1;
            double seasonWarmth = SeasonWarmthAt(world, pos); // +1 midsummer, -1 midwinter, 0 if season not ready
            double sign = -seasonWarmth; // summer (+warmth) -> water COOLER (-); winter -> water WARMER (+)
            double nightBoost = 1.0 + 0.6 * (1.0 - diurnal); // 1.0 at noon -> 1.6 at night
            return sign * (0.6 * depthFactor) * Amplitude * nightBoost;
        }

        // Land: dry, sun-exposed ground heats most; wet/forested less. Warmer + sunnier = warmer anomaly.
        pos.Y = terrainY;
        ClimateCondition cc = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, totalHours / 24.0);
        if (cc == null) return 0.0;

        double aridity = 1.0 - GameMath.Clamp(cc.Rainfall, 0f, 1f);
        double tempFactor = GameMath.Clamp((cc.Temperature + 5.0) / 30.0, 0.0, 1.0); // -5..25 deg C -> 0..1
        double coeff = 0.2 + 0.8 * aridity;                       // warmer than ambient (positive)
        // Stored heat: a solid 0.45 floor at night (ground stays warm) rising to full at solar noon - so
        // land thermals still drive pressure overnight, just less than under the midday sun.
        double mag = (0.45 + 0.55 * diurnal) * (0.3 + 0.7 * tempFactor);
        return coeff * mag * Amplitude;
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
    /// -1 at midwinter. Mutates <paramref name="pos"/>'s Y transiently (GetSeasonRel only needs X/Z, but the
    /// convention elsewhere in this mod is to pass Y = sea level) and restores it before returning, since
    /// callers pass in their own shared <see cref="BlockPos"/> and still need it unchanged afterward.</summary>
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
            return 0.0; // season/hemisphere not ready yet - neutral (no seasonal water-sign bias)
        }
    }
}
