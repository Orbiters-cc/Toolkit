using System;
using System.Collections.Concurrent;
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
    /// The vertex array passed to Build is kept by reference: refit after changing it in place, or hand the refit another
    /// array (<see cref="Refit(Vector3[])"/>); queries read the corners as of the last build or refit. Thread-safe for queries
    /// between refits. Answers depend only on the triangles (see <see cref="BvhKernels"/>): among equally near hits the
    /// lowest triangle index wins, whatever the tree's layout, refits, seeds or traversal order.
    /// <para>
    /// Filtered queries come two ways: a <see cref="TriangleRule"/> (tests on per-triangle data, run by Burst: the same bits as
    /// the unfiltered queries, what fitting code needs), or a generic <see cref="ITriangleFilter"/> run as managed code (any
    /// logic, but its last bits may differ from Burst's).
    /// </para>
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

            /// <summary><see cref="Triangle"/> when found, -1 otherwise (<see cref="Triangle"/> then reads 0).</summary>
            public int TriangleOrNone => Found ? Triangle : -1;
        }

        private BvhNode[] nodes;
        private int nodeCount;
        private int[] parents;
        private BvhTriangle[] corners;
        private int[] triangleOfSlot;
        private int3[] slotVertices;
        private int[] leafOfTriangle;
        // Per mesh triangle its slot, -1 when left out (seeded queries).
        private int[] slotOfTriangle;
        private Vector3[] vertices;
        private int[] triangles;
        // What every node's bounds are widened by (see BvhKernels' remarks).
        private float slack;

        /// <summary>
        /// The vertices went through a mirroring transform (a negative scale: its determinant is negative), so the mesh's
        /// front faces wind the other way round in them: raycasts without back faces and <see cref="FaceNormal"/> follow.
        /// </summary>
        public bool Mirrored { get; set; }

        public int TriangleCount => triangles.Length / 3;

        // ---- Burst or managed: one path for every result of a session ----------------------------------------------------

        private static readonly object warmLock = new object();
        private static volatile int compiled = -1;

        /// <summary>
        /// Compiles every query entry (<see cref="BvhKernels"/>) on this thread before anything uses them, and says whether the
        /// queries now run as Burst code. Every query calls it first (a volatile read once done), so the first query of a
        /// session, on whichever thread, compiles them all while the others wait. The entries compile synchronously, but only for the thread that first calls one: in
        /// the editor, threads calling an entry while it compiles run it as managed code, which rounds floats otherwise (Mono
        /// evaluates float expressions in double). So the first parallel batch of a session could mix the two paths. Callers
        /// whose results are cached or compared call this first (once per domain it compiles: a one-time stall of the
        /// order of a second or two per entry the first time, near nothing once Burst's cache has them; later calls are a read).
        /// Thread safe.
        /// </summary>
        public static bool WarmUp()
        {
            if (exercising) return false;
            int known = compiled;
            if (known >= 0) return known == 1 && !RunManaged;
            lock (warmLock)
            {
                if (compiled < 0) compiled = Exercise() ? 1 : 0;
                return compiled == 1 && !RunManaged;
            }
        }

        /// <summary>
        /// Whether this session's queries run as Burst code (after <see cref="WarmUp"/>): the path a cached or compared result
        /// must be keyed by. False with Burst compilation off, a failed compilation or <see cref="RunManaged"/>.
        /// </summary>
        public static bool QueriesCompiled => WarmUp();

        /// <summary>
        /// Tests only: every query of every tree runs the walks as managed code (the path an editor without Burst takes), to
        /// check that results of the two paths are never mixed in a cache. Never set in tools.
        /// </summary>
        public static bool RunManaged { get; set; }

        [ThreadStatic] private static bool exercising;

        // The warm-up always runs the Burst entries, so they are compiled whatever the test seam says.
        private static bool ManagedNow => RunManaged && !exercising;

        // Every entry once on a two-triangle tree (on this thread, so each compiles before any other thread calls it), then
        // the probe.
        private static bool Exercise()
        {
            exercising = true;
            try { return ExerciseEntries(); }
            finally { exercising = false; }
        }

        private static unsafe bool ExerciseEntries()
        {
            var vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
            var tree = Build(vertices, new[] { 0, 1, 2, 0, 3, 1 });
            tree.Refit();
            fixed (BvhNode* n = tree.nodes)
            fixed (int* p = tree.parents)
            fixed (BvhTriangle* c = tree.corners)
            fixed (Vector3* v = vertices)
            fixed (int3* sv = tree.slotVertices)
            {
                int leaf = 0;
                for (int i = 0; i < tree.nodeCount; i++) if (n[i].Count > 0) { leaf = i; break; }
                float widen = tree.slack;
                BvhKernels.RefitLeaves(n, p, c, (float3*)v, sv, &leaf, 1, &widen);
            }
            var point = new Vector3(.2f, .2f, .5f);
            tree.ClosestPoint(point, 2f);
            tree.Raycast(point, Vector3.back, 2f);
            tree.ClosestPoints(new[] { point }, 2f, new Hit[1], 1);
            tree.Raycasts(new[] { point }, new[] { Vector3.back }, 2f, true, new Hit[1], 1);
            var normals = new[] { Vector3.forward, Vector3.down };
            var rule = TriangleRule.All.Facing(normals, Vector3.forward, 0f);
            tree.Query(QueryKind.Closest, point, default, 2f, false, rule, 0, default, Span<Hit>.Empty);
            tree.Query(QueryKind.Ray, point, Vector3.back, 2f, true, rule, -1, default, Span<Hit>.Empty);
            var hits = new Hit[2];
            tree.Query(QueryKind.Classes, point, default, 2f, false, rule, -1, new TriangleClasses(rule, TriangleTests.None, TriangleTests.Facing), hits);
            return BvhKernels.CompiledProbe() == 1;
        }
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
            float slack;
            fixed (BvhTriangle* c = bvh.corners)
            fixed (int* order = bvh.triangleOfSlot)
            fixed (BvhNode* n = nodes)
            fixed (int* p = parents)
                BvhKernels.Build(c, order, count, n, p, &made, &slack);
            bvh.slack = slack;
            if (made < capacity)
            {
                Array.Resize(ref nodes, Math.Max(1, made));
                Array.Resize(ref parents, Math.Max(1, made));
            }
            bvh.nodes = nodes;
            bvh.parents = parents;
            bvh.nodeCount = made;
            bvh.slotVertices = new int3[count];
            bvh.slotOfTriangle = new int[triangleCount];
            for (int t = 0; t < triangleCount; t++) bvh.slotOfTriangle[t] = -1;
            for (int s = 0; s < count; s++)
            {
                int t = bvh.triangleOfSlot[s];
                bvh.slotVertices[s] = new int3(triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2]);
                bvh.slotOfTriangle[t] = s;
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
            float widen = slack;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (Vector3* v = vertices)
            fixed (int3* sv = slotVertices)
                BvhKernels.Refit(n, nodeCount, c, (float3*)v, sv, corners.Length, &widen);
            slack = widen;
        }

        /// <summary>
        /// Moves the tree onto <paramref name="moved"/> (the same vertices at other places, one per vertex) and refits it
        /// there: the array is kept by reference from now on, as Build's was.
        /// </summary>
        public void Refit(Vector3[] moved)
        {
            if (moved == null) throw new ArgumentNullException(nameof(moved));
            if (moved.Length != vertices.Length) throw new ArgumentException("One position per vertex: " + moved.Length + " for " + vertices.Length + ".", nameof(moved));
            RefuseWhilePinned();
            vertices = moved;
            Refit();
        }

        /// <summary>
        /// A copy of the tree reading <paramref name="moved"/> (the same vertices at other places, one per vertex: a shape
        /// frame on a body), to refit on its own: refit it along what moved (<see cref="RefitTriangles"/>, far cheaper than a
        /// build) before querying it; it then answers exactly as a tree built on <paramref name="moved"/> would. The copy has
        /// its own bounds and corners; the layout tables are shared (they never change after the build).
        /// </summary>
        public MeshBvh Clone(Vector3[] moved)
        {
            if (moved == null) throw new ArgumentNullException(nameof(moved));
            if (moved.Length != vertices.Length) throw new ArgumentException("One position per vertex: " + moved.Length + " for " + vertices.Length + ".", nameof(moved));
            return new MeshBvh
            {
                nodes = (BvhNode[])nodes.Clone(),
                nodeCount = nodeCount,
                parents = parents,
                corners = (BvhTriangle[])corners.Clone(),
                triangleOfSlot = triangleOfSlot,
                slotVertices = slotVertices,
                leafOfTriangle = leafOfTriangle,
                slotOfTriangle = slotOfTriangle,
                vertices = moved,
                triangles = triangles,
                slack = slack,
                Mirrored = Mirrored,
            };
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
            float widen = slack;
            fixed (BvhNode* n = nodes)
            fixed (int* p = parents)
            fixed (BvhTriangle* c = corners)
            fixed (Vector3* v = vertices)
            fixed (int3* sv = slotVertices)
            fixed (int* l = dirtyLeafBuffer)
                BvhKernels.RefitLeaves(n, p, c, (float3*)v, sv, l, leaves, &widen);
            slack = widen;
        }

        /// <summary>
        /// The tree's memory pinned for Burst jobs and other kernels (<see cref="BvhKernels"/>), until the lease is disposed.
        /// The lease owns the pins: hand <see cref="PinnedBvh.View"/> only to work that ends before it is disposed, and register
        /// every job that reads it with <see cref="PinnedBvh.DependsOn"/> (disposing completes them first). While any lease
        /// is out the tree refuses refits. A lease dropped without being disposed is released once the collector has
        /// finalized it and every job registered on it has completed (checked at the next pin or refit of any tree): never
        /// while a registered job may still read the memory.
        /// </summary>
        public PinnedBvh Pin()
        {
            PinnedBvh.ReleaseDropped(false);
            return new PinnedBvh(this);
        }

        /// <summary>
        /// Releases leases dropped without Dispose once collected. With <paramref name="completeJobs"/> (main thread only: the
        /// editor's periodic pump and the moment before a domain reload) their registered jobs are completed first, so every
        /// dropped lease goes; without it only those whose jobs are already done. A dropped lease keeps its tree alive until
        /// then.
        /// </summary>
        public static void ReleaseDroppedLeases(bool completeJobs) => PinnedBvh.ReleaseDropped(completeJobs);

        /// <summary>
        /// The tree's memory pinned for synchronous Burst work in one scope (a brush dab's kernel run with <c>Run()</c>), without
        /// allocating: <c>using (var pin = bvh.PinScope()) { ... pin.View ... }</c>. Refits are refused until it is disposed.
        /// Jobs scheduled to run past the scope need <see cref="Pin"/> and <see cref="PinnedBvh.DependsOn"/>.
        /// </summary>
        public PinScopeLease PinScope() => new PinScopeLease(this);

        /// <summary>A scoped pin of a <see cref="MeshBvh"/> (see <see cref="PinScope"/>): stack only, disposed by its scope.</summary>
        public ref struct PinScopeLease
        {
            private MeshBvh bvh;
            private GCHandle nodes, corners, triangles, slots;
            private BvhView view;

            internal unsafe PinScopeLease(MeshBvh bvh)
            {
                PinnedBvh.ReleaseDropped(false);
                this.bvh = bvh;
                nodes = GCHandle.Alloc(bvh.nodes, GCHandleType.Pinned);
                corners = GCHandle.Alloc(bvh.corners, GCHandleType.Pinned);
                triangles = GCHandle.Alloc(bvh.triangleOfSlot, GCHandleType.Pinned);
                slots = GCHandle.Alloc(bvh.slotOfTriangle, GCHandleType.Pinned);
                view = new BvhView
                {
                    Nodes = (BvhNode*)nodes.AddrOfPinnedObject(),
                    Corners = (BvhTriangle*)corners.AddrOfPinnedObject(),
                    TriangleOfSlot = (int*)triangles.AddrOfPinnedObject(),
                    SlotOfTriangle = (int*)slots.AddrOfPinnedObject(),
                    TriangleCount = bvh.slotOfTriangle.Length,
                    NodeCount = bvh.nodeCount,
                    Mirrored = bvh.Mirrored ? 1 : 0
                };
                System.Threading.Interlocked.Increment(ref bvh.leases);
            }

            /// <summary>The pinned memory; refused once the scope is disposed.</summary>
            public BvhView View => bvh != null ? view : throw new ObjectDisposedException(nameof(PinScopeLease));

            public void Dispose()
            {
                if (bvh == null) return;
                nodes.Free();
                corners.Free();
                triangles.Free();
                slots.Free();
                System.Threading.Interlocked.Decrement(ref bvh.leases);
                bvh = null;
            }
        }

        private int leases;

        private void RefuseWhilePinned()
        {
            if (System.Threading.Volatile.Read(ref leases) == 0) return;
            PinnedBvh.ReleaseDropped(false);
            if (System.Threading.Volatile.Read(ref leases) > 0) throw new InvalidOperationException("The BVH is pinned for queries: dispose its leases before refitting.");
        }

        /// <summary>A pin of a <see cref="MeshBvh"/>'s arrays (see <see cref="Pin"/>). Disposing it more than once does nothing.</summary>
        public sealed class PinnedBvh : IDisposable
        {
            private readonly MeshBvh bvh;
            private GCHandle nodes, corners, triangles, slots;
            private readonly BvhView view;
            private Unity.Jobs.JobHandle jobs;
            private bool hasJobs;
            private int disposed;

            // Leases the collector found dropped without Dispose: kept (and so their pins) until their jobs are done.
            private static readonly ConcurrentQueue<PinnedBvh> Dropped = new ConcurrentQueue<PinnedBvh>();

            internal unsafe PinnedBvh(MeshBvh bvh)
            {
                this.bvh = bvh;
                nodes = GCHandle.Alloc(bvh.nodes, GCHandleType.Pinned);
                corners = GCHandle.Alloc(bvh.corners, GCHandleType.Pinned);
                triangles = GCHandle.Alloc(bvh.triangleOfSlot, GCHandleType.Pinned);
                slots = GCHandle.Alloc(bvh.slotOfTriangle, GCHandleType.Pinned);
                view = new BvhView
                {
                    Nodes = (BvhNode*)nodes.AddrOfPinnedObject(),
                    Corners = (BvhTriangle*)corners.AddrOfPinnedObject(),
                    TriangleOfSlot = (int*)triangles.AddrOfPinnedObject(),
                    SlotOfTriangle = (int*)slots.AddrOfPinnedObject(),
                    TriangleCount = bvh.slotOfTriangle.Length,
                    NodeCount = bvh.nodeCount,
                    Mirrored = bvh.Mirrored ? 1 : 0
                };
                System.Threading.Interlocked.Increment(ref bvh.leases);
            }

            // Not a release: the jobs registered on the lease may still run, and only the main thread may complete them. The
            // lease is handed (resurrected) to the queue that releases it once they are done.
            ~PinnedBvh()
            {
                if (System.Threading.Volatile.Read(ref disposed) == 0) Dropped.Enqueue(this);
            }

            /// <summary>
            /// Releases the dropped leases whose registered jobs have completed (any thread: a completed handle is only read,
            /// never waited on); the others wait for a later call. With <paramref name="completeJobs"/> (main thread) their
            /// jobs are completed first and every dropped lease is released.
            /// </summary>
            internal static void ReleaseDropped(bool completeJobs)
            {
                for (int n = Dropped.Count; n > 0 && Dropped.TryDequeue(out var lease); n--)
                {
                    if (lease.hasJobs && !lease.jobs.IsCompleted)
                    {
                        if (!completeJobs) { Dropped.Enqueue(lease); continue; }
                        lease.jobs.Complete();
                    }
                    lease.Release();
                }
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
                if (System.Threading.Volatile.Read(ref disposed) != 0) return;
                // Every job that may still read the memory finishes before it can move.
                if (hasJobs) jobs.Complete();
                Release();
                GC.SuppressFinalize(this);
            }

            private void Release()
            {
                if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
                nodes.Free();
                corners.Free();
                triangles.Free();
                slots.Free();
                System.Threading.Interlocked.Decrement(ref bvh.leases);
            }
        }

        private unsafe BvhView ViewOf(BvhNode* n, BvhTriangle* c, int* t) =>
            new BvhView { Nodes = n, Corners = c, TriangleOfSlot = t, NodeCount = nodeCount, Mirrored = Mirrored ? 1 : 0 };

        private unsafe BvhView ViewOf(BvhNode* n, BvhTriangle* c, int* t, int* st) => new BvhView
        {
            Nodes = n, Corners = c, TriangleOfSlot = t, SlotOfTriangle = st, TriangleCount = slotOfTriangle.Length, NodeCount = nodeCount,
            Mirrored = Mirrored ? 1 : 0
        };

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
            WarmUp();
            direction.Normalize();
            float3 o = origin, d = direction;
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
            {
                var view = ViewOf(n, c, t);
                if (ManagedNow) { var all = new AcceptAllTriangles(); BvhKernels.Raycast(view, o, d, maxDistance, backfaces, ref all, out hit); }
                else BvhKernels.Raycast(view, o, d, maxDistance, backfaces ? 1 : 0, out hit);
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
            WarmUp();
            float3 p = point;
            BvhHit hit;
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
            {
                var view = ViewOf(n, c, t);
                if (ManagedNow) { var all = new AcceptAllTriangles(); BvhKernels.ClosestPoint(view, p, maxDistance, ref all, out hit); }
                else BvhKernels.ClosestPoint(view, p, maxDistance, out hit);
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

        /// <summary>
        /// The closest point among the triangles <paramref name="rule"/> accepts (Burst, any thread), starting from
        /// <paramref name="seed"/> (a triangle, typically the answer for a point near this one, or -1): the answer is the
        /// unseeded one, only found faster.
        /// </summary>
        public Hit ClosestPoint(Vector3 point, float maxDistance, in TriangleRule rule, int seed = -1)
        {
            return Query(QueryKind.Closest, point, default, maxDistance, false, rule, seed, default, Span<Hit>.Empty);
        }

        /// <summary>
        /// The first triangle along a ray among those <paramref name="rule"/> accepts (Burst, any thread). With
        /// <paramref name="contact"/> a triangle the origin lies on is met at distance 0 (a point already on a surface); otherwise
        /// only hits ahead of the origin count, as for the unfiltered rays.
        /// </summary>
        public Hit Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool backfaces, in TriangleRule rule, bool contact = false) =>
            Query(contact ? QueryKind.RayFromContact : QueryKind.Ray, origin, direction, maxDistance, backfaces, rule, -1, default, Span<Hit>.Empty);

        /// <summary>
        /// For each class of <paramref name="classes"/>, the closest point among its triangles within
        /// <paramref name="maxDistance"/> into <paramref name="hits"/>[class] (one traversal; what one
        /// <see cref="ClosestPoint(Vector3, float, in TriangleRule, int)"/> per class would return). Burst, any thread.
        /// </summary>
        public void ClosestPointsByClass(Vector3 point, float maxDistance, in TriangleClasses classes, Span<Hit> hits)
        {
            if (hits.Length < classes.Count) throw new ArgumentException("A hit per class.", nameof(hits));
            if (classes.Count < 1) throw new ArgumentException("No class.", nameof(classes));
            Query(QueryKind.Classes, point, default, maxDistance, false, classes.Rule, -1, classes, hits);
        }

        private enum QueryKind { Closest, Ray, RayFromContact, Classes }

        // The rule's arrays pinned for the one Burst call that reads them.
        private unsafe Hit Query(QueryKind kind, Vector3 point, Vector3 direction, float maxDistance, bool backfaces, in TriangleRule rule, int seed,
            in TriangleClasses classes, Span<Hit> hits)
        {
            if (nodeCount == 0)
            {
                for (int i = 0; i < hits.Length; i++) hits[i] = new Hit { Distance = maxDistance };
                return new Hit { Distance = maxDistance };
            }
            rule.Check(slotOfTriangle.Length);
            WarmUp();
            if (kind == QueryKind.Ray || kind == QueryKind.RayFromContact) direction.Normalize();
            BvhHit hit;
            var found = stackalloc BvhHit[TriangleClasses.Max];
            fixed (BvhNode* n = nodes)
            fixed (BvhTriangle* c = corners)
            fixed (int* t = triangleOfSlot)
            fixed (int* st = slotOfTriangle)
            fixed (Vector3* faceNormals = rule.faceNormals)
            fixed (int* masks = rule.masks)
            fixed (int* labels = rule.labels)
            fixed (bool* accepted = rule.acceptedLabels)
            fixed (int* moreLabels = rule.moreLabels)
            fixed (bool* moreAccepted = rule.acceptedMoreLabels)
            fixed (bool* allowed = rule.allowed)
            fixed (int* ranks = rule.ranks)
            fixed (int* awayTriangles = rule.awayTriangles)
            fixed (int* awayLabels = rule.awayLabels)
            {
                var view = ViewOf(n, c, t, st);
                var filter = new TriangleRuleView
                {
                    Set = (int)rule.set,
                    FaceNormals = (float3*)faceNormals,
                    Direction = rule.direction,
                    Minimum = rule.minimum,
                    Facing = (int)rule.facing,
                    Masks = masks,
                    Mask = rule.mask,
                    Labels = labels,
                    AcceptedLabels = (byte*)accepted,
                    HasAcceptedLabels = rule.acceptedLabels != null ? 1 : 0,
                    AcceptedLabelCount = rule.acceptedLabels?.Length ?? 0,
                    OutsideLabelsAccepted = rule.outsideLabelsAccepted ? 1 : 0,
                    HasExcludedLabel = rule.hasExcludedLabel ? 1 : 0,
                    ExcludedLabel = rule.excludedLabel,
                    MoreLabels = moreLabels,
                    AcceptedMoreLabels = (byte*)moreAccepted,
                    AcceptedMoreLabelCount = rule.acceptedMoreLabels?.Length ?? 0,
                    OutsideMoreLabelsAccepted = rule.outsideMoreLabelsAccepted ? 1 : 0,
                    Allowed = (byte*)allowed,
                    Ranks = ranks,
                    MinimumRank = rule.minimumRank,
                    AwayTriangles = awayTriangles,
                    AwayLabels = awayLabels,
                    AwayLabelCount = rule.awayLabels?.Length ?? 0,
                    AwayA = rule.awayA,
                    AwayB = rule.awayB,
                    ExcludedTriangle = rule.excludedTriangle,
                    Needs = classes.needs,
                    ClassCount = classes.Count,
                };
                float3 p = point;
                switch (kind)
                {
                    case QueryKind.Closest:
                        if (ManagedNow) BvhKernels.ClosestPoint(view, p, maxDistance, ref filter, seed, out hit);
                        else BvhKernels.ClosestPointByRule(view, p, maxDistance, filter, seed, out hit);
                        return ToHit(hit);
                    case QueryKind.Ray:
                    case QueryKind.RayFromContact:
                        float3 d = direction;
                        if (ManagedNow) BvhKernels.Raycast(view, p, d, maxDistance, backfaces, kind == QueryKind.RayFromContact, ref filter, out hit);
                        else BvhKernels.RaycastByRule(view, p, d, maxDistance, backfaces ? 1 : 0, kind == QueryKind.RayFromContact ? 1 : 0, filter, out hit);
                        return ToHit(hit);
                    default:
                        if (ManagedNow) BvhKernels.ClosestPoints(view, p, maxDistance, ref filter, classes.Count, found);
                        else BvhKernels.ClosestPointsByClass(view, p, maxDistance, filter, found);
                        for (int i = 0; i < classes.Count; i++) hits[i] = ToHit(found[i]);
                        return default;
                }
            }
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
            // Compiled before the blocks run in parallel: never a batch half managed, half Burst (see WarmUp).
            WarmUp();
            bool managed = ManagedNow;
            using (var pinned = Pin())
            {
                var view = pinned.View;
                Parallel.For(0, blocks, options, block =>
                {
                    int start = block * BatchBlock, count = Math.Min(BatchBlock, points.Length - start);
                    var hits = stackalloc BvhHit[BatchBlock];
                    if (managed)
                    {
                        var all = new AcceptAllTriangles();
                        for (int i = 0; i < count; i++) BvhKernels.ClosestPoint(view, points[start + i], maxDistance, ref all, out hits[i]);
                    }
                    else
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
            WarmUp();
            bool managed = ManagedNow;
            using (var pinned = Pin())
            {
                var view = pinned.View;
                Parallel.For(0, blocks, options, block =>
                {
                    int start = block * BatchBlock, count = Math.Min(BatchBlock, origins.Length - start);
                    var hits = stackalloc BvhHit[BatchBlock];
                    if (managed)
                    {
                        var all = new AcceptAllTriangles();
                        for (int i = 0; i < count; i++) BvhKernels.Raycast(view, origins[start + i], directions[start + i], maxDistance, backfaces, ref all, out hits[i]);
                    }
                    else
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
