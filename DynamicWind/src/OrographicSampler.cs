using System;
using AlgernonsTerrainSampler;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Server-side orographic correction. Uses Algernons Terrain Sampler to read terrain height around a
/// position and (a) slow wind over high terrain and (b) deflect it away from upslope gradients, so
/// flow goes around ridges and channels along valleys. Mutates the unit wind direction and strength
/// multiplier in place.
/// </summary>
internal static class OrographicSampler
{
    private const int SampleSpacing = 32; // blocks between gradient samples

    public static void Apply(TerrainSamplerMod sampler, OrographicConfig cfg, Vec3d pos,
        ref double nx, ref double nz, ref double strengthMul)
    {
        int x = (int)pos.X;
        int z = (int)pos.Z;

        double h = sampler.GetBlockColumnHeight(x, z);
        double hxp = sampler.GetBlockColumnHeight(x + SampleSpacing, z);
        double hxn = sampler.GetBlockColumnHeight(x - SampleSpacing, z);
        double hzp = sampler.GetBlockColumnHeight(x, z + SampleSpacing);
        double hzn = sampler.GetBlockColumnHeight(x, z - SampleSpacing);

        // Slow over high terrain.
        if (h > cfg.HeightThreshold)
        {
            double span = Math.Max(1.0, 255.0 - cfg.HeightThreshold);
            double t = (h - cfg.HeightThreshold) / span;
            if (t > 1) t = 1;
            strengthMul *= 1.0 - cfg.SlowdownFactor * t;
        }

        // Uphill gradient (rise per block).
        double gx = (hxp - hxn) / (2.0 * SampleSpacing);
        double gz = (hzp - hzn) / (2.0 * SampleSpacing);
        double glen = Math.Sqrt(gx * gx + gz * gz);
        if (glen < 1e-5 || cfg.DeflectionStrength <= 0) return;

        double gux = gx / glen;
        double guz = gz / glen;

        // Component of wind heading up the slope; remove part of it to flow around/along terrain.
        double uphill = nx * gux + nz * guz;
        if (uphill <= 0) return; // already going downhill/along — leave it

        double k = cfg.DeflectionStrength * Math.Min(1.0, glen * 4.0); // steeper = more deflection
        nx -= k * uphill * gux;
        nz -= k * uphill * guz;

        double nl = Math.Sqrt(nx * nx + nz * nz);
        if (nl < 1e-6) return;
        nx /= nl;
        nz /= nl;
    }
}
