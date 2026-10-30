using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEngine;

public sealed class MeshPartsSurfaceTests
{
    private readonly List<Mesh> meshes = new List<Mesh>();

    [TearDown] public void TearDown()
    {
        foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
        meshes.Clear();
    }

    [Test] public void SurfaceChecksKeepBothDistanceLimits()
    {
        var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        var triangles = new[] { 0, 1, 2 };
        var points = Enumerable.Repeat(new Vector3(.2f, .3f, 0), 100).ToArray();
        points[0].z = .015f;
        Assert.True(MeshParts.Within(points, vertices, triangles, .01f), "One percent may exceed the budget.");
        points[1].z = .015f;
        Assert.False(MeshParts.Within(points, vertices, triangles, .01f), "More than one percent may not.");
        points[1].z = 0;
        points[0].z = .021f;
        Assert.False(MeshParts.Within(points, vertices, triangles, .01f), "No point may exceed twice the budget.");
    }

    [Test] public void ShadingMatchesTheNearSideAndCannotBorrowADistantNormal()
    {
        var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, Vector3.right, Vector3.up };
        var triangles = new[] { 0, 1, 2, 3, 4, 5 };
        var normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.forward, Vector3.forward, Vector3.forward };
        var points = new[] { new Vector3(.2f, .3f, 0) };
        var facing = new[] { Vector3.forward };
        Assert.True(MeshParts.Within(points, vertices, triangles, .001f, facing, normals), "Either side of a two-sided card may match.");
        for (int i = 3; i < 6; i++) vertices[i].z = .002f;
        Assert.False(MeshParts.Within(points, vertices, triangles, .001f, facing, normals), "A separate sheet cannot hide a flipped normal.");
    }

    [Test] public void ShapeMatchingKeepsTheSideAndFramesAcrossConcurrentQueries()
    {
        var source = Own(new Mesh
        {
            vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, Vector3.right, Vector3.up },
            triangles = new[] { 0, 1, 2, 3, 4, 5 },
            normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.back, Vector3.back, Vector3.back }
        });
        var deltas = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.right, Vector3.right, Vector3.right };
        source.AddBlendShapeFrame("Sides", 50, deltas, null, null);
        source.AddBlendShapeFrame("Sides", 100, deltas.Select(d => d * 2).ToArray(), null, null);
        var shapes = new MeshParts.ShapeSource(source);
        var vertices = new[] { new Vector3(.2f, .3f, 0), new Vector3(.2f, .3f, 0) };
        var normals = new[] { Vector3.forward, Vector3.back };
        var queries = Enumerable.Range(0, 4).Select(_ => Task.Run(() => shapes.Match(vertices, normals))).ToArray();
        Task.WaitAll(queries);
        foreach (var query in queries)
        {
            var target = Own(new Mesh { vertices = vertices, normals = normals });
            shapes.Apply(target, query.Result, _ => true);
            Assert.That(target.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            var actual = new Vector3[2];
            target.GetBlendShapeFrameVertices(0, 1, actual, null, null);
            Assert.That(Vector3.Distance(actual[0], Vector3.up * 2), Is.LessThan(1e-6f));
            Assert.That(Vector3.Distance(actual[1], Vector3.right * 2), Is.LessThan(1e-6f));
        }
    }

    [Test] public void SurfaceChecksFindLongSliversAmongTinyTriangles()
    {
        var vertices = new List<Vector3> { Vector3.zero, Vector3.right, new Vector3(1, .002f, 0) };
        var triangles = new List<int> { 0, 1, 2 };
        for (int i = 0; i < 1000; i++)
        {
            int start = vertices.Count;
            var p = new Vector3(i * .002f, 1, 0);
            vertices.AddRange(new[] { p, p + Vector3.right * .001f, p + Vector3.up * .001f });
            triangles.AddRange(new[] { start, start + 1, start + 2 });
        }
        Assert.True(MeshParts.Within(new[] { new Vector3(.75f, .001f, 0) }, vertices.ToArray(), triangles.ToArray(), .0001f));
        Assert.False(MeshParts.Within(new[] { new Vector3(.75f, .01f, 0) }, vertices.ToArray(), triangles.ToArray(), .0001f));
    }

    private Mesh Own(Mesh mesh) { meshes.Add(mesh); return mesh; }
}
