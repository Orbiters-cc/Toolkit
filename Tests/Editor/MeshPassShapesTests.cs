using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.Meshes;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

// The mesh passes read and write blendshapes as sparse tables: each must make exactly the mesh the dense code made
// (MeshPassesReference), stream for stream, weight for weight and frame for frame, on skinned meshes with several kinds of
// shapes (sparse, dense, several frames, normal and tangent offsets, offsets too small to count, normals only).
public sealed class MeshPassShapesTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    private T Own<T>(T o) where T : Object { owned.Add(o); return o; }

    [Test] public void ReachCountsWhatTheDenseFramesMove()
    {
        var mesh = Own(Grid(9, 3, 1));
        // The dense count, as it was.
        var deltas = new Vector3[mesh.vertexCount];
        for (int shape = 0; shape < mesh.blendShapeCount; shape++)
        {
            var by = new bool[mesh.vertexCount];
            int count = 0;
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                mesh.GetBlendShapeFrameVertices(shape, frame, deltas, null, null);
                for (int v = 0; v < deltas.Length; v++) if (!by[v] && deltas[v].sqrMagnitude > 1e-10f) { by[v] = true; count++; }
            }
            var reach = MeshParts.Reach(mesh, out var moved);
            Assert.That(reach[shape], Is.EqualTo(count), mesh.GetBlendShapeName(shape));
            CollectionAssert.AreEqual(by, moved[shape], mesh.GetBlendShapeName(shape));
            Assert.That(MeshParts.Reach(BlendShapeTable.Read(mesh))[shape], Is.EqualTo(count), "from a table, without marks");
        }
    }

    [Test] public void ASubsetIsTheMeshTheDenseCodeMade()
    {
        var mesh = Own(Grid(12, 3, 2));
        Func<int, int, int, bool> keep = (a, b, c) => (a + b + c) % 5 != 0;
        Func<int, bool> shape = s => s != 2;
        var expected = Own(MeshPassesReference.Subset(mesh, keep, shape, out var was, false));
        var actual = Own(MeshParts.Subset(mesh, keep, shape, out var now));
        CollectionAssert.AreEqual(was, now);
        Same(expected, actual);
        // Without shapes, none.
        Same(Own(MeshPassesReference.Subset(mesh, keep, null, out _, false)), Own(MeshParts.Subset(mesh, keep)));
    }

    [Test] public void AJoinIsTheMeshTheDenseCodeMade()
    {
        var first = Own(Grid(8, 2, 1));
        var second = Own(Grid(7, 3, 3, renamed: true));
        Same(Own(MeshPassesReference.Join(first, second, new[] { 1, 0, 1 }, true)), Own(MeshParts.Join(first, second, new[] { 1, 0, 1 })));
        Same(Own(MeshPassesReference.Join(second, first, null, true)), Own(MeshParts.Join(second, first)));
        // A mesh without shapes joining one with them.
        var plain = Own(Grid(5, 1, 0, shapes: false));
        Same(Own(MeshPassesReference.Join(plain, first, null, true)), Own(MeshParts.Join(plain, first)));
    }

    [Test] public void AReplacementIsTheMeshTheDenseCodeMade()
    {
        var mesh = Own(Grid(12, 3, 4));
        Func<int, int, int, bool> keep = (a, b, c) => a % 3 == 0;
        // The replacement: another mesh with some of the same shapes, more frames of one and fewer of another.
        var replacement = Own(Grid(6, 2, 5, renamed: false, frames: 1));
        Func<int, bool> shape = s => s != 1;
        Same(Own(MeshPassesReference.Replace(mesh, keep, replacement, new[] { 1, 0 }, shape)), Own(MeshParts.Replace(mesh, keep, replacement, new[] { 1, 0 }, shape)));
    }

    [Test] public void TransferredShapesAreWhatTheDenseCodeAdded()
    {
        var from = Own(Grid(10, 2, 6));
        var count = 40;
        var random = new System.Random(7);
        var corners = new int[count * 3];
        var weights = new float[count * 3];
        for (int i = 0; i < count; i++)
        {
            // Some made vertices on nothing (no triangle found: corners 0, weights 0), the others on three random corners.
            if (i % 9 == 4) continue;
            for (int k = 0; k < 3; k++) { corners[3 * i + k] = random.Next(from.vertexCount); weights[3 * i + k] = (float)random.NextDouble(); }
        }
        var made = new Vector3[count];
        for (int i = 0; i < count; i++) made[i] = new Vector3(i, 0, 0);
        var expected = Own(new Mesh { vertices = made });
        var actual = Own(new Mesh { vertices = made });
        // A shape it has already stays first.
        foreach (var target in new[] { expected, actual }) target.AddBlendShapeFrame("Own", 100, made.Select(v => Vector3.up).ToArray(), null, null);
        MeshPassesReference.Apply(from, expected, corners, weights, s => s != 0);
        new MeshParts.ShapeSource(from).Apply(actual, new MeshParts.ShapeSource.Matches { Corners = corners, Weights = weights }, s => s != 0);
        Same(expected, actual);
    }

    [Test] public void MergedShapesAreWhatTheDenseCodeAdded()
    {
        var a = Own(Grid(9, 2, 7));
        var b = Own(Grid(7, 3, 8));
        var c = Own(Grid(5, 1, 9, renamed: true));
        // The second part in another space (a mirrored, scaled, turned bind), the third as it is.
        var space = Matrix4x4.TRS(new Vector3(.1f, .2f, .3f), Quaternion.Euler(10, 70, -20), new Vector3(-1.3f, .7f, 1.1f));
        var parts = new List<(Mesh Mesh, int Offset, Matrix4x4 Space, string Name)>
        {
            (a, 0, Matrix4x4.identity, "Body"), (b, a.vertexCount, space, "Shirt"), (c, a.vertexCount + b.vertexCount, Matrix4x4.identity, "Hat"),
        };
        int total = a.vertexCount + b.vertexCount + c.vertexCount;
        var expected = Own(new Mesh { indexFormat = IndexFormat.UInt32, vertices = new Vector3[total] });
        var actual = Own(new Mesh { indexFormat = IndexFormat.UInt32, vertices = new Vector3[total] });
        var names = MeshPassesReference.MergeShapes(expected, parts);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new List<BlendShapeTable.Shape>();
        for (int i = 0; i < parts.Count; i++)
            CollectionAssert.AreEquivalent(names[i], MeshMerge.AddShapes(parts[i].Mesh, parts[i].Offset, parts[i].Space, parts[i].Name, taken, shapes), "each shape's name in the merged mesh");
        new BlendShapeTable(total, shapes).WriteTo(actual);
        Same(expected, actual);
    }

    // A mesh written by a pass answers with what was written, until it changes.
    [Test] public void WrittenShapesAreKnownUntilTheMeshChanges()
    {
        var mesh = Own(Grid(6, 1, 10));
        var table = BlendShapeTable.Read(mesh);
        MeshShapes.Write(mesh, table);
        Assert.That(MeshShapes.Of(mesh), Is.SameAs(table));
        mesh.AddBlendShapeFrame("Added", 100, new Vector3[mesh.vertexCount], null, null);
        var read = MeshShapes.Of(mesh);
        Assert.That(read, Is.Not.SameAs(table), "a shape added in place");
        Assert.That(read.ShapeCount, Is.EqualTo(table.ShapeCount + 1));
        MeshShapes.Write(mesh, table);
        mesh.ClearBlendShapes();
        mesh.AddBlendShapeFrame("Other", 100, Enumerable.Repeat(Vector3.up, mesh.vertexCount).ToArray(), null, null);
        Assert.That(MeshShapes.Of(mesh).Shapes[0].Name, Is.EqualTo("Other"), "shapes cleared and added again");
    }

    // ---- Fixture ----

    // A grid of size × size vertices in `submeshes` bands, with every vertex stream a skinned mesh has and shapes of
    // every kind; `seed` varies the values, `renamed` gives some shapes other names, `frames` the multi-frame shape's count.
    private static Mesh Grid(int size, int submeshes, int seed, bool renamed = false, int frames = 3, bool shapes = true)
    {
        var random = new System.Random(seed);
        float R() => (float)(random.NextDouble() * 2 - 1);
        int n = size * size;
        var mesh = new Mesh { name = "Grid " + seed };
        var vertices = new Vector3[n];
        var normals = new Vector3[n];
        var tangents = new Vector4[n];
        var colors = new Color[n];
        var uv0 = new List<Vector2>(); var uv1 = new List<Vector3>(); var uv3 = new List<Vector4>();
        for (int y = 0, v = 0; y < size; y++)
            for (int x = 0; x < size; x++, v++)
            {
                vertices[v] = new Vector3(x * .1f, y * .1f, R() * .01f);
                normals[v] = new Vector3(R() * .1f, R() * .1f, 1).normalized;
                tangents[v] = new Vector4(1, R() * .1f, 0, v % 2 == 0 ? 1 : -1);
                colors[v] = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), 1);
                uv0.Add(new Vector2(x / (float)size, y / (float)size)); uv1.Add(new Vector3(R(), R(), R())); uv3.Add(new Vector4(R(), R(), R(), R()));
            }
        mesh.vertices = vertices; mesh.normals = normals; mesh.tangents = tangents; mesh.colors = colors;
        mesh.SetUVs(0, uv0); mesh.SetUVs(1, uv1); mesh.SetUVs(3, uv3);
        var bands = Enumerable.Range(0, submeshes).Select(_ => new List<int>()).ToList();
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                int v = y * size + x;
                // Neighbouring bands share their boundary rows' vertices.
                var band = bands[Math.Min(submeshes - 1, y * submeshes / (size - 1))];
                band.AddRange(new[] { v, v + size, v + 1, v + 1, v + size, v + size + 1 });
            }
        mesh.subMeshCount = submeshes;
        for (int s = 0; s < submeshes; s++) mesh.SetIndices(bands[s], MeshTopology.Triangles, s);
        // One to four bones per vertex, heaviest first.
        var counts = new byte[n];
        var weights = new List<BoneWeight1>();
        for (int v = 0; v < n; v++)
        {
            int k = 1 + v % 4;
            counts[v] = (byte)k;
            var w = Enumerable.Range(0, k).Select(_ => (float)random.NextDouble() + .01f).OrderByDescending(x => x).ToList();
            float sum = w.Sum();
            for (int i = 0; i < k; i++) weights.Add(new BoneWeight1 { boneIndex = (v + i) % 5, weight = w[i] / sum });
        }
        using (var perVertex = new NativeArray<byte>(counts, Allocator.Temp))
        using (var all = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
            mesh.SetBoneWeights(perVertex, all);
        mesh.bindposes = Enumerable.Range(0, 5).Select(i => Matrix4x4.TRS(new Vector3(0, -i * .1f, 0), Quaternion.Euler(0, i * 5, 0), Vector3.one)).ToArray();
        if (!shapes) return mesh;

        Vector3[] Dense(Func<int, Vector3> at) => Enumerable.Range(0, n).Select(at).ToArray();
        // Sparse: a few vertices, offsets only.
        mesh.AddBlendShapeFrame(renamed ? "Blink" : "Smile", 100, Dense(v => v % 7 == 0 ? new Vector3(R(), R(), R()) * .01f : Vector3.zero), null, null);
        // Dense, with normals and tangents.
        mesh.AddBlendShapeFrame("Body", 100, Dense(v => new Vector3(R(), R(), R()) * .02f), Dense(v => new Vector3(R(), R(), R()) * .1f), Dense(v => new Vector3(R(), R(), R()) * .1f));
        // Several frames, each moving its own vertices (a vertex one frame leaves out, another moves).
        for (int f = 0; f < frames; f++)
        {
            int frame = f;
            mesh.AddBlendShapeFrame("Flex", 100f * (f + 1) / frames, Dense(v => (v + frame) % 3 != 0 ? new Vector3(R(), R(), R()) * .01f * (frame + 1) : Vector3.zero),
                Dense(v => v % 2 == frame % 2 ? new Vector3(R(), R(), R()) * .05f : Vector3.zero), null);
        }
        // Offsets too small to count as moving, and one moving normals only.
        mesh.AddBlendShapeFrame("Tiny", 100, Dense(v => v % 5 == 0 ? Vector3.one * 1e-7f : Vector3.zero), null, null);
        mesh.AddBlendShapeFrame("Shade", 100, new Vector3[n], Dense(v => v % 4 == 1 ? Vector3.up * .3f : Vector3.zero), null);
        if (renamed) mesh.AddBlendShapeFrame("Hat only", 50, Dense(v => Vector3.forward * .01f), null, null);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Everything a mesh holds, compared value for value (exact floats: Unity's Vector3 == is approximate).
    private static void Same(Mesh expected, Mesh actual)
    {
        Assert.That(actual.vertexCount, Is.EqualTo(expected.vertexCount), "vertices");
        Exact(expected.vertices, actual.vertices, "positions");
        Exact(expected.normals, actual.normals, "normals");
        CollectionAssert.AreEqual(expected.tangents, actual.tangents, "tangents");
        CollectionAssert.AreEqual(expected.colors, actual.colors, "colors");
        for (int channel = 0; channel < 8; channel++)
        {
            var attribute = VertexAttribute.TexCoord0 + channel;
            Assert.That(actual.HasVertexAttribute(attribute), Is.EqualTo(expected.HasVertexAttribute(attribute)), "uv" + channel);
            if (!expected.HasVertexAttribute(attribute)) continue;
            Assert.That(actual.GetVertexAttributeDimension(attribute), Is.EqualTo(expected.GetVertexAttributeDimension(attribute)), "uv" + channel);
            var a = new List<Vector4>(); var b = new List<Vector4>();
            expected.GetUVs(channel, a); actual.GetUVs(channel, b);
            CollectionAssert.AreEqual(a, b, "uv" + channel);
        }
        CollectionAssert.AreEqual(expected.GetBonesPerVertex().ToArray(), actual.GetBonesPerVertex().ToArray(), "bones per vertex");
        CollectionAssert.AreEqual(expected.GetAllBoneWeights().ToArray(), actual.GetAllBoneWeights().ToArray(), "bone weights");
        CollectionAssert.AreEqual(expected.bindposes, actual.bindposes, "bind poses");
        Assert.That(actual.indexFormat, Is.EqualTo(expected.indexFormat), "index format");
        Assert.That(actual.subMeshCount, Is.EqualTo(expected.subMeshCount), "submeshes");
        for (int s = 0; s < expected.subMeshCount; s++)
        {
            Assert.That(actual.GetTopology(s), Is.EqualTo(expected.GetTopology(s)), "submesh " + s);
            CollectionAssert.AreEqual(expected.GetIndices(s), actual.GetIndices(s), "submesh " + s);
        }
        Assert.That(actual.bounds, Is.EqualTo(expected.bounds), "bounds");
        Assert.That(actual.blendShapeCount, Is.EqualTo(expected.blendShapeCount), "blendshapes");
        int n = expected.vertexCount;
        var ev = new Vector3[n]; var en = new Vector3[n]; var et = new Vector3[n];
        var av = new Vector3[n]; var an = new Vector3[n]; var at = new Vector3[n];
        for (int shape = 0; shape < expected.blendShapeCount; shape++)
        {
            string name = expected.GetBlendShapeName(shape);
            Assert.That(actual.GetBlendShapeName(shape), Is.EqualTo(name));
            Assert.That(actual.GetBlendShapeFrameCount(shape), Is.EqualTo(expected.GetBlendShapeFrameCount(shape)), name);
            for (int frame = 0; frame < expected.GetBlendShapeFrameCount(shape); frame++)
            {
                Assert.That(actual.GetBlendShapeFrameWeight(shape, frame), Is.EqualTo(expected.GetBlendShapeFrameWeight(shape, frame)), name + " " + frame);
                expected.GetBlendShapeFrameVertices(shape, frame, ev, en, et);
                actual.GetBlendShapeFrameVertices(shape, frame, av, an, at);
                Exact(ev, av, name + " " + frame + " offsets");
                Exact(en, an, name + " " + frame + " normals");
                Exact(et, at, name + " " + frame + " tangents");
            }
        }
    }

    private static void Exact(Vector3[] expected, Vector3[] actual, string what)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i].x != actual[i].x || expected[i].y != actual[i].y || expected[i].z != actual[i].z)
                Assert.Fail(what + " differ at " + i + ": " + expected[i].ToString("R") + " and " + actual[i].ToString("R"));
    }
}
