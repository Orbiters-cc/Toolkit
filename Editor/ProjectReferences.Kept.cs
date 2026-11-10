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
        /// would. Written whole through a temporary file, versioned (a reader change makes it ignored), checked whole by a
        /// checksum and ignored on any doubt, and not written past <see cref="MaxBytes"/>.
        /// Its one assumption: a file whose size and write time, and its .meta's, are what they were has the bytes it had.
        /// Unity's own change detection makes the same one; before this cache only one synchronous recheck relied on it, now
        /// every later scan and session does. Where Unity has an answer for the file (an imported asset), a dependency hash
        /// different from the kept one makes the file be read again even with equal stamps.
        /// </summary>
        internal static class KeptScan
        {
            // Bump on any change to what the reader recognises as a reference (the GUID tokens, which files' content is
            // read, the encodings), to the stamp, to Unity's part that is kept, or to this file's layout: a file of
            // another version is then ignored and every file read again.
            internal const int Version = 2;
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

            // The file: magic, version, payload length (8 bytes), payload, SHA-256 of the payload (32 bytes), nothing more.
            // The checksum guards against disk and write accidents (a flipped bit, a write cut short), not an attacker who
            // can write Library: SHA-256 because the Toolkit already has it (Hashing) and it costs nothing next to reading
            // the project; a 64-bit hash would do as well for accidents.
            private const int HeaderBytes = 4 + 4 + 8, ChecksumBytes = 32;

            /// <summary>
            /// The kept readings, or none (a full read) on any doubt: no file, another version, a wrong length or checksum,
            /// a count or a string longer than what remains, a malformed GUID, unknown flags, a path twice, bytes left over.
            /// Any thread; <paramref name="path"/> resolved on the main thread.
            /// </summary>
            public static Dictionary<string, FileRead> Load(string path, ScanStats stats)
            {
                var known = memory;
                if (known != null) { stats.KeptLoaded = true; return known; }
                var none = new Dictionary<string, FileRead>(StringComparer.OrdinalIgnoreCase);
                byte[] bytes;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length < HeaderBytes + ChecksumBytes || info.Length > MaxBytes) return none;
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return none; }
                var result = Parse(bytes);
                if (result == null) return none;
                stats.KeptLoaded = true;
                stats.KeptBytes = bytes.Length;
                return result;
            }

            // The readings in a whole kept file, or null when anything about it is in doubt. Every count and string length is
            // checked against the bytes that remain before anything is allocated for it.
            internal static Dictionary<string, FileRead> Parse(byte[] bytes)
            {
                try
                {
                    var payload = new Payload(bytes, 0, bytes.Length);
                    if (payload.UInt() != Magic || payload.Int() != Version) return null;
                    long length = payload.Long();
                    if (length < 0 || length != bytes.Length - HeaderBytes - ChecksumBytes) return null;
                    string expected = Hashing.Hex(bytes.Skip(HeaderBytes + (int)length).ToArray());
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        if (Hashing.Hex(sha.ComputeHash(bytes, HeaderBytes, (int)length)) != expected) return null;

                    var body = new Payload(bytes, HeaderBytes, HeaderBytes + (int)length);
                    // Each GUID takes at least 33 bytes (its length and 32 digits).
                    int guidCount = body.Count(33);
                    var guids = new string[guidCount];
                    for (int i = 0; i < guidCount; i++)
                    {
                        guids[i] = body.String();
                        if (!IsGuid(guids[i])) return null;
                    }
                    // Each entry takes at least 4 strings, 4 stamps, flags and 2 counts.
                    int count = body.Count(4 + 32 + 1 + 8);
                    var result = new Dictionary<string, FileRead>(Math.Min(count, 1 << 20), StringComparer.OrdinalIgnoreCase);
                    string[] Indexes()
                    {
                        int n = body.Count(4);
                        var values = new string[n];
                        for (int i = 0; i < n; i++)
                        {
                            int index = body.Int();
                            if (index < 0 || index >= guidCount) throw new InvalidDataException("bad index");
                            values[i] = guids[index];
                        }
                        return values;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        var read = new FileRead { Path = body.String() };
                        if (string.IsNullOrEmpty(read.Path) || result.ContainsKey(read.Path)) return null;
                        read.Stamp = new Stamp(body.Long(), body.Long(), body.Long(), body.Long());
                        byte flags = body.Byte();
                        if ((flags & ~7) != 0) return null;
                        read.Imported = (flags & 1) != 0;
                        read.ContentRead = (flags & 2) != 0;
                        read.UnityKnown = (flags & 4) != 0;
                        read.Importer = Null(body.String());
                        read.OwnGuid = Null(body.String());
                        if (read.OwnGuid != null && !IsGuid(read.OwnGuid)) return null;
                        read.DependencyHash = Null(body.String());
                        foreach (string guid in Indexes()) if (!read.Guids.Add(guid)) return null;
                        read.Dependencies = Indexes();
                        result[read.Path] = read;
                    }
                    return body.AtEnd ? result : null;
                }
                catch (InvalidDataException) { return null; }
            }

            private static bool IsGuid(string value)
            {
                if (value == null || value.Length != 32) return false;
                foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
                return true;
            }

            // A bounded reader over part of a byte array: reading past its end, a negative count or a string longer than
            // what remains throws InvalidDataException before anything is allocated.
            private sealed class Payload
            {
                private readonly byte[] bytes;
                private readonly int end;
                private int at;

                public Payload(byte[] bytes, int start, int end) { this.bytes = bytes; at = start; this.end = end; }

                public bool AtEnd => at == end;

                private void Need(long n)
                {
                    if (n < 0 || n > end - at) throw new InvalidDataException("past the end");
                }

                public byte Byte() { Need(1); return bytes[at++]; }
                public int Int() { Need(4); int v = BitConverter.ToInt32(bytes, at); at += 4; return v; }
                public uint UInt() { Need(4); uint v = BitConverter.ToUInt32(bytes, at); at += 4; return v; }
                public long Long() { Need(8); long v = BitConverter.ToInt64(bytes, at); at += 8; return v; }

                /// <summary>A count of items each at least <paramref name="minBytes"/> long: no more than the bytes left allow.</summary>
                public int Count(int minBytes)
                {
                    int n = Int();
                    if (n < 0 || (long)n * minBytes > end - at) throw new InvalidDataException("count past the end");
                    return n;
                }

                public string String()
                {
                    int n = Int();
                    Need(n);
                    string value = Encoding.UTF8.GetString(bytes, at, n);
                    at += n;
                    return value;
                }
            }

            private static string Null(string value) => value.Length == 0 ? null : value;

            // The bytes being written, refusing to grow past the bound as they grow.
            private sealed class Bounded
            {
                public readonly MemoryStream Stream = new MemoryStream();
                private readonly long limit;
                public Bounded(long limit) { this.limit = limit; }
                private void Check() { if (Stream.Length > limit) throw new InvalidDataException("past the size bound"); }
                public void Int(int v) { Stream.Write(BitConverter.GetBytes(v), 0, 4); Check(); }
                public void Long(long v) { Stream.Write(BitConverter.GetBytes(v), 0, 8); Check(); }
                public void Byte(byte v) { Stream.WriteByte(v); Check(); }
                public void String(string v)
                {
                    var data = Encoding.UTF8.GetBytes(v ?? "");
                    Int(data.Length);
                    Stream.Write(data, 0, data.Length);
                    Check();
                }
            }

            /// <summary>Keeps <paramref name="reads"/> (those read whole) for the next scan. Any thread; false when not written.</summary>
            public static bool Save(string path, List<FileRead> reads)
            {
                var keep = reads.Where(r => r != null && r.Failure == null && !string.IsNullOrEmpty(r.Path)).GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last()).ToList();
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
                    var body = new Bounded(MaxBytes - HeaderBytes - ChecksumBytes);
                    body.Int(guids.Count);
                    foreach (string guid in guids) body.String(guid);
                    body.Int(keep.Count);
                    foreach (var read in keep)
                    {
                        body.String(read.Path);
                        body.Long(read.Stamp.Size); body.Long(read.Stamp.Ticks); body.Long(read.Stamp.MetaSize); body.Long(read.Stamp.MetaTicks);
                        body.Byte((byte)((read.Imported ? 1 : 0) | (read.ContentRead ? 2 : 0) | (read.UnityKnown ? 4 : 0)));
                        body.String(read.Importer);
                        body.String(read.OwnGuid);
                        body.String(read.DependencyHash);
                        body.Int(read.Guids.Count);
                        foreach (string guid in read.Guids) body.Int(indexes[guid]);
                        var dependencies = read.Dependencies ?? Array.Empty<string>();
                        body.Int(dependencies.Length);
                        foreach (string guid in dependencies) body.Int(indexes[guid]);
                    }
                    var payload = body.Stream.ToArray();
                    byte[] checksum;
                    using (var sha = System.Security.Cryptography.SHA256.Create()) checksum = sha.ComputeHash(payload);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        file.Write(BitConverter.GetBytes(Magic), 0, 4);
                        file.Write(BitConverter.GetBytes(Version), 0, 4);
                        file.Write(BitConverter.GetBytes((long)payload.Length), 0, 8);
                        file.Write(payload, 0, payload.Length);
                        file.Write(checksum, 0, checksum.Length);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    // Past the bound: not kept on disk (the next session reads every file again), and no old copy left.
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
