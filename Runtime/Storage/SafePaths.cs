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

        /// <summary>The longest folder path Windows creates: MAX_PATH less room for an 8.3 file name.</summary>
        public const int MaxFolderPathLength = 247;

        /// <summary>
        /// A sibling of <paramref name="path"/> named "&lt;name&gt;.&lt;kind&gt;-&lt;8 hex&gt;", to stage or set a folder aside. Its files
        /// keep their names in it, so the suffix stays short: they must still fit <see cref="MaxPathLength"/>.
        /// </summary>
        public static string Sibling(string path, string kind) =>
            Path.GetFullPath(path).TrimEnd('/', '\\') + "." + kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// A <see cref="Sibling"/> ending in "~": Unity never imports it, so one left behind (a staging folder or a previous
        /// folder that could not be deleted) never brings a second copy of its assets' GUIDs into the project.
        /// </summary>
        public static string HiddenSibling(string path, string kind) => Sibling(path, kind) + "~";

        /// <summary>
        /// Refuses a file path too long for Windows, or in a folder too long for it, saying how much shorter the folder
        /// holding it must be: the project's, when it is in the project.
        /// </summary>
        public static void RequireLength(string fullPath)
        {
            if (Path.DirectorySeparatorChar != '\\' || string.IsNullOrEmpty(fullPath)) return;
            string folder = Path.GetDirectoryName(fullPath) ?? "";
            int over = Math.Max(fullPath.Length - MaxPathLength, folder.Length - MaxFolderPathLength);
            if (over <= 0) return;
            string need = fullPath.Length - MaxPathLength >= folder.Length - MaxFolderPathLength
                ? $"a file would need {fullPath.Length} characters, and Unity can use at most {MaxPathLength}"
                : $"a folder would need {folder.Length} characters, and Windows makes them {MaxFolderPathLength} at most";
            string project = Directory.GetCurrentDirectory().TrimEnd('/', '\\');
            if (!IsSameOrChild(fullPath, project))
                throw new PathTooLongException($"The folder path is too long for Windows: {need}. Use a folder path at least {over} characters shorter and try again.");
            // The project's own folder is the part of the path that can change; a folder at a drive's root is as short as it gets.
            string root = Path.GetPathRoot(project) ?? "";
            if (project.Length - over < root.Length + 1)
                throw new PathTooLongException($"Its files are nested too deeply for Windows: {need}, even with the project at the root of a drive.");
            string shorter = Path.Combine(root, "Unity", Path.GetFileName(project));
            string example = shorter.Length <= project.Length - over ? $" (for example {shorter})" : "";
            throw new PathTooLongException(
                $"The project's folder path is too long for Windows: {need}. Move the project to a folder path at least {over} characters " +
                $"shorter{example} and try again.");
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

        /// <summary>
        /// Whether Windows keeps this name for a device (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM and LPT with a digit or a
        /// superscript 1, 2 or 3), whatever its extension and the spaces before it ("NUL .txt").
        /// </summary>
        public static bool IsDeviceName(string segment)
        {
            string device = (segment ?? "").Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CONIN$" || device == "CONOUT$") return true;
            return device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                   "0123456789\u00b9\u00b2\u00b3".IndexOf(device[3]) >= 0;
        }

        /// <summary>
        /// <paramref name="name"/> (an object's, a menu's) as a plain file or folder name: separators, reserved and control
        /// characters become "_", no leading or trailing spaces, no trailing dot (so never "." or ".."), never a device name,
        /// at most <paramref name="maxLength"/> characters (paths must fit <see cref="MaxPathLength"/>); <paramref name="fallback"/>
        /// when nothing is left.
        /// </summary>
        public static string FileName(string name, string fallback = "Unnamed", int maxLength = 64)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string clean = new string((name ?? "").Select(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 || invalid.Contains(c) ? '_' : c).ToArray())
                .TrimStart().TrimEnd('.', ' ');
            if (clean.Length > maxLength) clean = clean.Substring(0, maxLength).TrimEnd('.', ' ');
            if (clean.Length == 0) clean = fallback;
            return IsDeviceName(clean) ? "_" + clean : clean;
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

        /// <summary>
        /// Writes a small text file (settings, lists kept for the user) through a temporary file beside it, replaced in one
        /// step: a crash or a full disk mid-write leaves the previous file whole, never a cut one. Throws what the write
        /// throws (IOException, UnauthorizedAccessException); the temporary file is removed then.
        /// </summary>
        public static void ReplaceFile(string path, string text) => ReplaceFile(path, temp => File.WriteAllText(temp, text));

        /// <summary>The same for a small binary file (a picture).</summary>
        public static void ReplaceFile(string path, byte[] bytes) => ReplaceFile(path, temp => File.WriteAllBytes(temp, bytes));

        private static void ReplaceFile(string path, Action<string> write)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                write(temp);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
            string previous = HiddenSibling(final, "trash");
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
