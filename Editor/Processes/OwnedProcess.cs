using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// A started process with its pipes, as the runner owns it. On Windows it is created suspended (CreateProcess with
    /// CREATE_SUSPENDED), put in its job, and only then resumed: nothing it starts can run outside the job, so stopping the
    /// job stops everything it started, and an empty job proves all its output has arrived. Elsewhere it is a
    /// System.Diagnostics.Process.
    /// </summary>
    internal abstract class OwnedProcess : IDisposable
    {
        internal abstract int Id { get; }
        internal abstract bool HasExited { get; }
        internal abstract int ExitCode { get; }
        internal abstract IntPtr Handle { get; }
        internal abstract void Kill();
        internal abstract bool WaitForExit(int milliseconds);
        internal Stream Input, Output, Error;
        internal Encoding OutputEncoding, ErrorEncoding;
        public abstract void Dispose();

        /// <summary>Closes stdin: the process reads its end.</summary>
        internal virtual void CloseInput()
        {
            try { Input?.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Creates the process, suspended where it can be (see <see cref="Resume"/>).</summary>
        internal static OwnedProcess Create(ProcessStartInfo info)
        {
            return Path.DirectorySeparatorChar == '\\' ? NativeProcess.CreateSuspended(info) : (OwnedProcess)new ManagedProcess(info);
        }

        /// <summary>Lets a suspended process run; nothing for one that runs already.</summary>
        internal virtual void Resume() { }
    }

    // Unix: Process.Start, as before.
    internal sealed class ManagedProcess : OwnedProcess
    {
        private readonly Process process;

        internal ManagedProcess(ProcessStartInfo info)
        {
            process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("The process did not start.");
            if (info.RedirectStandardInput) Input = process.StandardInput.BaseStream;
            if (info.RedirectStandardOutput) { Output = process.StandardOutput.BaseStream; OutputEncoding = process.StandardOutput.CurrentEncoding; }
            if (info.RedirectStandardError) { Error = process.StandardError.BaseStream; ErrorEncoding = process.StandardError.CurrentEncoding; }
        }

        internal override int Id => process.Id;
        internal override bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }
        internal override int ExitCode { get { try { return process.ExitCode; } catch (InvalidOperationException) { return -1; } } }
        internal override IntPtr Handle => process.Handle;
        internal override void Kill() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { } }
        internal override bool WaitForExit(int milliseconds) { try { return process.WaitForExit(milliseconds); } catch (InvalidOperationException) { return true; } catch (Win32Exception) { return true; } }

        public override void Dispose()
        {
            try { process.Dispose(); }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
    }

    // Windows: CreateProcess, suspended, with pipes of its own (the child's ends inheritable, the editor's not).
    internal sealed class NativeProcess : OwnedProcess
    {
        private IntPtr process, thread;
        // The editor's ends of the pipes. Mono's FileStream over a SafeFileHandle does not close the handle when the stream
        // closes, so they are closed here: else the child never sees the end of its input.
        private SafeFileHandle inputHandle, outputHandle, errorHandle;
        private readonly int id;
        private int exitCode = -1;
        private bool exited;

        private NativeProcess(IntPtr process, IntPtr thread, int id)
        {
            this.process = process;
            this.thread = thread;
            this.id = id;
        }

        /// <summary>For tests: called with the child's id right after it was created (suspended), before it is handed over.</summary>
        internal static Action<int> CreatedForTests;

        /// <summary>
        /// Creates the process suspended. It inherits exactly the three pipe ends meant for it and nothing else of the editor
        /// (PROC_THREAD_ATTRIBUTE_HANDLE_LIST): no other process's pipes, no unrelated handle. A slot that is not redirected
        /// gets no handle. The child's ends are made inheritable only for the CreateProcess call and closed right after it:
        /// a process started at that moment elsewhere in the editor by .NET (which passes every inheritable handle) could
        /// still take them with it, which nothing on this side can prevent; the window is kept that short. From the moment
        /// CreateProcess succeeds until the caller holds the owning object, a failure terminates the child (it has run no
        /// instruction yet) and closes its handles.
        /// </summary>
        internal static NativeProcess CreateSuspended(ProcessStartInfo info)
        {
            var created = new List<SafeFileHandle>();
            SafeFileHandle inRead = null, inWrite = null, outRead = null, outWrite = null, errRead = null, errWrite = null;
            IntPtr environment = IntPtr.Zero, attributes = IntPtr.Zero, handleArray = IntPtr.Zero;
            bool attributesReady = false, unowned = false;
            var started = new ProcessInformation();
            NativeProcess owned = null;
            try
            {
                if (info.RedirectStandardInput) Pipe(out inWrite, out inRead, false, created); // the editor writes, the child reads
                if (info.RedirectStandardOutput) Pipe(out outRead, out outWrite, true, created);
                if (info.RedirectStandardError) Pipe(out errRead, out errWrite, true, created);
                var childEnds = new[] { inRead, outWrite, errWrite }.Where(handle => handle != null).ToList();
                var startup = new StartupInfoEx();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                uint flags = 0x4 | 0x400; // CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT
                if (info.CreateNoWindow) flags |= 0x08000000; // CREATE_NO_WINDOW
                if (childEnds.Count > 0)
                {
                    startup.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES
                    startup.StartupInfo.hStdInput = inRead?.DangerousGetHandle() ?? IntPtr.Zero;
                    startup.StartupInfo.hStdOutput = outWrite?.DangerousGetHandle() ?? IntPtr.Zero;
                    startup.StartupInfo.hStdError = errWrite?.DangerousGetHandle() ?? IntPtr.Zero;
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    attributesReady = true;
                    handleArray = Marshal.AllocHGlobal(IntPtr.Size * childEnds.Count);
                    for (int i = 0; i < childEnds.Count; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, childEnds[i].DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handleArray, (IntPtr)(IntPtr.Size * childEnds.Count), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error()); // PROC_THREAD_ATTRIBUTE_HANDLE_LIST
                    startup.lpAttributeList = attributes;
                    flags |= 0x80000; // EXTENDED_STARTUPINFO_PRESENT
                }

                environment = EnvironmentBlock(info);
                var commandLine = new StringBuilder(CommandLine(info));
                string directory = string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory;
                bool createdProcess;
                try
                {
                    foreach (SafeFileHandle end in childEnds)
                        if (!SetHandleInformation(end, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); // inheritable, now only
                    createdProcess = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, childEnds.Count > 0, flags, environment, directory,
                        ref startup, out started);
                    if (createdProcess) unowned = true;
                }
                finally
                {
                    // The child's ends belong to the child now (or to nobody): the editor's copies go at once.
                    foreach (SafeFileHandle end in childEnds) { end.Dispose(); created.Remove(end); }
                }
                if (!createdProcess) throw new Win32Exception(Marshal.GetLastWin32Error());

                CreatedForTests?.Invoke(started.dwProcessId);
                owned = new NativeProcess(started.hProcess, started.hThread, started.dwProcessId);
                if (inWrite != null) { owned.Input = new FileStream(inWrite, FileAccess.Write, 4096, false); owned.inputHandle = inWrite; created.Remove(inWrite); }
                if (outRead != null) { owned.Output = new FileStream(outRead, FileAccess.Read, 4096, false); owned.outputHandle = outRead; created.Remove(outRead); }
                if (errRead != null) { owned.Error = new FileStream(errRead, FileAccess.Read, 4096, false); owned.errorHandle = errRead; created.Remove(errRead); }
                owned.OutputEncoding = info.StandardOutputEncoding ?? Console.OutputEncoding;
                owned.ErrorEncoding = info.StandardErrorEncoding ?? Console.OutputEncoding;
                unowned = false; // handed over: the caller owns it from here
                return owned;
            }
            finally
            {
                if (unowned)
                {
                    // Created, never handed over: it is still suspended, so it has run nothing. Terminate it, close its handles.
                    if (owned != null) owned.Abandon();
                    TerminateProcess(started.hProcess, 1);
                    CloseHandle(started.hThread);
                    CloseHandle(started.hProcess);
                }
                foreach (SafeFileHandle handle in created) handle.Dispose();
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            }
        }

        // Forgets the handles without closing them (the creation's cleanup closes them).
        private void Abandon()
        {
            process = thread = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        // A domain unload between creation and resume: the child, still suspended, never ran; it is terminated, not left
        // suspended for ever. A child that runs belongs to its job, whose closing stops it.
        ~NativeProcess()
        {
            if (thread != IntPtr.Zero)
            {
                if (process != IntPtr.Zero) TerminateProcess(process, 1);
                CloseHandle(thread);
            }
            if (process != IntPtr.Zero) CloseHandle(process);
        }

        // A pipe whose two ends are not inheritable (the child's end becomes so only for its CreateProcess). childWrites: the
        // child writes (stdout, stderr).
        private static void Pipe(out SafeFileHandle editorEnd, out SafeFileHandle childEnd, bool childWrites, List<SafeFileHandle> created)
        {
            var attributes = new SecurityAttributes { nLength = Marshal.SizeOf(typeof(SecurityAttributes)), bInheritHandle = false };
            if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref attributes, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            created.Add(read);
            created.Add(write);
            editorEnd = childWrites ? read : write;
            childEnd = childWrites ? write : read;
        }

        // The command line as .NET builds it: the file name (quoted when it holds a space), then the arguments as given.
        private static string CommandLine(ProcessStartInfo info)
        {
            string file = info.FileName ?? string.Empty;
            string quoted = file.Length > 0 && file[0] != '"' && file.IndexOf(' ') >= 0 ? "\"" + file + "\"" : file;
            return string.IsNullOrEmpty(info.Arguments) ? quoted : quoted + " " + info.Arguments;
        }

        private static IntPtr EnvironmentBlock(ProcessStartInfo info)
        {
            var block = new StringBuilder();
            foreach (KeyValuePair<string, string> variable in info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(variable.Key) || variable.Value == null) continue;
                block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
            }
            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        internal override void CloseInput()
        {
            base.CloseInput();
            inputHandle?.Dispose();
        }

        internal override void Resume()
        {
            IntPtr resuming = thread;
            thread = IntPtr.Zero;
            if (resuming == IntPtr.Zero) return;
            try
            {
                if (ResumeThread(resuming) == unchecked((uint)-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                CloseHandle(resuming);
            }
        }

        internal override int Id => id;
        internal override IntPtr Handle => process;

        internal override bool HasExited
        {
            get
            {
                if (exited) return true;
                if (process == IntPtr.Zero) return true;
                if (WaitForSingleObject(process, 0) != 0) return false;
                exited = true;
                if (GetExitCodeProcess(process, out uint code)) exitCode = unchecked((int)code);
                return true;
            }
        }

        internal override int ExitCode => HasExited ? exitCode : -1;

        internal override void Kill()
        {
            if (process != IntPtr.Zero && !HasExited) TerminateProcess(process, 1);
        }

        internal override bool WaitForExit(int milliseconds)
        {
            if (process == IntPtr.Zero) return true;
            return WaitForSingleObject(process, (uint)Math.Max(0, milliseconds)) == 0 && HasExited;
        }

        public override void Dispose()
        {
            GC.SuppressFinalize(this);
            if (thread != IntPtr.Zero) { CloseHandle(thread); thread = IntPtr.Zero; }
            bool ended = HasExited; // keeps the exit code when it has ended
            if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
            try { Input?.Dispose(); } catch (IOException) { }
            try { Output?.Dispose(); } catch (IOException) { }
            try { Error?.Dispose(); } catch (IOException) { }
            inputHandle?.Dispose();
            outputHandle?.Dispose();
            errorHandle?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup,
            out ProcessInformation information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup,
            out ProcessInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
