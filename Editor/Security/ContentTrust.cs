using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Orbiters.Toolkit.Editor.Storage;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Downloaded content lands under Assets/, where Unity compiles and runs any script as soon as it is imported. Code from
    /// a creator Orbiters has not marked trusted needs the user's consent first; the signed-in creator's own uploads do not.
    /// Shared by MCB versions and My Avatar's gallery: the decision is the same, each tool asks in its own UI.
    /// </summary>
    public static class ContentTrust
    {
        public const string UntrustedCodeMessage =
            "Orbiters does not control the content of the files and scripts of this asset or its author. Unity compiles and runs code as soon as it is imported.";

        /// <summary>The signed-in Orbiters user's id, 0 when signed out.</summary>
        public static int SignedInUserId()
        {
            var auth = AuthenticationService.GetAuth();
            return auth != null && int.TryParse(auth.user, out int id) ? id : 0;
        }

        /// <summary>Trusted creators (as the server says now) and the signed-in user's own uploads install without asking.</summary>
        public static bool IsTrusted(bool? creatorTrusted, int? creatorId, int signedInUserId) =>
            creatorTrusted == true || (signedInUserId > 0 && creatorId.HasValue && creatorId.Value == signedInUserId);

        /// <summary>
        /// Code files a ZIP would put in the project: its own entries, and those of the Unity packages it carries (listed as
        /// "package.unitypackage/Assets/..."). <paramref name="packageEntries"/> picks which entries of a nested package are
        /// imported (all of them when null). Everything is read within one <see cref="ArchiveBudget"/>.
        /// </summary>
        public static List<string> ListArchiveCode(Stream zipStream, ArchiveBudget budget = null,
            Func<string, UnityPackageIndex, IEnumerable<UnityPackageIndex.Entry>> packageEntries = null)
        {
            budget = budget ?? new ArchiveBudget();
            var code = new List<string>();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
            {
                budget.AddEntries(archive.Entries.Count);
                foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                {
                    string path = entry.FullName.Replace('\\', '/');
                    if (CodeContent.IsCode(path)) code.Add(path);
                    if (!path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) continue;

                    string temp = Path.Combine(Path.GetTempPath(), "orbiters-code-check-" + Guid.NewGuid().ToString("N") + ".unitypackage");
                    try
                    {
                        using (var source = entry.Open())
                        using (var file = File.Create(temp))
                            budget.Copy(source, file, path);
                        var index = ReadNested(temp, path, budget);
                        code.AddRange((packageEntries != null ? packageEntries(temp, index) : index.Entries)
                            .Where(e => CodeContent.IsCode(e.Path)).Select(e => path + "/" + e.Path));
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
            }
            return code.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Code files a Unity package would import, including existing project files its GUIDs resolve to.</summary>
        public static List<string> PackageCode(UnityPackageIndex index, Func<string, string> resolveGuid) =>
            index?.CodeFilesIncludingExisting(resolveGuid) ?? new List<string>();

        // A nested package's expansion is charged to the archive's budget before it is indexed within the rest of it.
        private static UnityPackageIndex ReadNested(string packagePath, string name, ArchiveBudget budget)
        {
            long before = budget.TotalBytes;
            using (var gzip = new GZipStream(File.OpenRead(packagePath), CompressionMode.Decompress))
                budget.Copy(gzip, Stream.Null, name);
            var index = UnityPackageIndex.Read(packagePath, maxExpandedBytes: Math.Max(1024L, budget.TotalBytes - before),
                maxEntries: Math.Max(1, budget.RemainingEntries));
            budget.AddEntries(index.Entries.Count);
            return index;
        }

        /// <summary>
        /// True when the content may be installed: no code, a trusted creator, or the user chose to continue in the shared
        /// <see cref="UntrustedCodeDialog"/>. Tools with their own in-window confirmation use <see cref="IsTrusted"/> instead.
        /// </summary>
        public static bool ConfirmCode(string subject, string author, IReadOnlyList<string> code, bool trusted, string confirmLabel = "Install anyway")
        {
            if (code == null || code.Count == 0 || trusted) return true;
            return UntrustedCodeDialog.Confirm(new UntrustedCodeDialog.Request
            {
                Subject = subject, Author = author, Message = UntrustedCodeMessage, Files = code, ConfirmLabel = confirmLabel,
            });
        }
    }
}
