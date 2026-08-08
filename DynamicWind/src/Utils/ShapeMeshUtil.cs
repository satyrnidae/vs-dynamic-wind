using System;
using Vintagestory.API.Client;

namespace DynamicWind.Utils;

/// <summary>Reusable unit meshes for procedural front-line decorations (cold/warm front pips/bumps, see
/// <see cref="DynamicWind.Client.MapLayers.FrontMapLayer"/>). Both shapes hinge at local X=0 (Y in [-1, 1])
/// and poke out to X=+1, so <c>Translate(sx,sy,z).RotateZ(angle).Scale(pokeLen, halfWidth, 0)</c> protrudes
/// the decoration from its anchor point in the front's local facing direction.</summary>
public static class ShapeMeshUtil
{
    /// <summary>An isoceles triangle: hinge edge at x=0 (y=-1..1), apex poking out to x=1 - the cold-front pip.</summary>
    public static MeshData GetTriangle()
    {
        var mesh = new MeshData();
        mesh.SetXyz(new float[]
        {
            0f, -1f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f
        });
        mesh.SetUv(new float[]
        {
            0f, 0f,
            0f, 0f,
            0f, 0f
        });
        mesh.SetVerticesCount(3);
        mesh.SetIndices(new[] { 0, 1, 2 });
        mesh.SetIndicesCount(3);
        return mesh;
    }

    /// <summary>A half-disc fan: flat diameter at x=0 (y=-1..1) around a hinge centre at the origin, bulging
    /// out to x=1 at y=0 - the warm-front hemicircle bump.</summary>
    public static MeshData GetSemicircle(int segments = 8)
    {
        int rimCount = segments + 1;
        int vertCount = rimCount + 1; // + centre
        var xyz = new float[vertCount * 3];
        var uv = new float[vertCount * 2];

        // Centre (the hinge point on the line).
        xyz[0] = 0f; xyz[1] = 0f; xyz[2] = 0f;

        for (int k = 0; k <= segments; k++)
        {
            double angle = -Math.PI / 2.0 + k * (Math.PI / segments);
            int vi = k + 1;
            xyz[vi * 3] = (float)Math.Cos(angle);
            xyz[vi * 3 + 1] = (float)Math.Sin(angle);
            xyz[vi * 3 + 2] = 0f;
        }

        var indices = new int[segments * 3];
        for (int k = 0; k < segments; k++)
        {
            indices[k * 3] = 0;
            indices[k * 3 + 1] = k + 1;
            indices[k * 3 + 2] = k + 2;
        }

        var mesh = new MeshData();
        mesh.SetXyz(xyz);
        mesh.SetUv(uv);
        mesh.SetVerticesCount(vertCount);
        mesh.SetIndices(indices);
        mesh.SetIndicesCount(indices.Length);
        return mesh;
    }
}
