using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Recover usable texture maps from broken imports without modifying the source material.</summary>
    public static class MaterialRepair
    {
        public const string ToonShader = "VRChat/Mobile/Toon Standard";

        public static string Reason(Material material)
        {
            if (!material) return null;
            var shader = material.shader;
            if (!shader || shader.name == "Hidden/InternalErrorShader" || !shader.isSupported)
                return "Its shader is missing or unsupported.";
            // Some FBX exports become solid white emissive Standard materials. Limit this diagnosis
            // to embedded model imports: an authored .mat may deliberately use an untextured glow.
            if (shader.name == "Standard" && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(material)) is ModelImporter &&
                material.IsKeywordEnabled("_EMISSION") && !material.GetTexture("_EmissionMap") &&
                White(material.GetColor("_EmissionColor")))
                return "Its imported white emission hides the color texture.";
            return null;
        }

        private static bool White(Color color) => Mathf.Min(color.r, Mathf.Min(color.g, color.b)) >= .99f;

        /// <summary>Recover an untextured FBX placeholder only when its package supplies one exact named material.</summary>
        public static Material AuthoredReplacement(Material material)
        {
            if (!material || !material.shader || material.shader.name != "Standard" || material.mainTexture ||
                material.GetTexture("_BumpMap") || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(material)) is ModelImporter)) return null;
            var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(material))?.Replace('\\', '/');
            // The model folder, then its package folder. Never search the whole project for a generic name.
            for (int level = 0; level < 2 && !string.IsNullOrEmpty(folder) && folder != "Assets"; level++, folder = Path.GetDirectoryName(folder)?.Replace('\\', '/'))
            {
                var candidates = AssetDatabase.FindAssets("t:Material", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).Select(AssetDatabase.LoadAssetAtPath<Material>)
                    .Where(m => m && string.Equals(m.name, material.name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length > 1) return null;
                if (candidates.Length == 1) return candidates[0];
            }
            return null;
        }

        public static Material CreateToon(Material source, string surfaceFolder = "Assets/Orbiters/MaterialEdits/SurfaceMaps")
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            var shader = Shader.Find(ToonShader);
            if (!shader || !shader.isSupported)
                throw new InvalidOperationException("VRChat Toon Standard is unavailable. Install or repair the VRChat SDK first.");
            var copy = new Material(shader) { name = source.name };
            // Saved properties survive a missing shader, unlike shader-reflected texture slots.
            using (var serialized = new SerializedObject(source))
            {
                var saved = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                for (int i = 0; saved != null && i < saved.arraySize; i++)
                {
                    var pair = saved.GetArrayElementAtIndex(i);
                    string property = pair.FindPropertyRelative("first").stringValue;
                    var value = pair.FindPropertyRelative("second");
                    var texture = value.FindPropertyRelative("m_Texture").objectReferenceValue as Texture;
                    if (!texture) continue;
                    if ((property == "_RoughnessMap" || property == "_SmoothnessMap") && texture is Texture2D data)
                    {
                        MaterialSurfaceMaps.Assign(copy, "_GlossMap", data, property == "_RoughnessMap" ? "roughness" : "smoothness", data.name, surfaceFolder);
                        copy.SetTextureScale("_GlossMap", value.FindPropertyRelative("m_Scale").vector2Value);
                        copy.SetTextureOffset("_GlossMap", value.FindPropertyRelative("m_Offset").vector2Value);
                        continue;
                    }
                    string target = property == "_BaseMap" ? "_MainTex" : property == "_MetallicGlossMap" ? "_MetallicMap" : property;
                    if (!copy.HasProperty(target)) continue;
                    copy.SetTexture(target, texture);
                    copy.SetTextureScale(target, value.FindPropertyRelative("m_Scale").vector2Value);
                    copy.SetTextureOffset(target, value.FindPropertyRelative("m_Offset").vector2Value);
                    if (property == "_MetallicGlossMap")
                    {
                        copy.SetTexture("_GlossMap", texture);
                        copy.SetTextureScale("_GlossMap", copy.GetTextureScale(target));
                        copy.SetTextureOffset("_GlossMap", copy.GetTextureOffset(target));
                    }
                }
            }
            bool brokenImport = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) is ModelImporter && Reason(source) != null;
            if (source.HasProperty("_SmoothnessTextureChannel") && source.GetFloat("_SmoothnessTextureChannel") == 1 && copy.GetTexture("_MainTex"))
            {
                copy.SetTexture("_GlossMap", copy.GetTexture("_MainTex"));
                copy.SetTextureScale("_GlossMap", copy.GetTextureScale("_MainTex"));
                copy.SetTextureOffset("_GlossMap", copy.GetTextureOffset("_MainTex"));
                copy.SetFloat("_GlossMapChannel", 3);
            }
            copy.SetColor("_Color", !brokenImport && source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
            copy.SetColor("_EmissionColor", !brokenImport && source.HasProperty("_EmissionColor") ? source.GetColor("_EmissionColor") : Color.black);
            foreach (string property in new[] { "_BumpScale", "_OcclusionStrength", "_DetailNormalMapScale", "_GlossMapChannel", "_MetallicMapChannel", "_GlossStrength" })
                if (source.HasProperty(property)) copy.SetFloat(property, source.GetFloat(property));
            if (copy.GetTexture("_MetallicMap")) copy.SetFloat("_MetallicStrength", 1);
            else if (source.HasProperty("_Metallic")) copy.SetFloat("_MetallicStrength", source.GetFloat("_Metallic"));
            string gloss = copy.GetTexture("_GlossMap") ? "_GlossMapScale" : "_Glossiness";
            if (source.HasProperty(gloss)) copy.SetFloat("_GlossStrength", source.GetFloat(gloss));
            if (source.shader && source.shader.name == "Standard")
            {
                copy.SetFloat("_DetailMode", 3); // Standard multiplies detail albedo by two.
                copy.SetFloat("_DetailUV", source.GetFloat("_UVSec"));
            }
            ConfigureTextureFeatures(copy);
            MaterialSurfaceMaps.DefaultRough(copy);
            return copy;
        }

        public static void ConfigureTextureFeatures(Material material)
        {
            if (!material || !material.shader || material.shader.name != ToonShader) return;
            Keyword(material, "USE_NORMAL_MAPS", material.GetTexture("_BumpMap"));
            Keyword(material, "USE_OCCLUSION_MAP", material.GetTexture("_OcclusionMap"));
            Keyword(material, "USE_DETAIL_MAPS", material.GetTexture("_DetailAlbedoMap") || material.GetTexture("_DetailNormalMap"));
            Keyword(material, "USE_SPECULAR", material.GetTexture("_MetallicMap") || material.GetTexture("_GlossMap") || material.GetFloat("_MetallicStrength") > 0);
            if (material.GetColor("_EmissionColor").maxColorComponent == 0)
                material.globalIlluminationFlags |= MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        private static void Keyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
        }
    }
}
