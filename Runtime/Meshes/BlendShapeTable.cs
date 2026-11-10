using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// A mesh's blendshapes read once and kept sparse: each frame holds only the vertices it moves (its offset, normal or
    /// tangent is not exactly zero), in ascending vertex order. Most shapes of a body move a small part of it, so a table is
    /// a fraction of the dense frames Unity's API hands out one at a time, and tools stop touching that API: they
    /// <see cref="Read"/> a table, evaluate it (<see cref="AddTo"/>), map it onto other vertices (<see cref="Remap"/>) and
    /// <see cref="WriteTo"/> a mesh. Tables are immutable once made, so one can be shared between threads and callers.
    /// </summary>
    public sealed class BlendShapeTable
    {
        /// <summary>One frame: the vertices it moves and by how much.</summary>
        public sealed class Frame
        {
            public readonly float Weight;
            private readonly int[] indices;
            private readonly Vector3[] deltas, normals, tangents;

            /// <param name="indices">Moved vertices, ascending and distinct.</param>
            /// <param name="normals">Null when the frame has no normal offsets; otherwise as long as <paramref name="indices"/>.</param>
            /// <param name="tangents">Null when the frame has no tangent offsets; otherwise as long as <paramref name="indices"/>.</param>
            public Frame(float weight, int[] indices, Vector3[] deltas, Vector3[] normals = null, Vector3[] tangents = null)
            {
                if (indices == null) throw new ArgumentNullException(nameof(indices));
                if (deltas == null || deltas.Length != indices.Length) throw new ArgumentException("One offset per index.", nameof(deltas));
                if (normals != null && normals.Length != indices.Length) throw new ArgumentException("One normal offset per index.", nameof(normals));
                if (tangents != null && tangents.Length != indices.Length) throw new ArgumentException("One tangent offset per index.", nameof(tangents));
                for (int i = 1; i < indices.Length; i++)
                    if (indices[i] <= indices[i - 1]) throw new ArgumentException("Indices must be ascending and distinct.", nameof(indices));
                Weight = weight;
                this.indices = indices;
                this.deltas = deltas;
                this.normals = normals;
                this.tangents = tangents;
            }

            /// <summary>How many vertices the frame moves.</summary>
            public int Count => indices.Length;
            public ReadOnlySpan<int> Indices => indices;
            public ReadOnlySpan<Vector3> Deltas => deltas;
            /// <summary>Empty when <see cref="HasNormals"/> is false.</summary>
            public ReadOnlySpan<Vector3> Normals => normals;
            /// <summary>Empty when <see cref="HasTangents"/> is false.</summary>
            public ReadOnlySpan<Vector3> Tangents => tangents;
            public bool HasNormals => normals != null;
            public bool HasTangents => tangents != null;

            /// <summary>A frame from dense per-vertex offsets (any of the normal and tangent arrays may be null).</summary>
            public static Frame FromDense(float weight, Vector3[] deltas, Vector3[] normals = null, Vector3[] tangents = null)
            {
                if (deltas == null) throw new ArgumentNullException(nameof(deltas));
                int n = deltas.Length;
                if (normals != null && normals.Length != n) throw new ArgumentException("One normal offset per vertex.", nameof(normals));
                if (tangents != null && tangents.Length != n) throw new ArgumentException("One tangent offset per vertex.", nameof(tangents));
                int count = 0;
                bool anyNormal = false, anyTangent = false;
                for (int v = 0; v < n; v++)
                {
                    bool d = !IsZero(deltas[v]);
                    bool m = normals != null && !IsZero(normals[v]);
                    bool t = tangents != null && !IsZero(tangents[v]);
                    if (d || m || t) count++;
                    anyNormal |= m;
                    anyTangent |= t;
                }
                var indices = new int[count];
                var sparseDeltas = new Vector3[count];
                var sparseNormals = anyNormal ? new Vector3[count] : null;
                var sparseTangents = anyTangent ? new Vector3[count] : null;
                for (int v = 0, k = 0; v < n && k < count; v++)
                {
                    bool moved = !IsZero(deltas[v]) || (normals != null && !IsZero(normals[v])) || (tangents != null && !IsZero(tangents[v]));
                    if (!moved) continue;
                    indices[k] = v;
                    sparseDeltas[k] = deltas[v];
                    if (anyNormal) sparseNormals[k] = normals[v];
                    if (anyTangent) sparseTangents[k] = tangents[v];
                    k++;
                }
                return new Frame(weight, indices, sparseDeltas, sparseNormals, sparseTangents);
            }

            /// <summary>Writes the frame into dense arrays (each may be null), zero where it moves nothing.</summary>
            public void Densify(Vector3[] denseDeltas, Vector3[] denseNormals = null, Vector3[] denseTangents = null)
            {
                if (denseDeltas != null) Array.Clear(denseDeltas, 0, denseDeltas.Length);
                if (denseNormals != null) Array.Clear(denseNormals, 0, denseNormals.Length);
                if (denseTangents != null) Array.Clear(denseTangents, 0, denseTangents.Length);
                Scatter(denseDeltas, denseNormals, denseTangents);
            }

            // Writes the moved vertices only (the rest of the arrays is left as it is).
            internal void Scatter(Vector3[] denseDeltas, Vector3[] denseNormals, Vector3[] denseTangents)
            {
                for (int k = 0; k < indices.Length; k++)
                {
                    int v = indices[k];
                    if (denseDeltas != null) denseDeltas[v] = deltas[k];
                    if (denseNormals != null && normals != null) denseNormals[v] = normals[k];
                    if (denseTangents != null && tangents != null) denseTangents[v] = tangents[k];
                }
            }

            // Zeroes what Scatter wrote: a frame's cost, not the mesh's.
            internal void Unscatter(Vector3[] denseDeltas, Vector3[] denseNormals, Vector3[] denseTangents)
            {
                for (int k = 0; k < indices.Length; k++)
                {
                    int v = indices[k];
                    if (denseDeltas != null) denseDeltas[v] = default;
                    if (denseNormals != null) denseNormals[v] = default;
                    if (denseTangents != null) denseTangents[v] = default;
                }
            }
        }

        /// <summary>A named blendshape and its frames, by ascending weight as Unity keeps them.</summary>
        public sealed class Shape
        {
            public readonly string Name;
            private readonly Frame[] frames;

            public Shape(string name, IReadOnlyList<Frame> frames)
            {
                Name = name ?? throw new ArgumentNullException(nameof(name));
                if (frames == null) throw new ArgumentNullException(nameof(frames));
                this.frames = new Frame[frames.Count];
                for (int i = 0; i < this.frames.Length; i++) this.frames[i] = frames[i] ?? throw new ArgumentException("A frame is missing.", nameof(frames));
            }

            public IReadOnlyList<Frame> Frames => frames;
            public int FrameCount => frames.Length;
        }

        /// <summary>Which offsets <see cref="Read"/> keeps.</summary>
        [Flags]
        public enum Parts
        {
            Deltas = 0,
            Normals = 1,
            Tangents = 2,
            All = Normals | Tangents
        }

        private static readonly ProfilerMarker ReadMarker = new ProfilerMarker("BlendShapeTable.Read");
        private static readonly ProfilerMarker WriteMarker = new ProfilerMarker("BlendShapeTable.WriteTo");

        private readonly Shape[] shapes;
        private Dictionary<string, int> byName;

        public BlendShapeTable(int vertexCount, IReadOnlyList<Shape> shapes)
        {
            if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
            if (shapes == null) throw new ArgumentNullException(nameof(shapes));
            VertexCount = vertexCount;
            this.shapes = new Shape[shapes.Count];
            for (int s = 0; s < this.shapes.Length; s++)
            {
                var shape = shapes[s] ?? throw new ArgumentException("A shape is missing.", nameof(shapes));
                foreach (var frame in shape.Frames)
                    if (frame.Count > 0 && frame.Indices[frame.Count - 1] >= vertexCount)
                        throw new ArgumentException(shape.Name + " moves a vertex past the table's " + vertexCount + ".", nameof(shapes));
                this.shapes[s] = shape;
            }
        }

        /// <summary>The vertex count of the mesh the table belongs to.</summary>
        public int VertexCount { get; }
        public IReadOnlyList<Shape> Shapes => shapes;
        public int ShapeCount => shapes.Length;

        /// <summary>Whether any frame has normal (tangent) offsets.</summary>
        public bool HasNormals => Any(f => f.HasNormals);
        public bool HasTangents => Any(f => f.HasTangents);

        /// <summary>The moved-vertex entries of every frame together: the table's size.</summary>
        public long Entries
        {
            get
            {
                long total = 0;
                foreach (var shape in shapes) foreach (var frame in shape.Frames) total += frame.Count;
                return total;
            }
        }

        /// <summary>The shape called <paramref name="name"/>, or -1 (Unity's names are unique per mesh).</summary>
        public int IndexOf(string name)
        {
            if (name == null) return -1;
            if (byName == null)
            {
                var map = new Dictionary<string, int>(shapes.Length);
                for (int s = shapes.Length - 1; s >= 0; s--) map[shapes[s].Name] = s;
                byName = map;
            }
            return byName.TryGetValue(name, out int index) ? index : -1;
        }

        private bool Any(Func<Frame, bool> test)
        {
            foreach (var shape in shapes) foreach (var frame in shape.Frames) if (test(frame)) return true;
            return false;
        }

        /// <summary>Reads every blendshape of <paramref name="mesh"/> (main thread: Unity's mesh API), one dense frame at a time.</summary>
        public static BlendShapeTable Read(Mesh mesh, Parts parts = Parts.All)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            using (ReadMarker.Auto())
            {
                int n = mesh.vertexCount;
                int count = mesh.blendShapeCount;
                var result = new Shape[count];
                if (count == 0) return new BlendShapeTable(n, result);
                var dv = new Vector3[n];
                var dn = (parts & Parts.Normals) != 0 ? new Vector3[n] : null;
                var dt = (parts & Parts.Tangents) != 0 ? new Vector3[n] : null;
                for (int s = 0; s < count; s++)
                {
                    int frameCount = mesh.GetBlendShapeFrameCount(s);
                    var frames = new Frame[frameCount];
                    for (int f = 0; f < frameCount; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                        frames[f] = Frame.FromDense(mesh.GetBlendShapeFrameWeight(s, f), dv, dn, dt);
                    }
                    result[s] = new Shape(mesh.GetBlendShapeName(s), frames);
                }
                return new BlendShapeTable(n, result);
            }
        }

#if UNITY_EDITOR
        private sealed class CacheEntry
        {
            public int DirtyCount, VertexCount, ShapeCount;
            public Parts Parts;
            public BlendShapeTable Table;
        }

        private static readonly ConditionalWeakTable<Mesh, CacheEntry> cache = new ConditionalWeakTable<Mesh, CacheEntry>();

        /// <summary>
        /// <see cref="Read"/>, kept with the mesh until its dirty count, vertex count or shape count changes. For meshes whose
        /// shapes only change through edits that mark them dirty (assets, imported models); a mesh a tool rebuilds in memory
        /// (ClearBlendShapes and the same number of shapes again) must be <see cref="Read"/> instead.
        /// </summary>
        public static BlendShapeTable Cached(Mesh mesh, Parts parts = Parts.All)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            int dirty = UnityEditor.EditorUtility.GetDirtyCount(mesh);
            lock (cache)
            {
                if (cache.TryGetValue(mesh, out var entry) && entry.DirtyCount == dirty && entry.VertexCount == mesh.vertexCount
                    && entry.ShapeCount == mesh.blendShapeCount && (entry.Parts & parts) == parts)
                    return entry.Table;
            }
            var table = Read(mesh, parts);
            lock (cache)
            {
                cache.Remove(mesh);
                cache.Add(mesh, new CacheEntry { DirtyCount = dirty, VertexCount = table.VertexCount, ShapeCount = table.ShapeCount, Parts = parts, Table = table });
            }
            return table;
        }
#endif

        /// <summary>
        /// Adds shape <paramref name="shape"/> at renderer weight <paramref name="weight"/> (100 = full) to
        /// <paramref name="vertices"/> and, when given, <paramref name="normals"/>, exactly as
        /// <see cref="BlendShapeEvaluation.Add"/> does from the mesh (the same frames, scales and interpolation), at the
        /// cost of the vertices the frames move.
        /// </summary>
        public void AddTo(int shape, float weight, bool clamp, Vector3[] vertices, Vector3[] normals = null)
        {
            var frames = shapes[shape].Frames;
            int frameCount = frames.Count;
            if (frameCount == 0) return;
            Span<float> frameWeights = frameCount <= 32 ? stackalloc float[frameCount] : new float[frameCount];
            for (int f = 0; f < frameCount; f++) frameWeights[f] = frames[f].Weight;
            if (!BlendShapeEvaluation.Resolve(frameWeights, weight, clamp, out var blend)) return;
            if (blend.Upper < 0)
            {
                AddScaled(frames[blend.Lower], blend.Scale, vertices, normals);
                return;
            }
            AddLerp(frames[blend.Lower], frames[blend.Upper], blend.T, vertices, normals);
        }

        /// <summary>Adds frame <paramref name="frame"/> of <paramref name="shape"/> times <paramref name="scale"/>.</summary>
        public void AddFrame(int shape, int frame, float scale, Vector3[] vertices, Vector3[] normals = null) =>
            AddScaled(shapes[shape].Frames[frame], scale, vertices, normals);

        private static void AddScaled(Frame frame, float scale, Vector3[] vertices, Vector3[] normals)
        {
            var indices = frame.Indices;
            var deltas = frame.Deltas;
            var frameNormals = frame.Normals;
            bool withNormals = normals != null && frame.HasNormals;
            for (int k = 0; k < indices.Length; k++)
            {
                int v = indices[k];
                vertices[v] += deltas[k] * scale;
                if (withNormals) normals[v] += frameNormals[k] * scale;
            }
        }

        // Vector3.Lerp of two frames (a vertex one of them leaves out counts as zero there), merged over their sorted indices.
        private static void AddLerp(Frame a, Frame b, float t, Vector3[] vertices, Vector3[] normals)
        {
            ReadOnlySpan<int> ia = a.Indices, ib = b.Indices;
            ReadOnlySpan<Vector3> da = a.Deltas, db = b.Deltas, na = a.Normals, nb = b.Normals;
            bool withNormals = normals != null;
            int i = 0, j = 0;
            while (i < ia.Length || j < ib.Length)
            {
                int va = i < ia.Length ? ia[i] : int.MaxValue;
                int vb = j < ib.Length ? ib[j] : int.MaxValue;
                int v = va < vb ? va : vb;
                Vector3 pa = default, pb = default, ma = default, mb = default;
                if (va == v) { pa = da[i]; if (na.Length > 0) ma = na[i]; i++; }
                if (vb == v) { pb = db[j]; if (nb.Length > 0) mb = nb[j]; j++; }
                vertices[v] += Vector3.Lerp(pa, pb, t);
                if (withNormals) normals[v] += Vector3.Lerp(ma, mb, t);
            }
        }

        /// <summary>Writes one frame into dense arrays (each may be null), zero where it moves nothing.</summary>
        public void Densify(int shape, int frame, Vector3[] deltas, Vector3[] normals = null, Vector3[] tangents = null) =>
            shapes[shape].Frames[frame].Densify(deltas, normals, tangents);

        /// <summary>
        /// The table on other vertices: vertex <c>v</c> becomes <c>oldToNew[v]</c>, or is dropped where that is negative. When
        /// several vertices land on one, the lowest of them keeps its offsets (welded vertices move alike anyway).
        /// </summary>
        public BlendShapeTable Remap(int[] oldToNew, int newVertexCount)
        {
            if (oldToNew == null) throw new ArgumentNullException(nameof(oldToNew));
            if (oldToNew.Length != VertexCount) throw new ArgumentException("One entry per vertex of the table.", nameof(oldToNew));
            // Each new vertex's source, decided once for every frame: the lowest old vertex mapped onto it, whether a frame
            // moves it or not (a frame that leaves it still leaves the new vertex still).
            var sourceOf = new int[newVertexCount];
            for (int v = 0; v < newVertexCount; v++) sourceOf[v] = -1;
            for (int v = 0; v < oldToNew.Length; v++)
            {
                int to = oldToNew[v];
                if (to < 0) continue;
                if (to >= newVertexCount) throw new ArgumentException("A vertex maps past " + newVertexCount + ".", nameof(oldToNew));
                if (sourceOf[to] < 0) sourceOf[to] = v;
            }
            var result = new Shape[shapes.Length];
            var keys = new List<long>();
            for (int s = 0; s < shapes.Length; s++)
            {
                var source = shapes[s];
                var frames = new Frame[source.FrameCount];
                for (int f = 0; f < frames.Length; f++)
                {
                    var frame = source.Frames[f];
                    var indices = frame.Indices;
                    keys.Clear();
                    bool sorted = true;
                    int last = -1;
                    for (int k = 0; k < indices.Length; k++)
                    {
                        int to = oldToNew[indices[k]];
                        if (to < 0 || sourceOf[to] != indices[k]) continue;
                        if (to <= last) sorted = false;
                        last = to;
                        keys.Add((long)to << 32 | (uint)k);
                    }
                    if (!sorted) keys.Sort();
                    int count = keys.Count;
                    var newIndices = new int[count];
                    var deltas = new Vector3[count];
                    var normals = frame.HasNormals ? new Vector3[count] : null;
                    var tangents = frame.HasTangents ? new Vector3[count] : null;
                    ReadOnlySpan<Vector3> sd = frame.Deltas, sn = frame.Normals, st = frame.Tangents;
                    for (int o = 0; o < count; o++)
                    {
                        int from = (int)(keys[o] & 0xFFFFFFFF);
                        newIndices[o] = (int)(keys[o] >> 32);
                        deltas[o] = sd[from];
                        if (normals != null) normals[o] = sn[from];
                        if (tangents != null) tangents[o] = st[from];
                    }
                    frames[f] = new Frame(frame.Weight, newIndices, deltas, normals, tangents);
                }
                result[s] = new Shape(source.Name, frames);
            }
            return new BlendShapeTable(newVertexCount, result);
        }

        /// <summary>
        /// Per vertex, a signature of what the shapes (<paramref name="shapes"/>, or all) move it by, from the offsets alone:
        /// two vertices that every frame moves exactly alike have equal signatures (and others almost never do). Zero for a
        /// vertex nothing moves.
        /// </summary>
        public ulong[] MotionSignatures(IReadOnlyList<int> shapes = null)
        {
            var result = new ulong[VertexCount];
            int count = shapes?.Count ?? this.shapes.Length;
            for (int i = 0; i < count; i++)
            {
                int s = shapes != null ? shapes[i] : i;
                var frames = this.shapes[s].Frames;
                for (int f = 0; f < frames.Count; f++)
                {
                    ulong salt = Mix((ulong)s << 16 | (uint)f);
                    var indices = frames[f].Indices;
                    var deltas = frames[f].Deltas;
                    for (int k = 0; k < indices.Length; k++)
                    {
                        if (IsZero(deltas[k])) continue;
                        int v = indices[k];
                        result[v] = Mix(result[v] ^ salt ^ Hash(deltas[k]));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Replaces every blendshape of <paramref name="mesh"/> with the table's (main thread). The one place tools write
        /// shapes: Unity takes a frame only as dense arrays, and each AddBlendShapeFrame costs more for every frame before
        /// it, so the dense arrays are made once and only the entries a frame moved are written and cleared again; offsets
        /// the table has none of (no normals, no tangents anywhere) are not passed at all.
        /// </summary>
        public void WriteTo(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (mesh.vertexCount != VertexCount)
                throw new ArgumentException("The mesh has " + mesh.vertexCount + " vertices, the table " + VertexCount + ".", nameof(mesh));
            using (WriteMarker.Auto())
            {
                mesh.ClearBlendShapes();
                if (shapes.Length == 0) return;
                var dv = new Vector3[VertexCount];
                var dn = HasNormals ? new Vector3[VertexCount] : null;
                var dt = HasTangents ? new Vector3[VertexCount] : null;
                foreach (var shape in shapes)
                    foreach (var frame in shape.Frames)
                    {
                        frame.Scatter(dv, dn, dt);
                        mesh.AddBlendShapeFrame(shape.Name, frame.Weight, dv, dn, dt);
                        frame.Unscatter(dv, dn, dt);
                    }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsZero(Vector3 v) => v.x == 0f && v.y == 0f && v.z == 0f;

        private static ulong Mix(ulong x)
        {
            x += 0x9e3779b97f4a7c15UL;
            x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }

        private static ulong Hash(Vector3 v) => Mix(Mix(Mix((uint)v.x.GetHashCode()) ^ (uint)v.y.GetHashCode()) ^ (uint)v.z.GetHashCode());
    }
}
