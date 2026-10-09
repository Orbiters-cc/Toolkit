using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Files that make Unity run or compile code once imported: scripts, assemblies and their definitions, compiler options,
    /// native plugins, and the project files that decide which packages Unity resolves and how it runs (the package
    /// manifests of Packages/, VPM's included, each package's package.json, ProjectSettings/). Everything else in an avatar
    /// or accessory package is data.
    /// </summary>
    public static class CodeContent
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".dll", ".asmdef", ".asmref", ".rsp", ".jslib", ".jspre", ".so", ".dylib", ".bundle", ".a", ".exe",
        };

        public static bool IsCode(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var clean = path.Replace('\\', '/').TrimEnd('/', ' ', '.');
            if (clean.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 5).TrimEnd(' ', '.');
            if (IsProjectFile(clean)) return true;
            // macOS plugin bundles are folders ("Plugin.bundle/Contents/...").
            if (clean.Split('/').Any(part => part.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))) return true;
            return Extensions.Contains(Path.GetExtension(clean));
        }

        // Packages/manifest.json, packages-lock.json, vpm-manifest.json and any other resolver file at the top of Packages/,
        // each package's package.json, and everything under ProjectSettings/.
        private static bool IsProjectFile(string clean)
        {
            var parts = clean.Split('/');
            if (parts[0].Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase)) return parts.Length > 1;
            if (!parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase)) return false;
            return parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                   parts.Length > 2 && parts[parts.Length - 1].Equals("package.json", StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> Filter(IEnumerable<string> paths) =>
            (paths ?? Enumerable.Empty<string>()).Where(IsCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
