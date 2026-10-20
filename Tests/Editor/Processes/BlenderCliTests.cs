using System;
using System.Collections;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Processes;
using UnityEngine.TestTools;

public sealed class BlenderCliTests
{
    // What the program receives is what the caller gave: backslashes stay single unless a quote follows them.
    [TestCase("plain", "plain")]
    [TestCase("", "\"\"")]
    [TestCase(@"C:\My Files\avatar.blend", "\"C:\\My Files\\avatar.blend\"")]
    [TestCase(@"C:\My Files\", "\"C:\\My Files\\\\\"")]
    [TestCase("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [TestCase(@"a\""b c", "\"a\\\\\\\"b c\"")]
    [TestCase(@"\\server\share\x y", "\"\\\\server\\share\\x y\"")]
    public void ArgumentsAreQuotedAsWindowsReadsThem(string argument, string expected)
    {
        Assert.That(BlenderCli.Quote(argument), Is.EqualTo(expected));
    }

    // A job cancelled before Blender starts still answers, once, as cancelled (it used to throw on the main thread).
    [UnityTest]
    public IEnumerator AJobCancelledBeforeItStartsStillAnswers()
    {
        string executable = Path.Combine(UnityEditor.EditorApplication.applicationContentsPath, "MonoBleedingEdge", "bin",
            Path.DirectorySeparatorChar == '\\' ? "mono.exe" : "mono");
        Assume.That(File.Exists(executable), "an executable to name");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            int calls = 0;
            BlenderCli.Result result = null;
            BlenderCli.Run(new BlenderCli.Job { Executable = executable }, r => { calls++; result = r; }, cancellation.Token);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (calls == 0 && DateTime.UtcNow < deadline) yield return null;
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Cancelled, Is.True);
            Assert.That(result.Success, Is.False);
        }
    }

    [TestCase("Blender 4.2.3 LTS", 4, 2, 3)]
    [TestCase("Blender 4.2.3 RC 2", 4, 2, 3)]
    [TestCase("4.3", 4, 3, -1)]
    public void VersionsAreTheFirstDottedNumber(string text, int major, int minor, int build)
    {
        var version = BlenderInstalls.ParseVersion(text);
        Assert.That((version.Major, version.Minor, version.Build), Is.EqualTo((major, minor, build)));
    }

    [Test]
    public void PrereleasesAndOddPathsAreToldApart()
    {
        Assert.That(BlenderInstalls.IsPrerelease("Blender 4.3.0 Alpha"), Is.True);
        Assume.That(Path.DirectorySeparatorChar == '\\', "Windows paths");
        Assert.That(BlenderInstalls.IsPrerelease("Blender 4.2.0", @"D:\blender-4.2.0-beta+v42.abc-windows.amd64-release\blender.exe"), Is.True);
        Assert.That(BlenderInstalls.IsPrerelease("Blender 4.2.3 LTS", @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"), Is.False);
    }
}
