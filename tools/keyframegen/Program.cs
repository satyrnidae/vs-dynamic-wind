using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Offline generator: bakes the clew-rope rig's aim-and-stretch solve (the same geometry ClewRopeRig used
// to compute at runtime before the mast/sails moved to a real baked Animation — see
// EntityBehaviorSailTrim's doc comment for why) into per-frame keyframe data for the "sailtrim" shape
// animation, using the real VintagestoryAPI.dll primitives (Mat4f/Quaternionf/ShapeElement) so this can't
// silently disagree with how the engine itself poses the model.
//
// Usage: dotnet run -- <path to sailed.json, fully patched> <output keyframes.json>
// Regenerate whenever the boat shape or its clew-rope patch changes; splice the branch objects in the
// output into the "sailtrim" animation's keyframes in patches/boat-sailed-shape.json (frames 1..179 need
// only the two sheet-root entries per branch; frames 0 and 180 also carry MastORIGIN/Mast7's rotationY).
//
// IMPORTANT: GetLocalTransformMatrix does NOT reset its `output` matrix to identity — it post-multiplies
// Translate/Rotate/Scale directly onto whatever is already there. Every call below resets its scratch
// matrix with Mat4f.Identity() first; skipping that (as a first draft of this tool did) silently composes
// stale data from the previous loop iteration and produces wrong, non-degenerate-looking garbage.

string shapePath = args.Length > 0 ? args[0] : throw new ArgumentException("usage: keyframegen <shape.json> <out.json>");
string outPath = args.Length > 1 ? args[1] : throw new ArgumentException("usage: keyframegen <shape.json> <out.json>");

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

// Mirrors ClewRopeRig.Branches exactly — keep the two in sync if the shape's element names ever change.
Branch[] branches =
{
    new("SailFurled",   "ClewFurledORIGIN", "ClewRopeR1Furled", "ClewRopeL1Furled",
        "BlockR3", "BlockL3", "ClewKnotRFurled", "ClewKnotLFurled"),
    new("SailHalf",     "ClewRopeOrigin",   "RClew1",           "LClew1",
        "Block4",  "BlockL2", "RClewKnot",       "ClewRopeLHalf"),
    new("SailUnfurled", "ClewORIGIN",       "ClewRopeR1",       "ClewRopeL1",
        "Block1",  "BlockL",  "ClewKnotR",       "ClewRopeL"),
};

float[] Sixteenths(double[]? v) => v == null ? new float[3] : new[] { (float)v[0] / 16f, (float)v[1] / 16f, (float)v[2] / 16f };

float Dot(float[] m, int row, float[] v) => m[row] * v[0] + m[4 + row] * v[1] + m[8 + row] * v[2];

bool Normalize(float[] v)
{
    double len = Math.Sqrt((double)v[0] * v[0] + (double)v[1] * v[1] + (double)v[2] * v[2]);
    if (len < 1e-9) return false;
    v[0] = (float)(v[0] / len); v[1] = (float)(v[1] / len); v[2] = (float)(v[2] / len);
    return true;
}

// Recovers Rx*Ry*Rz angles in degrees from a rotation matrix — must match ClewRopeRig.ToEulerXYZ exactly.
void ToEulerXYZ(float[] m, out float degX, out float degY, out float degZ)
{
    degY = (float)Math.Asin(GameMath.Clamp(m[8], -1f, 1f)) * GameMath.RAD2DEG;
    degX = (float)Math.Atan2(-m[9], m[10]) * GameMath.RAD2DEG;
    degZ = (float)Math.Atan2(-m[4], m[0]) * GameMath.RAD2DEG;
}

Sheet Capture(ElementPose[] chain, string ropeName, string pulleyName, string knotName)
{
    ShapeElement rootElem = Find(ropeName);
    ShapeElement knotElem = Find(knotName);
    ShapeElement pulleyElem = Find(pulleyName);
    if (rootElem.From == null) throw new Exception($"{ropeName} has no From");

    float[] toRoot = Mat4f.Create();
    Mat4f.Identity(toRoot);
    float[] local = Mat4f.Create();
    float[] tmp = Mat4f.Create();
    ShapeElement? e = knotElem;
    while (e != null && e != rootElem)
    {
        Mat4f.Identity(local);
        e.GetLocalTransformMatrix(0, local);
        Mat4f.Mul(tmp, local, toRoot);
        Mat4f.Copy(toRoot, tmp);
        e = e.ParentElement;
    }
    if (e != rootElem) throw new Exception($"{knotName} is not under {ropeName}");

    float[] pivot = Sixteenths(rootElem.RotationOrigin);
    float[] from = Sixteenths(rootElem.From);
    float[] offset =
    {
        toRoot[12] + from[0] - pivot[0],
        toRoot[13] + from[1] - pivot[1],
        toRoot[14] + from[2] - pivot[2],
    };
    if (Math.Abs(offset[1]) < 1e-6f) throw new Exception($"{ropeName} offset has no axial run");

    float[] restRotation = Mat4f.Create();
    Mat4f.Identity(restRotation);
    Mat4f.RotateByXYZ(restRotation,
        (float)rootElem.RotationX * GameMath.DEG2RAD,
        (float)rootElem.RotationY * GameMath.DEG2RAD,
        (float)rootElem.RotationZ * GameMath.DEG2RAD);

    float[] anchor =
    {
        pivot[0] + Dot(restRotation, 0, offset),
        pivot[1] + Dot(restRotation, 1, offset),
        pivot[2] + Dot(restRotation, 2, offset),
        1,
    };
    float[] lifted = new float[4];
    for (int i = chain.Length - 1; i >= 0; i--)
    {
        Mat4f.Identity(local);
        chain[i].ForElement.GetLocalTransformMatrix(0, local, chain[i]);
        Mat4f.MulWithVec4(local, anchor, lifted);
        Array.Copy(lifted, anchor, 4);
    }

    // The knot's own rest orientation, needed to counter-rotate it back to this every frame (see Solve):
    // every bone between root and knot is a plain [0,16,0] translation with no rotation of its own, so the
    // knot inherits the ROOT's full solved rotation verbatim, which would otherwise make a "knot tied to a
    // fixed post" visibly spin as the rope swings.
    float[] knotRestRotation = Mat4f.Create();
    Mat4f.Identity(knotRestRotation);
    Mat4f.RotateByXYZ(knotRestRotation,
        (float)knotElem.RotationX * GameMath.DEG2RAD,
        (float)knotElem.RotationY * GameMath.DEG2RAD,
        (float)knotElem.RotationZ * GameMath.DEG2RAD);

    return new Sheet
    {
        RootElem = rootElem, Pivot = pivot, Offset = offset, RestRotation = restRotation, Anchor = anchor,
        KnotElem = knotElem, KnotRestRotation = knotRestRotation, PulleyElem = pulleyElem,
    };
}

// (rootRx,rootRy,rootRz,scale,knotRx,knotRy,knotRz), or null if degenerate (should not happen across
// +-90deg for this rig).
(float rootRx, float rootRy, float rootRz, float scale, float knotRx, float knotRy, float knotRz)? Solve(Sheet sheet, float[] mA)
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

    ToEulerXYZ(mC, out float ex, out float ey, out float ez);

    // Counter-rotate the knot: every bone between root and knot is a pure translation (no rotation of its
    // own), so the knot inherits mC verbatim. To keep it world-fixed ("tied to a post"), give it the
    // rotation that undoes mC and restores the REST orientation instead: mC^-1 * RestRotation, composed
    // with the knot's own authored baseline (see Capture).
    float[] mCInv = Mat4f.Create();
    Mat4f.Invert(mCInv, mC);
    float[] correction = Mat4f.Create();
    Mat4f.Mul(correction, mCInv, sheet.RestRotation);
    float[] knotFinal = Mat4f.Create();
    Mat4f.Mul(knotFinal, correction, sheet.KnotRestRotation);
    ToEulerXYZ(knotFinal, out float kx, out float ky, out float kz);

    return (
        ex - (float)sheet.RootElem.RotationX, ey - (float)sheet.RootElem.RotationY, ez - (float)sheet.RootElem.RotationZ,
        (float)s,
        kx - (float)sheet.KnotElem.RotationX, ky - (float)sheet.KnotElem.RotationY, kz - (float)sheet.KnotElem.RotationZ);
}

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

    // Rest-pose ElementPose stand-ins (all-default fields) to feed GetLocalTransformMatrix, matching how
    // ClewRopeRig.Resolve() zeroed the live poses before capture.
    ElementPose[] chain = path.Select(e => new ElementPose { ForElement = e }).ToArray();

    Sheet stbd = Capture(chain, b.StbdRope, b.StbdPulley, b.StbdKnot);
    Sheet port = Capture(chain, b.PortRope, b.PortPulley, b.PortKnot);
    Console.Error.WriteLine($"branch {b.SailRoot}: chain length {chain.Length}");

    sb.Append($"  \"{b.SailRoot}\": {{\n");
    for (int frame = 0; frame <= 180; frame++)
    {
        float angleDeg = frame - 90f;

        // mC: rotator-parent space -> clew-root's-parent space. Only chain[0] (the rotator) carries the
        // swing angle, via a scratch pose rather than a live one — there is no live animator here.
        float[] mC = Mat4f.Create();
        Mat4f.Identity(mC);
        float[] mB = Mat4f.Create();
        float[] mD = Mat4f.Create();
        for (int i = 0; i < chain.Length; i++)
        {
            ElementPose tf = chain[i];
            if (i == 0) tf = new ElementPose { ForElement = chain[i].ForElement, degOffY = angleDeg };
            Mat4f.Identity(mB);
            chain[i].ForElement.GetLocalTransformMatrix(0, mB, tf);
            Mat4f.Mul(mD, mC, mB);
            Mat4f.Copy(mC, mD);
        }
        float[] mA = Mat4f.Create();
        Mat4f.Invert(mA, mC);

        var rStbd = Solve(stbd, mA);
        var rPort = Solve(port, mA);
        if (rStbd == null || rPort == null)
        {
            Console.Error.WriteLine($"  WARNING: branch {b.SailRoot} frame {frame} (angle {angleDeg}) degenerate solve, skipped");
            continue;
        }

        // rotShortestDistance*: forces the runtime blend between adjacent baked frames to take the short
        // arc rather than a plain lerp of the raw Euler values. Baked deltas between adjacent integer
        // frames are small (verified <1deg for this rig across the whole sweep) so this is normally a
        // no-op, but it's the engine's own documented fix for angle-wrap interpolation artifacts and costs
        // nothing to have on.
        //
        // Pulley reciprocal scale: the root's stretchY propagates multiplicatively down the whole chain
        // (plain [0,16,0] bones have no scale of their own to absorb it), so the pulley block would stretch
        // right along with the rope unless given 1/scale of its own to cancel that back out.
        float stbdPulleyScale = Math.Abs(rStbd.Value.scale) > 1e-6f ? 1f / rStbd.Value.scale : 1f;
        float portPulleyScale = Math.Abs(rPort.Value.scale) > 1e-6f ? 1f / rPort.Value.scale : 1f;
        sb.Append($"    \"{frame}\": {{\n");
        sb.Append($"      \"{b.StbdRope}\": {{ \"rotationX\": {rStbd.Value.rootRx:R}, \"rotationY\": {rStbd.Value.rootRy:R}, \"rotationZ\": {rStbd.Value.rootRz:R}, \"stretchX\": 1.0, \"stretchY\": {rStbd.Value.scale:R}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }},\n");
        sb.Append($"      \"{b.PortRope}\": {{ \"rotationX\": {rPort.Value.rootRx:R}, \"rotationY\": {rPort.Value.rootRy:R}, \"rotationZ\": {rPort.Value.rootRz:R}, \"stretchX\": 1.0, \"stretchY\": {rPort.Value.scale:R}, \"stretchZ\": 1.0, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }},\n");
        sb.Append($"      \"{b.StbdKnot}\": {{ \"rotationX\": {rStbd.Value.knotRx:R}, \"rotationY\": {rStbd.Value.knotRy:R}, \"rotationZ\": {rStbd.Value.knotRz:R}, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }},\n");
        sb.Append($"      \"{b.PortKnot}\": {{ \"rotationX\": {rPort.Value.knotRx:R}, \"rotationY\": {rPort.Value.knotRy:R}, \"rotationZ\": {rPort.Value.knotRz:R}, \"rotShortestDistanceX\": true, \"rotShortestDistanceY\": true, \"rotShortestDistanceZ\": true }},\n");
        sb.Append($"      \"{b.StbdPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {stbdPulleyScale:R}, \"stretchZ\": 1.0 }},\n");
        sb.Append($"      \"{b.PortPulley}\": {{ \"stretchX\": 1.0, \"stretchY\": {portPulleyScale:R}, \"stretchZ\": 1.0 }}\n");
        sb.Append(frame == 180 ? "    }\n" : "    },\n");
    }
    sb.Append(bi == branches.Length - 1 ? "  }\n" : "  },\n");
}
sb.Append("}\n");

File.WriteAllText(outPath, sb.ToString());
Console.Error.WriteLine($"wrote {outPath}");

readonly record struct Branch(string SailRoot, string ClewRoot, string StbdRope, string PortRope,
    string StbdPulley, string PortPulley, string StbdKnot, string PortKnot);

sealed class Sheet
{
    public required ShapeElement RootElem;
    public required float[] Pivot;
    public required float[] Offset;
    public required float[] RestRotation;
    public required float[] Anchor;
    public required ShapeElement KnotElem;
    public required float[] KnotRestRotation;
    public required ShapeElement PulleyElem;
}
