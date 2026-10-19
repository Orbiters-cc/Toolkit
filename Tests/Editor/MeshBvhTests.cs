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
    }
}
