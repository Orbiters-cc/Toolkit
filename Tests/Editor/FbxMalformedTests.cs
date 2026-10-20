using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;

// Damaged or hostile binary FBX files are refused with an InvalidDataException by both readers: never a loop that eats the
// memory, a stack overflow or a giant allocation. Models whose connections loop or that have two parents are built once.
public sealed class FbxMalformedTests
{
    private static byte[] Sample(uint version = 7400, Action<FbxNode> geometryExtra = null)
    {
        var document = new FbxDocument { Version = version };
        document.Nodes.Add(new FbxNode("FBXHeaderExtension"));
        document.Nodes[0].Add("FBXHeaderVersion", 1003);
        var objects = new FbxNode("Objects");
        document.Nodes.Add(objects);
        var geometry = objects.Add("Geometry", 42L, "Body\0\u0001Geometry", "Mesh");
        geometry.Add("Vertices", Enumerable.Range(0, 300).Select(i => i * 0.5).ToArray());
        geometry.Add("PolygonVertexIndex", new[] { 0, 1, ~2 });
        geometryExtra?.Invoke(geometry);
        document.Nodes.Add(new FbxNode("Connections"));
        document.Nodes[2].Add("C", "OO", 42L, 0L);
        return document.Write();
    }

    private static void BothRefuse(byte[] bytes, string why)
    {
        Assert.Throws<InvalidDataException>(() => FbxDocument.Read(bytes), why + " (FbxDocument)");
        Assert.Throws<InvalidDataException>(() => FbxReader.Read(bytes), why + " (FbxReader)");
    }

    private static int IndexOf(byte[] bytes, string text)
    {
        var pattern = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i + pattern.Length <= bytes.Length; i++)
            if (bytes.Skip(i).Take(pattern.Length).SequenceEqual(pattern)) return i;
        throw new InvalidOperationException(text + " not found");
    }

    [Test]
    public void TheSampleReadsWell()
    {
        Assert.That(FbxDocument.Read(Sample()).Nodes.Count, Is.EqualTo(3));
        Assert.That(FbxDocument.Read(Sample(7500)).Nodes.Count, Is.EqualTo(3));
        Assert.That(FbxReader.Read(Sample()).Single().Points.Length, Is.EqualTo(100));
    }

    [TestCase(7400u)]
    [TestCase(7500u)]
    public void ARecordEndingBeforeItStartsIsRefused(uint version)
    {
        var bytes = Sample(version);
        // The first record's end offset, right after the 27-byte file header: pointed back at the header.
        if (version >= 7500) BitConverter.GetBytes(10UL).CopyTo(bytes, 27);
        else BitConverter.GetBytes(10U).CopyTo(bytes, 27);
        BothRefuse(bytes, "an end offset before the record");

        bytes = Sample(version);
        if (version >= 7500) BitConverter.GetBytes((ulong)bytes.Length + 100).CopyTo(bytes, 27);
        else BitConverter.GetBytes((uint)bytes.Length + 100).CopyTo(bytes, 27);
        BothRefuse(bytes, "an end offset past the file");
    }

    [Test]
    public void NestingDeeperThanFilesAreMadeIsRefused()
    {
        var document = new FbxDocument { Version = 7400 };
        var node = new FbxNode("Deep", 1);
        document.Nodes.Add(node);
        for (int i = 0; i < 200; i++) node = node.Add("Deep", i);
        BothRefuse(document.Write(), "200 levels of records");
    }

    [Test]
    public void ArraysAnnouncingMoreThanTheyHoldAreRefused()
    {
        // Vertices: the name, then the property's type 'd' and its length.
        var bytes = Sample();
        int length = IndexOf(bytes, "Vertices") + "Vertices".Length + 1;
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, length);
        BothRefuse(bytes, "two billion doubles announced (a zlib bomb's header)");

        bytes = Sample();
        BitConverter.GetBytes(301).CopyTo(bytes, length);
        BothRefuse(bytes, "one double more than the compressed data holds");

        bytes = Sample();
        BitConverter.GetBytes(-1).CopyTo(bytes, length);
        BothRefuse(bytes, "a negative length");

        // PolygonVertexIndex is small, so stored plainly: its stored size must be its length's.
        bytes = Sample();
        int plain = IndexOf(bytes, "PolygonVertexIndex") + "PolygonVertexIndex".Length + 1;
        BitConverter.GetBytes(1000).CopyTo(bytes, plain);
        BothRefuse(bytes, "a plain array announcing more than it stores");
    }

    [Test]
    public void ATruncatedFileIsRefused()
    {
        var bytes = Sample();
        int cut = IndexOf(bytes, "PolygonVertexIndex");
        BothRefuse(bytes.Take(cut + 4).ToArray(), "a file cut inside a record");
    }

    // A model connected under two parents, and connections that loop, give each model once.
    [Test]
    public void ModelsWithLoopsOrTwoParentsAreBuiltOnce()
    {
        var document = new FbxDocument { Version = 7400 };
        var objects = new FbxNode("Objects");
        document.Nodes.Add(objects);
        objects.Add("Model", 1L, "A\0\u0001Model", "Null");
        objects.Add("Model", 2L, "B\0\u0001Model", "Null");
        objects.Add("Model", 3L, "C\0\u0001Model", "Null");
        var connections = new FbxNode("Connections");
        document.Nodes.Add(connections);
        connections.Add("C", "OO", 1L, 0L);
        connections.Add("C", "OO", 2L, 1L);
        connections.Add("C", "OO", 1L, 2L);
        connections.Add("C", "OO", 3L, 1L);
        connections.Add("C", "OO", 3L, 2L);
        GameObject root = null;
        try
        {
            root = FbxModel.Load(FbxDocument.Read(document.Write()), "Loops");
            var names = root.GetComponentsInChildren<Transform>(true).Skip(1).Select(t => t.name).ToList();
            Assert.That(names.OrderBy(n => n), Is.EqualTo(new[] { "A", "B", "C" }), "each model once");
            Assert.That(root.transform.Find("A/B"), Is.Not.Null);
            Assert.That(root.transform.Find("A/C"), Is.Not.Null, "C goes under the first parent the file gives it");
        }
        finally
        {
            FbxModel.Release(root);
        }
    }

    // A skin none of whose clusters has a bone in the file leaves the mesh unskinned instead of failing.
    [Test]
    public void ASkinWithoutBonesLoadsAsAPlainMesh()
    {
        var document = new FbxDocument { Version = 7400 };
        var objects = new FbxNode("Objects");
        document.Nodes.Add(objects);
        objects.Add("Model", 1L, "Body\0\u0001Model", "Mesh");
        var geometry = objects.Add("Geometry", 2L, "Body\0\u0001Geometry", "Mesh");
        geometry.Add("Vertices", new[] { 0d, 0, 0, 1, 0, 0, 0, 1, 0 });
        geometry.Add("PolygonVertexIndex", new[] { 0, 1, ~2 });
        objects.Add("Deformer", 3L, "Skin\0\u0001Deformer", "Skin");
        objects.Add("Deformer", 4L, "Cluster\0\u0001SubDeformer", "Cluster");
        var connections = new FbxNode("Connections");
        document.Nodes.Add(connections);
        connections.Add("C", "OO", 1L, 0L);
        connections.Add("C", "OO", 2L, 1L);
        connections.Add("C", "OO", 3L, 2L);
        connections.Add("C", "OO", 4L, 3L);
        GameObject root = null;
        try
        {
            root = FbxModel.Load(FbxDocument.Read(document.Write()), "Unskinned");
            Assert.That(root.GetComponentInChildren<SkinnedMeshRenderer>(true), Is.Null);
            var filter = root.GetComponentInChildren<MeshFilter>(true);
            Assert.That(filter != null && filter.sharedMesh != null && filter.sharedMesh.vertexCount == 3, Is.True);
        }
        finally
        {
            FbxModel.Release(root);
        }
    }

    // A material index of two billion puts the polygon in the first slot instead of making two billion slots.
    [Test]
    public void AHugeMaterialIndexIsNotASlotCount()
    {
        var bytes = Sample(geometryExtra: geometry =>
        {
            var layer = geometry.Add("LayerElementMaterial", 0);
            layer.Add("MappingInformationType", "ByPolygon");
            layer.Add("Materials", new[] { 2000000000 });
        });
        var mesh = FbxReader.Read(bytes, FbxReadOptions.Render).Single();
        Assert.That(mesh.SubMeshes.Count, Is.EqualTo(1));
        Assert.That(mesh.SubMeshes[0].Length, Is.EqualTo(3));
    }

    [Test]
    public void RecordNamesLongerThanAByteAreNotWritten()
    {
        var document = new FbxDocument { Version = 7400 };
        document.Nodes.Add(new FbxNode(new string('N', 256), 1));
        Assert.Throws<InvalidDataException>(() => document.Write());
    }
}
