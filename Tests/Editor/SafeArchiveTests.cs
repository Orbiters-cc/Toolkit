using System;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;

// Downloads expand next to their folder before replacing it: every file must also fit Windows' path limit there.
public sealed class SafeArchiveTests
{
    private const string FileName = "5cef3f62ee9aa7efc0907202ca9d657a457696f0decfe4ac5189169148a4e9b7.bin";
    private string root;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "orbiters-safe-archive-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // A folder whose full path is exactly `length` characters long.
    private string Destination(int length)
    {
        if (length - root.Length - 1 < 1) Assert.Ignore("The temporary folder's path is too long for this test.");
        string destination = root + Path.DirectorySeparatorChar + new string('d', length - root.Length - 1);
        Assert.That(destination.Length, Is.EqualTo(length));
        return destination;
    }

    private static MemoryStream Zip(string entryName)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open())) writer.Write("payload");
        stream.Position = 0;
        return stream;
    }

    [Test]
    public void SiblingFoldersAddAShortSuffix()
    {
        string folder = Destination(100);
        Assert.That(SafePaths.Sibling(folder, "building"), Does.Match("^" + System.Text.RegularExpressions.Regex.Escape(folder) + @"\.building-[0-9a-f]{8}$"));
        Assert.That(SafePaths.HiddenSibling(folder, "trash"), Does.Match("^" + System.Text.RegularExpressions.Regex.Escape(folder) + @"\.trash-[0-9a-f]{8}~$"),
            "Unity never imports a folder ending in ~.");
    }

    [Test]
    public void ExtractsFilesThatFitThroughTheShortStagingFolder()
    {
        // Fits with the 19-character staging suffix (".building-", 8 digits, "~"); a 42-character one (a whole GUID) went over.
        string destination = Destination(SafePaths.MaxPathLength - 19 - 1 - FileName.Length - 1);
        using (var zip = Zip(FileName)) SafeArchive.Extract(zip, destination);
        Assert.That(File.ReadAllText(Path.Combine(destination, FileName)), Is.EqualTo("payload"));
    }

    [Test]
    public void RefusesFilesTooDeepForWindowsAndKeepsThePreviousFolder()
    {
        if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Only Windows limits path length.");
        string destination = Destination(SafePaths.MaxPathLength - 18 - FileName.Length);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "previous.txt"), "kept");

        using (var zip = Zip(FileName))
        {
            var error = Assert.Throws<PathTooLongException>(() => SafeArchive.Extract(zip, destination));
            StringAssert.Contains("characters shorter", error.Message);
        }
        Assert.That(File.ReadAllText(Path.Combine(destination, "previous.txt")), Is.EqualTo("kept"));
        Assert.That(Directory.GetDirectories(root), Has.Length.EqualTo(1), "No staging folder is left behind.");
        Assert.Throws<PathTooLongException>(() => SafeArchive.RequireRoom(destination, new[] { FileName }));
    }

    [Test]
    public void RefusesFoldersTooLongForWindowsEvenWhenTheFileNameFits()
    {
        if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Only Windows limits path length.");
        // The staging folder is 248 characters: one more than Windows creates, though "a.bin" in it would fit 259.
        string destination = Destination(SafePaths.MaxFolderPathLength - 19 + 1);
        var error = Assert.Throws<PathTooLongException>(() => SafeArchive.RequireRoom(destination, new[] { "a.bin" }));
        StringAssert.Contains("a folder would need", error.Message);
        Assert.DoesNotThrow(() => SafeArchive.RequireRoom(Destination(SafePaths.MaxFolderPathLength - 19), new[] { "a.bin" }));
    }

    [Test]
    public void DeviceNamesAreRefusedWhateverTheirExtension()
    {
        foreach (var name in new[] { "CON", "nul.txt", "NUL .txt", "COM0", "lpt9.bin", "COM¹", "LPT³.dat", "CONIN$", "conout$.log" })
            Assert.That(SafePaths.IsDeviceName(name), Is.True, name);
        foreach (var name in new[] { "CONSOLE", "COM10", "LPT", "nullable.txt", "auxiliary" })
            Assert.That(SafePaths.IsDeviceName(name), Is.False, name);
    }
}
