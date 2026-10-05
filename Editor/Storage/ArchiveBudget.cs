using System;
using System.IO;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// How far a downloaded archive may expand, shared by its extraction and by the scans of the Unity packages it carries.
    /// Entries and bytes are counted as they are actually decompressed; the sizes an archive declares are never trusted.
    /// </summary>
    public class ArchiveBudget
    {
        public const int DefaultMaxEntries = 100000;
        public const long DefaultMaxTotalBytes = 8L * 1024 * 1024 * 1024;
        public const long DefaultMaxEntryBytes = 4L * 1024 * 1024 * 1024;
        public const long DefaultMaxCapturedBytes = 2L * 1024 * 1024 * 1024;

        public readonly int MaxEntries;
        public readonly long MaxTotalBytes;
        public readonly long MaxEntryBytes;
        /// <summary>Bytes kept in memory for callers; an entry that does not fit is only written to disk.</summary>
        public readonly long MaxCapturedBytes;
        /// <summary>What the archive holds, for messages ("version", "package").</summary>
        public readonly string Subject;

        public int Entries { get; private set; }
        public long TotalBytes { get; private set; }
        public long CapturedBytes { get; private set; }
        public int RemainingEntries => Math.Max(0, MaxEntries - Entries);
        public long RemainingBytes => Math.Max(0L, MaxTotalBytes - TotalBytes);

        public ArchiveBudget(int maxEntries = DefaultMaxEntries, long maxTotalBytes = DefaultMaxTotalBytes,
            long maxEntryBytes = DefaultMaxEntryBytes, long maxCapturedBytes = DefaultMaxCapturedBytes, string subject = "version")
        {
            if (maxEntries < 1 || maxTotalBytes < 1 || maxEntryBytes < 1 || maxCapturedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            MaxEntries = maxEntries;
            MaxTotalBytes = maxTotalBytes;
            MaxEntryBytes = maxEntryBytes;
            MaxCapturedBytes = maxCapturedBytes;
            Subject = string.IsNullOrWhiteSpace(subject) ? "version" : subject;
        }

        public void AddEntries(int count)
        {
            if (count < 0 || count > RemainingEntries)
                throw new InvalidDataException($"The {Subject} archive has more than {MaxEntries} files, the limit for a {Subject}.");
            Entries += count;
        }

        public void AddBytes(long bytes, string name)
        {
            if (bytes < 0 || bytes > RemainingBytes)
                throw new InvalidDataException($"The {Subject} archive expands beyond {Format(MaxTotalBytes)}, the size limit for a {Subject} (at '{name}').");
            TotalBytes += bytes;
        }

        /// <summary>Copies one entry as it decompresses, within the entry and archive limits.</summary>
        /// <param name="capture">Also keep the bytes in memory, while they fit <see cref="MaxCapturedBytes"/>.</param>
        /// <returns>The entry's bytes when captured, otherwise null.</returns>
        public byte[] Copy(Stream source, Stream destination, string name, bool capture = false)
        {
            var memory = capture ? new MemoryStream() : null;
            try
            {
                var buffer = new byte[81920];
                long entryBytes = 0;
                for (int read; (read = source.Read(buffer, 0, buffer.Length)) > 0;)
                {
                    entryBytes += read;
                    if (entryBytes > MaxEntryBytes)
                        throw new InvalidDataException($"'{name}' expands beyond {Format(MaxEntryBytes)}, the size limit for one file of a {Subject}.");
                    AddBytes(read, name);
                    destination.Write(buffer, 0, read);
                    if (memory == null) continue;
                    if (CapturedBytes + memory.Length + read > MaxCapturedBytes || memory.Length + read > int.MaxValue - 64)
                    {
                        Debug.Log($"[Orbiters] '{name}' is too large to keep in memory; it is read from disk.");
                        memory.Dispose();
                        memory = null;
                    }
                    else memory.Write(buffer, 0, read);
                }

                if (memory == null) return null;
                CapturedBytes += memory.Length;
                return memory.ToArray();
            }
            finally
            {
                memory?.Dispose();
            }
        }

        public static string Format(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.#} GB" :
            bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" :
            bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} bytes";
    }
}
