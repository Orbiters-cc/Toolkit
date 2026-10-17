using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbiters.Toolkit.Editor.Processes
{
    /// <summary>Where desktop applications are usually installed, to find one without asking (Blender, Substance 3D Painter...).</summary>
    public static class InstalledApps
    {
        /// <summary>The program folders of this machine (64 and 32 bits) and the user's local application data.</summary>
        public static IEnumerable<string> ProgramRoots() => new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Steam's library folders (its own and those listed in libraryfolders.vdf), each holding steamapps/common.</summary>
        public static IEnumerable<string> SteamLibraries()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string baseRoot in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                     })
            {
                if (string.IsNullOrWhiteSpace(baseRoot)) continue;
                string steamRoot = Path.Combine(baseRoot, "Steam");
                if (Directory.Exists(steamRoot)) roots.Add(steamRoot);
            }

            foreach (string root in roots.ToList())
            {
                string libraryFoldersPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersPath)) continue;
                try
                {
                    foreach (string line in File.ReadAllLines(libraryFoldersPath))
                    {
                        int pathIndex = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                        if (pathIndex < 0) continue;
                        string[] parts = line.Split('"');
                        if (parts.Length < 4) continue;
                        string path = parts[3].Replace("\\\\", "\\");
                        if (Directory.Exists(path)) roots.Add(path);
                    }
                }
                catch
                {
                    // Steam library discovery is best-effort.
                }
            }

            return roots;
        }

        /// <summary>The folders in <c>steamapps/common</c> of every Steam library whose name contains <paramref name="nameFragment"/>.</summary>
        public static IEnumerable<string> SteamApps(string nameFragment) => SteamLibraries()
            .SelectMany(root => Directories(Path.Combine(root, "steamapps", "common")))
            .Where(path => Path.GetFileName(path).IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The folders in <paramref name="path"/>, none when it doesn't exist or can't be read.</summary>
        public static IEnumerable<string> Directories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
