using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Orbiters.Toolkit.Storage;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// Validated, transactional ZIP extraction: every entry is checked before anything is written, files expand into a
    /// sibling staging folder within an <see cref="ArchiveBudget"/>, a single root folder is stripped, and the destination
    /// is replaced in one rename only once the caller validated the result. The previous folder stays intact on failure.
    /// </summary>
    public static class SafeArchive
    {
        /// <param name="capture">Relative paths whose bytes to return (within the budget's captured limit).</param>
        /// <param name="validate">Called with the staging folder before it replaces the destination; throw to refuse.</param>
        /// <returns>The captured entries' bytes, by relative path.</returns>
        public static Dictionary<string, byte[]> Extract(Stream zipStream, string destination, ISet<string> capture = null,
            Action<string> validate = null, ArchiveBudget budget = null)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentNullException(nameof(destination));
            budget = budget ?? new ArchiveBudget();
            var captured = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>((capture ?? new HashSet<string>()).Select(Normalize).Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);

            string finalPath = Path.GetFullPath(destination);
            string staging = finalPath + ".building-" + Guid.NewGuid().ToString("N");
            try
            {
                // Open the archive before making a staging directory; the previous folder stays intact.
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
                {
                    SafePaths.RejectLinks(Path.GetDirectoryName(finalPath), finalPath);
                    budget.AddEntries(archive.Entries.Count);
                    Directory.CreateDirectory(staging);
                    var files = archive.Entries.Where(entry => entry != null && !IsDirectory(entry)).ToList();
                    if (files.Count == 0) throw new InvalidDataException($"The {budget.Subject} archive is empty.");
                    foreach (var entry in files)
                    {
                        string raw = entry.FullName.Replace('\\', '/');
                        SafePaths.ContainedPath(staging, raw);
                        if (raw.StartsWith("/", StringComparison.Ordinal) || raw.Contains(":") || raw.Split('/').Any(segment => segment == ".." || segment == "."))
                            throw new InvalidDataException($"The {budget.Subject} archive contains an unsafe path.");
                    }
                    string rootPrefix = SingleRootPrefix(files);

                    foreach (var entry in files)
                    {
                        string relative = Normalize(entry.FullName);
                        if (string.IsNullOrWhiteSpace(relative)) continue;
                        if (!string.IsNullOrEmpty(rootPrefix) && relative.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                            relative = Normalize(relative.Substring(rootPrefix.Length));
                        if (string.IsNullOrWhiteSpace(relative)) continue;

                        string output = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!SafePaths.IsSameOrChild(output, staging))
                            throw new InvalidDataException($"ZIP entry resolves outside the {budget.Subject} folder: {entry.FullName}");
                        string folder = Path.GetDirectoryName(output);
                        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

                        // Sizes are counted as the entry decompresses: the sizes an archive declares are never trusted.
                        using (var source = entry.Open())
                        using (var file = File.Create(output))
                        {
                            byte[] bytes = budget.Copy(source, file, relative, wanted.Contains(relative));
                            if (bytes != null) captured[relative] = bytes;
                        }
                    }
                }

                validate?.Invoke(staging);
                SafePaths.ReplaceDirectory(staging, finalPath);
                return captured;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string normalized = path.Replace('\\', '/').TrimStart('/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            return normalized;
        }

        private static bool IsDirectory(ZipArchiveEntry entry) =>
            entry == null || string.IsNullOrEmpty(entry.Name) ||
            entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal);

        // "Name/" when every file lives under the same top folder, which is then stripped.
        private static string SingleRootPrefix(IEnumerable<ZipArchiveEntry> entries)
        {
            string root = null;
            foreach (var entry in entries)
            {
                string path = Normalize(entry.FullName);
                int slash = path.IndexOf('/');
                if (slash <= 0) return null;
                string candidate = path.Substring(0, slash);
                if (string.IsNullOrWhiteSpace(root)) root = candidate;
                else if (!string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return string.IsNullOrWhiteSpace(root) ? null : root + "/";
        }
    }
}
