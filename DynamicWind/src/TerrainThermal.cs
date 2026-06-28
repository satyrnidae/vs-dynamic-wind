using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Terrain-driven thermal pressure: warm, sun-exposed dry land creates low pressure (rising air),
/// cool water creates high pressure (sinking air). Computed from the world's terrain (seabed) height
/// + climate at a position, modulated by time of day. The water high-pressure contribution scales
/// with water DEPTH (sea level − seabed), so shallow coasts read near-neutral and deep oceans read
/// strong high. Same code runs on server and client; far/ungenerated columns return 0 (neutral).
///
/// Sign convention matches <see cref="WindField"/>: + = low pressure, − = high pressure.
/// </summary>
internal static class TerrainThermal
{
    private const double Amplitude = 1.2;
    private const double WaterFullDepth = 20.0; // blocks of depth for full high pressure

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

        if (depth > 0)
        {
            // Water: high pressure, scaled by depth (shallow → weak, deep → strong). Steady (high
            // thermal inertia), so no diurnal modulation.
            double depthFactor = depth / WaterFullDepth;
            if (depthFactor > 1) depthFactor = 1;
            return -1.0 * (0.6 * depthFactor) * Amplitude;
        }

        // Land: dry, sun-exposed ground heats most; wet/forested less. Warmer + sunnier = stronger low.
        pos.Y = terrainY;
        ClimateCondition cc = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, totalHours / 24.0);
        if (cc == null) return 0.0;

        double diurnal = DiurnalFactor(world); // 0 at night, 1 at solar noon
        double aridity = 1.0 - GameMath.Clamp(cc.Rainfall, 0f, 1f);
        double tempFactor = GameMath.Clamp((cc.Temperature + 5.0) / 30.0, 0.0, 1.0); // -5..25°C → 0..1
        double coeff = 0.2 + 0.8 * aridity;                       // low pressure (positive)
        double mag = (0.3 + 0.7 * diurnal) * (0.3 + 0.7 * tempFactor);
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
