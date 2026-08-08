using System;

namespace DynamicWind.Utils;

/// <summary>Shared 2D (X/Z, horizontal-plane) vector helpers used across the simulation and front tracing.</summary>
internal static class Vec2Math
{
    /// <summary>Normalizes (x, z) in place; leaves both at 0 if the vector is too near zero to normalize
    /// safely (below <paramref name="minLength"/>).</summary>
    public static bool TryNormalize(ref double x, ref double z, double minLength = 1e-6)
    {
        double len = Math.Sqrt(x * x + z * z);
        if (len < minLength) { x = 0; z = 0; return false; }
        x /= len;
        z /= len;
        return true;
    }
}
