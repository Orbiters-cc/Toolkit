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
    /// so the editor stays responsive. Tools use it before removing files, through <see cref="Removable"/> in the step that
    /// deletes: a material another avatar's prefab still uses is kept.
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
        /// Builds the map. <paramref name="progress"/> receives 0..1. Call on the main thread; the files are listed and read
        /// on a worker and Unity is asked a slice per editor frame; at the end the project is listed again (on the worker)
        /// and what changed meanwhile is read again, until a listing finds nothing new. Throws
        /// <see cref="UncertainReferencesException"/> when what uses what cannot be known (a binary file whose bytes are not
        /// known to be what Unity imported, a file that cannot be read or decoded): a caller deciding a deletion then keeps
        /// everything.
        /// A deletion must be decided by <see cref="Removable"/>, in the same synchronous step as the deletion itself: it
        /// checks the project once more and answers which candidates may go. Deleting on <see cref="UsersOf"/> after the
        /// scan returned, without that call, is a bug: files may have changed since.
        /// </summary>
        public static async Task<ProjectReferences> ScanAsync(Action<float, string> progress = null, CancellationToken cancellation = default)
        {
            var total = Stopwatch.StartNew();
            var map = new ProjectReferences { roots = Roots() };
            ImportStamps.Load();
            string keptPath = KeptScan.FilePath;
            var reader = new Reader(map.roots, cancellation);
            map.Stats = reader.Stats;
            LastScan = map.Stats;
            try
            {
                // Each file is read again only when its stamp changed since the kept scan (KeptScan).
                var reading = Task.Run(() =>
                {
                    reader.Kept = KeptScan.Load(keptPath, reader.Stats);
                    return reader.Run();
                }, cancellation);
                if (progress != null)
                {
                    var shown = Stopwatch.StartNew();
                    void Report()
                    {
                        if (reading.IsCompleted) { EditorApplication.update -= Report; return; }
                        if (shown.ElapsedMilliseconds < 100) return;
                        shown.Restart();
                        progress(0.5f * reader.Fraction, $"Reading the project's files… {reader.Read} of {reader.Found}");
                    }
                    EditorApplication.update += Report;
                }
                var found = await reading;
                await map.AddSlicedAsync(found, 0.5f, 0.45f, progress, cancellation);
                // Files changed while the scan ran are read again, until a listing finds nothing new.
                var checking = Stopwatch.StartNew();
                for (int round = 0; ; round++)
                {
                    var known = map.files.ToDictionary(p => p.Key, p => p.Value.Stamp, StringComparer.OrdinalIgnoreCase);
                    var roots = map.roots;
                    var diff = await Task.Run(() => Diff(roots, known, null, cancellation, true), cancellation);
                    if (diff.Uncertain != null) throw new UncertainReferencesException(diff.Uncertain);
                    if (diff.Changed.Count == 0 && diff.Gone.Count == 0) break;
                    if (round >= 4) throw new UncertainReferencesException("Files kept changing while the project was checked.");
                    foreach (string gone in diff.Gone) map.Remove(gone);
                    foreach (var read in diff.Changed) map.Remove(read.Path);
                    map.Stats.Rereads += diff.Changed.Count;
                    await map.AddSlicedAsync(diff.Changed, 0.95f, 0f, progress, cancellation);
                }
                map.Stats.RecheckMilliseconds = checking.Elapsed.TotalMilliseconds;
                if (!SameRoots(Roots(), map.roots)) throw new UncertainReferencesException("The project's packages changed while it was checked.");
                progress?.Invoke(1f, "Checked " + map.files.Count + " files");
                return map;
            }
            finally
            {
                map.Stats.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                // What was read whole is kept, also from a scan that stopped: the next one reads only what changed.
                if (map.files.Count > 0)
                {
                    var reads = map.files.Values.ToList();
                    KeptScan.Saving = Task.Run(() => KeptScan.Save(keptPath, reads));
                }
            }
        }

        internal ScanStats Stats = new ScanStats();

        // Unity's part of reading each file, slice by slice: 12 ms while someone uses the editor, 100 ms when nobody does
        // (in the background, in batch mode), each slice on the next editor update rather than on a posted continuation
        // (which the editor runs far less often than its updates when it is not in front). A slice ends after the file
        // that passes its budget: one file Unity takes long over (a large scene's dependencies) can make it longer.
        private async Task AddSlicedAsync(List<FileRead> found, float from, float span, Action<float, string> progress, CancellationToken cancellation)
        {
            var slice = Stopwatch.StartNew();
            var work = Stopwatch.StartNew();
            double budget = SliceMilliseconds();
            for (int i = 0; i < found.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                Add(found[i]);
                if (slice.Elapsed.TotalMilliseconds <= budget) continue;
                Stats.UnityMilliseconds += work.Elapsed.TotalMilliseconds;
                progress?.Invoke(from + span * (i + 1f) / found.Count, $"Checking what uses what… {i + 1} of {found.Count}");
                Stats.Continuations++;
                await NextUpdate(cancellation);
                work.Restart();
                slice.Restart();
                budget = SliceMilliseconds();
            }
            Stats.UnityMilliseconds += work.Elapsed.TotalMilliseconds;
        }

        private static double SliceMilliseconds() =>
            !UnityEngine.Application.isBatchMode && UnityEditorInternal.InternalEditorUtility.isApplicationActive ? 12 : 100;

        // Completes on the next EditorApplication.update, running the awaiting code inside it (the update runs on the main
        // thread with Unity's context current, so the continuation is not posted again). Cancelled with the token: the
        // handler is removed at once when the cancel comes on the main thread, else at the next update (which then does
        // nothing more).
        private static Task NextUpdate(CancellationToken cancellation)
        {
            var next = new TaskCompletionSource<bool>();
            CancellationTokenRegistration registration = default;
            void Tick()
            {
                EditorApplication.update -= Tick;
                registration.Dispose();
                next.TrySetResult(true);
            }
            EditorApplication.update += Tick;
            if (cancellation.CanBeCanceled)
            {
                int main = Thread.CurrentThread.ManagedThreadId;
                registration = cancellation.Register(() =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == main) EditorApplication.update -= Tick;
                    next.TrySetCanceled();
                });
            }
            return next.Task;
        }

        /// <summary>
        /// Checks the project once more and answers which of <paramref name="candidates"/> may go, in one synchronous call
        /// to make right before deleting them, in the same step: those not used by any file outside the candidates (files
        /// only used by other candidates going go together) and not kept by <paramref name="keep"/> (used by an object
        /// loaded in the editor, say). None when the project cannot be checked again now: it changed too much since the
        /// scan, the check would take too long, a file cannot be read, the packages changed.
        /// </summary>
        public List<string> Removable(IEnumerable<string> candidates, Func<string, bool> keep = null) => Removable(candidates, keep, out _);

        /// <summary><see cref="Removable(IEnumerable{string}, Func{string, bool})"/>, saying why it answered none when it could not check (else null).</summary>
        public List<string> Removable(IEnumerable<string> candidates, Func<string, bool> keep, out string uncertain)
        {
            uncertain = null;
            var removable = new HashSet<string>(candidates ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (removable.Count == 0) return new List<string>();
            uncertain = Recheck(out _);
            if (uncertain != null) return new List<string>();
            if (keep != null) removable.RemoveWhere(p => keep(p));
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (string path in removable.ToList())
                    if (UsersOf(path, removable).Count > 0) { removable.Remove(path); changed = true; }
            }
            return removable.ToList();
        }

        /// <summary>
        /// Brings the map up to date at once, on the main thread: the project is listed again and the files new or changed
        /// since the map read them (by their stamp) are read again, those gone forgotten. Bounded: it lists for at most
        /// <see cref="RecheckMilliseconds"/> and reads at most <see cref="RecheckFiles"/> files and <see cref="RecheckBytes"/>
        /// bytes; past that it answers false instead of holding the editor. False also when a file cannot be read, a binary
        /// file is not as Unity imported it, or the packages changed: keep everything then. <see cref="Removable"/> calls it.
        /// </summary>
        public bool Recheck() => Recheck(out _) == null;

        // Listing a project of 18,000 files took 470 ms on a hard disk (measured): the check runs once, right before a
        // deletion, so it may take the time a large project's listing takes rather than refuse there every time.
        internal const double RecheckMilliseconds = 1500;
        internal const int RecheckFiles = 64;
        internal const long RecheckBytes = 8L * 1024 * 1024;

        internal string Recheck(out int reread)
        {
            reread = 0;
            var now = Roots();
            if (!SameRoots(now, roots)) return "The project's packages changed while it was checked.";
            var known = files.ToDictionary(p => p.Key, p => p.Value.Stamp, StringComparer.OrdinalIgnoreCase);
            var diff = Diff(now, known, Stopwatch.StartNew(), CancellationToken.None, false);
            if (diff.Uncertain != null) return diff.Uncertain;
            foreach (string gone in diff.Gone) Remove(gone);
            try
            {
                foreach (var read in diff.Changed)
                {
                    Remove(read.Path);
                    Add(read);
                    reread++;
                }
            }
            catch (UncertainReferencesException ex) { return ex.Message; }
            return null;
        }

        private sealed class Changes
        {
            public readonly List<FileRead> Changed = new List<FileRead>();
            public readonly List<string> Gone = new List<string>();
            public string Uncertain;
        }

        // Lists the roots and reads the files whose stamp differs from the one they were read with (new ones too); no
        // Unity call, so it may run on a worker. With a clock, bounded by the recheck limits.
        private static Changes Diff(List<Root> roots, Dictionary<string, Stamp> known, Stopwatch clock, CancellationToken cancellation, bool unbounded)
        {
            var changes = new Changes();
            var listing = Listing.Of(roots, cancellation, unbounded ? double.PositiveInfinity : RecheckMilliseconds, clock);
            if (listing.Uncertain != null) { changes.Uncertain = listing.Uncertain; return changes; }
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = new List<Listing.Entry>();
            long bytes = 0;
            foreach (var entry in listing.Entries)
            {
                present.Add(entry.Path);
                if (known.TryGetValue(entry.Path, out var stamp) && stamp.Equals(entry.Stamp)) continue;
                changed.Add(entry);
                bytes += Math.Max(0, entry.Stamp.Size) + Math.Max(0, entry.Stamp.MetaSize);
            }
            // At once, a few files a save or an import just wrote, not a project copied in.
            if (!unbounded && (changed.Count > RecheckFiles || bytes > RecheckBytes))
            {
                changes.Uncertain = $"{changed.Count} files changed since the project was checked.";
                return changes;
            }
            changes.Gone.AddRange(known.Keys.Where(p => !present.Contains(p)));
            foreach (var entry in changed)
            {
                if (!unbounded && clock != null && clock.Elapsed.TotalMilliseconds > RecheckMilliseconds * 2)
                {
                    changes.Uncertain = "Checking the project again took too long.";
                    return changes;
                }
                var read = Reader.ReadOne(entry, cancellation);
                if (read != null) changes.Changed.Add(read);
                else changes.Gone.Add(entry.Path);
            }
            return changes;
        }

        private void Remove(string path)
        {
            if (!files.TryGetValue(path, out var read)) return;
            files.Remove(path);
            foreach (string guid in read.Uses ?? read.Guids)
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
