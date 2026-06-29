using System;
using System.Collections.Generic;

namespace DynamicWind;

public enum FrontType { Cold, Warm, Stationary }

/// <summary>A pressure-system marker (the "H"/"L" letter) at a cell's current effective centre.</summary>
public struct FrontMarker
{
    public double X;
    public double Z;
    public bool IsHigh;
}

/// <summary>
/// A front drawn as an ENCLOSED LOOP encircling a low-pressure system. The loop is NOT a circle: its
/// radius varies with angle (<see cref="Radii"/>, one entry per angular bucket) so it hugs the actual
/// shape of the low-pressure "blob" — where neighbouring cells merge, the contour bulges out to wrap
/// them. <see cref="MoveX"/>/<see cref="MoveZ"/> is the system's unit travel direction; the renderer
/// uses it to pick warm-front symbols on the leading edge and cold-front symbols on the trailing edge.
/// </summary>
public struct FrontLine
{
    public double CenterX, CenterZ;
    public double[] Radii;        // contour radius per angular bucket (length AngleSamples)
    public double MoveX, MoveZ;   // unit travel direction (leading vs trailing edge classification)
    public double Seed;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Each significant LOW gets a front: a closed contour traced at a fraction of the system's peak
/// pressure. Because the contour follows the summed cell pressure, overlapping cells merge into one
/// larger blob and the outline becomes non-circular. Markers (H/L) are emitted for every significant
/// cell so highs still show on the overlay (highs carry no fronts, matching real surface analysis).
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.15f;     // mark/draw systems down to this |strength|
    private const int AngleSamples = 36;               // contour resolution (every 10°)
    private const double ContourFrac = 0.45;           // trace the loop where pressure = this × the centre value
    private const double MaxContourRadiusFactor = 2.4; // cap the march at this × the cell radius
    private const int MarchSteps = 28;                 // radial samples per angle while tracing

    public static void Compute(WindField field, double totalHours, List<FrontMarker> markers, List<FrontLine> lines)
    {
        markers.Clear();
        lines.Clear();
        if (field.Cells.Count == 0) return;

        double elapsed = totalHours - field.AnchorHours;

        for (int i = 0; i < field.Cells.Count; i++)
        {
            PressureCell c = field.Cells[i];
            if (Math.Abs(c.Strength) < MarkerMinStrength) continue;

            double ex = c.X + c.VelX * elapsed;
            double ez = c.Z + c.VelZ * elapsed;
            markers.Add(new FrontMarker { X = ex, Z = ez, IsHigh = !c.IsLow });

            if (!c.IsLow) continue; // only lows carry fronts

            // Centre pressure (from all cells, so a merged blob reads as one deep low) sets the contour
            // level we trace. If the merged field is too weak here, skip.
            double centre = field.CellPressureAt(ex, ez, totalHours);
            if (centre < MarkerMinStrength) continue;
            double level = ContourFrac * centre;
            double maxR = MaxContourRadiusFactor * c.Radius;

            var radii = new double[AngleSamples];
            for (int a = 0; a < AngleSamples; a++)
            {
                double theta = a / (double)AngleSamples * 2 * Math.PI;
                double ct = Math.Cos(theta), st = Math.Sin(theta);
                // March outward until the summed cell pressure falls through the contour level; linearly
                // interpolate the crossing for a smooth outline. Default to maxR if it never drops.
                double r = maxR;
                double prevR = 0, prevP = centre;
                for (int s = 1; s <= MarchSteps; s++)
                {
                    double rr = maxR * s / MarchSteps;
                    double p = field.CellPressureAt(ex + ct * rr, ez + st * rr, totalHours);
                    if (p <= level)
                    {
                        double frac = (prevP - level) / Math.Max(1e-6, prevP - p);
                        r = prevR + (rr - prevR) * frac;
                        break;
                    }
                    prevR = rr; prevP = p;
                }
                radii[a] = r;
            }

            // Travel direction (advection); fall back to due-east if a cell is momentarily stationary.
            double mdx = c.VelX, mdz = c.VelZ;
            double ml = Math.Sqrt(mdx * mdx + mdz * mdz);
            if (ml < 1e-6) { mdx = 1; mdz = 0; } else { mdx /= ml; mdz /= ml; }

            lines.Add(new FrontLine
            {
                CenterX = ex,
                CenterZ = ez,
                Radii = radii,
                MoveX = mdx,
                MoveZ = mdz,
                Seed = c.BornTotalHours
            });
        }
    }

    /// <summary>Contour radius at an arbitrary angle, linearly interpolated between the sampled buckets.</summary>
    public static double RadiusAt(double[] radii, double theta)
    {
        double frac = theta / (2 * Math.PI);
        frac -= Math.Floor(frac);
        double f = frac * radii.Length;
        int i0 = (int)f;
        int i1 = (i0 + 1) % radii.Length;
        double t = f - i0;
        return radii[i0] * (1 - t) + radii[i1] * t;
    }

    /// <summary>Smooth, slowly-evolving curvature offset (in [-~1.5, ~1.5]) along the front loop.</summary>
    public static double Curve(double u, double seed, double totalHours)
    {
        return Math.Sin(u * 0.0025 + seed + totalHours * 0.05)
             + 0.5 * Math.Sin(u * 0.006 - seed * 1.3 + totalHours * 0.03);
    }
}
