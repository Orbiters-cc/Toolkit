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
    }

    [Test]
    public void ExtractsFilesThatFitThroughTheShortStagingFolder()
    {
        // Fits with the 18-character staging suffix; a 42-character one (".building-" and a whole GUID) went over the limit.
        string destination = Destination(SafePaths.MaxPathLength - 18 - 1 - FileName.Length - 2);
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
            StringAssert.Contains("Move the project", error.Message);
        }
        Assert.That(File.ReadAllText(Path.Combine(destination, "previous.txt")), Is.EqualTo("kept"));
        Assert.That(Directory.GetDirectories(root), Has.Length.EqualTo(1), "No staging folder is left behind.");
        Assert.Throws<PathTooLongException>(() => SafeArchive.RequireRoom(destination, new[] { FileName }));
    }
}
