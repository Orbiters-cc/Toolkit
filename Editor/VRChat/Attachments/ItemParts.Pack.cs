using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    public static partial class ItemParts
    {
        /// <summary>
        /// A base variant as Orbiters takes it (publishing posts the creator's base variants as their official ones): its
        /// files, its thumbnail (shipped beside it, else drawn: the renderer's picture or the prefab's renderers) and, for
        /// a model base variant without a mesh (the item's own model), that model exported to an FBX with its materials
        /// described from the prefab's (their shaders must be among the part's) and their textures as PNGs.
        /// </summary>
        public static async Task<PartVariant> PackAsync(GameObject prefab, Definition definition, string prefabPath, string partsPath, Part part, PartVariant variant)
        {
            var packed = variant.Copy();
            packed.Folder = variant.Folder; packed.BaseName = variant.BaseName; packed.Official = true;
            if (part.kind == Model && string.IsNullOrEmpty(variant.model?.mesh)) PackOwnModel(prefab, part, packed);
            if (packed.Thumbnail == null)
                packed.Thumbnail = await ThumbnailAsync(prefab != null ? prefab.transform : null, definition, prefabPath, partsPath, part, packed);
            return packed;
        }

        // The prefab's own model in the slots (the first path of each), as a variant's FBX, materials and textures.
        private static void PackOwnModel(GameObject prefab, Part part, PartVariant packed)
        {
            if (prefab == null) throw new InvalidOperationException(part.Label + ": the item's prefab is needed to post its own model.");
            packed.model = new VariantModel { mesh = "model.fbx" };
            var meshes = new List<FbxExportMesh>();
            var holders = new List<GameObject>();
            try
            {
                foreach (var slot in (part.slots ?? new List<Slot>()).Where(s => s != null))
                {
                    var renderer = Find(prefab.transform, (slot.paths ?? new List<string>()).FirstOrDefault())?.GetComponent<Renderer>();
                    var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer != null ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
                    if (mesh == null) throw new InvalidOperationException(part.Label + ": " + slot.Label + " has no mesh in the prefab.");
                    // At the file's origin, named by its slot: the variant's slot takes that object.
                    var holder = EditorUtility.CreateGameObjectWithHideFlags(slot.key, HideFlags.HideAndDontSave);
                    holders.Add(holder);
                    var materials = renderer.sharedMaterials;
                    meshes.Add(new FbxExportMesh { Transform = holder.transform, Mesh = mesh, Name = slot.key, Materials = materials });
                    var described = new List<VariantMaterial>();
                    for (int i = 0; i < materials.Length; i++) described.Add(Describe(part, slot, i, materials[i], packed));
                    packed.model.slots[slot.key] = new VariantSlot { objectName = slot.key, materials = described };
                }
                string file = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp", "Orbiters parts " + Guid.NewGuid().ToString("N") + ".fbx");
                var result = FbxExport.Write(file, meshes);
                if (!result.Success) throw new InvalidOperationException(part.Label + ": its model could not be written: " + result.Error);
                try { packed.Files["model.fbx"] = File.ReadAllBytes(file); }
                finally { File.Delete(file); }
            }
            finally { foreach (var holder in holders) Object.DestroyImmediate(holder); }
        }

        // A material as a variant describes it: its shader (one of the part's), its colours, numbers and 2D textures.
        private static VariantMaterial Describe(Part part, Slot slot, int index, Material material, PartVariant packed)
        {
            if (material == null || !(part.shaders ?? new List<string>()).Contains(material.shader.name))
                throw new InvalidOperationException(part.Label + ": " + slot.Label + "'s material " + (index + 1) + " uses " + (material != null ? material.shader.name : "nothing") +
                                                    ", not one of the part's shaders (" + string.Join(", ", part.shaders ?? new List<string>()) + ").");
            var described = new VariantMaterial { shader = material.shader.name };
            var shader = material.shader;
            for (int p = 0; p < shader.GetPropertyCount(); p++)
            {
                string name = shader.GetPropertyName(p);
                switch (shader.GetPropertyType(p))
                {
                    // Only what differs from the shader's defaults (Orbiters takes 32 of each at most).
                    case ShaderPropertyType.Color:
                        if (ColorText(material.GetColor(name)) != ColorText((Color)shader.GetPropertyDefaultVectorValue(p))) described.colors[name] = ColorText(material.GetColor(name));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (Mathf.Abs(material.GetFloat(name) - shader.GetPropertyDefaultFloatValue(p)) > 1e-5f) described.floats[name] = material.GetFloat(name);
                        break;
                    case ShaderPropertyType.Texture:
                        if (shader.GetPropertyTextureDimension(p) != TextureDimension.Tex2D || !(material.GetTexture(name) is Texture2D texture)) break;
                        // Normal maps are stored swizzled: only colour pictures travel.
                        if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer && importer.textureType == TextureImporterType.NormalMap) break;
                        string file = Clean(slot.key + " " + (index + 1) + name, 48) + ".png";
                        packed.Files[file] = Png(texture, Mathf.Max(16, part.maxTextureSize));
                        described.textures[name] = file;
                        break;
                }
            }
            return described;
        }

        // A texture as a PNG of at most `max` pixels a side, read back through the GPU (compressed or unreadable ones too).
        private static byte[] Png(Texture2D texture, int max)
        {
            float scale = Mathf.Min(1, (float)max / Mathf.Max(texture.width, texture.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale)), height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, render);
                RenderTexture.active = render;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(render);
                Object.DestroyImmediate(copy);
            }
        }
    }
}
