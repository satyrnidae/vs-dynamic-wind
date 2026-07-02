using System;
using System.Collections.Generic;
using DynamicWind.WindSystem;
using Vintagestory.API.MathTools;

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
/// A weather front for one LOW pressure system (highs carry no fronts — see FrontComputer). A
/// near-STATIONARY or OCCLUDED low encircles itself with a closed radial contour (<see cref="Radii"/>,
/// one radius per angular bucket around <see cref="CenterX"/>/<see cref="CenterZ"/>) — real fronts of
/// that kind do legitimately spiral/close around a single system. A MOVING, open-wave low instead gets
/// an open curve (<see cref="Points"/>) marched outward from the low along the local isobar — on a
/// flat, non-global map a moving front reads as a boundary sweeping across the land, not a ring — split
/// by real geography rather than direction of travel: a Warm curve on the poleward side (warm air
/// overruns, advancing poleward) and a Cold curve on the equatorward side (cold air digs in, advancing
/// equatorward) — the classic open-wave cyclone layout. Exactly one of
/// <see cref="Radii"/>/<see cref="Points"/> is populated, per <see cref="Type"/>.
/// <see cref="Strength"/> (|cell strength|) scales how prominent the front is drawn.
/// </summary>
public struct FrontLine
{
    public FrontType Type;
    public double Seed;
    public float Strength;

    // Stationary/Occluded only: closed radial contour around the system's centre.
    public double CenterX, CenterZ;
    public double[]? Radii;

    // Warm/Cold only: open marched polyline, world-space, ordered outward from the low's centre.
    public Vec2d[]? Points;
    // Warm/Cold only: the low's own (unit) direction of travel — matches the real convention that a
    // front's pip/triangle symbols point in the direction the front is advancing, not a purely geographic
    // direction. Using this (rather than the poleward/equatorward direction the curve itself is biased
    // toward) avoids picking a reference nearly PARALLEL to the curve's own tangent, which made the two
    // tangent-perpendiculars indistinguishable (near-zero dot product either way) and left the facing
    // side effectively arbitrary. At every sample the symbol faces whichever perpendicular of the LOCAL
    // tangent best aligns with this direction — see <see cref="ChooseFacing"/>.
    public double RefX, RefZ;
}

/// <summary>
/// Derives weather fronts from the (already-synced, already-persisted) pressure-cell field ALONE — a
/// pure, deterministic function of <see cref="WindField.CellPressureAt"/>, so both sides compute the
/// same fronts and they survive world reloads exactly as the cells do (no live-weather sampling, which
/// is stateful/side-dependent and was what made fronts jump on every reload).
///
/// Only LOW cells carry fronts — a real high is fair-weather subsidence with no frontal boundary of its
/// own. A moving low gets BOTH a warm front (its poleward half, where warm air overruns, advancing
/// poleward) and a cold front (its equatorward half, where cold air digs in, advancing equatorward) from
/// the same traced contour — split by real geography (<see cref="WindField.PolewardZSign"/>), not the
/// system's own direction of travel, since a
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

    // Stationary/Occluded contour tracing (closed radial loop).
    private const int AngleSamples = 36;               // contour resolution (every 10°)
    private const double ContourFrac = 0.45;           // trace the loop where pressure = this × the centre value
    private const double MaxContourRadiusFactor = 2.4; // cap the march at this × the cell radius
    private const int MarchSteps = 28;                 // radial samples per angle while tracing

    // Warm/Cold open-curve tracing. If a low has a real NEIGHBOUR low on a given side (poleward for
    // Warm, equatorward for Cold), that curve is an explicit Bezier JOIN out to the midpoint between the
    // two systems — real fronts often chain low-to-low ("join to the nearest, even a different front
    // type"), which a purely emergent gradient march kept getting wrong (it paired same-type curves and
    // tangled). With no neighbour on that side, the curve FREE-marches outward instead: nearly straight,
    // gently deflected by whatever else is nearby, trailing off along the contour.
    private const int OpenMarchStepCount = 40;           // steps per curve
    private const double MaxOpenCurveLengthFactor = 4.0; // FREE march length cap, × the cell radius
    private const double OpenContourFrac = 0.35;         // FREE march stop threshold, fraction of centre pressure
    private const double InitialOffsetFrac = 0.15;       // start-point offset from centre, × cell radius
    private const double GradSampleDistFront = 64.0;     // central-difference probe distance
    private const double GradMinMag = 1e-6;              // below this, nothing nearby to deflect toward
    private const double PullGain = 400.0;               // background gradient magnitude -> deflection strength
    private const double MaxFreeDeflect = 0.12;           // per-step cap on how far FREE deflects from straight
    private const int MinOpenCurvePoints = 3;             // guaranteed minimum output points

    public static void Compute(WindField field, double totalHours, List<FrontMarker> markers, List<FrontLine> lines)
    {
        markers.Clear();
        lines.Clear();
        if (field.Cells.Count == 0) return;

        double elapsed = totalHours - field.AnchorHours;

        // First pass: effective positions + which cells are significant LOWs (needed up front so the
        // neighbour lookup below can see every low regardless of its index in the list).
        var ex = new double[field.Cells.Count];
        var ez = new double[field.Cells.Count];
        var lowIndices = new List<int>();
        // Subset of lowIndices eligible as a JOIN target: MOVING, open-wave lows only. A Stationary or
        // Occluded system already draws its own full closed loop, so a curve marching INTO it would
        // either duplicate that loop or read as attaching to the wrong kind of boundary — those systems
        // still get markers/their own contour, they're just excluded from other lows' neighbour search.
        var movingLowIndices = new List<int>();
        for (int i = 0; i < field.Cells.Count; i++)
        {
            PressureCell c = field.Cells[i];
            if (Math.Abs(c.Strength) < MarkerMinStrength) continue;
            ex[i] = c.X + c.VelX * elapsed;
            ez[i] = c.Z + c.VelZ * elapsed;
            markers.Add(new FrontMarker { X = ex[i], Z = ez[i], IsHigh = !c.IsLow });
            if (!c.IsLow) continue;
            lowIndices.Add(i);
            bool occluded = totalHours < c.OccludedUntilTotalHours;
            double spd = Math.Sqrt(c.VelX * c.VelX + c.VelZ * c.VelZ);
            if (!occluded && spd >= StationarySpeed) movingLowIndices.Add(i);
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
                // OCCLUDED: the cold front has caught the warm front — render one merged closed loop.
                double maxR = MaxContourRadiusFactor * c.Radius;
                double[] radii = TraceContour(field, ex[i], ez[i], centre, maxR, totalHours);
                radii = ApplyFrontReach(radii, field, ez[i], c.ColdFrontReach, c.WarmFrontReach);
                lines.Add(new FrontLine
                {
                    Type = FrontType.Occluded,
                    Seed = c.BornTotalHours,
                    Strength = strength,
                    CenterX = ex[i],
                    CenterZ = ez[i],
                    Radii = radii
                });
                continue;
            }

            if (speed < StationarySpeed)
            {
                // STATIONARY: encircle the whole system with a closed loop.
                double maxR = MaxContourRadiusFactor * c.Radius;
                double[] radii = TraceContour(field, ex[i], ez[i], centre, maxR, totalHours);
                radii = ApplyFrontReach(radii, field, ez[i], c.ColdFrontReach, c.WarmFrontReach);
                lines.Add(new FrontLine
                {
                    Type = FrontType.Stationary,
                    Seed = c.BornTotalHours,
                    Strength = strength,
                    CenterX = ex[i],
                    CenterZ = ez[i],
                    Radii = radii
                });
                continue;
            }

            // MOVING, open wave: split by real geography, not the system's own direction of travel — the
            // warm front heads out the POLEWARD side (warm air overruns, advancing poleward into cold
            // territory), the cold front out the EQUATORWARD side (cold air digs in, advancing equatorward
            // into warm territory) — matches the classic open-wave cyclone model (warm front leads toward
            // the pole side, cold front trails toward the equator side). See WindField.PolewardZSign.
            double poleAngle = field.PolewardZSign(ez[i]) > 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
            double poleX = Math.Cos(poleAngle), poleZ = Math.Sin(poleAngle);

            // A real neighbouring low strictly on the poleward side becomes this low's Warm JOIN target;
            // one strictly on the equatorward side becomes the Cold JOIN target — the nearest in each
            // direction, regardless of what type of front the neighbour itself ends up drawing. With no
            // neighbour on a side, that curve FREE-marches instead (see MarchOrJoin).
            FindSidedNeighbour(movingLowIndices, i, ex, ez, poleX, poleZ, out int poleNeighbour, out _);
            FindSidedNeighbour(movingLowIndices, i, ex, ez, -poleX, -poleZ, out int equatorNeighbour, out _);

            Vec2d[] warmPts = MarchOrJoin(field, i, ex[i], ez[i], centre, c.Radius, poleAngle, c.WarmFrontReach,
                c.VelX, c.VelZ, poleNeighbour >= 0, poleNeighbour >= 0 ? ex[poleNeighbour] : 0, poleNeighbour >= 0 ? ez[poleNeighbour] : 0,
                totalHours);
            Vec2d[] coldPts = MarchOrJoin(field, i, ex[i], ez[i], centre, c.Radius, poleAngle + Math.PI, c.ColdFrontReach,
                c.VelX, c.VelZ, equatorNeighbour >= 0, equatorNeighbour >= 0 ? ex[equatorNeighbour] : 0, equatorNeighbour >= 0 ? ez[equatorNeighbour] : 0,
                totalHours);

            double refX = c.VelX / speed, refZ = c.VelZ / speed; // speed >= StationarySpeed here, never zero

            lines.Add(new FrontLine
            {
                Type = FrontType.Warm,
                Seed = c.BornTotalHours,
                Strength = strength,
                Points = warmPts,
                RefX = refX,
                RefZ = refZ
            });
            lines.Add(new FrontLine
            {
                Type = FrontType.Cold,
                Seed = c.BornTotalHours,
                Strength = strength,
                Points = coldPts,
                RefX = refX,
                RefZ = refZ
            });
        }
    }

    /// <summary>Finds the nearest OTHER candidate low strictly on the (<paramref name="dirX"/>,
    /// <paramref name="dirZ"/>) side of low <paramref name="self"/> (dot product of the offset with that
    /// direction &gt; 0) — used to find each front curve's explicit JOIN target, if any. <paramref
    /// name="candidateIndices"/> should already be restricted to eligible join targets (MOVING lows —
    /// see <c>movingLowIndices</c> in <see cref="Compute"/>), not every low.</summary>
    private static void FindSidedNeighbour(List<int> candidateIndices, int self, double[] ex, double[] ez,
        double dirX, double dirZ, out int neighbourIndex, out double neighbourDist)
    {
        neighbourIndex = -1;
        neighbourDist = double.MaxValue;
        for (int k = 0; k < candidateIndices.Count; k++)
        {
            int j = candidateIndices[k];
            if (j == self) continue;
            double dx = ex[j] - ex[self], dz = ez[j] - ez[self];
            if (dx * dirX + dz * dirZ <= 0) continue; // not on this side
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < neighbourDist) { neighbourDist = dist; neighbourIndex = j; }
        }
    }

    /// <summary>
    /// Builds one Warm or Cold open curve: an explicit Bezier JOIN to <paramref name="neighbourX"/>/
    /// <paramref name="neighbourZ"/> if <paramref name="hasNeighbour"/>, otherwise a FREE march outward.
    /// See <see cref="BuildJoinCurve"/>/<see cref="MarchFree"/>.
    /// </summary>
    private static Vec2d[] MarchOrJoin(WindField field, int cellIndex, double ex, double ez, double centre,
        double cellRadius, double biasAngle, float reach, double velX, double velZ,
        bool hasNeighbour, double neighbourX, double neighbourZ, double totalHours)
    {
        double biasX = Math.Cos(biasAngle), biasZ = Math.Sin(biasAngle);
        double startX = ex + biasX * InitialOffsetFrac * cellRadius;
        double startZ = ez + biasZ * InitialOffsetFrac * cellRadius;

        return hasNeighbour
            ? BuildJoinCurve(startX, startZ, biasX, biasZ, ex, ez, neighbourX, neighbourZ)
            : MarchFree(field, cellIndex, ex, ez, centre, startX, startZ, biasX, biasZ, cellRadius, reach, velX, velZ, totalHours);
    }

    /// <summary>
    /// JOIN case: a quadratic Bezier from just outside the low's own centre out to the midpoint between
    /// it and a real neighbouring low on this side — "join to the nearest, even a different front type"
    /// instead of independently marching two curves that may tangle. The control point keeps the curve
    /// leaving along the initial bias direction (poleward/equatorward) before bending toward the
    /// neighbour, so it still reads as a front leaving its own low first. Always connects the FULL way to
    /// the midpoint as soon as a neighbour exists — a low's ColdFrontReach/WarmFrontReach grow at
    /// different rates, so gating the join on either side's own reach let the two ends of the same
    /// connection grow in at different speeds and visibly fail to meet; contiguousness matters more here
    /// than animating the join in over time.
    /// </summary>
    private static Vec2d[] BuildJoinCurve(double startX, double startZ, double biasX, double biasZ,
        double ex, double ez, double neighbourX, double neighbourZ)
    {
        // Midpoint of the two low CENTRES (not the offset start points) so both sides of a join —
        // computed independently from each low's own perspective — always agree on the exact same
        // target and therefore always meet, regardless of where each curve's own start point sits.
        double endX = (ex + neighbourX) / 2.0;
        double endZ = (ez + neighbourZ) / 2.0;

        double legLen = Math.Sqrt((endX - startX) * (endX - startX) + (endZ - startZ) * (endZ - startZ));
        double ctrlX = startX + biasX * legLen * 0.5;
        double ctrlZ = startZ + biasZ * legLen * 0.5;

        var points = new Vec2d[OpenMarchStepCount + 1];
        for (int s = 0; s <= OpenMarchStepCount; s++)
        {
            double t = (double)s / OpenMarchStepCount;
            double omt = 1 - t;
            double px = omt * omt * startX + 2 * omt * t * ctrlX + t * t * endX;
            double pz = omt * omt * startZ + 2 * omt * t * ctrlZ + t * t * endZ;
            points[s] = new Vec2d(px, pz);
        }
        return points;
    }

    /// <summary>
    /// FREE case (no neighbour on this side): marches outward in a nearly straight line, gently deflected
    /// by the "background" pressure field induced by every OTHER cell (<see
    /// cref="WindField.CellPressureAt(double,double,double,int)"/> excluding this low) — an isolated low
    /// has ~zero background gradient anywhere along the march, so it stays straight (fixing the old
    /// isobar-following march, which for a lone radially-symmetric low was by definition a closed contour
    /// and always spiralled). Trails off once the low's own pressure falls under <paramref name="reach"/>
    /// (ColdFrontReach/WarmFrontReach)'s threshold, but is also truncated the moment the drawn symbol
    /// would end up facing away from the low's own direction of travel (<paramref name="velX"/>/<paramref
    /// name="velZ"/>) — beyond that point it no longer reads as a front trailing a moving system.
    /// </summary>
    private static Vec2d[] MarchFree(WindField field, int cellIndex, double ex, double ez, double centre,
        double startX, double startZ, double biasX, double biasZ, double cellRadius, float reach,
        double velX, double velZ, double totalHours)
    {
        double maxLen = MaxOpenCurveLengthFactor * cellRadius;
        double stepLen = maxLen / OpenMarchStepCount;
        double level = OpenContourFrac / Math.Max(1.0, reach) * centre;

        var points = new List<Vec2d> { new Vec2d(startX, startZ) };
        double px = startX, pz = startZ;
        double dirX = biasX, dirZ = biasZ;

        for (int step = 0; step < OpenMarchStepCount; step++)
        {
            double d = GradSampleDistFront;
            double gx = (field.CellPressureAt(px + d, pz, totalHours, cellIndex) - field.CellPressureAt(px - d, pz, totalHours, cellIndex)) / (2 * d);
            double gz = (field.CellPressureAt(px, pz + d, totalHours, cellIndex) - field.CellPressureAt(px, pz - d, totalHours, cellIndex)) / (2 * d);
            double gmag = Math.Sqrt(gx * gx + gz * gz);

            double newX = dirX, newZ = dirZ; // default: keep marching straight (nothing nearby)
            if (gmag > GradMinMag)
            {
                double pullX = gx / gmag, pullZ = gz / gmag;
                double deflect = Math.Min(MaxFreeDeflect, gmag * PullGain);
                double bx = dirX + pullX * deflect;
                double bz = dirZ + pullZ * deflect;
                double blen = Math.Sqrt(bx * bx + bz * bz);
                if (blen > 1e-9) { newX = bx / blen; newZ = bz / blen; }
            }

            double nx = px + newX * stepLen, nz = pz + newZ * stepLen;
            double p = field.CellPressureAt(nx, nz, totalHours);
            bool stopHere = Math.Abs(p) <= Math.Abs(level);

            points.Add(new Vec2d(nx, nz));
            px = nx; pz = nz; dirX = newX; dirZ = newZ;

            if (stopHere) break;
        }

        // Geometric fallback: guarantee MinOpenCurvePoints even in a degenerate field, no more sampling.
        while (points.Count < MinOpenCurvePoints)
        {
            px += dirX * stepLen; pz += dirZ * stepLen;
            points.Add(new Vec2d(px, pz));
        }

        // Trail off, but stop once the drawn symbol would face away from the system's own direction of
        // travel — beyond that the curve has bent back far enough it no longer reads as this (moving)
        // low's own trailing front. Uses the SAME reference (the low's own travel direction) SampleAt
        // renders facing with (see ChooseFacing) — since that's also the disambiguating reference, the
        // chosen facing is by construction never more than ~90° from travel, so this rarely trims
        // anything; it exists for the sharp-bend edge cases where travel briefly aligns with the tangent.
        double travelLen = Math.Sqrt(velX * velX + velZ * velZ);
        if (travelLen > 1e-6 && points.Count > MinOpenCurvePoints)
        {
            double travelX = velX / travelLen, travelZ = velZ / travelLen;
            int cut = points.Count;
            for (int k = 1; k < points.Count; k++)
            {
                double tx = points[k].X - points[k - 1].X, tz = points[k].Y - points[k - 1].Y;
                double tl = Math.Sqrt(tx * tx + tz * tz);
                if (tl < 1e-9) continue;
                tx /= tl; tz /= tl;
                ChooseFacing(tx, tz, travelX, travelZ, out double faceX, out double faceZ);
                if (faceX * travelX + faceZ * travelZ < 0) { cut = k; break; }
            }
            if (cut >= MinOpenCurvePoints) points.RemoveRange(cut, points.Count - cut);
        }

        return points.ToArray();
    }

    /// <summary>Picks whichever ±90° perpendicular of a (normalized) tangent best aligns with
    /// <paramref name="refX"/>/<paramref name="refZ"/> (the low's own direction of travel — real front
    /// symbols point in the direction the front is advancing). Used both when rendering
    /// (<see cref="SampleAt"/>) and when trimming a FREE curve, so a front symbol stays correctly oriented
    /// along a curve's whole length instead of a sign fixed once at the (possibly near-degenerate) start.
    /// Travel is roughly perpendicular to a poleward/equatorward-biased curve in the common case, so this
    /// comparison is well-conditioned — unlike comparing against the curve's own (near-parallel) bias
    /// direction, which made the two candidates indistinguishable.</summary>
    private static void ChooseFacing(double tanX, double tanZ, double refX, double refZ, out double faceX, out double faceZ)
    {
        double p1x = -tanZ, p1z = tanX;
        double p2x = tanZ, p2z = -tanX;
        if (p1x * refX + p1z * refZ >= p2x * refX + p2z * refZ) { faceX = p1x; faceZ = p1z; }
        else { faceX = p2x; faceZ = p2z; }
    }

    /// <summary>Total drawable world-space length of a front's curve: the traced polyline length for
    /// an open Warm/Cold curve, or the mean-radius circumference for a closed Stationary/Occluded loop.</summary>
    public static double CurveLength(in FrontLine f)
    {
        if (f.Points != null)
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
        if (f.Radii == null || f.Radii.Length == 0) return 0;
        double rMean = 0;
        for (int i = 0; i < f.Radii.Length; i++) rMean += f.Radii[i];
        rMean /= f.Radii.Length;
        return rMean * 2 * Math.PI;
    }

    /// <summary>
    /// Uniform curve-parameter sample: <paramref name="t"/> in [0,1] spans the whole drawable curve —
    /// the full loop for a Stationary/Occluded contour, or the full open curve for a Warm/Cold march.
    /// Returns a world position (with the per-tile curvature wobble already applied) and the local
    /// facing angle (outward-radial for a loop; for an open curve, whichever tangent-perpendicular best
    /// matches <see cref="FrontLine.RefX"/>/<see cref="FrontLine.RefZ"/> — see <see cref="ChooseFacing"/>).
    /// </summary>
    public static void SampleAt(in FrontLine f, double t, double curveAmpL, double totalHours,
        out double wx, out double wz, out double facingAngle)
    {
        if (f.Points != null)
        {
            Vec2d[] pts = f.Points;
            if (pts.Length < 2)
            {
                wx = pts.Length == 1 ? pts[0].X : 0;
                wz = pts.Length == 1 ? pts[0].Y : 0;
                facingAngle = 0;
                return;
            }

            var cum = new double[pts.Length];
            for (int i = 1; i < pts.Length; i++)
            {
                double dx = pts[i].X - pts[i - 1].X, dz = pts[i].Y - pts[i - 1].Y;
                cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dz * dz);
            }
            double target = t * cum[pts.Length - 1];
            int seg = 0;
            while (seg < pts.Length - 2 && cum[seg + 1] < target) seg++;
            double segLen = cum[seg + 1] - cum[seg];
            double frac = segLen > 1e-9 ? (target - cum[seg]) / segLen : 0;

            double tanX = pts[seg + 1].X - pts[seg].X, tanZ = pts[seg + 1].Y - pts[seg].Y;
            double tanLen = Math.Sqrt(tanX * tanX + tanZ * tanZ);
            if (tanLen > 1e-9) { tanX /= tanLen; tanZ /= tanLen; }

            wx = pts[seg].X + (pts[seg + 1].X - pts[seg].X) * frac;
            wz = pts[seg].Y + (pts[seg + 1].Y - pts[seg].Y) * frac;

            double wob = curveAmpL * Curve(target, f.Seed, totalHours);
            wx += wob * -tanZ;
            wz += wob * tanX;

            ChooseFacing(tanX, tanZ, f.RefX, f.RefZ, out double faceX, out double faceZ);
            facingAngle = Math.Atan2(faceZ, faceX);
            return;
        }

        double theta = t * 2 * Math.PI;
        double rr = RadiusAt(f.Radii!, theta) + curveAmpL * Curve(theta * (CurveLength(f) / (2 * Math.PI)), f.Seed, totalHours);
        wx = f.CenterX + rr * Math.Cos(theta);
        wz = f.CenterZ + rr * Math.Sin(theta);
        facingAngle = Math.Atan2(Math.Sin(theta), Math.Cos(theta));
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

    /// <summary>Scales each angle bucket of a traced contour by <paramref name="warmReach"/> on the
    /// poleward side or <paramref name="coldReach"/> on the equatorward side (relative to
    /// <see cref="WindField.PolewardZSign"/> at the cell's own position), so a low's cold and warm sides
    /// can independently stretch as PressureCell.ColdFrontReach/WarmFrontReach evolve. Blended smoothly
    /// across the whole ring by <c>sin(theta)·poleSign</c> rather than switched at a hard threshold — a
    /// binary switch made the two opposite crossing points (due "east"/"west" of the low) jump discontinuously
    /// whenever ColdFrontReach and WarmFrontReach differed, reading as a broken/non-contiguous loop.</summary>
    private static double[] ApplyFrontReach(double[] radii, WindField field, double ez, float coldReach, float warmReach)
    {
        double poleSign = field.PolewardZSign(ez);
        var scaled = new double[radii.Length];
        for (int a = 0; a < radii.Length; a++)
        {
            double theta = a / (double)radii.Length * 2 * Math.PI;
            double poleAmt = (Math.Sin(theta) * poleSign + 1.0) * 0.5; // 0 at equatorward point, 1 at poleward point
            double reach = coldReach + (warmReach - coldReach) * poleAmt;
            scaled[a] = radii[a] * reach;
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
