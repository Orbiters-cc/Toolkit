using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.AlterMesh;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

// AlterMesh's changes from Meshia 3.2.0 (Editor/AlterMesh/README.md): without each, a mesh is shaded wrong, a build stops,
// or vertices are never joined.
public sealed class AlterMeshTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void BlendshapeNormalAndTangentOffsetsKeepTheirLengthThroughCollapses()
    {
        var mesh = Own(Grid(12, 12));
        int count = mesh.vertexCount;
        mesh.AddBlendShapeFrame("Flex", 100, Enumerable.Repeat(new Vector3(0, 0, .01f), count).ToArray(),
            Enumerable.Repeat(new Vector3(.5f, 0, 0), count).ToArray(), Enumerable.Repeat(new Vector3(0, .25f, 0), count).ToArray());

        var simplified = Simplify(mesh, MeshSimplifierOptions.Default);

        Assert.That(simplified.triangles.Length, Is.LessThan(mesh.triangles.Length), "Edges collapsed.");
        var deltas = new Vector3[simplified.vertexCount];
        var normals = new Vector3[simplified.vertexCount];
        var tangents = new Vector3[simplified.vertexCount];
        simplified.GetBlendShapeFrameVertices(0, 0, deltas, normals, tangents);
        Assert.That(normals.All(n => (n - new Vector3(.5f, 0, 0)).sqrMagnitude < 1e-8f), Is.True, "Offsets are not directions: normalized, the flex would reshade the mesh.");
        Assert.That(tangents.All(t => (t - new Vector3(0, .25f, 0)).sqrMagnitude < 1e-8f), Is.True);
    }

    [Test] public void AnEmptyTriangleSubmeshSimplifiesAndStays()
    {
        var mesh = Own(Grid(8, 8));
        var triangles = mesh.triangles;
        mesh.subMeshCount = 2;
        mesh.SetTriangles(triangles, 0);
        mesh.SetTriangles(new int[0], 1);

        // Smart links build a k-d tree per submesh (an empty one read a node of an empty list); counting the removed
        // triangles of an empty range threw.
        var simplified = Simplify(mesh, MeshSimplifierOptions.Default);

        Assert.That(simplified.subMeshCount, Is.EqualTo(2));
        Assert.That(simplified.GetSubMesh(1).indexCount, Is.EqualTo(0));
        Assert.That(simplified.GetSubMesh(0).indexCount, Is.GreaterThan(0).And.LessThan(triangles.Length));
    }

    [Test] public void ALinesSubmeshWithoutVerticesSimplifies()
    {
        var mesh = Own(Grid(8, 8));
        int indices = mesh.triangles.Length;
        mesh.subMeshCount = 2;
        mesh.SetSubMesh(1, new SubMeshDescriptor(indices, 0, MeshTopology.Lines));

        var simplified = Simplify(mesh, MeshSimplifierOptions.Default);

        Assert.That(simplified.subMeshCount, Is.EqualTo(2), "Setting zero bits for its vertices threw.");
        Assert.That(simplified.GetSubMesh(0).indexCount, Is.LessThan(indices));
    }

    [Test] public void TheKdTreeFindsEveryPoint()
    {
        var random = new System.Random(7);
        var points = Enumerable.Range(0, 97).Select(_ => new float3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble())).ToArray();
        var tree = new UnsafeKdTree(Allocator.Temp);
        var found = new UnsafeList<int>(16, Allocator.Temp);
        try
        {
            tree.Initialize(points);
            for (int i = 0; i < points.Length; i++)
            {
                found.Clear();
                tree.QueryPointsInSphere(points, points[i], 1e-4f, ref found);
                Assert.That(Contains(found, i), Is.True, $"Point {i} isn't in the tree: Meshia 3.2.0 dropped the one before each median.");
            }
            for (int q = 0; q < 40; q++)
            {
                var center = new float3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                float radius = .05f + .25f * (float)random.NextDouble();
                found.Clear();
                tree.QueryPointsInSphere(points, center, radius, ref found);
                var expected = Enumerable.Range(0, points.Length).Where(i => math.distancesq(points[i], center) < radius * radius).ToList();
                var actual = new List<int>();
                for (int i = 0; i < found.Length; i++) actual.Add(found[i]);
                Assert.That(actual, Is.EquivalentTo(expected));
            }
        }
        finally
        {
            found.Dispose();
            tree.Dispose();
        }
    }

    [Test] public void AnEmptyKdTreeFindsNothing()
    {
        var tree = new UnsafeKdTree(Allocator.Temp);
        var found = new UnsafeList<int>(4, Allocator.Temp);
        try
        {
            tree.Initialize(new float3[0]);
            tree.QueryPointsInSphere(new float3[0], float3.zero, 1f, ref found);
            Assert.That(found.Length, Is.EqualTo(0));
        }
        finally
        {
            found.Dispose();
            tree.Dispose();
        }
    }

    private Mesh Simplify(Mesh mesh, MeshSimplifierOptions options)
    {
        var simplified = Own(new Mesh());
        MeshSimplifier.Simplify(mesh, new MeshSimplificationTarget { Kind = MeshSimplificationTargetKind.RelativeTriangleCount, Value = .5f }, options, simplified);
        return simplified;
    }

    private static bool Contains(UnsafeList<int> list, int value)
    {
        for (int i = 0; i < list.Length; i++) if (list[i] == value) return true;
        return false;
    }

    // A gently curved sheet of quads (a flat one collapses without error everywhere), normals up.
    private static Mesh Grid(int columns, int rows)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                float u = x / (float)columns, v = y / (float)rows;
                vertices.Add(new Vector3(u, .1f * Mathf.Sin(u * 3f) * Mathf.Cos(v * 2f), v));
                normals.Add(Vector3.up);
                uvs.Add(new Vector2(u, v));
            }
        var triangles = new List<int>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = y * (columns + 1) + x, b = a + 1, c = a + columns + 1, d = c + 1;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
        var mesh = new Mesh { name = "Sheet" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateTangents();
        return mesh;
    }

    private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
}
