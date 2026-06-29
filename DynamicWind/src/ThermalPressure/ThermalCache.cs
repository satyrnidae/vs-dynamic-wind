using System;
using System.Collections.Generic;
using DynamicWind.WindSystem;

namespace DynamicWind.ThermalPressure;

/// <summary>
/// Coarse, time-bucketed cache around the (expensive) terrain-thermal pressure function. Thermal
/// pressure varies slowly in space and time, but <see cref="WindField.WindAt"/> samples it 4× per
/// call (numeric pressure gradient) on a hot path, and the underlying lookup calls <c>GetClimateAt</c>.
/// Quantizing to a block tile + a game-hour bucket turns almost every sample into an O(1) dictionary
/// hit, which also avoids repeatedly re-entering the weather-coupling <c>OnGetClimate</c> handler.
/// Thread-safe (item physics may query wind off the main thread).
/// </summary>
internal sealed class ThermalCache
{
    private readonly Func<double, double, double, double> inner;
    private readonly double tile;
    private readonly double bucketHours;
    private readonly Dictionary<long, double> map = new();
    private readonly object sync = new();
    private double curBucket = double.NaN;

    public ThermalCache(Func<double, double, double, double> inner, double tile = 32.0, double bucketHours = 0.1)
    {
        this.inner = inner;
        this.tile = tile;
        this.bucketHours = bucketHours;
    }

    public double Get(double x, double z, double totalHours)
    {
        double bucket = Math.Floor(totalHours / bucketHours);
        long kx = (long)Math.Floor(x / tile);
        long kz = (long)Math.Floor(z / tile);
        long key = (kx & 0xFFFFFFFFL) | ((kz & 0xFFFFFFFFL) << 32);

        lock (sync)
        {
            if (bucket != curBucket)
            {
                map.Clear();
                curBucket = bucket;
            }
            if (map.TryGetValue(key, out double v)) return v;
        }

        double sampled = inner((kx + 0.5) * tile, (kz + 0.5) * tile, totalHours);

        lock (sync)
        {
            map[key] = sampled;
        }
        return sampled;
    }
}
