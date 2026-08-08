using System;
using System.Collections.Generic;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using Vintagestory.API.MathTools;

namespace DynamicWind.PressureSystem;

public enum FrontType { Cold, Warm, Stationary, Occluded, ColdOcclusion, WarmOcclusion }

/// <summary>A pressure-system marker (the "H"/"L" letter) at a cell's current effective centre. Carries
/// the cell's own elliptical footprint (<see cref="Radius"/>/<see cref="RadiusMinor"/>/
/// <see cref="Orientation"/>) so a renderer whose true centre has scrolled off-screen can clamp the marker
/// to the system's own boundary (see <see cref="Utils.EllipseGeometry.RadiusToward(double,double,double,double,double)"/>)
/// instead of an arbitrary on-screen position - the same boundary a front's leading-edge arc traces
/// (<see cref="FrontComputer.BuildLeadingEdgeArc"/>), so the icon never reads as further "ahead" than the
/// system's actual influence reaches.</summary>
public struct FrontMarker
{
    public double X;
    public double Z;
    public bool IsHigh;
    public float Radius;
    public float RadiusMinor;
    public float Orientation;
}

/// <summary>
/// A weather front for one LOW pressure system (highs carry no fronts - see FrontComputer). Every front -
/// Warm/Cold (a moving low's SINGLE front line, poleward for a warm-natured low or equatorward for a
/// cold-natured one), Stationary (the connecting line to its nearest significant neighbour), and
/// Occluded/ColdOcclusion/WarmOcclusion (the line toward whatever pressure cell its cold front caught) -
/// is an open marched polyline (<see cref="Points"/>), never a closed loop:
/// on a flat, non-global map a front reads as a boundary sweeping across the land, not a ring, regardless
/// of how little its own system is currently advancing. <see cref="Strength"/> (|cell strength|) scales
/// how prominent the front is drawn.
/// </summary>
public struct FrontLine
{
    public FrontType Type;
    public float Strength;

    // The low's own elliptical LEADING EDGE, world-space (not its centre) - see
    // FrontComputer.BuildLeadingEdgeArc/EllipseGeometry.RadiusToward.
    public Vec2d[] Points;
    // Cumulative arc-length along Points (CumulativeLength[0] == 0, CumulativeLength[^1] == total length),
    // precomputed once alongside Points so SampleAt doesn't rebuild it on every call.
    public double[] CumulativeLength;
    // The (unit) direction this front is actually advancing - matches the real convention that a front's
    // pip/triangle symbols point the way it's advancing. Warm/Cold: the low's own travel direction.
    // Stationary: the neighbour's velocity RELATIVE to this low's own (the low's own velocity is near zero
    // by definition). Occlusion: travel direction, falling back to catch direction when that's near zero.
    // Using this rather than the poleward/equatorward bias direction avoids a reference nearly PARALLEL to
    // the curve's own tangent, which made the two tangent-perpendiculars indistinguishable (near-zero dot
    // product either way) and left the facing side effectively arbitrary. At every sample the symbol faces
    // whichever local-tangent perpendicular best aligns with this direction - see <see cref="ChooseFacing"/>.
    public double RefX, RefZ;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE - a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Only LOW cells carry fronts - a real high is fair-weather subsidence with no frontal boundary of its
/// own. A moving low renders the classic Norwegian-cyclone-model pair: BOTH a warm front (leading, ahead
/// of the low's own direction of travel, tilted poleward - where warm air overruns) and a cold front
/// (trailing, behind it, tilted equatorward - where cold air digs in), meeting at the low's own centre -
/// see the MOVING branch of <see cref="Compute"/> and <see cref="WindField.PolewardZSign"/> for the
/// geography split. A near-stationary low instead renders one line toward its nearest significant
/// neighbour - the "connecting line" a stationary front actually sits on - flagged Stationary, and a low
/// mid-occlusion (see WindFieldSimulation.ApplyOcclusion) renders one line toward whatever pressure cell
/// its cold front caught, flagged Occluded/ColdOcclusion/WarmOcclusion per
/// <see cref="PressureCell.OcclusionKind"/>. Highs still get an H marker, just no front line. Markers
/// (H/L) are emitted for every significant cell.
/// </summary>
public static class FrontComputer
{
    #region Constants

    private const float MarkerMinStrength = 0.15f; // mark/draw systems down to this |strength|
    private const double StationarySpeed = WindField.StationarySpeed;

    // Warm/Cold front tracing: the line traces the pressure cell's OWN elliptical boundary across a wide
    // angular span (centered on the bias direction - travel heading for Warm/Cold, catch direction for
    // Occluded, bearing-to-neighbour for Stationary), scaled outward by that side's ColdFrontReach/
    // WarmFrontReach - a front literally IS the system's edge, growing as the reach grows, never anything
    // free-marched or wobbled beyond it. (Two earlier approaches were tried and discarded: an explicit
    // Bezier JOIN to a neighbouring low, which could self-intersect/degenerate when systems sat close
    // together; and a free march with pressure-gradient deflection plus a sine "wobble" for texture, which
    // read as jagged/noisy once cells grew to synoptic scale. Both replaced by this simpler elliptical
    // contour.)
    private const double ArcHalfAngle = 50.0 * Math.PI / 180.0; // half-span of the leading-edge arc
    private const int ArcStepCount = 24;                        // arc sample steps (ArcStepCount+1 points)

    // Overlap-aware fallback: the single-cell analytic arc above ignores every OTHER cell's contribution to
    // the pressure field - exact when systems are isolated, wrong once two footprints genuinely overlap
    // (the real combined field pinches/merges between them, not two ellipses crossing through each other).
    // When overlap is detected, trace the ACTUAL combined field (WindField.CellPressureAt, summed over
    // every cell) with marching squares instead - see BuildFrontLine/BuildFrontLineViaOverlap. Isolated
    // systems (the common case, given the low MaxActiveCells cap and spawn logic that keeps new cells
    // apart) are unaffected: this path only engages when actually needed.
    private const double OverlapDistanceFactor = 1.3;   // x (radius_i + radius_j) - overlap trigger threshold
    private const int OverlapGridResolution = 64;        // grid divisions along the longer axis of the sampled box
    private const int OverlapGridMaxCells = 6000;        // safety cap on total grid points
    private const double OverlapBoxMarginFactor = 1.5;   // x own radius - MINIMUM margin around a lone cell's box
    // The traced contour sits at radius x reach, and reach grows to MaxFrontReach (3), so the box must
    // widen with it: a fixed 1.5x margin left the target contour outside the sampled box for any front past
    // reach 1.5 (~6 game-hours of growth), silently falling back to the analytic arc this path exists to
    // replace - after paying for the full grid sample.
    private const double OverlapBoxReachPad = 0.5;       // + reach, x own radius - box half-extent at high reach
    private const double OverlapWedgeMaxDistFactor = 3.0; // x own radius - discard traced segments farther than this
    // Endpoint-merge tolerance for chaining. MarchingSquares.Trace emits exactly-coincident endpoints for
    // segments sharing a contour, so this only needs to absorb floating-point error - a large fraction of
    // the grid step would instead weld endpoints belonging to two DISTINCT branches through one ambiguous
    // saddle cell (exactly what forms in the pinch between two overlapping systems), producing a spurious
    // stroke jumping across the saddle with pips stamped along it.
    private const double OverlapChainMergeStepFactor = 1e-3; // x grid step

    #endregion

    #region Public API

    public static void Compute(WindField field, double totalHours, List<FrontMarker> markers, List<FrontLine> lines)
    {
        markers.Clear();
        lines.Clear();
        lock (field.CellsLock)
        {
            if (field.Cells.Count == 0) return;

            double elapsed = totalHours - field.AnchorHours;

            // First pass: effective positions + which cells are significant (needed up front so the
            // nearest-neighbour lookup below can see every cell regardless of its index in the list).
            var ex = new double[field.Cells.Count];
            var ez = new double[field.Cells.Count];
            // Every marker-eligible cell (lows AND highs).
            var significantIndices = new List<int>();
            var lowIndices = new List<int>();
            for (int i = 0; i < field.Cells.Count; i++)
            {
                PressureCell c = field.Cells[i];
                if (Math.Abs(c.Strength) < MarkerMinStrength) continue;
                ex[i] = c.X + c.VelX * elapsed;
                ez[i] = c.Z + c.VelZ * elapsed;
                markers.Add(new FrontMarker
                {
                    X = ex[i], Z = ez[i], IsHigh = !c.IsLow,
                    Radius = c.Radius, RadiusMinor = c.RadiusMinor, Orientation = c.Orientation
                });

                significantIndices.Add(i);
                if (!c.IsLow) continue;
                lowIndices.Add(i);
            }

            foreach (int i in lowIndices)
            {
                PressureCell c = field.Cells[i];
                float strength = Math.Abs(c.Strength);
                double speed = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);

                double centre = field.CellPressureAt(ex[i], ez[i], totalHours);
                if (Math.Abs(centre) < MarkerMinStrength) continue;

                if (totalHours < c.OccludedUntilTotalHours)
                {
                    // OCCLUDED: the cold front physically caught another pressure cell on the equatorward
                    // side (see WindFieldSimulation.ApplyOcclusion) - renders a single open curve toward
                    // that side, using the cold/warm symbol per PressureCell.OcclusionKind (Unset falls
                    // back to the generic occluded symbol, e.g. saves migrated from before it existed).
                    FrontType occType = c.OcclusionKind switch
                    {
                        PressureCell.OcclusionKindCold => FrontType.ColdOcclusion,
                        PressureCell.OcclusionKindWarm => FrontType.WarmOcclusion,
                        _ => FrontType.Occluded
                    };

                    double occPoleAngle = field.PolewardZSign(ez[i]) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
                    double coldAngle = occPoleAngle + Math.PI; // equatorward - the catching side

                    Vec2d[] occPts = BuildFrontLine(field, totalHours, elapsed, i, c, ex[i], ez[i], coldAngle, c.ColdFrontReach);

                    // Own travel direction where it's meaningful; an occluded low can be near-stationary, so
                    // fall back to the catch direction itself rather than dividing by a near-zero speed.
                    NormalizeOrFallback(c.VelX, c.VelZ, speed, coldAngle, out double occRefX, out double occRefZ);

                    lines.Add(MakeLine(occType, strength, occPts, occRefX, occRefZ));
                    continue;
                }

                if (speed < StationarySpeed)
                {
                    // STATIONARY: a real stationary front sits on the (non-advancing) boundary between two
                    // systems, not a closed loop encircling one - renders a single open curve toward this
                    // low's nearest significant neighbour (a fallback direction if truly isolated). Its
                    // facing follows how that connecting line is actually moving - the neighbour's velocity
                    // RELATIVE to this low's own, since the low's own velocity is by definition near zero
                    // and useless as a facing reference on its own.
                    int neighbour = FindNearestSignificantCell(significantIndices, i, ex, ez);
                    double fallbackAngle = field.PolewardZSign(ez[i]) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
                    double bearingAngle = neighbour >= 0
                        ? Math.Atan2(ez[neighbour] - ez[i], ex[neighbour] - ex[i])
                        : fallbackAngle;
                    float statReach = (c.ColdFrontReach + c.WarmFrontReach) * 0.5f;

                    Vec2d[] statPts = BuildFrontLine(field, totalHours, elapsed, i, c, ex[i], ez[i], bearingAngle, statReach);

                    double relVelX = neighbour >= 0 ? field.Cells[neighbour].VelX - c.VelX : Math.Cos(bearingAngle);
                    double relVelZ = neighbour >= 0 ? field.Cells[neighbour].VelZ - c.VelZ : Math.Sin(bearingAngle);
                    double relSpeed = Math.Sqrt(relVelX * relVelX + relVelZ * relVelZ);
                    NormalizeOrFallback(relVelX, relVelZ, relSpeed, bearingAngle, out double statRefX, out double statRefZ);

                    lines.Add(MakeLine(FrontType.Stationary, strength, statPts, statRefX, statRefZ));
                    continue;
                }

                // MOVING, open wave: matching the classic Norwegian cyclone model, a mature wave low shows
                // BOTH a warm front (leading, ahead of travel) and a cold front (trailing, behind it) at
                // once, meeting at the low's own centre - not just one or the other. Each is tilted off the
                // pure travel/anti-travel axis toward its real-world side (warm leads AND tilts poleward -
                // where warm air overruns; cold trails AND tilts equatorward - where cold air digs in),
                // giving the classic "comma"/wave shape rather than two lines sitting exactly opposite each
                // other - see WindField.MovingFrontAngles, the single source of truth for this split (also
                // used by FrontRegimeBiasAt so precipitation intensity-type weighting lines up with these
                // exact same two edges, not an independently drifting geography split). WarmFrontReach/
                // ColdFrontReach (which already grow at different rates - see "Front reach") give the two
                // sides their own, independently evolving extent.
                field.MovingFrontAngles(c.VelX, c.VelZ, ez[i], out double waveWarmAngle, out double waveColdAngle);

                Vec2d[] warmPts = BuildFrontLine(field, totalHours, elapsed, i, c, ex[i], ez[i], waveWarmAngle, c.WarmFrontReach);
                Vec2d[] coldPts = BuildFrontLine(field, totalHours, elapsed, i, c, ex[i], ez[i], waveColdAngle, c.ColdFrontReach);

                double refX = c.VelX / speed, refZ = c.VelZ / speed; // speed >= StationarySpeed here, never zero

                lines.Add(MakeLine(FrontType.Warm, strength, warmPts, refX, refZ));
                lines.Add(MakeLine(FrontType.Cold, strength, coldPts, refX, refZ));
            }
        }
    }

    #endregion

    #region Line-Building Helpers

    /// <summary>Normalizes (vx, vz) of known length <paramref name="len"/> to a unit vector, or falls back
    /// to the unit vector at <paramref name="fallbackAngle"/> when too near zero to normalize safely
    /// (a front's own reference velocity can be near-stationary - see the Occluded/Stationary branches
    /// above).</summary>
    private static void NormalizeOrFallback(double vx, double vz, double len, double fallbackAngle,
        out double nx, out double nz)
    {
        if (len > 1e-6) { nx = vx / len; nz = vz / len; }
        else { nx = Math.Cos(fallbackAngle); nz = Math.Sin(fallbackAngle); }
    }

    /// <summary>Builds a computed <see cref="FrontLine"/> from its traced points - shared by all four
    /// branches in <see cref="Compute"/> (Occluded, Stationary, Warm, Cold).</summary>
    private static FrontLine MakeLine(FrontType type, float strength, Vec2d[] pts, double refX, double refZ) => new()
    {
        Type = type,
        Strength = strength,
        Points = pts,
        CumulativeLength = BuildCumulative(pts),
        RefX = refX,
        RefZ = refZ
    };

    /// <summary>Euclidean distance between two curve points - shared by every polyline-length accumulator
    /// below so the same formula isn't hand-rolled at each call site.</summary>
    private static double SegmentLength(Vec2d a, Vec2d b)
    {
        double dx = b.X - a.X, dz = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Precomputes cumulative arc-length along an open curve's points, for O(1) reuse by
    /// <see cref="SampleAt"/> instead of rebuilding it on every sample.</summary>
    private static double[] BuildCumulative(Vec2d[] pts)
    {
        var cumLen = new double[pts.Length];
        for (int i = 1; i < pts.Length; i++)
            cumLen[i] = cumLen[i - 1] + SegmentLength(pts[i - 1], pts[i]);
        return cumLen;
    }

    /// <summary>Finds the nearest OTHER significant cell to <paramref name="self"/> in ANY direction (low
    /// or high - <paramref name="candidateIndices"/> is expected to be <c>significantIndices</c>, every
    /// marker-eligible cell) - used to find a Stationary low's "connecting line" front target, which
    /// unlike a Warm/Cold JOIN isn't restricted to a poleward/equatorward side. Returns -1 if
    /// <paramref name="self"/> has no other significant cell in the field at all.</summary>
    private static int FindNearestSignificantCell(List<int> candidateIndices, int self, double[] ex, double[] ez)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int k = 0; k < candidateIndices.Count; k++)
        {
            int j = candidateIndices[k];
            if (j == self) continue;
            double dx = ex[j] - ex[self], dz = ez[j] - ez[self];
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < bestDist) { bestDist = dist; best = j; }
        }
        return best;
    }

    #endregion

    #region Isolated-Cell Arc Tracing

    /// <summary>Samples <see cref="ArcStepCount"/>+1 points along the pressure cell's OWN elliptical
    /// boundary (<see cref="EllipseGeometry.RadiusToward"/>, so an elongated/oriented cell's true edge is
    /// used, not a circular approximation), spanning <see cref="ArcHalfAngle"/> either side of
    /// <paramref name="biasAngle"/>, with the boundary radius scaled by <paramref name="reach"/>
    /// (ColdFrontReach/WarmFrontReach - grows over the front's life, so the traced edge visibly grows
    /// with it). This IS the whole front line: no free march, no added noise - a front is drawn exactly as
    /// the system's own (reach-scaled) edge, matching how a real synoptic chart traces a front along the
    /// system's boundary rather than an arbitrary curve trailing away from it.</summary>
    private static Vec2d[] BuildLeadingEdgeArc(PressureCell cell, double ex, double ez, double biasAngle, float reach)
    {
        var pts = new Vec2d[ArcStepCount + 1];
        for (int s = 0; s <= ArcStepCount; s++)
        {
            double t = (double)s / ArcStepCount;
            double angle = biasAngle - ArcHalfAngle + t * (2.0 * ArcHalfAngle);
            double dirX = Math.Cos(angle), dirZ = Math.Sin(angle);
            double r = EllipseGeometry.RadiusToward(cell, dirX, dirZ) * Math.Max(1.0, reach);
            pts[s] = new Vec2d(ex + dirX * r, ez + dirZ * r);
        }
        return pts;
    }

    /// <summary>Builds one front's line, picking the cheap single-cell analytic arc
    /// (<see cref="BuildLeadingEdgeArc"/>) when <paramref name="cell"/> is isolated, or the marching-squares
    /// trace of the ACTUAL combined field (<see cref="BuildFrontLineViaOverlap"/>) when it meaningfully
    /// overlaps another significant cell - see <see cref="OverlapDistanceFactor"/>.</summary>
    private static Vec2d[] BuildFrontLine(WindField field, double totalHours, double elapsed, int selfIndex,
        PressureCell cell, double cx, double cz, double biasAngle, float reach)
    {
        double ownRadius = Math.Max(cell.Radius, cell.RadiusMinor);
        if (HasSignificantOverlap(field, selfIndex, cx, cz, ownRadius, elapsed))
            return BuildFrontLineViaOverlap(field, totalHours, elapsed, selfIndex, cell, cx, cz, ownRadius, biasAngle, reach);
        return BuildLeadingEdgeArc(cell, cx, cz, biasAngle, reach);
    }

    /// <summary>True if any OTHER significant cell's footprint sits close enough to <paramref name="cell"/>
    /// that the single-cell analytic arc would no longer match the true (summed) pressure field - a plain
    /// distance-between-centres check against the sum of the two cells' own (major-axis) radii, scaled by
    /// <see cref="OverlapDistanceFactor"/>. Deliberately approximate (ellipse orientation isn't accounted
    /// for): this only decides whether to pay for the more expensive marching-squares path, not the traced
    /// geometry itself.</summary>
    private static bool HasSignificantOverlap(WindField field, int selfIndex, double cx, double cz, double ownRadius, double elapsed)
    {
        for (int j = 0; j < field.Cells.Count; j++)
        {
            if (j == selfIndex) continue;
            PressureCell o = field.Cells[j];
            if (Math.Abs(o.Strength) < MarkerMinStrength) continue;
            double ox = o.X + o.VelX * elapsed, oz = o.Z + o.VelZ * elapsed;
            double dx = ox - cx, dz = oz - cz;
            double oRadius = Math.Max(o.Radius, o.RadiusMinor);
            double threshold = (ownRadius + oRadius) * OverlapDistanceFactor;
            if (dx * dx + dz * dz < threshold * threshold) return true;
        }
        return false;
    }

    #endregion

    #region Overlap Tracing (Marching Squares)

    /// <summary>
    /// Traces this front's line from the ACTUAL combined pressure field (every cell, not just
    /// <paramref name="cell"/> alone) via <see cref="MarchingSquares"/>, for the case where a nearby
    /// system's footprint would otherwise make the single-cell analytic ellipse visibly wrong (crossing
    /// through the other system instead of the real merged/pinched boundary between them).
    ///
    /// The target LEVEL is derived the same way the analytic arc's radius-scaling works: scaling a single
    /// cell's own boundary radius by <paramref name="reach"/> is equivalent to contouring at
    /// <c>|Strength| * WindField.PressureFalloff(reach^2)</c> (the elliptical falloff's <c>norm2</c> at
    /// radiusxreach is exactly reach^2) - so reach still controls how far out the front sits, just expressed
    /// as a pressure level instead of a radius multiplier, and both formulas agree exactly at reach = 1.
    /// This MUST go through <see cref="WindField.PressureFalloff"/> rather than hardcoding its formula:
    /// the two definitions of "where the boundary is" have to stay in step or the traced contour lands at a
    /// different radius than the analytic arc it falls back to.
    ///
    /// A LOCAL grid (this cell's own footprint plus any cell it overlaps, not the whole map) is sampled
    /// once and marched at that level; raw segments are then restricted to a wedge around
    /// <paramref name="biasAngle"/> (matching <see cref="ArcHalfAngle"/>) and within
    /// <see cref="OverlapWedgeMaxDistFactor"/>xradius of <paramref name="cell"/>'s own centre, so an
    /// unrelated contour elsewhere in the sampled box (e.g. the far side of the overlapping neighbour) is
    /// never picked up. The filtered segments are chained into one polyline
    /// (<see cref="MarchingSquares.ChainSegments"/>) and the longest chain is returned - falling back to
    /// the analytic arc if the grid was too coarse to catch anything at all in the wedge.
    /// </summary>
    private static Vec2d[] BuildFrontLineViaOverlap(WindField field, double totalHours, double elapsed, int selfIndex,
        PressureCell cell, double cx, double cz, double ownRadius, double biasAngle, float reach)
    {
        double absStrength = Math.Abs(cell.Strength);
        double signedLevel = Math.Sign(cell.Strength) * absStrength * WindField.PressureFalloff((double)reach * reach);

        // Local sample box: this cell's own footprint, expanded to include any cell it overlaps (so the
        // merged/pinched boundary between them is fully captured, not cut off at the box edge).
        double boxHalf = ownRadius * Math.Max(OverlapBoxMarginFactor, reach + OverlapBoxReachPad);
        double minX = cx - boxHalf, maxX = cx + boxHalf;
        double minZ = cz - boxHalf, maxZ = cz + boxHalf;
        for (int j = 0; j < field.Cells.Count; j++)
        {
            if (j == selfIndex) continue;
            PressureCell o = field.Cells[j];
            if (Math.Abs(o.Strength) < MarkerMinStrength) continue;
            double ox = o.X + o.VelX * elapsed, oz = o.Z + o.VelZ * elapsed;
            double oRadius = Math.Max(o.Radius, o.RadiusMinor);
            double dx = ox - cx, dz = oz - cz;
            double maxOverlapDist = (ownRadius + oRadius) * OverlapDistanceFactor;
            if (dx * dx + dz * dz >= maxOverlapDist * maxOverlapDist) continue;
            minX = Math.Min(minX, ox - oRadius); maxX = Math.Max(maxX, ox + oRadius);
            minZ = Math.Min(minZ, oz - oRadius); maxZ = Math.Max(maxZ, oz + oRadius);
        }

        double spanX = maxX - minX, spanZ = maxZ - minZ;
        double step = Math.Max(spanX, spanZ) / OverlapGridResolution;
        if (step < 1.0) step = 1.0;
        while ((spanX / step) * (spanZ / step) > OverlapGridMaxCells) step *= 1.5;

        int nx = (int)Math.Ceiling(spanX / step) + 2;
        int nz = (int)Math.Ceiling(spanZ / step) + 2;
        var values = new float[nx * nz];
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
                values[j * nx + i] = (float)field.CellPressureAt(minX + i * step, minZ + j * step, totalHours);

        List<MarchingSquares.Segment> rawSegs = MarchingSquares.Trace(values, nx, nz, minX, minZ, step, signedLevel);

        double maxDist = ownRadius * OverlapWedgeMaxDistFactor;
        var filtered = new List<MarchingSquares.Segment>(rawSegs.Count);
        foreach (MarchingSquares.Segment seg in rawSegs)
        {
            double mx = (seg.A.X + seg.B.X) * 0.5, mz = (seg.A.Y + seg.B.Y) * 0.5;
            double dx = mx - cx, dz = mz - cz;
            if (dx * dx + dz * dz > maxDist * maxDist) continue;
            double angle = Math.Atan2(dz, dx);
            double diff = AngleMath.AngleDiff(biasAngle, angle);
            if (Math.Abs(diff) > ArcHalfAngle) continue;
            filtered.Add(seg);
        }
        if (filtered.Count == 0) return BuildLeadingEdgeArc(cell, cx, cz, biasAngle, reach);

        List<Vec2d[]> chains = MarchingSquares.ChainSegments(filtered, step * OverlapChainMergeStepFactor);
        Vec2d[] best = chains[0];
        double bestLen = ChainLength(best);
        for (int k = 1; k < chains.Count; k++)
        {
            double len = ChainLength(chains[k]);
            if (len > bestLen) { bestLen = len; best = chains[k]; }
        }
        return best;
    }

    private static double ChainLength(Vec2d[] pts)
    {
        double len = 0;
        for (int i = 1; i < pts.Length; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dz = pts[i].Y - pts[i - 1].Y;
            len += Math.Sqrt(dx * dx + dz * dz);
        }
        return len;
    }

    #endregion

    #region Curve Sampling (Public)

    /// <summary>Picks whichever +/-90deg perpendicular of a (normalized) tangent best aligns with
    /// <paramref name="refX"/>/<paramref name="refZ"/> (the low's own direction of travel - real front
    /// symbols point in the direction the front is advancing). Used when rendering (<see cref="SampleAt"/>)
    /// so a front symbol stays correctly oriented along the curve's whole length. Can become near-degenerate
    /// - both candidates close to equally aligned - once the curve's tangent nears parallel to ref (e.g. a
    /// front whose FREE march has bent nearly back onto its own asymptotic trail direction).</summary>
    private static void ChooseFacing(double tanX, double tanZ, double refX, double refZ, out double faceX, out double faceZ)
    {
        double p1x = -tanZ, p1z = tanX;
        double p2x = tanZ, p2z = -tanX;
        if (p1x * refX + p1z * refZ >= p2x * refX + p2z * refZ) { faceX = p1x; faceZ = p1z; }
        else { faceX = p2x; faceZ = p2z; }
    }

    /// <summary>Total drawable world-space length of a front's traced polyline.</summary>
    public static double CurveLength(in FrontLine f)
    {
        double len = 0;
        for (int i = 1; i < f.Points.Length; i++)
        {
            double dx = f.Points[i].X - f.Points[i - 1].X;
            double dz = f.Points[i].Y - f.Points[i - 1].Y;
            len += Math.Sqrt(dx * dx + dz * dz);
        }
        return len;
    }

    /// <summary>
    /// Uniform curve-parameter sample: <paramref name="t"/> in [0,1] spans the whole open curve. Returns
    /// the exact world position on the traced polyline (no added noise/wobble - a front IS the system's
    /// own edge, see <see cref="BuildLeadingEdgeArc"/>) and the local facing angle (whichever
    /// tangent-perpendicular best matches <see cref="FrontLine.RefX"/>/<see cref="FrontLine.RefZ"/> - see
    /// <see cref="ChooseFacing"/>).
    /// </summary>
    public static void SampleAt(in FrontLine f, double t, out double wx, out double wz, out double facingAngle)
    {
        Vec2d[] pts = f.Points;
        if (pts.Length < 2)
        {
            wx = pts.Length == 1 ? pts[0].X : 0;
            wz = pts.Length == 1 ? pts[0].Y : 0;
            facingAngle = 0;
            return;
        }

        double[] cumLen = f.CumulativeLength ?? BuildCumulative(pts);
        double target = t * cumLen[pts.Length - 1];
        int seg = 0;
        while (seg < pts.Length - 2 && cumLen[seg + 1] < target) seg++;
        double segLen = cumLen[seg + 1] - cumLen[seg];
        double frac = segLen > 1e-9 ? (target - cumLen[seg]) / segLen : 0;

        double tanX = pts[seg + 1].X - pts[seg].X, tanZ = pts[seg + 1].Y - pts[seg].Y;
        double tanLen = Math.Sqrt(tanX * tanX + tanZ * tanZ);
        if (tanLen > 1e-9) { tanX /= tanLen; tanZ /= tanLen; }

        wx = pts[seg].X + (pts[seg + 1].X - pts[seg].X) * frac;
        wz = pts[seg].Y + (pts[seg + 1].Y - pts[seg].Y) * frac;

        ChooseFacing(tanX, tanZ, f.RefX, f.RefZ, out double faceX, out double faceZ);
        facingAngle = Math.Atan2(faceZ, faceX);
    }

    #endregion
}
