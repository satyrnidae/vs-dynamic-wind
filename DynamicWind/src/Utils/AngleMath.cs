using System;

namespace DynamicWind.Utils;

/// <summary>Shared angle-interpolation helpers used by both the simulation and map rendering.</summary>
internal static class AngleMath
{
    /// <summary>Shortest signed angular distance from <paramref name="from"/> to <paramref name="to"/>
    /// (radians, wrapped through whichever direction is closer).</summary>
    public static double AngleDiff(double from, double to) => Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));

    /// <summary>Shortest-path angle interpolation: eases <paramref name="from"/> toward <paramref name="to"/>
    /// by fraction <paramref name="t"/>, wrapping through whichever direction is closer.</summary>
    public static double AngleLerp(double from, double to, double t) => from + AngleDiff(from, to) * t;

    /// <summary>Float overload of <see cref="AngleLerp(double, double, double)"/>.</summary>
    public static float AngleLerp(float from, float to, float t)
        => (float)AngleLerp((double)from, to, t);
}
