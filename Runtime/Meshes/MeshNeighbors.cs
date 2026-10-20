using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// The topology brushes and smoothing need, built once per mesh: vertices welded by position (Unity splits a
    /// vertex at every UV seam and hard edge, and a brush that moved only one half would tear the mesh), each welded
    /// vertex's neighbours over the edges, and the triangles around it. Indices are into the mesh's own vertex array
    /// unless the name says <c>welded</c>.
    /// </summary>
    public sealed class MeshNeighbors
    {
        /// <summary>Vertex to its welded vertex index (0..<see cref="WeldedCount"/>-1).</summary>
        public int[] Weld;
        /// <summary>Welded vertex to the mesh vertices that share its position.</summary>
        public int[][] Members;
        /// <summary>Welded vertex to its adjacent welded vertices (each edge once).</summary>
        public int[][] Adjacent;
        /// <summary>Welded vertex to the triangles that use it.</summary>
        public int[][] TrianglesOf;
        /// <summary>Welded vertex to one representative mesh vertex (its first member).</summary>
        public int[] Representative;
        public int WeldedCount => Members.Length;

        /// <param name="normals">With <paramref name="minNormalDot"/>: vertices at one place weld only when their normals
        /// agree that much with every vertex already welded there (two sheets that touch, as the front and back of
        /// double-sided cloth or closed lips, face apart and stay apart; UV seams and hard edges still weld), into the group
        /// whose normals they agree with best. Normals alone cannot tell two sheets apart where both have a hard edge at
        /// the same place: <paramref name="weldKeys"/> can. Null welds by position alone.</param>
        /// <param name="weldKeys">Per vertex: vertices at one place weld only when their keys are equal (a caller's
        /// signature of what else must agree, as skin and blendshape motion). Null: no such condition.</param>
        public static MeshNeighbors Build(Vector3[] vertices, int[] triangles, float weldTolerance = 1e-5f, Vector3[] normals = null, float minNormalDot = -1f, long[] weldKeys = null)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            bool byNormal = normals != null && normals.Length == vertices.Length && minNormalDot > -1f;
            bool byKey = weldKeys != null && weldKeys.Length == vertices.Length;
            int n = vertices.Length;
            var weld = new int[n];
            var membersList = new List<List<int>>();
            float cell = Math.Max(weldTolerance * 4f, 1e-7f);
            var grid = new Dictionary<long, List<int>>();
            float tol2 = weldTolerance * weldTolerance;
            for (int v = 0; v < n; v++)
            {
                var p = vertices[v];
                int cx = (int)Math.Floor(p.x / cell), cy = (int)Math.Floor(p.y / cell), cz = (int)Math.Floor(p.z / cell);
                int found = -1;
                float bestAgreement = float.NegativeInfinity;
                // By position alone the first group found is the one; with normals every group in reach is weighed.
                for (int dx = -1; dx <= 1 && (found < 0 || byNormal); dx++)
                    for (int dy = -1; dy <= 1 && (found < 0 || byNormal); dy++)
                        for (int dz = -1; dz <= 1 && (found < 0 || byNormal); dz++)
                        {
                            if (!grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                            for (int i = 0; i < bucket.Count; i++)
                            {
                                int w = bucket[i];
                                var group = membersList[w];
                                int first = group[0];
                                var q = vertices[first];
                                float ddx = q.x - p.x, ddy = q.y - p.y, ddz = q.z - p.z;
                                if (ddx * ddx + ddy * ddy + ddz * ddz > tol2) continue;
                                if (byKey && weldKeys[first] != weldKeys[v]) continue;
                                if (!byNormal)
                                {
                                    found = w;
                                    break;
                                }
                                // Every member, not only the first: otherwise two members up to twice the limit apart
                                // weld through a third, and which ones depends on the vertex order.
                                float agreement = Agreement(normals, group, normals[v], minNormalDot);
                                if (agreement > bestAgreement)
                                {
                                    bestAgreement = agreement;
                                    found = w;
                                }
                            }
                        }
                if (found < 0)
                {
                    found = membersList.Count;
                    membersList.Add(new List<int>(2));
                    long key = Key(cx, cy, cz);
                    if (!grid.TryGetValue(key, out var mine)) grid[key] = mine = new List<int>(2);
                    mine.Add(found);
                }
                membersList[found].Add(v);
                weld[v] = found;
            }

            int w2 = membersList.Count;
            var members = new int[w2][];
            var representative = new int[w2];
            for (int w = 0; w < w2; w++) { members[w] = membersList[w].ToArray(); representative[w] = members[w][0]; }

            var adjacency = new HashSet<int>[w2];
            var triangleLists = new List<int>[w2];
            int triangleCount = triangles.Length / 3;
            for (int t = 0; t < triangleCount; t++)
            {
                int a = weld[triangles[t * 3]], b = weld[triangles[t * 3 + 1]], c = weld[triangles[t * 3 + 2]];
                Link(adjacency, a, b); Link(adjacency, b, c); Link(adjacency, c, a);
                Use(triangleLists, a, t); if (b != a) Use(triangleLists, b, t); if (c != a && c != b) Use(triangleLists, c, t);
            }
            var adjacent = new int[w2][];
            var trianglesOf = new int[w2][];
            for (int w = 0; w < w2; w++)
            {
                if (adjacency[w] != null) { adjacent[w] = new int[adjacency[w].Count]; adjacency[w].CopyTo(adjacent[w]); Array.Sort(adjacent[w]); }
                else adjacent[w] = Array.Empty<int>();
                trianglesOf[w] = triangleLists[w] != null ? triangleLists[w].ToArray() : Array.Empty<int>();
            }
            return new MeshNeighbors { Weld = weld, Members = members, Adjacent = adjacent, TrianglesOf = trianglesOf, Representative = representative };
        }

        // How well a normal agrees with a group's: the least cosine to any member, or -infinity when one disagrees past the
        // limit. A missing (zero) normal agrees with any.
        private static float Agreement(Vector3[] normals, List<int> group, Vector3 normal, float minDot)
        {
            float least = 1f;
            float ln = normal.sqrMagnitude;
            if (ln < 1e-12f) return 1f;
            foreach (int member in group)
            {
                var other = normals[member];
                float lo = other.sqrMagnitude;
                if (lo < 1e-12f) continue;
                float cosine = Vector3.Dot(other, normal) / Mathf.Sqrt(lo * ln);
                if (cosine < minDot) return float.NegativeInfinity;
                least = Math.Min(least, cosine);
            }
            return least;
        }

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        private static void Link(HashSet<int>[] adjacency, int a, int b)
        {
            if (a == b) return;
            (adjacency[a] ?? (adjacency[a] = new HashSet<int>())).Add(b);
            (adjacency[b] ?? (adjacency[b] = new HashSet<int>())).Add(a);
        }

        private static void Use(List<int>[] lists, int w, int t) => (lists[w] ?? (lists[w] = new List<int>(6))).Add(t);

        /// <summary>The welded vertices within <paramref name="rings"/> edge steps of a welded vertex (itself included).</summary>
        public void Ring(int welded, int rings, HashSet<int> result)
        {
            result.Add(welded);
            if (rings <= 0) return;
            var frontier = new List<int> { welded };
            for (int r = 0; r < rings; r++)
            {
                var next = new List<int>();
                foreach (int w in frontier)
                    foreach (int a in Adjacent[w])
                        if (result.Add(a)) next.Add(a);
                frontier = next;
                if (frontier.Count == 0) break;
            }
        }

        /// <summary>Spreads a per-mesh-vertex value to every member of its welded vertex (keeps split vertices together).</summary>
        public void Unsplit(Vector3[] values)
        {
            for (int w = 0; w < Members.Length; w++)
            {
                var m = Members[w];
                if (m.Length < 2) continue;
                var v = values[m[0]];
                for (int i = 1; i < m.Length; i++) values[m[i]] = v;
            }
        }
    }
}
