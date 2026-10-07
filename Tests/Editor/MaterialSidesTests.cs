using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class MaterialSidesTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test]
        public void ACardIsOpenAndAClosedBoxIsNot()
        {
            // Two triangles: four of the five edges border one triangle only.
            var card = Mesh(new[] { Vector3.zero, Vector3.up, Vector3.right, new Vector3(1, 1, 0) }, new[] { 0, 1, 2, 2, 1, 3 });
            Assert.AreEqual(.8f, MaterialSides.OpenRatio(card, 0), 1e-4f);
            Assert.True(MaterialSides.IsOpen(card, 0));

            // A box with its corners split per face, as normals and UVs split them: welded, every edge has two triangles.
            Assert.AreEqual(0f, MaterialSides.OpenRatio(Box(), 0), 1e-4f);
            Assert.False(MaterialSides.IsOpen(Box(), 0));
        }

        [Test]
        public void CullingIsReadAndTurnedOffOnTheShadersProperty()
        {
            var standard = Material("Standard");
            Assert.True(MaterialSides.IsStandard(standard));
            Assert.AreEqual(false, MaterialSides.ShowsBackFaces(standard));
            Assert.False(MaterialSides.CanShowBackFaces(standard), "Standard has no culling to change");

            var toon = Material("VRChat/Mobile/Toon Standard");
            Assert.AreEqual(false, MaterialSides.ShowsBackFaces(toon), "Toon Standard culls back faces by default");
            Assert.True(MaterialSides.CanShowBackFaces(toon));
            MaterialSides.ShowBackFaces(toon);
            Assert.AreEqual(0f, toon.GetFloat("_Culling"));
            Assert.AreEqual(true, MaterialSides.ShowsBackFaces(toon));
        }

        private Material Material(string shader)
        {
            var material = new Material(Shader.Find(shader)); owned.Add(material);
            return material;
        }

        private Mesh Mesh(Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { vertices = vertices, triangles = triangles }; owned.Add(mesh);
            return mesh;
        }

        private Mesh Box()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var normal in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                var u = Vector3.Cross(normal, Mathf.Abs(normal.y) > .5f ? Vector3.forward : Vector3.up);
                var v = Vector3.Cross(normal, u);
                int first = vertices.Count;
                vertices.Add((normal - u - v) * .5f); vertices.Add((normal + u - v) * .5f);
                vertices.Add((normal + u + v) * .5f); vertices.Add((normal - u + v) * .5f);
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            return Mesh(vertices.ToArray(), triangles.ToArray());
        }
    }
}
