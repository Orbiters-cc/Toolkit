using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Files that make Unity run or compile code once imported: scripts, assemblies and their definitions, compiler options
    /// and native plugins. Everything else in an avatar or accessory package is data.
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
            if (clean.Equals("Packages/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("Packages/packages-lock.json", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) && clean.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase)) return true;
            // macOS plugin bundles are folders ("Plugin.bundle/Contents/...").
            if (clean.Split('/').Any(part => part.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))) return true;
            return Extensions.Contains(Path.GetExtension(clean));
        }

        public static List<string> Filter(IEnumerable<string> paths) =>
            (paths ?? Enumerable.Empty<string>()).Where(IsCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
