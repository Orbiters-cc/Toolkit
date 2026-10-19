using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// Finds the Blender executables on this machine (the chosen one, PATH, the Blender Foundation folders, Steam)
    /// and remembers which one the Orbiters tools use (EditorPrefs <c>Orbiters.Blender.Executable</c>).
    /// </summary>
    public static class BlenderInstalls
    {
        public const string ExecutablePrefsKey = "Orbiters.Blender.Executable";
        private const double CacheDurationSeconds = 30.0d;

        private static List<Installation> cachedInstallations;
        private static double nextRefreshTime;

        public sealed class Installation
        {
            public string ExecutablePath;
            public string Version;

            public string DisplayLabel
            {
                get
                {
                    string versionText = string.IsNullOrWhiteSpace(Version) ? "Unknown version" : Version;
                    return versionText + " - " + ExecutablePath;
                }
            }
        }

        public static List<Installation> GetInstallations(bool forceRefresh = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!forceRefresh && cachedInstallations != null && now < nextRefreshTime)
                return new List<Installation>(cachedInstallations);
            nextRefreshTime = now + CacheDurationSeconds;
            cachedInstallations = ScanInstallations();
            return new List<Installation>(cachedInstallations);
        }

        /// <summary>The executable the user chose, else the newest one found, else an empty string.</summary>
        public static string GetSelectedExecutablePath()
        {
            string configured = NormalizeExecutablePath(EditorPrefs.GetString(ExecutablePrefsKey, ""));
            if (IsExecutableFile(configured)) return configured;
            var installations = GetInstallations();
            return installations.FirstOrDefault()?.ExecutablePath ?? "";
        }

        public static void SetSelectedExecutablePath(string path)
        {
            string normalized = NormalizeExecutablePath(path);
            if (string.IsNullOrWhiteSpace(normalized)) EditorPrefs.DeleteKey(ExecutablePrefsKey);
            else EditorPrefs.SetString(ExecutablePrefsKey, normalized);
        }

        public static void ClearSelectedExecutablePath() => EditorPrefs.DeleteKey(ExecutablePrefsKey);

        /// <summary>Opens a file panel for the executable; returns the chosen path or an empty string.</summary>
        public static string PickExecutable()
        {
            string extension = Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "";
            string selected = EditorUtility.OpenFilePanel("Select Blender executable", "", extension);
            if (string.IsNullOrWhiteSpace(selected)) return "";
            string normalized = NormalizeExecutablePath(selected);
            if (!IsExecutableFile(normalized))
            {
                EditorUtility.DisplayDialog("Invalid Blender Path", "The selected file does not exist.", "OK");
                return "";
            }
            SetSelectedExecutablePath(normalized);
            GetInstallations(forceRefresh: true);
            return normalized;
        }

        public static string NormalizeExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
                fullPath = Path.Combine(fullPath, Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender");
            return fullPath;
        }

        /// <summary>"4.2.3", "5.2.0"... from an installation's version text, or null.</summary>
        public static Version ParseVersion(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText)) return null;
            string digits = new string(versionText.Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
            return Version.TryParse(digits, out var version) ? version : null;
        }

        private static List<Installation> ScanInstallations()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIfExecutable(paths, EditorPrefs.GetString(ExecutablePrefsKey, ""));
            AddIfExecutable(paths, FindExecutableOnPath(Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender"));
            foreach (string candidate in GetStandardCandidates()) AddIfExecutable(paths, candidate);
            return paths
                .Select(path => new Installation { ExecutablePath = path, Version = ReadBlenderVersion(path) })
                .OrderByDescending(item => ParseVersion(item.Version) ?? new Version(0, 0))
                .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddIfExecutable(HashSet<string> paths, string path)
        {
            string normalized = NormalizeExecutablePath(path);
            if (IsExecutableFile(normalized)) paths.Add(normalized);
        }

        private static bool IsExecutableFile(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        private static string FindExecutableOnPath(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName)) return "";
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate)) return candidate;
            }
            return "";
        }

        private static IEnumerable<string> GetStandardCandidates()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                foreach (string candidate in GetWindowsCandidates()) yield return candidate;
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                yield return "/Applications/Blender.app/Contents/MacOS/Blender";
            }
            else
            {
                yield return "/usr/bin/blender";
                yield return "/usr/local/bin/blender";
                yield return "/snap/bin/blender";
            }
        }

        private static IEnumerable<string> GetWindowsCandidates()
        {
            foreach (string root in InstalledApps.ProgramRoots())
            {
                string foundation = Path.Combine(root, "Blender Foundation");
                foreach (string directory in InstalledApps.Directories(foundation).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(directory, "blender.exe");
                    foreach (string child in InstalledApps.Directories(directory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                        yield return Path.Combine(child, "blender.exe");
                }
            }
            foreach (string blenderDirectory in InstalledApps.SteamApps("Blender"))
                yield return Path.Combine(blenderDirectory, "blender.exe");
        }

        private static string ReadBlenderVersion(string executablePath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return "";
                    if (!process.WaitForExit(2500))
                    {
                        try { process.Kill(); } catch { }
                        return VersionFromPath(executablePath);
                    }
                    string output = process.StandardOutput.ReadToEnd();
                    string firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstLine)) return firstLine.Trim();
                }
            }
            catch
            {
                // Fall through to folder-name version guessing.
            }
            return VersionFromPath(executablePath);
        }

        private static string VersionFromPath(string executablePath)
        {
            string directory = Path.GetDirectoryName(executablePath);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string name = Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(name) && name.IndexOf("Blender", StringComparison.OrdinalIgnoreCase) >= 0) return name;
                directory = Path.GetDirectoryName(directory);
            }
            return "";
        }
    }
}
