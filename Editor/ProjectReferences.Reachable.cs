using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    public sealed partial class ProjectReferences
    {
        /// <summary>
        /// Every asset under Assets/ that <paramref name="roots"/> use, directly or through other assets under Assets/ (a
        /// prefab's controller and the animations it plays), the roots included: what Unity's recursive dependency search
        /// finds, a slice per editor tick. Each asset's references are kept in Library/Orbiters while it, its .meta and
        /// Unity's import of it stay the same, and saved as the search goes (an interrupted search is not lost), so a later
        /// search reads only what changed. A reference to a file that is missing counts again as soon as the file is back.
        /// <paramref name="skip"/> names assets whose own references do not matter (generated meshes the caller may
        /// delete); meshes and Avatars are never read. Files outside Assets/ (installed packages) are not followed.
        /// </summary>
        public static async Task<HashSet<string>> ReachableAsync(IEnumerable<string> roots, Func<string, bool> skip = null,
            Action<float, string> progress = null, CancellationToken cancellation = default)
        {
            var walk = new Walk(roots, skip);
            var slice = DateTime.UtcNow;
            var saved = DateTime.UtcNow;
            try
            {
                while (walk.Step())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((DateTime.UtcNow - slice).TotalMilliseconds <= 12) continue;
                    if ((DateTime.UtcNow - saved).TotalSeconds > 10)
                    {
                        Store.Save();
                        saved = DateTime.UtcNow;
                    }
                    progress?.Invoke(walk.Progress, $"Checking what the project uses… {walk.Read} of {walk.Found} files");
                    await Task.Yield();
                    slice = DateTime.UtcNow;
                }
            }
            finally
            {
                Store.Save();
            }
            progress?.Invoke(1f, "Checked " + walk.Found + " files");
            return walk.Reached;
        }

        /// <summary>The search of <see cref="ReachableAsync"/> at once, on the main thread.</summary>
        public static HashSet<string> Reachable(IEnumerable<string> roots, Func<string, bool> skip = null)
        {
            var walk = new Walk(roots, skip);
            try { while (walk.Step()) { } }
            finally { Store.Save(); }
            return walk.Reached;
        }

        /// <summary>
        /// Tests: searches read and write <paramref name="path"/> (seeded with the project's file) instead of
        /// Library/Orbiters/ProjectReferences.json until disposed.
        /// </summary>
        public static IDisposable UseCacheFile(string path) => Store.Use(path);

        private sealed class Walk
        {
            private readonly Stack<string> pending = new Stack<string>();
            private readonly Func<string, bool> skip;
            public readonly HashSet<string> Reached = new HashSet<string>(StringComparer.Ordinal);
            public int Read { get; private set; }
            public int Found => Reached.Count;
            public float Progress => Found == 0 ? 1f : Mathf.Clamp01((float)Read / Found);

            public Walk(IEnumerable<string> roots, Func<string, bool> skip)
            {
                this.skip = skip;
                Store.Load();
                foreach (string root in roots ?? Enumerable.Empty<string>())
                    if (Store.IsProjectFile(root) && Reached.Add(root)) pending.Push(root);
            }

            public bool Step()
            {
                if (pending.Count == 0) return false;
                string path = pending.Pop();
                Read++;
                if (skip != null && skip(path)) return pending.Count > 0;
                foreach (string guid in Store.Uses(path))
                {
                    // Unity still names a deleted asset's path: only files there now count (the cached GUID makes it
                    // count again once it is back).
                    string used = AssetDatabase.GUIDToAssetPath(guid);
                    if (Store.IsProjectFile(used) && File.Exists(used) && Reached.Add(used)) pending.Push(used);
                }
                return pending.Count > 0;
            }
        }

        // Each project file's references, as GUIDs: a moved file leaves the entries of the files using it valid, and one
        // missing when its user was read is found again once it is back.
        private static class Store
        {
            private const int Schema = 1;
            private static readonly Regex GuidReference = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);
            private static readonly Type[] SelfContained = { typeof(Mesh), typeof(Avatar) };
            private static string overridePath;
            private static Dictionary<string, Entry> entries;
            private static bool dirty;

            private static string FilePath => overridePath ?? Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "Orbiters", "ProjectReferences.json");

            private sealed class Entry
            {
                public long size, ticks, metaSize, metaTicks;
                public string hash;
                public string[] uses;
            }

            [Serializable]
            private sealed class Stored
            {
                public int schema;
                public List<string> guids = new List<string>();
                public List<StoredEntry> files = new List<StoredEntry>();
            }

            [Serializable]
            private sealed class StoredEntry
            {
                public string path, hash;
                public long size, ticks, metaSize, metaTicks;
                public int[] uses;
            }

            public static bool IsProjectFile(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal);

            public static string[] Uses(string path)
            {
                var file = new FileInfo(path);
                if (!file.Exists) return Array.Empty<string>(); // a folder, or a file deleted since the last refresh
                var meta = new FileInfo(path + ".meta");
                long metaSize = meta.Exists ? meta.Length : -1, metaTicks = meta.Exists ? meta.LastWriteTimeUtc.Ticks : 0;
                // Unity answers from its last import: a file changed on disk but not imported yet is read again once it is.
                string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                if (entries.TryGetValue(path, out var known) && known.size == file.Length && known.ticks == file.LastWriteTimeUtc.Ticks &&
                    known.metaSize == metaSize && known.metaTicks == metaTicks && known.hash == hash)
                    return known.uses;

                bool cacheable = true;
                var uses = new HashSet<string>(StringComparer.Ordinal);
                if (!SelfContained.Contains(AssetDatabase.GetMainAssetTypeAtPath(path)))
                {
                    foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                    {
                        string guid = dependency != path && IsProjectFile(dependency) ? AssetDatabase.AssetPathToGUID(dependency) : null;
                        if (!string.IsNullOrEmpty(guid)) uses.Add(guid);
                    }
                    // Unity lists only the references it can resolve; the text also names files missing right now.
                    if (IsText(file.FullName)) AddReferences(file.FullName, uses);
                    else if (IsNative(path)) cacheable = false; // binary serialization: its missing references cannot be seen
                    if (meta.Exists) AddReferences(meta.FullName, uses);
                }
                var result = uses.ToArray();
                if (cacheable)
                {
                    entries[path] = new Entry { size = file.Length, ticks = file.LastWriteTimeUtc.Ticks, metaSize = metaSize, metaTicks = metaTicks, hash = hash, uses = result };
                    dirty = true;
                }
                return result;
            }

            private static bool IsText(string fullPath)
            {
                var start = new byte[5];
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return stream.Read(start, 0, 5) == 5 && Encoding.ASCII.GetString(start) == "%YAML";
            }

            private static bool IsNative(string path) =>
                path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                AssetImporter.GetAtPath(path)?.GetType().Name == "NativeFormatImporter";

            // GUIDs the file names that resolve under Assets/ or not at all (a missing file); installed packages are left out.
            private static void AddReferences(string fullPath, HashSet<string> uses)
            {
                foreach (Match match in GuidReference.Matches(File.ReadAllText(fullPath)))
                {
                    string guid = match.Groups[1].Value;
                    string used = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(used) || IsProjectFile(used)) uses.Add(guid);
                }
            }

            public static void Load()
            {
                if (entries != null) return;
                entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                try
                {
                    if (!File.Exists(FilePath)) return;
                    var stored = JsonUtility.FromJson<Stored>(File.ReadAllText(FilePath));
                    if (stored == null || stored.schema != Schema || stored.guids == null || stored.files == null) return;
                    foreach (var value in stored.files)
                    {
                        if (string.IsNullOrEmpty(value?.path) || value.uses == null || value.uses.Any(index => index < 0 || index >= stored.guids.Count)) continue;
                        entries[value.path] = new Entry
                        {
                            size = value.size, ticks = value.ticks, metaSize = value.metaSize, metaTicks = value.metaTicks, hash = value.hash,
                            uses = value.uses.Select(index => stored.guids[index]).ToArray()
                        };
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    // An unreadable file only costs reading the project's files again.
                    entries.Clear();
                    Debug.LogWarning("[Orbiters] Ignoring an unreadable project references cache: " + ex.Message);
                }
            }

            public static void Save()
            {
                if (!dirty || entries == null) return;
                // Files deleted since are forgotten.
                foreach (string path in entries.Keys.Where(path => !File.Exists(path)).ToList()) entries.Remove(path);
                var guids = new List<string>();
                var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
                int Index(string guid)
                {
                    if (!indexes.TryGetValue(guid, out int index))
                    {
                        indexes[guid] = index = guids.Count;
                        guids.Add(guid);
                    }
                    return index;
                }
                var stored = new Stored
                {
                    schema = Schema,
                    guids = guids,
                    files = entries.Select(pair => new StoredEntry
                    {
                        path = pair.Key, size = pair.Value.size, ticks = pair.Value.ticks, metaSize = pair.Value.metaSize, metaTicks = pair.Value.metaTicks,
                        hash = pair.Value.hash, uses = pair.Value.uses.Select(Index).ToArray()
                    }).ToList()
                };
                string temporary = FilePath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllText(temporary, JsonUtility.ToJson(stored));
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                    else File.Move(temporary, FilePath);
                    dirty = false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // The search's answer stands; this session keeps what it read in memory.
                    Debug.LogWarning("[Orbiters] Could not save the project references cache: " + ex.Message);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }

            public static IDisposable Use(string path)
            {
                Save();
                if (File.Exists(FilePath) && !File.Exists(path)) File.Copy(FilePath, path);
                overridePath = Path.GetFullPath(path);
                entries = null;
                dirty = false;
                return new Scope();
            }

            private sealed class Scope : IDisposable
            {
                public void Dispose()
                {
                    Save();
                    overridePath = null;
                    entries = null;
                    dirty = false;
                }
            }
        }
    }
}
