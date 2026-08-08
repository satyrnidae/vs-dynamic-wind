using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Offline generator: bakes the clew-rope rig's aim-and-stretch solve into per-frame keyframe data for the
// "rotatesail" shape animation, using the real VintagestoryAPI.dll primitives (Mat4f/Quaternionf/
// ShapeElement) so this can't silently disagree with how the engine itself poses the model.
//
// The mast/spar/sail swing and sail-billow effect are hand-authored in Anego Studios' model tool (see
// starting-point.json, the checked-in export) - this tool ONLY solves the clew ropes, since the authoring
// tool can't track a fixed hull anchor point through the swing. Each branch's clew rope is a single rigid
// chain rooted under that branch's ClewOrigin (ClewORIGIN/ClewORIGINFurled/ClewRopeOrigin); only the
// chain's OWN root gets a keyframe (rotation + stretchY) - every segment below it (incl. the terminal knot
// mesh) is a plain [0,16,0] translation with no rotation of its own, so it rigidly follows the root through
// the composed transform for free. The old rig's per-branch knot counter-rotation (keeping the knot mesh
// looking tied to a fixed post) is gone: the new model's terminal knot pieces just ride the rope.
//
// Usage: dotnet run -- [output keyframes.json]  (defaults to starting-point.json as input, clewkeyframes.json as output)
//
// IMPORTANT: GetLocalTransformMatrix does NOT reset its `output` matrix to identity - it post-multiplies
// Translate/Rotate/Scale onto whatever is already there. Every call below resets its scratch matrix with
// Mat4f.Identity() first; skipping that silently compounds stale data from the previous loop iteration into
// wrong, non-degenerate-looking garbage.

#region Shape loading

string shapePath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "starting-point.json");
string outPath = args.Length > 1 ? args[1] : "clewkeyframes.json";

string json = File.ReadAllText(shapePath);
Shape shape = JsonConvert.DeserializeObject<Shape>(json)!;

var byName = new Dictionary<string, ShapeElement>(StringComparer.OrdinalIgnoreCase);
void Walk(ShapeElement[]? elements, ShapeElement? parent)
{
    if (elements == null) return;
    foreach (ShapeElement e in elements)
    {
        e.ParentElement = parent;
        if (e.Name != null) byName[e.Name] = e;
        Walk(e.Children, e);
    }
}
Walk(shape.Elements, null);

ShapeElement Find(string name) =>
    byName.TryGetValue(name, out ShapeElement? e) ? e : throw new Exception($"element not found: {name}");

const string RotatorName = "MastORIGIN";
const string OriginName = "ORIGIN";
const string AnchorR = "ClewKnotAnchorR";
const string AnchorL = "ClewKnotAnchorL";

#endregion

#region Branch definitions

// Mirrors the shape's own naming exactly (see starting-point.json) - keep in sync if it's regenerated
// from a new export. StbdPulley/PortPulley only need a reciprocal-scale keyframe (see Solve): every other
// element in the chain between root and the pulley/knot is a pure [0,16,0] translation with no pose of
// its own, so it never needs an explicit keyframe.
//
// SailRoot's own frame-0/89/179 offset (pixel units, matching From/RotationOrigin) is NOT solved by this
// tool - it's hand-authored directly in boat-sailed-animations.json's "rotatesail" animation, alongside a
// rotationY that (up to rounding) already agrees with FrameForAngle's own angle<->frame mapping. The
// offset does NOT similarly fall out for free: SailFurled/SailHalf/SailUnfurled each pivot the sail mesh
// about their OWN local rotationOrigin rather than the true mast axis, so the hand-authored keyframes
// carry a small compensating translation (up to 4px) to keep the mesh visually aligned with the mast
// column through the swing - asymmetric between frame 0 and frame 179 for SailHalf/SailUnfurled (e.g.
// SailHalf: (-4,0,0) at frame 0 vs (0.6,0,-4) at frame 179), since Anego Studios' authoring tool doesn't
// guarantee a symmetric correction each side. The clew-rope solve chain MUST reproduce this same
// translation at every densified frame in between (via the same frame-fraction lerp the engine itself
// applies between 0/89/179), or the aim-and-stretch solve targets the anchor from a rope root that isn't
// actually where the rendered mesh puts it - previously this offset was ignored entirely (assumed zero),
// which reproduced correctly only at frame 89 (where both branches' offset is zero by construction) and
// drifted with growing error toward each extreme, worst for SailHalf/SailUnfurled's larger offsets -
// keep BOTH frame-0/179 constants below and boat-sailed-animations.json's own SailX keyframes in sync if
// the swing is ever re-authored.
Branch[] branches =
{
    new("SailFurled",   "ClewORIGINFurled", "ClewRopeR5", "ClewRopeL5", "Block11", "BlockL4",
        Frame0Offset: new float[] { 2f, 0f, -2f }, Frame179Offset: new float[] { 2f, 0f, 2f }),
    new("SailHalf",     "ClewRopeOrigin",   "RClew1",     "LClew1",     "Block4",  "BlockL2",
        Frame0Offset: new float[] { -4f, 0f, 0f }, Frame179Offset: new float[] { 0.6f, 0f, -4f }),
    new("SailUnfurled", "ClewORIGIN",       "ClewRopeR1", "ClewRopeL1", "Block1",  "BlockL",
        Frame0Offset: new float[] { -3f, 0f, 0f }, Frame179Offset: new float[] { 1.1f, 0f, -4f }),
};

#endregion

#region Matrix helpers

float[] Sixteenths(double[]? v) => v == null ? new float[3] : new[] { (float)v[0] / 16f, (float)v[1] / 16f, (float)v[2] / 16f };

// Round-trip ("R") formatting drops the decimal point for exact integer values (e.g. "0" or "1" instead
// of "0.0"/"1.0"), which reads as an int rather than a float once spliced into the shape JSON - out of
// step with the rest of the file's convention of always writing floats with a decimal point.
string Fmt(float v)
{
    string s = v.ToString("R");
    return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
}

float Dot(float[] m, int row, float[] v) => m[row] * v[0] + m[4 + row] * v[1] + m[8 + row] * v[2];

bool Normalize(float[] v)
{
    double len = Math.Sqrt((double)v[0] * v[0] + (double)v[1] * v[1] + (double)v[2] * v[2]);
    if (len < 1e-9) return false;
    v[0] = (float)(v[0] / len); v[1] = (float)(v[1] / len); v[2] = (float)(v[2] / len);
    return true;
}

// Recovers Rx*Ry*Rz angles in degrees from a rotation matrix. This decomposition has TWO equally valid
// solutions whenever cos(degY) != 0: the one below, and the alternate branch degY' = 180-degY,
// degX' = degX+180, degZ' = degZ+180 (all wrapped to +-180) - both compose back to the same rotation
// matrix. Picking a fixed branch every frame with no regard for the previous frame lets adjacent baked
// frames land on OPPOSITE branches for a physically smooth rotation (confirmed in the old baked data:
// ClewRopeR1 rotationX flips sign and rotationZ jumps past 90deg between frames 149 and 179), which reads
// as a sign-flip bounce once interpolated at runtime. `prev` (the previous frame's own decomposition for
// this same element, or null for the first frame baked in a walk) picks whichever branch stays closer to
// it, so a continuously-varying rotation bakes to a continuously-varying Euler triple.
void ToEulerXYZ(float[] m, (float x, float y, float z)? prev, out float degX, out float degY, out float degZ)
{
    float ay = (float)Math.Asin(GameMath.Clamp(m[8], -1f, 1f)) * GameMath.RAD2DEG;
    float ax = (float)Math.Atan2(-m[9], m[10]) * GameMath.RAD2DEG;
    float az = (float)Math.Atan2(-m[4], m[0]) * GameMath.RAD2DEG;

    if (prev == null) { degX = ax; degY = ay; degZ = az; return; }

    float bx = GameMath.AngleDegDistance(0f, ax + 180f);
    float by = GameMath.AngleDegDistance(0f, 180f - ay);
    float bz = GameMath.AngleDegDistance(0f, az + 180f);

    float distA = Math.Abs(GameMath.AngleDegDistance(prev.Value.x, ax))
        + Math.Abs(GameMath.AngleDegDistance(prev.Value.y, ay))
        + Math.Abs(GameMath.AngleDegDistance(prev.Value.z, az));
    float distB = Math.Abs(GameMath.AngleDegDistance(prev.Value.x, bx))
        + Math.Abs(GameMath.AngleDegDistance(prev.Value.y, by))
        + Math.Abs(GameMath.AngleDegDistance(prev.Value.z, bz));

    if (distB < distA) { degX = bx; degY = by; degZ = bz; }
    else { degX = ax; degY = ay; degZ = az; }
}

// Composes GetLocalTransformMatrix from `leaf` up through its parents, stopping BEFORE
// `stopBeforeAncestor`. Applied to `startPoint` (a point in leaf's OWN local frame, e.g. (0,0,0,1) for
// leaf's own origin, or an offset from it like a far corner/face centre), this gives that point's rest
// position expressed in stopBeforeAncestor's own local space (the same space stopBeforeAncestor's own
// From/RotationOrigin values are already written in).
float[] LiftToAncestorSpace(ShapeElement leaf, ShapeElement stopBeforeAncestor, float[]? startPoint = null)
{
    float[] p = startPoint ?? new float[] { 0, 0, 0, 1 };
    float[] local = Mat4f.Create();
    float[] tmp = new float[4];
    for (ShapeElement? e = leaf; e != null && e != stopBeforeAncestor; e = e.ParentElement)
    {
        Mat4f.Identity(local);
        e.GetLocalTransformMatrix(0, local);
        Mat4f.MulWithVec4(local, p, tmp);
        Array.Copy(tmp, p, 4);
    }
    return p;
}

// Walks DOWN from `root` to the chain's true visual terminus, returning the full path (root first, leaf
// last). The pulley is a decorative rider attached to (grouped under) a rope segment, NOT the rope's own
// endpoint - it never counts as part of the "real" chain, even when it is the only child at a fork (its
// own children are just its further decorative sub-parts). So at every fork, take whichever child is NOT
// `pulley`; if the pulley is the only child, the walk stops at the CURRENT element (the segment the pulley
// is grouped under) instead of descending into it.
List<ShapeElement> WalkToTrueLeaf(ShapeElement root, ShapeElement pulley)
{
    var path = new List<ShapeElement> { root };
    ShapeElement leaf = root;
    while (leaf.Children != null && leaf.Children.Length > 0)
    {
        ShapeElement? nonPulley = leaf.Children.FirstOrDefault(c => c != pulley);
        if (nonPulley == null) break;
        leaf = nonPulley;
        path.Add(leaf);
    }
    return path;
}

// Builds the rotator-parent-space -> clew-root's-parent-space matrix for `chain` at rest (angle 0).
float[] RestChainMatrix(ElementPose[] chain)
{
    float[] mC = Mat4f.Create();
    Mat4f.Identity(mC);
    float[] mB = Mat4f.Create();
    float[] mD = Mat4f.Create();
    foreach (ElementPose tf in chain)
    {
        Mat4f.Identity(mB);
        tf.ForElement.GetLocalTransformMatrix(0, mB, tf);
        Mat4f.Mul(mD, mC, mB);
        Mat4f.Copy(mC, mD);
    }
    return mC;
}

#endregion

#region Anchor capture

Sheet Capture(ElementPose[] chain, string ropeName, string pulleyName, string anchorName)
{
    ShapeElement rootElem = Find(ropeName);
    ShapeElement pulleyElem = Find(pulleyName);
    if (rootElem.From == null) throw new Exception($"{ropeName} has no From");

    float[] pivot = Sixteenths(rootElem.RotationOrigin);
    float[] from = Sixteenths(rootElem.From);

    float[] restRotation = Mat4f.Create();
    Mat4f.Identity(restRotation);
    Mat4f.RotateByXYZ(restRotation,
        (float)rootElem.RotationX * GameMath.DEG2RAD,
        (float)rootElem.RotationY * GameMath.DEG2RAD,
        (float)rootElem.RotationZ * GameMath.DEG2RAD);

    // The anchor is a fixed hull point under ORIGIN, not a descendant of rootElem - capture its rest
    // position directly in ORIGIN's own local space. `Anchor` is what Solve() aims the chain AT
    // (`target = mA * Anchor`).
    //
    // The anchor element is a real box (deck knot marker), not a point - querying its own local origin
    // (0,0,0), as an earlier version did, ties the rope to one specific CORNER of that box (the same
    // "corner vs. extent" mistake already found and fixed for the rope's own terminus, here on the OTHER
    // end of the rope) rather than a natural tie-point. Use the centre of whichever FACE of the anchor box
    // faces the rope instead - found by comparing each of the 6 face-centre candidates' rest position
    // against the rope root's own natural rest position and picking the closest, rather than hardcoding an
    // assumption about which local axis is "fore" on this particular anchor's own (rotated ~10°) geometry.
    ShapeElement anchorElem = Find(anchorName);
    ShapeElement origin = Find(OriginName);
    if (anchorElem.From == null || anchorElem.To == null) throw new Exception($"{anchorName} has no From/To");
    float[] anchorFrom = Sixteenths(anchorElem.From);
    float[] anchorTo = Sixteenths(anchorElem.To);
    float[] anchorExtent = { anchorTo[0] - anchorFrom[0], anchorTo[1] - anchorFrom[1], anchorTo[2] - anchorFrom[2] };
    float[] rootRestInOrigin = LiftToAncestorSpace(rootElem, origin);

    (float x, float y, float z) FaceCandidate(int axis, bool far) => axis switch
    {
        0 => (far ? anchorExtent[0] : 0f, anchorExtent[1] / 2f, anchorExtent[2] / 2f),
        1 => (anchorExtent[0] / 2f, far ? anchorExtent[1] : 0f, anchorExtent[2] / 2f),
        _ => (anchorExtent[0] / 2f, anchorExtent[1] / 2f, far ? anchorExtent[2] : 0f),
    };

    float[]? bestAnchor = null;
    float bestDistSq = float.MaxValue;
    for (int axis = 0; axis < 3; axis++)
    for (int sign = 0; sign < 2; sign++)
    {
        var (cx, cy, cz) = FaceCandidate(axis, sign == 1);
        float[] candidate = LiftToAncestorSpace(anchorElem, origin, new[] { cx, cy, cz, 1f });
        float distSq = (float)(Math.Pow(candidate[0] - rootRestInOrigin[0], 2)
            + Math.Pow(candidate[1] - rootRestInOrigin[1], 2) + Math.Pow(candidate[2] - rootRestInOrigin[2], 2));
        if (distSq < bestDistSq) { bestDistSq = distSq; bestAnchor = candidate; }
    }
    float[] anchor = bestAnchor!;

    // `offset` (the vector Solve() rotates+scales) must be the chain's OWN natural, unstretched reach -
    // root's pivot to the TRUE terminal segment's rest position - NOT the anchor's own position. An
    // earlier version used the anchor directly here, which is only valid if the rope's natural rest length
    // happens to already coincide with the anchor - checked directly (composing the real element chain,
    // not just Solve()'s own self-consistent target formula) and found FALSE by ~0.4-1.5 blocks even at
    // rest (scale=1, angle=0) for every branch: the aim-and-stretch technique was rotating+scaling a vector
    // that pointed at the anchor, but the actual rendered rope (root + its plain [0,16,0] segments, unaware
    // of the anchor) doesn't travel along that vector - it travels along its OWN natural direction, merely
    // rotated and stretched as a rigid shape. Using the chain's real natural endpoint as the vector to aim
    // fixes this: the ROTATION now carries the chain's true direction onto the anchor's true direction, and
    // the SCALE stretches the chain's true rest length to the anchor's true distance, instead of assuming
    // the anchor's own direction/distance were already the chain's natural ones.
    //
    // The leaf is a real box with extent (e.g. the terminal rope segment spans 4px past its own origin)
    // that should also count toward "how far does this rope really reach" - measuring only the origin
    // under-counts the natural reach and makes Solve() over-stretch to compensate, visibly overshooting the
    // anchor by roughly the leaf's own (scaled) extent. The leaf is never the pulley (WalkToTrueLeaf stops
    // at whichever segment the pulley is grouped under, since the pulley is a decorative rider, not the
    // rope's own endpoint - see its own comment), so it - origin AND far corner alike - is always a plain,
    // unkeyframed continuation of the chain and scales uniformly with everything else. No split needed:
    // FixedTail is always zero (Solve()/ReconstructPos still carry it generically in case a future branch's
    // topology needs it again, but nothing currently sets it nonzero).
    List<ShapeElement> leafPath = WalkToTrueLeaf(rootElem, pulleyElem);
    ShapeElement trueLeaf = leafPath[^1];
    if (trueLeaf.From == null || trueLeaf.To == null) throw new Exception($"{trueLeaf.Name} has no From/To");

    ElementPose[] leafChain = leafPath.Select(e => new ElementPose { ForElement = e }).ToArray();
    float[] mLeafRest = RestChainMatrix(leafChain);
    float[] leafFrom = Sixteenths(trueLeaf.From);
    float[] leafTo = Sixteenths(trueLeaf.To);
    float[] leafFarCorner = { leafTo[0] - leafFrom[0], leafTo[1] - leafFrom[1], leafTo[2] - leafFrom[2], 1 };
    float[] leafNaturalInRootParent = new float[4];
    Mat4f.MulWithVec4(mLeafRest, leafFarCorner, leafNaturalInRootParent);

    // offset must be expressed in rootElem's OWN pre-rotation local frame - the same frame Solve() later
    // re-rotates by restRotation to place things back in the parent frame. leafNaturalInRootParent is
    // already in that PARENT frame, so un-rotate by R^-1 first.
    float[] diff =
    {
        leafNaturalInRootParent[0] - pivot[0],
        leafNaturalInRootParent[1] - pivot[1],
        leafNaturalInRootParent[2] - pivot[2],
    };
    float[] invRestRotation = Mat4f.Create();
    Mat4f.Invert(invRestRotation, restRotation);
    float[] offset =
    {
        Dot(invRestRotation, 0, diff),
        Dot(invRestRotation, 1, diff),
        Dot(invRestRotation, 2, diff),
    };
    float[] fixedTail = { 0f, 0f, 0f };

    if (Math.Abs(offset[1]) < 1e-6f) throw new Exception($"{ropeName} offset has no axial run");

    // Anchor must stay the TRUE fixed point in ORIGIN's own space, not reconstructed via pivot+R*offset:
    // Solve()'s `mA * sheet.Anchor` expects an ORIGIN-space input (mA maps ORIGIN-space -> the current
    // frame's swung rope-root-local-space). An already-local-space reconstruction would double-transform it,
    // collapsing the solved target toward the rope root's own frame instead of the real deck anchor.
    return new Sheet
    {
        RootElem = rootElem, Pivot = pivot, Offset = offset, FixedTail = fixedTail, RestRotation = restRotation,
        Anchor = anchor,
        PulleyElem = pulleyElem,
    };
}

#endregion

#region Solve

// (rootRx,rootRy,rootRz,scale), or null if degenerate (should not happen across the full +/-90deg sweep).
// `prevAbs` is the caller's running Euler state for this sheet - read for continuity branch selection,
// then updated in place to this frame's own absolute (pre-rest-subtraction) result for the next call.
(float rootRx, float rootRy, float rootRz, float scale)? Solve(Sheet sheet, float[] mA, ref (float x, float y, float z)? prevAbs)
{
    float[] target = new float[4];
    Mat4f.MulWithVec4(mA, sheet.Anchor, target);

    double dx = target[0] - sheet.Pivot[0];
    double dy = target[1] - sheet.Pivot[1];
    double dz = target[2] - sheet.Pivot[2];
    double lenSq = dx * dx + dy * dy + dz * dz;
    double len = Math.Sqrt(lenSq);
    if (len < 1e-9) return null;

    // u is the portion that scales uniformly with s; f (FixedTail, zero unless the true terminus is the
    // pulley itself - see Capture's comments) is a constant addition that does NOT scale, since it
    // represents geometry the pulley's own reciprocal-stretch keyframe deliberately holds at constant
    // size. The scaled vector is (u.x+f.x, s*u.y+f.y, u.z+f.z); solving |scaled|^2 = lenSq for s only
    // needs completing the square along Y, since f's lateral (x,z) components combine directly with u's
    // and only the Y term actually depends on s.
    float[] u = sheet.Offset;
    float[] f = sheet.FixedTail;
    double lateral = Math.Pow((double)u[0] + f[0], 2) + Math.Pow((double)u[2] + f[2], 2);
    if (lenSq < lateral) return null;
    double yTarget = Math.Sqrt(lenSq - lateral); // |s*u.y + f.y|, always non-negative by construction
    double s1 = (yTarget - f[1]) / u[1];
    double s2 = (-yTarget - f[1]) / u[1];
    // Physically only a POSITIVE stretch is meaningful; prefer whichever root is positive, and if both
    // are, the one closer to 1 (the natural, near-unstretched configuration) rather than an unphysical
    // near-zero-or-negative-approaching root the algebra also admits.
    double s = (s1 > 0 && (s2 <= 0 || Math.Abs(s1 - 1) <= Math.Abs(s2 - 1))) ? s1 : s2;
    if (s <= 0) return null;

    float[] scaled = { u[0] + f[0], (float)(s * u[1]) + f[1], u[2] + f[2], 0 };
    float[] dirFrom = { Dot(sheet.RestRotation, 0, scaled), Dot(sheet.RestRotation, 1, scaled), Dot(sheet.RestRotation, 2, scaled) };
    if (!Normalize(dirFrom)) return null;

    float[] dirTo = { (float)(dx / len), (float)(dy / len), (float)(dz / len) };

    float[] quat = new float[4];
    Quaternionf.RotationTo(quat, dirFrom, dirTo);
    float[] mB = Mat4f.Create();
    Mat4f.FromRotationTranslation(mB, quat, new float[] { 0, 0, 0 });
    float[] mC = Mat4f.Create();
    Mat4f.Mul(mC, mB, sheet.RestRotation);

    ToEulerXYZ(mC, prevAbs, out float ex, out float ey, out float ez);
    prevAbs = (ex, ey, ez);

    return (ex - (float)sheet.RootElem.RotationX, ey - (float)sheet.RootElem.RotationY, ez - (float)sheet.RootElem.RotationZ, (float)s);
}

#endregion

#region Frame/angle mapping

// Frame for a given trim angle, rounded to the nearest integer frame, matching RotateSail's own three
// authored keyframes exactly: frame 0 = +90 (full port), frame 89 = 0 (dead ahead), frame 179 = -90
// (full starboard). The two halves have slightly different per-frame slopes (89/90 vs 90/90) since the
// range isn't evenly split around frame 89.
int FrameForAngle(float angleDeg) => (int)Math.Round(angleDeg >= 0f
    ? (90f - angleDeg) * (89f / 90f)
    : 89f - angleDeg, MidpointRounding.AwayFromZero);

#endregion

#region Bake and emit keyframes

const float BakeStepDeg = 15f;
// The 15deg grid is deliberately NOT tightened uniformly: each sail-state change forces the entity's
// AnimationCache to rebuild (so the billow animation never builds against a stale cache - see
// EntityBehaviorSailTrim/CLAUDE.md's "Sail billow intensity"), which re-processes the full keyframe set,
// so keyframe count is a recurring cost during normal play, not a one-time load cost.
//
// Local density beyond that grid is found ADAPTIVELY per branch/side (see RefineSide below) rather than
// a hand-picked fixed list: instrumenting the solve confirmed the visible bounce is real, fast-
// accelerating motion, not an Euler-decomposition artifact - the wrong branch (see ToEulerXYZ above) is
// always ~500deg away and never gets picked - but WHERE that acceleration is worst is asymmetric between
// port and starboard (each sheet goes taut on a different side of the swing) and shifted substantially
// once the SailRoot's own hand-authored offset lerp was folded into the chain (see the Branch array's own
// comment). The refinement criterion is the actual rendered POSITION gap the engine's own linear
// interpolation between two baked keyframes would produce, not an Euler-degree proxy: a first version
// bisected on raw rotation-angle deltas, but that measure can diverge near-singular configurations (where
// the aim-and-stretch solve's required rotation swings sharply for a small physical target change) even
// though the true positional error stays modest, and conversely can pass a degree-based threshold while
// still leaving a visible gap - it doesn't directly measure "does the rope look attached to the knot",
// which is what actually matters. Bisecting on measured position error, per branch and per side
// independently, tracks whichever asymmetry the current geometry actually has instead of assuming it
// mirrors around the extremes.
const float PositionThresholdBlocks = 0.04f;
const int MaxRefineIterationsPerSide = 30;

var bakeAngles = new List<float>();
for (float angle = 90f; angle >= -90f; angle -= BakeStepDeg) bakeAngles.Add(angle);
bakeAngles = bakeAngles.Distinct().OrderByDescending(a => a).ToList();

// Processed from the known-good rest pose (angle 0) outward in both directions, NOT sequentially from
// +90 down to -90: seeding the continuity walk at one of the extremes would leave that very first frame's
// branch choice arbitrary, in exactly the gimbal-adjacent region most prone to the ambiguity this is
// fixing. Splitting into a positive-angle walk and a negative-angle walk, each re-anchored at angle 0,
// keeps both directions independently continuous from a stable reference.
var positiveAngles = bakeAngles.Where(a => a > 0f).OrderBy(a => a).ToList();
var negativeAngles = bakeAngles.Where(a => a < 0f).OrderByDescending(a => a).ToList();

// How far (pixel units, matching From/RotationOrigin) the pulley blocks slide along their own rope per
// unit of scale deviation from 1 - toward the root (up the chain) as the rope compresses, to clear the
// deck post; AWAY from the root (down the chain) as the rope extends, to clear a DIFFERENT post further
// toward the fore of the ship the pulley would otherwise clip through when stretched. Tuned in-game; 6
// (the compression side's own original estimate) and 10 both still clipped the top half of the pulley
// block on full extension, raised to 14.
const float PulleyLiftPerScaleUnit = 14f;

var sb = new StringBuilder();
sb.Append("{\n");

for (int bi = 0; bi < branches.Length; bi++)
{
    Branch b = branches[bi];
    ShapeElement rotator = Find(RotatorName);
    ShapeElement clewRootElem = Find(b.ClewRoot);

    var path = new List<ShapeElement>();
    for (ShapeElement? e = clewRootElem; e != null; e = e.ParentElement)
    {
        path.Add(e);
        if (e == rotator) break;
    }
    if (path[^1] != rotator) throw new Exception($"{b.ClewRoot} is not under {RotatorName}");
    path.Reverse();

    // Rest-pose ElementPose stand-ins (all-default fields) to feed GetLocalTransformMatrix.
    ElementPose[] chain = path.Select(e => new ElementPose { ForElement = e }).ToArray();

    Sheet stbd = Capture(chain, b.StbdRope, b.StbdPulley, AnchorR);
    Sheet port = Capture(chain, b.PortRope, b.PortPulley, AnchorL);
    Console.Error.WriteLine($"branch {b.SailRoot}: chain length {chain.Length}");
    Console.Error.WriteLine($"  stbd pivot=({stbd.Pivot[0]:R},{stbd.Pivot[1]:R},{stbd.Pivot[2]:R}) offset=({stbd.Offset[0]:R},{stbd.Offset[1]:R},{stbd.Offset[2]:R}) anchor=({stbd.Anchor[0]:R},{stbd.Anchor[1]:R},{stbd.Anchor[2]:R})");
    Console.Error.WriteLine($"  port pivot=({port.Pivot[0]:R},{port.Pivot[1]:R},{port.Pivot[2]:R}) offset=({port.Offset[0]:R},{port.Offset[1]:R},{port.Offset[2]:R}) anchor=({port.Anchor[0]:R},{port.Anchor[1]:R},{port.Anchor[2]:R})");

    sb.Append($"  \"{b.SailRoot}\": {{\n");
    var frameEntries = new Dictionary<int, string>();
    (float x, float y, float z)? prevStbd = null;
    (float x, float y, float z)? prevPort = null;

    // Reconstructs the rope root's actual 3D position (in rootElem's-parent-space, matching sheet.Pivot's
    // own space) from a solved (rootRx,rootRy,rootRz,scale) tuple - the same composition the runtime itself
    // performs (RotateXYZ(base rotation + baked keyframe) then apply the scaled rest offset). Used both to
    // sanity-check Solve()'s exactness and, in RefineSide, to measure what the ENGINE's own linear
    // interpolation between two baked keyframes would actually render at a point between them.
    float[] ReconstructPos(Sheet sheet, (float rootRx, float rootRy, float rootRz, float scale) r)
    {
        float[] rot = Mat4f.Create();
        Mat4f.Identity(rot);
        Mat4f.RotateByXYZ(rot,
            (float)(sheet.RootElem.RotationX + r.rootRx) * GameMath.DEG2RAD,
            (float)(sheet.RootElem.RotationY + r.rootRy) * GameMath.DEG2RAD,
            (float)(sheet.RootElem.RotationZ + r.rootRz) * GameMath.DEG2RAD);
        float[] scaled = { sheet.Offset[0] + sheet.FixedTail[0], r.scale * sheet.Offset[1] + sheet.FixedTail[1], sheet.Offset[2] + sheet.FixedTail[2] };
        float[] recon = { Dot(rot, 0, scaled), Dot(rot, 1, scaled), Dot(rot, 2, scaled) };
        return new[] { sheet.Pivot[0] + recon[0], sheet.Pivot[1] + recon[1], sheet.Pivot[2] + recon[2] };
    }

    // Computes mA (ORIGIN-space -> this branch's rope-root-parent-space, see Capture's comments) for a
    // given trim angle and solves both sheets against it. Factored out of BakeFrame so RefineSide's
    // position-divergence probes (see below) can solve an arbitrary angle against caller-supplied
    // continuity seeds without disturbing the outer walk's own prevStbd/prevPort state.
    ((float rootRx, float rootRy, float rootRz, float scale) stbd, (float rootRx, float rootRy, float rootRz, float scale) port)? SolveAngle(
        float angleDeg, ref (float x, float y, float z)? seedStbdRef, ref (float x, float y, float z)? seedPortRef)
    {
        int frame = FrameForAngle(angleDeg);

        // Only the sail branch root itself carries the swing (MastORIGIN/Mast1-6 stay put, matching how
        // RotateSail's own authored keyframes pose them) -- the chain still needs to walk the full
        // MastORIGIN..ClewRoot path for this to compose correctly, since Mast1-6's own (static) rest
        // translations still separate the anchor from the rope root.
        float[] mC = Mat4f.Create();
        Mat4f.Identity(mC);
        float[] mB = Mat4f.Create();
        float[] mD = Mat4f.Create();
        // SailRoot's real keyframes lerp offsetX/Y/Z alongside rotationY between frames 0/89/179 (see the
        // Branch array's own comment above) - reproduce that same frame-fraction lerp here so the solved
        // rope root targets the anchor from wherever the rendered mesh actually is at this frame, not from
        // a bare rotation with the compensating translation silently dropped.
        float offsetT = frame <= 89 ? frame / 89f : (frame - 89) / 90f;
        float[] off0 = frame <= 89 ? b.Frame0Offset : new float[] { 0f, 0f, 0f };
        float[] off1 = frame <= 89 ? new float[] { 0f, 0f, 0f } : b.Frame179Offset;
        float sailOffX = GameMath.Lerp(off0[0], off1[0], offsetT) / 16f;
        float sailOffY = GameMath.Lerp(off0[1], off1[1], offsetT) / 16f;
        float sailOffZ = GameMath.Lerp(off0[2], off1[2], offsetT) / 16f;

        for (int i = 0; i < chain.Length; i++)
        {
            ElementPose tf = chain[i];
            if (chain[i].ForElement.Name == b.SailRoot) tf = new ElementPose
            {
                ForElement = chain[i].ForElement, degOffY = angleDeg,
                translateX = sailOffX, translateY = sailOffY, translateZ = sailOffZ,
            };
            Mat4f.Identity(mB);
            chain[i].ForElement.GetLocalTransformMatrix(0, mB, tf);
            Mat4f.Mul(mD, mC, mB);
            Mat4f.Copy(mC, mD);
        }
        float[] mA = Mat4f.Create();
        Mat4f.Invert(mA, mC);

        var rStbd = Solve(stbd, mA, ref seedStbdRef);
        var rPort = Solve(port, mA, ref seedPortRef);
        if (rStbd == null || rPort == null) return null;

        if (Environment.GetEnvironmentVariable("KFGEN_RESIDUAL") != null)
        {
            void CheckResidual(string label, Sheet sheet, (float rootRx, float rootRy, float rootRz, float scale) r)
            {
                float[] target = new float[4];
                Mat4f.MulWithVec4(mA, sheet.Anchor, target);
                float[] recon = ReconstructPos(sheet, r);
                float residual = (float)Math.Sqrt(Math.Pow(recon[0] - target[0], 2) + Math.Pow(recon[1] - target[1], 2) + Math.Pow(recon[2] - target[2], 2));
                if (residual > 1e-3f)
                    Console.Error.WriteLine($"  RESIDUAL branch={b.SailRoot} frame={frame} sheet={label} residual={residual:F5} (blocks)");

                // TRUE-CHAIN check: ReconstructPos assumes the ROPE's real geometric terminus sits exactly
                // at pivot + Rot*(s*u) - i.e. that the whole natural chain from root to its true dead-end
                // leaf scales uniformly by s. Verify that against the ACTUAL shape topology: walk from
                // RootElem down, at every fork taking whichever child ISN'T PulleyElem's own subtree (the
                // pulley branches off as a SIBLING partway down the chain in at least one branch - it is
                // NOT necessarily an ancestor of the true terminal leaf), composing each segment's REAL
                // GetLocalTransformMatrix (root gets the solved rotation+scale; every other segment on this
                // path is a plain, unkeyframed [0,16,0] step, so it just inherits root's cumulative scale)
                // down to the leaf, and compare that leaf's true position against the anchor target.
                float pulleyScale = Math.Abs(r.scale) > 1e-6f ? 1f / r.scale : 1f;
                float pulleyLift = PulleyLiftPerScaleUnit * (r.scale - 1f);

                List<ShapeElement> segs = WalkToTrueLeaf(sheet.RootElem, sheet.PulleyElem);
                ShapeElement leaf = segs[^1];
                float[] m = Mat4f.Create();
                Mat4f.Identity(m);
                float[] step = Mat4f.Create();
                float[] tmp2 = Mat4f.Create();
                foreach (ShapeElement seg in segs)
                {
                    ElementPose tf = seg == sheet.RootElem
                        ? new ElementPose { ForElement = seg, degX = r.rootRx, degY = r.rootRy, degZ = r.rootRz, scaleX = 1, scaleY = r.scale, scaleZ = 1 }
                        : seg == sheet.PulleyElem
                            ? new ElementPose { ForElement = seg, scaleX = 1, scaleY = pulleyScale, scaleZ = 1, translateY = pulleyLift / 16f }
                            : new ElementPose { ForElement = seg };
                    Mat4f.Identity(step);
                    seg.GetLocalTransformMatrix(0, step, tf);
                    Mat4f.Mul(tmp2, m, step);
                    Mat4f.Copy(m, tmp2);
                }
                float[] leafFromB = Sixteenths(leaf.From);
                float[] leafToB = Sixteenths(leaf.To);
                float[] farCorner = { leafToB[0] - leafFromB[0], leafToB[1] - leafFromB[1], leafToB[2] - leafFromB[2], 1 };
                float[] truePos = new float[4];
                Mat4f.MulWithVec4(m, farCorner, truePos);
                float trueGap = (float)Math.Sqrt(Math.Pow(truePos[0] - target[0], 2) + Math.Pow(truePos[1] - target[1], 2) + Math.Pow(truePos[2] - target[2], 2));
                if (trueGap > 1e-3f)
                    Console.Error.WriteLine($"  TRUECHAIN branch={b.SailRoot} frame={frame} sheet={label} leaf={leaf.Name} gap={trueGap:F5} (blocks) scale={r.scale:F3}");
            }
            CheckResidual("stbd", stbd, rStbd.Value);
            CheckResidual("port", port, rPort.Value);
        }

        return (rStbd.Value, rPort.Value);
    }

    string? BakeFrame(float angleDeg)
    {
        int frame = FrameForAngle(angleDeg);
        var solved = SolveAngle(angleDeg, ref prevStbd, ref prevPort);
        if (solved == null)
        {
            Console.Error.WriteLine($"  WARNING: branch {b.SailRoot} frame {frame} (angle {angleDeg}) degenerate solve, skipped");
            return null;
        }
        (float rootRx, float rootRy, float rootRz, float scale) rStbd = solved.Value.stbd;
        (float rootRx, float rootRy, float rootRz, float scale) rPort = solved.Value.port;

        // rotShortestDistance*: forces the runtime blend between adjacent baked frames to take the short
        // arc rather than a plain lerp of the raw Euler values. The engine's own documented fix for
        // angle-wrap interpolation artifacts; costs nothing to have on.
        //
        // Pulley reciprocal scale: the root's stretchY propagates multiplicatively down the whole chain
        // (plain [0,16,0] bones have no scale of their own to absorb it), so the pulley block would
        // stretch right along with the rope unless given 1/scale of its own to cancel that back out.
        //
        // Pulley OffsetY: a fixed [0,16,0] step down the chain has no way to absorb the rope's length
        // change beyond its own reciprocal stretch - as the rope compresses (scale < 1) the pulley's fixed
        // chain position rides increasingly close to the deck knot/post; as it EXTENDS (scale > 1) the
        // pulley's fixed position instead clips through a DIFFERENT post further toward the fore of the
        // ship. Both are the same problem in opposite directions, so both get one symmetric, unclamped
        // ramp: OffsetY proportional to (scale - 1) - negative (up the chain, toward the root - see
        // CLAUDE.md's Clew ropes section for the +Y-down-the-rope convention) when compressed, positive
        // (down the chain, away from the root) when extended. PulleyLiftPerScaleUnit is a tuned constant,
        // not a closed-form geometric solve like the rest of this rig.
        //
        // offsetX/offsetZ must be emitted explicitly (as 0) alongside offsetY: AnimationKeyFrameElement's
        // PositionSet is true if ANY offset axis is present, but lerpKeyFrameElement then unconditionally
        // reads .Value on all three - an omitted axis throws "Nullable object must have a value" every
        // frame this element is keyframed, not just a cosmetic default-to-zero.
        float stbdPulleyScale = Math.Abs(rStbd.scale) > 1e-6f ? 1f / rStbd.scale : 1f;
        float portPulleyScale = Math.Abs(rPort.scale) > 1e-6f ? 1f / rPort.scale : 1f;
        float stbdLift = PulleyLiftPerScaleUnit * (rStbd.scale - 1f);
        float portLift = PulleyLiftPerScaleUnit * (rPort.scale - 1f);

        var lines = new List<string>
        {
            $"      \"{b.StbdRope}\": {{ \"rotationX\": {Fmt(rStbd.rootRx)}, \"rotationY\": {Fmt(rStbd.rootRy)}, \"rotationZ\": {Fmt(rStbd.rootRz)}, \"stretchX\": 1.0, \"stretchY\": {Fmt(rStbd.scale)}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }}",
            $"      \"{b.PortRope}\": {{ \"rotationX\": {Fmt(rPort.rootRx)}, \"rotationY\": {Fmt(rPort.rootRy)}, \"rotationZ\": {Fmt(rPort.rootRz)}, \"stretchX\": 1.0, \"stretchY\": {Fmt(rPort.scale)}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }}",
            $"      \"{b.StbdPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {Fmt(stbdPulleyScale)}, \"stretchZ\": 1.0, \"offsetX\": 0.0, \"offsetY\": {Fmt(stbdLift)}, \"offsetZ\": 0.0 }}",
            $"      \"{b.PortPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {Fmt(portPulleyScale)}, \"stretchZ\": 1.0, \"offsetX\": 0.0, \"offsetY\": {Fmt(portLift)}, \"offsetZ\": 0.0 }}",
        };

        return $"    \"{frame}\": {{\n" + string.Join(",\n", lines) + "\n    }";
    }

    (float rootRx, float rootRy, float rootRz, float scale) LerpRaw(
        (float rootRx, float rootRy, float rootRz, float scale) a, (float rootRx, float rootRy, float rootRz, float scale) b, float t) =>
        (GameMath.Lerp(a.rootRx, b.rootRx, t), GameMath.Lerp(a.rootRy, b.rootRy, t), GameMath.Lerp(a.rootRz, b.rootRz, t), GameMath.Lerp(a.scale, b.scale, t));

    float Dist(float[] a, float[] b) =>
        (float)Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));

    // Bakes `angles` (walked in order away from zero, continuity-seeded from sideSeed*) and bisects any
    // adjacent pair where the ENGINE's own linear interpolation (matching lerpKeyFrameElement: raw lerp of
    // rootRx/Y/Z and scale, at the true fractional frame position between the two neighbours) would render
    // a position more than PositionThresholdBlocks away from the true solved position at that same point -
    // a direct measure of the actual visible gap, not an angular proxy (Euler-degree deltas can diverge
    // near-singular configurations even when the real positional error stays small, and vice versa).
    // Repeats until stable or MaxRefineIterationsPerSide is hit. A candidate midpoint that would round to
    // the SAME baked frame as either neighbour is dropped (integer frame resolution exhausted right there,
    // not fixable by further subdivision) rather than looping forever trying to insert a duplicate frame.
    List<float> RefineSide(List<float> seedAngles, (float x, float y, float z)? sideSeedStbd, (float x, float y, float z)? sideSeedPort)
    {
        var angles = seedAngles.Distinct().OrderBy(a => Math.Abs(a)).ToList();
        for (int iter = 0; iter < MaxRefineIterationsPerSide; iter++)
        {
            var walkStbd = sideSeedStbd;
            var walkPort = sideSeedPort;
            var raws = new List<((float rootRx, float rootRy, float rootRz, float scale) stbd, (float rootRx, float rootRy, float rootRz, float scale) port)?>();
            foreach (float angle in angles) raws.Add(SolveAngle(angle, ref walkStbd, ref walkPort));

            var toInsert = new List<float>();
            for (int i = 1; i < angles.Count; i++)
            {
                var rawLo = raws[i - 1];
                var rawHi = raws[i];
                if (rawLo == null || rawHi == null) continue;

                int fLo = FrameForAngle(angles[i - 1]), fHi = FrameForAngle(angles[i]);
                float mid = (angles[i - 1] + angles[i]) / 2f;
                int fMid = FrameForAngle(mid);
                if (fMid == fLo || fMid == fHi) continue;

                float t = (fMid - fLo) / (float)(fHi - fLo);
                float[] stbdNaivePos = ReconstructPos(stbd, LerpRaw(rawLo.Value.stbd, rawHi.Value.stbd, t));
                float[] portNaivePos = ReconstructPos(port, LerpRaw(rawLo.Value.port, rawHi.Value.port, t));

                (float x, float y, float z)? probeStbd = null, probePort = null;
                var exact = SolveAngle(mid, ref probeStbd, ref probePort);
                if (exact == null) continue;
                float[] stbdExactPos = ReconstructPos(stbd, exact.Value.stbd);
                float[] portExactPos = ReconstructPos(port, exact.Value.port);

                if (Math.Max(Dist(stbdNaivePos, stbdExactPos), Dist(portNaivePos, portExactPos)) > PositionThresholdBlocks)
                    toInsert.Add(mid);
            }
            if (toInsert.Count == 0) break;
            angles.AddRange(toInsert);
            angles = angles.Distinct().OrderBy(a => Math.Abs(a)).ToList();
            if (iter == MaxRefineIterationsPerSide - 1)
                Console.Error.WriteLine($"  WARNING: branch {b.SailRoot} hit MaxRefineIterationsPerSide with {toInsert.Count} unresolved position violations remaining");
        }
        return angles;
    }

    // angle 0 (dead-ahead, frame 89) first - a stable, non-gimbal-adjacent anchor - then walk outward in
    // each direction independently re-seeded from that same anchor (see the comment on positiveAngles/
    // negativeAngles above).
    string? zeroEntry = BakeFrame(0f);
    if (zeroEntry != null) frameEntries[FrameForAngle(0f)] = zeroEntry;
    (float x, float y, float z)? seedStbd = prevStbd;
    (float x, float y, float z)? seedPort = prevPort;

    var finalPositiveAngles = RefineSide(positiveAngles, seedStbd, seedPort);
    var finalNegativeAngles = RefineSide(negativeAngles, seedStbd, seedPort);
    Console.Error.WriteLine($"branch {b.SailRoot}: {finalPositiveAngles.Count} positive-side + {finalNegativeAngles.Count} negative-side frames after adaptive refinement (threshold {PositionThresholdBlocks} blocks)");

    prevStbd = seedStbd;
    prevPort = seedPort;
    foreach (float angle in finalPositiveAngles)
    {
        string? entry = BakeFrame(angle);
        if (entry != null) frameEntries[FrameForAngle(angle)] = entry;
    }

    prevStbd = seedStbd;
    prevPort = seedPort;
    foreach (float angle in finalNegativeAngles)
    {
        string? entry = BakeFrame(angle);
        if (entry != null) frameEntries[FrameForAngle(angle)] = entry;
    }

    sb.Append(string.Join(",\n", frameEntries.OrderBy(kv => kv.Key).Select(kv => kv.Value)));
    sb.Append('\n');
    sb.Append(bi == branches.Length - 1 ? "  }\n" : "  },\n");
}
sb.Append("}\n");

File.WriteAllText(outPath, sb.ToString());
Console.Error.WriteLine($"wrote {outPath}");

#endregion

#region Types

readonly record struct Branch(string SailRoot, string ClewRoot, string StbdRope, string PortRope, string StbdPulley, string PortPulley,
    float[] Frame0Offset, float[] Frame179Offset);

sealed class Sheet
{
    public required ShapeElement RootElem;
    public required float[] Pivot;
    public required float[] Offset;
    public required float[] FixedTail;
    public required float[] RestRotation;
    public required float[] Anchor;
    public required ShapeElement PulleyElem;
}

#endregion
