using System;
using System.Collections.Generic;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using Vintagestory.API.MathTools;

namespace DynamicWind.PressureSystem;

public enum FrontType { Cold, Warm, Stationary, Occluded, ColdOcclusion, WarmOcclusion }

/// <summary>A pressure-system marker (the "H"/"L" letter) at a cell's current effective centre. Carries
/// the cell's own elliptical footprint (<see cref="Radius"/>/<see cref="RadiusMinor"/>/
/// <see cref="Orientation"/>) so a renderer whose true centre has scrolled off-screen can clamp the
/// marker to the system's own boundary (see <see cref="Utils.EllipseGeometry.RadiusToward(double,double,double,double,double)"/>)
/// instead of placing it at an arbitrary on-screen position — the same boundary a front's own
/// leading-edge arc traces (<see cref="FrontComputer.BuildLeadingEdgeArc"/>), so the icon never reads as
/// further "ahead" than the system's actual influence reaches.</summary>
public struct FrontMarker
{
    public double X;
    public double Z;
    public bool IsHigh;
    public float Radius;
    public float RadiusMinor;
    public float Orientation;
}

/// <summary>A single isobar (constant-pressure contour) ring around one pressure cell. Since a lone
/// cell's own pressure contribution is exactly <c>Strength * exp(-norm2)</c> (see
/// <see cref="WindField.CellPressureAt(double,double,double,int)"/>) and <c>norm2 == 1</c> exactly at
/// <see cref="Utils.EllipseGeometry.RadiusToward(PressureCell,double,double)"/>'s own boundary, the
/// contour at any pressure level below the cell's peak strength is just that same ellipse uniformly
/// scaled — see <see cref="FrontComputer.BuildIsobarRing"/>. Like the front-line arc, this ignores
/// neighbouring cells' contribution to the shape (a single-dominant-cell approximation).</summary>
public struct IsobarRing
{
    public Vec2d[] Points; // closed loop (first point == last point)
    public bool IsLow;
    public float Level;      // pressure magnitude this ring represents (always positive)
    public int RingIndex;    // 0 = innermost (highest |pressure|) ring for this cell
}

/// <summary>
/// A weather front for one LOW pressure system (highs carry no fronts — see FrontComputer). Every front —
/// Warm/Cold (a moving low's SINGLE front line, poleward for a warm-natured low or equatorward for a
/// cold-natured one), Stationary (the connecting line to its nearest significant neighbour), and
/// Occluded/ColdOcclusion/WarmOcclusion (the line toward whatever pressure cell its cold front caught) —
/// is an open marched polyline (<see cref="Points"/>), never a closed loop:
/// on a flat, non-global map a front reads as a boundary sweeping across the land, not a ring, regardless
/// of how little its own system is currently advancing. <see cref="Strength"/> (|cell strength|) scales
/// how prominent the front is drawn.
/// </summary>
public struct FrontLine
{
    public FrontType Type;
    public float Strength;

    // The low's own elliptical LEADING EDGE, world-space (not its centre) — see
    // FrontComputer.BuildLeadingEdgeArc/EllipseGeometry.RadiusToward.
    public Vec2d[] Points;
    // Cumulative arc-length along Points (CumulativeLength[0] == 0, CumulativeLength[^1] == total length),
    // precomputed once alongside Points so SampleAt doesn't rebuild it on every call.
    public double[] CumulativeLength;
    // The (unit) direction this front is actually advancing — matches the real convention that a front's
    // pip/triangle symbols point in the direction the front is advancing. For Warm/Cold this is the low's
    // own travel direction; for Stationary it's the connecting-line neighbour's velocity RELATIVE to this
    // low's own (a stationary low's own velocity is near zero by definition); for an occlusion it's the
    // low's own travel direction, falling back to the catch direction when that's near zero. Using this
    // (rather than the poleward/equatorward direction the curve itself is biased toward) avoids picking a
    // reference nearly PARALLEL to the curve's own tangent, which made the two tangent-perpendiculars
    // indistinguishable (near-zero dot product either way) and left the facing side effectively arbitrary.
    // At every sample the symbol faces whichever perpendicular of the LOCAL tangent best aligns with this
    // direction — see <see cref="ChooseFacing"/>.
    public double RefX, RefZ;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Only LOW cells carry fronts — a real high is fair-weather subsidence with no frontal boundary of its
/// own. A moving low renders the classic Norwegian-cyclone-model pair: BOTH a warm front (leading, ahead
/// of the low's own direction of travel, tilted poleward — where warm air overruns) and a cold front
/// (trailing, behind it, tilted equatorward — where cold air digs in), meeting at the low's own centre —
/// see the MOVING branch of <see cref="Compute"/> and <see cref="WindField.PolewardZSign"/> for the
/// geography split. A near-stationary low instead renders one line toward its nearest significant
/// neighbour — the "connecting line" a stationary front actually sits on — flagged Stationary, and a low
/// mid-occlusion (see WindFieldSimulation.ApplyOcclusion) renders one line toward whatever pressure cell
/// its cold front caught, flagged Occluded/ColdOcclusion/WarmOcclusion per
/// <see cref="PressureCell.OcclusionKind"/>. Highs still get an H marker, just no front line. Markers
/// (H/L) are emitted for every significant cell.
/// </summary>
public static class FrontComputer
{
    private const float MarkerMinStrength = 0.15f; // mark/draw systems down to this |strength|
    private const double StationarySpeed = WindField.StationarySpeed;

    // Warm/Cold front tracing: the line traces the pressure cell's OWN elliptical boundary across a wide
    // angular span (centered on the bias direction — travel heading for Warm/Cold, catch direction for
    // Occluded, bearing-to-neighbour for Stationary), scaled outward by that side's ColdFrontReach/
    // WarmFrontReach — a front literally IS the system's edge, growing as the reach grows, never anything
    // free-marched or wobbled beyond it. (Two earlier approaches were tried and discarded: an explicit
    // Bezier JOIN out to a neighbouring low, which could self-intersect/degenerate when systems sat close
    // together; and a free march trailing outward from the arc's ends with pressure-gradient deflection
    // plus a sine "wobble" for organic texture, which — especially once cells grew to synoptic scale —
    // read as jagged/noisy rather than a smooth boundary. Both were removed in favour of this simpler,
    // purely elliptical contour.)
    private const double ArcHalfAngle = 50.0 * Math.PI / 180.0; // half-span of the leading-edge arc
    private const int ArcStepCount = 24;                        // arc sample steps (ArcStepCount+1 points)

    // Isobars: real-chart style — a FIXED absolute pressure step, so a weak system shows a ring or two and
    // a strong one shows many tightly-packed rings (that density variation is the point, same as a real
    // surface chart, not something to normalize away). A single cell's own contribution is exactly
    // Strength * exp(-norm2) (see WindField.CellPressureAt) and norm2 == 1 exactly at
    // EllipseGeometry.RadiusToward's boundary, so the contour at level L < |Strength| is that same ellipse
    // scaled by sqrt(ln(|Strength| / L)) — see BuildIsobarRing. Applies to EVERY significant cell (highs
    // included), unlike fronts (lows only) — a real chart draws isobars around anticyclones too.
    private const double IsobarStep = 0.2;
    private const int MaxIsobarsPerCell = 12; // safety cap for a near-max-strength cell (e.g. a heat/cold wave)
    private const int IsobarRingSteps = 48;   // sample points around the full closed ring

    public static void Compute(WindField field, double totalHours, List<FrontMarker> markers, List<FrontLine> lines,
        List<IsobarRing> isobars)
    {
        markers.Clear();
        lines.Clear();
        isobars.Clear();
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

                float absStrength = Math.Abs(c.Strength);
                for (int ring = 0; ring < MaxIsobarsPerCell; ring++)
                {
                    double level = (ring + 1) * IsobarStep;
                    if (level >= absStrength) break;
                    double scale = Math.Sqrt(Math.Log(absStrength / level));
                    isobars.Add(new IsobarRing
                    {
                        Points = BuildIsobarRing(c, ex[i], ez[i], scale),
                        IsLow = c.IsLow,
                        Level = (float)level,
                        RingIndex = ring
                    });
                }

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
                    // side (see WindFieldSimulation.ApplyOcclusion) — this renders a single open curve
                    // FREE-marching in that direction, using the cold/warm symbol per
                    // PressureCell.OcclusionKind (0/unset falls back to the generic occluded symbol, e.g.
                    // for saves migrated from before OcclusionKind existed).
                    FrontType occType = c.OcclusionKind switch
                    {
                        1 => FrontType.ColdOcclusion,
                        2 => FrontType.WarmOcclusion,
                        _ => FrontType.Occluded
                    };

                    double occPoleAngle = field.PolewardZSign(ez[i]) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
                    double coldAngle = occPoleAngle + Math.PI; // equatorward — the catching side

                    Vec2d[] occPts = BuildLeadingEdgeArc(c, ex[i], ez[i], coldAngle, c.ColdFrontReach);

                    // Own travel direction where it's meaningful; an occluded low can be near-stationary, so
                    // fall back to the catch direction itself rather than dividing by a near-zero speed.
                    NormalizeOrFallback(c.VelX, c.VelZ, speed, coldAngle, out double occRefX, out double occRefZ);

                    lines.Add(MakeLine(occType, strength, occPts, occRefX, occRefZ));
                    continue;
                }

                if (speed < StationarySpeed)
                {
                    // STATIONARY: a real stationary front sits on the (non-advancing) boundary between two
                    // systems, not a closed loop encircling one — so this renders a single open curve
                    // FREE-marching toward this low's nearest significant neighbour (a fallback direction if
                    // truly isolated). Its facing follows how that connecting line is actually moving — the
                    // neighbour's velocity RELATIVE to this low's own, since a stationary low's own velocity
                    // is by definition near zero and useless as a facing reference on its own.
                    int neighbour = FindNearestSignificantCell(significantIndices, i, ex, ez);
                    double fallbackAngle = field.PolewardZSign(ez[i]) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
                    double bearingAngle = neighbour >= 0
                        ? Math.Atan2(ez[neighbour] - ez[i], ex[neighbour] - ex[i])
                        : fallbackAngle;
                    float statReach = (c.ColdFrontReach + c.WarmFrontReach) * 0.5f;

                    Vec2d[] statPts = BuildLeadingEdgeArc(c, ex[i], ez[i], bearingAngle, statReach);

                    double relVelX = neighbour >= 0 ? field.Cells[neighbour].VelX - c.VelX : Math.Cos(bearingAngle);
                    double relVelZ = neighbour >= 0 ? field.Cells[neighbour].VelZ - c.VelZ : Math.Sin(bearingAngle);
                    double relSpeed = Math.Sqrt(relVelX * relVelX + relVelZ * relVelZ);
                    NormalizeOrFallback(relVelX, relVelZ, relSpeed, bearingAngle, out double statRefX, out double statRefZ);

                    lines.Add(MakeLine(FrontType.Stationary, strength, statPts, statRefX, statRefZ));
                    continue;
                }

                // MOVING, open wave: matching the classic Norwegian cyclone model, a mature wave low shows
                // BOTH a warm front (leading, ahead of the low's own direction of travel) and a cold front
                // (trailing, behind it) at once, meeting at the low's own centre — not just one or the
                // other. Each is tilted off the pure travel/anti-travel axis toward its real-world side
                // (warm leads AND tilts poleward — where warm air overruns; cold trails AND tilts
                // equatorward — where cold air digs in), giving the classic "comma"/wave shape rather than
                // two lines sitting exactly opposite each other — see WindField.MovingFrontAngles, the
                // single source of truth for this split (also used by StormBiasAt/FrontRegimeBiasAt so
                // precipitation intensifies along these exact same two edges, not some independently
                // drifting geography split). `WarmFrontReach`/`ColdFrontReach` (which already grow at
                // different rates — see "Front reach") give the two sides their own, independently
                // evolving extent.
                field.MovingFrontAngles(c.VelX, c.VelZ, ez[i], out double waveWarmAngle, out double waveColdAngle);

                Vec2d[] warmPts = BuildLeadingEdgeArc(c, ex[i], ez[i], waveWarmAngle, c.WarmFrontReach);
                Vec2d[] coldPts = BuildLeadingEdgeArc(c, ex[i], ez[i], waveColdAngle, c.ColdFrontReach);

                double refX = c.VelX / speed, refZ = c.VelZ / speed; // speed >= StationarySpeed here, never zero

                lines.Add(MakeLine(FrontType.Warm, strength, warmPts, refX, refZ));
                lines.Add(MakeLine(FrontType.Cold, strength, coldPts, refX, refZ));
            }
        }
    }

    /// <summary>Normalizes (vx, vz) of known length <paramref name="len"/> to a unit vector, or falls back
    /// to the unit vector at <paramref name="fallbackAngle"/> when too near zero to normalize safely
    /// (a front's own reference velocity can be near-stationary — see the Occluded/Stationary branches
    /// above).</summary>
    private static void NormalizeOrFallback(double vx, double vz, double len, double fallbackAngle,
        out double nx, out double nz)
    {
        if (len > 1e-6) { nx = vx / len; nz = vz / len; }
        else { nx = Math.Cos(fallbackAngle); nz = Math.Sin(fallbackAngle); }
    }

    /// <summary>Builds a computed <see cref="FrontLine"/> from its traced points — shared by all four
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

    /// <summary>Precomputes cumulative arc-length along an open curve's points, for O(1) reuse by
    /// <see cref="SampleAt"/> instead of rebuilding it on every sample.</summary>
    private static double[] BuildCumulative(Vec2d[] pts)
    {
        var cumLen = new double[pts.Length];
        for (int i = 1; i < pts.Length; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dz = pts[i].Y - pts[i - 1].Y;
            cumLen[i] = cumLen[i - 1] + Math.Sqrt(dx * dx + dz * dz);
        }
        return cumLen;
    }

    /// <summary>Finds the nearest OTHER significant cell to <paramref name="self"/> in ANY direction (low
    /// or high — <paramref name="candidateIndices"/> is expected to be <c>significantIndices</c>, every
    /// marker-eligible cell) — used to find a Stationary low's "connecting line" front target, which
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

    /// <summary>Samples <see cref="ArcStepCount"/>+1 points along the pressure cell's OWN elliptical
    /// boundary (<see cref="EllipseGeometry.RadiusToward"/>, so an elongated/oriented cell's true edge is
    /// used, not a circular approximation), spanning <see cref="ArcHalfAngle"/> either side of
    /// <paramref name="biasAngle"/>, with the boundary radius scaled by <paramref name="reach"/>
    /// (ColdFrontReach/WarmFrontReach — grows over the front's life, so the traced edge visibly grows
    /// with it). This IS the whole front line: no free march, no added noise — a front is drawn exactly as
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

    /// <summary>Samples <see cref="IsobarRingSteps"/>+1 points around a cell's own elliptical footprint,
    /// scaled by <paramref name="scale"/>, closing the loop (first point == last). See
    /// <see cref="IsobarRing"/> for the derivation of <paramref name="scale"/> from a target pressure
    /// level.</summary>
    private static Vec2d[] BuildIsobarRing(PressureCell cell, double ex, double ez, double scale)
    {
        var pts = new Vec2d[IsobarRingSteps + 1];
        for (int s = 0; s <= IsobarRingSteps; s++)
        {
            double angle = 2.0 * Math.PI * s / IsobarRingSteps;
            double dirX = Math.Cos(angle), dirZ = Math.Sin(angle);
            double r = EllipseGeometry.RadiusToward(cell, dirX, dirZ) * scale;
            pts[s] = new Vec2d(ex + dirX * r, ez + dirZ * r);
        }
        return pts;
    }

    /// <summary>Picks whichever ±90° perpendicular of a (normalized) tangent best aligns with
    /// <paramref name="refX"/>/<paramref name="refZ"/> (the low's own direction of travel — real front
    /// symbols point in the direction the front is advancing). Used when rendering (<see cref="SampleAt"/>)
    /// so a front symbol stays correctly oriented along the curve's whole length. Can become near-degenerate
    /// — both candidates close to equally aligned — once the curve's tangent nears parallel to ref (e.g. a
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
    /// the exact world position on the traced polyline (no added noise/wobble — a front IS the system's
    /// own edge, see <see cref="BuildLeadingEdgeArc"/>) and the local facing angle (whichever
    /// tangent-perpendicular best matches <see cref="FrontLine.RefX"/>/<see cref="FrontLine.RefZ"/> — see
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
}
