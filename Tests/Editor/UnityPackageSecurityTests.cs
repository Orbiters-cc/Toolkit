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
    [TestCase("Packages/vpm-manifest.json")][TestCase("Packages/packages-lock.json")][TestCase("Packages/scoped-registries.json")]
    [TestCase("ProjectSettings/ProjectSettings.asset")][TestCase("ProjectSettings/PackageManagerSettings.asset.meta")]
    public void CodeActivationFilesRequireConsent(string path) => Assert.IsTrue(CodeContent.IsCode(path));

    // Package resolution files only count at the top of Packages/ (or as a package's package.json): data elsewhere.
    [TestCase("Assets/Packages/vpm-manifest.json")][TestCase("Assets/Hat/settings.json")][TestCase("Packages/example/Runtime/data.json")]
    public void JsonDataIsNotCode(string path) => Assert.IsFalse(CodeContent.IsCode(path));

    // Unity writes an entry to the asset already holding its GUID: one held by a package (a VPM package's file) is never
    // imported, whatever path the entry names, and its code is not asked about since it is not imported.
    [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator EntriesUpdatingAPackageFileAreLeftOut()
    {
        string guid = UnityEditor.AssetDatabase.AssetPathToGUID("Packages/orbiters.toolkit/package.json");
        Assert.IsNotEmpty(guid);
        Write((guid + "/pathname", Bytes("Assets/Evil/Setup.cs")), (guid + "/asset", Bytes("class Evil {}")),
            (A + "/pathname", Bytes("Assets/Hat.txt")), (A + "/asset", Bytes("hat")), (B + "/pathname", Bytes("Packages/vpm-manifest.json")), (B + "/asset", Bytes("{}")));
        var task = UnityPackagePreview.CreateAsync(file, UnityPackageIndex.Read(file));
        while (!task.IsCompleted) yield return null;
        var preview = task.Result;
        CollectionAssert.AreEquivalent(new[] { "Packages/orbiters.toolkit/package.json", "Packages/vpm-manifest.json" }, preview.Skipped);
        CollectionAssert.AreEqual(new[] { A }, preview.ImportGuids.ToArray());
        Assert.IsEmpty(preview.CodeFiles);
    }

    // An index of a package naming many long paths stops at the pathname budget, before keeping them all.
    [Test] public void PathnamesTogetherAreBounded()
    {
        Write((A + "/pathname", Bytes("Assets/" + new string('a', 60) + ".txt")), (B + "/pathname", Bytes("Assets/" + new string('b', 60) + ".txt")));
        Assert.AreEqual(2, UnityPackageIndex.Read(file, maxPathnameTotalBytes: 200).Entries.Count);
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file, maxPathnameTotalBytes: 100));
    }

    // Tools that only read a package (MCB's source FBX, the Unity Package Manager) take a pathname's first line, as Unity's
    // importer does; what follows it never names a second asset, and the line itself is checked like any pathname.
    [Test] public void FirstLinePathnamesReadThePathAsUnityDoes()
    {
        Write((A + "/pathname", Bytes("Assets/a.fbx\n00")), (B + "/pathname", Bytes("Assets/b.fbx\r\n")));
        CollectionAssert.AreEqual(new[] { "Assets/a.fbx", "Assets/b.fbx" }, UnityPackageIndex.Read(file, firstLinePathname: true).Paths);
        Write((A + "/pathname", Bytes("Assets/evil.cs\0.txt\n00")));
        Assert.Throws<InvalidDataException>(() => UnityPackageIndex.Read(file, firstLinePathname: true));
    }

    [Test] public void ARecordsDataIsNotAllocatedAsDeclared()
    {
        var stream = new RecordingStream(new byte[100]);
        var record = new UnityPackageReader.Record { Size = 1L << 30, Content = stream };
        Assert.Throws<EndOfStreamException>(() => record.ReadAll());
        Assert.LessOrEqual(stream.LargestBuffer, 1 << 20);
        var data = new byte[3 * 1024 * 1024 + 7];
        new Random(1).NextBytes(data);
        Assert.IsTrue(data.SequenceEqual(new UnityPackageReader.Record { Size = data.Length, Content = new MemoryStream(data) }.ReadAll()), "Growing, it still returns every byte");
    }

    [Test] public void AHugeEntryCutShortFailsWhileItIsRead()
    {
        Compress(Header(A + "/asset", 1L << 30));
        Assert.Throws<EndOfStreamException>(() => UnityPackageReader.Read(file, null, record => record.ReadAll()));
    }

    // A filtered copy carries the chosen records as they were stored (long names too) and reads back as a valid package.
    [Test] public void FilteredCopiesKeepTheChosenRecordsAsStored()
    {
        string longPath = "Assets/" + new string('l', 120) + ".txt", copy = file + ".copy.unitypackage";
        var longName = Bytes(A + "/pathname");
        var archive = new MemoryStream();
        foreach (var part in new[] { Header("././@LongLink", longName.Length, 'L'), Pad(longName), Tar((A + "/pathname", Bytes(longPath)), (A + "/asset", Bytes("long")),
            (B + "/pathname", Bytes("Assets/b.txt")), (B + "/asset", Bytes("bee"))) })
            archive.Write(part, 0, part.Length);
        // The GNU long-name record names the header after it, whose own short name is then ignored.
        Compress(archive.ToArray());
        try
        {
            UnityPackageFiles.CopyEntries(file, copy, new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { A });
            var index = UnityPackageIndex.Read(copy, keepContent: _ => true);
            Assert.AreEqual(longPath, index.Entries.Single().Path);
            Assert.AreEqual("long", Encoding.UTF8.GetString(index.Entries.Single().Content));
            var hashes = UnityPackageFiles.AssetHashes(copy);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                Assert.AreEqual(BitConverter.ToString(sha.ComputeHash(Bytes("long"))).Replace("-", "").ToLowerInvariant(), hashes[A]);
            Assert.AreEqual(Decompressed(copy), index.ExpandedBytes, "The expanded size is every byte of the tar");
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
    }

    private static long Decompressed(string path)
    {
        using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress)) { long total = 0; var buffer = new byte[81920]; for (int n; (n = gz.Read(buffer, 0, buffer.Length)) > 0;) total += n; return total; }
    }

    private static byte[] Pad(byte[] data) => data.Concat(new byte[(512 - data.Length % 512) % 512]).ToArray();

    private sealed class RecordingStream : MemoryStream
    {
        public int LargestBuffer;
        public RecordingStream(byte[] data) : base(data) { }
        public override int Read(byte[] buffer, int offset, int count) { LargestBuffer = Math.Max(LargestBuffer, buffer.Length); return base.Read(buffer, offset, count); }
    }

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
