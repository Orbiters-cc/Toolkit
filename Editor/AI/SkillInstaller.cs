using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    public static class SkillInstaller
    {
        private const string SkillName = "orbiters-toolkit";
        private const string Owner = "orbiters.toolkit";

        [Serializable]
        private sealed class Receipt { public string owner; public string sha256; }

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string BundledSkill
        {
            get
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SkillInstaller).Assembly);
                if (package == null) throw new InvalidOperationException("Cannot locate the Orbiters Toolkit package.");
                return File.ReadAllText(Path.Combine(package.resolvedPath, "Editor", "AI", "Skills", SkillName, "SKILL.md"));
            }
        }

        public static string RelativePath(bool codex) =>
            (codex ? ".agents" : ".claude") + "/skills/" + SkillName + "/SKILL.md";

        public static string Status(string projectRoot, bool codex, string content)
        {
            string path = Destination(projectRoot, codex);
            if (!File.Exists(path)) return "Not installed";
            if (File.ReadAllText(path) == content) return "Installed";
            return OwnedAndUnedited(path) ? "Update available" : "Local changes — preserved";
        }

        public static string Install(string projectRoot, bool codex, bool claude, string content)
        {
            if (!codex && !claude) throw new ArgumentException("Choose at least one assistant.");
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("The bundled skill is empty.");
            var paths = new[] { codex ? Destination(projectRoot, true) : null,
                claude ? Destination(projectRoot, false) : null }.Where(p => p != null).ToArray();
            // Check every destination before writing either client, preserving user-owned instructions.
            foreach (string path in paths)
                if (File.Exists(path) && File.ReadAllText(path) != content && !OwnedAndUnedited(path))
                    throw new IOException("Existing instructions were preserved at " + path +
                        ". Move your edited skill aside before installing the bundled version.");
            foreach (string path in paths)
            {
                if (File.Exists(path) && File.ReadAllText(path) == content) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteAtomically(path, content);
                WriteAtomically(ReceiptPath(path), JsonUtility.ToJson(new Receipt { owner = Owner, sha256 = Hash(content) }, true));
            }
            return "AI integration installed for " + (codex && claude ? "Codex and Claude Code" : codex ? "Codex" : "Claude Code") +
                ". Start a new chat or reload skills if it is not listed yet.";
        }

        private static string Destination(string projectRoot, bool codex)
        {
            string root = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Project directory does not exist.");
            string path = Path.GetFullPath(Path.Combine(root, RelativePath(codex)));
            // Do not follow a symlink/junction into another workspace or a personal skill directory.
            for (string current = path; current != root; current = Path.GetDirectoryName(current))
            {
                if (string.IsNullOrEmpty(current)) throw new IOException("Skill destination escaped the project directory.");
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Skill installation does not follow linked paths: " + current);
            }
            string receipt = ReceiptPath(path);
            if (File.Exists(receipt) && (File.GetAttributes(receipt) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The skill installation receipt is a linked file.");
            return path;
        }

        private static bool OwnedAndUnedited(string path)
        {
            try
            {
                string receiptPath = ReceiptPath(path);
                if (!File.Exists(receiptPath)) return false;
                var receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(receiptPath));
                return receipt != null && receipt.owner == Owner && receipt.sha256 == Hash(File.ReadAllText(path));
            }
            catch (ArgumentException) { return false; }
        }

        private static string ReceiptPath(string path) => Path.Combine(Path.GetDirectoryName(path), ".orbiters-toolkit-install.json");

        private static string Hash(string content)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteAtomically(string path, string content)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
