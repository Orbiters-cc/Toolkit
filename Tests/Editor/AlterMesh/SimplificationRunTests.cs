using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.AlterMesh;
using Orbiters.Toolkit.Meshes;
using UnityEngine;
using Object = UnityEngine.Object;

// TKG-23, TKG-24, TKG-25, TKG-3: one run, any prefix; welded topology with seams that keep their islands; exact shapes.
public sealed class SimplificationRunTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    // A curved sheet in two UV islands: the column at x = seam is split (same places, the right island's UVs shifted).
    private static Mesh Sheet(int columns, int rows, int seam)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var index = new int[columns + 2, rows + 1];
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                float u = x / (float)columns, v = y / (float)rows;
                var p = new Vector3(u, .15f * Mathf.Sin(u * 3f) * Mathf.Cos(v * 2f), v);
                index[x, y] = vertices.Count;
                vertices.Add(p); normals.Add(Vector3.up);
                uvs.Add(new Vector2(x <= seam ? u * .5f : .5f + u * .5f, v));
                if (x == seam)
                {
                    // The right island's copy of the seam column.
                    index[columns + 1, y] = vertices.Count;
                    vertices.Add(p); normals.Add(Vector3.up);
                    uvs.Add(new Vector2(.5f + u * .5f, v));
                }
            }
        int At(int x, int y, bool right) => x == seam && right ? index[columns + 1, y] : index[x, y];
        var triangles = new List<int>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                bool right = x >= seam;
                int a = At(x, y, right), b = At(x + 1, y, right), c = At(x, y + 1, right), d = At(x + 1, y + 1, right);
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

    [Test] public void EveryPrefixIsAMeshOfTheRun()
    {
        var mesh = Own(Sheet(16, 12, 8));
        var run = SimplificationRun.Prepare(mesh, SimplificationOptions.Default);
        run.Run();
        Assert.That(run.TriangleCount, Is.EqualTo(16 * 12 * 2));
        Assert.That(run.Count, Is.GreaterThan(50), "most of a gently curved sheet collapses");
        int last = run.TriangleCount;
        for (int k = 0; k <= run.Count; k++)
        {
            Assert.That(run.TrianglesAt(k), Is.LessThanOrEqualTo(last), "never more triangles with more collapses");
            last = run.TrianglesAt(k);
            if (k > 0) Assert.That(run.DeviationAt(k), Is.GreaterThanOrEqualTo(run.DeviationAt(k - 1)));
        }
        foreach (int k in new[] { 0, 1, run.Count / 3, run.Count / 2, run.Count })
        {
            var result = Own(run.Write(k, new Mesh()));
            Assert.That(result.triangles.Length / 3, Is.EqualTo(run.TrianglesAt(k)), "prefix " + k + ": the log counts what a replay writes");
            var kept = run.KeptVertices(k);
            var source = mesh.vertices;
            var written = result.vertices;
            var sourceUvs = mesh.uv;
            var writtenUvs = result.uv;
            Assert.That(written.Length, Is.EqualTo(kept.Length));
            for (int j = 0; j < kept.Length; j++)
            {
                Assert.That(written[j], Is.EqualTo(source[kept[j]]), "kept vertices are unchanged");
                Assert.That(writtenUvs[j], Is.EqualTo(sourceUvs[kept[j]]));
            }
        }
        int half = run.CollapsesFor(.5f);
        Assert.That(run.TrianglesAt(half), Is.LessThanOrEqualTo(run.TriangleCount / 2));
        Assert.That(half == 0 || run.TrianglesAt(half - 1) > run.TriangleCount / 2, Is.True, "the fewest collapses that get there");
        Assert.That(run.CollapsesWithin(0f), Is.LessThanOrEqualTo(run.CollapsesWithin(.01f)));
        Assert.That(run.DeviationAt(run.CollapsesWithin(.01f)), Is.LessThanOrEqualTo(.01f));
    }

    [Test] public void SeamsKeepTheirIslandsAndTheirPairs()
    {
        var mesh = Own(Sheet(16, 12, 8));
        var run = SimplificationRun.Prepare(mesh, SimplificationOptions.Default);
        run.Run();
        var result = Own(run.Write(run.Count, new Mesh()));
        var uvs = result.uv;
        var vertices = result.vertices;
        var triangles = result.triangles;
        Assert.That(triangles.Length, Is.LessThan(mesh.triangles.Length / 2));
        for (int t = 0; t < triangles.Length; t += 3)
        {
            // Left island: u <= .25 (the seam column at u = .25 on the left, .75 on the right).
            bool a = uvs[triangles[t]].x <= .25f + 1e-5f, b = uvs[triangles[t + 1]].x <= .25f + 1e-5f, c = uvs[triangles[t + 2]].x <= .25f + 1e-5f;
            Assert.That(a == b && b == c, Is.True, "triangle " + t / 3 + " takes its UVs from one island");
        }
        // Every place on the seam that is left still has both of its copies.
        var byPlace = new Dictionary<Vector3, int>();
        foreach (var p in vertices) byPlace[p] = byPlace.TryGetValue(p, out int n) ? n + 1 : 1;
        var source = mesh.vertices;
        var seamPlaces = new HashSet<Vector3>(source.Where(p => Mathf.Abs(p.x - .5f) < 1e-6f));
        foreach (var place in seamPlaces.Where(byPlace.ContainsKey))
            Assert.That(byPlace[place], Is.EqualTo(2), "the seam point at " + place + " keeps both wedges");
    }

    [Test] public void LockedBordersAndVerticesStay()
    {
        var mesh = Own(Sheet(14, 10, 7));
        var source = mesh.vertices;
        var locked = new bool[source.Length];
        locked[source.Length / 2] = true;
        var options = SimplificationOptions.Default;
        options.LockBorders = true;
        options.LockedVertices = locked;
        var run = SimplificationRun.Prepare(mesh, options);
        run.Run();
        var kept = new HashSet<int>(run.KeptVertices(run.Count));
        Assert.That(run.Count, Is.GreaterThan(0));
        for (int v = 0; v < source.Length; v++)
        {
            var p = source[v];
            bool border = p.x <= 1e-6f || p.x >= 1f - 1e-6f || p.z <= 1e-6f || p.z >= 1f - 1e-6f;
            if (border || locked[v]) Assert.That(kept.Contains(v), Is.True, "vertex " + v + " at " + p + " stays");
        }
    }

    [Test] public void ADeviationBudgetBoundsTheRun()
    {
        var mesh = Own(Sheet(16, 12, 8));
        var options = SimplificationOptions.Default;
        options.MaxDeviation = 1e-4f;
        var tight = SimplificationRun.Prepare(mesh, options);
        tight.Run();
        var loose = SimplificationRun.Prepare(mesh, SimplificationOptions.Default);
        loose.Run();
        Assert.That(tight.DeviationAt(tight.Count), Is.LessThanOrEqualTo(1e-4f * 1.001f));
        Assert.That(tight.TrianglesAt(tight.Count), Is.GreaterThanOrEqualTo(loose.TrianglesAt(loose.Count)));
    }

    [Test] public void BlendshapesAreReplayedExactly()
    {
        var mesh = Own(Sheet(12, 10, 6));
        int n = mesh.vertexCount;
        var deltas = new Vector3[n];
        var normals = new Vector3[n];
        for (int v = 0; v < n; v++)
            if (v % 3 == 0) { deltas[v] = new Vector3(0, .01f * v, 0); normals[v] = new Vector3(.1f, 0, 0); }
        mesh.AddBlendShapeFrame("Flex", 100f, deltas, normals, null);
        var table = BlendShapeTable.Read(mesh);
        var run = SimplificationRun.Prepare(mesh, SimplificationOptions.Default);
        run.Run();
        int k = run.Count / 2;
        var result = Own(run.Write(k, new Mesh(), table));
        var kept = run.KeptVertices(k);
        Assert.That(result.blendShapeCount, Is.EqualTo(1));
        var got = new Vector3[result.vertexCount];
        var gotNormals = new Vector3[result.vertexCount];
        result.GetBlendShapeFrameVertices(0, 0, got, gotNormals, null);
        for (int j = 0; j < kept.Length; j++)
        {
            Assert.That(got[j], Is.EqualTo(deltas[kept[j]]), "vertex " + j + " keeps its own offset");
            Assert.That(gotNormals[j], Is.EqualTo(normals[kept[j]]));
        }
        var replayed = run.Shapes(k, table);
        Assert.That(replayed.VertexCount, Is.EqualTo(result.vertexCount));
        Assert.That(replayed.Entries, Is.LessThanOrEqualTo(table.Entries));
    }

    [Test] public void SkinWeightsAndSubmeshesFollow()
    {
        var mesh = Own(Sheet(10, 8, 5));
        int n = mesh.vertexCount;
        var weights = new BoneWeight[n];
        for (int v = 0; v < n; v++) weights[v] = new BoneWeight { boneIndex0 = v % 2, weight0 = .75f, boneIndex1 = 2, weight1 = .25f };
        mesh.boneWeights = weights;
        mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
        var triangles = mesh.triangles;
        mesh.subMeshCount = 2;
        mesh.SetTriangles(triangles.Take(triangles.Length / 2).ToArray(), 0);
        mesh.SetTriangles(triangles.Skip(triangles.Length / 2).ToArray(), 1);
        var run = SimplificationRun.Prepare(mesh, SimplificationOptions.Default);
        run.Run();
        var result = Own(run.Write(run.Count, new Mesh()));
        Assert.That(result.subMeshCount, Is.EqualTo(2));
        Assert.That(result.bindposes.Length, Is.EqualTo(3));
        var kept = run.KeptVertices(run.Count);
        var written = result.boneWeights;
        for (int j = 0; j < kept.Length; j++)
        {
            Assert.That(written[j].boneIndex0, Is.EqualTo(weights[kept[j]].boneIndex0));
            Assert.That(written[j].weight0, Is.EqualTo(weights[kept[j]].weight0).Within(1e-6f));
        }
    }
}
