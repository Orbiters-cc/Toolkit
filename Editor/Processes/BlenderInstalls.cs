using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>
    /// Finds the Blender executables on this machine (the chosen one, PATH, the Blender Foundation folders, Steam)
    /// and remembers which one the Orbiters tools use (EditorPrefs <c>Orbiters.Blender.Executable</c>). Finding them runs
    /// none of them: versions come from the executable's version resource (Windows) or the app's Info.plist (macOS); only
    /// on Linux is <c>blender --version</c> asked, once per executable and editor session. Release builds come before
    /// alpha, beta and release-candidate ones, then the newest first.
    /// </summary>
    public static class BlenderInstalls
    {
        public const string ExecutablePrefsKey = "Orbiters.Blender.Executable";
        private const double CacheDurationSeconds = 30.0d;
        private const string ProbedVersionsKey = "Orbiters.Blender.ProbedVersions";

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

        /// <summary>The executable the user chose, else the newest release found, else an empty string.</summary>
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

        /// <summary>
        /// The full path of an executable, or "" for one that cannot be a path (quotes, reserved characters). A folder means
        /// the Blender in it, and a macOS app bundle the executable inside it.
        /// </summary>
        public static string NormalizeExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
                if (Directory.Exists(fullPath))
                {
                    if (fullPath.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                        fullPath = Path.Combine(fullPath, "Contents", "MacOS", "Blender");
                    else
                        fullPath = Path.Combine(fullPath, Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender");
                }
                return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return "";
            }
        }

        /// <summary>"4.2.3", "5.2.0"... from an installation's version text (its first dotted number), or null.</summary>
        public static Version ParseVersion(string versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText)) return null;
            var match = Regex.Match(versionText, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success) return null;
            return Version.TryParse(match.Value, out var version) ? version : null;
        }

        /// <summary>Whether the version text or the Blender folder holding it names an alpha, beta or release-candidate build.</summary>
        public static bool IsPrerelease(string versionText, string executablePath = null)
        {
            string text = (versionText ?? "") + " " + (executablePath != null ? VersionFromPath(executablePath) : "");
            return Regex.IsMatch(text, @"(?i)\b(alpha|beta|rc\d*|release[ -]?candidate|daily|experimental)\b|[-+](alpha|beta|rc)");
        }

        private static List<Installation> ScanInstallations()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIfExecutable(paths, EditorPrefs.GetString(ExecutablePrefsKey, ""));
            AddIfExecutable(paths, FindExecutableOnPath(Application.platform == RuntimePlatform.WindowsEditor ? "blender.exe" : "blender"));
            foreach (string candidate in GetStandardCandidates()) AddIfExecutable(paths, candidate);
            return paths
                .Select(path => new Installation { ExecutablePath = path, Version = ReadBlenderVersion(path) })
                .OrderBy(item => IsPrerelease(item.Version, item.ExecutablePath) ? 1 : 0)
                .ThenByDescending(item => ParseVersion(item.Version) ?? new Version(0, 0))
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
                // Windows allows quoted entries ("C:\Program Files\Blender"), which Path.Combine refuses.
                string folder = directory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    string candidate = Path.Combine(folder, executableName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
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
                foreach (string app in InstalledApps.Directories("/Applications").Where(d => Path.GetFileName(d).StartsWith("Blender", StringComparison.OrdinalIgnoreCase) && d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
                    yield return Path.Combine(app, "Contents", "MacOS", "Blender");
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

        // Read without running the executable where the platform says it: a scan neither waits on Blender nor starts
        // whatever lies in a folder another user can write to.
        private static string ReadBlenderVersion(string executablePath)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    var info = FileVersionInfo.GetVersionInfo(executablePath);
                    string version = !string.IsNullOrWhiteSpace(info.ProductVersion) ? info.ProductVersion : info.FileVersion;
                    if (ParseVersion(version) != null) return "Blender " + version.Trim();
                }
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    string version = BundleVersion(executablePath);
                    if (version != null) return "Blender " + version;
                }
                else
                {
                    string version = ProbedVersion(executablePath);
                    if (!string.IsNullOrWhiteSpace(version)) return version;
                }
            }
            catch (Exception)
            {
                // Fall through to folder-name version guessing.
            }
            return VersionFromPath(executablePath);
        }

        // Blender.app/Contents/MacOS/Blender → Blender.app/Contents/Info.plist's CFBundleShortVersionString.
        private static string BundleVersion(string executablePath)
        {
            string contents = Path.GetDirectoryName(Path.GetDirectoryName(executablePath));
            string plist = contents != null ? Path.Combine(contents, "Info.plist") : null;
            if (plist == null || !File.Exists(plist)) return null;
            var match = Regex.Match(File.ReadAllText(plist), @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        [Serializable] private sealed class Probe { public string key, version; }
        [Serializable] private sealed class Probes { public List<Probe> items = new List<Probe>(); }

        // Linux: the first line of `blender --version`, kept for the editor session (a script reload keeps it) by the
        // executable's path, size and time.
        private static string ProbedVersion(string executablePath)
        {
            var file = new FileInfo(executablePath);
            string key = executablePath + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            Probes probes;
            try { probes = JsonUtility.FromJson<Probes>(SessionState.GetString(ProbedVersionsKey, "")) ?? new Probes(); }
            catch (Exception) { probes = new Probes(); }
            var known = probes.items.FirstOrDefault(p => p.key == key);
            if (known != null) return known.version;
            string version = RunVersion(executablePath);
            probes.items.RemoveAll(p => p.key.StartsWith(executablePath + "|", StringComparison.Ordinal));
            probes.items.Add(new Probe { key = key, version = version });
            SessionState.SetString(ProbedVersionsKey, JsonUtility.ToJson(probes));
            return version;
        }

        private static string RunVersion(string executablePath)
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
                // Read while it runs: a full pipe would otherwise hold it until the timeout.
                var output = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(2500))
                {
                    try { process.Kill(); } catch { }
                    return "";
                }
                string firstLine = output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return string.IsNullOrWhiteSpace(firstLine) ? "" : firstLine.Trim();
            }
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
