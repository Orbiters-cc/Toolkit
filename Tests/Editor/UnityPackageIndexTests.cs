using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;

public sealed class UnityPackageIndexTests
{
    private string file;

    [SetUp] public void SetUp() => file = Path.Combine(Path.GetTempPath(), "orbiters-upi-" + Guid.NewGuid().ToString("N") + ".unitypackage");
    [TearDown] public void TearDown() { if (File.Exists(file)) File.Delete(file); }

    [Test] public void ListsPathsCodeAndKeptContent()
    {
        Write(("g1/pathname", Encoding.UTF8.GetBytes("Assets/Hat/Hat.prefab")), ("g1/asset", new byte[10]),
              ("g2/pathname", Encoding.UTF8.GetBytes("Assets/Hat/Editor/Setup.cs\n")), ("g2/asset", Encoding.UTF8.GetBytes("class A {}")),
              ("g3/asset", Encoding.UTF8.GetBytes("Read me")), ("g3/pathname", Encoding.UTF8.GetBytes("Assets/Hat/README.txt")));
        var index = UnityPackageIndex.Read(file, p => p.EndsWith(".txt"));
        CollectionAssert.AreEquivalent(new[] { "Assets/Hat/Hat.prefab", "Assets/Hat/Editor/Setup.cs", "Assets/Hat/README.txt" }, index.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { "Assets/Hat/Editor/Setup.cs" }, index.CodeFiles);
        Assert.AreEqual("Read me", Encoding.UTF8.GetString(index.Entries.Single(e => e.Path.EndsWith(".txt")).Content));
        Assert.IsNull(index.Entries.Single(e => e.Path.EndsWith(".cs")).Content, "only the requested content is kept");
    }

    // A header may declare a huge entry that the file does not contain: it must fail before allocating it.
    [Test] public void HugeDeclaredNameIsRejectedWithoutAllocating()
    {
        using (var gz = new GZipStream(File.Create(file), CompressionMode.Compress))
        {
            var header = Header("g1/pathname", 1073741823L);
            gz.Write(header, 0, 512);
        }
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [Test] public void ExpandedSizeIsBounded()
    {
        Write(("g1/pathname", Encoding.UTF8.GetBytes("Assets/a.bin")), ("g1/asset", new byte[4096]));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file, maxExpandedBytes: 2048));
    }

    [TestCase("Assets/a/b.prefab", true)]
    [TestCase("Packages/com.x/a.cs", true)]
    [TestCase("Assets/../../outside.txt", false)]
    [TestCase("C:/Windows/evil.dll", false)]
    [TestCase("/etc/passwd", false)]
    [TestCase("ProjectSettings/x.asset", false)]
    public void ProjectPaths(string path, bool expected) => Assert.AreEqual(expected, UnityPackageIndex.IsProjectPath(path));

    [TestCase("Assets/A.cs", true)]
    [TestCase("Assets/Plugins/x.dll", true)]
    [TestCase("Assets/My.asmdef", true)]
    [TestCase("Assets/csc.rsp", true)]
    [TestCase("Assets/Plugins/Mac.bundle/Contents/Info.plist", true)]
    [TestCase("Assets/Hat.prefab", false)]
    [TestCase("Assets/model.bin", false)]
    public void CodeDetection(string path, bool expected) => Assert.AreEqual(expected, CodeContent.IsCode(path));

    private void Write(params (string name, byte[] data)[] entries)
    {
        using (var gz = new GZipStream(File.Create(file), CompressionMode.Compress))
        {
            foreach (var (name, data) in entries)
            {
                gz.Write(Header(name, data.Length), 0, 512);
                gz.Write(data, 0, data.Length);
                int pad = (512 - data.Length % 512) % 512;
                gz.Write(new byte[pad], 0, pad);
            }
            gz.Write(new byte[1024], 0, 1024);
        }
    }

    private static byte[] Header(string name, long size)
    {
        name = name.Replace("g1/", "11111111111111111111111111111111/").Replace("g2/", "22222222222222222222222222222222/").Replace("g3/", "33333333333333333333333333333333/");
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(header, 124);
        header[156] = (byte)'0';
        for (int i = 148; i < 156; i++) header[i] = 32;
        Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }
}
