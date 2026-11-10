using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Processes;

// What a child of the runner holds of the editor (Windows): only its own pipe ends, and nothing at all when it was created
// but never handed over. Run on the same compiled fixture as the other process tests.
public sealed class ProcessHandleTests
{
    private string folder, mono, fixture;

    [OneTimeSetUp]
    public void CompileFixture()
    {
        if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows handle inheritance.");
        string data = UnityEditor.EditorApplication.applicationContentsPath;
        mono = Path.Combine(data, "MonoBleedingEdge", "bin", "mono.exe");
        folder = Path.Combine(Path.GetTempPath(), "orbiters-process-handle-tests-" + Guid.NewGuid().ToString("N"));
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

    private ProcessStartInfo Command(string mode, bool input = false) => Start(Quote(fixture) + " " + mode + " " + Quote(mono), input);

    [Test]
    public void AChildDoesNotInheritAnUnrelatedInheritableHandle()
    {
        // An inheritable pipe of the editor's that has nothing to do with the child: if the child took its write end with
        // it, closing ours would not end the pipe while the child lives.
        var inheritable = new SecurityAttributes { nLength = Marshal.SizeOf(typeof(SecurityAttributes)), bInheritHandle = true };
        Assert.That(CreatePipe(out IntPtr read, out IntPtr write, ref inheritable, 0), Is.True);
        try
        {
            using (EditorLiveProcess child = EditorLiveProcess.Start(Command("answer", true)))
            {
                Assert.That(child, Is.Not.Null);
                CloseHandle(write);
                write = IntPtr.Zero;
                bool open = PeekNamedPipe(read, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero);
                int error = Marshal.GetLastWin32Error();
                Assert.That(open, Is.False, "The child holds the editor's unrelated pipe.");
                Assert.That(error, Is.EqualTo(109), "Broken pipe: no other writer.");
                Assert.That(child.HasExited, Is.False);
            }
        }
        finally
        {
            if (write != IntPtr.Zero) CloseHandle(write);
            CloseHandle(read);
        }
    }

    [Test]
    public void TwoChildrenEachSeeTheEndOfTheirOwnInputAtOnce()
    {
        using (EditorLiveProcess first = EditorLiveProcess.Start(Command("stdin", true)))
        using (EditorLiveProcess second = EditorLiveProcess.Start(Command("stdin", true)))
        {
            var record = new MemoryStream();
            var elapsed = Stopwatch.StartNew();
            first.Write(Encoding.UTF8.GetBytes("abc"));
            first.CloseInput();
            Assert.That(first.ReadRecord((byte)'\n', record, 10000, null, out bool timedOut), Is.True);
            Assert.That(timedOut, Is.False);
            Assert.That(Encoding.UTF8.GetString(record.ToArray()), Is.EqualTo("3"), "The first child read its whole input and ended.");
            Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(5000));
            Assert.That(SpinWait.SpinUntil(() => first.HasExited, 5000), Is.True);
            Assert.That(second.HasExited, Is.False, "The second child still waits for its own input.");
            second.CloseInput();
            Assert.That(second.ReadRecord((byte)'\n', record, 10000, null, out _), Is.True);
            Assert.That(Encoding.UTF8.GetString(record.ToArray()), Is.EqualTo("0"));
        }
    }

    [Test]
    public void AChildCreatedButNeverHandedOverIsTerminated()
    {
        int created = 0;
        int before = EditorProcessRunner.ActiveCount;
        NativeProcess.CreatedForTests = id => { created = id; throw new InvalidOperationException("seam"); };
        try
        {
            Assert.Throws<InvalidOperationException>(() => EditorProcessRunner.Run(Command("sleep"), 5000));
        }
        finally
        {
            NativeProcess.CreatedForTests = null;
        }
        Assert.That(created, Is.GreaterThan(0));
        Assert.That(SpinWait.SpinUntil(() =>
        {
            try { using (var process = Process.GetProcessById(created)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }, 3000), Is.True, "The suspended child was left behind.");
        Assert.That(EditorProcessRunner.ActiveCount, Is.EqualTo(before));
        Assert.That(EditorProcessRunner.Run(Command("text"), 5000).Success, Is.True, "Starting works again.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
