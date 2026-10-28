using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class MeshBvhTests
    {
        // A unit sphere of welded quads split into triangles, with the UV seam vertices split (two vertices per seam point).
        private static void Sphere(int rings, int segments, out Vector3[] vertices, out int[] triangles)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++) // segments + 1: the seam column is duplicated like a UV seam
                {
                    float theta = 2f * Mathf.PI * s / segments;
                    v.Add(new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta)));
                }
            }
            int w = segments + 1;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * w + s, b = a + 1, c = a + w, d = c + 1;
                    t.AddRange(new[] { a, c, b, b, c, d });
                }
            vertices = v.ToArray();
            triangles = t.ToArray();
        }

        [Test]
        public void NeighborsWeldSeamsAndLinkEdges()
        {
            Sphere(8, 16, out var vertices, out var triangles);
            var n = MeshNeighbors.Build(vertices, triangles, 1e-5f);
            Assert.That(n.WeldedCount, Is.LessThan(vertices.Length), "seam and pole vertices are welded");
            // The seam column (s = 0 and s = 16) shares positions: both map to the same welded vertex.
            Assert.That(n.Weld[1 * 17 + 0], Is.EqualTo(n.Weld[1 * 17 + 16]));
            // An interior vertex has 6 neighbours (two triangles per quad around it).
            int interior = n.Weld[4 * 17 + 5];
            Assert.That(n.Adjacent[interior].Length, Is.EqualTo(6));
            Assert.That(n.TrianglesOf[interior].Length, Is.EqualTo(6));
            foreach (int a in n.Adjacent[interior]) Assert.That(n.Adjacent[a], Does.Contain(interior), "adjacency is symmetric");
            var ring = new HashSet<int>();
            n.Ring(interior, 1, ring);
            Assert.That(ring.Count, Is.EqualTo(7));
        }

        // Double-sided cloth: a front quad and its back copy (same places, normals facing apart) with a hard edge
        // (a second quad at a right angle sharing an edge, its own normals there).
        [Test]
        public void NormalsKeepTouchingSheetsApartButWeldHardEdges()
        {
            var vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0),   // front, normal -Z
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0),   // back, normal +Z
                new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 1), new Vector3(1, 1, 1),   // side, normal +X
            };
            var normals = new Vector3[12];
            for (int i = 0; i < 4; i++) { normals[i] = Vector3.back; normals[4 + i] = Vector3.forward; normals[8 + i] = Vector3.right; }
            var triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 8, 9, 10, 9, 11, 10 };

            var byPlace = MeshNeighbors.Build(vertices, triangles, 1e-5f);
            Assert.That(byPlace.Weld[0], Is.EqualTo(byPlace.Weld[4]), "by place alone the two sides weld");
            var byNormal = MeshNeighbors.Build(vertices, triangles, 1e-5f, normals, -0.5f);
            Assert.That(byNormal.Weld[0], Is.Not.EqualTo(byNormal.Weld[4]), "front and back stay two sheets");
            Assert.That(byNormal.Weld[1], Is.EqualTo(byNormal.Weld[8]), "a hard edge (normals at a right angle) still welds");
            Assert.That(byNormal.WeldedCount, Is.EqualTo(10));
        }

        [Test]
        public void RaycastAndClosestPointFindTheSurface()
        {
            Sphere(24, 48, out var vertices, out var triangles);
            var bvh = MeshBvh.Build(vertices, triangles);
            var hit = bvh.Raycast(new Vector3(0, 0, -5), Vector3.forward, 100f);
            Assert.That(hit.Found, Is.True);
            Assert.That(hit.Position.z, Is.EqualTo(-1f).Within(0.02f), "front of the sphere");
            Assert.That(hit.Distance, Is.EqualTo(4f).Within(0.02f));
            Assert.That(bvh.Raycast(new Vector3(0, 3, -5), Vector3.forward, 100f).Found, Is.False, "a ray that misses");
            var closest = bvh.ClosestPoint(new Vector3(0, 2, 0), 10f);
            Assert.That(closest.Found, Is.True);
            Assert.That(closest.Position.y, Is.EqualTo(1f).Within(0.01f));
            Assert.That(closest.Distance, Is.EqualTo(1f).Within(0.01f));
            Assert.That(bvh.ClosestPoint(new Vector3(0, 2, 0), 0.5f).Found, Is.False, "beyond the maximum distance");
        }

        [Test]
        public void SphereQueryAndPartialRefitFollowMovedVertices()
        {
            Sphere(24, 48, out var vertices, out var triangles);
            var bvh = MeshBvh.Build(vertices, triangles);
            var n = MeshNeighbors.Build(vertices, triangles);
            var inside = new List<int>();
            bvh.VerticesInSphere(new Vector3(0, 1, 0), 0.3f, inside);
            Assert.That(inside.Count, Is.GreaterThan(10));
            Assert.That(inside.Distinct().Count(), Is.EqualTo(inside.Count), "each vertex once");
            foreach (int v in inside) Assert.That(Vector3.Distance(vertices[v], new Vector3(0, 1, 0)), Is.LessThanOrEqualTo(0.3f));

            // Pull the top vertices up: the ray along +Y from far above must now hit the moved surface.
            foreach (int v in inside) vertices[v] += Vector3.up * 0.5f;
            bvh.RefitVertices(inside, n);
            var hit = bvh.Raycast(new Vector3(0, 5, 0), Vector3.down, 100f);
            Assert.That(hit.Found, Is.True);
            Assert.That(hit.Position.y, Is.EqualTo(1.5f).Within(0.02f), "the partial refit covers the moved region");
            bvh.Refit();
            var again = bvh.Raycast(new Vector3(0, 5, 0), Vector3.down, 100f);
            Assert.That(again.Position.y, Is.EqualTo(hit.Position.y).Within(1e-5f), "a full refit agrees");
        }

        [Test]
        public void MeshAssetsSaveKeepsOthersAndReplaceInPlace()
        {
            const string folder = "Assets/Orbiters/ToolkitTests/Meshes/Fixture";
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var saved = MeshAssets.Save(mesh, folder, "Tri:angle");
                Assert.That(AssetDatabase.Contains(saved), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(saved), Is.EqualTo(folder + "/Tri_angle.asset"));
                Assert.That(MeshAssets.IsOwnedBy(saved, "ToolkitTests"), Is.True);
                // Another mesh with the same name (a second avatar's "Body") gets a file of its own.
                var other = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.forward }, triangles = new[] { 0, 1, 2 } };
                var second = MeshAssets.Save(other, folder, "Tri:angle");
                Assert.That(second, Is.Not.SameAs(saved));
                Assert.That(AssetDatabase.GetAssetPath(second), Is.Not.EqualTo(AssetDatabase.GetAssetPath(saved)));
                Assert.That(saved.vertexCount, Is.EqualTo(3), "the first asset is untouched");
                var replacement = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one }, triangles = new[] { 0, 1, 2, 1, 3, 2 } };
                var again = MeshAssets.Replace(replacement, saved);
                Assert.That(again, Is.SameAs(saved), "the existing asset is kept, so references to it stay valid");
                Assert.That(saved.vertexCount, Is.EqualTo(4));
                Assert.That(saved.name, Is.EqualTo("Tri_angle"));
                Assert.That(replacement == null, Is.True, "the replacement was copied in and destroyed");
            }
            finally
            {
                AssetDatabase.DeleteAsset("Assets/Orbiters/ToolkitTests");
            }
        }
    

        // REF-05 (audit 2026-10-28): a collapsed triangle (two corners at one place, all three on a line) gives the nearest
        // point of its edges, never NaN; ReFit's SurfaceBvh has the same closest-point code (its own fix).
        [Test]
        public void CollapsedTrianglesGiveFiniteNearestPoints()
        {
            var q = MeshBvh.ClosestPointOnTriangle(new Vector3(.5f, 1, 0), Vector3.zero, Vector3.zero, new Vector3(1, 0, 0), out var bary);
            Assert.That((q - new Vector3(.5f, 0, 0)).magnitude, Is.LessThan(1e-6f), "the audit's case: " + q);
            Assert.That((bary.x + bary.y) * Vector3.zero + bary.z * new Vector3(1, 0, 0), Is.EqualTo(q));
            q = MeshBvh.ClosestPointOnTriangle(new Vector3(3, 1, 0), Vector3.one, Vector3.one, Vector3.one, out bary);
            Assert.That(q, Is.EqualTo(Vector3.one), "all corners at one place");
            var random = new System.Random(3);
            Vector3 R() => new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
            for (int i = 0; i < 20000; i++)
            {
                Vector3 a = R(), b = R(), p = R() * 2;
                // On the line through a and b, inside or outside the segment.
                var c = i % 2 == 0 ? a + (b - a) * (float)random.NextDouble() * 1.5f : b;
                q = MeshBvh.ClosestPointOnTriangle(p, a, b, c, out bary);
                float best = new[] { Segment(p, a, b), Segment(p, b, c), Segment(p, c, a) }.Min(s => (s - p).magnitude);
                Assert.That(float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z), Is.False, "case " + i);
                Assert.That((q - p).magnitude, Is.EqualTo(best).Within(1e-4f), "case " + i);
                Assert.That((a * bary.x + b * bary.y + c * bary.z - q).magnitude, Is.LessThan(1e-4f), "case " + i);
            }
            // A mesh with a collapsed triangle: the query still finds the real nearest triangle.
            var bvh = MeshBvh.Build(new[] { Vector3.zero, Vector3.zero, Vector3.right, new Vector3(0, 0, 1), new Vector3(1, 0, 1) }, new[] { 0, 1, 2, 2, 3, 4 });
            var hit = bvh.ClosestPoint(new Vector3(.5f, 1, .5f), 10);
            Assert.That(hit.Found && !float.IsNaN(hit.Distance), Is.True);
        }

        private static Vector3 Segment(Vector3 p, Vector3 a, Vector3 b)
        {
            var d = b - a;
            float length = d.sqrMagnitude;
            return length <= 0 ? a : a + d * Mathf.Clamp01(Vector3.Dot(p - a, d) / length);
        }

        // OPT-11: rays search the child they enter first; the hit is still the nearest one (checked against every triangle).
        [Test]
        public void RaycastsFindTheNearestHit()
        {
            Sphere(40, 60, out var vertices, out var triangles);
            var random = new System.Random(5);
            vertices = vertices.Select(v => v * (1 + (float)(random.NextDouble() - .5) * .3f)).ToArray();
            var bvh = MeshBvh.Build(vertices, triangles);
            for (int i = 0; i < 500; i++)
            {
                var origin = new Vector3((float)random.NextDouble() * 6 - 3, (float)random.NextDouble() * 6 - 3, (float)random.NextDouble() * 6 - 3) * (i % 2 == 0 ? 1 : .1f);
                var direction = (new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f) - origin).normalized;
                var hit = bvh.Raycast(origin, direction, 100);
                // Every triangle the ray clearly crosses (away from its edges, where either answer is right).
                float nearest = float.PositiveInfinity;
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                    new Plane(a, b, c).Raycast(new Ray(origin, direction), out float d);
                    if (!(d > 0)) continue;
                    var at = origin + direction * d;
                    var on = MeshBvh.ClosestPointOnTriangle(at, a, b, c, out var w);
                    if ((on - at).magnitude < 1e-5f && w.x > 1e-3f && w.y > 1e-3f && w.z > 1e-3f) nearest = Mathf.Min(nearest, d);
                }
                if (!float.IsPositiveInfinity(nearest)) Assert.That(hit.Found && hit.Distance <= nearest + 1e-4f, Is.True, "ray " + i + ": a nearer triangle was missed");
                if (hit.Found)
                {
                    int t = hit.Triangle * 3;
                    var on = MeshBvh.ClosestPointOnTriangle(hit.Position, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]], out _);
                    Assert.That((on - hit.Position).magnitude, Is.LessThan(1e-4f), "ray " + i + ": the hit is on its triangle");
                }
            }
        }

        // OPT-02: welding by normal compares a vertex with a group's distinct normals: many coincident vertices facing alike
        // (a pile of collapsed vertices) stay fast, and the groups are the same as comparing with every member.
        [Test]
        public void ManyCoincidentVerticesWeldQuickly()
        {
            var points = new Vector3[8000];
            var normals = Enumerable.Repeat(Vector3.up, points.Length).ToArray();
            normals[100] = Vector3.down;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var neighbors = MeshNeighbors.Build(points, System.Array.Empty<int>(), normals: normals, minNormalDot: .5f);
            watch.Stop();
            Assert.That(neighbors.WeldedCount, Is.EqualTo(2), "the one facing down stays apart");
            Assert.That(neighbors.Weld[100], Is.Not.EqualTo(neighbors.Weld[0]));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(50), "was ~100 ms (8000 x 8000 / 2 comparisons)");
        }
    }
}
