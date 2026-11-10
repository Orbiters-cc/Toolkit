using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// Bounding volume hierarchy over a mesh's triangles for brush and fitting queries: the triangles or vertices inside a
    /// sphere, the first triangle along a ray, the closest surface point, one at a time or in batches. A flat tree
    /// (binned SAH build, 32-byte nodes, leaves of four triangles whose corners sit in leaf order), built, refitted and
    /// queried by Burst code (<see cref="BvhKernels"/>) in managed arrays, so nothing has to be disposed.
    /// The vertex array passed to Build is kept by reference: refit after changing it in place (queries read the corners
    /// as of the last build or refit). Thread-safe for queries between refits. Ties are deterministic: among equally near
    /// hits the lowest triangle index wins, whatever the tree's layout.
    /// </summary>
    public sealed class MeshBvh
    {
        public struct Hit
        {
            public bool Found;
            public int Triangle;
            public Vector3 Position;
            public Vector3 Bary;
            public float Distance;
        }

        private BvhNode[] nodes;
        private int nodeCount;
        private int[] parents;
        private BvhTriangle[] corners;
        private int[] triangleOfSlot;
        private int3[] slotVertices;
        private int[] leafOfTriangle;
        private Vector3[] vertices;
        private int[] triangles;

        /// <summary>
        /// The vertices went through a mirroring transform (a negative scale: its determinant is negative), so the mesh's
        /// front faces wind the other way round in them: raycasts without back faces and <see cref="FaceNormal"/> follow.
        /// </summary>
        public bool Mirrored { get; set; }

        public int TriangleCount => triangles.Length / 3;
        public Vector3[] Vertices => vertices;
        public int[] Triangles => triangles;

        /// <summary>
        /// Builds the tree over every triangle of <paramref name="triangles"/>, or only those <paramref name="include"/>
        /// marks (one flag per triangle; queries then never return the others). Any thread.
        /// </summary>
        public static unsafe MeshBvh Build(Vector3[] vertices, int[] triangles, bool[] include = null)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            int triangleCount = triangles.Length / 3;
            if (include != null && include.Length != triangleCount) throw new ArgumentException("One flag per triangle.", nameof(include));
            // Burst reads through pointers: every index is checked here once.
            for (int i = 0; i < triangleCount * 3; i++)
                if ((uint)triangles[i] >= (uint)vertices.Length)
                    throw new ArgumentException("Triangle " + i / 3 + " uses vertex " + triangles[i] + " of " + vertices.Length + ".", nameof(triangles));
            int count = 0;
            for (int t = 0; t < triangleCount; t++) if (include == null || include[t]) count++;

            var bvh = new MeshBvh { vertices = vertices, triangles = triangles };
            bvh.corners = new BvhTriangle[count];
            bvh.triangleOfSlot = new int[count];
            bvh.leafOfTriangle = new int[triangleCount];
            for (int t = 0, s = 0; t < triangleCount; t++)
            {
                bvh.leafOfTriangle[t] = -1;
                if (include != null && !include[t]) continue;
                bvh.triangleOfSlot[s] = t;
                bvh.corners[s] = new BvhTriangle { A = vertices[triangles[t * 3]], B = vertices[triangles[t * 3 + 1]], C = vertices[triangles[t * 3 + 2]] };
                s++;
            }
            int capacity = Math.Max(1, 2 * count - 1);
            var nodes = new BvhNode[capacity];
            var parents = new int[capacity];
            int made;
            fixed (BvhTriangle* c = bvh.corners)
            fixed (int* order = bvh.triangleOfSlot)
            fixed (BvhNode* n = nodes)
            fixed (int* p = parents)
                BvhKernels.Build(c, order, count, n, p, &made);
            if (made < capacity)
            {
                Array.Resize(ref nodes, Math.Max(1, made));
                Array.Resize(ref parents, Math.Max(1, made));
            }
            bvh.nodes = nodes;
            bvh.parents = parents;
            bvh.nodeCount = made;
            bvh.slotVertices = new int3[count];
            for (int s = 0; s < count; s++)
            {
                int t = bvh.triangleOfSlot[s];
                bvh.slotVertices[s] = new int3(triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2]);
            }
            for (int i = 0; i < made; i++)
            {
                ref var node = ref nodes[i];
                if (node.Count == 0) continue;
                for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++) bvh.leafOfTriangle[bvh.triangleOfSlot[s]] = i;
            }
            return bvh;
        }

        /// <summary>Recomputes every node's bounds from the current vertex positions (same topology).</summary>
        public unsafe void Refit()
        {
            RefuseWhilePinned();
            if (nodeCount == 0) return;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (Vector3* v = vertices)
            fixed (int3* sv = slotVertices)
                BvhKernels.Refit(n, nodeCount, c, (float3*)v, sv, corners.Length);
        }

        /// <summary>Refits only the leaves holding <paramref name="movedTriangles"/> and their ancestors.</summary>
        public void RefitTriangles(IReadOnlyList<int> movedTriangles)
        {
            RefuseWhilePinned();
            if (movedTriangles == null || movedTriangles.Count == 0 || nodeCount == 0) return;
            NextStamp();
            dirtyLeaves.Clear();
            for (int i = 0; i < movedTriangles.Count; i++) MarkLeaf(leafOfTriangle[movedTriangles[i]]);
            RefitMarked();
        }

        /// <summary>Refits the leaves holding the triangles around <paramref name="movedVertices"/>.</summary>
        public void RefitVertices(IReadOnlyList<int> movedVertices, MeshNeighbors neighbors)
        {
            RefuseWhilePinned();
            if (movedVertices == null || movedVertices.Count == 0 || nodeCount == 0) return;
            NextStamp();
            dirtyLeaves.Clear();
            for (int i = 0; i < movedVertices.Count; i++)
            {
                var around = neighbors.TrianglesAround(neighbors.Weld[movedVertices[i]]);
                for (int k = 0; k < around.Length; k++)
                {
                    int t = around[k];
                    if (triangleStamp[t] == stamp) continue;
                    triangleStamp[t] = stamp;
                    MarkLeaf(leafOfTriangle[t]);
                }
            }
            RefitMarked();
        }

        // Refits mark what they visit with a stamp instead of filling sets: a stroke refits thousands of triangles a step.
        private int[] triangleStamp, nodeStamp;
        private int stamp;
        private int[] dirtyLeafBuffer = Array.Empty<int>();
        private readonly List<int> dirtyLeaves = new List<int>();

        private void NextStamp()
        {
            if (nodeStamp == null || nodeStamp.Length < nodeCount) nodeStamp = new int[nodeCount];
            if (triangleStamp == null || triangleStamp.Length != leafOfTriangle.Length) triangleStamp = new int[leafOfTriangle.Length];
            if (++stamp == int.MaxValue)
            {
                Array.Clear(nodeStamp, 0, nodeStamp.Length);
                Array.Clear(triangleStamp, 0, triangleStamp.Length);
                stamp = 1;
            }
        }

        private void MarkLeaf(int leaf)
        {
            // A triangle left out of the tree (Build's include) has no leaf.
            if (leaf < 0 || nodeStamp[leaf] == stamp) return;
            nodeStamp[leaf] = stamp;
            dirtyLeaves.Add(leaf);
        }

        private unsafe void RefitMarked()
        {
            int leaves = dirtyLeaves.Count;
            if (leaves == 0) return;
            // A large part of the tree: one pass over every node is cheaper than walking up from each leaf.
            if (leaves * 4 > nodeCount) { Refit(); return; }
            if (dirtyLeafBuffer.Length < leaves) dirtyLeafBuffer = new int[Math.Max(leaves, dirtyLeafBuffer.Length * 2)];
            dirtyLeaves.CopyTo(dirtyLeafBuffer);
            fixed (BvhNode* n = nodes)
            fixed (int* p = parents)
            fixed (BvhTriangle* c = corners)
            fixed (Vector3* v = vertices)
            fixed (int3* sv = slotVertices)
            fixed (int* l = dirtyLeafBuffer)
                BvhKernels.RefitLeaves(n, p, c, (float3*)v, sv, l, leaves);
        }

        /// <summary>
        /// The tree's memory pinned for Burst jobs and other kernels (<see cref="BvhKernels"/>), until the lease is disposed.
        /// The lease owns the pins: hand <see cref="PinnedBvh.View"/> only to work that ends before it is disposed, and register
        /// every job that reads it with <see cref="PinnedBvh.DependsOn"/> (disposing completes them first). While any lease
        /// is out the tree refuses refits.
        /// </summary>
        public PinnedBvh Pin() => new PinnedBvh(this);

        private int leases;

        private void RefuseWhilePinned()
        {
            if (System.Threading.Volatile.Read(ref leases) > 0) throw new InvalidOperationException("The BVH is pinned for queries: dispose its leases before refitting.");
        }

        /// <summary>A pin of a <see cref="MeshBvh"/>'s arrays (see <see cref="Pin"/>). Disposing it more than once does nothing.</summary>
        public sealed class PinnedBvh : IDisposable
        {
            private readonly MeshBvh bvh;
            private GCHandle nodes, corners, triangles;
            private readonly BvhView view;
            private Unity.Jobs.JobHandle jobs;
            private bool hasJobs;
            private int disposed;

            internal unsafe PinnedBvh(MeshBvh bvh)
            {
                this.bvh = bvh;
                nodes = GCHandle.Alloc(bvh.nodes, GCHandleType.Pinned);
                corners = GCHandle.Alloc(bvh.corners, GCHandleType.Pinned);
                triangles = GCHandle.Alloc(bvh.triangleOfSlot, GCHandleType.Pinned);
                view = new BvhView
                {
                    Nodes = (BvhNode*)nodes.AddrOfPinnedObject(),
                    Corners = (BvhTriangle*)corners.AddrOfPinnedObject(),
                    TriangleOfSlot = (int*)triangles.AddrOfPinnedObject(),
                    NodeCount = bvh.nodeCount,
                    Mirrored = bvh.Mirrored ? 1 : 0
                };
                System.Threading.Interlocked.Increment(ref bvh.leases);
            }

            /// <summary>The pinned memory; refused once the lease is disposed.</summary>
            public BvhView View
            {
                get
                {
                    if (System.Threading.Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(PinnedBvh));
                    return view;
                }
            }

            /// <summary>A job that reads <see cref="View"/>: disposing the lease completes it before unpinning (main thread).</summary>
            public void DependsOn(Unity.Jobs.JobHandle job)
            {
                if (System.Threading.Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(PinnedBvh));
                jobs = Unity.Jobs.JobHandle.CombineDependencies(jobs, job);
                hasJobs = true;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
                // Every job that may still read the memory finishes before it can move.
                if (hasJobs) jobs.Complete();
                nodes.Free();
                corners.Free();
                triangles.Free();
                System.Threading.Interlocked.Decrement(ref bvh.leases);
            }
        }

        private unsafe BvhView ViewOf(BvhNode* n, BvhTriangle* c, int* t) =>
            new BvhView { Nodes = n, Corners = c, TriangleOfSlot = t, NodeCount = nodeCount, Mirrored = Mirrored ? 1 : 0 };

        /// <summary>The triangles whose bounds overlap the sphere (a superset of those really inside).</summary>
        public void TrianglesNear(Vector3 center, float radius, List<int> result)
        {
            result.Clear();
            if (nodeCount == 0) return;
            float3 c = center;
            float r2 = radius * radius;
            Span<int> stack = stackalloc int[BvhKernels.StackSize];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                ref var node = ref nodes[stack[--top]];
                if (BvhKernels.BoxDistance2(c, node.Min, node.Max) > r2) continue;
                if (node.Count > 0)
                {
                    // Each triangle's own box, not only its leaf's.
                    for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++)
                    {
                        ref var t = ref corners[s];
                        if (BvhKernels.BoxDistance2(c, math.min(t.A, math.min(t.B, t.C)), math.max(t.A, math.max(t.B, t.C))) <= r2) result.Add(triangleOfSlot[s]);
                    }
                }
                else { stack[top++] = node.LeftOrFirst; stack[top++] = node.LeftOrFirst + 1; }
            }
        }

        /// <summary>The mesh vertices within <paramref name="radius"/> of <paramref name="center"/>, each once, ascending.</summary>
        public void VerticesInSphere(Vector3 center, float radius, List<int> result, List<int> triangleScratch = null)
        {
            result.Clear();
            var tris = triangleScratch ?? new List<int>();
            TrianglesNear(center, radius, tris);
            float r2 = radius * radius;
            // Every corner within reach, then sorted so each vertex is kept once: no set per call.
            for (int i = 0; i < tris.Count; i++)
            {
                int t = tris[i] * 3;
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[t + k];
                    var p = vertices[v];
                    float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                    if (dx * dx + dy * dy + dz * dz <= r2) result.Add(v);
                }
            }
            result.Sort();
            int kept = 0;
            for (int i = 0; i < result.Count; i++)
                if (i == 0 || result[i] != result[i - 1]) result[kept++] = result[i];
            result.RemoveRange(kept, result.Count - kept);
        }

        public unsafe Hit Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool backfaces = true)
        {
            if (nodeCount == 0) return new Hit { Distance = maxDistance };
            direction.Normalize();
            float3 o = origin, d = direction;
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
            {
                var view = ViewOf(n, c, t);
                BvhKernels.Raycast(view, o, d, maxDistance, backfaces ? 1 : 0, out hit);
            }
            return ToHit(hit);
        }

        /// <summary><see cref="Raycast(Vector3, Vector3, float, bool)"/> over the triangles <paramref name="filter"/> accepts (managed code).</summary>
        public unsafe Hit Raycast<TFilter>(Vector3 origin, Vector3 direction, float maxDistance, bool backfaces, TFilter filter) where TFilter : struct, ITriangleFilter
        {
            if (nodeCount == 0) return new Hit { Distance = maxDistance };
            direction.Normalize();
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
                BvhKernels.Raycast(ViewOf(n, c, t), origin, direction, maxDistance, backfaces, ref filter, out hit);
            return ToHit(hit);
        }

        public unsafe Hit ClosestPoint(Vector3 point, float maxDistance)
        {
            if (nodeCount == 0) return new Hit { Distance = maxDistance };
            float3 p = point;
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
            {
                var view = ViewOf(n, c, t);
                BvhKernels.ClosestPoint(view, p, maxDistance, out hit);
            }
            return ToHit(hit);
        }

        /// <summary><see cref="ClosestPoint(Vector3, float)"/> over the triangles <paramref name="filter"/> accepts (managed code).</summary>
        public unsafe Hit ClosestPoint<TFilter>(Vector3 point, float maxDistance, TFilter filter) where TFilter : struct, ITriangleFilter
        {
            if (nodeCount == 0) return new Hit { Distance = maxDistance };
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
                BvhKernels.ClosestPoint(ViewOf(n, c, t), point, maxDistance, ref filter, out hit);
            return ToHit(hit);
        }

        // Batches: blocks of queries per Burst call, spread over the thread pool (any thread; the job system needs the main one).
        private const int BatchBlock = 256;

        /// <summary>The closest point for each of <paramref name="points"/> into <paramref name="results"/>, in parallel.</summary>
        public unsafe void ClosestPoints(Vector3[] points, float maxDistance, Hit[] results, int maxThreads = -1)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (results == null || results.Length < points.Length) throw new ArgumentException("A result per point.", nameof(results));
            int blocks = (points.Length + BatchBlock - 1) / BatchBlock;
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Environment.ProcessorCount };
            using (var pinned = Pin())
            {
                var view = pinned.View;
                Parallel.For(0, blocks, options, block =>
                {
                    int start = block * BatchBlock, count = Math.Min(BatchBlock, points.Length - start);
                    var hits = stackalloc BvhHit[BatchBlock];
                    fixed (Vector3* p = &points[start])
                        BvhKernels.ClosestPoints(view, (float3*)p, count, maxDistance, hits);
                    for (int i = 0; i < count; i++) results[start + i] = ToHit(hits[i]);
                });
            }
        }

        /// <summary>A raycast for each origin and direction (unit length) into <paramref name="results"/>, in parallel.</summary>
        public unsafe void Raycasts(Vector3[] origins, Vector3[] directions, float maxDistance, bool backfaces, Hit[] results, int maxThreads = -1)
        {
            if (origins == null) throw new ArgumentNullException(nameof(origins));
            if (directions == null || directions.Length < origins.Length) throw new ArgumentException("A direction per origin.", nameof(directions));
            if (results == null || results.Length < origins.Length) throw new ArgumentException("A result per ray.", nameof(results));
            int blocks = (origins.Length + BatchBlock - 1) / BatchBlock;
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Environment.ProcessorCount };
            using (var pinned = Pin())
            {
                var view = pinned.View;
                Parallel.For(0, blocks, options, block =>
                {
                    int start = block * BatchBlock, count = Math.Min(BatchBlock, origins.Length - start);
                    var hits = stackalloc BvhHit[BatchBlock];
                    fixed (Vector3* o = &origins[start])
                    fixed (Vector3* d = &directions[start])
                        BvhKernels.Raycasts(view, (float3*)o, (float3*)d, count, maxDistance, backfaces ? 1 : 0, hits);
                    for (int i = 0; i < count; i++) results[start + i] = ToHit(hits[i]);
                });
            }
        }

        private static Hit ToHit(in BvhHit hit) => new Hit
        {
            Found = hit.Triangle >= 0,
            Triangle = hit.Triangle >= 0 ? hit.Triangle : 0,
            Position = hit.Triangle >= 0 ? (Vector3)hit.Position : default,
            Bary = hit.Triangle >= 0 ? (Vector3)hit.Bary : default,
            Distance = hit.Distance
        };

        public Vector3 FaceNormal(int triangle)
        {
            int t = triangle * 3;
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            var normal = Vector3.Cross(b - a, c - a).normalized;
            return Mirrored ? -normal : normal;
        }

        /// <inheritdoc cref="Triangle.ClosestPoint(Vector3, Vector3, Vector3, Vector3, out Vector3)"/>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary) =>
            Meshes.Triangle.ClosestPoint(p, a, b, c, out bary);
    }
}
