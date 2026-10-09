using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The one reader of .unitypackage files (a gzipped tar of &lt;guid&gt;/pathname, &lt;guid&gt;/asset, &lt;guid&gt;/asset.meta
    /// and &lt;guid&gt;/preview.png records), shared by every Orbiters tool: the import consent index, asset hashes and filtered
    /// copies, MCB's source FBX extraction and the Unity Package Manager. It checks the archive's structure while it streams
    /// (header checksums, record names, sizes declared before anything is allocated, a bound on the expanded size and the
    /// entry count, metadata naming its record's GUID, one GUID per asset path) and hands each record to the caller, which
    /// reads as much of it as it needs.
    /// </summary>
    public static class UnityPackageReader
    {
        public enum Part { Pathname, Asset, Meta, Preview }

        public const long DefaultMaxExpandedBytes = 8L * 1024 * 1024 * 1024;
        public const int DefaultMaxEntries = 200000;

        public sealed class Options
        {
            public long MaxExpandedBytes = DefaultMaxExpandedBytes;
            public int MaxEntries = DefaultMaxEntries;
            /// <summary>The pathnames of every entry together, as the archive stores them.</summary>
            public long MaxPathnameTotalBytes = UnityPackageIndex.DefaultMaxPathnameTotalBytes;
            /// <summary>
            /// Read a pathname's first line, as Unity's importer does (some exporters write more after it, e.g. "\n00"),
            /// instead of refusing it as ambiguous. Tools that only read a package may; the import consent check does not.
            /// </summary>
            public bool FirstLinePathname;
        }

        public sealed class Record
        {
            public string Guid;
            public Part Part;
            public long Size;
            /// <summary>For a pathname record: the asset path it names, with '/' separators.</summary>
            public string Pathname;
            /// <summary>The record's data. Whatever the caller leaves unread is skipped.</summary>
            public Stream Content;
            /// <summary>The record's tar header as stored, after its GNU long-name record when it has one: to copy it as is.</summary>
            public byte[] Header;

            /// <summary>All of the record's data, in a buffer that grows with what the archive holds, not what it declares.</summary>
            public byte[] ReadAll()
            {
                if (Size > int.MaxValue) throw new InvalidDataException("Package entry is too large to read into memory.");
                var buffer = new byte[Math.Min(Size, 1024 * 1024)];
                int offset = 0;
                while (offset < Size)
                {
                    if (offset == buffer.Length) Array.Resize(ref buffer, (int)Math.Min(Size, buffer.LongLength * 2));
                    int read = Content.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("Package entry is truncated.");
                    offset += read;
                }
                return buffer;
            }
        }

        /// <summary>What the archive holds: the asset path of each GUID (in archive order) and its expanded size.</summary>
        public sealed class Summary
        {
            public readonly Dictionary<string, string> Pathnames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public long ExpandedBytes;
            public int Entries;
        }

        public static Summary Read(string path, Options options = null, Action<Record> visit = null)
        {
            options = options ?? new Options();
            if (options.MaxExpandedBytes < 1024 || options.MaxEntries < 1 || options.MaxPathnameTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(options));
            string file = System.IO.Path.GetFileName(path);
            var summary = new Summary();
            var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long names = 0;
            using (var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            {
                var header = new byte[512];
                string longName = null;
                byte[] longNameRecord = null;
                bool terminated = false;
                while (ReadExactly(stream, header, 512))
                {
                    summary.ExpandedBytes += 512;
                    if (summary.ExpandedBytes > options.MaxExpandedBytes) throw new InvalidDataException("Package exceeds the expanded size limit.");
                    if (header.All(b => b == 0))
                    {
                        if (longName != null) throw new InvalidDataException("A long entry name has no following entry.");
                        if (!ReadExactly(stream, header, 512) || header.Any(b => b != 0)) throw new InvalidDataException("Package has an invalid tar terminator.");
                        summary.ExpandedBytes += 512;
                        // Tar writers may pad their final record with zero blocks, but no entries may hide after the terminator.
                        if (summary.ExpandedBytes > options.MaxExpandedBytes) throw new InvalidDataException("Package exceeds the expanded size limit.");
                        var trailer = new byte[81920];
                        for (int count; (count = stream.Read(trailer, 0, trailer.Length)) > 0;)
                        {
                            summary.ExpandedBytes += count;
                            if (summary.ExpandedBytes > options.MaxExpandedBytes || trailer.Take(count).Any(b => b != 0)) throw new InvalidDataException("Package has data after its tar terminator.");
                        }
                        terminated = true;
                        break;
                    }
                    Checksum(header);
                    if (++summary.Entries > options.MaxEntries) throw new InvalidDataException(file + " has too many entries to be a Unity package.");
                    string name = longName ?? Ascii(header, 0, 100);
                    string prefix = Ascii(header, 345, 155);
                    if (longName == null && prefix.Length > 0) name = prefix + "/" + name;
                    var stored = longNameRecord != null ? longNameRecord.Concat(header).ToArray() : (byte[])header.Clone();
                    longName = null; longNameRecord = null;
                    long size = Size(header, file);
                    char type = (char)header[156];
                    if (type != '\0' && type != '0' && type != '5' && type != 'L') throw new InvalidDataException("Package contains unsupported tar links or extension records.");
                    long padding = (512 - size % 512) % 512;
                    summary.ExpandedBytes += size + padding;
                    if (summary.ExpandedBytes > options.MaxExpandedBytes) throw new InvalidDataException(file + " expands beyond the size limit for a package.");
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    var parts = name.TrimEnd('/').Split('/');
                    string guid = parts.Length == 2 ? parts[0] : null, kind = parts.Length == 2 ? parts[1] : null;
                    if (type == '5')
                    {
                        if (size != 0 || !(name == "" || name == "." || parts.Length == 1 && IsGuid(parts[0]))) throw new InvalidDataException("Package has an invalid directory record.");
                        continue;
                    }
                    if (type == 'L')
                    {
                        var data = Pathname(stream, size, file, ref names, options);
                        Padding(stream, padding);
                        longName = Decode(data).TrimEnd('\0');
                        if (longName.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid long entry name.");
                        longNameRecord = stored.Concat(data).Concat(new byte[padding]).ToArray();
                        continue;
                    }
                    // Packages exported with an icon carry it as one root entry; it is never imported.
                    if (name == ".icon.png")
                    {
                        if (!Skip(stream, size)) throw new EndOfStreamException(file + " is truncated.");
                        Padding(stream, padding);
                        continue;
                    }
                    if (name.EndsWith("/", StringComparison.Ordinal) || !IsGuid(guid) || (kind != "pathname" && kind != "asset" && kind != "asset.meta" && kind != "preview.png"))
                        throw new InvalidDataException("Package has an ambiguous asset record name.");
                    if (!records.Add(guid + "/" + kind)) throw new InvalidDataException("Package repeats an asset record.");
                    if (kind == "asset" || kind == "asset.meta") assets.Add(guid);

                    var record = new Record { Guid = guid, Size = size, Header = stored };
                    RecordStream content;
                    MetaGuid meta = null;
                    if (kind == "pathname")
                    {
                        var data = Pathname(stream, size, file, ref names, options);
                        string pathname = AssetPath(Decode(data), options.FirstLinePathname);
                        if (!targets.Add(pathname)) throw new InvalidDataException("Package assigns several GUIDs to the same asset path.");
                        summary.Pathnames.Add(guid, pathname);
                        record.Part = Part.Pathname; record.Pathname = pathname;
                        content = new RecordStream(new MemoryStream(data), size, null, file);
                    }
                    else
                    {
                        record.Part = kind == "asset" ? Part.Asset : kind == "asset.meta" ? Part.Meta : Part.Preview;
                        // Metadata can be large (FBX and sprite importers): its GUID is checked as it passes, whoever reads it.
                        if (record.Part == Part.Meta) meta = new MetaGuid(guid);
                        content = new RecordStream(stream, size, meta, file);
                    }
                    record.Content = content;
                    visit?.Invoke(record);
                    content.Drain();
                    meta?.End();
                    Padding(stream, padding);
                }
                if (!terminated) throw new InvalidDataException("Package has no complete tar terminator.");
            }
            if (assets.Any(g => !summary.Pathnames.ContainsKey(g))) throw new InvalidDataException("Package has asset data without a pathname.");
            return summary;
        }

        private static byte[] Pathname(Stream stream, long size, string file, ref long names, Options options)
        {
            if (size > UnityPackageIndex.MaxPathnameBytes) throw new InvalidDataException(file + " has an invalid entry name.");
            if ((names += size) > options.MaxPathnameTotalBytes) throw new InvalidDataException(file + " names too many files to be read safely.");
            var data = new byte[size];
            if (!ReadExactly(stream, data, (int)size)) throw new EndOfStreamException(file + " is truncated.");
            return data;
        }

        private static string AssetPath(string pathname, bool firstLine)
        {
            if (firstLine)
            {
                int end = pathname.IndexOf('\n');
                if (end >= 0) pathname = pathname.Substring(0, end);
                pathname = pathname.TrimEnd('\r', '\0');
            }
            else
            {
                // A single terminal newline is harmless; embedded records and NULs are read differently by tar consumers.
                if (pathname.EndsWith("\n", StringComparison.Ordinal)) pathname = pathname.Substring(0, pathname.Length - 1);
                if (pathname.EndsWith("\r", StringComparison.Ordinal)) pathname = pathname.Substring(0, pathname.Length - 1);
            }
            if (pathname.Any(char.IsControl)) throw new InvalidDataException("Package has an ambiguous asset pathname.");
            return pathname.Replace('\\', '/');
        }

        private static void Padding(Stream stream, long padding)
        {
            for (long i = 0; i < padding; i++)
                if (stream.ReadByte() != 0) throw new InvalidDataException("Package has truncated or invalid entry padding.");
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

        // Octal digits only (the POSIX field); the GNU base-256 form marks sizes no avatar package needs.
        private static long Size(byte[] header, string file)
        {
            if ((header[124] & 0x80) != 0) throw new InvalidDataException(file + " declares an entry too large for a package.");
            string field = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
            if (field.Length == 0 || field.Any(c => c < '0' || c > '7')) throw new InvalidDataException(file + " has an invalid tar size.");
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

        // Rejects metadata naming a different or a second top-level GUID, so the consent check cannot resolve another
        // destination than Unity's importer; reads lines of at most 128 characters, whatever the file's size.
        private sealed class MetaGuid
        {
            private readonly string guid;
            private readonly StringBuilder line = new StringBuilder(128);
            private bool overflow, found;

            public MetaGuid(string guid) { this.guid = guid; }

            public void Observe(byte[] buffer, int offset, int count)
            {
                for (int i = offset; i < offset + count; i++)
                    if (buffer[i] == '\n') Line(); else if (line.Length < 128) line.Append((char)buffer[i]); else overflow = true;
            }

            public void End()
            {
                Line();
                if (!found) throw new InvalidDataException("Package metadata has no asset GUID.");
            }

            private void Line()
            {
                string text = line.ToString().TrimEnd('\r');
                if (text.StartsWith("guid:", StringComparison.Ordinal))
                {
                    if (overflow || found || !string.Equals(text.Substring(5).Trim(), guid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package metadata GUID differs from its asset record.");
                    found = true;
                }
                line.Clear(); overflow = false;
            }
        }

        // One record's data within the archive: reads stop at its end, and what the caller leaves is drained after it.
        private sealed class RecordStream : Stream
        {
            private readonly Stream source;
            private readonly MetaGuid meta;
            private readonly string file;
            private long remaining;

            public RecordStream(Stream source, long size, MetaGuid meta, string file)
            {
                this.source = source; remaining = size; this.meta = meta; this.file = file;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining <= 0 || count <= 0) return 0;
                int read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
                if (read <= 0) throw new EndOfStreamException(file + " is truncated.");
                remaining -= read;
                meta?.Observe(buffer, offset, read);
                return read;
            }

            public void Drain()
            {
                if (remaining <= 0) return;
                if (meta == null) { if (!Skip(source, remaining)) throw new EndOfStreamException(file + " is truncated."); remaining = 0; return; }
                var buffer = new byte[81920];
                while (remaining > 0) Read(buffer, 0, buffer.Length);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
