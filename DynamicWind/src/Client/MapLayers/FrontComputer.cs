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
/// A weather front for one LOW pressure system (highs carry no fronts — see FrontComputer), traced
/// from a march-to-contour geometry (<see cref="Radii"/>, one radius per angular bucket around
/// <see cref="CenterX"/>/<see cref="CenterZ"/>). A near-STATIONARY or OCCLUDED low encircles itself
/// with the full closed loop. A MOVING, open-wave low instead produces two half-loop entries sharing
/// the same contour, split by real geography rather than direction of travel: a Warm front on the
/// equatorward ~180° (centred on <see cref="LeadAngle"/> — where warm air overruns) and a Cold front
/// on the poleward ~180° (its own <see cref="LeadAngle"/>, the opposite pole-pointing direction — where
/// cold air digs in). <see cref="Strength"/> (|cell strength|) scales how prominent the front is drawn.
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
/// Only LOW cells carry fronts — a real high is fair-weather subsidence with no frontal boundary of its
/// own. A moving low gets BOTH a warm front (its equatorward half, where warm air overruns) and a cold
/// front (its poleward half, where cold air digs in) from the same traced contour — split by real
/// geography (<see cref="WindField.PolewardZSign"/>), not the system's own direction of travel, since a
/// front's warm/cold sides are determined by which air mass is on which side of the boundary, not by
/// which way the low happens to be moving. A near-stationary low instead renders the full encircling
/// contour, flagged stationary, and a low mid-occlusion (see WindFieldSimulation.ApplyOcclusion) renders
/// the full contour as the occluded symbol. Highs still get an H marker, just no front line. Markers
/// (H/L) are emitted for every significant cell.
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
            markers.Add(new FrontMarker { X = ex, Z = ez, IsHigh = !c.IsLow });

            if (!c.IsLow) continue; // highs are fair weather — marker only, no front

            float strength = Math.Abs(c.Strength);
            double speed = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);

            double centre = field.CellPressureAt(ex, ez, totalHours);
            if (Math.Abs(centre) < MarkerMinStrength) continue;
            double maxR = MaxContourRadiusFactor * c.Radius;
            double[] radii = TraceContour(field, ex, ez, centre, maxR, totalHours);
            // Independently stretch the poleward (cold) and equatorward (warm) sides by the cell's own
            // evolving reach — see PressureCell.ColdFrontReach/WarmFrontReach — so the two sides visibly
            // move/expand at different rates instead of a single symmetric contour.
            radii = ApplyFrontReach(radii, field, ez, c.ColdFrontReach, c.WarmFrontReach);

            if (totalHours < c.OccludedUntilTotalHours)
            {
                // OCCLUDED: the cold front has caught the warm front — render one merged closed loop.
                lines.Add(new FrontLine
                {
                    Type = FrontType.Occluded,
                    Seed = c.BornTotalHours,
                    Strength = strength,
                    CenterX = ex,
                    CenterZ = ez,
                    Radii = radii
                });
                continue;
            }

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

            // MOVING, open wave: split by real geography, not the system's own direction of travel — the
            // cold front sits on the POLEWARD half (cold air digs in behind/above), the warm front on
            // the EQUATORWARD half (warm air overruns ahead/below), both from the same contour. See
            // WindField.PolewardZSign.
            double poleAngle = field.PolewardZSign(ez) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;

            lines.Add(new FrontLine
            {
                Type = FrontType.Warm,
                Seed = c.BornTotalHours,
                Strength = strength,
                CenterX = ex,
                CenterZ = ez,
                Radii = radii,
                LeadAngle = poleAngle + Math.PI // equatorward
            });
            lines.Add(new FrontLine
            {
                Type = FrontType.Cold,
                Seed = c.BornTotalHours,
                Strength = strength,
                CenterX = ex,
                CenterZ = ez,
                Radii = radii,
                LeadAngle = poleAngle // poleward
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

    /// <summary>Scales each angle bucket of a traced contour by <paramref name="coldReach"/> on the
    /// poleward side or <paramref name="warmReach"/> on the equatorward side (relative to
    /// <see cref="WindField.PolewardZSign"/> at the cell's own position), so a low's cold and warm sides
    /// can independently stretch as PressureCell.ColdFrontReach/WarmFrontReach evolve.</summary>
    private static double[] ApplyFrontReach(double[] radii, WindField field, double ez, float coldReach, float warmReach)
    {
        double poleSign = field.PolewardZSign(ez);
        var scaled = new double[radii.Length];
        for (int a = 0; a < radii.Length; a++)
        {
            double theta = a / (double)radii.Length * 2 * Math.PI;
            bool polewardSide = Math.Sin(theta) * poleSign >= 0;
            scaled[a] = radii[a] * (polewardSide ? coldReach : warmReach);
        }
        return scaled;
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
