using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;

public sealed class UnityPackageSecurityTests
{
    private const string A = "11111111111111111111111111111111", B = "22222222222222222222222222222222";
    private string file;
    [SetUp] public void SetUp() => file = Path.Combine(Path.GetTempPath(), "orbiters-security-" + Guid.NewGuid().ToString("N") + ".unitypackage");
    [TearDown] public void TearDown() { if (File.Exists(file)) File.Delete(file); }

    [Test] public void EveryTruncationOfAValidSmallArchiveFailsClosed()
    {
        var archive = Tar((A + "/pathname", Bytes("Assets/Editor/Setup.cs")), (A + "/asset", Bytes("class Setup {}")));
        for (int length = 0; length < archive.Length; length++)
        {
            Compress(archive.Take(length).ToArray());
            Assert.That(() => UnityPackageIndex.Read(file), Throws.InstanceOf<IOException>().Or.InstanceOf<InvalidDataException>(), "Truncation at byte " + length);
        }
    }

    [Test] public void CorruptChecksumPaddingAndHiddenEntriesAreRejected()
    {
        var archive = Tar((A + "/pathname", Bytes("Assets/a.txt")));
        archive[10] ^= 1; Compress(archive);
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
        archive = Tar((A + "/pathname", Bytes("Assets/a.txt"))); archive[1023] = 1; Compress(archive);
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
        Compress(Tar((A + "/pathname", Bytes("Assets/a.txt"))).Concat(Tar((B + "/pathname", Bytes("Assets/hidden.cs")))).ToArray());
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [TestCase("Assets/evil.cs\nAssets/safe.txt")][TestCase("Assets/evil.cs\0.txt")]
    [TestCase("Assets/evil.cs\n00")][TestCase("Assets/evil.cs\r\r\n")]
    public void AmbiguousPathnameBytesAreRejected(string path)
    {
        Write((A + "/pathname", Bytes(path)));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [Test] public void InvalidUtf8PathnameIsRejected()
    {
        Write((A + "/pathname", new byte[] { 0xc0, 0xaf }));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [Test] public void DuplicateRecordsAndCaseAliasedTargetsAreRejected()
    {
        Write((A + "/pathname", Bytes("Assets/evil.cs")), (A + "/pathname", Bytes("Assets/safe.txt")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
        Write((A + "/pathname", Bytes("Assets/a.cs")), (B + "/pathname", Bytes("Assets/A.cs")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
        Write((A + "/pathname", Bytes("Assets/a.cs")), (A + "/asset", Bytes("one")), (A + "/asset", Bytes("two")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [TestCase('x')][TestCase('g')][TestCase('1')][TestCase('2')][TestCase('K')]
    public void TarLinksAndExtensionOverridesAreRejected(char type)
    {
        Compress(Header(A + "/pathname", 0, type).Concat(new byte[1024]).ToArray());
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [TestCase("../pathname")][TestCase("x/pathname")][TestCase("11111111111111111111111111111111/pathname/")]
    [TestCase("11111111111111111111111111111111/../pathname")]
    public void AmbiguousRecordNamesAreRejected(string name)
    {
        Write((name, Bytes("Assets/evil.cs")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [Test] public void MissingAndMismatchedMetadataGuidsAreRejected()
    {
        foreach (string meta in new[] { "fileFormatVersion: 2\n", "guid: " + B + "\n", "guid: " + A + "\nguid: " + B + "\n" })
        {
            Write((A + "/pathname", Bytes("Assets/safe.txt")), (A + "/asset.meta", Bytes(meta)));
            Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
        }
        Write((A + "/asset", Bytes("unindexed content")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file));
    }

    [Test] public void ExistingGuidCodeDestinationsRequireConsent()
    {
        Write((A + "/pathname", Bytes("Assets/safe.txt")));
        var index = UnityPackageIndex.Read(file);
        Assert.That(index.CodeFiles, Is.Empty);
        Assert.That(index.CodeFilesIncludingExisting(guid => guid == A ? "Assets/Editor/Existing.cs" : ""), Is.EqualTo(new[] { "Assets/Editor/Existing.cs" }));
    }

    [Test] public void LimitsIncludeEntriesPaddingTerminatorsAndTotalKeptContent()
    {
        Write((A + "/pathname", Bytes("Assets/a.txt")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file, maxExpandedBytes: 1536));
        Assert.AreEqual(1, UnityPackageIndex.Read(file, maxExpandedBytes: 2048).Entries.Count);
        Write((A + "/pathname", Bytes("Assets/a.txt")), (A + "/asset", new byte[10]));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file, maxEntries: 1));
        Write(Enumerable.Range(0, 17).SelectMany(i => new[] { (i.ToString("x32") + "/pathname", Bytes("Assets/" + i + ".txt")), (i.ToString("x32") + "/asset", new byte[1024 * 1024]) }).ToArray());
        Assert.AreEqual(16L * 1024 * 1024, UnityPackageIndex.Read(file, _ => true, 1024 * 1024).Entries.Sum(e => (long)(e.Content?.Length ?? 0)));
    }

    [Test] public void ThousandsOfDistinctRecordsAndLargeMetadataStayBounded()
    {
        Write(Enumerable.Range(0, 2500).Select(i => (i.ToString("x32") + "/pathname", Bytes("Assets/" + i + ".txt"))).ToArray());
        Assert.AreEqual(2500, UnityPackageIndex.Read(file).Entries.Count);
        Write((A + "/pathname", Bytes("Assets/model.fbx")), (A + "/asset.meta", Bytes("fileFormatVersion: 2\nguid: " + A + "\nuserData: " + new string('x', 256 * 1024))));
        Assert.AreEqual(1, UnityPackageIndex.Read(file).Entries.Count);
    }

    [TestCase("Assets/a.cs.")][TestCase("Assets/a.cs ")][TestCase("Assets/a.cs:payload")]
    [TestCase("Assets/CON.cs")][TestCase("Assets//a.cs")][TestCase("Assets/./a.cs")]
    public void WindowsPathAliasesAreUnsafe(string path) => Assert.IsFalse(UnityPackageIndex.IsProjectPath(path));

    [TestCase("Assets/A.cs.meta")][TestCase("Assets/Plugins/x.dll.meta")]
    [TestCase("Assets/a.cs.")][TestCase("Assets/a.cs ")]
    [TestCase("Packages/manifest.json")][TestCase("Packages/example/package.json")]
    public void CodeActivationFilesRequireConsent(string path) => Assert.IsTrue(CodeContent.IsCode(path));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private void Write(params (string name, byte[] data)[] entries) => Compress(Tar(entries));
    private void Compress(byte[] data)
    {
        using (var gz = new GZipStream(File.Create(file), CompressionMode.Compress)) gz.Write(data, 0, data.Length);
    }
    private static byte[] Tar(params (string name, byte[] data)[] entries)
    {
        using (var stream = new MemoryStream())
        {
            foreach (var (name, data) in entries)
            {
                stream.Write(Header(name, data.Length), 0, 512);
                stream.Write(data, 0, data.Length);
                int pad = (512 - data.Length % 512) % 512;
                stream.Write(new byte[pad], 0, pad);
            }
            stream.Write(new byte[1024], 0, 1024);
            return stream.ToArray();
        }
    }
    private static byte[] Header(string name, long size, char type = '0')
    {
        var header = new byte[512]; Bytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(header, 124); header[156] = (byte)type;
        for (int i = 148; i < 156; i++) header[i] = 32;
        Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        return header;
    }
}
