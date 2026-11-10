using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// Owns every process the runner starts, the ones kept alive between requests included: each runs in a Windows job that
    /// kills it and its children when closed, and all are stopped before a domain reload and when Unity quits, so none
    /// outlives the editor that started it. Processes marked as writes (they change files a user owns, such as a Git
    /// repository) get <see cref="WriteGraceMilliseconds"/> to finish first.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorProcessLifetime
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Process, WindowsProcessJob> Active = new Dictionary<Process, WindowsProcessJob>();
        // Processes that change something: at a reload or quit they get time to finish before they are stopped.
        private static readonly HashSet<Process> Writes = new HashSet<Process>();
        // Processes whose assignment to their job succeeded: only for them does an empty job mean nothing of theirs runs.
        private static readonly HashSet<Process> Assigned = new HashSet<Process>();
        // Processes stopped while they still ran: a job closed under them makes them exit with code 0, which is no success.
        private static readonly ConditionalWeakTable<Process, object> StoppedRunning = new ConditionalWeakTable<Process, object>();
        private static readonly List<Action> StopListeners = new List<Action>();

        // Every start in this domain goes through this lock, from Process.Start until the child's ends of its pipes are
        // closed (Process.Start returns). Windows passes every inheritable handle of the editor to a process created with
        // bInheritHandles, which Mono's Process.Start always sets (the child needs its own pipe ends); Mono creates both
        // ends of each pipe inheritable and only then makes the editor's end private and closes the child's after
        // CreateProcess. A second start in that window would take the first one's pipe ends with it: a stdout write end
        // keeps the first process's output open after it exits, a stdin write end keeps its input from ever ending.
        // A start that waits longer than this goes ahead without the lock rather than hold the editor up.
        private static readonly object StartSync = new object();
        private const int StartLockWaitMilliseconds = 10000;

        /// <summary>How long a reload or quit waits for writes in flight to finish before it stops them.</summary>
        internal const int WriteGraceMilliseconds = 10000;

        private static volatile bool stopping, stoppingAllowsWrites;
        internal static bool IsStopping => stopping;
        internal static int ActiveCount { get { lock (Sync) return Active.Count; } }

        static EditorProcessLifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorApplication.quitting += StopAll;
        }

        /// <summary>Whether a reload or quit is under way and this process should stop now (a write in flight may finish).</summary>
        internal static bool ShouldStop(bool write) => stopping && !(write && stoppingAllowsWrites);

        /// <summary>Called once when everything stops (reload, quit), before the processes are: owners of kept-alive processes forget them.</summary>
        internal static void OnStopping(Action listener)
        {
            if (listener == null) return;
            lock (Sync) StopListeners.Add(listener);
        }

        /// <summary>Starts the process in a job of its own and owns it; false when the editor is stopping (reload, quit).</summary>
        internal static bool Start(Process process, bool write)
        {
            if (stopping) return false;
            WindowsProcessJob job = Path.DirectorySeparatorChar == '\\' ? WindowsProcessJob.Create() : null;
            bool started = false, owned = false;
            try
            {
                if (!StartLocked(process, StartLockWaitMilliseconds, true, out started))
                {
                    job?.Dispose();
                    return false;
                }
                lock (Sync)
                {
                    // A reload that began while this one started has already listed what it stops: this one is a few
                    // microseconds old and stops here instead of escaping it.
                    if (!stopping)
                    {
                        Active.Add(process, job);
                        if (write) Writes.Add(process);
                        owned = true;
                    }
                }
                if (!owned)
                {
                    job?.Dispose();
                    KillRoot(process);
                    return false;
                }
                try
                {
                    job?.Assign(process);
                    if (job != null) lock (Sync) Assigned.Add(process);
                }
                catch (System.ComponentModel.Win32Exception) when (HasExited(process))
                {
                    // Windows refuses to assign a process that already ended (a quick command can end before this line).
                    // It may have started a child first, which is in no job: the job proves nothing about it (see
                    // RunningInJob), so its output counts as complete only when its pipes close.
                }
                return true;
            }
            catch
            {
                if (owned) Stop(process);
                else
                {
                    job?.Dispose();
                    if (started) KillRoot(process);
                }
                throw;
            }
        }

        // Process.Start under the start lock; false when the editor began stopping while it waited, or Start returned false.
        private static bool StartLocked(Process process, int waitMilliseconds, bool refuseWhileStopping, out bool started)
        {
            started = false;
            bool locked = Monitor.TryEnter(StartSync, waitMilliseconds);
            try
            {
                if (refuseWhileStopping && stopping) return false;
                started = process.Start();
                return started;
            }
            finally
            {
                if (locked) Monitor.Exit(StartSync);
            }
        }

        /// <summary>Whether this process ended because it was stopped (timeout, cancel, reload), not by itself.</summary>
        internal static bool WasStopped(Process process)
        {
            lock (Sync) return StoppedRunning.TryGetValue(process, out _);
        }

        /// <summary>
        /// How many processes of this one's job still run (it and everything it started); -1 when unknown (no job). Zero
        /// after it exited: everything it and its children wrote is in its pipes, whoever else may still hold them.
        /// Unknown (-1) too when the process was not provably put in its job: a process that exited before its assignment may
        /// have started a child that still writes, outside the job.
        /// </summary>
        internal static int RunningInJob(Process process)
        {
            WindowsProcessJob job;
            lock (Sync)
            {
                if (!Assigned.Contains(process)) return -1;
                Active.TryGetValue(process, out job);
            }
            return job == null ? -1 : job.ActiveProcesses();
        }

        /// <summary>Stops the process and everything it started (if it still runs), then forgets it.</summary>
        internal static void Stop(Process process)
        {
            if (process == null) return;
            WindowsProcessJob job;
            lock (Sync)
            {
                Active.TryGetValue(process, out job);
                Active.Remove(process);
                Writes.Remove(process);
                Assigned.Remove(process);
            }
            bool running;
            try { running = !process.HasExited; }
            catch (InvalidOperationException) { running = false; }
            if (running)
                lock (Sync)
                    if (!StoppedRunning.TryGetValue(process, out _)) StoppedRunning.Add(process, null);
            // Closing the job kills what is left of it even when the root exited by itself (a descendant holding a pipe).
            if (job != null) { job.Dispose(); KillRoot(process); }
            else if (running) StopTreeWithoutJob(process);
            if (!running) return;
            try { process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void StopTreeWithoutJob(Process process)
        {
            try
            {
                if (process.HasExited) return;
                if (Path.DirectorySeparatorChar == '\\')
                {
                    string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                    if (File.Exists(taskkill)) RunCleanup(taskkill, "/T /F /PID " + process.Id, 750, false);
                }
                else StopUnixChildren(process.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException)
            { /* Already exited or inaccessible; still try the owned root below. */ }
            finally
            {
                KillRoot(process);
            }
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static void KillRoot(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void StopAll() => StopAll(WriteGraceMilliseconds);

        /// <summary>
        /// Before a reload and at quit: reads and kept-alive processes stop at once; a write in flight gets up to
        /// <paramref name="graceMilliseconds"/> to finish (its runner keeps reading its output meanwhile), and only then is
        /// stopped. Nothing new starts once this began.
        /// </summary>
        internal static void StopAll(int graceMilliseconds)
        {
            Process[] reads;
            Action[] listeners;
            lock (Sync)
            {
                stoppingAllowsWrites = true;
                stopping = true;
                reads = Active.Keys.Where(process => !Writes.Contains(process)).ToArray();
                listeners = StopListeners.ToArray();
            }
            foreach (Action listener in listeners)
            {
                try { listener(); }
                catch (Exception) { /* Stopping goes on for the others. */ }
            }
            foreach (Process process in reads) Stop(process);
            var waited = Stopwatch.StartNew();
            Process[] writes;
            lock (Sync) writes = Writes.ToArray();
            foreach (Process process in writes)
            {
                try
                {
                    int left = (int)Math.Max(0, graceMilliseconds - waited.ElapsedMilliseconds);
                    if (left > 0) process.WaitForExit(left);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            stoppingAllowsWrites = false;
            Process[] rest;
            lock (Sync) rest = Active.Keys.ToArray();
            foreach (Process process in rest) Stop(process);
        }

        /// <summary>For tests only: <see cref="StopAll(int)"/> as a reload runs it, then starting processes is allowed again.</summary>
        internal static void StopAllThenResume(int graceMilliseconds)
        {
            StopAll(graceMilliseconds);
            stopping = false;
        }

        private static void StopUnixChildren(int root)
        {
            string listing = RunCleanup("ps", "-A -o pid= -o ppid=", 500, true);
            if (listing == null) return;
            var children = new Dictionary<int, List<int>>();
            foreach (string line in listing.Split('\n'))
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || !int.TryParse(parts[1], out int parent)) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(pid);
            }
            var order = new List<int>();
            var visited = new HashSet<int> { root };
            var stack = new Stack<int>(); stack.Push(root);
            while (stack.Count > 0)
            {
                int parent = stack.Pop();
                if (!children.TryGetValue(parent, out var list)) continue;
                foreach (int child in list) if (visited.Add(child)) { order.Add(child); stack.Push(child); }
            }
            order.Reverse();
            if (order.Count > 0) RunCleanup("kill", "-9 " + string.Join(" ", order), 500, false);
        }

        // A short helper run outside the ownership (it must work while everything is stopping), under the start lock for a
        // short while only. Its output when asked, else empty; null when it could not run.
        private static string RunCleanup(string file, string arguments, int timeoutMilliseconds, bool readOutput)
        {
            try
            {
                using (var process = new Process
                       {
                           StartInfo = new ProcessStartInfo(file, arguments)
                           {
                               UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = readOutput
                           }
                       })
                {
                    if (!StartLocked(process, 500, false, out _)) return null;
                    var elapsed = Stopwatch.StartNew();
                    var output = new MemoryStream();
                    var pipe = readOutput ? new ProcessPipe(process.StandardOutput.BaseStream) : null;
                    var chunk = new byte[8192];
                    while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
                    {
                        if (pipe == null)
                        {
                            if (process.WaitForExit(Math.Max(1, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds))) break;
                            continue;
                        }
                        int read = pipe.Read(chunk);
                        if (read < 0) break;
                        if (read > 0) output.Write(chunk, 0, read);
                        else Thread.Sleep(5);
                    }
                    KillRoot(process);
                    return System.Text.Encoding.UTF8.GetString(output.ToArray());
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                return null;
            }
        }
    }
}
