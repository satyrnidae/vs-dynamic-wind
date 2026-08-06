using System;
using DynamicWind.PressureSystem;

namespace DynamicWind.Utils;

/// <summary>
/// Shared elliptical-footprint math for a <see cref="PressureCell"/>: cells are elongated along their
/// direction of travel (<see cref="PressureCell.RadiusMinor"/> along-stream, <see cref="PressureCell.Radius"/>
/// cross-stream, rotated by <see cref="PressureCell.Orientation"/>) rather than circular. Used by both
/// <c>WindField</c> (falloff/gradient sampling) and <c>WindFieldSimulation</c> (despawn-radius checks).
/// </summary>
internal static class EllipseGeometry
{
    /// <summary>Projects a (dx, dz) offset from a cell's centre onto its along-stream/cross-stream axes.</summary>
    private static void ProjectAxes(PressureCell cell, double dx, double dz,
        out double along, out double cross, out double rMinor, out double rMajor,
        out double alongX, out double alongZ, out double crossX, out double crossZ)
    {
        crossX = Math.Cos(cell.Orientation); crossZ = Math.Sin(cell.Orientation);
        alongX = -crossZ; alongZ = crossX;
        along = dx * alongX + dz * alongZ;
        cross = dx * crossX + dz * crossZ;
        rMinor = cell.RadiusMinor > 1e-3f ? cell.RadiusMinor : cell.Radius;
        rMajor = cell.Radius;
    }

    /// <summary>
    /// Elliptical-gaussian normalized squared distance from a cell's centre to a (dx, dz) offset (already
    /// relative to the cell's advected position): 1.0 sits on the 1/e falloff contour. Reduces to the
    /// familiar circular <c>dist²/Radius²</c> when <c>RadiusMinor == Radius</c>.
    /// </summary>
    public static double Norm2(PressureCell cell, double dx, double dz)
    {
        ProjectAxes(cell, dx, dz, out double along, out double cross, out double rMinor, out double rMajor,
            out _, out _, out _, out _);
        double au = along / rMinor;
        double cu = cross / rMajor;
        return au * au + cu * cu;
    }

    /// <summary>
    /// Outward "radial" unit direction at a (dx, dz) offset from a cell's centre, generalized to the
    /// cell's elliptical footprint: the normalized gradient of <see cref="Norm2"/>. Reduces exactly to the
    /// circular radial direction <c>(dx,dz)/dist</c> when <c>RadiusMinor == Radius</c>, so swirl direction
    /// follows the ellipse's edge instead of a plain circle once a cell has elongated.
    /// </summary>
    public static void Radial(PressureCell cell, double dx, double dz, out double nx, out double nz)
    {
        ProjectAxes(cell, dx, dz, out double along, out double cross, out double rMinor, out double rMajor,
            out double alongX, out double alongZ, out double crossX, out double crossZ);
        double gx = along / (rMinor * rMinor) * alongX + cross / (rMajor * rMajor) * crossX;
        double gz = along / (rMinor * rMinor) * alongZ + cross / (rMajor * rMajor) * crossZ;
        double glen = Math.Sqrt(gx * gx + gz * gz);
        if (glen < 1e-9) { nx = crossX; nz = crossZ; }
        else { nx = gx / glen; nz = gz / glen; }
    }

    /// <summary>
    /// The cell's elliptical footprint radius in the direction of (<paramref name="dx"/>, <paramref name="dz"/>)
    /// (a vector from the cell's centre outward, not necessarily normalized) - same ellipse geometry as
    /// <see cref="Norm2"/>, evaluated along this one direction rather than at a point.
    /// </summary>
    public static double RadiusToward(PressureCell cell, double dx, double dz)
        => RadiusToward(cell.Radius, cell.RadiusMinor, cell.Orientation, dx, dz);

    /// <summary>As <see cref="RadiusToward(PressureCell,double,double)"/> but from the raw ellipse
    /// parameters directly, for callers (e.g. <c>FrontMapLayer</c>'s off-screen marker clamp) that only
    /// carry a snapshot of a cell's footprint (<c>FrontMarker</c>) rather than a live <see cref="PressureCell"/>.</summary>
    public static double RadiusToward(double radius, double radiusMinor, double orientation, double dx, double dz)
    {
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist < 1e-6) return radius;
        double crossX = Math.Cos(orientation), crossZ = Math.Sin(orientation);
        double alongX = -crossZ, alongZ = crossX;
        double ndx = dx / dist, ndz = dz / dist;
        double along = ndx * alongX + ndz * alongZ;
        double cross = ndx * crossX + ndz * crossZ;
        double rMinor = radiusMinor > 1e-3 ? radiusMinor : radius;
        double rMajor = radius;
        double f = (along * along) / (rMinor * rMinor) + (cross * cross) / (rMajor * rMajor);
        return f > 1e-9 ? 1.0 / Math.Sqrt(f) : rMajor;
    }
}
