using System;
using System.Collections.Generic;
using DynamicWind.WindSystem;

namespace DynamicWind.Client.MapLayers;

public enum FrontType { Cold, Warm, Stationary }

/// <summary>A pressure-system marker (the "H"/"L" letter) at a cell's current effective centre.</summary>
public struct FrontMarker
{
    public double X;
    public double Z;
    public bool IsHigh;
}

/// <summary>
/// A weather front for one pressure system. A MOVING system draws a leading-edge LINE (the system's
/// active edge in its travel direction, for both lows and highs): <see cref="DirX"/>/<see cref="DirZ"/> runs
/// along the isobar, <see cref="AxisX"/>/<see cref="AxisZ"/> points toward the high-pressure side
/// (curvature direction), <see cref="MidX"/>/<see cref="MidZ"/> is its midpoint and
/// <see cref="Length"/> its span in blocks. A near-STATIONARY system instead encircles itself: then
/// <see cref="Radii"/> (one contour radius per angular bucket around <see cref="CenterX"/>/
/// <see cref="CenterZ"/>) is non-null and the renderer draws a closed loop. <see cref="Type"/> selects
/// the symbol: a moving low is Cold, a moving high is Warm, a stationary system is Stationary.
/// <see cref="Strength"/> (|cell strength|) scales how prominent the front is drawn.
/// </summary>
public struct FrontLine
{
    public double MidX, MidZ;
    public double DirX, DirZ;
    public double AxisX, AxisZ;
    public double Length;
    public FrontType Type;
    public double Seed;
    public float Strength;

    // Stationary systems only: the encircling contour (one radius per angular bucket) around the centre.
    public double CenterX, CenterZ;
    public double[]? Radii;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Every significant cell — low OR high — gets a front. A moving system gets a leading/trailing-edge
/// line anchored on its active edge (where precipitation forms), running along the local isobar and
/// classified cold (moving low) / warm (moving high). A near-stationary system instead gets an
/// encircling closed contour (traced at a fraction of its peak pressure) flagged stationary. Markers
/// (H/L) are emitted for every significant cell.
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.15f; // mark/draw systems down to this |strength|
    private const double FrontLengthFactor = 1.4;  // × radius — span of the front line
    private const double FrontEdgeOffset = 0.7;    // × radius — how far out along the active edge the front sits
    private const double GradSampleDist = 64.0;    // pressure-gradient sample spacing for the high-side axis
    private const double StationarySpeed = 60.0;   // |velocity| (blocks/hour) below which a system is "stationary"

    // Stationary contour tracing.
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
            bool isHigh = !c.IsLow;
            markers.Add(new FrontMarker { X = ex, Z = ez, IsHigh = isHigh });

            float strength = Math.Abs(c.Strength);
            double speed = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);

            if (speed < StationarySpeed)
            {
                // STATIONARY: encircle the whole system with a stationary-front loop traced from the
                // summed cell pressure (so overlapping cells wrap into one blob).
                double centre = field.CellPressureAt(ex, ez, totalHours);
                if (Math.Abs(centre) < MarkerMinStrength) continue;
                double level = ContourFrac * centre;          // same sign as centre
                double maxR = MaxContourRadiusFactor * c.Radius;

                var radii = new double[AngleSamples];
                for (int a = 0; a < AngleSamples; a++)
                {
                    double theta = a / (double)AngleSamples * 2 * Math.PI;
                    double ct = Math.Cos(theta), st = Math.Sin(theta);
                    double r = maxR;
                    double prevR = 0, prevP = centre;
                    for (int s = 1; s <= MarchSteps; s++)
                    {
                        double rr = maxR * s / MarchSteps;
                        double p = field.CellPressureAt(ex + ct * rr, ez + st * rr, totalHours);
                        // Cross the contour: |pressure| falls through |level| (works for highs too).
                        if (Math.Abs(p) <= Math.Abs(level))
                        {
                            double frac = (Math.Abs(prevP) - Math.Abs(level)) / Math.Max(1e-6, Math.Abs(prevP) - Math.Abs(p));
                            r = prevR + (rr - prevR) * frac;
                            break;
                        }
                        prevR = rr; prevP = p;
                    }
                    radii[a] = r;
                }

                lines.Add(new FrontLine
                {
                    Type = FrontType.Stationary,
                    Seed = c.BornTotalHours,
                    Strength = strength,
                    CenterX = ex,
                    CenterZ = ez,
                    Radii = radii
                });
                continue;
            }

            // MOVING: leading-edge (low) / trailing-edge (high) line.
            double mdx = c.VelX, mdz = c.VelZ;
            double ml = Math.Sqrt(mdx * mdx + mdz * mdz);
            mdx /= ml; mdz /= ml;

            // Active edge: the leading edge in the system's direction of travel (+motion) for both —
            // a cold front is the advancing edge of a high pushing into new territory, same as a
            // warm front leads a moving low.
            double edx = mdx;
            double edz = mdz;

            double off = FrontEdgeOffset * c.Radius;
            double midX = ex + edx * off;
            double midZ = ez + edz * off;

            // High-pressure side = up the actual-pressure gradient. Our field stores + = low, so higher
            // real pressure lies along −∇(cell pressure). Cells-only so it stays reload-stable.
            double d = GradSampleDist;
            double gx = (field.CellPressureAt(midX + d, midZ, totalHours) - field.CellPressureAt(midX - d, midZ, totalHours)) / (2 * d);
            double gz = (field.CellPressureAt(midX, midZ + d, totalHours) - field.CellPressureAt(midX, midZ - d, totalHours)) / (2 * d);
            double axX = -gx, axZ = -gz;
            double al = Math.Sqrt(axX * axX + axZ * axZ);
            if (al < 1e-9) { axX = edx; axZ = edz; al = 1; } // flat gradient → fall back to the edge dir
            axX /= al; axZ /= al;

            // Front line runs along the isobar, perpendicular to the high-side axis.
            double dirX = -axZ, dirZ = axX;

            // Stronger systems draw longer, more prominent fronts.
            double length = FrontLengthFactor * c.Radius * (0.6 + 0.4 * Math.Min(1.0, strength / 3.0));

            lines.Add(new FrontLine
            {
                MidX = midX,
                MidZ = midZ,
                DirX = dirX,
                DirZ = dirZ,
                AxisX = axX,
                AxisZ = axZ,
                Length = length,
                Type = isHigh ? FrontType.Cold : FrontType.Warm, // moving high = cold symbol, moving low = warm symbol
                Seed = c.BornTotalHours,
                Strength = strength
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

    /// <summary>Smooth, slowly-evolving curvature offset (in [-~1.5, ~1.5]) along the front.</summary>
    public static double Curve(double u, double seed, double totalHours)
    {
        return Math.Sin(u * 0.0025 + seed + totalHours * 0.05)
             + 0.5 * Math.Sin(u * 0.006 - seed * 1.3 + totalHours * 0.03);
    }
}
