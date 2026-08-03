using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace DynamicWind.Utils;

/// <summary>
/// Generic marching-squares contour extraction over a regular scalar grid — used by
/// <see cref="Client.MapLayers.FrontMapLayer"/> for isobars (traced over the whole visible viewport) and
/// by <see cref="PressureSystem.FrontComputer"/> for front lines where two cells' footprints overlap
/// enough that the single-cell analytic ellipse (<see cref="EllipseGeometry.RadiusToward(PressureCell,double,double)"/>)
/// would no longer match the true (summed) pressure field. Contains no game-specific knowledge — callers
/// supply the sampled grid and get back raw, unordered line segments at a target level.
/// </summary>
internal static class MarchingSquares
{
    public readonly struct Segment
    {
        public readonly Vec2d A, B;
        public Segment(Vec2d a, Vec2d b) { A = a; B = b; }
    }

    /// <summary>
    /// Traces every place the field crosses <paramref name="level"/> within a regular
    /// <paramref name="nx"/>×<paramref name="nz"/> grid of samples (row-major, x fastest,
    /// <paramref name="values"/>[j*nx+i] sampled at world position
    /// (<paramref name="originX"/> + i*<paramref name="step"/>, <paramref name="originZ"/> + j*<paramref name="step"/>)).
    /// Returns disconnected 2-point segments — the standard 16-case marching-squares table, with the two
    /// ambiguous diagonal cases (5 and 10) resolved by the average-of-four-corners test. Callers that need
    /// one continuous ordered polyline (front lines, which sample along arc length for symbol placement)
    /// should feed the result through <see cref="ChainSegments"/>; callers that just draw each segment as
    /// its own stroke (isobars) can use the raw list directly.
    /// </summary>
    public static List<Segment> Trace(float[] values, int nx, int nz, double originX, double originZ, double step, double level)
    {
        var segments = new List<Segment>();
        for (int j = 0; j < nz - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                double v00 = values[j * nx + i];           // bottom-left
                double v10 = values[j * nx + i + 1];       // bottom-right
                double v11 = values[(j + 1) * nx + i + 1]; // top-right
                double v01 = values[(j + 1) * nx + i];     // top-left

                int caseIdx = 0;
                if (v00 > level) caseIdx |= 1;
                if (v10 > level) caseIdx |= 2;
                if (v11 > level) caseIdx |= 4;
                if (v01 > level) caseIdx |= 8;
                if (caseIdx == 0 || caseIdx == 15) continue; // entirely below/above — no crossing

                double x0 = originX + i * step, x1 = x0 + step;
                double z0 = originZ + j * step, z1 = z0 + step;

                Vec2d Bottom() => new(Lerp(x0, x1, v00, v10, level), z0);
                Vec2d Top() => new(Lerp(x0, x1, v01, v11, level), z1);
                Vec2d Left() => new(x0, Lerp(z0, z1, v00, v01, level));
                Vec2d Right() => new(x1, Lerp(z0, z1, v10, v11, level));

                switch (caseIdx)
                {
                    case 1: case 14: segments.Add(new Segment(Left(), Bottom())); break;
                    case 2: case 13: segments.Add(new Segment(Bottom(), Right())); break;
                    case 3: case 12: segments.Add(new Segment(Left(), Right())); break;
                    case 4: case 11: segments.Add(new Segment(Right(), Top())); break;
                    case 6: case 9: segments.Add(new Segment(Bottom(), Top())); break;
                    case 7: case 8: segments.Add(new Segment(Left(), Top())); break;
                    case 5: // ambiguous diagonal (bottom-left + top-right above level)
                        if ((v00 + v10 + v11 + v01) * 0.25 > level)
                        { segments.Add(new Segment(Left(), Top())); segments.Add(new Segment(Bottom(), Right())); }
                        else
                        { segments.Add(new Segment(Left(), Bottom())); segments.Add(new Segment(Right(), Top())); }
                        break;
                    case 10: // ambiguous diagonal (bottom-right + top-left above level)
                        if ((v00 + v10 + v11 + v01) * 0.25 > level)
                        { segments.Add(new Segment(Left(), Bottom())); segments.Add(new Segment(Right(), Top())); }
                        else
                        { segments.Add(new Segment(Left(), Top())); segments.Add(new Segment(Bottom(), Right())); }
                        break;
                }
            }
        }
        return segments;
    }

    private static double Lerp(double a, double b, double va, double vb, double level)
    {
        double t = Math.Abs(vb - va) > 1e-9 ? (level - va) / (vb - va) : 0.5;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return a + (b - a) * t;
    }

    /// <summary>Greedily stitches an unordered segment bag into continuous polylines by matching
    /// coincident endpoints (within <paramref name="mergeEps"/> world units — grid-cell-scale, since
    /// marching-squares segments sharing a grid edge produce exactly matching interpolated endpoints up to
    /// floating-point error). O(n²) in segment count, which is fine for the small, LOCAL segment sets
    /// <see cref="PressureSystem.FrontComputer"/> traces around one overlapping cell pair — not intended
    /// for isobars' much larger whole-viewport segment sets, which don't need ordering at all.</summary>
    public static List<Vec2d[]> ChainSegments(List<Segment> segments, double mergeEps)
    {
        var used = new bool[segments.Count];
        var chains = new List<Vec2d[]>();
        for (int start = 0; start < segments.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;
            var chain = new List<Vec2d> { segments[start].A, segments[start].B };
            bool extended = true;
            while (extended)
            {
                extended = false;
                for (int k = 0; k < segments.Count; k++)
                {
                    if (used[k]) continue;
                    Segment s = segments[k];
                    if (Near(chain[^1], s.A, mergeEps)) { chain.Add(s.B); used[k] = true; extended = true; }
                    else if (Near(chain[^1], s.B, mergeEps)) { chain.Add(s.A); used[k] = true; extended = true; }
                    else if (Near(chain[0], s.A, mergeEps)) { chain.Insert(0, s.B); used[k] = true; extended = true; }
                    else if (Near(chain[0], s.B, mergeEps)) { chain.Insert(0, s.A); used[k] = true; extended = true; }
                }
            }
            chains.Add(chain.ToArray());
        }
        return chains;
    }

    private static bool Near(Vec2d a, Vec2d b, double eps)
    {
        double dx = a.X - b.X, dz = a.Y - b.Y;
        return dx * dx + dz * dz <= eps * eps;
    }
}
