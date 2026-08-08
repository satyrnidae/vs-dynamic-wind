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
Branch[] branches =
{
    new("SailFurled",   "ClewORIGINFurled", "ClewRopeR5", "ClewRopeL5", "Block11", "BlockL4"),
    new("SailHalf",     "ClewRopeOrigin",   "RClew1",     "LClew1",     "Block4",  "BlockL2"),
    new("SailUnfurled", "ClewORIGIN",       "ClewRopeR1", "ClewRopeL1", "Block1",  "BlockL"),
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
// `stopBeforeAncestor`. Applied to leaf's own local origin, this gives leaf's rest position expressed in
// stopBeforeAncestor's own local space (the same space stopBeforeAncestor's own From/RotationOrigin
// values are already written in).
float[] LiftToAncestorSpace(ShapeElement leaf, ShapeElement stopBeforeAncestor)
{
    float[] p = { 0, 0, 0, 1 };
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
    // position directly in ORIGIN's own local space, then bring that anchor back down into rootElem's
    // PARENT's space via the rest-pose chain matrix (the exact inverse of what the per-frame loop does
    // to go the other way).
    ShapeElement anchorElem = Find(anchorName);
    ShapeElement origin = Find(OriginName);
    float[] anchor = LiftToAncestorSpace(anchorElem, origin);

    float[] mCRest = RestChainMatrix(chain);
    float[] mARest = Mat4f.Create();
    Mat4f.Invert(mARest, mCRest);
    float[] anchorInRootParent = new float[4];
    Mat4f.MulWithVec4(mARest, anchor, anchorInRootParent);

    // offset must be expressed in rootElem's OWN pre-rotation local frame - the same frame Solve() later
    // re-rotates by restRotation to place things back in the parent frame (anchor = pivot + R*offset).
    // anchorInRootParent is already in the PARENT frame, so un-rotate by R^-1 first.
    float[] diff =
    {
        anchorInRootParent[0] - pivot[0],
        anchorInRootParent[1] - pivot[1],
        anchorInRootParent[2] - pivot[2],
    };
    float[] invRestRotation = Mat4f.Create();
    Mat4f.Invert(invRestRotation, restRotation);
    float[] offset =
    {
        Dot(invRestRotation, 0, diff),
        Dot(invRestRotation, 1, diff),
        Dot(invRestRotation, 2, diff),
    };

    if (Math.Abs(offset[1]) < 1e-6f) throw new Exception($"{ropeName} offset has no axial run");

    // Anchor must stay the TRUE fixed point in ORIGIN's own space, not reconstructed via pivot+R*offset:
    // Solve()'s `mA * sheet.Anchor` expects an ORIGIN-space input (mA maps ORIGIN-space -> the current
    // frame's swung rope-root-local-space). An already-local-space reconstruction would double-transform it,
    // collapsing the solved target toward the rope root's own frame instead of the real deck anchor.
    return new Sheet
    {
        RootElem = rootElem, Pivot = pivot, Offset = offset, RestRotation = restRotation,
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

    float[] u = sheet.Offset;
    double lateral = (double)u[0] * u[0] + (double)u[2] * u[2];
    if (lenSq < lateral) return null;
    double s = Math.Sqrt((lenSq - lateral) / ((double)u[1] * u[1]));

    float[] scaled = { u[0], (float)(s * u[1]), u[2], 0 };
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
// It IS locally densified in the outermost band on each side, though: instrumenting the solve confirmed
// the visible bounce there is real, fast-accelerating motion, not an Euler-decomposition artifact - the
// wrong branch (see ToEulerXYZ above) is always ~500deg away and never gets picked, but even the correct
// branch's Euler angles swing up to ~85deg of total change within a single 15deg trim step approaching
// full feather (dirFrom/dirTo's dot product goes from 0.6 to -0.4 over the last two 15deg steps - the aim
// solve genuinely races as the rope runs out of geometric slack on that side). The curvature ramps up
// starting around 60deg, not just in the last 15deg before the extreme, so the band from 60 to 90deg gets
// bisected on each side (67.5/80/85) rather than just its outermost slice - measured afterward to bring
// the worst single-step Euler change down from ~67deg to a smooth few degrees per step.
var extraAngles = new[] { 85f, 80f, 67.5f, -67.5f, -80f, -85f };
var bakeAngles = new List<float>();
for (float angle = 90f; angle >= -90f; angle -= BakeStepDeg) bakeAngles.Add(angle);
bakeAngles.AddRange(extraAngles);
bakeAngles = bakeAngles.Distinct().OrderByDescending(a => a).ToList();
Console.Error.WriteLine($"baking {bakeAngles.Count} frames ({BakeStepDeg}deg grid + {extraAngles.Length} extra near +/-90deg): [{string.Join(", ", bakeAngles.Select(FrameForAngle))}]");

// Processed from the known-good rest pose (angle 0) outward in both directions, NOT sequentially from
// +90 down to -90: seeding the continuity walk at one of the extremes would leave that very first frame's
// branch choice arbitrary, in exactly the gimbal-adjacent region most prone to the ambiguity this is
// fixing. Splitting into a positive-angle walk and a negative-angle walk, each re-anchored at angle 0,
// keeps both directions independently continuous from a stable reference.
var positiveAngles = bakeAngles.Where(a => a > 0f).OrderBy(a => a).ToList();
var negativeAngles = bakeAngles.Where(a => a < 0f).OrderByDescending(a => a).ToList();

// How far (pixel units, matching From/RotationOrigin) the pulley blocks lift up their own rope toward the
// root at maximum rope compression, to clear the deck post. Tuned in-game; the user's own estimate was a
// magnitude of 4-8, starting at the midpoint.
const float MaxPulleyLift = 6f;

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

    string? BakeFrame(float angleDeg)
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
        for (int i = 0; i < chain.Length; i++)
        {
            ElementPose tf = chain[i];
            if (chain[i].ForElement.Name == b.SailRoot) tf = new ElementPose { ForElement = chain[i].ForElement, degOffY = angleDeg };
            Mat4f.Identity(mB);
            chain[i].ForElement.GetLocalTransformMatrix(0, mB, tf);
            Mat4f.Mul(mD, mC, mB);
            Mat4f.Copy(mC, mD);
        }
        float[] mA = Mat4f.Create();
        Mat4f.Invert(mA, mC);

        var rStbd = Solve(stbd, mA, ref prevStbd);
        var rPort = Solve(port, mA, ref prevPort);
        if (rStbd == null || rPort == null)
        {
            Console.Error.WriteLine($"  WARNING: branch {b.SailRoot} frame {frame} (angle {angleDeg}) degenerate solve, skipped");
            return null;
        }

        // rotShortestDistance*: forces the runtime blend between adjacent baked frames to take the short
        // arc rather than a plain lerp of the raw Euler values. The engine's own documented fix for
        // angle-wrap interpolation artifacts; costs nothing to have on.
        //
        // Pulley reciprocal scale: the root's stretchY propagates multiplicatively down the whole chain
        // (plain [0,16,0] bones have no scale of their own to absorb it), so the pulley block would
        // stretch right along with the rope unless given 1/scale of its own to cancel that back out.
        //
        // Pulley OffsetY: a fixed [0,16,0] step down the chain has no way to absorb the rope shortening
        // beyond its own stretch - as the rope compresses (scale < 1) the pulley's fixed chain position
        // rides increasingly close to the deck knot/post. Ramp a negative OffsetY (up the chain, toward
        // the root - see CLAUDE.md's Clew ropes section for the +Y-down-the-rope convention) in
        // proportion to how compressed the rope is at this frame, so the pulley lifts clear instead of
        // visually clipping through the post near the point where the rope is shortest. MaxPulleyLift is
        // a tuned constant, not a closed-form geometric solve like the rest of this rig.
        float stbdPulleyScale = Math.Abs(rStbd.Value.scale) > 1e-6f ? 1f / rStbd.Value.scale : 1f;
        float portPulleyScale = Math.Abs(rPort.Value.scale) > 1e-6f ? 1f / rPort.Value.scale : 1f;
        float stbdLift = -MaxPulleyLift * Math.Clamp(1f - rStbd.Value.scale, 0f, 1f);
        float portLift = -MaxPulleyLift * Math.Clamp(1f - rPort.Value.scale, 0f, 1f);

        var lines = new List<string>
        {
            $"      \"{b.StbdRope}\": {{ \"rotationX\": {Fmt(rStbd.Value.rootRx)}, \"rotationY\": {Fmt(rStbd.Value.rootRy)}, \"rotationZ\": {Fmt(rStbd.Value.rootRz)}, \"stretchX\": 1.0, \"stretchY\": {Fmt(rStbd.Value.scale)}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }}",
            $"      \"{b.PortRope}\": {{ \"rotationX\": {Fmt(rPort.Value.rootRx)}, \"rotationY\": {Fmt(rPort.Value.rootRy)}, \"rotationZ\": {Fmt(rPort.Value.rootRz)}, \"stretchX\": 1.0, \"stretchY\": {Fmt(rPort.Value.scale)}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }}",
            $"      \"{b.StbdPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {Fmt(stbdPulleyScale)}, \"stretchZ\": 1.0, \"offsetY\": {Fmt(stbdLift)} }}",
            $"      \"{b.PortPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {Fmt(portPulleyScale)}, \"stretchZ\": 1.0, \"offsetY\": {Fmt(portLift)} }}",
        };

        return $"    \"{frame}\": {{\n" + string.Join(",\n", lines) + "\n    }";
    }

    // angle 0 (dead-ahead, frame 89) first - a stable, non-gimbal-adjacent anchor - then walk outward in
    // each direction independently re-seeded from that same anchor (see the comment on positiveAngles/
    // negativeAngles above).
    string? zeroEntry = BakeFrame(0f);
    if (zeroEntry != null) frameEntries[FrameForAngle(0f)] = zeroEntry;
    (float x, float y, float z)? seedStbd = prevStbd;
    (float x, float y, float z)? seedPort = prevPort;

    foreach (float angle in positiveAngles)
    {
        string? entry = BakeFrame(angle);
        if (entry != null) frameEntries[FrameForAngle(angle)] = entry;
    }

    prevStbd = seedStbd;
    prevPort = seedPort;
    foreach (float angle in negativeAngles)
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

readonly record struct Branch(string SailRoot, string ClewRoot, string StbdRope, string PortRope, string StbdPulley, string PortPulley);

sealed class Sheet
{
    public required ShapeElement RootElem;
    public required float[] Pivot;
    public required float[] Offset;
    public required float[] RestRotation;
    public required float[] Anchor;
    public required ShapeElement PulleyElem;
}

#endregion
