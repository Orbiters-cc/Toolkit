using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>VRChat's logo (its speech bubble with the name): marks what is a VRChat avatar.</summary>
    public static class VRChatLogo
    {
        /// <summary>
        /// The logo rasterized from VRChat's SVG at the size it shows (36 × 18 points), once per screen scale: Unity's UI
        /// draws textures without their mipmaps, so a large image shrunk on screen, or vectors drawn at this size, came
        /// out jagged and hard to read.
        /// </summary>
        public const string ImagePath1x = "Packages/orbiters.toolkit/Editor/UI/VRChatLogo@1x.png";
        public const string ImagePath2x = "Packages/orbiters.toolkit/Editor/UI/VRChatLogo@2x.png";
        public const float Width = 36f, Height = 18f;

        private static Texture2D texture1x, texture2x;
        private static bool hooked;

        /// <summary>The logo at its size; null when its image is missing.</summary>
        public static VisualElement Create()
        {
            var image = Texture;
            if (image == null) return null;
            var logo = new Image { image = image, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            logo.AddToClassList("orb-vrchat-logo");
            logo.style.width = Width;
            logo.style.height = Height;
            logo.style.flexShrink = 0;
            return logo;
        }

        /// <summary>The image for the current screen scale (pixel for pixel at 100 % and 200 %).</summary>
        public static Texture2D Texture => EditorGUIUtility.pixelsPerPoint > 1.25f
            ? texture2x != null ? texture2x : texture2x = Load(ImagePath2x)
            : texture1x != null ? texture1x : texture1x = Load(ImagePath1x);

        // Read from the file itself: the importer's defaults (compression, a power-of-two resize) would blur it. Freed
        // before a script reload, which would orphan them.
        private static Texture2D Load(string assetPath)
        {
            if (!hooked)
            {
                hooked = true;
                AssemblyReloadEvents.beforeAssemblyReload += () =>
                {
                    if (texture1x != null) Object.DestroyImmediate(texture1x);
                    if (texture2x != null) Object.DestroyImmediate(texture2x);
                    texture1x = texture2x = null;
                };
            }
            string path = Path.GetFullPath(assetPath);
            if (!File.Exists(path)) return null;
            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "VRChat logo", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (loaded.LoadImage(File.ReadAllBytes(path), true)) { loaded.filterMode = FilterMode.Bilinear; loaded.wrapMode = TextureWrapMode.Clamp; return loaded; }
            Object.DestroyImmediate(loaded);
            return null;
        }
    }
}
