using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Textures a material still points at that its shader no longer has (left by an earlier shader, or a feature a locked
    /// Poiyomi shader compiled out). The shader never samples them, but they upload with the avatar and VRChat counts
    /// them in its texture memory. Run it after shaders are locked (Poiyomi locks at build step 100).
    /// </summary>
    public static class MaterialCleanup
    {
        // Each file's hash, by path, size and time written: read once.
        private static readonly Dictionary<string, (long Length, long Written, string Hash)> Hashes = new Dictionary<string, (long, long, string)>();

        /// <summary>
        /// Textures that are the same image imported the same way under two names (the same file bytes and import settings
        /// for the platform the editor builds for: a package bringing its own copy of a texture the avatar already has)
        /// upload once: the renderers below <paramref name="root"/> get copies of their materials pointing at one of them
        /// (in memory, for the build to keep). Returns how many textures no renderer shows any more.
        /// </summary>
        public static int MergeDuplicateTextures(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var uses = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
                .SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture).OfType<Texture2D>().Distinct()).ToList();
            var same = new Dictionary<Texture, Texture>();
            foreach (var group in uses.Distinct().Select(t => (Texture: t, Identity: Identity(t))).Where(p => p.Identity != null).GroupBy(p => p.Identity))
            {
                // The one most materials show already: the fewest copies.
                var kept = group.OrderByDescending(p => uses.Count(u => u == p.Texture)).ThenBy(p => AssetDatabase.GetAssetPath(p.Texture), StringComparer.Ordinal).First().Texture;
                foreach (var (texture, _) in group) if (texture != kept) same[texture] = kept;
            }
            if (same.Count == 0) return 0;
            var copies = new Dictionary<Material, Material>();
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    if (!copies.TryGetValue(material, out var copy))
                    {
                        copy = material;
                        foreach (int id in material.GetTexturePropertyNameIDs())
                        {
                            var texture = material.GetTexture(id);
                            if (texture == null || !same.TryGetValue(texture, out var kept)) continue;
                            if (copy == material) copy = new Material(material) { name = material.name, renderQueue = material.renderQueue };
                            copy.SetTexture(id, kept);
                        }
                        copies[material] = copy;
                    }
                    if (copy == material) continue;
                    materials[i] = copy;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            var shown = new HashSet<Texture>(renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
                .SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture)).Where(t => t != null));
            return same.Keys.Count(t => !shown.Contains(t));
        }

        // The file's bytes and how it imports for the platform the editor builds for; null for a texture that is no file.
        private static string Identity(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return null;
            // A package's files may live in the package cache: the full path finds them wherever they are.
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) return null;
            if (!Hashes.TryGetValue(path, out var known) || known.Length != file.Length || known.Written != file.LastWriteTimeUtc.Ticks)
            {
                using (var md5 = MD5.Create())
                using (var stream = file.OpenRead())
                    known = (file.Length, file.LastWriteTimeUtc.Ticks, Convert.ToBase64String(md5.ComputeHash(stream)));
                Hashes[path] = known;
            }
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            var target = EditorUserBuildSettings.activeBuildTarget;
            string platform = target == BuildTarget.Android ? "Android" : target == BuildTarget.iOS ? "iPhone" : "Standalone";
            var own = importer.GetPlatformTextureSettings(platform);
            return known.Hash + "|" + JsonUtility.ToJson(settings) + "|" + JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings()) + "|" + JsonUtility.ToJson(own);
        }

        /// <summary>
        /// The texture properties of <paramref name="material"/> that hold a texture its shader does not declare. The main
        /// texture stays: VRChat's fallback shader shows it to players who hide custom shaders.
        /// </summary>
        public static List<string> UnusedTextures(Material material)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null || !shader.isSupported || shader.name == "Hidden/InternalErrorShader") return new List<string>();
            return material.GetTexturePropertyNames().Where(n => n != "_MainTex" && material.GetTexture(n) != null && shader.FindPropertyIndex(n) < 0).ToList();
        }

        /// <summary>
        /// Gives the renderers below <paramref name="root"/> copies of their materials without those textures (one copy per
        /// material, in memory, for the build to keep). Materials an animation swaps in keep theirs. Returns how many
        /// texture references went.
        /// </summary>
        public static int DropUnusedTextures(GameObject root)
        {
            var copies = new Dictionary<Material, Material>();
            int dropped = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    if (!copies.TryGetValue(material, out var copy))
                    {
                        var unused = UnusedTextures(material);
                        copy = material;
                        if (unused.Count > 0)
                        {
                            copy = new Material(material) { name = material.name, renderQueue = material.renderQueue };
                            foreach (string name in unused) copy.SetTexture(name, null);
                            dropped += unused.Count;
                        }
                        copies[material] = copy;
                    }
                    if (copy == material) continue;
                    materials[i] = copy;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            return dropped;
        }
    }
}
