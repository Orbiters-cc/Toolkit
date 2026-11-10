using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    public sealed partial class ProjectReferences
    {
        /// <summary>How the last scan spent its time, for measuring it in a real project (<see cref="Measure"/>).</summary>
        public sealed class ScanStats
        {
            public int Listed, Read, FromKept, UnityAsked, Continuations, Rereads;
            public long BytesRead;
            public double ListingMilliseconds, ReadingMilliseconds, UnityMilliseconds, RecheckMilliseconds, TotalMilliseconds;
            public bool KeptLoaded;
            public long KeptBytes;
            internal readonly HashSet<string> ReadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public override string ToString() =>
                $"Listed {Listed} files in {ListingMilliseconds:0} ms; read {Read} ({BytesRead / (1024.0 * 1024.0):0.0} MB) in {ReadingMilliseconds:0} ms " +
                $"({(ReadingMilliseconds > 0 ? BytesRead / (1024.0 * 1024.0) / (ReadingMilliseconds / 1000.0) : 0):0} MB/s), {FromKept} from the kept scan " +
                $"({(KeptLoaded ? KeptBytes / 1024 + " KB" : "none")}); Unity asked for {UnityAsked} files, {UnityMilliseconds:0} ms of Unity work over " +
                $"{Continuations} slices; final checks {RecheckMilliseconds:0} ms ({Rereads} files read again); {TotalMilliseconds:0} ms in all.";
        }

        /// <summary>The statistics of the last scan that finished or failed, or null.</summary>
        public static ScanStats LastScan { get; private set; }

        /// <summary>
        /// Runs a scan and logs where its time went (<see cref="LastScan"/> too). <paramref name="keptResults"/> false
        /// reads every file as a session's very first scan would.
        /// </summary>
        public static async void Measure(bool keptResults = true)
        {
            try
            {
                if (!keptResults) KeptScan.Forget();
                await ScanAsync();
                Debug.Log("[Orbiters] Project reference scan: " + LastScan);
            }
            catch (Exception ex) { Debug.Log("[Orbiters] Project reference scan stopped (" + ex.Message + "): " + LastScan); }
        }

        /// <summary>
        /// Each file's reading kept across script reloads and sessions in Library/Orbiters/ProjectReferencesScan.bin: what
        /// its text and .meta name, keyed by its path and stamp (the size and write time of the file and of its .meta, as
        /// Unity's own change detection uses), and Unity's answers for it under the dependency hash they were asked with.
        /// A file whose stamp differs is read again; Unity is asked again when the hash differs; the binary-file test and
        /// the rechecks before a deletion run afresh every time, so a kept reading never decides more than a fresh one
        /// would. Written whole through a temporary file, versioned (a reader change makes it ignored), ignored whole when
        /// it does not read, and not written past <see cref="MaxBytes"/>.
        /// </summary>
        internal static class KeptScan
        {
            // Bump when the reader or what is kept changes: an older file is then ignored.
            private const int Version = 1;
            private const uint Magic = 0x5250524F; // "ORPR"
            internal static long MaxBytes = 128L * 1024 * 1024;
            private static string overridePath;
            // The last readings kept, as written (read and replaced whole by workers and the main thread).
            private static volatile Dictionary<string, FileRead> memory;

            internal static string FilePath => overridePath ?? Path.Combine(Storage.ProjectPaths.OrbitersLibrary, "ProjectReferencesScan.bin");

            /// <summary>The last scan's write of its readings (tests wait for it).</summary>
            internal static System.Threading.Tasks.Task<bool> Saving = System.Threading.Tasks.Task.FromResult(false);

            /// <summary>Tests: read and write <paramref name="path"/> instead (seeded with the project's file) until disposed.</summary>
            internal static IDisposable Use(string path)
            {
                string project = FilePath;
                try { if (!File.Exists(path) && File.Exists(project)) File.Copy(project, path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                overridePath = Path.GetFullPath(path);
                memory = null;
                return new Scope();
            }

            private sealed class Scope : IDisposable
            {
                public void Dispose() { overridePath = null; memory = null; }
            }

            /// <summary>Tests: as after a script reload, nothing in memory: the next scan starts from the file.</summary>
            internal static void ForgetMemory() => memory = null;

            /// <summary>Nothing kept: the next scan reads every file.</summary>
            internal static void Forget()
            {
                memory = null;
                try { File.Delete(FilePath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            /// <summary>The kept readings (empty when none or unreadable). Any thread; <paramref name="path"/> resolved on the main thread.</summary>
            public static Dictionary<string, FileRead> Load(string path, ScanStats stats)
            {
                var known = memory;
                if (known != null) { stats.KeptLoaded = true; return known; }
                var result = new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length > MaxBytes) return result;
                    using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Encoding.UTF8))
                    {
                        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return result;
                        int guidCount = reader.ReadInt32();
                        if (guidCount < 0 || guidCount > 10_000_000) return result;
                        var guids = new string[guidCount];
                        for (int i = 0; i < guidCount; i++) guids[i] = reader.ReadString();
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 10_000_000) return result;
                        string[] Indexes()
                        {
                            int n = reader.ReadInt32();
                            if (n < 0 || n > guidCount) throw new InvalidDataException("bad count");
                            var values = new string[n];
                            for (int i = 0; i < n; i++)
                            {
                                int index = reader.ReadInt32();
                                if (index < 0 || index >= guidCount) throw new InvalidDataException("bad index");
                                values[i] = guids[index];
                            }
                            return values;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            var read = new FileRead { Path = reader.ReadString() };
                            read.Stamp = new Stamp(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                            byte flags = reader.ReadByte();
                            read.Imported = (flags & 1) != 0;
                            read.ContentRead = (flags & 2) != 0;
                            read.UnityKnown = (flags & 4) != 0;
                            read.Importer = Null(reader.ReadString());
                            read.OwnGuid = Null(reader.ReadString());
                            read.DependencyHash = Null(reader.ReadString());
                            read.Guids.UnionWith(Indexes());
                            read.Dependencies = Indexes();
                            result[read.Path] = read;
                        }
                        if (reader.ReadUInt32() != Magic) return new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
                    }
                    stats.KeptLoaded = true;
                    stats.KeptBytes = info.Length;
                    return result;
                }
                // A cut, corrupt or unreadable file: read every file again, and write it anew.
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException ||
                                           ex is FormatException || ex is OverflowException)
                {
                    return new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
                }
            }

            private static string Null(string value) => value.Length == 0 ? null : value;

            /// <summary>Keeps <paramref name="reads"/> (those read whole) for the next scan. Any thread; false when not written.</summary>
            public static bool Save(string path, List<FileRead> reads)
            {
                var keep = reads.Where(r => r != null && r.Failure == null && r.Path != null).ToList();
                var byPath = new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
                foreach (var read in keep) byPath[read.Path] = read;
                memory = byPath;
                var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
                var guids = new List<string>();
                int Index(string guid)
                {
                    if (!indexes.TryGetValue(guid, out int index)) { indexes[guid] = index = guids.Count; guids.Add(guid); }
                    return index;
                }
                foreach (var read in keep)
                {
                    foreach (string guid in read.Guids) Index(guid);
                    foreach (string guid in read.Dependencies ?? Array.Empty<string>()) Index(guid);
                }
                string temporary = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), Encoding.UTF8))
                    {
                        writer.Write(Magic);
                        writer.Write(Version);
                        writer.Write(guids.Count);
                        foreach (string guid in guids) writer.Write(guid);
                        writer.Write(keep.Count);
                        foreach (var read in keep)
                        {
                            writer.Write(read.Path);
                            writer.Write(read.Stamp.Size); writer.Write(read.Stamp.Ticks); writer.Write(read.Stamp.MetaSize); writer.Write(read.Stamp.MetaTicks);
                            writer.Write((byte)((read.Imported ? 1 : 0) | (read.ContentRead ? 2 : 0) | (read.UnityKnown ? 4 : 0)));
                            writer.Write(read.Importer ?? "");
                            writer.Write(read.OwnGuid ?? "");
                            writer.Write(read.DependencyHash ?? "");
                            writer.Write(read.Guids.Count);
                            foreach (string guid in read.Guids) writer.Write(indexes[guid]);
                            var dependencies = read.Dependencies ?? Array.Empty<string>();
                            writer.Write(dependencies.Length);
                            foreach (string guid in dependencies) writer.Write(indexes[guid]);
                        }
                        writer.Write(Magic);
                        // Past the bound: not kept on disk (the next session reads every file again), and no old copy left.
                        if (writer.BaseStream.Length > MaxBytes) throw new InvalidDataException("past the size bound");
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    if (ex is InvalidDataException)
                        try { File.Delete(path); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                    return false;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
        }
    }
}
