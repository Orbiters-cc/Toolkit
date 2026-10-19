using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// Saves meshes as native <c>.asset</c> files: the only kind of import that costs milliseconds, since no model
    /// importer runs. A tool that edits meshes commits here instead of rewriting an FBX, which would re-import for
    /// tens of seconds on a body with many blendshapes. A new mesh never takes the place of another asset that has
    /// its name (two avatars both have a "Body"); a mesh made again from one of the tool's own assets replaces that
    /// asset in place, so every renderer and prefab that points at it keeps pointing at it.
    /// </summary>
    public static class MeshAssets
    {
        /// <summary>Where a product keeps the meshes it made: <c>Assets/Orbiters/&lt;product&gt;/Meshes/&lt;owner&gt;</c>.</summary>
        public static string Folder(string product, string owner) =>
            "Assets/Orbiters/" + Clean(product) + "/Meshes/" + Clean(owner);

        /// <summary>
        /// Writes <paramref name="mesh"/> as a new asset: <c>folder/name.asset</c>, or <c>name 1.asset</c>,
        /// <c>name 2.asset</c>… when that file exists already. Returns the asset (<paramref name="mesh"/> itself).
        /// </summary>
        public static Mesh Save(Mesh mesh, string folder, string name)
        {
            if (mesh == null) throw new System.ArgumentNullException(nameof(mesh));
            EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Clean(name) + ".asset");
            mesh.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssetIfDirty(mesh);
            return mesh;
        }

        /// <summary>
        /// Copies <paramref name="mesh"/> into the asset <paramref name="existing"/> (its file, name and GUID stay, so
        /// references to it hold) and returns <paramref name="existing"/>. <paramref name="mesh"/> is destroyed unless it
        /// is an asset itself: the caller must not keep using it.
        /// </summary>
        public static Mesh Replace(Mesh mesh, Mesh existing)
        {
            if (mesh == null) throw new System.ArgumentNullException(nameof(mesh));
            if (existing == null || !AssetDatabase.Contains(existing)) throw new System.ArgumentException("Not a mesh asset.", nameof(existing));
            if (mesh == existing) return existing;
            string name = existing.name;
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = name;
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            if (!AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            return existing;
        }

        /// <summary>Whether <paramref name="mesh"/> is an asset under a product's folder.</summary>
        public static bool IsOwnedBy(Mesh mesh, string product)
        {
            if (mesh == null || !AssetDatabase.Contains(mesh)) return false;
            string path = AssetDatabase.GetAssetPath(mesh);
            return path.StartsWith("Assets/Orbiters/" + Clean(product) + "/", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates the folder (and its parents) through the asset database without a full refresh.</summary>
        public static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent)) return;
            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, leaf);
        }

        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Mesh";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (System.Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '/' || chars[i] == '\\') chars[i] = '_';
            string cleaned = new string(chars).Trim();
            return cleaned.Length == 0 ? "Mesh" : cleaned;
        }
    }
}
