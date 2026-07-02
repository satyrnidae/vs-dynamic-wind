using System;
using System.Collections.Generic;
using DynamicWind.WindSystem;

namespace DynamicWind.Client.MapLayers;

public enum FrontType { Cold, Warm, Stationary, Occluded }

/// <summary>A pressure-system marker (the "H"/"L" letter) at a cell's current effective centre.</summary>
public struct FrontMarker
{
    public double X;
    public double Z;
    public bool IsHigh;
}

/// <summary>
/// A weather front for one pressure system, traced from the same march-to-contour geometry
/// (<see cref="Radii"/>, one radius per angular bucket around <see cref="CenterX"/>/
/// <see cref="CenterZ"/>) for both kinds of system. A near-STATIONARY system encircles itself with
/// the full closed loop. A MOVING system instead renders only the leading half of that same contour
/// — the ~180° slice centred on <see cref="LeadAngle"/> (its smoothed direction of travel) — since
/// that's the system's active edge (where precipitation forms), for both lows and highs.
/// <see cref="Type"/> selects the symbol: a moving low is Cold, a moving high is Warm, a stationary
/// system is Stationary. <see cref="Strength"/> (|cell strength|) scales how prominent the front is
/// drawn.
/// </summary>
public struct FrontLine
{
    public FrontType Type;
    public double Seed;
    public float Strength;

    public double CenterX, CenterZ;
    public double[]? Radii;

    // Moving systems only: the contour is rendered within ±90° of this smoothed travel bearing.
    public double LeadAngle;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Every significant cell — low OR high — gets a front, traced at a fraction of its peak pressure. A
/// moving system renders the leading half of that contour (its active edge, where precipitation
/// forms), classified cold (moving low) / warm (moving high). A near-stationary system instead
/// renders the full encircling contour, flagged stationary. Markers (H/L) are emitted for every
/// significant cell.
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.15f; // mark/draw systems down to this |strength|
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

            // Both branches trace the same march-to-contour geometry from the summed cell pressure
            // (so overlapping cells wrap into one blob); only the rendered slice differs.
            double centre = field.CellPressureAt(ex, ez, totalHours);
            if (Math.Abs(centre) < MarkerMinStrength) continue;
            double maxR = MaxContourRadiusFactor * c.Radius;
            double[] radii = TraceContour(field, ex, ez, centre, maxR, totalHours);

            if (speed < StationarySpeed)
            {
                // STATIONARY: encircle the whole system with a closed loop.
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

            // MOVING: render only the leading half of the contour, centred on the system's smoothed
            // direction of travel — its active edge, where precipitation forms, for both lows and
            // highs. Uses the eased/frozen Orientation (not raw VelX/VelZ) so a numerically noisy
            // instantaneous velocity can't flip the rendered line by ~180° tick to tick.
            double bearing = c.Orientation - Math.PI / 2.0; // Orientation = bearing + π/2 (set at spawn/ease)

            // A cold (high) front that just occluded a warm front renders as the occluded symbol for a
            // while (WindFieldSimulation.MergeAndDisplace sets OccludedUntilTotalHours) instead of its
            // normal cold-front symbol.
            FrontType type = isHigh && totalHours < c.OccludedUntilTotalHours ? FrontType.Occluded
                : isHigh ? FrontType.Cold : FrontType.Warm; // moving high = cold symbol, moving low = warm symbol

            lines.Add(new FrontLine
            {
                Type = type,
                Seed = c.BornTotalHours,
                Strength = strength,
                CenterX = ex,
                CenterZ = ez,
                Radii = radii,
                LeadAngle = bearing
            });
        }
    }

    /// <summary>Marches outward from (ex,ez) at each of <see cref="AngleSamples"/> angles until the
    /// summed cell pressure falls through <see cref="ContourFrac"/>×centre, returning one radius per
    /// angle (capped at maxR).</summary>
    private static double[] TraceContour(WindField field, double ex, double ez, double centre, double maxR, double totalHours)
    {
        double level = ContourFrac * centre; // same sign as centre
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
        return radii;
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
