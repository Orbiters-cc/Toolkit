using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What importing a .unitypackage would do to the project, before it is imported. Unity's importer writes every entry
    /// to the asset that already has its GUID: a package carrying a prefab, material or mesh the project already has with
    /// other content replaces it, also for every avatar and scene using it. This lists those replacements so a tool can
    /// ask first, and leaves out entries under Packages/, which belong to VPM and other package managers, as well as entries
    /// whose GUID Unity would resolve to a file there.
    /// </summary>
    public sealed class UnityPackagePreview
    {
        public sealed class Change
        {
            public string Guid, PackagePath, ProjectPath;
            /// <summary>The project keeps the asset at another path than the package names: Unity updates it in place.</summary>
            public bool Moved => !string.Equals(PackagePath, ProjectPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>New files.</summary>
        public readonly List<Change> Added = new List<Change>();
        /// <summary>Files the project has with other content: importing replaces them.</summary>
        public readonly List<Change> Replaced = new List<Change>();
        /// <summary>Files the project already has with the same content.</summary>
        public readonly List<Change> Unchanged = new List<Change>();
        /// <summary>
        /// Entries that would write outside Assets/ (VPM packages and other managed folders), by their name or through a GUID
        /// the project already has there: never imported by the caller. The project path for the latter.
        /// </summary>
        public readonly List<string> Skipped = new List<string>();
        public bool HasConflicts => Replaced.Count > 0;
        /// <summary>The GUIDs to import: everything under Assets/.</summary>
        public IEnumerable<string> ImportGuids => Added.Concat(Replaced).Concat(Unchanged).Select(c => c.Guid);
        /// <summary>Code the import would write: its entries under Assets/ and the existing files their GUIDs update.</summary>
        public List<string> CodeFiles => CodeContent.Filter(Added.Concat(Replaced).Concat(Unchanged).SelectMany(c => new[] { c.PackagePath, c.ProjectPath }));

        /// <summary>Main thread: reads the project's side (only for entries under Assets/), then hashes on a worker thread.</summary>
        public static async Task<UnityPackagePreview> CreateAsync(string packagePath, UnityPackageIndex index, CancellationToken cancellation = default)
        {
            var existing = index.Entries.Where(e => e.Path.StartsWith("Assets/", StringComparison.Ordinal))
                .ToDictionary(e => e.Guid, e => AssetDatabase.GUIDToAssetPath(e.Guid), StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(existing.Values.Where(p => !string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p)), StringComparer.OrdinalIgnoreCase);
            string root = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            return await Task.Run(() =>
            {
                var preview = new UnityPackagePreview();
                var hashes = UnityPackageFiles.AssetHashes(packagePath);
                foreach (var entry in index.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!entry.Path.StartsWith("Assets/", StringComparison.Ordinal)) { preview.Skipped.Add(entry.Path); continue; }
                    existing.TryGetValue(entry.Guid, out string projectPath);
                    // Unity writes an entry to the asset holding its GUID, wherever it is: a VPM package file stays as it is.
                    if (!string.IsNullOrEmpty(projectPath) && !projectPath.StartsWith("Assets/", StringComparison.Ordinal)) { preview.Skipped.Add(projectPath); continue; }
                    var change = new Change { Guid = entry.Guid, PackagePath = entry.Path, ProjectPath = string.IsNullOrEmpty(projectPath) ? entry.Path : projectPath };
                    string file = string.IsNullOrEmpty(projectPath) ? null : Path.Combine(root, projectPath);
                    if (file == null || folders.Contains(projectPath) || (!File.Exists(file) && !Directory.Exists(file))) { preview.Added.Add(change); continue; }
                    // A folder record has no content to replace.
                    if (!hashes.TryGetValue(entry.Guid, out string packageHash) || Directory.Exists(file)) { preview.Unchanged.Add(change); continue; }
                    string projectHash = UnityPackageFiles.FileHash(file);
                    (string.Equals(projectHash, packageHash, StringComparison.OrdinalIgnoreCase) ? preview.Unchanged : preview.Replaced).Add(change);
                }
                return preview;
            }, cancellation);
        }

        /// <summary>The kind of a replaced file, for summaries ("3 materials, 1 prefab").</summary>
        public static string Kind(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".prefab": return "prefab";
                case ".mat": return "material";
                case ".fbx": case ".obj": case ".blend": case ".mesh": case ".asset": return "model or asset";
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".tif": case ".tiff": case ".exr": return "texture";
                case ".anim": case ".controller": case ".overrideController": case ".mask": return "animation";
                case ".shader": case ".cginc": case ".hlsl": case ".shadergraph": return "shader";
                case ".cs": case ".dll": case ".asmdef": return "script";
                default: return "file";
            }
        }

        /// <summary>"2 materials, 1 prefab and 4 textures".</summary>
        public static string Summary(IEnumerable<Change> changes)
        {
            var groups = changes.GroupBy(c => Kind(c.ProjectPath)).OrderByDescending(g => g.Count())
                .Select(g => g.Count() + " " + (g.Count() == 1 ? g.Key : Plural(g.Key))).ToList();
            if (groups.Count == 0) return "nothing";
            return groups.Count == 1 ? groups[0] : string.Join(", ", groups.Take(groups.Count - 1)) + " and " + groups[groups.Count - 1];
        }

        private static string Plural(string kind) => kind == "model or asset" ? "models or assets" : kind + "s";
    }
}
