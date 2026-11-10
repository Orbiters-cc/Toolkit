using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Which project files use which. Every file a user of the project can change is read: everything under Assets/, the
    /// embedded and local packages, and ProjectSettings/, each file with its .meta (a script's default references, a
    /// model's remapped materials). A file counts as using every asset whose GUID its text or its .meta names, and every
    /// asset Unity's import says it depends on. A file whose own content cannot be read (Unity's binary format) is trusted
    /// only while it is exactly as Unity last imported it; otherwise the scan cannot say what uses what and fails with
    /// <see cref="UncertainReferencesException"/>. Files are read on a worker and Unity is asked a slice per editor frame,
    /// so the editor stays responsive; the scan ends by checking, in the same step as its return, that no file changed
    /// meanwhile (those that did are read again). Tools use it before removing files: a material another avatar's prefab
    /// still uses is kept.
    /// </summary>
    public sealed partial class ProjectReferences
    {
        // GUID (lower case) → the files naming it, by asset-style path ("Assets/…", "Packages/<name>/…", "ProjectSettings/…").
        private readonly Dictionary<string, HashSet<string>> usedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Each file as it was read: its stamp then and the GUIDs it names.
        private readonly Dictionary<string, FileRead> files = new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
        private List<Root> roots;

        /// <summary>The files read.</summary>
        public int Scanned => files.Count;

        /// <summary>The files directly using <paramref name="path"/>, leaving out <paramref name="ignore"/> and the file itself.</summary>
        public IReadOnlyCollection<string> UsersOf(string path, ICollection<string> ignore = null)
        {
            string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid) || !usedBy.TryGetValue(guid, out var users)) return Array.Empty<string>();
            return users.Where(u => !string.Equals(u, path, StringComparison.OrdinalIgnoreCase) && (ignore == null || !ignore.Contains(u))).ToList();
        }

        /// <summary>
        /// Builds the map. <paramref name="progress"/> receives 0..1. Call on the main thread; the files are read on a worker
        /// and Unity is asked a slice per editor frame. Throws <see cref="UncertainReferencesException"/> when what uses what
        /// cannot be known (a file in Unity's binary format changed since its import, a file that cannot be read): a caller
        /// deciding a deletion then keeps everything.
        /// </summary>
        public static async Task<ProjectReferences> ScanAsync(Action<float, string> progress = null, CancellationToken cancellation = default)
        {
            var map = new ProjectReferences { roots = Roots() };
            ImportStamps.Load();
            var reader = new Reader(map.roots, cancellation);
            var reading = Task.Run(reader.Run, cancellation);
            while (!reading.IsCompleted)
            {
                progress?.Invoke(0.5f * reader.Fraction, $"Reading the project's files… {reader.Read} of {reader.Found}");
                await Task.Yield();
            }
            var found = await reading;
            var slice = Stopwatch.StartNew();
            for (int i = 0; i < found.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                map.Add(found[i]);
                if (slice.Elapsed.TotalMilliseconds <= 12) continue;
                progress?.Invoke(0.5f + 0.5f * (i + 1f) / found.Count, $"Checking what uses what… {i + 1} of {found.Count}");
                await Task.Yield();
                slice.Restart();
            }
            // The last word, in one step with the return: files changed while the scan ran are read again.
            string reason = map.Recheck(out _);
            if (reason != null) throw new UncertainReferencesException(reason);
            progress?.Invoke(1f, "Checked " + map.files.Count + " files");
            return map;
        }

        /// <summary>
        /// Brings the map up to date at once, on the main thread: the files are listed again and those new or changed since
        /// they were read are read again, those gone forgotten. Call it in the same synchronous step as a deletion the map
        /// decides. False when the map cannot be trusted any more (too many changes to read now, a file that cannot be
        /// read, a binary file changed since its import, the set of packages changed): keep everything then.
        /// </summary>
        public bool Recheck() => Recheck(out _) == null;

        internal string Recheck(out int reread)
        {
            reread = 0;
            var now = Roots();
            if (!SameRoots(now, roots)) return "The project's packages changed while it was checked.";
            var listing = Listing.Of(now, CancellationToken.None);
            if (listing.Uncertain != null) return listing.Uncertain;
            var changed = new List<Listing.Entry>();
            long bytes = 0;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in listing.Entries)
            {
                present.Add(entry.Path);
                if (files.TryGetValue(entry.Path, out var known) && known.Stamp.Equals(entry.Stamp)) continue;
                changed.Add(entry);
                bytes += Math.Max(0, entry.Stamp.Size) + Math.Max(0, entry.Stamp.MetaSize);
            }
            // Read again now, in one step: a few files a save or an import just wrote, not a project copied in.
            if (changed.Count > RecheckFiles || bytes > RecheckBytes) return $"{changed.Count} files changed while the project was checked.";
            foreach (string gone in files.Keys.Where(p => !present.Contains(p)).ToList()) Remove(gone);
            try
            {
                foreach (var entry in changed)
                {
                    Remove(entry.Path);
                    var read = Reader.ReadOne(entry, CancellationToken.None);
                    if (read != null) Add(read);
                    reread++;
                }
            }
            catch (UncertainReferencesException ex) { return ex.Message; }
            return null;
        }

        private const int RecheckFiles = 256;
        private const long RecheckBytes = 64L * 1024 * 1024;

        private void Remove(string path)
        {
            if (!files.TryGetValue(path, out var read)) return;
            files.Remove(path);
            foreach (string guid in read.Guids)
                if (usedBy.TryGetValue(guid, out var users)) users.Remove(path);
        }

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

    /// <summary>What uses what in the project cannot be known right now; whoever was about to delete keeps everything.</summary>
    public sealed class UncertainReferencesException : Exception
    {
        public UncertainReferencesException(string message) : base(message) { }
    }
}
