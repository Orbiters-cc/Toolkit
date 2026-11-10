using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Processes;

// Stdin, raw streamed output with an early stop, kept-alive processes, the reload grace for writes and the shared start
// lock: what Mixer Git needs from the runner, checked on the same compiled fixture as ProcessRunnerTests.
public sealed class ProcessRunnerStreamingTests
{
    private string folder, mono, fixture;

    [OneTimeSetUp]
    public void CompileFixture()
    {
        string data = UnityEditor.EditorApplication.applicationContentsPath;
        mono = Path.Combine(data, "MonoBleedingEdge", "bin", Path.DirectorySeparatorChar == '\\' ? "mono.exe" : "mono");
        folder = Path.Combine(Path.GetTempPath(), "orbiters-process-streaming-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        fixture = Path.Combine(folder, "fixture.exe");
        string compiler = Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe");
        string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EditorProcessRunner).Assembly).resolvedPath;
        string source = Path.Combine(package, "Tests", "Editor", "Processes", "ProcessFixture.cs");
        var result = EditorProcessRunner.Run(Start(Quote(compiler) + " -nologo -out:" + Quote(fixture) + " " + Quote(source), false), 15000);
        Assert.That(result.Success, Is.True, result.StandardOutput + result.StandardError);
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private ProcessStartInfo Start(string arguments, bool input) => new ProcessStartInfo(mono, arguments)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        RedirectStandardInput = input, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
    };

    private ProcessStartInfo Command(string mode, bool input = false, string extra = "") =>
        Start(Quote(fixture) + " " + mode + " " + Quote(mono) + (extra.Length > 0 ? " " + extra : string.Empty), input);

    private static byte[] AllBytes(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 7 + i / 256);
        return bytes;
    }

    [Test]
    public void InputReachesTheProcessExactlyWhileItsOutputIsRead()
    {
        if (!EditorProcessRunner.CleanInput) Assert.Ignore("Stdin cannot be clean on this system.");
        byte[] input = AllBytes(5 * 1024 * 1024 + 17);
        using (var output = new MemoryStream())
        {
            var result = EditorProcessRunner.Run(new EditorProcessRequest
            {
                StartInfo = Command("echo", true), TimeoutMilliseconds = 60000, Input = input, OutputDestination = output
            });
            Assert.That(result.Success, Is.True, result.StandardError);
            Assert.That(result.InputFailed, Is.False);
            // No byte order mark in front, nothing lost while both pipes were full.
            Assert.That(output.ToArray(), Is.EqualTo(input));
        }
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void InputNeedsARedirectedStdin()
    {
        Assert.Throws<ArgumentException>(() => EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("echo"), TimeoutMilliseconds = 5000, Input = new byte[] { 1 }
        }));
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void ProcessThatStopsReadingReportsFailedInput()
    {
        if (!EditorProcessRunner.CleanInput) Assert.Ignore("Stdin cannot be clean on this system.");
        var result = EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("fail-early", true), TimeoutMilliseconds = 30000, Input = AllBytes(16 * 1024 * 1024)
        });
        Assert.That(result.ExitCode, Is.EqualTo(3));
        Assert.That(result.InputFailed, Is.True);
        Assert.That(result.Success, Is.False);
        Assert.That(result.StandardError, Is.EqualTo("refused"));
    }

    [Test]
    public void RawOutputCanStopTheProcessEarly()
    {
        long seen = 0;
        var elapsed = Stopwatch.StartNew();
        var result = EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("endless"), TimeoutMilliseconds = 30000,
            Output = (bytes, offset, count) => (seen += count) < 1024 * 1024
        });
        Assert.That(result.Stopped, Is.True);
        Assert.That(result.Success, Is.False);
        Assert.That(result.StandardOutput, Is.Empty, "Raw output is not kept as text.");
        Assert.That(seen, Is.GreaterThanOrEqualTo(1024 * 1024));
        Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(20000));
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void EndlessStderrDoesNotKeepTheRunnerFromItsTimeout()
    {
        // stderr never runs dry and each line is slow to take: the runner still gets back to its deadline.
        int lines = 0;
        var elapsed = Stopwatch.StartNew();
        var result = EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("stderr-flood"), TimeoutMilliseconds = 1000,
            ErrorLine = line => { if (++lines % 64 == 0) Thread.Sleep(1); }
        });
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.Success, Is.False);
        Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(6000));
        Assert.That(lines, Is.GreaterThan(0));
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void KeptAliveProcessAnswersOneRequestAfterTheOther()
    {
        if (!EditorProcessRunner.CleanInput) Assert.Ignore("Stdin cannot be clean on this system.");
        int before = EditorProcessRunner.ActiveCount;
        var record = new MemoryStream();
        using (EditorLiveProcess live = EditorLiveProcess.Start(Command("answer", true)))
        {
            Assert.That(live, Is.Not.Null);
            Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before + 1));
            for (int i = 0; i < 200; i++)
            {
                string question = "q " + i + " é";
                live.Write(Encoding.UTF8.GetBytes(question + "\n"));
                Assert.That(live.ReadRecord((byte)'\n', record, 10000, null, out bool timedOut), Is.True);
                Assert.That(timedOut, Is.False);
                Assert.That(Encoding.UTF8.GetString(record.ToArray()), Is.EqualTo("got:" + question));
            }
            // A request with no answer times out instead of waiting for good.
            Assert.That(live.ReadRecord((byte)'\n', record, 200, null, out bool late), Is.False);
            Assert.That(late, Is.True);
            live.CloseInput();
        }
        Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before));
    }

    [Test]
    public void WriteInFlightAtReloadFinishesWhileReadsStopAtOnce()
    {
        EditorProcessResult change = null, read = null;
        var changing = new Thread(() => change = EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("slow", false, "1500"), TimeoutMilliseconds = 30000, Write = true
        }));
        var reading = new Thread(() => read = EditorProcessRunner.Run(Command("sleep"), 30000));
        changing.Start();
        reading.Start();
        var wait = Stopwatch.StartNew();
        while (EditorProcessRunner.ActiveCount < 2 && wait.ElapsedMilliseconds < 10000) Thread.Sleep(5);
        Assert.That(EditorProcessRunner.ActiveCount, Is.GreaterThanOrEqualTo(2));
        var stopping = Stopwatch.StartNew();
        EditorProcessRunner.StopAllThenResume(EditorProcessRunner.WriteGraceMilliseconds);
        changing.Join(20000);
        reading.Join(20000);
        Assert.That(read.Cancelled, Is.True, "A read stops at once.");
        Assert.That(change.Success, Is.True, "The write finished: " + change.StandardError);
        Assert.That(change.StandardOutput, Does.EndWith("done"));
        Assert.That(stopping.ElapsedMilliseconds, Is.LessThan(EditorProcessRunner.WriteGraceMilliseconds));
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }

    [Test]
    public void WriteThatOutlastsTheGraceIsStoppedAndNeverReportedAsSuccess()
    {
        EditorProcessResult change = null;
        int pid = 0;
        var changing = new Thread(() => change = EditorProcessRunner.Run(new EditorProcessRequest
        {
            StartInfo = Command("slow", false, "30000"), TimeoutMilliseconds = 60000, Write = true,
            OutputLine = line => int.TryParse(line, out pid)
        }));
        changing.Start();
        var wait = Stopwatch.StartNew();
        while (pid == 0 && wait.ElapsedMilliseconds < 10000) Thread.Sleep(5);
        EditorProcessRunner.StopAllThenResume(300);
        changing.Join(20000);
        Assert.That(change.Success, Is.False);
        Assert.That(change.Cancelled, Is.True);
        Assert.That(pid, Is.GreaterThan(0));
        Assert.That(SpinWait.SpinUntil(() =>
        {
            try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }, 2000), Is.True);
    }

    [Test]
    public void NothingStartsWhileStoppingAndStartsAgainAfter()
    {
        EditorProcessRunner.StopAllThenResume(0);
        var result = EditorProcessRunner.Run(Command("text"), 10000);
        Assert.That(result.Success, Is.True, "Starting is allowed again after the stop.");
    }

    [Test]
    public void ProcessesStartedAtTheSameMomentNeverHoldEachOthersPipes()
    {
        // Every start, run to an end or kept alive, takes the same lock: no process inherits another's pipe ends, so no
        // output is held open after its process exits. A race test: it can only fail when the lock is missing.
        var failures = new List<string>();
        var threads = Enumerable.Range(0, 6).Select(index => new Thread(() =>
        {
            for (int i = 0; i < 15; i++)
            {
                if (index % 2 == 0)
                {
                    var result = EditorProcessRunner.Run(Command("text"), 10000);
                    if (!result.Success || result.OutputHeldOpen || result.StandardOutput != "héllo\nlast")
                        lock (failures) failures.Add("run: " + result.ExitCode + " " + result.TimedOut + " " + result.StandardError);
                }
                else
                {
                    using (EditorLiveProcess live = EditorLiveProcess.Start(Command("text")))
                    {
                        var record = new MemoryStream();
                        if (live == null || !live.ReadRecord((byte)'\n', record, 10000, null, out _))
                            lock (failures) failures.Add("live: no answer");
                    }
                }
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join(120000));
        Assert.That(failures, Is.Empty);
        Assert.That(EditorProcessRunner.ActiveCount, Is.Zero);
    }
}
