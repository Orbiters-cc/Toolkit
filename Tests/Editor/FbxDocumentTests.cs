using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;

// Binary FBX written by FbxDocument and read back: records, every property type, compressed and plain arrays, both
// header layouts; Euler angles and matrices as FBX stores them.
public sealed class FbxDocumentTests
{
    private static FbxDocument Sample(uint version)
    {
        var document = new FbxDocument { Version = version };
        document.Nodes.Add(new FbxNode("FBXHeaderExtension"));
        document.Nodes[0].Add("FBXHeaderVersion", 1003);
        var objects = new FbxNode("Objects");
        document.Nodes.Add(objects);
        var geometry = objects.Add("Geometry", 42L, "Body\0\u0001Geometry", "Mesh");
        geometry.Add("Vertices", Enumerable.Range(0, 300).Select(i => i * 0.5).ToArray());
        geometry.Add("PolygonVertexIndex", new[] { 0, 1, ~2 });
        geometry.Add("Small", new[] { 1.5, 2.5 });
        geometry.Add("Mixed", (short)3, (byte)1, 7, 1.25f, 2.5, 9L, "text", new byte[] { 1, 2, 3 });
        geometry.Add("Flags", new[] { true, false, true });
        geometry.Add("Floats", new[] { 0.5f, 1.5f });
        geometry.Add("Longs", new[] { 1L, 2L });
        document.Nodes.Add(new FbxNode("Connections"));
        document.Nodes[2].Add("C", "OO", 42L, 0L);
        return document;
    }

    [TestCase(7400u)]
    [TestCase(7500u)]
    public void WrittenFilesReadBack(uint version)
    {
        var bytes = Sample(version).Write();
        Assert.That(FbxReader.IsBinary(bytes), Is.True);
        var read = FbxDocument.Read(bytes);
        Assert.That(read.Version, Is.EqualTo(version));
        Assert.That(read.Nodes.Select(n => n.Name), Is.EqualTo(new[] { "FBXHeaderExtension", "Objects", "Connections" }));
        var geometry = read.Objects()[42L];
        Assert.That(geometry.ObjectName, Is.EqualTo("Body"));
        Assert.That(geometry.Class, Is.EqualTo("Mesh"));
        Assert.That(geometry.Value<double[]>("Vertices"), Is.EqualTo(Enumerable.Range(0, 300).Select(i => i * 0.5).ToArray()), "A compressed array.");
        Assert.That(geometry.Value<double[]>("Small"), Is.EqualTo(new[] { 1.5, 2.5 }), "A plain array.");
        Assert.That(geometry.Value<int[]>("PolygonVertexIndex"), Is.EqualTo(new[] { 0, 1, ~2 }));
        Assert.That(geometry.Child("Mixed").Properties, Is.EqualTo(new object[] { (short)3, (byte)1, 7, 1.25f, 2.5, 9L, "text", new byte[] { 1, 2, 3 } }));
        Assert.That(geometry.Value<bool[]>("Flags"), Is.EqualTo(new[] { true, false, true }));
        Assert.That(geometry.Value<float[]>("Floats"), Is.EqualTo(new[] { 0.5f, 1.5f }));
        Assert.That(geometry.Value<long[]>("Longs"), Is.EqualTo(new[] { 1L, 2L }));
        var connection = read.Connections().Single();
        Assert.That(connection.Child, Is.EqualTo(42L));
        Assert.That(connection.Parent, Is.EqualTo(0L));
    }

    [Test]
    public void EulerAnglesAndMatricesRoundTrip()
    {
        var angles = new Vector3(177.4f, 0.09f, -30.5f);
        var back = FbxTransform.EulerXyz(FbxTransform.Euler(angles, 0));
        Assert.That((back - angles).magnitude, Is.LessThan(1e-3f));
        var matrix = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(10, 20, 30), Vector3.one);
        var values = FbxTransform.ToDoubles(matrix);
        Assert.That(values[12], Is.EqualTo(1).Within(1e-6), "Translation is the fourth column.");
        Assert.That(FbxTransform.FromDoubles(values), Is.EqualTo(matrix));
    }
}
