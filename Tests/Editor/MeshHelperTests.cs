using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class MeshHelperTests
    {
        // Three vertices at one place: B 110° from A one way, C 110° the other way, so B and C are 140° apart. Each agrees
        // with A within the limit (120°), but B and C do not with each other: they must not end up welded through A.
        [Test]
        public void NormalGatedWeldsAgreeWithEveryMember()
        {
            var vertices = new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.right, Vector3.up };
            Vector3 Turn(float degrees) => Quaternion.AngleAxis(degrees, Vector3.forward) * Vector3.right;
            var normals = new[] { Turn(0), Turn(110), Turn(-110), Vector3.back, Vector3.back };
            var triangles = new[] { 0, 3, 4, 1, 3, 4, 2, 3, 4 };
            foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 1, 0, 2 }, new[] { 2, 1, 0 } })
            {
                var v = order.Select(i => vertices[i]).Concat(vertices.Skip(3)).ToArray();
                var n = order.Select(i => normals[i]).Concat(normals.Skip(3)).ToArray();
                var neighbors = MeshNeighbors.Build(v, triangles, 1e-5f, n, -0.5f);
                int b = Array.IndexOf(order, 1), c = Array.IndexOf(order, 2);
                Assert.That(neighbors.Weld[b], Is.Not.EqualTo(neighbors.Weld[c]), "B and C stay apart whatever the order: " + string.Join(",", order));
            }
        }

        // A mesh under a mirroring transform (negative scale) has its front faces wound the other way in world space:
        // a ray without back faces still hits the face it sees, and the face normal still points at it.
        [Test]
        public void MirroredMeshesAreHitOnTheirFront()
        {
            var front = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, -1, 0) };
            var triangles = new[] { 0, 1, 2 };
            var bvh = MeshBvh.Build(front, triangles);
            var plain = bvh.Raycast(new Vector3(-0.5f, 0, -5), Vector3.forward, 10f, backfaces: false);
            Assert.That(plain.Found, Is.True, "the face is seen from -Z");
            Assert.That(Vector3.Dot(bvh.FaceNormal(plain.Triangle), Vector3.back), Is.GreaterThan(0.9f));

            var mirror = Matrix4x4.Scale(new Vector3(-1, 1, 1));
            var mirrored = MeshBvh.Build(front.Select(p => mirror.MultiplyPoint3x4(p)).ToArray(), triangles);
            Assert.That(mirrored.Raycast(new Vector3(0.5f, 0, -5), Vector3.forward, 10f, backfaces: false).Found, Is.False,
                "without knowing it is mirrored, the seen side counts as the back");
            mirrored.Mirrored = true;
            var hit = mirrored.Raycast(new Vector3(0.5f, 0, -5), Vector3.forward, 10f, backfaces: false);
            Assert.That(hit.Found, Is.True);
            Assert.That(Vector3.Dot(mirrored.FaceNormal(hit.Triangle), Vector3.back), Is.GreaterThan(0.9f), "the normal faces the viewer");
        }

        // A hard edge (two faces at a right angle, their vertices split at the edge) stays hard; the tangents follow.
        [Test]
        public void RecalculatedNormalsKeepHardEdgesAndTurnTangents()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 0, 0),   // floor of the edge, normal -Z
                    new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1),   // wall, normal +X... at the same edge
                },
                triangles = new[] { 0, 1, 2, 3, 5, 4 },
            };
            mesh.RecalculateNormals();
            var before = mesh.normals;
            mesh.tangents = Enumerable.Repeat(new Vector4(0.7f, 0.7f, 0f, -1f), 6).ToArray();
            try
            {
                MeshNormals.Recalculate(mesh, new[] { 0, 1, 2, 3, 4, 5 });
                var after = mesh.normals;
                for (int i = 0; i < 6; i++)
                    Assert.That(Vector3.Dot(after[i], before[i]), Is.GreaterThan(0.99f), "vertex " + i + " keeps its own face's normal");
                var tangents = mesh.tangents;
                for (int i = 0; i < 6; i++)
                {
                    Assert.That(Mathf.Abs(Vector3.Dot(tangents[i], after[i])), Is.LessThan(1e-4f), "tangent " + i + " is square to the normal");
                    Assert.That(tangents[i].w, Is.EqualTo(-1f), "and keeps its sign");
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshFileNamesAreAlwaysUsable()
        {
            Assert.That(MeshAssets.Clean("CON"), Is.EqualTo("_CON"));
            Assert.That(MeshAssets.Clean(".."), Is.EqualTo("Mesh"));
            Assert.That(MeshAssets.Clean(" Body. "), Is.EqualTo("Body"));
            Assert.That(MeshAssets.Clean("a/b:c"), Is.EqualTo("a_b_c"));
        }

        // Replacing a skinned mesh asset in place gives it the new mesh whole: vertices, skin, bind poses, shapes and parts
        // (Unity's CopySerialized kept a skinned mesh's old vertices).
        [Test]
        public void ReplacingASkinnedMeshAssetGivesItTheNewMeshWhole()
        {
            string folder = "Assets/Orbiters/ToolkitTests-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/Meshes";
            Mesh Skinned(float k, int bones)
            {
                var m = new Mesh { vertices = new[] { Vector3.one * k, Vector3.right * k, Vector3.up * k, Vector3.forward * k } };
                m.subMeshCount = 2;
                m.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
                m.SetIndices(new[] { 0, 2, 3 }, MeshTopology.Triangles, 1);
                m.SetUVs(1, new[] { new Vector3(k, 0, 1), Vector3.zero, Vector3.zero, Vector3.zero }.ToList());
                m.boneWeights = Enumerable.Range(0, 4).Select(i => new BoneWeight { boneIndex0 = i % bones, weight0 = 1 }).ToArray();
                m.bindposes = Enumerable.Range(0, bones).Select(i => Matrix4x4.Translate(Vector3.one * (k + i))).ToArray();
                m.AddBlendShapeFrame("Shape " + k, 100, Enumerable.Repeat(Vector3.up * k, 4).ToArray(), null, null);
                m.RecalculateNormals();
                return m;
            }
            try
            {
                var asset = MeshAssets.Save(Skinned(1, 1), folder, "Body");
                var replaced = MeshAssets.Replace(Skinned(5, 2), asset);
                Assert.That(replaced, Is.SameAs(asset));
                Assert.That(asset.name, Is.EqualTo("Body"));
                Assert.That(asset.vertices[0], Is.EqualTo(Vector3.one * 5));
                Assert.That(asset.bindposes.Length, Is.EqualTo(2));
                Assert.That(asset.boneWeights[1].boneIndex0, Is.EqualTo(1));
                Assert.That(asset.subMeshCount, Is.EqualTo(2));
                Assert.That(asset.GetIndices(1), Is.EqualTo(new[] { 0, 2, 3 }));
                Assert.That(asset.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord1), Is.EqualTo(3));
                Assert.That(asset.blendShapeCount, Is.EqualTo(1));
                Assert.That(asset.GetBlendShapeName(0), Is.EqualTo("Shape 5"));
                var deltas = new Vector3[4];
                asset.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
                Assert.That(deltas[2], Is.EqualTo(Vector3.up * 5));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder.Substring(0, folder.LastIndexOf('/')));
            }
        }

        // Saving a mesh that is an asset already gives a new asset (a copy) and leaves the first one where it is.
        [Test]
        public void SavingAnAssetMeshMakesACopy()
        {
            string folder = "Assets/Orbiters/ToolkitTests-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/Meshes";
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var saved = MeshAssets.Save(mesh, folder, "Body");
                var again = MeshAssets.Save(saved, folder, "Body");
                Assert.That(again, Is.Not.SameAs(saved));
                Assert.That(AssetDatabase.GetAssetPath(saved), Is.EqualTo(folder + "/Body.asset"));
                Assert.That(AssetDatabase.GetAssetPath(again), Is.Not.EqualTo(AssetDatabase.GetAssetPath(saved)));
                var loose = new Mesh();
                Assert.That(MeshAssets.IsOwnedBy(loose, "ToolkitTests"), Is.False);
                Object.DestroyImmediate(loose);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder.Substring(0, folder.LastIndexOf('/')));
            }
        }
    }
}
