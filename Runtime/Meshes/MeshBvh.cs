using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// Bounding volume hierarchy over a mesh's triangles for brush queries: the triangles or vertices inside a sphere,
    /// the first triangle along a ray, the closest surface point. Built once; <see cref="Refit"/> follows the vertices
    /// as they move (a few milliseconds for a body), <see cref="RefitTriangles"/> only the leaves a stroke touched.
    /// The vertex array passed to Build is kept by reference: refit after changing it in place. Thread-safe for
    /// queries between refits.
    /// </summary>
    public sealed class MeshBvh
    {
        private struct Node
        {
            public Vector3 Min, Max;
            public int Left, Right;   // children, -1 for leaves
            public int Start, Count;  // leaf triangles (into Order)
            public int Parent;
        }

        public struct Hit
        {
            public bool Found;
            public int Triangle;
            public Vector3 Position;
            public Vector3 Bary;
            public float Distance;
        }

        private const int LeafSize = 8;
        private Node[] nodes;
        private int[] order;
        private int[] leafOfTriangle;
        private Vector3[] vertices;
        private int[] triangles;
        private int nodeCount;

        /// <summary>
        /// The vertices went through a mirroring transform (a negative scale: its determinant is negative), so the mesh's
        /// front faces wind the other way round in them: raycasts without back faces and <see cref="FaceNormal"/> follow.
        /// </summary>
        public bool Mirrored { get; set; }

        public int TriangleCount => triangles.Length / 3;
        public Vector3[] Vertices => vertices;
        public int[] Triangles => triangles;

        public static MeshBvh Build(Vector3[] vertices, int[] triangles)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            var bvh = new MeshBvh { vertices = vertices, triangles = triangles };
            int count = triangles.Length / 3;
            bvh.order = new int[count];
            for (int i = 0; i < count; i++) bvh.order[i] = i;
            bvh.leafOfTriangle = new int[count];
            bvh.nodes = new Node[Math.Max(1, 2 * count)];
            var centroids = new Vector3[count];
            for (int t = 0; t < count; t++)
                centroids[t] = (vertices[triangles[t * 3]] + vertices[triangles[t * 3 + 1]] + vertices[triangles[t * 3 + 2]]) / 3f;
            bvh.nodeCount = 0;
            bvh.BuildRecursive(0, count, -1, centroids);
            return bvh;
        }

        private int BuildRecursive(int start, int count, int parent, Vector3[] centroids)
        {
            int index = nodeCount++;
            if (index >= nodes.Length) Array.Resize(ref nodes, nodes.Length * 2);
            var node = new Node { Parent = parent, Left = -1, Right = -1, Start = start, Count = count };
            Bounds(start, count, out node.Min, out node.Max);
            if (count <= LeafSize)
            {
                for (int i = start; i < start + count; i++) leafOfTriangle[order[i]] = index;
                nodes[index] = node;
                return index;
            }
            // Split at the median along the widest axis of the centroids.
            Vector3 cmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), cmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = start; i < start + count; i++) { var c = centroids[order[i]]; cmin = Vector3.Min(cmin, c); cmax = Vector3.Max(cmax, c); }
            var extent = cmax - cmin;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            int mid = start + count / 2;
            NthElement(order, start, start + count - 1, mid, centroids, axis);
            nodes[index] = node;
            int left = BuildRecursive(start, mid - start, index, centroids);
            int right = BuildRecursive(mid, start + count - mid, index, centroids);
            node.Left = left; node.Right = right;
            nodes[index] = node;
            return index;
        }

        private static void NthElement(int[] items, int lo, int hi, int nth, Vector3[] keys, int axis)
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

        private void Bounds(int start, int count, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = order[i] * 3;
                for (int k = 0; k < 3; k++)
                {
                    var p = vertices[triangles[t + k]];
                    if (p.x < min.x) min.x = p.x; if (p.y < min.y) min.y = p.y; if (p.z < min.z) min.z = p.z;
                    if (p.x > max.x) max.x = p.x; if (p.y > max.y) max.y = p.y; if (p.z > max.z) max.z = p.z;
                }
            }
        }

        /// <summary>Recomputes every node's bounds from the current vertex positions (same topology).</summary>
        public void Refit()
        {
            // Nodes are allocated parents before children, so a reverse pass sees children first.
            for (int i = nodeCount - 1; i >= 0; i--)
            {
                ref var node = ref nodes[i];
                if (node.Left < 0) Bounds(node.Start, node.Count, out node.Min, out node.Max);
                else
                {
                    node.Min = Vector3.Min(nodes[node.Left].Min, nodes[node.Right].Min);
                    node.Max = Vector3.Max(nodes[node.Left].Max, nodes[node.Right].Max);
                }
            }
        }

        /// <summary>Refits only the leaves holding <paramref name="movedTriangles"/> and their ancestors.</summary>
        public void RefitTriangles(IReadOnlyList<int> movedTriangles)
        {
            if (movedTriangles == null || movedTriangles.Count == 0) return;
            NextStamp();
            dirtyNodes.Clear();
            for (int i = 0; i < movedTriangles.Count; i++) MarkLeaf(leafOfTriangle[movedTriangles[i]]);
            RefitMarked();
        }

        /// <summary>Refits the leaves holding the triangles around <paramref name="movedVertices"/>.</summary>
        public void RefitVertices(IReadOnlyList<int> movedVertices, MeshNeighbors neighbors)
        {
            if (movedVertices == null || movedVertices.Count == 0) return;
            NextStamp();
            dirtyNodes.Clear();
            for (int i = 0; i < movedVertices.Count; i++)
            {
                foreach (int t in neighbors.TrianglesOf[neighbors.Weld[movedVertices[i]]])
                {
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
        private readonly List<int> dirtyNodes = new List<int>();
        private static readonly IComparer<int> Descending = Comparer<int>.Create((a, b) => b.CompareTo(a));

        private void NextStamp()
        {
            if (nodeStamp == null || nodeStamp.Length < nodeCount) nodeStamp = new int[nodes.Length];
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
            if (nodeStamp[leaf] == stamp) return;
            nodeStamp[leaf] = stamp;
            dirtyNodes.Add(leaf);
        }

        // The marked leaves, then their ancestors deepest first (children have higher indices than their parents).
        private void RefitMarked()
        {
            int leaves = dirtyNodes.Count;
            if (leaves == 0) return;
            // A large part of the tree: one pass over every node is cheaper than walking up from each leaf.
            if (leaves * 4 > nodeCount) { Refit(); return; }
            for (int i = 0; i < leaves; i++)
            {
                ref var node = ref nodes[dirtyNodes[i]];
                Bounds(node.Start, node.Count, out node.Min, out node.Max);
                for (int p = node.Parent; p >= 0 && nodeStamp[p] != stamp; p = nodes[p].Parent)
                {
                    nodeStamp[p] = stamp;
                    dirtyNodes.Add(p);
                }
            }
            dirtyNodes.Sort(leaves, dirtyNodes.Count - leaves, Descending);
            for (int i = leaves; i < dirtyNodes.Count; i++)
            {
                ref var node = ref nodes[dirtyNodes[i]];
                node.Min = Vector3.Min(nodes[node.Left].Min, nodes[node.Right].Min);
                node.Max = Vector3.Max(nodes[node.Left].Max, nodes[node.Right].Max);
            }
        }

        // The deepest a walk's stack gets: a median split keeps the tree about log2(triangles / LeafSize) deep, and a walk
        // holds at most one pending sibling per level (plus the two children just pushed).
        private const int StackSize = 128;

        /// <summary>The triangles whose bounds overlap the sphere (a superset of those really inside).</summary>
        public void TrianglesNear(Vector3 center, float radius, List<int> result)
        {
            result.Clear();
            if (nodeCount == 0) return;
            float r2 = radius * radius;
            Span<int> stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int i = stack[--top];
                ref var node = ref nodes[i];
                if (BoxDistance2(center, node.Min, node.Max) > r2) continue;
                if (node.Left < 0)
                {
                    for (int k = node.Start; k < node.Start + node.Count; k++) result.Add(order[k]);
                }
                else { stack[top++] = node.Left; stack[top++] = node.Right; }
            }
        }

        /// <summary>The mesh vertices within <paramref name="radius"/> of <paramref name="center"/>, each once.</summary>
        public void VerticesInSphere(Vector3 center, float radius, List<int> result, List<int> triangleScratch = null)
        {
            result.Clear();
            var tris = triangleScratch ?? new List<int>();
            TrianglesNear(center, radius, tris);
            float r2 = radius * radius;
            var seen = new HashSet<int>();
            for (int i = 0; i < tris.Count; i++)
            {
                int t = tris[i] * 3;
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[t + k];
                    if (!seen.Add(v)) continue;
                    var p = vertices[v];
                    float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                    if (dx * dx + dy * dy + dz * dz <= r2) result.Add(v);
                }
            }
        }

        public Hit Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool backfaces = true)
        {
            var best = new Hit { Distance = maxDistance };
            if (nodeCount == 0) return best;
            direction.Normalize();
            var inv = new Vector3(1f / (direction.x == 0f ? 1e-20f : direction.x), 1f / (direction.y == 0f ? 1e-20f : direction.y), 1f / (direction.z == 0f ? 1e-20f : direction.z));
            Span<int> stack = stackalloc int[StackSize];
            // Where the ray enters each node on the stack: one entered past the best hit so far is skipped.
            Span<float> entries = stackalloc float[StackSize];
            int top = 0;
            if (!RayBox(origin, inv, nodes[0].Min, nodes[0].Max, best.Distance, out float rootEntry)) return best;
            stack[top] = 0; entries[top++] = rootEntry;
            while (top > 0)
            {
                --top;
                if (entries[top] > best.Distance) continue;
                ref var node = ref nodes[stack[top]];
                if (node.Left < 0)
                {
                    for (int k = node.Start; k < node.Start + node.Count; k++)
                    {
                        int t = order[k];
                        if (RayTriangle(origin, direction, t, backfaces, out float d, out Vector3 bary) && d < best.Distance)
                        {
                            best.Found = true; best.Triangle = t; best.Distance = d; best.Bary = bary;
                            best.Position = origin + direction * d;
                        }
                    }
                }
                else
                {
                    // The child the ray enters first is searched first: a hit in it rules out more of the other.
                    bool left = RayBox(origin, inv, nodes[node.Left].Min, nodes[node.Left].Max, best.Distance, out float leftEntry);
                    bool right = RayBox(origin, inv, nodes[node.Right].Min, nodes[node.Right].Max, best.Distance, out float rightEntry);
                    if (left && right && leftEntry > rightEntry) { stack[top] = node.Left; entries[top++] = leftEntry; stack[top] = node.Right; entries[top++] = rightEntry; }
                    else
                    {
                        if (right) { stack[top] = node.Right; entries[top++] = rightEntry; }
                        if (left) { stack[top] = node.Left; entries[top++] = leftEntry; }
                    }
                }
            }
            return best;
        }

        public Hit ClosestPoint(Vector3 point, float maxDistance)
        {
            var best = new Hit { Distance = maxDistance };
            if (nodeCount == 0) return best;
            float best2 = maxDistance * maxDistance;
            Span<int> stack = stackalloc int[StackSize];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                int i = stack[--top];
                ref var node = ref nodes[i];
                if (BoxDistance2(point, node.Min, node.Max) > best2) continue;
                if (node.Left < 0)
                {
                    for (int k = node.Start; k < node.Start + node.Count; k++)
                    {
                        int t = order[k];
                        var q = ClosestPointOnTriangle(point, vertices[triangles[t * 3]], vertices[triangles[t * 3 + 1]], vertices[triangles[t * 3 + 2]], out Vector3 bary);
                        float d2 = (q - point).sqrMagnitude;
                        if (d2 < best2) { best2 = d2; best.Found = true; best.Triangle = t; best.Position = q; best.Bary = bary; best.Distance = (float)Math.Sqrt(d2); }
                    }
                }
                else
                {
                    // Nearer child first so the bound tightens early.
                    float dl = BoxDistance2(point, nodes[node.Left].Min, nodes[node.Left].Max);
                    float dr = BoxDistance2(point, nodes[node.Right].Min, nodes[node.Right].Max);
                    if (dl < dr) { stack[top++] = node.Right; stack[top++] = node.Left; } else { stack[top++] = node.Left; stack[top++] = node.Right; }
                }
            }
            return best;
        }

        public Vector3 FaceNormal(int triangle)
        {
            int t = triangle * 3;
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            var normal = Vector3.Cross(b - a, c - a).normalized;
            return Mirrored ? -normal : normal;
        }

        private static float BoxDistance2(Vector3 p, Vector3 min, Vector3 max)
        {
            float dx = p.x < min.x ? min.x - p.x : p.x > max.x ? p.x - max.x : 0f;
            float dy = p.y < min.y ? min.y - p.y : p.y > max.y ? p.y - max.y : 0f;
            float dz = p.z < min.z ? min.z - p.z : p.z > max.z ? p.z - max.z : 0f;
            return dx * dx + dy * dy + dz * dz;
        }

        private static bool RayBox(Vector3 origin, Vector3 inv, Vector3 min, Vector3 max, float maxDistance, out float entry)
        {
            float t1 = (min.x - origin.x) * inv.x, t2 = (max.x - origin.x) * inv.x;
            float tmin = Math.Min(t1, t2), tmax = Math.Max(t1, t2);
            t1 = (min.y - origin.y) * inv.y; t2 = (max.y - origin.y) * inv.y;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            t1 = (min.z - origin.z) * inv.z; t2 = (max.z - origin.z) * inv.z;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            entry = Math.Max(tmin, 0f);
            return tmax >= entry && tmin <= maxDistance;
        }

        private bool RayTriangle(Vector3 origin, Vector3 dir, int triangle, bool backfaces, out float distance, out Vector3 bary)
        {
            int t = triangle * 3;
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            distance = 0f; bary = Vector3.zero;
            if (backfaces ? Math.Abs(det) < 1e-12f : (Mirrored ? -det : det) < 1e-12f) return false;
            float invDet = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            float d = Vector3.Dot(e2, q) * invDet;
            if (d <= 0f) return false;
            distance = d; bary = new Vector3(1f - u - v, u, v);
            return true;
        }

        /// <summary>
        /// The point of triangle abc nearest <paramref name="p"/>, with its barycentric weights. A collapsed triangle (two
        /// corners at one place, or all three on a line) is the nearest point of its edges: always finite for finite input.
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            // Ericson, Real-Time Collision Detection 5.1.5.
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }
            float vc = d1 * d4 - d3 * d2;
            // A triangle without area (two corners at one place, or all three about on a line): an edge test passed still
            // gives the right point (p's projection lies within that edge) unless the edge has no length, and the face
            // test below would divide by about nothing, so its edges decide there.
            if (vc <= 0f && d1 >= 0f && d3 <= 0f && d1 - d3 > 0f) { float v = d1 / (d1 - d3); bary = new Vector3(1 - v, v, 0); return a + v * ab; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f && d2 - d6 > 0f) { float w = d2 / (d2 - d6); bary = new Vector3(1 - w, 0, w); return a + w * ac; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f && (d4 - d3) + (d5 - d6) > 0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bary = new Vector3(0, 1 - w, w); return b + w * (c - b); }
            if (!(Vector3.Cross(ab, ac).sqrMagnitude > 1e-10f * ab.sqrMagnitude * ac.sqrMagnitude)) return ClosestPointOnCollapsed(p, a, b, c, out bary);
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            bary = new Vector3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        // A triangle without area: the nearest of its three edges' nearest points (a point when all corners meet).
        private static Vector3 ClosestPointOnCollapsed(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            float tab = Along(p, a, b), tbc = Along(p, b, c), tca = Along(p, c, a);
            Vector3 qab = a + (b - a) * tab, qbc = b + (c - b) * tbc, qca = c + (a - c) * tca;
            float dab = (qab - p).sqrMagnitude, dbc = (qbc - p).sqrMagnitude, dca = (qca - p).sqrMagnitude;
            if (dab <= dbc && dab <= dca) { bary = new Vector3(1 - tab, tab, 0); return qab; }
            if (dbc <= dca) { bary = new Vector3(0, 1 - tbc, tbc); return qbc; }
            bary = new Vector3(tca, 0, 1 - tca);
            return qca;
        }

        // Where along segment from-to the point nearest p lies (0..1; 0 for a segment of no length).
        private static float Along(Vector3 p, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float length2 = d.sqrMagnitude;
            if (length2 <= 0f) return 0f;
            float t = Vector3.Dot(p - from, d) / length2;
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }
    }
}
