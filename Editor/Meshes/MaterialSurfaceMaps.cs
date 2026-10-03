using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Surface-map semantics shared by texture drops and imported clothing material repair.</summary>
    public static class MaterialSurfaceMaps
    {
        public static bool Surface(string role) => role == "roughness" || role == "smoothness";
        public static bool PackedSlot(string property) => property == "_MetallicGlossMap" || property == "_MochieMetallicMaps";
        public static bool CanAssign(string role, string property) => Surface(role) &&
            (PackedSlot(property) || property == "_GlossMap" || property == "_SmoothnessMap" || property == "_RoughnessMap" || property == "_SmoothnessMask");
        public static bool CanCombine(string property, string[] roles) => PackedSlot(property) && roles.Length == 2 &&
            roles.Count(r => r == "metallic") == 1 && roles.Count(Surface) == 1;

        private static bool HasMap(Material m, string p) => m.HasProperty(p) && m.GetTexture(p) != null;
        private static float Value(Material m, string p, float fallback = 0) => m.HasProperty(p) ? m.GetFloat(p) : fallback;
        private static void Set(Material m, string p, float value) { if (m.HasProperty(p)) m.SetFloat(p, value); }

        public static bool NeedsRoughDefault(Material m)
        {
            if (!m) return false;
            bool albedoAlpha = Value(m, "_SmoothnessTextureChannel") == 1 && (HasMap(m, "_MainTex") || HasMap(m, "_BaseMap"));
            if (!HasMap(m, "_MetallicGlossMap") && !HasMap(m, "_SpecGlossMap") && !HasMap(m, "_MaskMap") && !HasMap(m, "_SmoothnessMap") && !HasMap(m, "_SmoothnessMask") && !albedoAlpha &&
                (Value(m, "_Glossiness") != 0 || Value(m, "_Smoothness") != 0 || Value(m, "_GlossMapScale") != 0)) return true;
            if (!HasMap(m, "_GlossMap") && Value(m, "_GlossStrength") != 0) return true;
            if (!HasMap(m, "_MochieMetallicMaps") && Value(m, "_MochieRoughnessMultiplier") != 0) return true;
            if (!HasMap(m, "_RoughnessMap") && m.HasProperty("_Roughness") && Value(m, "_Roughness") != 1) return true;
            return false;
        }

        // Mutate only a caller-owned copy. Locked shaders bake scalar values into code; changing a float alone has no effect.
        public static void MakeEditable(Material copy)
        {
            if (!copy.shader || !copy.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal)) return;
            var name = copy.GetTag("OriginalShader", false);
            var shader = Shader.Find(name);
            string guid = copy.GetTag("OriginalShaderGUID", false);
            if (!shader && !string.IsNullOrEmpty(guid)) shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
            if (!shader && name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0)
                shader = Shader.Find(".poiyomi/Poiyomi Toon");
            if (!shader) throw new InvalidOperationException("Install the original shader to edit " + copy.name + ": " + name);
            int queue = copy.renderQueue;
            string renderType = copy.GetTag("RenderType", false);
            string keywords = copy.GetTag("OriginalKeywords", false, string.Join(" ", copy.shaderKeywords));
            copy.shader = shader;
            copy.renderQueue = queue; copy.SetOverrideTag("RenderType", renderType);
            copy.shaderKeywords = keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string property in copy.GetTexturePropertyNames())
            {
                string textureGuid = copy.GetTag("_stripped_tex_" + property, false);
                if (string.IsNullOrEmpty(textureGuid)) continue;
                copy.SetTexture(property, AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(textureGuid)));
                copy.SetOverrideTag("_stripped_tex_" + property, "");
            }
            copy.SetOverrideTag("OriginalShader", ""); copy.SetOverrideTag("OriginalShaderGUID", "");
            Set(copy, "_ShaderOptimizerEnabled", 0);
            // Locked variants remove feature keywords after baking them. Rebuild those toggles on
            // the editable copy, or authored decals/emission disappear even though their maps survived.
            MaterialEditor.ApplyMaterialPropertyDrawers(copy);
        }

        public static void DefaultRough(Material m)
        {
            if (!m) return;
            bool albedoAlpha = Value(m, "_SmoothnessTextureChannel") == 1 && (HasMap(m, "_MainTex") || HasMap(m, "_BaseMap"));
            if (!HasMap(m, "_MetallicGlossMap") && !HasMap(m, "_SpecGlossMap") && !HasMap(m, "_MaskMap") && !HasMap(m, "_SmoothnessMap") && !HasMap(m, "_SmoothnessMask") && !albedoAlpha)
            { Set(m, "_Glossiness", 0); Set(m, "_Smoothness", 0); Set(m, "_GlossMapScale", 0); }
            if (!HasMap(m, "_GlossMap")) Set(m, "_GlossStrength", 0);
            if (!HasMap(m, "_MochieMetallicMaps")) Set(m, "_MochieRoughnessMultiplier", 0);
            if (!HasMap(m, "_RoughnessMap")) Set(m, "_Roughness", 1);
        }

        /// <summary>Assign grayscale data in the shader's expected channel. Packed maps preserve unrelated channels.</summary>
        public static void Assign(Material m, string property, Texture2D texture, string role, string fileName, string folder)
        {
            // Bundled images can already be assigned by the creator. Their shader channels,
            // inversion and strengths are authoritative; a filename is only a hint for a NEW map.
            // In particular, Poiyomi's MetallicSmoothnessMaps need not use Standard's alpha layout.
            if ((Surface(role) || role == "metallic") && texture && m.HasProperty(property) && m.GetTexture(property) == texture) return;
            MakeEditable(m);
            bool surface = Surface(role), packed = PackedSlot(property);
            string layout = PackedLayout(fileName);
            if (role == "metallic" && layout != null && layout != "ms" && (packed || property == "_MetallicMap"))
            {
                var input = Read(texture, texture.width, texture.height);
                var output = new Color[input.Length]; var occlusion = new Color[input.Length];
                for (int i = 0; i < input.Length; i++)
                {
                    var p = input[i];
                    float metal = layout == "orm" || layout == "arm" ? p.b : layout == "rma" ? p.g : p.r;
                    float smooth = 1 - (layout == "rma" ? p.r : p.g);
                    float ao = layout == "orm" || layout == "arm" ? p.r : p.b;
                    output[i] = property == "_MochieMetallicMaps" ? new Color(metal, smooth, 1, 1) : new Color(metal, 0, 0, smooth);
                    occlusion[i] = new Color(ao, ao, ao, 1);
                }
                var map = Save(output, texture, folder); m.SetTexture(property, map);
                if (property == "_MetallicMap")
                {
                    m.SetTexture("_GlossMap", map); Set(m, "_MetallicMapChannel", 0); Set(m, "_GlossMapChannel", 3);
                    Set(m, "_MetallicStrength", 1); Set(m, "_GlossStrength", 1);
                }
                else EnableSurface(m, property);
                if (property == "_MochieMetallicMaps") { Set(m, "_MochieMetallicMultiplier", 1); Set(m, "_MochieMetallicMapsMetallicChannel", 0); }
                if (m.HasProperty("_OcclusionMap") && !HasMap(m, "_OcclusionMap"))
                { m.SetTexture("_OcclusionMap", Save(occlusion, texture, folder)); Set(m, "_OcclusionMapChannel", 0); Set(m, "_OcclusionStrength", 1); }
                return;
            }
            bool metallicOnly = role == "metallic" && packed && !IsPackedFile(fileName);
            if (role == "metallic" && IsPackedFile(fileName) && property == "_MetallicMap" && m.HasProperty("_GlossMap"))
            {
                m.SetTexture(property, texture); m.SetTexture("_GlossMap", texture);
                Set(m, "_MetallicMapChannel", 0); Set(m, "_GlossMapChannel", 3);
                Set(m, "_MetallicStrength", 1); Set(m, "_GlossStrength", 1);
                return;
            }
            if (role == "metallic" && IsPackedFile(fileName) && property == "_MochieMetallicMaps")
            {
                var data = Read(texture, texture.width, texture.height);
                for (int i = 0; i < data.Length; i++) data[i] = new Color(data[i].r, data[i].a, 1, 1);
                m.SetTexture(property, Save(data, texture, folder)); EnableSurface(m, property);
                Set(m, "_MochieMetallicMultiplier", 1); Set(m, "_MochieMetallicMapsMetallicChannel", 0);
                return;
            }
            if (!surface && !metallicOnly)
            {
                m.SetTexture(property, texture);
                if (property == "_MetallicMap" && role == "metallic") Set(m, "_MetallicStrength", 1);
                if (packed && IsPackedFile(fileName)) EnableSurface(m, property);
                return;
            }
            bool roughTarget = !packed && property.IndexOf("rough", StringComparison.OrdinalIgnoreCase) >= 0;
            bool invert = surface && (role == "roughness") != roughTarget;
            int channel = property == "_MochieMetallicMaps" ? 1 : 3;
            Texture2D result = texture;
            if (packed || invert)
            {
                var pixels = Read(texture, texture.width, texture.height);
                var existing = packed && m.GetTexture(property) != null ? Read(m.GetTexture(property), texture.width, texture.height) : null;
                float metallic = Value(m, property == "_MochieMetallicMaps" ? "_MochieMetallicMultiplier" : "_Metallic");
                var channelProperties = new[] { "_MochieMetallicMapsMetallicChannel", "_MochieMetallicMapsRoughnessChannel", "_MochieMetallicMapsReflectionMaskChannel", "_MochieMetallicMapsSpecularMaskChannel" };
                var channels = channelProperties.Select((p, index) => Mathf.Clamp((int)Value(m, p, index), 0, 3)).ToArray();
                for (int i = 0; i < pixels.Length; i++)
                {
                    float value = invert ? 1 - pixels[i].r : pixels[i].r;
                    if (!packed) { pixels[i] = new Color(value, value, value, 1); continue; }
                    Color output = existing != null ? existing[i] : property == "_MochieMetallicMaps" ? new Color(metallic, 0, 1, 1) : new Color(metallic, 0, 0, 0);
                    if (existing != null && property == "_MochieMetallicMaps")
                    { var old = existing[i]; output = new Color(old[channels[0]], old[channels[1]], old[channels[2]], old[channels[3]]); }
                    if (metallicOnly) output.r = value;
                    else output[channel] = value;
                    pixels[i] = output;
                }
                result = Save(pixels, texture, folder);
                if (property == "_MochieMetallicMaps")
                    for (int i = 0; i < channelProperties.Length; i++) Set(m, channelProperties[i], i);
                if (property == "_MochieMetallicMaps" && (existing == null || metallicOnly))
                { Set(m, "_MochieMetallicMultiplier", 1); Set(m, "_MochieMetallicMapsMetallicChannel", 0); }
            }
            m.SetTexture(property, result);
            if (surface) EnableSurface(m, property);
            if (property == "_GlossMap") Set(m, "_GlossMapChannel", 0); // Grayscale exports store data in RGB, not opaque alpha.
            if (metallicOnly && !HasMap(m, "_MochieMetallicMaps")) Set(m, "_Metallic", 1);
        }

        public static bool IsPackedFile(string name)
            => PackedLayout(name) != null;

        private static string PackedLayout(string name)
        {
            string compact = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (compact.Contains("metallicsmoothness") || compact.Contains("metallicgloss") || compact.Contains("metalnesssmoothness")) return "ms";
            var tokens = Regex.Split(Path.GetFileNameWithoutExtension(name ?? "").ToLowerInvariant(), "[^a-z0-9]+");
            return tokens.LastOrDefault(t => t == "orm" || t == "arm" || t == "rma" || t == "mra");
        }

        private static void EnableSurface(Material m, string property)
        {
            if (property == "_MetallicGlossMap") { Set(m, "_GlossMapScale", 1); Set(m, "_Smoothness", 1); Set(m, "_SmoothnessTextureChannel", 0); m.EnableKeyword("_METALLICGLOSSMAP"); m.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"); }
            else if (property == "_MochieMetallicMaps") { Set(m, "_MochieRoughnessMultiplier", 1); Set(m, "_MochieRoughnessMapInvert", 0); Set(m, "_MochieMetallicMapsRoughnessChannel", 1); Set(m, "_MochieBRDF", 1); m.EnableKeyword("MOCHIE_PBR"); }
            else if (property == "_GlossMap") Set(m, "_GlossStrength", 1);
            else if (property == "_RoughnessMap") Set(m, "_Roughness", 1);
            else Set(m, "_Smoothness", 1);
        }

        private static Color[] Read(Texture texture, int width, int height)
        {
            var previous = RenderTexture.active;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            bool srgb = importer != null ? importer.sRGBTexture : texture.isDataSRGB;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try { Graphics.Blit(texture, rt); RenderTexture.active = rt; read.ReadPixels(new Rect(0, 0, width, height), 0, 0); return read.GetPixels(); }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.DestroyImmediate(read); }
        }

        private static Texture2D Save(Color[] pixels, Texture2D source, string folder)
        {
            Directory.CreateDirectory(folder);
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            string path = folder + "/Surface-" + Guid.NewGuid().ToString("N") + ".png";
            try { texture.SetPixels(pixels); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false; importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = source.wrapMode; importer.filterMode = source.filterMode;
            importer.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(source.width, source.height)), 32, 16384);
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
