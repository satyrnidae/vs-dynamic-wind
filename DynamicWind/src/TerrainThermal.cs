using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Terrain-driven thermal pressure: warm, sun-exposed dry land creates low pressure (rising air),
/// cool water creates high pressure (sinking air). Computed from the world's surface height + climate
/// at a position, modulated by time of day. Same code runs on server and client (each passes its own
/// <see cref="IWorldAccessor"/>); far/ungenerated columns return 0 (neutral).
///
/// Sign convention matches <see cref="WindField"/>: + = low pressure, − = high pressure.
/// </summary>
internal static class TerrainThermal
{
    private const double Amplitude = 1.2;

    public static double PressureAt(IWorldAccessor world, double x, double z, double totalHours)
    {
        int ix = (int)x;
        int iz = (int)z;

        int surfaceY = world.BlockAccessor.GetRainMapHeightAt(ix, iz);
        if (surfaceY <= 0) return 0.0; // ungenerated/void → neutral

        int seaLevel = world.SeaLevel;
        var pos = new BlockPos(ix, surfaceY, iz, 0);
        ClimateCondition cc = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, totalHours / 24.0);
        if (cc == null) return 0.0;

        double diurnal = DiurnalFactor(world); // 0 at night, 1 at solar noon
        bool water = surfaceY <= seaLevel;     // surface at/below sea level → water/coast

        double coeff, mag;
        if (water)
        {
            // Water cools the air above it and has high thermal inertia → steady high pressure.
            coeff = -1.0;
            mag = 0.6;
        }
        else
        {
            // Dry, exposed ground heats most; wet/forested ground less. Warmer + sunnier = stronger low.
            double aridity = 1.0 - GameMath.Clamp(cc.Rainfall, 0f, 1f);
            double tempFactor = GameMath.Clamp((cc.Temperature + 5.0) / 30.0, 0.0, 1.0); // -5..25°C → 0..1
            coeff = 0.2 + 0.8 * aridity;          // low pressure (positive)
            mag = (0.3 + 0.7 * diurnal) * (0.3 + 0.7 * tempFactor);
        }

        return coeff * mag * Amplitude;
    }

    /// <summary>0 at night, peaks at ~solar noon (sine over the daylight half).</summary>
    private static double DiurnalFactor(IWorldAccessor world)
    {
        double hour = world.Calendar.HourOfDay; // 0..24
        double s = Math.Sin((hour - 6.0) / 12.0 * Math.PI);
        return s < 0 ? 0 : s;
    }
}
