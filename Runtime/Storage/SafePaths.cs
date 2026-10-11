#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;

namespace Orbiters.Toolkit.Storage
{
    /// <summary>
    /// Containment for folders filled from downloads (MCB versions, gallery packages): every relative path must stay in its
    /// folder, use plain names, never traverse a link, and a folder is replaced in one rename that keeps the previous one
    /// until the new one is in place.
    /// </summary>
    public static class SafePaths
    {
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// The longest file path Unity can use on Windows (MAX_PATH less its terminating null): Unity is not long-path aware,
        /// whatever the system's setting.
        /// </summary>
        public const int MaxPathLength = 259;

        /// <summary>
        /// A sibling of <paramref name="path"/> named "&lt;name&gt;.&lt;kind&gt;-&lt;8 hex&gt;", to stage or set a folder aside. Its files
        /// keep their names in it, so the suffix stays short: they must still fit <see cref="MaxPathLength"/>.
        /// </summary>
        public static string Sibling(string path, string kind) =>
            Path.GetFullPath(path).TrimEnd('/', '\\') + "." + kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Refuses a path too long for Windows, saying how much shorter the project's folder must be.</summary>
        public static void RequireLength(string fullPath)
        {
            if (Path.DirectorySeparatorChar != '\\' || fullPath == null || fullPath.Length <= MaxPathLength) return;
            string project = Directory.GetCurrentDirectory().TrimEnd('/', '\\');
            string shorter = Path.Combine(Path.GetPathRoot(project) ?? "", "Unity", Path.GetFileName(project));
            throw new PathTooLongException(
                $"The project's folder path is too long for Windows: a file would need {fullPath.Length} characters, and Unity can use " +
                $"at most {MaxPathLength}. Move the project to a folder path at least {fullPath.Length - MaxPathLength} characters " +
                $"shorter (for example {shorter}) and try again.");
        }

        /// <summary>A plain file or folder name: no separators, reserved characters or trailing dot.</summary>
        public static void ValidateLabel(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.EndsWith(".", StringComparison.Ordinal) ||
                value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0))
                throw new ArgumentException("Version labels must be plain names without path separators or reserved filename characters.", parameter);
        }

        /// <summary>The full path of <paramref name="relative"/> inside <paramref name="root"/>, or an exception.</summary>
        public static string ContainedPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException("A relative path inside the version folder is required.");
            foreach (string segment in relative.Replace('\\', '/').Split('/'))
            {
                try { ValidateLabel(segment, nameof(relative)); }
                catch (ArgumentException ex) { throw new InvalidDataException("The version path contains an invalid segment.", ex); }
                if (IsDeviceName(segment)) throw new InvalidDataException("Version paths cannot use reserved device names.");
            }
            string fullRoot = Path.GetFullPath(root).TrimEnd('/', '\\');
            string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('\\', '/')));
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("The version path resolves outside its storage folder.");
            RejectLinks(fullRoot, full);
            return full;
        }

        public static bool IsDeviceName(string segment)
        {
            string device = (segment ?? "").Split('.')[0].ToUpperInvariant();
            return device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                   (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9');
        }

        /// <summary>Refuses a path that goes through a symbolic link or junction between it and <paramref name="root"/>.</summary>
        public static void RejectLinks(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('/', '\\');
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Version storage cannot traverse symbolic links or junctions.");
                if (string.Equals(current.TrimEnd('/', '\\'), fullRoot, PathComparison)) return;
            }
            throw new InvalidDataException("The version path resolves outside its storage folder.");
        }

        public static bool IsSameOrChild(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, root, PathComparison) ||
                   candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ||
                   candidate.StartsWith(root + Path.AltDirectorySeparatorChar, PathComparison);
        }

        /// <summary>Stage must be a sibling on the same volume. Keep the previous directory until the rename succeeds.</summary>
        public static void ReplaceDirectory(string staging, string destination)
        {
            string staged = Path.GetFullPath(staging), final = Path.GetFullPath(destination);
            string parent = Path.GetDirectoryName(final);
            if (!string.Equals(parent, Path.GetDirectoryName(staged), PathComparison) ||
                string.Equals(staged, final, PathComparison))
                throw new InvalidDataException("Replacement requires a distinct sibling staging folder.");
            RejectLinks(parent, staged);
            RejectLinks(parent, final);
            string previous = Sibling(final, "trash");
            bool moved = false;
            try
            {
                if (Directory.Exists(final)) { Directory.Move(final, previous); moved = true; }
                Directory.Move(staged, final);
            }
            catch
            {
                if (moved && !Directory.Exists(final)) Directory.Move(previous, final);
                throw;
            }
            // Cleanup failure must not turn a successfully committed replacement into a failed download.
            if (moved)
            {
                try { Directory.Delete(previous, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
#endif
