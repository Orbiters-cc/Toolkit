using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.Storage;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The file contents of a .unitypackage, read without importing it: the SHA-256 of each asset it carries (to tell what an
    /// import would change in the project) and a filtered copy with only some of its entries (to import part of it).
    /// Use <see cref="UnityPackageIndex"/> first to validate the package; these readers trust its structure.
    /// </summary>
    public static class UnityPackageFiles
    {
        private readonly struct Stamp : IEquatable<Stamp>
        {
            public readonly long Length, Ticks;
            public Stamp(FileInfo info) { Length = info.Length; Ticks = info.LastWriteTimeUtc.Ticks; }
            public bool Equals(Stamp other) => Length == other.Length && Ticks == other.Ticks;
        }

        private static readonly Dictionary<string, (Stamp stamp, Dictionary<string, string> hashes)> Cache =
            new Dictionary<string, (Stamp, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>SHA-256 (lowercase hex) of each asset a Unity package carries, by GUID; cached while the file is unchanged.</summary>
        public static Dictionary<string, string> AssetHashes(string packagePath, long maxExpandedBytes = ArchiveBudget.DefaultMaxTotalBytes)
        {
            string fullPath = Path.GetFullPath(packagePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists) throw new FileNotFoundException("Unity package not found.", fullPath);
            var stamp = new Stamp(info);
            lock (Cache)
                if (Cache.TryGetValue(fullPath, out var cached) && cached.stamp.Equals(stamp)) return cached.hashes;

            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var header = new byte[512];
            var buffer = new byte[81920];
            string longName = null;
            long expanded = 0;
            using (var input = new GZipStream(File.OpenRead(fullPath), CompressionMode.Decompress))
            {
                while (Read(input, header, 0, 512) && header.Any(b => b != 0))
                {
                    long size = Size(header);
                    long padded = (size + 511) / 512 * 512;
                    expanded += 512 + padded;
                    if (expanded > maxExpandedBytes) throw new InvalidDataException("The Unity package expands beyond the size limit.");
                    if (header[156] == (byte)'L')
                    {
                        if (size > UnityPackageIndex.MaxPathnameBytes) throw new InvalidDataException("The Unity package has an invalid entry name.");
                        var name = new byte[padded];
                        if (!Read(input, name, 0, (int)padded)) throw new EndOfStreamException("The Unity package is truncated.");
                        longName = Encoding.UTF8.GetString(name, 0, (int)size).TrimEnd('\0');
                        continue;
                    }

                    string prefix = Text(header, 345, 155);
                    string[] parts = (longName ?? (prefix.Length > 0 ? prefix + "/" : "") + Text(header, 0, 100)).TrimStart('.', '/').Split('/');
                    longName = null;
                    using (var sha = parts.Length == 2 && parts[1] == "asset" ? SHA256.Create() : null)
                    {
                        for (long remaining = padded, content = size; remaining > 0;)
                        {
                            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read <= 0) throw new EndOfStreamException("The Unity package is truncated.");
                            int data = (int)Math.Min(read, content);
                            if (sha != null && data > 0) sha.TransformBlock(buffer, 0, data, null, 0);
                            content -= data;
                            remaining -= read;
                        }
                        if (sha == null) continue;
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        hashes[parts[0]] = Hex(sha.Hash);
                    }
                }
            }

            lock (Cache)
            {
                if (Cache.Count > 64) Cache.Clear();
                Cache[fullPath] = (stamp, hashes);
            }
            return hashes;
        }

        /// <summary>Writes a package holding only the entries of <paramref name="guids"/>.</summary>
        public static void CopyEntries(string source, string destination, ISet<string> guids)
        {
            var header = new byte[512];
            var buffer = new byte[81920];
            byte[] longNameRecord = null;
            string longName = null;
            using (var input = new GZipStream(File.OpenRead(source), CompressionMode.Decompress))
            using (var output = new GZipStream(File.Create(destination), CompressionLevel.Fastest))
            {
                while (Read(input, header, 0, 512) && header.Any(b => b != 0))
                {
                    long size = Size(header);
                    long padded = (size + 511) / 512 * 512;
                    if (header[156] == (byte)'L')
                    {
                        longNameRecord = new byte[512 + padded];
                        Buffer.BlockCopy(header, 0, longNameRecord, 0, 512);
                        if (!Read(input, longNameRecord, 512, (int)padded)) throw new EndOfStreamException("The Unity package is truncated.");
                        longName = Encoding.UTF8.GetString(longNameRecord, 512, (int)size).TrimEnd('\0');
                        continue;
                    }

                    string prefix = Text(header, 345, 155);
                    string name = longName ?? (prefix.Length > 0 ? prefix + "/" : "") + Text(header, 0, 100);
                    bool keep = guids.Contains(name.TrimStart('.', '/').Split('/')[0]);
                    if (keep)
                    {
                        if (longNameRecord != null) output.Write(longNameRecord, 0, longNameRecord.Length);
                        output.Write(header, 0, 512);
                    }
                    longNameRecord = null;
                    longName = null;

                    for (long remaining = padded; remaining > 0;)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) throw new EndOfStreamException("The Unity package is truncated.");
                        if (keep) output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }

                output.Write(new byte[1024], 0, 1024);
            }
        }

        /// <summary>SHA-256 of a file, lowercase hex.</summary>
        public static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        private static long Size(byte[] header)
        {
            long size = 0;
            for (int i = 124; i < 136; i++)
            {
                byte b = header[i];
                if (b == 0 || b == (byte)' ') { if (size > 0) break; continue; }
                if (b < (byte)'0' || b > (byte)'7') throw new InvalidDataException("The Unity package has an invalid entry size.");
                size = size * 8 + (b - '0');
            }
            return size;
        }

        private static bool Read(Stream stream, byte[] buffer, int offset, int count)
        {
            for (int read = 0; read < count;)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static string Text(byte[] header, int offset, int length)
        {
            int end = Array.IndexOf(header, (byte)0, offset, length);
            return Encoding.UTF8.GetString(header, offset, (end < 0 ? offset + length : end) - offset);
        }
    }
}
