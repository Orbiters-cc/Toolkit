using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Orbiters.Toolkit.Meshes
{
    /// <summary>
    /// The topology brushes and smoothing need, built once per mesh: vertices welded by position (Unity splits a
    /// vertex at every UV seam and hard edge, and a brush that moved only one half would tear the mesh), each welded
    /// vertex's neighbours over the edges, and the triangles around it. Indices are into the mesh's own vertex array
    /// unless the name says <c>welded</c>. Flat arrays (offsets and values, one pair per relation), read through spans:
    /// a build makes about a dozen arrays whatever the mesh's size, and the result is not changed after it.
    /// <para>
    /// Read-only, though arrays are handed out: <see cref="Weld"/> and <see cref="Representative"/> are the weld itself
    /// (the spans, refits and every other reader share them), and <see cref="Members"/>, <see cref="Adjacent"/> and
    /// <see cref="TrianglesOf"/> are copies made once for callers that still index arrays: writing into any of them makes
    /// readers disagree. They stay arrays only until those callers (Mixer) read the spans.
    /// </para>
    /// </summary>
    public sealed class MeshNeighbors
    {
        private int[] weld, representative;
        private int[] memberOffsets, members;
        private int[] adjacentOffsets, adjacent;
        private int[] triangleOffsets, trianglesOf;
        private int[][] membersJagged, adjacentJagged, trianglesJagged;

        /// <summary>Vertex to its welded vertex index (0..<see cref="WeldedCount"/>-1).</summary>
        public int[] Weld => weld;
        /// <summary>Welded vertex to one representative mesh vertex (its first member, the lowest index).</summary>
        public int[] Representative => representative;
        public int WeldedCount => representative.Length;

        /// <summary>The mesh vertices that share welded vertex <paramref name="welded"/>'s position, ascending.</summary>
        public ReadOnlySpan<int> MembersOf(int welded) => new ReadOnlySpan<int>(members, memberOffsets[welded], memberOffsets[welded + 1] - memberOffsets[welded]);
        /// <summary>The welded vertices adjacent to <paramref name="welded"/> over an edge, ascending.</summary>
        public ReadOnlySpan<int> AdjacentTo(int welded) => new ReadOnlySpan<int>(adjacent, adjacentOffsets[welded], adjacentOffsets[welded + 1] - adjacentOffsets[welded]);
        /// <summary>The triangles that use welded vertex <paramref name="welded"/>, ascending.</summary>
        public ReadOnlySpan<int> TrianglesAround(int welded) => new ReadOnlySpan<int>(trianglesOf, triangleOffsets[welded], triangleOffsets[welded + 1] - triangleOffsets[welded]);

        /// <summary>
        /// The relations as compressed rows, for kernels that read them whole (pin a span with <c>fixed</c>): welded vertex w's
        /// members are <see cref="MemberValues"/>[<see cref="MemberStarts"/>[w] .. <see cref="MemberStarts"/>[w + 1]), and the same
        /// for adjacency and triangles. Starts hold <see cref="WeldedCount"/> + 1 entries; values are what the per-vertex spans
        /// read. Read-only.
        /// </summary>
        public ReadOnlySpan<int> MemberStarts => memberOffsets;
        /// <inheritdoc cref="MemberStarts"/>
        public ReadOnlySpan<int> MemberValues => members;
        /// <inheritdoc cref="MemberStarts"/>
        public ReadOnlySpan<int> AdjacentStarts => adjacentOffsets;
        /// <inheritdoc cref="MemberStarts"/>
        public ReadOnlySpan<int> AdjacentValues => adjacent;
        /// <inheritdoc cref="MemberStarts"/>
        public ReadOnlySpan<int> TriangleStarts => triangleOffsets;
        /// <inheritdoc cref="MemberStarts"/>
        public ReadOnlySpan<int> TriangleValues => trianglesOf;

        /// <summary>Welded vertex to the mesh vertices that share its position (an array per welded vertex, made on first use: prefer <see cref="MembersOf"/>).</summary>
        public int[][] Members => membersJagged ?? Jagged(ref membersJagged, memberOffsets, members);
        /// <summary>Welded vertex to its adjacent welded vertices (made on first use: prefer <see cref="AdjacentTo"/>).</summary>
        public int[][] Adjacent => adjacentJagged ?? Jagged(ref adjacentJagged, adjacentOffsets, adjacent);
        /// <summary>Welded vertex to the triangles that use it (made on first use: prefer <see cref="TrianglesAround"/>).</summary>
        public int[][] TrianglesOf => trianglesJagged ?? Jagged(ref trianglesJagged, triangleOffsets, trianglesOf);

        private static int[][] Jagged(ref int[][] field, int[] offsets, int[] values)
        {
            var result = new int[offsets.Length - 1][];
            for (int w = 0; w < result.Length; w++)
            {
                int count = offsets[w + 1] - offsets[w];
                if (count == 0) { result[w] = Array.Empty<int>(); continue; }
                result[w] = new int[count];
                Array.Copy(values, offsets[w], result[w], 0, count);
            }
            return Interlocked.CompareExchange(ref field, result, null) ?? result;
        }

        /// <param name="normals">With <paramref name="minNormalDot"/>: vertices at one place weld only when their normals
        /// agree that much with every vertex already welded there (two sheets that touch, as the front and back of
        /// double-sided cloth or closed lips, face apart and stay apart; UV seams and hard edges still weld), into the group
        /// whose normals they agree with best. Normals alone cannot tell two sheets apart where both have a hard edge at
        /// the same place: <paramref name="weldKeys"/> can. Null or empty welds by position alone.</param>
        /// <param name="weldKeys">Per vertex: vertices at one place weld only when their keys are equal (a caller's
        /// signature of what else must agree, as skin and blendshape motion). Null or empty: no such condition.</param>
        /// <remarks>With normals or keys this is the position weld split by <see cref="Refine"/>: a caller that has the
        /// position weld already refines it instead.</remarks>
        public static MeshNeighbors Build(Vector3[] vertices, int[] triangles, float weldTolerance = 1e-5f, Vector3[] normals = null, float minNormalDot = -1f, long[] weldKeys = null)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            bool byNormal = Given(normals, vertices.Length, nameof(normals)) && minNormalDot > -1f;
            bool byKey = Given(weldKeys, vertices.Length, nameof(weldKeys));
            var positional = BuildByPosition(vertices, triangles, weldTolerance);
            return byNormal || byKey ? positional.Refine(vertices, triangles, weldTolerance, byNormal ? normals : null, minNormalDot, byKey ? weldKeys : null) : positional;
        }

        // An optional per-vertex array: absent when null or empty, wrong when another length (two sheets would weld silently).
        private static bool Given<T>(T[] values, int vertexCount, string name)
        {
            if (values == null || values.Length == 0) return false;
            if (values.Length != vertexCount) throw new ArgumentException("One value per vertex: " + values.Length + " for " + vertexCount + " vertices.", name);
            return true;
        }

        // Vertices welded into the first group (by creation) whose first member lies within the tolerance. Groups are found
        // through cells four tolerances wide, sorted by key: a vertex looks only in the cells its tolerance reaches (one to
        // eight), and each cell lists its groups by index arrays, so nothing is allocated per vertex or per cell.
        private static MeshNeighbors BuildByPosition(Vector3[] vertices, int[] triangles, float weldTolerance)
        {
            int n = vertices.Length;
            float cell = Math.Max(weldTolerance * 4f, 1e-7f);
            float tol2 = weldTolerance * weldTolerance;
            // Reach past the tolerance so rounding at a cell border never hides a match (distances decide); still at most two
            // cells per axis, as a cell is four tolerances wide.
            double reach = weldTolerance * 1.5;
            var keys = new long[n];
            var byKey = new int[n];
            for (int v = 0; v < n; v++)
            {
                var p = vertices[v];
                keys[v] = Key(CellOf(p.x, cell), CellOf(p.y, cell), CellOf(p.z, cell));
                byKey[v] = v;
            }
            var sortedKeys = (long[])keys.Clone();
            Array.Sort(sortedKeys, byKey);
            int cells = 0;
            for (int i = 0; i < n; i++) if (i == 0 || sortedKeys[i] != sortedKeys[i - 1]) sortedKeys[cells++] = sortedKeys[i];
            // Each cell's groups in creation order: a head and tail per cell, a next per group.
            var cellHead = new int[cells];
            var cellTail = new int[cells];
            for (int c = 0; c < cells; c++) cellHead[c] = cellTail[c] = -1;
            var nextInCell = new int[n];
            var groupFirst = new int[n];
            var weld = new int[n];
            int groups = 0;
            for (int v = 0; v < n; v++)
            {
                var p = vertices[v];
                int x0 = CellOf(p.x - reach, cell), x1 = Math.Min(CellOf(p.x + reach, cell), x0 + 1);
                int y0 = CellOf(p.y - reach, cell), y1 = Math.Min(CellOf(p.y + reach, cell), y0 + 1);
                int z0 = CellOf(p.z - reach, cell), z1 = Math.Min(CellOf(p.z + reach, cell), z0 + 1);
                int found = -1;
                for (int x = x0; x <= x1 && found < 0; x++)
                    for (int y = y0; y <= y1 && found < 0; y++)
                        for (int z = z0; z <= z1 && found < 0; z++)
                        {
                            int c = Array.BinarySearch(sortedKeys, 0, cells, Key(x, y, z));
                            if (c < 0) continue;
                            for (int g = cellHead[c]; g >= 0; g = nextInCell[g])
                            {
                                var q = vertices[groupFirst[g]];
                                float dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
                                if (dx * dx + dy * dy + dz * dz > tol2) continue;
                                found = g;
                                break;
                            }
                        }
                if (found < 0)
                {
                    found = groups++;
                    groupFirst[found] = v;
                    nextInCell[found] = -1;
                    int c = Array.BinarySearch(sortedKeys, 0, cells, keys[v]);
                    if (cellTail[c] >= 0) nextInCell[cellTail[c]] = found; else cellHead[c] = found;
                    cellTail[c] = found;
                }
                weld[v] = found;
            }
            return FromWeld(weld, groups, triangles);
        }

        // Floor of a coordinate in cells, clamped into int range (far or broken coordinates share the outermost cells).
        private static int CellOf(double coordinate, float cell)
        {
            double c = Math.Floor(coordinate / cell);
            return double.IsNaN(c) ? 0 : c >= int.MaxValue - 2 ? int.MaxValue - 2 : c <= int.MinValue ? int.MinValue : (int)c;
        }

        /// <summary>
        /// This weld split further: within each welded vertex, members stay together only when their
        /// <paramref name="weldKeys"/> are equal and their <paramref name="normals"/> agree by at least
        /// <paramref name="minNormalDot"/> (see <see cref="Build"/>), each joining the group it agrees with best. Every new
        /// group lies inside one of this weld's, which is what callers that use both rely on.
        /// </summary>
        public MeshNeighbors Refine(Vector3[] vertices, int[] triangles, float weldTolerance, Vector3[] normals, float minNormalDot, long[] weldKeys = null)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));
            if (vertices.Length != weld.Length) throw new ArgumentException("The vertices this weld was made from.", nameof(vertices));
            bool byNormal = Given(normals, vertices.Length, nameof(normals)) && minNormalDot > -1f;
            bool byKey = Given(weldKeys, vertices.Length, nameof(weldKeys));
            if (!byNormal && !byKey) return this;
            int n = vertices.Length;
            float tol2 = weldTolerance * weldTolerance;
            var refined = new int[n];
            // The groups made inside the current welded vertex, and each one's distinct normals as a chain.
            var local = new List<int>();
            var groupFirst = new List<int>();
            var normalHead = new List<int>();
            var chainNormal = new List<Vector3>();
            var chainNext = new List<int>();
            for (int w = 0; w < WeldedCount; w++)
            {
                local.Clear();
                var group = MembersOf(w);
                for (int i = 0; i < group.Length; i++)
                {
                    int v = group[i];
                    var p = vertices[v];
                    int found = -1;
                    float bestAgreement = float.NegativeInfinity;
                    for (int k = 0; k < local.Count; k++)
                    {
                        int g = local[k];
                        int first = groupFirst[g];
                        var q = vertices[first];
                        float dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
                        if (dx * dx + dy * dy + dz * dz > tol2) continue;
                        if (byKey && weldKeys[first] != weldKeys[v]) continue;
                        if (!byNormal) { found = g; break; }
                        // Every member's normal, not only the first's: otherwise two members up to twice the limit apart
                        // weld through a third, and which ones depends on the vertex order.
                        float agreement = Agreement(chainNormal, chainNext, normalHead[g], normals[v], minNormalDot);
                        if (agreement > bestAgreement) { bestAgreement = agreement; found = g; }
                    }
                    if (found < 0)
                    {
                        found = groupFirst.Count;
                        groupFirst.Add(v);
                        normalHead.Add(-1);
                        local.Add(found);
                    }
                    if (byNormal) AddNormal(chainNormal, chainNext, normalHead, found, normals[v]);
                    refined[v] = found;
                }
            }
            // Groups numbered by their first member, as a weld from scratch numbers them.
            int groups = groupFirst.Count;
            var order = new int[groups];
            var firsts = groupFirst.ToArray();
            for (int g = 0; g < groups; g++) order[g] = g;
            Array.Sort(firsts, order);
            var renumber = new int[groups];
            for (int i = 0; i < groups; i++) renumber[order[i]] = i;
            for (int v = 0; v < n; v++) refined[v] = renumber[refined[v]];
            return FromWeld(refined, groups, triangles);
        }

        // A member's normal, unless the group has it already (exactly) or it is missing (zero): those change no agreement.
        private static void AddNormal(List<Vector3> chainNormal, List<int> chainNext, List<int> head, int group, Vector3 normal)
        {
            if (normal.sqrMagnitude < 1e-12f) return;
            int last = -1;
            for (int i = head[group]; i >= 0; last = i, i = chainNext[i])
                if (chainNormal[i].x == normal.x && chainNormal[i].y == normal.y && chainNormal[i].z == normal.z) return;
            chainNormal.Add(normal);
            chainNext.Add(-1);
            if (last >= 0) chainNext[last] = chainNormal.Count - 1; else head[group] = chainNormal.Count - 1;
        }

        // How well a normal agrees with a group's: the least cosine to any member's, or -infinity when one disagrees past
        // the limit. A missing (zero) normal agrees with any.
        private static float Agreement(List<Vector3> chainNormal, List<int> chainNext, int head, Vector3 normal, float minDot)
        {
            float least = 1f;
            float ln = normal.sqrMagnitude;
            if (ln < 1e-12f) return 1f;
            for (int i = head; i >= 0; i = chainNext[i])
            {
                var other = chainNormal[i];
                float lo = other.sqrMagnitude;
                if (lo < 1e-12f) continue;
                float cosine = Vector3.Dot(other, normal) / Mathf.Sqrt(lo * ln);
                if (cosine < minDot) return float.NegativeInfinity;
                least = Math.Min(least, cosine);
            }
            return least;
        }

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        // Members, triangles and neighbours of each welded vertex, as offsets and values (counting passes, no lists).
        private static MeshNeighbors FromWeld(int[] weld, int groups, int[] triangles)
        {
            int n = weld.Length;
            var memberOffsets = new int[groups + 1];
            for (int v = 0; v < n; v++) memberOffsets[weld[v] + 1]++;
            for (int w = 0; w < groups; w++) memberOffsets[w + 1] += memberOffsets[w];
            var members = new int[n];
            var fill = new int[groups];
            Array.Copy(memberOffsets, fill, groups);
            for (int v = 0; v < n; v++) members[fill[weld[v]]++] = v;
            var representative = new int[groups];
            for (int w = 0; w < groups; w++) representative[w] = members[memberOffsets[w]];

            int triangleCount = triangles.Length / 3;
            var triangleOffsets = new int[groups + 1];
            for (int t = 0; t < triangleCount; t++)
            {
                int a = weld[triangles[t * 3]], b = weld[triangles[t * 3 + 1]], c = weld[triangles[t * 3 + 2]];
                triangleOffsets[a + 1]++;
                if (b != a) triangleOffsets[b + 1]++;
                if (c != a && c != b) triangleOffsets[c + 1]++;
            }
            for (int w = 0; w < groups; w++) triangleOffsets[w + 1] += triangleOffsets[w];
            var trianglesOf = new int[triangleOffsets[groups]];
            Array.Copy(triangleOffsets, fill, groups);
            for (int t = 0; t < triangleCount; t++)
            {
                int a = weld[triangles[t * 3]], b = weld[triangles[t * 3 + 1]], c = weld[triangles[t * 3 + 2]];
                trianglesOf[fill[a]++] = t;
                if (b != a) trianglesOf[fill[b]++] = t;
                if (c != a && c != b) trianglesOf[fill[c]++] = t;
            }

            // Neighbours: the other corners of each welded vertex's triangles, sorted and made unique in place.
            var adjacentOffsets = new int[groups + 1];
            var adjacent = new int[2 * trianglesOf.Length];
            int written = 0;
            for (int w = 0; w < groups; w++)
            {
                int start = written;
                for (int k = triangleOffsets[w]; k < triangleOffsets[w + 1]; k++)
                {
                    int t = trianglesOf[k];
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int other = weld[triangles[t * 3 + corner]];
                        if (other != w) adjacent[written++] = other;
                    }
                }
                Array.Sort(adjacent, start, written - start);
                int kept = start;
                for (int i = start; i < written; i++)
                    if (i == start || adjacent[i] != adjacent[i - 1]) adjacent[kept++] = adjacent[i];
                written = kept;
                adjacentOffsets[w + 1] = written;
            }
            Array.Resize(ref adjacent, written);
            return new MeshNeighbors
            {
                weld = weld, representative = representative, memberOffsets = memberOffsets, members = members,
                triangleOffsets = triangleOffsets, trianglesOf = trianglesOf, adjacentOffsets = adjacentOffsets, adjacent = adjacent
            };
        }

        /// <summary>The welded vertices within <paramref name="rings"/> edge steps of a welded vertex (itself included).</summary>
        public void Ring(int welded, int rings, HashSet<int> result)
        {
            result.Add(welded);
            if (rings <= 0) return;
            var frontier = new List<int> { welded };
            var next = new List<int>();
            for (int r = 0; r < rings; r++)
            {
                next.Clear();
                foreach (int w in frontier)
                {
                    var around = AdjacentTo(w);
                    for (int i = 0; i < around.Length; i++)
                        if (result.Add(around[i])) next.Add(around[i]);
                }
                (frontier, next) = (next, frontier);
                if (frontier.Count == 0) break;
            }
        }

        /// <summary>Spreads a per-mesh-vertex value to every member of its welded vertex (keeps split vertices together).</summary>
        public void Unsplit(Vector3[] values)
        {
            for (int w = 0; w < WeldedCount; w++)
            {
                int start = memberOffsets[w], end = memberOffsets[w + 1];
                if (end - start < 2) continue;
                var v = values[members[start]];
                for (int i = start + 1; i < end; i++) values[members[i]] = v;
            }
        }
    }
}
