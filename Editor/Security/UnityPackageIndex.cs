using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What a .unitypackage (a gzipped tar of &lt;guid&gt;/pathname, &lt;guid&gt;/asset and &lt;guid&gt;/asset.meta entries)
    /// would import, read without importing it through <see cref="UnityPackageReader"/>: sizes declared by the archive are
    /// checked before anything is allocated and the expanded size is bounded, so a small crafted file cannot exhaust memory
    /// or keep Unity busy decompressing.
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
        /// <summary>The pathnames of every entry together: a package naming 200,000 files of 4 KB each is not an avatar package.</summary>
        public const long DefaultMaxPathnameTotalBytes = 64L * 1024 * 1024;
        public readonly List<Entry> Entries = new List<Entry>();

        public IEnumerable<string> Paths => Entries.Select(e => e.Path);
        public List<string> CodeFiles => CodeContent.Filter(Paths);
        /// <summary>Unity may resolve an imported GUID to an existing asset; include code at that destination too.</summary>
        public List<string> CodeFilesIncludingExisting(Func<string, string> resolveGuid) => CodeContent.Filter(Paths.Concat(
            resolveGuid == null ? Enumerable.Empty<string>() : Entries.Select(e => resolveGuid(e.Guid)).Where(p => !string.IsNullOrEmpty(p))));
        /// <summary>Entries whose path leaves the project (absolute, "..", or outside Assets/ and Packages/).</summary>
        public List<string> UnsafePaths => Entries.Where(e => !IsProjectPath(e.Path)).Select(e => e.Path).ToList();

        /// <summary>The archive's size once decompressed, headers and padding included.</summary>
        public long ExpandedBytes { get; private set; }

        /// <param name="keepContent">Which asset paths to keep the bytes of (at most <paramref name="maxContentBytes"/> each).</param>
        /// <param name="maxPathnameTotalBytes">The pathnames of all entries together, as the archive stores them.</param>
        /// <param name="firstLinePathname">Read only the first line of each pathname (<see cref="UnityPackageReader.Options.FirstLinePathname"/>).</param>
        public static UnityPackageIndex Read(string path, Func<string, bool> keepContent = null, int maxContentBytes = 64 * 1024,
            long maxExpandedBytes = UnityPackageReader.DefaultMaxExpandedBytes, int maxEntries = UnityPackageReader.DefaultMaxEntries, long maxPathnameTotalBytes = DefaultMaxPathnameTotalBytes,
            bool firstLinePathname = false)
        {
            if (maxContentBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long kept = 0;
            // Small assets are kept until their pathname says whether they are wanted: bounded in total too.
            const long MaxKeptBytes = 16 * 1024 * 1024;
            var summary = UnityPackageReader.Read(path, new UnityPackageReader.Options
            {
                MaxExpandedBytes = maxExpandedBytes, MaxEntries = maxEntries, MaxPathnameTotalBytes = maxPathnameTotalBytes, FirstLinePathname = firstLinePathname,
            }, record =>
            {
                if (record.Part != UnityPackageReader.Part.Asset || keepContent == null || record.Size > maxContentBytes || kept + record.Size > MaxKeptBytes) return;
                kept += record.Size;
                contents[record.Guid] = record.ReadAll();
            });
            var index = new UnityPackageIndex { ExpandedBytes = summary.ExpandedBytes };
            foreach (var pair in summary.Pathnames)
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
    }
}
