using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    // TKG-22: the flat weld and adjacency against the dictionary-and-lists build it replaced (kept here as the reference).
    public sealed class MeshNeighborsTests
    {
        private static class Reference
        {
            public sealed class Result
            {
                public int[] Weld;
                public int[][] Members, Adjacent, TrianglesOf;
            }

            public static Result Build(Vector3[] vertices, int[] triangles, float weldTolerance = 1e-5f, Vector3[] normals = null, float minNormalDot = -1f, long[] weldKeys = null)
            {
                if (vertices == null) throw new ArgumentNullException(nameof(vertices));
                if (triangles == null) throw new ArgumentNullException(nameof(triangles));
                bool byNormal = normals != null && normals.Length == vertices.Length && minNormalDot > -1f;
                bool byKey = weldKeys != null && weldKeys.Length == vertices.Length;
                int n = vertices.Length;
                var weld = new int[n];
                var membersList = new List<List<int>>();
                // By normal: each group's distinct normals (members facing exactly alike agree alike, so one stands for them all).
                var groupNormals = byNormal ? new List<List<Vector3>>() : null;
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
                                    float agreement = Agreement(groupNormals[w], normals[v], minNormalDot);
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
                        groupNormals?.Add(new List<Vector3>(2));
                        long key = Key(cx, cy, cz);
                        if (!grid.TryGetValue(key, out var mine)) grid[key] = mine = new List<int>(2);
                        mine.Add(found);
                    }
                    membersList[found].Add(v);
                    if (byNormal) AddNormal(groupNormals[found], normals[v]);
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
                return new Result { Weld = weld, Members = members, Adjacent = adjacent, TrianglesOf = trianglesOf };
            }

            // A member's normal, unless the group has it already (exactly) or it is missing (zero): those change no agreement.
            private static void AddNormal(List<Vector3> distinct, Vector3 normal)
            {
                if (normal.sqrMagnitude < 1e-12f) return;
                for (int i = 0; i < distinct.Count; i++)
                    if (distinct[i].x == normal.x && distinct[i].y == normal.y && distinct[i].z == normal.z) return;
                distinct.Add(normal);
            }

            // How well a normal agrees with a group's: the least cosine to any member's, or -infinity when one disagrees past
            // the limit. A missing (zero) normal agrees with any.
            private static float Agreement(List<Vector3> groupNormals, Vector3 normal, float minDot)
            {
                float least = 1f;
                float ln = normal.sqrMagnitude;
                if (ln < 1e-12f) return 1f;
                foreach (var other in groupNormals)
                {
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

        }

        private static void AssertSame(Reference.Result expected, MeshNeighbors actual, string what)
        {
            Assert.That(actual.Weld, Is.EqualTo(expected.Weld), what + ": weld");
            Assert.That(actual.WeldedCount, Is.EqualTo(expected.Members.Length), what + ": welded count");
            for (int w = 0; w < actual.WeldedCount; w++)
            {
                Assert.That(actual.MembersOf(w).ToArray(), Is.EqualTo(expected.Members[w]), what + ": members of " + w);
                Assert.That(actual.Members[w], Is.EqualTo(expected.Members[w]), what + ": members of " + w + " (arrays)");
                Assert.That(actual.Representative[w], Is.EqualTo(expected.Members[w][0]), what + ": representative of " + w);
                Assert.That(actual.AdjacentTo(w).ToArray(), Is.EqualTo(expected.Adjacent[w]), what + ": neighbours of " + w);
                Assert.That(actual.TrianglesAround(w).ToArray(), Is.EqualTo(expected.TrianglesOf[w]), what + ": triangles of " + w);
                Assert.That(actual.TrianglesOf[w], Is.EqualTo(expected.TrianglesOf[w]), what + ": triangles of " + w + " (arrays)");
            }
        }

        // A grid of quads with every vertex split four ways (as UV islands per quad) and some splits nudged within the
        // tolerance, a few more beyond it; normals per quad, tilted, and keys per island row.
        private static void SplitGrid(int size, int seed, out Vector3[] vertices, out int[] triangles, out Vector3[] normals, out long[] keys)
        {
            var random = new System.Random(seed);
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var k = new List<long>();
            var t = new List<int>();
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    int start = v.Count;
                    var normal = new Vector3((float)random.NextDouble() - .5f, 1f, (float)random.NextDouble() - .5f).normalized;
                    if (random.Next(5) == 0) normal = -normal;
                    foreach (var (dx, dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                    {
                        var p = new Vector3((x + dx) * .01f, Mathf.Sin(x + dx) * .001f, (y + dy) * .01f);
                        int roll = random.Next(10);
                        if (roll == 0) p += new Vector3(4e-6f, 0, 0);
                        else if (roll == 1) p += new Vector3(0, 3e-5f, 0);
                        v.Add(p);
                        n.Add(normal);
                        k.Add(y % 3 == 0 ? 7 : 1);
                    }
                    t.AddRange(new[] { start, start + 2, start + 1, start + 1, start + 2, start + 3 });
                }
            // A collapsed triangle and an unused vertex.
            t.AddRange(new[] { 0, 0, 1 });
            v.Add(new Vector3(9, 9, 9)); n.Add(Vector3.up); k.Add(1);
            vertices = v.ToArray(); triangles = t.ToArray(); normals = n.ToArray(); keys = k.ToArray();
        }

        [Test]
        public void PositionWeldMatchesTheReference([Values(1, 2, 3)] int seed)
        {
            SplitGrid(24, seed, out var vertices, out var triangles, out _, out _);
            AssertSame(Reference.Build(vertices, triangles, 1e-5f), MeshNeighbors.Build(vertices, triangles, 1e-5f), "by position");
            AssertSame(Reference.Build(vertices, triangles, 0f), MeshNeighbors.Build(vertices, triangles, 0f), "exact places");
        }

        // Exactly coincident splits: the normal and key weld refined from the position weld groups as a weld from scratch did.
        [Test]
        public void NormalAndKeyWeldsMatchTheReference([Values(1, 2)] int seed)
        {
            SplitGrid(20, seed, out var vertices, out var triangles, out var normals, out var keys);
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(Mathf.Round(vertices[i].x * 100f) / 100f, vertices[i].y, Mathf.Round(vertices[i].z * 100f) / 100f);
            AssertSame(Reference.Build(vertices, triangles, 1e-5f, normals, .5f), MeshNeighbors.Build(vertices, triangles, 1e-5f, normals, .5f), "by normal");
            AssertSame(Reference.Build(vertices, triangles, 1e-5f, null, -1f, keys), MeshNeighbors.Build(vertices, triangles, 1e-5f, null, -1f, keys), "by key");
            AssertSame(Reference.Build(vertices, triangles, 1e-5f, normals, -.2f, keys), MeshNeighbors.Build(vertices, triangles, 1e-5f, normals, -.2f, keys), "by normal and key");
            var position = MeshNeighbors.Build(vertices, triangles, 1e-5f);
            var refined = position.Refine(vertices, triangles, 1e-5f, normals, .5f);
            AssertSame(Reference.Build(vertices, triangles, 1e-5f, normals, .5f), refined, "refined from the position weld");
            for (int v = 0; v < vertices.Length; v++)
                Assert.That(position.Weld[refined.Representative[refined.Weld[v]]], Is.EqualTo(position.Weld[v]), "a refined group lies in one position group");
        }

        [Test]
        public void WrongLengthNormalsOrKeysAreRefused()
        {
            var vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var triangles = new[] { 0, 1, 2 };
            Assert.Throws<ArgumentException>(() => MeshNeighbors.Build(vertices, triangles, 1e-5f, new Vector3[2], .5f));
            Assert.Throws<ArgumentException>(() => MeshNeighbors.Build(vertices, triangles, 1e-5f, null, -1f, new long[4]));
            // Empty means none (callers pass an empty array for a mesh without normals).
            Assert.That(MeshNeighbors.Build(vertices, triangles, 1e-5f, new Vector3[0], .5f).WeldedCount, Is.EqualTo(3));
        }
    }
}
