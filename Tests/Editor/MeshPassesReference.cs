using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// The mesh passes' blendshape code as it was before the passes read and wrote BlendShapeTables (MeshParts and MeshMerge,
// 2026-11-10), kept to compare the new code with: every frame read dense from Unity and added back dense.
internal static class MeshPassesReference
{
    // With otherTopologies, submeshes of lines or points come whole.
    internal static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape, out int[] vertices, bool otherTopologies)
    {
        var map = new int[mesh.vertexCount];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        var used = new List<int>();
        var submeshes = new List<(int[] Indices, MeshTopology Topology)>();
        int Map(int v)
        {
            if (map[v] < 0) { map[v] = used.Count; used.Add(v); }
            return map[v];
        }
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var indices = mesh.GetIndices(s);
            var topology = mesh.GetTopology(s);
            var kept = new List<int>();
            if (topology != MeshTopology.Triangles)
            {
                if (otherTopologies) foreach (int v in indices) kept.Add(Map(v));
            }
            else
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    if (!keep(indices[t], indices[t + 1], indices[t + 2])) continue;
                    for (int k = 0; k < 3; k++) kept.Add(Map(indices[t + k]));
                }
            submeshes.Add((kept.ToArray(), topology));
        }
        vertices = used.ToArray();
        var part = new Mesh { name = mesh.name, indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        Copy(mesh, part, used);
        part.subMeshCount = submeshes.Count;
        for (int s = 0; s < submeshes.Count; s++) part.SetIndices(submeshes[s].Indices, submeshes[s].Topology, s, false);
        if (shape != null)
        {
            var picked = vertices;
            var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
            var mv = new Vector3[used.Count]; var mn = new Vector3[used.Count]; var mt = new Vector3[used.Count];
            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                if (!shape(index)) continue;
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                    for (int i = 0; i < picked.Length; i++) { mv[i] = dv[picked[i]]; mn[i] = dn[picked[i]]; mt[i] = dt[picked[i]]; }
                    part.AddBlendShapeFrame(mesh.GetBlendShapeName(index), mesh.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
                }
            }
        }
        part.bindposes = mesh.bindposes;
        part.RecalculateBounds();
        return part;
    }

    internal static Mesh Join(Mesh first, Mesh second, int[] slots, bool shapes)
    {
        int a = first.vertexCount, total = a + second.vertexCount;
        var joined = new Mesh { name = first.name, indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        Append(first, second, joined);
        var indices = Enumerable.Range(0, first.subMeshCount).Select(s => first.GetIndices(s).ToList()).ToList();
        var topologies = Enumerable.Range(0, first.subMeshCount).Select(first.GetTopology).ToList();
        for (int s = 0; s < second.subMeshCount; s++)
        {
            int slot = slots != null ? slots[s] : s;
            while (indices.Count <= slot) { indices.Add(new List<int>()); topologies.Add(second.GetTopology(s)); }
            if (topologies[slot] != second.GetTopology(s) && indices[slot].Count > 0 && second.GetIndexCount(s) > 0)
                throw new ArgumentException("Submesh " + s + " draws " + second.GetTopology(s) + " into a slot of " + topologies[slot] + ".", nameof(slots));
            if (indices[slot].Count == 0) topologies[slot] = second.GetTopology(s);
            indices[slot].AddRange(second.GetIndices(s).Select(i => i + a));
        }
        joined.subMeshCount = indices.Count;
        for (int s = 0; s < indices.Count; s++) joined.SetIndices(indices[s], topologies[s], s, false);
        joined.bindposes = first.bindposes;
        if (!shapes) { joined.RecalculateBounds(); return joined; }

        var names = Enumerable.Range(0, first.blendShapeCount).Select(first.GetBlendShapeName)
            .Concat(Enumerable.Range(0, second.blendShapeCount).Select(second.GetBlendShapeName)).Distinct().ToList();
        var mv = new Vector3[total]; var mn = new Vector3[total]; var mt = new Vector3[total];
        var dv = new Vector3[Math.Max(a, second.vertexCount)]; var dn = new Vector3[dv.Length]; var dt = new Vector3[dv.Length];
        foreach (var name in names)
        {
            int i1 = first.GetBlendShapeIndex(name), i2 = second.GetBlendShapeIndex(name);
            // Frames at every weight either mesh has one at; each mesh moves there as Unity would blend it.
            var weights = new SortedSet<float>();
            if (i1 >= 0) for (int f = 0; f < first.GetBlendShapeFrameCount(i1); f++) weights.Add(first.GetBlendShapeFrameWeight(i1, f));
            if (i2 >= 0) for (int f = 0; f < second.GetBlendShapeFrameCount(i2); f++) weights.Add(second.GetBlendShapeFrameWeight(i2, f));
            foreach (float weight in weights)
            {
                Array.Clear(mv, 0, total); Array.Clear(mn, 0, total); Array.Clear(mt, 0, total);
                if (i1 >= 0)
                {
                    ShapeAt(first, i1, weight, dv, dn, dt);
                    Array.Copy(dv, 0, mv, 0, a); Array.Copy(dn, 0, mn, 0, a); Array.Copy(dt, 0, mt, 0, a);
                }
                if (i2 >= 0)
                {
                    ShapeAt(second, i2, weight, dv, dn, dt);
                    Array.Copy(dv, 0, mv, a, second.vertexCount); Array.Copy(dn, 0, mn, a, second.vertexCount); Array.Copy(dt, 0, mt, a, second.vertexCount);
                }
                joined.AddBlendShapeFrame(name, weight, mv, mn, mt);
            }
        }
        joined.bindposes = first.bindposes;
        joined.RecalculateBounds();
        return joined;
    }

    public static Mesh Replace(Mesh mesh, Func<int, int, int, bool> keep, Mesh replacement, int[] slots, Func<int, bool> shape)
    {
        // Lines and points stay as they are with what is kept.
        var kept = Subset(mesh, keep, null, out var vertices, true);
        try
        {
            var joined = Join(kept, replacement, slots, false);
            int offset = kept.vertexCount, total = joined.vertexCount, extra = replacement.vertexCount;
            var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
            var rv = new Vector3[extra]; var rn = new Vector3[extra]; var rt = new Vector3[extra];
            var mv = new Vector3[total]; var mn = new Vector3[total]; var mt = new Vector3[total];
            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                if (!shape(index)) continue;
                string name = mesh.GetBlendShapeName(index);
                int other = replacement.GetBlendShapeIndex(name);
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                    for (int i = 0; i < vertices.Length; i++) { mv[i] = dv[vertices[i]]; mn[i] = dn[vertices[i]]; mt[i] = dt[vertices[i]]; }
                    if (other >= 0 && frame < replacement.GetBlendShapeFrameCount(other))
                    {
                        replacement.GetBlendShapeFrameVertices(other, frame, rv, rn, rt);
                        Array.Copy(rv, 0, mv, offset, extra); Array.Copy(rn, 0, mn, offset, extra); Array.Copy(rt, 0, mt, offset, extra);
                    }
                    else { Array.Clear(mv, offset, extra); Array.Clear(mn, offset, extra); Array.Clear(mt, offset, extra); }
                    joined.AddBlendShapeFrame(name, mesh.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
                }
            }
            return joined;
        }
        finally { UnityEngine.Object.DestroyImmediate(kept); }
    }

    public static void ShapeAt(Mesh mesh, int shape, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)
    {
        int frames = mesh.GetBlendShapeFrameCount(shape), n = mesh.vertexCount;
        int upper = 0;
        while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
        float upperWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
        float lowerWeight = upper == 0 ? 0 : mesh.GetBlendShapeFrameWeight(shape, upper - 1);
        float t = upperWeight == lowerWeight ? 1 : (weight - lowerWeight) / (upperWeight - lowerWeight);
        Read(mesh, shape, upper, dv, dn, dt);
        for (int v = 0; v < n; v++) { dv[v] *= t; dn[v] *= t; dt[v] *= t; }
        if (upper == 0 || t == 1) return;
        var lv = new Vector3[n]; var ln = new Vector3[n]; var lt = new Vector3[n];
        Read(mesh, shape, upper - 1, lv, ln, lt);
        for (int v = 0; v < n; v++) { dv[v] += lv[v] * (1 - t); dn[v] += ln[v] * (1 - t); dt[v] += lt[v] * (1 - t); }
    }

    // A frame into arrays that may be longer than the mesh's vertices.
    private static void Read(Mesh mesh, int shape, int frame, Vector3[] dv, Vector3[] dn, Vector3[] dt)
    {
        int n = mesh.vertexCount;
        if (dv.Length == n) { mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt); return; }
        var v = new Vector3[n]; var nn = new Vector3[n]; var tt = new Vector3[n];
        mesh.GetBlendShapeFrameVertices(shape, frame, v, nn, tt);
        Array.Copy(v, dv, n); Array.Copy(nn, dn, n); Array.Copy(tt, dt, n);
    }

    // The listed vertices of source, every attribute it has, into target.
    private static void Copy(Mesh source, Mesh target, List<int> vertices)
    {
        target.SetVertices(Pick(source.vertices, vertices));
        if (source.normals.Length > 0) target.SetNormals(Pick(source.normals, vertices));
        if (source.tangents.Length > 0) target.SetTangents(Pick(source.tangents, vertices));
        if (source.colors.Length > 0) target.SetColors(Pick(source.colors, vertices));
        var buffer = new List<Vector4>();
        for (int channel = 0; channel < 8; channel++)
        {
            var attribute = VertexAttribute.TexCoord0 + channel;
            if (!source.HasVertexAttribute(attribute)) continue;
            source.GetUVs(channel, buffer);
            SetUVs(target, channel, source.GetVertexAttributeDimension(attribute), Pick(buffer, vertices));
        }
        var counts = source.GetBonesPerVertex();
        if (counts.Length == 0) return;
        var weights = source.GetAllBoneWeights();
        var starts = new int[counts.Length];
        for (int v = 1; v < counts.Length; v++) starts[v] = starts[v - 1] + counts[v - 1];
        var newCounts = new byte[vertices.Count];
        var newWeights = new List<BoneWeight1>();
        for (int i = 0; i < vertices.Count; i++)
        {
            int v = vertices[i];
            newCounts[i] = counts[v];
            for (int w = 0; w < counts[v]; w++) newWeights.Add(weights[starts[v] + w]);
        }
        using (var perVertex = new NativeArray<byte>(newCounts, Allocator.Temp))
        using (var all = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
            target.SetBoneWeights(perVertex, all);
    }

    // Both meshes' vertices one after the other; an attribute only one has gets defaults on the other's.
    private static void Append(Mesh first, Mesh second, Mesh target)
    {
        target.SetVertices(first.vertices.Concat(second.vertices).ToList());
        if (first.normals.Length > 0 || second.normals.Length > 0)
            target.SetNormals(Or(first.normals, first.vertexCount, Vector3.up).Concat(Or(second.normals, second.vertexCount, Vector3.up)).ToList());
        if (first.tangents.Length > 0 || second.tangents.Length > 0)
            target.SetTangents(Or(first.tangents, first.vertexCount, new Vector4(1, 0, 0, 1)).Concat(Or(second.tangents, second.vertexCount, new Vector4(1, 0, 0, 1))).ToList());
        if (first.colors.Length > 0 || second.colors.Length > 0)
            target.SetColors(Or(first.colors, first.vertexCount, Color.white).Concat(Or(second.colors, second.vertexCount, Color.white)).ToList());
        var a = new List<Vector4>(); var b = new List<Vector4>();
        for (int channel = 0; channel < 8; channel++)
        {
            var attribute = VertexAttribute.TexCoord0 + channel;
            int dimension = Math.Max(first.HasVertexAttribute(attribute) ? first.GetVertexAttributeDimension(attribute) : 0,
                                     second.HasVertexAttribute(attribute) ? second.GetVertexAttributeDimension(attribute) : 0);
            if (dimension == 0) continue;
            a.Clear(); b.Clear();
            if (first.HasVertexAttribute(attribute)) first.GetUVs(channel, a); else a.AddRange(Enumerable.Repeat(Vector4.zero, first.vertexCount));
            if (second.HasVertexAttribute(attribute)) second.GetUVs(channel, b); else b.AddRange(Enumerable.Repeat(Vector4.zero, second.vertexCount));
            SetUVs(target, channel, dimension, a.Concat(b).ToList());
        }
        // A mesh without weights joining one with them keeps its vertices weighted to no bone; the other keeps its own.
        var firstCounts = first.GetBonesPerVertex(); var secondCounts = second.GetBonesPerVertex();
        if (firstCounts.Length == 0 && secondCounts.Length == 0) return;
        var counts = (firstCounts.Length > 0 ? firstCounts.ToArray() : new byte[first.vertexCount])
            .Concat(secondCounts.Length > 0 ? secondCounts.ToArray() : new byte[second.vertexCount]).ToArray();
        if (counts.Length != first.vertexCount + second.vertexCount) return;
        using (var perVertex = new NativeArray<byte>(counts, Allocator.Temp))
        using (var all = new NativeArray<BoneWeight1>(first.GetAllBoneWeights().Concat(second.GetAllBoneWeights()).ToArray(), Allocator.Temp))
            target.SetBoneWeights(perVertex, all);
    }

    private static List<T> Pick<T>(IList<T> values, List<int> vertices)
    {
        var picked = new List<T>(vertices.Count);
        foreach (int v in vertices) picked.Add(values[v]);
        return picked;
    }

    private static IEnumerable<T> Or<T>(T[] values, int count, T fallback) => values.Length > 0 ? values : Enumerable.Repeat(fallback, count);

    private static void SetUVs(Mesh mesh, int channel, int dimension, List<Vector4> uvs)
    {
        if (dimension <= 2) mesh.SetUVs(channel, uvs.Select(u => (Vector2)u).ToList());
        else if (dimension == 3) mesh.SetUVs(channel, uvs.Select(u => (Vector3)u).ToList());
        else mesh.SetUVs(channel, uvs);
    }

    // MeshParts.ShapeSource.Apply as it was.
    public static void Apply(Mesh from, Mesh to, int[] corners, float[] weights, Func<int, bool> shape)
    {
        int count = corners.Length / 3;
        var dv = new Vector3[from.vertexCount]; var dn = new Vector3[from.vertexCount]; var dt = new Vector3[from.vertexCount];
        var mv = new Vector3[count]; var mn = new Vector3[count]; var mt = new Vector3[count];
        for (int index = 0; index < from.blendShapeCount; index++)
        {
            if (!shape(index)) continue;
            for (int frame = 0; frame < from.GetBlendShapeFrameCount(index); frame++)
            {
                from.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                for (int i = 0; i < count; i++)
                {
                    int a = corners[3 * i], b = corners[3 * i + 1], c = corners[3 * i + 2];
                    float wa = weights[3 * i], wb = weights[3 * i + 1], wc = weights[3 * i + 2];
                    mv[i] = dv[a] * wa + dv[b] * wb + dv[c] * wc;
                    mn[i] = dn[a] * wa + dn[b] * wb + dn[c] * wc;
                    mt[i] = dt[a] * wa + dt[b] * wb + dt[c] * wc;
                }
                to.AddBlendShapeFrame(from.GetBlendShapeName(index), from.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
            }
        }
    }

    // MeshMerge.Merge's blendshape loop as it was: each part's (mesh, offset into the merged mesh, its space, its renderer's name).
    public static List<Dictionary<string, string>> MergeShapes(Mesh merged, IReadOnlyList<(Mesh Mesh, int Offset, Matrix4x4 Space, string Name)> parts)
    {
        int vertices = merged.vertexCount;
        var result = new List<Dictionary<string, string>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var mv = new Vector3[vertices]; var mn = new Vector3[vertices]; var mt = new Vector3[vertices];
        foreach (var part in parts)
        {
            var shaped = part.Mesh;
            int count = shaped.vertexCount;
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            var dv = new Vector3[count]; var dn = new Vector3[count]; var dt = new Vector3[count];
            bool same = part.Space == Matrix4x4.identity;
            var normalScale = same ? null : Lengths(shaped.normals, count, part.Space);
            var tangentScale = same ? null : Lengths(shaped.tangents.Select(t => (Vector3)t).ToArray(), count, part.Space);
            for (int shape = 0; shape < shaped.blendShapeCount; shape++)
            {
                string name = shaped.GetBlendShapeName(shape), unique = name;
                for (int n = 2; names.Contains(unique); n++) unique = name + " " + part.Name + (n > 2 ? " " + n : "");
                names.Add(unique);
                kept[name] = unique;
                for (int frame = 0; frame < shaped.GetBlendShapeFrameCount(shape); frame++)
                {
                    shaped.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                    if (same) { Array.Copy(dv, 0, mv, part.Offset, count); Array.Copy(dn, 0, mn, part.Offset, count); Array.Copy(dt, 0, mt, part.Offset, count); }
                    else
                        for (int v = 0; v < count; v++)
                        {
                            mv[part.Offset + v] = part.Space.MultiplyVector(dv[v]);
                            mn[part.Offset + v] = part.Space.MultiplyVector(dn[v]) * normalScale[v];
                            mt[part.Offset + v] = part.Space.MultiplyVector(dt[v]) * tangentScale[v];
                        }
                    merged.AddBlendShapeFrame(unique, shaped.GetBlendShapeFrameWeight(shape, frame), mv, mn, mt);
                }
            }
            Array.Clear(mv, part.Offset, count); Array.Clear(mn, part.Offset, count); Array.Clear(mt, part.Offset, count);
            result.Add(kept);
        }
        return result;
    }

    private static float[] Lengths(Vector3[] directions, int count, Matrix4x4 space)
    {
        var scale = new float[count];
        for (int v = 0; v < count; v++)
        {
            float length = v < directions.Length ? space.MultiplyVector(directions[v]).magnitude : 0f;
            scale[v] = length > 1e-12f ? 1f / length : 1f;
        }
        return scale;
    }
}
