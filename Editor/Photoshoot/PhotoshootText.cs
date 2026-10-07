using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    public enum PhotoshootTextStyle { Clean, Outline, Shadow, Glow }

    /// <summary>A line of text over a shot, placed and sized as a share of the picture, so it lands the same at any size.</summary>
    [Serializable]
    public sealed class PhotoshootText
    {
        public string value = "";
        /// <summary>The font's asset path; empty for Unity's default font.</summary>
        public string font = "";
        /// <summary>Where the text's middle is, as a share of the picture's width and height from its top-left corner.</summary>
        public Vector2 center = new Vector2(.5f, .8f);
        /// <summary>The font size as a share of the picture's height.</summary>
        public float size = .13f;
        public Color color = Color.white;
        public PhotoshootTextStyle style = PhotoshootTextStyle.Outline;

        public const float MinSize = .03f, MaxSize = .45f;

        public bool IsEmpty => string.IsNullOrWhiteSpace(value);
        public PhotoshootText Clone() => (PhotoshootText)MemberwiseClone();
        public string ToJson() => JsonUtility.ToJson(this);

        public static PhotoshootText FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<PhotoshootText>(json); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>The font to draw with: the asset at <see cref="font"/>, or Unity's default font.</summary>
        public static Font LoadFont(string path)
        {
            var font = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Font>(path);
            return font ? font : DefaultFont;
        }

        public static Font DefaultFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        /// <summary>A font's family name as people know it ("Pirata One"), from the font file.</summary>
        public static string FamilyName(Font font)
        {
            if (!font) return "";
            if (font == DefaultFont) return "Unity default";
            var names = font.fontNames;
            if (names != null && names.Length > 0 && !string.IsNullOrWhiteSpace(names[0])) return names[0];
            return ObjectNames.NicifyVariableName(font.name.Replace("-Regular", ""));
        }
    }

    /// <summary>
    /// Draws a <see cref="PhotoshootText"/> as a transparent image: glyph coverage from the font's atlas on the GPU, then
    /// its outline, shadow or glow and colours on the CPU. The live preview shows that same image over the shot, so a
    /// captured picture has the text exactly where and how it was seen.
    /// </summary>
    internal static class PhotoshootTextRenderer
    {
        private const string ShaderName = "Hidden/Orbiters/PhotoshootGlyphs";
        private const string ShaderPath = "Packages/orbiters.toolkit/Editor/Photoshoot/PhotoshootGlyphs.shader";
        private static Material material;

        /// <summary>
        /// The text as an image for a picture of <paramref name="width"/> × <paramref name="height"/> pixels, and where it
        /// goes in that picture (pixels from its top-left corner). Null for empty text. Destroy the texture when done.
        /// </summary>
        internal static Texture2D Render(PhotoshootText text, int width, int height, out Rect bounds)
        {
            bounds = default;
            if (text == null || text.IsEmpty || width <= 0 || height <= 0) return null;
            var font = PhotoshootText.LoadFont(text.font);
            string line = text.value.Replace('\n', ' ').Replace('\r', ' ');
            int px = Mathf.Clamp(Mathf.RoundToInt(text.size * height), 4, 1024);
            // Every character first: a request can rebuild the atlas, which moves the glyphs already read.
            font.RequestCharactersInTexture(line, px, FontStyle.Normal);
            var glyphs = new List<(Rect rect, CharacterInfo info)>();
            float x = 0f, minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (char c in line)
            {
                if (!font.GetCharacterInfo(c, out var info, px, FontStyle.Normal)) continue;
                var rect = Rect.MinMaxRect(x + info.minX, info.minY, x + info.maxX, info.maxY);
                if (rect.width > 0 && rect.height > 0)
                {
                    glyphs.Add((rect, info));
                    minX = Mathf.Min(minX, rect.xMin); maxX = Mathf.Max(maxX, rect.xMax);
                    minY = Mathf.Min(minY, rect.yMin); maxY = Mathf.Max(maxY, rect.yMax);
                }
                x += info.advance;
            }
            if (glyphs.Count == 0) return null;

            var look = Look.Of(text, px);
            int pad = Mathf.CeilToInt(look.Outline + look.Blur * 2f + Mathf.Max(Mathf.Abs(look.Shadow.x), Mathf.Abs(look.Shadow.y))) + 2;
            int w = Mathf.Min(4096, Mathf.CeilToInt(maxX - minX) + pad * 2), h = Mathf.Min(4096, Mathf.CeilToInt(maxY - minY) + pad * 2);
            var origin = new Vector2(pad - minX, pad - minY);
            var fill = Coverage(font, glyphs, origin, w, h, new[] { Vector2.zero });
            var outline = look.Outline > 0f ? Coverage(font, glyphs, origin, w, h, Ring(look.Outline)) : null;

            var pixels = new Color[w * h];
            if (look.Style == PhotoshootTextStyle.Shadow) Over(pixels, Shift(Blur(fill, w, h, look.Blur), w, h, look.Shadow), look.Effect);
            if (look.Style == PhotoshootTextStyle.Glow) Over(pixels, Strengthen(Blur(outline ?? fill, w, h, look.Blur), 1.6f), look.Effect);
            if (outline != null && look.Style != PhotoshootTextStyle.Glow) Over(pixels, outline, look.Edge);
            Over(pixels, fill, look.Fill);

            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Orbiters Photoshoot Text", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            var center = new Vector2(text.center.x * width, text.center.y * height);
            bounds = new Rect(center.x - w / 2f, center.y - h / 2f, w, h);
            return texture;
        }

        /// <summary>Draws the text into <paramref name="picture"/> (its own pixels), as <see cref="Render"/> shows it.</summary>
        internal static void Composite(Texture2D picture, PhotoshootText text)
        {
            var image = Render(text, picture.width, picture.height, out var bounds);
            if (image == null) return;
            try
            {
                var source = image.GetPixels();
                int left = Mathf.RoundToInt(bounds.x), bottom = Mathf.RoundToInt(picture.height - bounds.yMax);
                int x0 = Mathf.Max(0, left), y0 = Mathf.Max(0, bottom);
                int x1 = Mathf.Min(picture.width, left + image.width), y1 = Mathf.Min(picture.height, bottom + image.height);
                if (x1 <= x0 || y1 <= y0) return;
                var target = picture.GetPixels(x0, y0, x1 - x0, y1 - y0);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        var s = source[(y - bottom) * image.width + (x - left)];
                        if (s.a <= 0f) continue;
                        int i = (y - y0) * (x1 - x0) + (x - x0);
                        var d = target[i];
                        float a = s.a + d.a * (1f - s.a);
                        target[i] = a <= 0f ? Color.clear : new Color(
                            (s.r * s.a + d.r * d.a * (1f - s.a)) / a, (s.g * s.a + d.g * d.a * (1f - s.a)) / a, (s.b * s.a + d.b * d.a * (1f - s.a)) / a, a);
                    }
                picture.SetPixels(x0, y0, x1 - x0, y1 - y0, target);
                picture.Apply(false, false);
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }

        // ---- How each style looks, in pixels of the font size ----

        private struct Look
        {
            public PhotoshootTextStyle Style;
            public float Outline, Blur;
            public Vector2 Shadow;
            public Color Fill, Edge, Effect;

            public static Look Of(PhotoshootText text, int px)
            {
                var fill = text.color; fill.a = 1f;
                // The edge contrasts with the letters: dark around light text, light around dark text.
                bool light = fill.r * .299f + fill.g * .587f + fill.b * .114f > .5f;
                var look = new Look { Style = text.style, Fill = fill, Edge = light ? new Color(.05f, .05f, .07f, 1f) : Color.white };
                switch (text.style)
                {
                    case PhotoshootTextStyle.Outline: look.Outline = Mathf.Max(1.5f, px * .075f); break;
                    case PhotoshootTextStyle.Shadow:
                        look.Blur = Mathf.Max(1f, px * .07f); look.Shadow = new Vector2(px * .04f, -px * .06f);
                        look.Effect = new Color(0f, 0f, 0f, .75f); break;
                    case PhotoshootTextStyle.Glow:
                        // Neon: the letters' own colour around them, the letters themselves brighter.
                        look.Outline = Mathf.Max(1f, px * .05f); look.Blur = Mathf.Max(2f, px * .16f);
                        look.Effect = fill; look.Fill = Color.Lerp(fill, Color.white, .55f); break;
                }
                return look;
            }
        }

        // Offsets around a circle of this radius, filled in: drawing the glyphs at each one gives their outline.
        private static Vector2[] Ring(float radius)
        {
            var offsets = new List<Vector2> { Vector2.zero };
            int steps = Mathf.Clamp(Mathf.CeilToInt(radius * 2.5f), 8, 48);
            for (float r = radius; r > .5f; r -= Mathf.Max(1f, radius / 3f))
                for (int i = 0; i < steps; i++)
                {
                    float angle = i * Mathf.PI * 2f / steps;
                    offsets.Add(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r);
                }
            return offsets.ToArray();
        }

        private static float[] Coverage(Font font, List<(Rect rect, CharacterInfo info)> glyphs, Vector2 origin, int w, int h, Vector2[] offsets)
        {
            var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.clear);
                var draw = Material();
                draw.mainTexture = font.material.mainTexture;
                draw.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, 0, h);
                GL.Begin(GL.QUADS);
                foreach (var offset in offsets)
                    foreach (var (rect, info) in glyphs)
                    {
                        float x0 = rect.xMin + origin.x + offset.x, x1 = rect.xMax + origin.x + offset.x;
                        float y0 = rect.yMin + origin.y + offset.y, y1 = rect.yMax + origin.y + offset.y;
                        GL.TexCoord(info.uvBottomLeft); GL.Vertex3(x0, y0, 0);
                        GL.TexCoord(info.uvTopLeft); GL.Vertex3(x0, y1, 0);
                        GL.TexCoord(info.uvTopRight); GL.Vertex3(x1, y1, 0);
                        GL.TexCoord(info.uvBottomRight); GL.Vertex3(x1, y0, 0);
                    }
                GL.End();
                GL.PopMatrix();
                var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                try
                {
                    read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    var raw = read.GetRawTextureData<Color32>();
                    var coverage = new float[w * h];
                    for (int i = 0; i < coverage.Length; i++) coverage[i] = raw[i].a / 255f;
                    return coverage;
                }
                finally { UnityEngine.Object.DestroyImmediate(read); }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static Material Material()
        {
            if (material) return material;
            var shader = Shader.Find(ShaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (!shader) throw new InvalidOperationException("The photoshoot's text shader is missing: reimport Orbiters Toolkit.");
            return material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        // Three box blurs: close to a gaussian of this radius.
        private static float[] Blur(float[] source, int w, int h, float radius)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(radius / 1.7f));
            var a = (float[])source.Clone();
            var b = new float[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(a, b, w, h, r, 1, w);
                BoxBlur(b, a, h, w, r, w, 1);
            }
            return a;
        }

        // Running sums along each line (step) for every line (stride).
        private static void BoxBlur(float[] from, float[] to, int length, int lines, int r, int step, int stride)
        {
            float scale = 1f / (r * 2 + 1);
            for (int line = 0; line < lines; line++)
            {
                int start = line * stride;
                float sum = 0f;
                for (int i = -r; i <= r; i++) sum += from[start + Mathf.Clamp(i, 0, length - 1) * step];
                for (int i = 0; i < length; i++)
                {
                    to[start + i * step] = sum * scale;
                    sum += from[start + Mathf.Min(i + r + 1, length - 1) * step] - from[start + Mathf.Max(i - r, 0) * step];
                }
            }
        }

        private static float[] Shift(float[] source, int w, int h, Vector2 by)
        {
            int dx = Mathf.RoundToInt(by.x), dy = Mathf.RoundToInt(by.y);
            var shifted = new float[source.Length];
            for (int y = 0; y < h; y++)
            {
                int sy = y - dy;
                if (sy < 0 || sy >= h) continue;
                for (int x = 0; x < w; x++)
                {
                    int sx = x - dx;
                    if (sx >= 0 && sx < w) shifted[y * w + x] = source[sy * w + sx];
                }
            }
            return shifted;
        }

        private static float[] Strengthen(float[] source, float factor)
        {
            for (int i = 0; i < source.Length; i++) source[i] = Mathf.Min(1f, source[i] * factor);
            return source;
        }

        // Straight-alpha "over": the layer's colour at its coverage on top of what is there.
        private static void Over(Color[] pixels, float[] coverage, Color color)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                float sa = coverage[i] * color.a;
                if (sa <= 0f) continue;
                var d = pixels[i];
                float a = sa + d.a * (1f - sa);
                pixels[i] = new Color((color.r * sa + d.r * d.a * (1f - sa)) / a, (color.g * sa + d.g * d.a * (1f - sa)) / a, (color.b * sa + d.b * d.a * (1f - sa)) / a, a);
            }
        }
    }
}
