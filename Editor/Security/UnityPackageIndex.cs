using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What a .unitypackage (a gzipped tar of &lt;guid&gt;/pathname, &lt;guid&gt;/asset and &lt;guid&gt;/asset.meta entries)
    /// would import, read without importing it. Sizes declared by the archive are checked before anything is allocated
    /// and the expanded size is bounded, so a small crafted file cannot exhaust memory or keep Unity busy decompressing.
    /// </summary>
    public sealed class UnityPackageIndex
    {
        public sealed class Entry
        {
            public string Guid, Path;
            /// <summary>The asset's bytes when the reader was asked to keep them (small text files such as readmes).</summary>
            public byte[] Content;
        }

        public const int MaxPathnameBytes = 4096;
        public readonly List<Entry> Entries = new List<Entry>();

        public IEnumerable<string> Paths => Entries.Select(e => e.Path);
        public List<string> CodeFiles => CodeContent.Filter(Paths);
        /// <summary>Unity may resolve an imported GUID to an existing asset; include code at that destination too.</summary>
        public List<string> CodeFilesIncludingExisting(Func<string, string> resolveGuid) => CodeContent.Filter(Paths.Concat(
            resolveGuid == null ? Enumerable.Empty<string>() : Entries.Select(e => resolveGuid(e.Guid)).Where(p => !string.IsNullOrEmpty(p))));
        /// <summary>Entries whose path leaves the project (absolute, "..", or outside Assets/ and Packages/).</summary>
        public List<string> UnsafePaths => Entries.Where(e => !IsProjectPath(e.Path)).Select(e => e.Path).ToList();

        /// <param name="keepContent">Which asset paths to keep the bytes of (at most <paramref name="maxContentBytes"/> each).</param>
        public static UnityPackageIndex Read(string path, Func<string, bool> keepContent = null, int maxContentBytes = 64 * 1024,
            long maxExpandedBytes = 8L * 1024 * 1024 * 1024, int maxEntries = 200000)
        {
            if (maxContentBytes < 0 || maxExpandedBytes < 1024 || maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxExpandedBytes));
            var index = new UnityPackageIndex();
            var pathnames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0, kept = 0;
            int entries = 0;
            // Small assets are kept until their pathname says whether they are wanted: bounded in total too.
            const long MaxKeptBytes = 16 * 1024 * 1024;
            using (var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            {
                var header = new byte[512];
                string longName = null;
                bool terminated = false;
                while (ReadExactly(stream, header, 512))
                {
                    expanded += 512;
                    if (expanded > maxExpandedBytes) throw new InvalidDataException("Package exceeds the expanded size limit.");
                    if (header.All(b => b == 0))
                    {
                        if (longName != null) throw new InvalidDataException("A long entry name has no following entry.");
                        if (!ReadExactly(stream, header, 512) || header.Any(b => b != 0)) throw new InvalidDataException("Package has an invalid tar terminator.");
                        expanded += 512;
                        // Tar writers may pad their final record with zero blocks, but no entries may hide after the terminator.
                        if (expanded > maxExpandedBytes) throw new InvalidDataException("Package exceeds the expanded size limit.");
                        var trailer = new byte[81920];
                        for (int count; (count = stream.Read(trailer, 0, trailer.Length)) > 0;)
                        {
                            expanded += count;
                            if (expanded > maxExpandedBytes || trailer.Take(count).Any(b => b != 0)) throw new InvalidDataException("Package has data after its tar terminator.");
                        }
                        terminated = true;
                        break;
                    }
                    Checksum(header);
                    if (++entries > maxEntries) throw new InvalidDataException(System.IO.Path.GetFileName(path) + " has too many entries to be a Unity package.");
                    string name = longName ?? Ascii(header, 0, 100);
                    string prefix = Ascii(header, 345, 155);
                    if (longName == null && prefix.Length > 0) name = prefix + "/" + name;
                    longName = null;
                    long size = Size(header, path);
                    char type = (char)header[156];
                    if (type != '\0' && type != '0' && type != '5' && type != 'L') throw new InvalidDataException("Package contains unsupported tar links or extension records.");
                    long padding = (512 - size % 512) % 512;
                    expanded += size + padding;
                    if (expanded > maxExpandedBytes) throw new InvalidDataException(System.IO.Path.GetFileName(path) + " expands beyond the size limit for a package.");
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    var parts = name.TrimEnd('/').Split('/');
                    string guid = parts.Length == 2 ? parts[0] : null, kind = parts.Length == 2 ? parts[1] : null;
                    if (type == '5')
                    {
                        if (size != 0 || !(name == "" || name == "." || parts.Length == 1 && IsGuid(parts[0]))) throw new InvalidDataException("Package has an invalid directory record.");
                        continue;
                    }
                    if (type != 'L')
                    {
                        if (name.EndsWith("/", StringComparison.Ordinal) || !IsGuid(guid) || (kind != "pathname" && kind != "asset" && kind != "asset.meta" && kind != "preview.png"))
                            throw new InvalidDataException("Package has an ambiguous asset record name.");
                        if (!records.Add(guid + "/" + kind)) throw new InvalidDataException("Package repeats an asset record.");
                        if (kind == "asset" || kind == "asset.meta") assets.Add(guid);
                    }
                    byte[] data = null;
                    if (type == 'L' || kind == "pathname")
                    {
                        if (size > MaxPathnameBytes) throw new InvalidDataException(System.IO.Path.GetFileName(path) + " has an invalid entry name.");
                        data = new byte[size];
                        if (!ReadExactly(stream, data, (int)size)) throw new EndOfStreamException(System.IO.Path.GetFileName(path) + " is truncated.");
                    }
                    else if (kind == "asset.meta") ReadMeta(stream, size, guid);
                    else if (kind == "asset" && keepContent != null && size <= maxContentBytes && kept + size <= MaxKeptBytes)
                    {
                        kept += size;
                        data = new byte[size];
                        if (!ReadExactly(stream, data, (int)size)) throw new EndOfStreamException(System.IO.Path.GetFileName(path) + " is truncated.");
                    }
                    else if (!Skip(stream, size)) throw new EndOfStreamException(System.IO.Path.GetFileName(path) + " is truncated.");
                    for (long i = 0; i < padding; i++)
                        if (stream.ReadByte() != 0) throw new InvalidDataException("Package has truncated or invalid entry padding.");
                    if (type == 'L') { longName = Decode(data).TrimEnd('\0'); if (longName.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid long entry name."); continue; }
                    if (data == null || guid == null) continue;
                    if (kind == "pathname")
                    {
                        string pathname = Decode(data);
                        // A single terminal newline is harmless; embedded records/NULs are interpreted differently by tar consumers.
                        if (pathname.EndsWith("\n", StringComparison.Ordinal)) pathname = pathname.Substring(0, pathname.Length - 1);
                        if (pathname.EndsWith("\r", StringComparison.Ordinal)) pathname = pathname.Substring(0, pathname.Length - 1);
                        if (pathname.Any(char.IsControl)) throw new InvalidDataException("Package has an ambiguous asset pathname.");
                        pathname = pathname.Replace('\\', '/');
                        if (!targets.Add(pathname)) throw new InvalidDataException("Package assigns several GUIDs to the same asset path.");
                        pathnames.Add(guid, pathname);
                    }
                    else contents[guid] = data;
                }
                if (!terminated) throw new InvalidDataException("Package has no complete tar terminator.");
            }
            if (assets.Any(g => !pathnames.ContainsKey(g))) throw new InvalidDataException("Package has asset data without a pathname.");
            foreach (var pair in pathnames)
            {
                contents.TryGetValue(pair.Key, out var content);
                index.Entries.Add(new Entry { Guid = pair.Key, Path = pair.Value, Content = content != null && keepContent(pair.Value) ? content : null });
            }
            return index;
        }

        public static bool IsProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path) || path.Any(char.IsControl) || path.IndexOfAny(new[] { ':', '<', '>', '"', '|', '?', '*' }) >= 0) return false;
            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Any(p => string.IsNullOrEmpty(p) || p == ".." || p == "." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) ||
                Regex.IsMatch(p, @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))) return false;
            return parts[0] == "Assets" || parts[0] == "Packages";
        }

        private static bool IsGuid(string value) => value != null && value.Length == 32 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
        private static string Decode(byte[] data)
        {
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Package contains an invalid UTF-8 pathname.", ex); }
        }

        private static void Checksum(byte[] header)
        {
            string field = Encoding.ASCII.GetString(header, 148, 8).Trim('\0', ' ');
            if (field.Length == 0 || field.Any(c => c < '0' || c > '7')) throw new InvalidDataException("Package has an invalid tar checksum.");
            long expected = Convert.ToInt64(field, 8), actual = 0;
            for (int i = 0; i < header.Length; i++) actual += i >= 148 && i < 156 ? 32 : header[i];
            if (expected != actual) throw new InvalidDataException("Package has an invalid tar checksum.");
        }

        // Metadata can be large (FBX/sprite importers). Scan it without allocating the whole file, rejecting a different or
        // repeated top-level GUID so the consent check cannot resolve a different destination from Unity's importer.
        private static void ReadMeta(Stream stream, long size, string guid)
        {
            var line = new StringBuilder(128); bool overflow = false, found = false;
            void CheckLine()
            {
                string text = line.ToString().TrimEnd('\r');
                if (text.StartsWith("guid:", StringComparison.Ordinal))
                {
                    if (overflow || found || !string.Equals(text.Substring(5).Trim(), guid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package metadata GUID differs from its asset record.");
                    found = true;
                }
                line.Clear(); overflow = false;
            }
            var buffer = new byte[81920];
            while (size > 0)
            {
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size));
                if (count == 0) throw new EndOfStreamException("Package metadata is truncated.");
                size -= count;
                for (int i = 0; i < count; i++)
                    if (buffer[i] == '\n') CheckLine(); else if (line.Length < 128) line.Append((char)buffer[i]); else overflow = true;
            }
            CheckLine();
            if (!found) throw new InvalidDataException("Package metadata has no asset GUID.");
        }

        // Octal digits only (the POSIX field); the GNU base-256 form marks sizes no avatar package needs.
        private static long Size(byte[] header, string path)
        {
            if ((header[124] & 0x80) != 0) throw new InvalidDataException(System.IO.Path.GetFileName(path) + " declares an entry too large for a package.");
            string field = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
            if (field.Length == 0 || field.Any(c => c < '0' || c > '7')) throw new InvalidDataException(System.IO.Path.GetFileName(path) + " has an invalid tar size.");
            return Convert.ToInt64(field, 8);
        }

        private static string Ascii(byte[] buffer, int offset, int length)
        {
            int end = Array.IndexOf(buffer, (byte)0, offset, length);
            return Encoding.UTF8.GetString(buffer, offset, (end < 0 ? offset + length : end) - offset);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) { if (read > 0) throw new EndOfStreamException("Package has a truncated tar record."); return false; }
                read += n;
            }
            return true;
        }

        private static readonly byte[] SkipBuffer = new byte[81920];

        private static bool Skip(Stream stream, long count)
        {
            lock (SkipBuffer)
                while (count > 0)
                {
                    int n = stream.Read(SkipBuffer, 0, (int)Math.Min(SkipBuffer.Length, count));
                    if (n <= 0) return false;
                    count -= n;
                }
            return true;
        }
    }
}
