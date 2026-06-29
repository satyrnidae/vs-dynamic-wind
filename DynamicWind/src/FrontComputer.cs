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
/// A front line on a pressure system's active edge. <see cref="DirX"/>/<see cref="DirZ"/> is the unit
/// direction the line runs (along the isobar); <see cref="AxisX"/>/<see cref="AxisZ"/> points toward the
/// high-pressure side (the curvature-offset direction and the side front rain forms on);
/// <see cref="Length"/> is in blocks.
/// </summary>
public struct FrontLine
{
    public double MidX, MidZ;
    public double DirX, DirZ;
    public double AxisX, AxisZ;
    public double Length;
    public FrontType Type;
    public double Seed;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a pure,
/// deterministic function of the field, so both sides compute the same fronts and they survive world
/// reloads exactly as the cells do (the cells persist, so the fronts do). For each significant system a
/// front is anchored on its ACTIVE edge — the leading edge of a low (its direction of travel) or the
/// trailing edge of a high — at a fixed offset along that edge, which is where precipitation forms. The
/// line runs along the local isobar (perpendicular to the pressure gradient); its high-pressure side
/// (used by <see cref="WeatherCoupling"/> for front rain) is the up-gradient direction, and it is
/// classified cold/warm/stationary by how that axis lines up with the jetstream. Markers (H/L) are
/// emitted for every significant cell.
///
/// NOTE: front placement deliberately does NOT sample the live weather precipitation. That precipitation
/// is stateful and side-dependent (the server's includes the front-rain feedback; the client has no
/// coupling and re-syncs weather on reload), so driving placement from it made the fronts jump and change
/// on every reload even though the cells were unchanged. Pressure → precipitation is already monotonic
/// (see <see cref="WindField.PrecipGate"/>, used by the radar), so anchoring to the pressure systems keeps
/// the fronts co-located with where it rains while staying perfectly reproducible.
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.15f; // mark/draw systems down to this |strength| (incl. decayed highs)
    private const double FrontLengthFactor = 1.4;  // × radius — span of the front line
    private const double FrontEdgeOffset = 0.7;    // × radius — how far out along the active edge the front sits
    private const double StationaryDot = 0.4;      // |axis · jet| below this ⇒ stationary
    private const double GradSampleDist = 64.0;    // pressure-gradient sample spacing for the high-side axis

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

            // Motion direction (advection); fall back to the local jetstream bearing if near-stationary.
            double mdx = c.VelX, mdz = c.VelZ;
            double ml = Math.Sqrt(mdx * mdx + mdz * mdz);
            if (ml < 1e-6)
            {
                double a0 = field.MeanderAmplitude * WindField.Meander(ex, ez, totalHours)
                          + field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
                mdx = Math.Cos(a0); mdz = Math.Sin(a0); ml = 1;
            }
            mdx /= ml; mdz /= ml;

            // Active edge: a low's LEADING edge (+motion), a high's TRAILING edge (−motion).
            double edx = isHigh ? -mdx : mdx;
            double edz = isHigh ? -mdz : mdz;

            double off = FrontEdgeOffset * c.Radius;
            double midX = ex + edx * off;
            double midZ = ez + edz * off;

            // High-pressure side = up the actual-pressure gradient. Our field stores + = low, so higher
            // real pressure lies along −∇(field pressure).
            double d = GradSampleDist;
            double gx = (field.PressureAt(midX + d, midZ, totalHours) - field.PressureAt(midX - d, midZ, totalHours)) / (2 * d);
            double gz = (field.PressureAt(midX, midZ + d, totalHours) - field.PressureAt(midX, midZ - d, totalHours)) / (2 * d);
            double axX = -gx, axZ = -gz;
            double al = Math.Sqrt(axX * axX + axZ * axZ);
            if (al < 1e-9) { axX = edx; axZ = edz; al = 1; } // flat gradient → fall back to the edge dir
            axX /= al; axZ /= al;

            // Front line runs along the isobar, perpendicular to the high-side axis.
            double dirX = -axZ, dirZ = axX;

            // Classify by how the high-side axis lines up with the jetstream.
            double ang = field.MeanderAmplitude * WindField.Meander(midX, midZ, totalHours)
                       + field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
            double jetX = Math.Cos(ang), jetZ = Math.Sin(ang);
            double dot = axX * jetX + axZ * jetZ;
            FrontType type = dot <= -StationaryDot ? FrontType.Cold
                           : dot >= StationaryDot ? FrontType.Warm
                           : FrontType.Stationary;

            lines.Add(new FrontLine
            {
                MidX = midX,
                MidZ = midZ,
                DirX = dirX,
                DirZ = dirZ,
                AxisX = axX,
                AxisZ = axZ,
                Length = FrontLengthFactor * c.Radius,
                Type = type,
                Seed = c.BornTotalHours
            });
        }
    }

    /// <summary>Smooth, slowly-evolving curvature offset (blocks-scale, in [-~1.5, ~1.5]) along the front.</summary>
    public static double Curve(double u, double seed, double totalHours)
    {
        return Math.Sin(u * 0.0025 + seed + totalHours * 0.05)
             + 0.5 * Math.Sin(u * 0.006 - seed * 1.3 + totalHours * 0.03);
    }
}
