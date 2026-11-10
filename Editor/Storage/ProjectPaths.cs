using System.IO;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// The project's own folders as full paths, never relative to the current directory (which a tool or a process may
    /// change). Orbiters' per-project caches go under <see cref="OrbitersLibrary"/>.
    /// </summary>
    public static class ProjectPaths
    {
        private static string root;

        /// <summary>The project folder (the one holding Assets).</summary>
        public static string Root => root ??= Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? ".");

        public static string Library => Path.Combine(Root, "Library");

        /// <summary>Library/Orbiters: caches Orbiters' tools keep for this project (rebuilt when Library is deleted).</summary>
        public static string OrbitersLibrary => Path.Combine(Root, "Library", "Orbiters");

        public static string ProjectSettings => Path.Combine(Root, "ProjectSettings");

        public static string UserSettings => Path.Combine(Root, "UserSettings");

        public static string Temp => Path.Combine(Root, "Temp");

        /// <summary>A path inside <see cref="OrbitersLibrary"/>, its folder created.</summary>
        public static string InOrbitersLibrary(params string[] parts)
        {
            string path = Path.Combine(OrbitersLibrary, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? OrbitersLibrary);
            return path;
        }
    }
}
