using System;

namespace DynamicWind.Utils;

/// <summary>Shared angle-interpolation helpers used by both the simulation and map rendering.</summary>
internal static class AngleMath
{
    /// <summary>Shortest-path angle interpolation: eases <paramref name="from"/> toward <paramref name="to"/>
    /// by fraction <paramref name="t"/>, wrapping through whichever direction is closer.</summary>
    public static double AngleLerp(double from, double to, double t)
    {
        double diff = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        return from + diff * t;
    }

    /// <summary>Float overload of <see cref="AngleLerp(double, double, double)"/>.</summary>
    public static float AngleLerp(float from, float to, float t)
        => (float)AngleLerp((double)from, to, t);
}
