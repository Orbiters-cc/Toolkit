using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>"by blackorbit · support me on KoFi" footer of the Orbiters tools. Clicking it opens the Ko-fi page.</summary>
    public sealed class SupportCredit : VisualElement
    {
        public const string KofiUrl = "https://ko-fi.com/blackorbit";
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/support-credit.uss";
        private const string ProfilePath = "Packages/orbiters.toolkit/Editor/UI/Images/blackorbit.png";
        private const string KofiSymbolPath = "Packages/orbiters.toolkit/Editor/UI/Images/kofi_symbol.png";
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        public SupportCredit()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-credit");
            tooltip = KofiUrl;
            Add(Text("by blackorbit"));
            Add(Picture(ProfilePath, "orb-credit__profile", true, ScaleMode.ScaleAndCrop));
            Add(Text("support me on KoFi"));
            Add(Picture(KofiSymbolPath, "orb-credit__kofi", false, ScaleMode.ScaleToFit));
            RegisterCallback<MouseDownEvent>(_ => AddToClassList("orb-credit--pressed"));
            RegisterCallback<MouseUpEvent>(_ => RemoveFromClassList("orb-credit--pressed"));
            RegisterCallback<MouseLeaveEvent>(_ => RemoveFromClassList("orb-credit--pressed"));
            RegisterCallback<ClickEvent>(evt => { Application.OpenURL(KofiUrl); evt.StopPropagation(); });
        }

        private static Label Text(string text)
        {
            var label = new Label(text);
            label.AddToClassList("orb-credit__text");
            return label;
        }

        private static Image Picture(string path, string className, bool circular, ScaleMode scaleMode)
        {
            var image = new Image { image = Load(path, circular), scaleMode = scaleMode };
            image.AddToClassList(className);
            return image;
        }

        private static Texture2D Load(string path, bool circular)
        {
            string key = path + (circular ? "|circle" : "");
            if (Textures.TryGetValue(key, out var cached) && cached != null) return cached;
            // Read the file directly so the image stays crisp whatever its import settings.
            Texture2D texture = null;
            var file = Path.GetFullPath(path);
            if (File.Exists(file))
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = Path.GetFileNameWithoutExtension(path) };
                if (!texture.LoadImage(File.ReadAllBytes(file))) { Object.DestroyImmediate(texture); texture = null; }
            }
            if (texture == null) texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                if (circular && texture.isReadable) Circle(texture);
            }
            Textures[key] = texture;
            return texture;
        }

        // Clears the corners outside the inscribed circle, in place.
        private static void Circle(Texture2D texture)
        {
            int width = texture.width, height = texture.height;
            float radius = Mathf.Min(width, height) * 0.5f;
            var pixels = texture.GetPixels32();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float dx = x + 0.5f - width * 0.5f, dy = y + 0.5f - height * 0.5f;
                if (dx * dx + dy * dy > radius * radius) pixels[y * width + x].a = 0;
            }
            texture.SetPixels32(pixels);
            texture.Apply();
        }
    }
}
