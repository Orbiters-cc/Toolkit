using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Orbiters.Toolkit.Editor.Storage;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The file contents of a .unitypackage, read without importing it: the SHA-256 of each asset it carries (to tell what an
    /// import would change in the project) and a filtered copy with only some of its entries (to import part of it).
    /// Both go through <see cref="UnityPackageReader"/>, which checks the archive's structure as it reads.
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
            var buffer = new byte[81920];
            UnityPackageReader.Read(fullPath, new UnityPackageReader.Options { MaxExpandedBytes = maxExpandedBytes }, record =>
            {
                if (record.Part != UnityPackageReader.Part.Asset) return;
                using (var sha = SHA256.Create())
                {
                    for (int read; (read = record.Content.Read(buffer, 0, buffer.Length)) > 0;) sha.TransformBlock(buffer, 0, read, null, 0);
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hashes[record.Guid] = Hex(sha.Hash);
                }
            });

            lock (Cache)
            {
                if (Cache.Count > 64) Cache.Clear();
                Cache[fullPath] = (stamp, hashes);
            }
            return hashes;
        }

        /// <summary>Writes a package holding only the entries of <paramref name="guids"/>, each record copied as stored.</summary>
        public static void CopyEntries(string source, string destination, ISet<string> guids)
        {
            using (var output = new GZipStream(File.Create(destination), CompressionLevel.Fastest))
            {
                UnityPackageReader.Read(source, null, record =>
                {
                    if (!guids.Contains(record.Guid)) return;
                    output.Write(record.Header, 0, record.Header.Length);
                    record.Content.CopyTo(output);
                    int padding = (int)((512 - record.Size % 512) % 512);
                    output.Write(new byte[padding], 0, padding);
                });
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
    }
}
