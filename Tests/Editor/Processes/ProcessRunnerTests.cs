using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Processes;

public sealed class ProcessRunnerTests
{
    private string folder, mono, fixture;

    [OneTimeSetUp]
    public void CompileFixture()
    {
        string data = UnityEditor.EditorApplication.applicationContentsPath;
        mono = Path.Combine(data, "MonoBleedingEdge", "bin", Path.DirectorySeparatorChar == '\\' ? "mono.exe" : "mono");
        folder = Path.Combine(Path.GetTempPath(), "orbiters-process-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        fixture = Path.Combine(folder, "fixture.exe");
        string compiler = Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
        string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EditorProcessRunner).Assembly).resolvedPath;
        string source = Path.Combine(package, "Tests", "Editor", "Processes", "ProcessFixture.cs");
        var result = EditorProcessRunner.Run(Start(Quote(compiler) + " -nologo -out:" + Quote(fixture) + " " + Quote(source)), 15000);
        Assert.That(result.Success, Is.True, result.StandardOutput + result.StandardError);
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private ProcessStartInfo Start(string arguments) => new ProcessStartInfo(mono, arguments)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
    private ProcessStartInfo Command(string mode) => Start(Quote(fixture) + " " + mode + " " + Quote(mono));

    [Test]
    public void FastCommandsCanExitDuringStartup()
    {
        var command = Path.DirectorySeparatorChar == '\\'
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0")
            : new ProcessStartInfo("/bin/true");
        command.UseShellExecute = false;
        command.CreateNoWindow = true;
        for (int i = 0; i < 25; i++) Assert.That(EditorProcessRunner.Run(command, 5000).Success, Is.True);
    }

    [Test]
    public void PreservesUnicodeAndLastLine()
    {
        var lines = new System.Collections.Generic.List<string>();
        var result = EditorProcessRunner.Run(Command("text"), 5000, stdoutLine: lines.Add);
        Assert.That(result.Success, Is.True, result.StandardError);
        Assert.That(result.StandardOutput, Is.EqualTo("héllo\nlast"));
        Assert.That(result.StandardError, Is.EqualTo("érr\n"));
        Assert.That(lines, Is.EqualTo(new[] { "héllo", "last" }));
    }

    [Test]
    public void PreservesLineCallbacksForCrLfAndCrLfPairs()
    {
        var lines = new System.Collections.Generic.List<string>();
        var result = EditorProcessRunner.Run(Command("newlines"), 5000, stdoutLine: lines.Add);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StandardOutput, Is.EqualTo("one\rtwo\r\nthree\n\nlast"));
        Assert.That(lines, Is.EqualTo(new[] { "one", "two", "three", "", "last" }));
    }

    [Test]
    public void RemovesTextBom()
    {
        var result = EditorProcessRunner.Run(Command("bom"), 5000);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StandardOutput, Is.EqualTo("héllo"));
    }

    [Test]
    public void DrainsBothFullPipesWithoutLosingOutput()
    {
        var result = EditorProcessRunner.Run(Command("flood"), 10000);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StandardOutput, Is.EqualTo(new string('o', 128 * 8192)));
        Assert.That(result.StandardError, Is.EqualTo(new string('e', 128 * 8192)));
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void DecodesUtf8AcrossReads()
    {
        var result = EditorProcessRunner.Run(Command("split-utf8"), 5000);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StandardOutput, Is.EqualTo("hé🦊\nlast"));
    }

    [Test]
    public void CopiesBinaryBytesWithoutTextConversion()
    {
        using (var output = new MemoryStream())
        {
            var result = EditorProcessRunner.Run(Command("binary"), 5000, standardOutputDestination: output);
            Assert.That(result.Success, Is.True);
            Assert.That(output.ToArray(), Is.EqualTo(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()));
            Assert.That(output.CanWrite, Is.True);
        }
    }

    [Test]
    public void ClosesRedirectedStdin()
    {
        var command = Command("stdin"); command.RedirectStandardInput = true;
        var result = EditorProcessRunner.Run(command, 5000);
        Assert.That(result.Success, Is.True);
        Assert.That(result.StandardOutput, Is.EqualTo("0"));
    }

    [Test]
    public void CancellationBeforeLaunchDoesNotStartAnything()
    {
        var result = EditorProcessRunner.Run(new ProcessStartInfo("does-not-exist"), 5000, () => true);
        Assert.That(result.Cancelled, Is.True);
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void CancellationAfterOutputStopsOwnedProcess()
    {
        int pid = 0;
        var result = EditorProcessRunner.Run(Command("sleep"), 5000, () => pid != 0, line => int.TryParse(line, out pid));
        Assert.That(result.Cancelled, Is.True);
        AssertStopped(pid);
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void TimeoutStopsOwnedProcess()
    {
        int pid = 0;
        var elapsed = Stopwatch.StartNew();
        var result = EditorProcessRunner.Run(Command("sleep"), 1000, stdoutLine: line => int.TryParse(line, out pid));
        Assert.That(result.TimedOut, Is.True);
        Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(3500));
        AssertStopped(pid);
    }

    [Test]
    public void ThrowingCallbackStillStopsOwnedProcess()
    {
        int pid = 0;
        Assert.Throws<InvalidOperationException>(() => EditorProcessRunner.Run(Command("sleep"), 5000,
            stdoutLine: line => { int.TryParse(line, out pid); throw new InvalidOperationException("callback"); }));
        AssertStopped(pid);
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void InheritedPipeAfterLauncherExitHasBoundedDrainAndStopsChild()
    {
        if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows job ownership regression.");
        int child = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = EditorProcessRunner.Run(Command("child-pipe"), 5000, stdoutLine: line =>
            { if (line.StartsWith("CHILD:")) int.TryParse(line.Substring(6), out child); });
            Assert.That(result.TimedOut, Is.True);
            Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(3000));
            AssertStopped(child);
        }
        finally { StopFixture(child); }
    }

    private static void AssertStopped(int pid)
    {
        Assert.That(pid, Is.GreaterThan(0));
        Assert.That(SpinWait.SpinUntil(() =>
        {
            try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }, 2000), Is.True, "Owned fixture process survived cleanup: " + pid);
    }

    private static void StopFixture(int pid)
    {
        if (pid <= 0) return;
        try { using (var process = Process.GetProcessById(pid)) if (!process.HasExited) process.Kill(); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
