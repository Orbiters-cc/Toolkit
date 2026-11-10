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
        // What each file read uses (paths), and its file, .meta and import as they were when it was read: what
        // RefreshAsync compares the disk with.
        private readonly Dictionary<string, string[]> uses = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Stamp> stamps = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);

        private readonly struct Stamp : IEquatable<Stamp>
        {
            public readonly long Size, Ticks, MetaSize, MetaTicks;
            public readonly string Import;

            public Stamp(string path)
            {
                var file = new FileInfo(path);
                var meta = new FileInfo(path + ".meta");
                Size = file.Exists ? file.Length : -1;
                Ticks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
                MetaSize = meta.Exists ? meta.Length : -1;
                MetaTicks = meta.Exists ? meta.LastWriteTimeUtc.Ticks : 0;
                Import = AssetDatabase.GetAssetDependencyHash(path).ToString();
            }

            public bool SameFiles(Stamp other) => Size == other.Size && Ticks == other.Ticks && MetaSize == other.MetaSize && MetaTicks == other.MetaTicks;
            public bool Equals(Stamp other) => SameFiles(other) && Import == other.Import;
        }

        // Reads what a file uses (stamped first: a change while it is read shows as a change later) into the map.
        private void Read(string path)
        {
            var stamp = new Stamp(path);
            var used = new List<string>();
            foreach (string guid in Store.Uses(path))
            {
                string dependency = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(dependency) || string.Equals(dependency, path, StringComparison.OrdinalIgnoreCase)) continue;
                used.Add(dependency);
            }
            Forget(path);
            stamps[path] = stamp;
            uses[path] = used.ToArray();
            foreach (string dependency in used)
            {
                if (!usedBy.TryGetValue(dependency, out var users)) usedBy[dependency] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                users.Add(path);
            }
        }

        private void Forget(string path)
        {
            if (uses.TryGetValue(path, out var old))
                foreach (string dependency in old)
                    if (usedBy.TryGetValue(dependency, out var users)) users.Remove(path);
            uses.Remove(path);
            stamps.Remove(path);
        }

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
                // Anything that may change what a file uses: an import, a deletion or a move, an asset edited in the editor, an
                // Undo or Redo, an unknown change.
                EditorChangeBus.Changed += changes =>
                {
                    if (changes.Project || changes.Everything || changes.UndoRedo || changes.Assets.Count > 0 ||
                        changes.Imported.Count + changes.Deleted.Count + changes.Moved.Count + changes.MovedFrom.Count > 0) filesRevision++;
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
                    map.Read(assets[i]);
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

        /// <summary>
        /// Checks the map against the disk itself before anything is deleted on its word: every file under Assets/ that may
        /// use another is looked at again (its size, time, .meta and import), and those new or changed since they were read
        /// are read again, also when Unity has not imported the change yet (their text and their .meta name what they use).
        /// False when that cannot be done for certain: a file in Unity's binary format changed on disk and not imported
        /// again yet (what it names cannot be read), a file that cannot be read, a change reported meanwhile, cancellation.
        /// Main thread; yields between slices.
        /// </summary>
        public async Task<bool> RefreshAsync(CancellationToken cancellation = default)
        {
            try
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(UnityEngine.Application.dataPath));
                string assets = Path.Combine(root, "Assets");
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var started = DateTime.UtcNow;
                Store.Load();
                foreach (string full in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (full.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = full.Substring(root.Length + 1).Replace('\\', '/');
                    if (!MayUseOthers(path)) continue;
                    present.Add(path);
                    var now = new Stamp(path);
                    if (stamps.TryGetValue(path, out var known) && known.Equals(now)) continue;
                    // Changed on disk and not imported again: Unity's own answer is the old one. Text names what it uses; a
                    // file in Unity's binary format cannot be read.
                    if (!Store.IsText(full) && Store.IsNative(path) && (!stamps.TryGetValue(path, out known) || known.Import == now.Import)) return false;
                    Read(path);
                    if ((DateTime.UtcNow - started).TotalMilliseconds > 12)
                    {
                        await Task.Yield();
                        started = DateTime.UtcNow;
                    }
                }
                // Files gone use nothing.
                foreach (string gone in uses.Keys.Where(p => !present.Contains(p)).ToList()) Forget(gone);
                Store.Save();
                return Current;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                UnityEngine.Debug.LogWarning("[Orbiters] Could not check what the project's files use: " + ex.Message);
                return false;
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
