using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>Another application's window brought in front of the others (Show Substance, Show Blender...).</summary>
    public static class AppWindows
    {
        /// <summary>
        /// Brings the main window of the running <paramref name="processName"/> to the front, restored when it was minimised;
        /// false when no such application runs with a window. Called from a click in Unity: Windows lets the application in
        /// front hand the focus to another one.
        /// </summary>
        public static bool BringToFront(string processName)
        {
            try
            {
                if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    var start = new ProcessStartInfo("osascript", "-e \"tell application \\\"" + processName + "\\\" to activate\"")
                        { UseShellExecute = false, CreateNoWindow = true };
                    Process.Start(start)?.Dispose();
                    return true;
                }
                if (Application.platform != RuntimePlatform.WindowsEditor) return false;
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    var window = processes.Select(p => p.MainWindowHandle).FirstOrDefault(handle => handle != IntPtr.Zero);
                    if (window == IntPtr.Zero) return false;
                    if (IsIconic(window)) ShowWindow(window, Restore);
                    return SetForegroundWindow(window);
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch (Exception) { return false; }
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
    }
}
