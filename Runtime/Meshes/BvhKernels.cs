using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>A node of the flat BVH: 32 bytes, children side by side (a pair is one cache line).</summary>
    public struct BvhNode
    {
        public float3 Min;
        /// <summary>Inner node: the left child (the right one follows it). Leaf: its first slot.</summary>
        public int LeftOrFirst;
        public float3 Max;
        /// <summary>Leaf: how many slots it holds (at least one). Inner node: 0.</summary>
        public int Count;

        public bool IsLeaf => Count > 0;
    }

    /// <summary>A triangle's corners, copied into leaf order so a leaf test reads one contiguous block.</summary>
    public struct BvhTriangle
    {
        public float3 A, B, C;
    }

    /// <summary>A query's answer: <see cref="Triangle"/> is the mesh's triangle index, or -1 when nothing was found.</summary>
    public struct BvhHit
    {
        public int Triangle;
        public float3 Position;
        public float3 Bary;
        public float Distance;
    }

    /// <summary>Which triangles a query considers (by the mesh's triangle index). A struct, so calls inline.</summary>
    public interface ITriangleFilter
    {
        bool Accept(int triangle);
    }

    /// <summary>Every triangle.</summary>
    public struct AcceptAllTriangles : ITriangleFilter
    {
        public bool Accept(int triangle) => true;
    }

    /// <summary>
    /// A BVH's memory as Burst code sees it (pointers into arrays someone keeps alive and pinned, see
    /// <see cref="MeshBvh.Pin"/>). Slots are the triangles in leaf order.
    /// </summary>
    public unsafe struct BvhView
    {
        [NativeDisableUnsafePtrRestriction] public BvhNode* Nodes;
        [NativeDisableUnsafePtrRestriction] public BvhTriangle* Corners;
        [NativeDisableUnsafePtrRestriction] public int* TriangleOfSlot;
        /// <summary>Per mesh triangle, its slot (-1 for a triangle left out of the tree): where a seeded query starts.</summary>
        [NativeDisableUnsafePtrRestriction] public int* SlotOfTriangle;
        /// <summary>How many triangles the mesh has (in the tree or not).</summary>
        public int TriangleCount;
        public int NodeCount;
        /// <summary>1 when the corners went through a mirroring transform (front faces wind the other way).</summary>
        public int Mirrored;
    }

    /// <summary>
    /// The flat BVH's build, refit and queries, Burst-compiled (called directly from managed code, from any thread; the same
    /// code runs managed only where Burst is off). Ties are deterministic whatever the tree's layout: among equally
    /// near hits the lowest triangle index wins.
    /// <para>
    /// The contract every layout keeps: a closest-point query returns, among the accepted triangles nearer than the range, the
    /// one at the least computed distance, the lowest triangle index among equal ones; a ray the least hit distance below the
    /// range, the lowest index among equal ones. Each triangle's distance comes from one routine (<see cref="Triangle"/>) on
    /// its corners, and every node's bounds are widened by a rounding slack (a few ulps of the largest coordinate): a
    /// triangle's computed distance can lie a few ulps below the computed distance of its exact box, and without the slack a
    /// node holding an equally near, lower-indexed triangle could be pruned. So the answer depends only on the triangles and
    /// the query, never on how the tree was built, refitted, traversed or seeded.
    /// </para>
    /// <para>
    /// Floats: every entry is FloatMode.Strict with standard precision (no fast math, no reordering, no contraction into
    /// fused multiply-adds), so a query gives the same bits on every machine; compiled synchronously, so the editor never
    /// answers from the managed fallback while Burst compiles. The filtered queries (generic, managed filters) run as
    /// managed code, which may round differently in the last bit: callers that need answers identical across machines
    /// use the Burst entries (whole tree, or a subset tree from Build's include) rather than a managed filter.
    /// </para>
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class BvhKernels
    {
        public const int LeafSize = 4;
        /// <summary>The deepest a query's stack gets (see <see cref="SahDepth"/>).</summary>
        public const int StackSize = 128;
        // Below this depth splits are binned SAH; deeper, object medians, which keep any remaining subtree within
        // log2(triangles) levels, so a walk never holds more than StackSize entries.
        private const int SahDepth = 48;
        private const int Bins = 12;

        private struct Range
        {
            public int Node, Start, Count, Depth;
        }

        /// <summary>
        /// Builds the tree over <paramref name="count"/> triangles: <paramref name="corners"/> holds each one's corners in
        /// input order and is reordered into leaf order with <paramref name="triangleOfSlot"/> (which starts as each input
        /// triangle's mesh index). <paramref name="nodes"/> and <paramref name="parents"/> need room for 2 * count - 1
        /// nodes; <paramref name="nodeCount"/> says how many were made. <paramref name="slack"/> receives the rounding slack
        /// every node's bounds are widened by (see the class remarks).
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Build(BvhTriangle* corners, int* triangleOfSlot, int count, BvhNode* nodes, int* parents, int* nodeCount, float* slack)
        {
            *nodeCount = 0;
            *slack = Slack(Extent(corners, 0, count));
            if (count <= 0) return;
            float widen = *slack;
            long bytes3 = (long)count * UnsafeUtility.SizeOf<float3>();
            var centroids = (float3*)UnsafeUtility.Malloc(bytes3, 16, Allocator.Persistent);
            var order = (int*)UnsafeUtility.Malloc((long)count * sizeof(int), 4, Allocator.Persistent);
            var stack = (Range*)UnsafeUtility.Malloc((long)StackSize * 2 * UnsafeUtility.SizeOf<Range>(), 4, Allocator.Persistent);
            for (int i = 0; i < count; i++)
            {
                var t = corners[i];
                var c = (t.A + t.B + t.C) * (1f / 3f);
                // A corner at infinity or NaN still gets a place in the tree (its queries just never match).
                centroids[i] = math.select(float3.zero, c, math.isfinite(c));
                order[i] = i;
            }
            int made = 1;
            int top = 0;
            stack[top++] = new Range { Node = 0, Start = 0, Count = count, Depth = 0 };
            parents[0] = -1;
            var binMin = stackalloc float3[3 * Bins];
            var binMax = stackalloc float3[3 * Bins];
            var binCount = stackalloc int[3 * Bins];
            var rightArea = stackalloc float[Bins];
            var rightCount = stackalloc int[Bins];
            while (top > 0)
            {
                var range = stack[--top];
                float3 min = new float3(float.MaxValue), max = new float3(float.MinValue);
                float3 cmin = min, cmax = max;
                for (int i = range.Start; i < range.Start + range.Count; i++)
                {
                    int k = order[i];
                    var t = corners[k];
                    min = math.min(min, math.min(t.A, math.min(t.B, t.C)));
                    max = math.max(max, math.max(t.A, math.max(t.B, t.C)));
                    cmin = math.min(cmin, centroids[k]);
                    cmax = math.max(cmax, centroids[k]);
                }
                ref var node = ref nodes[range.Node];
                node.Min = min - widen;
                node.Max = max + widen;
                if (range.Count <= LeafSize)
                {
                    node.LeftOrFirst = range.Start;
                    node.Count = range.Count;
                    continue;
                }

                int mid = -1;
                float3 extent = cmax - cmin;
                if (range.Depth < SahDepth)
                {
                    // Binned SAH: the cost of a split is each side's surface area times its triangle count.
                    for (int b = 0; b < 3 * Bins; b++) { binMin[b] = new float3(float.MaxValue); binMax[b] = new float3(float.MinValue); binCount[b] = 0; }
                    float3 scale = math.select(float3.zero, Bins / extent, extent > 0f);
                    for (int i = range.Start; i < range.Start + range.Count; i++)
                    {
                        int k = order[i];
                        var t = corners[k];
                        float3 tmin = math.min(t.A, math.min(t.B, t.C)), tmax = math.max(t.A, math.max(t.B, t.C));
                        int3 bin = math.clamp((int3)((centroids[k] - cmin) * scale), 0, Bins - 1);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int slot = axis * Bins + bin[axis];
                            binMin[slot] = math.min(binMin[slot], tmin);
                            binMax[slot] = math.max(binMax[slot], tmax);
                            binCount[slot]++;
                        }
                    }
                    float bestCost = float.MaxValue;
                    int bestAxis = -1, bestBin = -1;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (!(extent[axis] > 0f)) continue;
                        float3 rmin = new float3(float.MaxValue), rmax = new float3(float.MinValue);
                        int rcount = 0;
                        for (int b = Bins - 1; b > 0; b--)
                        {
                            int slot = axis * Bins + b;
                            rmin = math.min(rmin, binMin[slot]); rmax = math.max(rmax, binMax[slot]); rcount += binCount[slot];
                            rightArea[b] = rcount > 0 ? HalfArea(rmin, rmax) : 0f;
                            rightCount[b] = rcount;
                        }
                        float3 lmin = new float3(float.MaxValue), lmax = new float3(float.MinValue);
                        int lcount = 0;
                        for (int b = 0; b < Bins - 1; b++)
                        {
                            int slot = axis * Bins + b;
                            lmin = math.min(lmin, binMin[slot]); lmax = math.max(lmax, binMax[slot]); lcount += binCount[slot];
                            // Split between bin b and b + 1.
                            if (lcount == 0 || rightCount[b + 1] == 0) continue;
                            float cost = HalfArea(lmin, lmax) * lcount + rightArea[b + 1] * rightCount[b + 1];
                            if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestBin = b; }
                        }
                    }
                    if (bestAxis >= 0)
                    {
                        float scaleAxis = scale[bestAxis], low = cmin[bestAxis];
                        int i = range.Start, j = range.Start + range.Count - 1;
                        while (i <= j)
                        {
                            int bin = math.clamp((int)((centroids[order[i]][bestAxis] - low) * scaleAxis), 0, Bins - 1);
                            if (bin <= bestBin) i++;
                            else { int swap = order[i]; order[i] = order[j]; order[j] = swap; j--; }
                        }
                        if (i > range.Start && i < range.Start + range.Count) mid = i;
                    }
                }
                if (mid < 0)
                {
                    // Object median along the widest centroid axis (or by order when the centroids all coincide).
                    mid = range.Start + range.Count / 2;
                    int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
                    if (extent[axis] > 0f) NthElement(order, range.Start, range.Start + range.Count - 1, mid, centroids, axis);
                }

                int left = made;
                made += 2;
                node.LeftOrFirst = left;
                node.Count = 0;
                parents[left] = range.Node;
                parents[left + 1] = range.Node;
                stack[top++] = new Range { Node = left + 1, Start = mid, Count = range.Start + range.Count - mid, Depth = range.Depth + 1 };
                stack[top++] = new Range { Node = left, Start = range.Start, Count = mid - range.Start, Depth = range.Depth + 1 };
            }
            *nodeCount = made;

            // Corners and mesh indices into leaf order.
            var sortedCorners = (BvhTriangle*)UnsafeUtility.Malloc((long)count * UnsafeUtility.SizeOf<BvhTriangle>(), 16, Allocator.Persistent);
            var sortedTriangles = (int*)centroids; // no longer needed, and large enough for count ints
            for (int i = 0; i < count; i++)
            {
                sortedCorners[i] = corners[order[i]];
                sortedTriangles[i] = triangleOfSlot[order[i]];
            }
            UnsafeUtility.MemCpy(corners, sortedCorners, (long)count * UnsafeUtility.SizeOf<BvhTriangle>());
            UnsafeUtility.MemCpy(triangleOfSlot, sortedTriangles, (long)count * sizeof(int));
            UnsafeUtility.Free(sortedCorners, Allocator.Persistent);
            UnsafeUtility.Free(stack, Allocator.Persistent);
            UnsafeUtility.Free(order, Allocator.Persistent);
            UnsafeUtility.Free(centroids, Allocator.Persistent);
        }

        // The largest finite coordinate magnitude of slots [first, first + count) (a corner at infinity or NaN adds nothing).
        private static float Extent(BvhTriangle* corners, int first, int count)
        {
            float extent = 0f;
            for (int s = first; s < first + count; s++)
            {
                var t = corners[s];
                extent = math.max(extent, Finite(t.A));
                extent = math.max(extent, Finite(t.B));
                extent = math.max(extent, Finite(t.C));
            }
            return extent;
        }

        private static float Finite(float3 v) => math.cmax(math.select(float3.zero, math.abs(v), math.isfinite(v)));

        /// <summary>
        /// The bounds slack for corners up to <paramref name="extent"/> in magnitude: a few ulps of it (float epsilon is 1.2e-7),
        /// and something above zero for a surface at the origin.
        /// </summary>
        public static float Slack(float extent) => extent * 1e-6f + 1e-9f;

        private static float HalfArea(float3 min, float3 max)
        {
            float3 d = math.max(max - min, 0f);
            return d.x * d.y + d.y * d.z + d.z * d.x;
        }

        private static void NthElement(int* items, int lo, int hi, int nth, float3* keys, int axis)
        {
            while (lo < hi)
            {
                float pivot = keys[items[(lo + hi) / 2]][axis];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (keys[items[i]][axis] < pivot) i++;
                    while (keys[items[j]][axis] > pivot) j--;
                    if (i <= j) { int t = items[i]; items[i] = items[j]; items[j] = t; i++; j--; }
                }
                if (nth <= j) hi = j; else if (nth >= i) lo = i; else return;
            }
        }

        /// <summary>
        /// Copies each slot's corners from the vertices again (<paramref name="slotVertices"/>: three vertex indices per slot)
        /// and recomputes every node's bounds, children before parents, widened by the slack of the new corners (written to
        /// <paramref name="slack"/>).
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Refit(BvhNode* nodes, int nodeCount, BvhTriangle* corners, float3* vertices, int3* slotVertices, int slotCount, float* slack)
        {
            for (int s = 0; s < slotCount; s++)
            {
                int3 v = slotVertices[s];
                corners[s] = new BvhTriangle { A = vertices[v.x], B = vertices[v.y], C = vertices[v.z] };
            }
            *slack = Slack(Extent(corners, 0, slotCount));
            float widen = *slack;
            // Nodes are made parents before children, so a reverse pass sees children first.
            for (int i = nodeCount - 1; i >= 0; i--)
            {
                ref var node = ref nodes[i];
                if (node.Count > 0) LeafBounds(corners, node.LeftOrFirst, node.Count, widen, out node.Min, out node.Max);
                else
                {
                    node.Min = math.min(nodes[node.LeftOrFirst].Min, nodes[node.LeftOrFirst + 1].Min);
                    node.Max = math.max(nodes[node.LeftOrFirst].Max, nodes[node.LeftOrFirst + 1].Max);
                }
            }
        }

        /// <summary>
        /// Refits only <paramref name="leaves"/> (each once): their corners from the vertices, their bounds, and their
        /// ancestors up to the first one whose bounds come out unchanged (the tree was consistent before, so nothing above
        /// it changes). No sorting: a walk recomputes each ancestor from its children as they are now. The slack only grows
        /// (to cover the moved corners): what was enough for the corners that did not move still is.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void RefitLeaves(BvhNode* nodes, int* parents, BvhTriangle* corners, float3* vertices, int3* slotVertices, int* leaves, int leafCount,
            float* slack)
        {
            float extent = 0f;
            for (int l = 0; l < leafCount; l++)
            {
                ref var moved = ref nodes[leaves[l]];
                for (int s = moved.LeftOrFirst; s < moved.LeftOrFirst + moved.Count; s++)
                {
                    int3 v = slotVertices[s];
                    corners[s] = new BvhTriangle { A = vertices[v.x], B = vertices[v.y], C = vertices[v.z] };
                }
                extent = math.max(extent, Extent(corners, moved.LeftOrFirst, moved.Count));
            }
            *slack = math.max(*slack, Slack(extent));
            float widen = *slack;
            for (int l = 0; l < leafCount; l++)
            {
                int index = leaves[l];
                ref var leaf = ref nodes[index];
                LeafBounds(corners, leaf.LeftOrFirst, leaf.Count, widen, out float3 min, out float3 max);
                if (Same(min, leaf.Min) && Same(max, leaf.Max)) continue;
                leaf.Min = min;
                leaf.Max = max;
                for (int p = parents[index]; p >= 0; p = parents[p])
                {
                    ref var node = ref nodes[p];
                    float3 pmin = math.min(nodes[node.LeftOrFirst].Min, nodes[node.LeftOrFirst + 1].Min);
                    float3 pmax = math.max(nodes[node.LeftOrFirst].Max, nodes[node.LeftOrFirst + 1].Max);
                    if (Same(pmin, node.Min) && Same(pmax, node.Max)) break;
                    node.Min = pmin;
                    node.Max = pmax;
                }
            }
        }

        private static bool Same(float3 a, float3 b) => math.all(a == b);

        private static void LeafBounds(BvhTriangle* corners, int first, int count, float slack, out float3 min, out float3 max)
        {
            min = new float3(float.MaxValue);
            max = new float3(float.MinValue);
            for (int s = first; s < first + count; s++)
            {
                var t = corners[s];
                min = math.min(min, math.min(t.A, math.min(t.B, t.C)));
                max = math.max(max, math.max(t.A, math.max(t.B, t.C)));
            }
            min -= slack;
            max += slack;
        }

        // ---- Queries ------------------------------------------------------------------------------------------------------

        /// <summary>The nearest point of the surface within <paramref name="maxDistance"/> (not found: Triangle -1).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void ClosestPoint(in BvhView view, in float3 point, float maxDistance, out BvhHit hit)
        {
            var filter = new AcceptAllTriangles();
            ClosestPoint(view, point, maxDistance, ref filter, out hit);
        }

        /// <summary><see cref="ClosestPoint(in BvhView, in float3, float, out BvhHit)"/> for each of <paramref name="count"/> points.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void ClosestPoints(in BvhView view, float3* points, int count, float maxDistance, BvhHit* hits)
        {
            var filter = new AcceptAllTriangles();
            for (int i = 0; i < count; i++) ClosestPoint(view, points[i], maxDistance, ref filter, out hits[i]);
        }

        /// <summary>The closest-point walk with a triangle filter (managed callers instantiate it with their own filter).</summary>
        public static void ClosestPoint<TFilter>(in BvhView view, float3 point, float maxDistance, ref TFilter filter, out BvhHit hit) where TFilter : struct, ITriangleFilter =>
            ClosestPoint(view, point, maxDistance, ref filter, -1, out hit);

        /// <summary>
        /// The closest-point walk starting from <paramref name="seed"/> (a mesh triangle, typically the previous answer for a
        /// point that moved a little, or -1): its distance bounds the search from the start, so nearly everything farther is
        /// pruned. The answer is the unseeded one (see the class remarks): the seed is a candidate like any other.
        /// </summary>
        public static void ClosestPoint<TFilter>(in BvhView view, float3 point, float maxDistance, ref TFilter filter, int seed, out BvhHit hit)
            where TFilter : struct, ITriangleFilter
        {
            hit = new BvhHit { Triangle = -1, Distance = maxDistance };
            if (view.NodeCount == 0) return;
            float best2 = maxDistance * maxDistance;
            if (seed >= 0 && seed < view.TriangleCount && view.SlotOfTriangle != null)
            {
                int slot = view.SlotOfTriangle[seed];
                if (slot >= 0 && filter.Accept(seed))
                {
                    var corners = view.Corners[slot];
                    float3 q = Triangle.ClosestPoint(point, corners.A, corners.B, corners.C, out float3 bary);
                    float d2 = math.lengthsq(q - point);
                    if (d2 < best2)
                    {
                        best2 = d2;
                        hit.Triangle = seed;
                        hit.Position = q;
                        hit.Bary = bary;
                        hit.Distance = math.sqrt(d2);
                    }
                }
            }
            int* stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            var nodes = view.Nodes;
            while (top > 0)
            {
                ref var node = ref nodes[stack[--top]];
                // Equal distances go on: a lower triangle index there may win the tie.
                if (BoxDistance2(point, node.Min, node.Max) > best2) continue;
                if (node.Count > 0)
                {
                    for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++)
                    {
                        int t = view.TriangleOfSlot[s];
                        if (!filter.Accept(t)) continue;
                        var c = view.Corners[s];
                        float3 q = Triangle.ClosestPoint(point, c.A, c.B, c.C, out float3 bary);
                        float d2 = math.lengthsq(q - point);
                        if (d2 < best2 || (hit.Triangle >= 0 && d2 == best2 && t < hit.Triangle))
                        {
                            best2 = d2;
                            hit.Triangle = t;
                            hit.Position = q;
                            hit.Bary = bary;
                            hit.Distance = math.sqrt(d2);
                        }
                    }
                }
                else
                {
                    // Nearer child first so the bound tightens early.
                    int left = node.LeftOrFirst;
                    float dl = BoxDistance2(point, nodes[left].Min, nodes[left].Max);
                    float dr = BoxDistance2(point, nodes[left + 1].Min, nodes[left + 1].Max);
                    if (dl < dr) { stack[top++] = left + 1; stack[top++] = left; }
                    else { stack[top++] = left; stack[top++] = left + 1; }
                }
            }
        }

        /// <summary>
        /// The first triangle along a ray (unit <paramref name="direction"/>) within <paramref name="maxDistance"/>;
        /// <paramref name="backfaces"/> 0 skips triangles facing away.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Raycast(in BvhView view, in float3 origin, in float3 direction, float maxDistance, int backfaces, out BvhHit hit)
        {
            var filter = new AcceptAllTriangles();
            Raycast(view, origin, direction, maxDistance, backfaces != 0, ref filter, out hit);
        }

        /// <summary><see cref="Raycast(in BvhView, in float3, in float3, float, int, out BvhHit)"/> for each ray.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Raycasts(in BvhView view, float3* origins, float3* directions, int count, float maxDistance, int backfaces, BvhHit* hits)
        {
            var filter = new AcceptAllTriangles();
            for (int i = 0; i < count; i++) Raycast(view, origins[i], directions[i], maxDistance, backfaces != 0, ref filter, out hits[i]);
        }

        /// <summary>The ray walk with a triangle filter.</summary>
        public static void Raycast<TFilter>(in BvhView view, float3 origin, float3 direction, float maxDistance, bool backfaces, ref TFilter filter, out BvhHit hit) where TFilter : struct, ITriangleFilter
        {
            hit = new BvhHit { Triangle = -1, Distance = maxDistance };
            if (view.NodeCount == 0) return;
            var nodes = view.Nodes;
            bool mirrored = view.Mirrored != 0;
            // Infinite on an axis the ray runs parallel to: RayBox handles that axis by the origin alone.
            float3 inv = 1f / direction;
            int* stack = stackalloc int[StackSize];
            // Where the ray enters each node on the stack: one entered past the best hit so far is skipped.
            float* entries = stackalloc float[StackSize];
            int top = 0;
            if (!RayBox(origin, direction, inv, nodes[0].Min, nodes[0].Max, hit.Distance, out float rootEntry)) return;
            stack[top] = 0; entries[top++] = rootEntry;
            while (top > 0)
            {
                --top;
                if (entries[top] > hit.Distance) continue;
                ref var node = ref nodes[stack[top]];
                if (node.Count > 0)
                {
                    for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++)
                    {
                        int t = view.TriangleOfSlot[s];
                        if (!filter.Accept(t)) continue;
                        var c = view.Corners[s];
                        if (!Triangle.Raycast(origin, direction, c.A, c.B, c.C, backfaces, mirrored, out float d, out float3 bary)) continue;
                        if (d < hit.Distance || (hit.Triangle >= 0 && d == hit.Distance && t < hit.Triangle))
                        {
                            hit.Triangle = t; hit.Distance = d; hit.Bary = bary;
                            hit.Position = origin + direction * d;
                        }
                    }
                }
                else
                {
                    // The child the ray enters first is searched first: a hit in it rules out more of the other.
                    int left = node.LeftOrFirst;
                    bool l = RayBox(origin, direction, inv, nodes[left].Min, nodes[left].Max, hit.Distance, out float leftEntry);
                    bool r = RayBox(origin, direction, inv, nodes[left + 1].Min, nodes[left + 1].Max, hit.Distance, out float rightEntry);
                    if (l && r && leftEntry > rightEntry) { stack[top] = left; entries[top++] = leftEntry; stack[top] = left + 1; entries[top++] = rightEntry; }
                    else
                    {
                        if (r) { stack[top] = left + 1; entries[top++] = rightEntry; }
                        if (l) { stack[top] = left; entries[top++] = leftEntry; }
                    }
                }
            }
        }

        // ---- Queries under a TriangleRule: Burst entries, so filtered answers are as reproducible as unfiltered ones -------

        /// <summary>The closest point among the triangles <paramref name="rule"/> accepts, from <paramref name="seed"/> (or -1).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void ClosestPointByRule(in BvhView view, in float3 point, float maxDistance, in TriangleRuleView rule, int seed, out BvhHit hit)
        {
            var filter = rule;
            ClosestPoint(view, point, maxDistance, ref filter, seed, out hit);
        }

        /// <summary>The first triangle along a ray among those <paramref name="rule"/> accepts.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void RaycastByRule(in BvhView view, in float3 origin, in float3 direction, float maxDistance, int backfaces, in TriangleRuleView rule,
            out BvhHit hit)
        {
            var filter = rule;
            Raycast(view, origin, direction, maxDistance, backfaces != 0, ref filter, out hit);
        }

        /// <summary>The closest point of each of the classes of <paramref name="rule"/> (its ClassCount) into <paramref name="hits"/>.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void ClosestPointsByClass(in BvhView view, in float3 point, float maxDistance, in TriangleRuleView rule, BvhHit* hits)
        {
            var classifier = rule;
            ClosestPoints(view, point, maxDistance, ref classifier, rule.ClassCount, hits);
        }

        /// <summary>
        /// For each of <paramref name="classCount"/> (1 to 4) acceptance classes, the closest point among the triangles of that
        /// class within <paramref name="maxDistance"/>, written to <paramref name="hits"/>[class]: what one filtered closest-point
        /// walk per class would return, in one traversal (a node is pruned only when it is farther than the best of every
        /// class so far, and each class keeps its own tie rule).
        /// </summary>
        public static void ClosestPoints<TClassifier>(in BvhView view, float3 point, float maxDistance, ref TClassifier classifier, int classCount, BvhHit* hits)
            where TClassifier : struct, ITriangleClassifier
        {
            if (classCount < 1) return;
            if (classCount > 4) classCount = 4;
            float limit = maxDistance * maxDistance;
            float4 best = new float4(limit);
            for (int k = 0; k < classCount; k++) hits[k] = new BvhHit { Triangle = -1, Distance = maxDistance };
            if (view.NodeCount == 0) return;
            int all = (1 << classCount) - 1;
            // The bound: the farthest best of the classes (all start at the range).
            float bound = limit;
            int* stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            var nodes = view.Nodes;
            while (top > 0)
            {
                ref var node = ref nodes[stack[--top]];
                if (BoxDistance2(point, node.Min, node.Max) > bound) continue;
                if (node.Count > 0)
                {
                    bool improved = false;
                    for (int s = node.LeftOrFirst; s < node.LeftOrFirst + node.Count; s++)
                    {
                        int t = view.TriangleOfSlot[s];
                        int classes = classifier.Classes(t) & all;
                        if (classes == 0) continue;
                        var c = view.Corners[s];
                        float3 q = Triangle.ClosestPoint(point, c.A, c.B, c.C, out float3 bary);
                        float d2 = math.lengthsq(q - point);
                        for (int k = 0; k < classCount; k++)
                        {
                            if ((classes & (1 << k)) == 0) continue;
                            if (d2 < best[k] || (hits[k].Triangle >= 0 && d2 == best[k] && t < hits[k].Triangle))
                            {
                                best[k] = d2;
                                hits[k] = new BvhHit { Triangle = t, Position = q, Bary = bary, Distance = math.sqrt(d2) };
                                improved = true;
                            }
                        }
                    }
                    if (improved)
                    {
                        bound = best[0];
                        for (int k = 1; k < classCount; k++) bound = math.max(bound, best[k]);
                    }
                }
                else
                {
                    int left = node.LeftOrFirst;
                    float dl = BoxDistance2(point, nodes[left].Min, nodes[left].Max);
                    float dr = BoxDistance2(point, nodes[left + 1].Min, nodes[left + 1].Max);
                    if (dl < dr) { stack[top++] = left + 1; stack[top++] = left; }
                    else { stack[top++] = left; stack[top++] = left + 1; }
                }
            }
        }

        public static float BoxDistance2(float3 p, float3 min, float3 max)
        {
            float3 d = math.max(math.max(min - p, p - max), 0f);
            return math.lengthsq(d);
        }

        // Slabs, conservatively: an axis the ray is parallel to only asks whether the origin lies within the box there (no
        // 0 x infinity), and the interval is widened by a few float steps so a hit on a box's face (a flat box, a triangle in
        // a slab's plane) is never lost to rounding. Widening only lets more nodes be searched, so ties are untouched.
        private static bool RayBox(float3 origin, float3 direction, float3 inv, float3 min, float3 max, float maxDistance, out float entry)
        {
            float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                if (direction[axis] == 0f)
                {
                    if (origin[axis] < min[axis] || origin[axis] > max[axis]) { entry = 0f; return false; }
                    continue;
                }
                float t1 = (min[axis] - origin[axis]) * inv[axis], t2 = (max[axis] - origin[axis]) * inv[axis];
                tmin = math.max(tmin, math.min(t1, t2));
                tmax = math.min(tmax, math.max(t1, t2));
            }
            const float Widen = 4e-7f;
            tmin -= math.abs(tmin) * Widen;
            tmax += math.abs(tmax) * Widen;
            entry = math.max(tmin, 0f);
            return tmax >= entry && entry <= maxDistance;
        }
    }
}
