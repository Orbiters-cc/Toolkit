using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// The blendshapes of the meshes the build's mesh passes read and write, as sparse tables (<see cref="BlendShapeTable"/>):
    /// each pass reads a mesh's shapes once (none again for a mesh a pass before wrote, while it holds what was written) and
    /// writes the shapes of each mesh it makes once, through the table's own write.
    /// </summary>
    internal static class MeshShapes
    {
        // What a pass wrote into a mesh, and the shape of what it wrote: a mesh changed since (a shape added or cleared, its
        // vertex count, a shape's name, frame count or weights) is read again. Unity changes a frame's offsets only by
        // clearing the shapes and adding them again, which a later pass does on a copy of its own (BlendShapePruning), or in
        // place by adding a frame (MergeToggled's collapse shapes): both show in the counts or the names.
        private sealed class Written
        {
            public BlendShapeTable Table;
            public int VertexCount;
            public string[] Names;
            public float[][] Weights;
        }

        private static readonly ConditionalWeakTable<Mesh, Written> Tables = new ConditionalWeakTable<Mesh, Written>();

        /// <summary>
        /// The blendshapes of <paramref name="mesh"/> (main thread). A mesh a pass wrote with <see cref="Write"/> answers with what
        /// was written while its shapes are as written; an asset is read once until it changes (<see cref="BlendShapeTable.Cached"/>);
        /// any other in-memory mesh is read now.
        /// </summary>
        public static BlendShapeTable Of(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (Tables.TryGetValue(mesh, out var written) && Holds(mesh, written)) return written.Table;
            if (mesh.blendShapeCount == 0) return new BlendShapeTable(mesh.vertexCount, Array.Empty<BlendShapeTable.Shape>());
            // An asset changes through edits that mark it dirty; a mesh in memory may be rebuilt without (Cached's own rule).
            return EditorUtility.IsPersistent(mesh) ? BlendShapeTable.Cached(mesh) : BlendShapeTable.Read(mesh);
        }

        /// <summary>Replaces the blendshapes of <paramref name="mesh"/> with <paramref name="table"/>'s, once, and remembers them.</summary>
        public static void Write(Mesh mesh, BlendShapeTable table)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (table == null) throw new ArgumentNullException(nameof(table));
            table.WriteTo(mesh);
            Remember(mesh, table);
        }

        /// <summary>Remembers that <paramref name="mesh"/> holds <paramref name="table"/>'s blendshapes (written by its own code).</summary>
        public static void Remember(Mesh mesh, BlendShapeTable table)
        {
            var shapes = table.Shapes;
            var written = new Written { Table = table, VertexCount = table.VertexCount, Names = new string[shapes.Count], Weights = new float[shapes.Count][] };
            for (int s = 0; s < shapes.Count; s++)
            {
                written.Names[s] = shapes[s].Name;
                var weights = written.Weights[s] = new float[shapes[s].FrameCount];
                for (int f = 0; f < weights.Length; f++) weights[f] = shapes[s].Frames[f].Weight;
            }
            Tables.Remove(mesh);
            Tables.Add(mesh, written);
        }

        // The mesh's shapes have the layout written: same vertices, names, frame counts and weights.
        private static bool Holds(Mesh mesh, Written written)
        {
            if (mesh.vertexCount != written.VertexCount || mesh.blendShapeCount != written.Names.Length) return false;
            for (int s = 0; s < written.Names.Length; s++)
            {
                if (mesh.GetBlendShapeName(s) != written.Names[s]) return false;
                var weights = written.Weights[s];
                if (mesh.GetBlendShapeFrameCount(s) != weights.Length) return false;
                for (int f = 0; f < weights.Length; f++)
                    if (!mesh.GetBlendShapeFrameWeight(s, f).Equals(weights[f])) return false;
            }
            return true;
        }

        /// <summary>The shapes of <paramref name="table"/> that <paramref name="pick"/> picks (by index), in their order.</summary>
        public static BlendShapeTable Picked(BlendShapeTable table, Func<int, bool> pick)
        {
            var shapes = new List<BlendShapeTable.Shape>(table.ShapeCount);
            for (int s = 0; s < table.ShapeCount; s++) if (pick(s)) shapes.Add(table.Shapes[s]);
            return shapes.Count == table.ShapeCount ? table : new BlendShapeTable(table.VertexCount, shapes);
        }

        /// <summary>
        /// A frame of shape <paramref name="shape"/> at <paramref name="weight"/> as Unity blends it, exactly as
        /// <see cref="MeshParts.ShapeAt"/> computes it from the dense frames (upper frame times t, plus the lower times 1 − t),
        /// on the vertices either frame moves, each shifted by <paramref name="offset"/>.
        /// </summary>
        public static void AddShapeAt(BlendShapeTable table, int shape, float weight, int offset, FrameBuilder into)
        {
            var frames = table.Shapes[shape].Frames;
            int count = frames.Count;
            int upper = 0;
            while (upper < count - 1 && frames[upper].Weight < weight) upper++;
            float upperWeight = frames[upper].Weight;
            float lowerWeight = upper == 0 ? 0 : frames[upper - 1].Weight;
            float t = upperWeight == lowerWeight ? 1 : (weight - lowerWeight) / (upperWeight - lowerWeight);
            var high = frames[upper];
            if (upper == 0 || t == 1)
            {
                into.AddScaled(high, t, offset);
                return;
            }
            into.AddBlend(high, t, frames[upper - 1], 1 - t, offset);
        }

        /// <summary>
        /// One frame of a mesh being put together, from parts that each fill their own range of vertices (added in ascending
        /// order of their ranges). Offsets keep the arithmetic of the dense code they replace, value for value.
        /// </summary>
        public sealed class FrameBuilder
        {
            private readonly List<int> indices = new List<int>();
            private readonly List<Vector3> deltas = new List<Vector3>(), normals = new List<Vector3>(), tangents = new List<Vector3>();
            private bool anyNormal, anyTangent;

            public int Count => indices.Count;

            public void Clear()
            {
                indices.Clear(); deltas.Clear(); normals.Clear(); tangents.Clear();
                anyNormal = anyTangent = false;
            }

            /// <summary>One vertex's offsets (vertices added in ascending order).</summary>
            public void Add(int vertex, Vector3 delta, Vector3 normal, Vector3 tangent, bool hasNormal, bool hasTangent)
            {
                indices.Add(vertex); deltas.Add(delta); normals.Add(normal); tangents.Add(tangent);
                anyNormal |= hasNormal;
                anyTangent |= hasTangent;
            }

            /// <summary>Every vertex <paramref name="frame"/> moves, its offsets as they are.</summary>
            public void AddCopy(BlendShapeTable.Frame frame, int offset)
            {
                var ix = frame.Indices; var d = frame.Deltas; var n = frame.Normals; var g = frame.Tangents;
                bool hn = frame.HasNormals, ht = frame.HasTangents;
                for (int k = 0; k < ix.Length; k++) Add(ix[k] + offset, d[k], hn ? n[k] : Vector3.zero, ht ? g[k] : Vector3.zero, hn, ht);
            }

            /// <summary>Every vertex <paramref name="frame"/> moves, times <paramref name="scale"/> (dense: <c>d *= t</c>).</summary>
            public void AddScaled(BlendShapeTable.Frame frame, float scale, int offset)
            {
                var ix = frame.Indices; var d = frame.Deltas; var n = frame.Normals; var g = frame.Tangents;
                bool hn = frame.HasNormals, ht = frame.HasTangents;
                for (int k = 0; k < ix.Length; k++)
                    Add(ix[k] + offset, d[k] * scale, hn ? n[k] * scale : Vector3.zero, ht ? g[k] * scale : Vector3.zero, hn, ht);
            }

            /// <summary>
            /// <c>a * ta + b * tb</c> per vertex either frame moves (one that leaves a vertex counts as zero there), merged
            /// over their sorted indices: the dense <c>d *= t; d += lower * (1 − t)</c>.
            /// </summary>
            public void AddBlend(BlendShapeTable.Frame a, float ta, BlendShapeTable.Frame b, float tb, int offset)
            {
                ReadOnlySpan<int> ia = a.Indices, ib = b.Indices;
                ReadOnlySpan<Vector3> da = a.Deltas, db = b.Deltas, na = a.Normals, nb = b.Normals, ga = a.Tangents, gb = b.Tangents;
                int i = 0, j = 0;
                while (i < ia.Length || j < ib.Length)
                {
                    int va = i < ia.Length ? ia[i] : int.MaxValue;
                    int vb = j < ib.Length ? ib[j] : int.MaxValue;
                    int v = va < vb ? va : vb;
                    Vector3 pa = default, pb = default, ma = default, mb = default, sa = default, sb = default;
                    if (va == v) { pa = da[i]; if (na.Length > 0) ma = na[i]; if (ga.Length > 0) sa = ga[i]; i++; }
                    if (vb == v) { pb = db[j]; if (nb.Length > 0) mb = nb[j]; if (gb.Length > 0) sb = gb[j]; j++; }
                    var delta = pa * ta; delta += pb * tb;
                    var normal = ma * ta; normal += mb * tb;
                    var tangent = sa * ta; tangent += sb * tb;
                    Add(v + offset, delta, normal, tangent, a.HasNormals || b.HasNormals, a.HasTangents || b.HasTangents);
                }
            }

            /// <summary>The frame at <paramref name="weight"/> (normal and tangent offsets only when a part had some).</summary>
            public BlendShapeTable.Frame Build(float weight) => new BlendShapeTable.Frame(weight, indices.ToArray(), deltas.ToArray(),
                anyNormal ? normals.ToArray() : null, anyTangent ? tangents.ToArray() : null);
        }
    }
}
