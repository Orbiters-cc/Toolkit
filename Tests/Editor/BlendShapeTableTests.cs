using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class BlendShapeTableTests
    {
        private const int VertexCount = 8;

        // Shapes of every kind Unity's evaluation tells apart: one frame with normals and tangents, three in-between frames
        // moving different vertices, frames below zero, and a lone frame at weight zero.
        private static Mesh MeshWithShapes()
        {
            var mesh = new Mesh { name = "BlendShapeTableTests" };
            var vertices = new Vector3[VertexCount];
            for (int i = 0; i < VertexCount; i++) vertices[i] = new Vector3(i, i * 0.5f, -i);
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2, 2, 3, 4, 4, 5, 6, 5, 6, 7 };
            mesh.RecalculateNormals();

            Vector3[] D(params (int v, Vector3 d)[] moves)
            {
                var dense = new Vector3[VertexCount];
                foreach (var (v, d) in moves) dense[v] = d;
                return dense;
            }
            mesh.AddBlendShapeFrame("single", 100f, D((1, new Vector3(0.1f, 0.2f, 0.3f)), (6, new Vector3(-1f, 0f, 2f))),
                D((1, new Vector3(0f, 0.5f, 0f)), (3, new Vector3(0.25f, 0f, 0f))), D((7, new Vector3(0f, 0f, 1f))));
            mesh.AddBlendShapeFrame("multi", 20f, D((0, new Vector3(1f, 0f, 0f)), (2, new Vector3(0f, 1f, 0f))), null, null);
            mesh.AddBlendShapeFrame("multi", 60f, D((2, new Vector3(0f, 3f, 0f)), (4, new Vector3(0f, 0f, 2f))), null, null);
            mesh.AddBlendShapeFrame("multi", 100f, D((0, new Vector3(2f, 0f, 0f)), (5, new Vector3(0.5f, 0.5f, 0.5f))), null, null);
            mesh.AddBlendShapeFrame("negative", -50f, D((3, new Vector3(0f, -1f, 0f))), null, null);
            mesh.AddBlendShapeFrame("negative", 50f, D((3, new Vector3(0f, 1f, 0f)), (4, new Vector3(1f, 1f, 1f))), null, null);
            mesh.AddBlendShapeFrame("zero", 0f, D((7, new Vector3(0.3f, 0f, 0f))), null, null);
            return mesh;
        }

        private static void AssertSameShapes(Mesh expected, Mesh actual)
        {
            Assert.That(actual.blendShapeCount, Is.EqualTo(expected.blendShapeCount));
            int n = expected.vertexCount;
            Vector3[] ev = new Vector3[n], en = new Vector3[n], et = new Vector3[n], av = new Vector3[n], an = new Vector3[n], at = new Vector3[n];
            for (int s = 0; s < expected.blendShapeCount; s++)
            {
                Assert.That(actual.GetBlendShapeName(s), Is.EqualTo(expected.GetBlendShapeName(s)));
                Assert.That(actual.GetBlendShapeFrameCount(s), Is.EqualTo(expected.GetBlendShapeFrameCount(s)));
                for (int f = 0; f < expected.GetBlendShapeFrameCount(s); f++)
                {
                    Assert.That(actual.GetBlendShapeFrameWeight(s, f), Is.EqualTo(expected.GetBlendShapeFrameWeight(s, f)));
                    expected.GetBlendShapeFrameVertices(s, f, ev, en, et);
                    actual.GetBlendShapeFrameVertices(s, f, av, an, at);
                    Assert.That(av, Is.EqualTo(ev), "offsets of " + expected.GetBlendShapeName(s) + " frame " + f);
                    Assert.That(an, Is.EqualTo(en), "normal offsets of " + expected.GetBlendShapeName(s) + " frame " + f);
                    Assert.That(at, Is.EqualTo(et), "tangent offsets of " + expected.GetBlendShapeName(s) + " frame " + f);
                }
            }
        }

        [Test]
        public void ReadThenWriteGivesTheSameFrames()
        {
            var mesh = MeshWithShapes();
            var copy = Object.Instantiate(mesh);
            try
            {
                var table = BlendShapeTable.Read(mesh);
                Assert.That(table.ShapeCount, Is.EqualTo(4));
                Assert.That(table.IndexOf("negative"), Is.EqualTo(2));
                Assert.That(table.HasNormals && table.HasTangents, Is.True);
                // Sparse: only moved vertices, ascending.
                var single = table.Shapes[0].Frames[0];
                Assert.That(single.Indices.ToArray(), Is.EqualTo(new[] { 1, 3, 6, 7 }));
                Assert.That(table.Entries, Is.LessThan(8L * VertexCount));

                copy.ClearBlendShapes();
                table.WriteTo(copy);
                AssertSameShapes(mesh, copy);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void AddToMatchesTheDenseEvaluation([Values(true, false)] bool clamp)
        {
            var mesh = MeshWithShapes();
            try
            {
                var table = BlendShapeTable.Read(mesh);
                var scratch = new BlendShapeEvaluation.Scratch(VertexCount);
                foreach (float weight in new[] { -80f, -50f, -10f, 0f, 5f, 20f, 35f, 60f, 80f, 100f, 140f })
                    for (int s = 0; s < mesh.blendShapeCount; s++)
                    {
                        var denseV = new Vector3[VertexCount];
                        var denseN = new Vector3[VertexCount];
                        var sparseV = new Vector3[VertexCount];
                        var sparseN = new Vector3[VertexCount];
                        for (int i = 0; i < VertexCount; i++) denseV[i] = sparseV[i] = new Vector3(i, 1f, 2f);
                        BlendShapeEvaluation.Add(mesh, s, weight, clamp, denseV, denseN, scratch);
                        table.AddTo(s, weight, clamp, sparseV, sparseN);
                        Assert.That(sparseV, Is.EqualTo(denseV), "positions of shape " + s + " at " + weight);
                        Assert.That(sparseN, Is.EqualTo(denseN), "normals of shape " + s + " at " + weight);
                    }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void RemapMatchesADenseRemap()
        {
            var mesh = MeshWithShapes();
            try
            {
                var table = BlendShapeTable.Read(mesh);
                // Reversed, vertex 2 dropped, vertices 6 and 7 welded into one (6, the lower, keeps its offsets).
                var oldToNew = new[] { 6, 5, -1, 4, 3, 2, 1, 1 };
                var remapped = table.Remap(oldToNew, 7);
                Assert.That(remapped.VertexCount, Is.EqualTo(7));
                var dense = new Vector3[VertexCount];
                var denseNormals = new Vector3[VertexCount];
                var got = new Vector3[7];
                var gotNormals = new Vector3[7];
                for (int s = 0; s < table.ShapeCount; s++)
                    for (int f = 0; f < table.Shapes[s].FrameCount; f++)
                    {
                        table.Densify(s, f, dense, denseNormals);
                        remapped.Densify(s, f, got, gotNormals);
                        var expected = new Vector3[7];
                        var expectedNormals = new Vector3[7];
                        for (int v = VertexCount - 1; v >= 0; v--)
                        {
                            if (oldToNew[v] < 0) continue;
                            expected[oldToNew[v]] = dense[v];
                            expectedNormals[oldToNew[v]] = denseNormals[v];
                        }
                        Assert.That(got, Is.EqualTo(expected), "shape " + s + " frame " + f);
                        Assert.That(gotNormals, Is.EqualTo(expectedNormals), "normals of shape " + s + " frame " + f);
                        var indices = remapped.Shapes[s].Frames[f].Indices;
                        for (int k = 1; k < indices.Length; k++) Assert.That(indices[k], Is.GreaterThan(indices[k - 1]));
                    }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MotionSignaturesTellMovesApart()
        {
            var frames = new[]
            {
                new BlendShapeTable.Frame(100f, new[] { 0, 1, 2 }, new[] { Vector3.up, Vector3.up, Vector3.right })
            };
            var table = new BlendShapeTable(4, new[] { new BlendShapeTable.Shape("a", frames) });
            var signatures = table.MotionSignatures();
            Assert.That(signatures[0], Is.EqualTo(signatures[1]), "moved alike");
            Assert.That(signatures[0], Is.Not.EqualTo(signatures[2]), "moved differently");
            Assert.That(signatures[3], Is.EqualTo(0UL), "not moved");
        }

        // Review: the lowest old vertex is the source even where a frame leaves it still (its zero wins over another's move).
        [Test]
        public void RemapKeepsTheLowestVertexEvenWhenItIsStill()
        {
            var moving = new BlendShapeTable.Frame(100f, new[] { 1 }, new[] { Vector3.up });
            var both = new BlendShapeTable.Frame(50f, new[] { 0, 1 }, new[] { Vector3.right, Vector3.up });
            var table = new BlendShapeTable(2, new[] { new BlendShapeTable.Shape("a", new[] { both, moving }) });
            var remapped = table.Remap(new[] { 0, 0 }, 1);
            var dense = new Vector3[1];
            remapped.Densify(0, 0, dense);
            Assert.That(dense[0], Is.EqualTo(Vector3.right), "vertex 0's offset, not vertex 1's");
            remapped.Densify(0, 1, dense);
            Assert.That(dense[0], Is.EqualTo(Vector3.zero), "vertex 0 is still in this frame, so the new vertex is too");
            Assert.That(remapped.Shapes[0].Frames[1].Count, Is.EqualTo(0));
        }

        [Test]
        public void FramesRejectUnsortedIndices()
        {
            Assert.Throws<System.ArgumentException>(() => new BlendShapeTable.Frame(100f, new[] { 2, 1 }, new[] { Vector3.up, Vector3.up }));
        }
    }
}
