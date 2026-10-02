using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Processes
{
    [InitializeOnLoad]
    internal static class EditorProcessLifetime
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Process, WindowsProcessJob> Active = new Dictionary<Process, WindowsProcessJob>();
        private static volatile bool stopping;
        internal static bool IsStopping => stopping;
        internal static int ActiveCount { get { lock (Sync) return Active.Count; } }

        static EditorProcessLifetime()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorApplication.quitting += StopAll;
        }

        internal static bool Start(Process process)
        {
            lock (Sync)
            {
                if (stopping) return false;
                WindowsProcessJob job = Path.DirectorySeparatorChar == '\\' ? WindowsProcessJob.Create() : null;
                bool started = false;
                try
                {
                    if (!process.Start()) { job?.Dispose(); return false; }
                    started = true;
                    job?.Assign(process);
                    Active.Add(process, job);
                }
                catch
                {
                    job?.Dispose();
                    try { if (started && !process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                    throw;
                }
                return true;
            }
        }

        internal static void Remove(Process process)
        {
            WindowsProcessJob job;
            lock (Sync) { Active.TryGetValue(process, out job); Active.Remove(process); }
            job?.Dispose();
        }

        private static void StopAll()
        {
            Process[] processes;
            lock (Sync) { stopping = true; processes = Active.Keys.ToArray(); }
            foreach (Process process in processes) StopTree(process);
        }

        internal static void StopTree(Process process)
        {
            WindowsProcessJob job;
            lock (Sync) Active.TryGetValue(process, out job);
            if (job != null) { job.Dispose(); return; }
            try
            {
                if (process == null || process.HasExited) return;
                if (Path.DirectorySeparatorChar == '\\')
                {
                    string taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                    if (File.Exists(taskkill))
                        EditorProcessRunner.RunCleanup(new ProcessStartInfo(taskkill, "/T /F /PID " + process.Id)
                        { UseShellExecute = false, CreateNoWindow = true }, 750);
                }
                else StopUnixChildren(process.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is IOException)
            { /* Already exited or inaccessible; still try the owned root below. */ }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        private static void StopUnixChildren(int root)
        {
            var listing = EditorProcessRunner.RunCleanup(new ProcessStartInfo("ps", "-A -o pid= -o ppid=")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }, 500);
            if (!listing.Success) return;
            var children = new Dictionary<int, List<int>>();
            foreach (string line in listing.StandardOutput.Split('\n'))
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
            if (order.Count > 0)
                EditorProcessRunner.RunCleanup(new ProcessStartInfo("kill", "-9 " + string.Join(" ", order))
                { UseShellExecute = false, CreateNoWindow = true }, 500);
        }
    }
}
