using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

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
/// A front line drawn at the boundary between a paired low and high. <see cref="DirX"/>/<see cref="DirZ"/>
/// is the unit direction the line runs; <see cref="AxisX"/>/<see cref="AxisZ"/> points from the low toward
/// the high (the curvature-offset direction); <see cref="Length"/> is in blocks.
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
/// Derives weather fronts purely from the (already-synced) pressure-cell field, so no extra netcode is
/// needed — the client recomputes the same fronts from its mirror of the field. Each significant low is
/// paired with its nearest high; the front sits on the boundary between them and is classified by how the
/// low→high axis lines up with the local jetstream (high upwind ⇒ cold front, downwind ⇒ warm, across ⇒
/// stationary). Markers (H/L) are emitted for every significant cell. As the cells advect/disperse on the
/// jetstream and from terrain seeding, the fronts move and evolve with them.
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.25f;
    private const double MaxPairDist = 12000.0;   // furthest a low will reach for a partner high
    private const double FrontLengthFactor = 1.4; // × lowRadius — span of the front line
    private const double StationaryDot = 0.4;     // |L→H · jet| below this ⇒ stationary

    public static void Compute(WindField field, double totalHours, List<FrontMarker> markers, List<FrontLine> lines)
    {
        markers.Clear();
        lines.Clear();
        if (field.Cells.Count == 0) return;

        double elapsed = totalHours - field.AnchorHours;

        // Effective centres + markers.
        int n = field.Cells.Count;
        var px = new double[n];
        var pz = new double[n];
        for (int i = 0; i < n; i++)
        {
            PressureCell c = field.Cells[i];
            px[i] = c.X + c.VelX * elapsed;
            pz[i] = c.Z + c.VelZ * elapsed;
            if (Math.Abs(c.Strength) >= MarkerMinStrength)
                markers.Add(new FrontMarker { X = px[i], Z = pz[i], IsHigh = !c.IsLow });
        }

        // Pair each significant low with its nearest significant high → a front on the boundary.
        for (int i = 0; i < n; i++)
        {
            PressureCell low = field.Cells[i];
            if (!low.IsLow || low.Strength < MarkerMinStrength) continue;

            int best = -1;
            double bestD2 = double.MaxValue;
            for (int j = 0; j < n; j++)
            {
                PressureCell high = field.Cells[j];
                if (high.IsLow || -high.Strength < MarkerMinStrength) continue;
                double dx = px[j] - px[i];
                double dz = pz[j] - pz[i];
                double d2 = dx * dx + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; best = j; } // nearest high
            }
            if (best < 0 || bestD2 > MaxPairDist * MaxPairDist) continue;

            double ax = px[best] - px[i];
            double az = pz[best] - pz[i];
            double dist = Math.Sqrt(ax * ax + az * az);
            if (dist < 1e-3) continue;
            ax /= dist; az /= dist; // unit low→high

            // Anchor the front to the low's leading edge (toward the high), so it stays attached to the
            // low even when the partner high is far away, rather than floating at the midpoint.
            double frontOffset = Math.Min(dist * 0.5, low.Radius);
            double midX = px[i] + ax * frontOffset;
            double midZ = pz[i] + az * frontOffset;

            // Front line runs perpendicular to the low→high axis.
            double dirX = -az, dirZ = ax;

            // Classify by the base jetstream direction at the midpoint.
            double ang = field.MeanderAmplitude * WindField.Meander(midX, midZ, totalHours)
                       + field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
            double jetX = Math.Cos(ang), jetZ = Math.Sin(ang);
            double dot = ax * jetX + az * jetZ; // L→H vs jet
            FrontType type = dot <= -StationaryDot ? FrontType.Cold
                           : dot >= StationaryDot ? FrontType.Warm
                           : FrontType.Stationary;

            lines.Add(new FrontLine
            {
                MidX = midX,
                MidZ = midZ,
                DirX = dirX,
                DirZ = dirZ,
                AxisX = ax,
                AxisZ = az,
                Length = FrontLengthFactor * low.Radius,
                Type = type,
                Seed = low.BornTotalHours
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
