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
    /// Which project assets use which: every file under Assets/ that can use another (all but folders and the kinds that
    /// never do, as pictures, sounds and scripts: override controllers, sprite atlases, shaders and whatever comes next count
    /// too) is asked for its direct dependencies, a slice per editor tick so the editor stays responsive. Tools use it before
    /// replacing or removing files (an imported material another avatar's prefab still uses is kept).
    /// </summary>
    public sealed partial class ProjectReferences
    {
        // Files that never use another asset: not asked (a picture, a sound, a script, plain data).
        private static readonly HashSet<string> Leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".iff", ".pict",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".mod", ".it", ".s3m", ".xm",
            ".cs", ".dll", ".txt", ".json", ".bytes", ".md", ".csv", ".xml", ".yaml", ".html", ".htm", ".py",
        };

        /// <summary>True when the scan asks <paramref name="path"/> what it uses (it may use another asset).</summary>
        public static bool MayUseOthers(string path) => !string.IsNullOrEmpty(path) && !Leaves.Contains(Path.GetExtension(path));

        private readonly Dictionary<string, HashSet<string>> usedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public int Scanned { get; private set; }

        /// <summary>The assets directly using <paramref name="path"/>, leaving out <paramref name="ignore"/>.</summary>
        public IReadOnlyCollection<string> UsersOf(string path, ICollection<string> ignore = null) =>
            usedBy.TryGetValue(path, out var users) ? (IReadOnlyCollection<string>)users.Where(u => ignore == null || !ignore.Contains(u)).ToList() : Array.Empty<string>();

        /// <summary>
        /// Builds the map. <paramref name="progress"/> receives 0..1. Main thread; yields between slices. Each file's
        /// references come from the cache <see cref="ReachableAsync"/> keeps in Library/Orbiters (read again only when the
        /// file, its .meta or Unity's import of it changed), so a scan after the first mostly lists files; only references to
        /// files under Assets/ are kept.
        /// </summary>
        public static async Task<ProjectReferences> ScanAsync(Action<float, string> progress = null, CancellationToken cancellation = default)
        {
            if (!watching)
            {
                watching = true;
                EditorChangeBus.Changed += changes =>
                {
                    if (changes.Project || changes.Imported.Count + changes.Deleted.Count + changes.Moved.Count + changes.MovedFrom.Count > 0) filesRevision++;
                };
            }
            // Nothing imported, deleted or moved since the last complete scan of this script session: its map still stands.
            EditorChangeBus.Flush();
            if (last != null && last.revision == filesRevision)
            {
                progress?.Invoke(1f, "Checked " + last.Scanned + " assets");
                return last;
            }
            // Only a scan that finishes against files that did not change meanwhile is kept (a cancelled or failed one never).
            last = null;
            var map = new ProjectReferences { revision = filesRevision };
            // Every asset, not a list of types: a kind left out (an override controller using a clip) would make what it uses
            // look unused.
            var assets = AssetDatabase.FindAssets("", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => MayUseOthers(p) && !AssetDatabase.IsValidFolder(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Store.Load();
            var started = DateTime.UtcNow;
            var saved = DateTime.UtcNow;
            try
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    foreach (string guid in Store.Uses(assets[i]))
                    {
                        string dependency = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(dependency) || string.Equals(dependency, assets[i], StringComparison.OrdinalIgnoreCase)) continue;
                        if (!map.usedBy.TryGetValue(dependency, out var users)) map.usedBy[dependency] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        users.Add(assets[i]);
                    }
                    map.Scanned++;
                    if ((DateTime.UtcNow - started).TotalMilliseconds > 12)
                    {
                        if ((DateTime.UtcNow - saved).TotalSeconds > 10)
                        {
                            Store.Save();
                            saved = DateTime.UtcNow;
                        }
                        progress?.Invoke((i + 1f) / assets.Count, $"Checking what uses what… {i + 1} of {assets.Count}");
                        await Task.Yield();
                        started = DateTime.UtcNow;
                    }
                }
            }
            finally
            {
                Store.Save();
            }
            progress?.Invoke(1f, "Checked " + assets.Count + " assets");
            // A file changed during the scan leaves the next one to read it.
            EditorChangeBus.Flush();
            if (map.Current) last = map;
            return map;
        }

        /// <summary>
        /// False once a file was imported, deleted or moved after this map's scan started (also during it): what it says may
        /// be out of date, and nothing should be deleted on its word.
        /// </summary>
        public bool Current
        {
            get
            {
                EditorChangeBus.Flush();
                return revision == filesRevision;
            }
        }

        private long revision;
        private static ProjectReferences last;
        private static bool watching;
        private static long filesRevision;

        // Each asset's direct dependencies, read again only when the asset or what it uses changed.
        private static readonly Dictionary<string, (UnityEngine.Hash128 Hash, string[] Dependencies)> Direct = new Dictionary<string, (UnityEngine.Hash128, string[])>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The assets under Assets/ that <paramref name="filters"/> find (AssetDatabase.FindAssets filters, e.g. "t:Prefab",
        /// "t:Scene") directly using each of <paramref name="paths"/>, at once on the main thread (for a check before writing
        /// an asset other things may share). Only the assets that changed since an earlier call are read again.
        /// </summary>
        public static Dictionary<string, List<string>> DirectUsers(IEnumerable<string> paths, params string[] filters)
        {
            var wanted = new HashSet<string>(paths.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
            var users = wanted.ToDictionary(p => p, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return users;
            var assets = filters.SelectMany(f => AssetDatabase.FindAssets(f, new[] { "Assets" })).Distinct().Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string asset in assets)
            {
                var hash = AssetDatabase.GetAssetDependencyHash(asset);
                if (!Direct.TryGetValue(asset, out var known) || known.Hash != hash)
                    Direct[asset] = known = (hash, AssetDatabase.GetDependencies(asset, false));
                foreach (string dependency in known.Dependencies)
                    if (wanted.Contains(dependency) && !string.Equals(dependency, asset, StringComparison.OrdinalIgnoreCase)) users[dependency].Add(asset);
            }
            return users;
        }
    }
}
