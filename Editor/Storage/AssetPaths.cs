using System;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// Paths of project assets: folders created with their parents, file names made from display names (clip, model or
    /// object names), and asset paths of files on disk.
    /// </summary>
    public static class AssetPaths
    {
        private const string Reserved = "<>:\"/\\|?*";

        /// <summary>Forward slashes, no trailing slash; empty for null.</summary>
        public static string Normalize(string path) => (path ?? "").Replace('\\', '/').TrimEnd('/');

        /// <summary>Whether <paramref name="path"/> is an asset path: under Assets or a package.</summary>
        public static bool IsProjectPath(string path) =>
            path == "Assets" || (path ?? "").StartsWith("Assets/", StringComparison.Ordinal) || (path ?? "").StartsWith("Packages/", StringComparison.Ordinal);

        /// <summary>
        /// Creates <paramref name="folder"/> ("Assets/A/B") and its missing parents; false when it cannot be made. Call it outside
        /// StartAssetEditing: a folder created inside a batch is not a valid parent until the batch ends.
        /// </summary>
        public static bool EnsureFolder(string folder)
        {
            folder = Normalize(folder);
            if (AssetDatabase.IsValidFolder(folder)) return true;
            string[] parts = folder.Split('/');
            if (!AssetDatabase.IsValidFolder(parts[0])) return false;
            string parent = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = parent + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, parts[i]))) return false;
                parent = next;
            }
            // CreateFolder picks another name when a file already holds this one.
            return AssetDatabase.IsValidFolder(folder);
        }

        /// <summary>
        /// A file name (without extension) for <paramref name="name"/> on every platform: reserved and control characters become
        /// "_", surrounding spaces and trailing dots go, and device names (CON, NUL…) get a "_".
        /// </summary>
        public static string FileName(string name, string fallback = "Untitled")
        {
            string result = new string((name ?? "").Select(c => char.IsControl(c) || Reserved.IndexOf(c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (result.Length == 0) result = fallback;
            if (!SafePaths.IsDeviceName(result)) return result;
            int dot = result.IndexOf('.');
            return result.Insert(dot < 0 ? result.Length : dot, "_");
        }

        /// <summary>The asset path ("Assets/…", "Packages/…") of a file or folder on disk, or null when it is outside the project.</summary>
        public static string FromFullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full, root;
            try
            {
                full = Normalize(Path.GetFullPath(path));
                root = Normalize(Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? "")) + "/";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            string relative = full.Substring(root.Length);
            // A path typed or dropped with other casing still names the same folder on Windows.
            foreach (string top in new[] { "Assets", "Packages" })
                if (relative.Equals(top, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(top + "/", StringComparison.OrdinalIgnoreCase))
                    relative = top + relative.Substring(top.Length);
            return IsProjectPath(relative) && relative != "Packages" ? relative : null;
        }

        /// <summary>Whether an asset, or a file or folder Unity has not imported yet, is at <paramref name="assetPath"/>.</summary>
        public static bool Exists(string assetPath)
        {
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets))) return true;
            string physical = FileUtil.GetPhysicalPath(assetPath);
            return !string.IsNullOrEmpty(physical) && (File.Exists(physical) || Directory.Exists(physical));
        }
    }
}
