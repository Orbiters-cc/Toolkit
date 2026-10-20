using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// Another application's window brought in front of the others (Show Substance, Blender opened from Unity...). Windows
    /// lets the application in front hand the focus over: called from a click in Unity, or right after Unity started the
    /// application.
    /// </summary>
    public static class AppWindows
    {
        /// <summary>
        /// Brings the main window of the running <paramref name="processName"/> to the front, restored when it was minimised;
        /// false when no such application runs with a window.
        /// </summary>
        public static bool BringToFront(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    // macOS: the running application by its process (asking for it by name would launch it when it isn't).
                    if (Application.platform == RuntimePlatform.OSXEditor)
                        return processes.Any(p => BringToFront(p));
                    var shown = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                    return shown != null && BringToFront(shown);
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>Brings this process's main window to the front, restored when it was minimised; false without a window.</summary>
        public static bool BringToFront(Process process)
        {
            try
            {
                if (process == null || process.HasExited) return false;
                if (Application.platform == RuntimePlatform.OSXEditor)
                    return Activate("tell application \"System Events\" to set frontmost of (first process whose unix id is " + process.Id + ") to true");
                if (Application.platform != RuntimePlatform.WindowsEditor) return false;
                process.Refresh();
                var window = process.MainWindowHandle;
                if (window == IntPtr.Zero) return false;
                if (IsIconic(window)) ShowWindow(window, Restore);
                return SetForegroundWindow(window);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The running <paramref name="processName"/> whose window title holds <paramref name="text"/> (Blender's title holds
        /// the file it has open), or null. The caller disposes it.
        /// </summary>
        public static Process FindShowing(string processName, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Process found = null;
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch (Exception) { return null; }
            foreach (var process in processes)
            {
                bool match = false;
                try { match = found == null && !process.HasExited && Title(process.MainWindowHandle).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0; }
                catch (Exception) { }
                if (match) found = process;
                else process.Dispose();
            }
            return found;
        }

        // Started applications waiting for their window to come forward.
        private static readonly List<(Process Process, double Until)> Starting = new List<(Process, double)>();

        /// <summary>
        /// Brings an application Unity just started to the front once its window shows (an application that opens a big
        /// file shows it after a while, behind Unity otherwise). Gives up after <paramref name="seconds"/>.
        /// </summary>
        public static void BringToFrontOnceShown(Process process, double seconds = 120)
        {
            if (process == null) return;
            // Elsewhere there is no window handle to wait for: an application started from Unity comes to the front itself.
            if (Application.platform != RuntimePlatform.WindowsEditor) return;
            // It may take the focus itself too, as long as Unity is in front.
            try { AllowSetForegroundWindow(process.Id); } catch (Exception) { }
            Starting.Add((process, EditorApplication.timeSinceStartup + seconds));
            if (Starting.Count == 1) EditorApplication.update += Watch;
        }

        private static void Watch()
        {
            double now = EditorApplication.timeSinceStartup;
            for (int i = Starting.Count - 1; i >= 0; i--)
            {
                var (process, until) = Starting[i];
                bool done;
                try
                {
                    process.Refresh();
                    // Once: an application that couldn't come forward isn't pushed again later, when the user is elsewhere.
                    bool shown = !process.HasExited && process.MainWindowHandle != IntPtr.Zero;
                    if (shown) BringToFront(process);
                    done = shown || process.HasExited || now > until;
                }
                catch (Exception) { done = true; }
                if (!done) continue;
                Starting.RemoveAt(i);
                process.Dispose();
            }
            if (Starting.Count == 0) EditorApplication.update -= Watch;
        }

        // The window's title, read from the window: Unity's Mono leaves Process.MainWindowTitle empty.
        private static string Title(IntPtr window)
        {
            if (window == IntPtr.Zero || Application.platform != RuntimePlatform.WindowsEditor) return "";
            int length = GetWindowTextLength(window);
            if (length <= 0) return "";
            var text = new System.Text.StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        // The script names a process by its number only: nothing in it comes from a name that could hold a quote.
        private static bool Activate(string script)
        {
            var start = new ProcessStartInfo("osascript", "-e \"" + script.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") { UseShellExecute = false, CreateNoWindow = true };
            Process.Start(start)?.Dispose();
            return true;
        }

        private const int Restore = 9;
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);
    }
}
