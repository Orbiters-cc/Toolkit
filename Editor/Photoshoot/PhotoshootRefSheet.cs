using System;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// A character reference sheet: the posed avatar from the front, the back and the side, side by side at one scale on a
    /// plain background, each view named above it. <see cref="PhotoshootState"/> renders it with the photoshoot's own
    /// stage, pose, light and expression; this lays the sheet out.
    /// </summary>
    public static class PhotoshootRefSheet
    {
        public static readonly Vector2Int Size = new Vector2Int(1920, 1080);
        public static readonly Color DefaultBackground = new Color32(0x1f, 0x1f, 0x1f, 0xff);
        internal static readonly string[] Views = { "Front", "Back", "Side" };
        private static readonly Color LabelColor = new Color32(0x9a, 0x9a, 0x9a, 0xff);
        // Parts of the sheet's height: the band the names sit in, their size and baseline, and the margin under the feet.
        internal const float LabelBand = 0.13f, Floor = 0.035f;
        private const float LabelSize = 0.042f, LabelBaseline = 0.088f;
        private static Font labelFont;

        /// <summary>How each view turns the avatar: facing the camera, its back, then its side facing left or right.</summary>
        internal static float Yaw(int view, bool sideFacesRight) => view == 0 ? 0f : view == 1 ? 180f : sideFacesRight ? -90f : 90f;

        /// <summary>The size each view renders at on a sheet of <paramref name="sheet"/>.</summary>
        internal static Vector2Int ViewSize(Vector2Int sheet) =>
            new Vector2Int(Mathf.Max(1, sheet.x / Views.Length), Mathf.Max(1, Mathf.RoundToInt(sheet.y * (1f - LabelBand - Floor))));

        /// <summary>Fills the sheet with its background before the views are copied in.</summary>
        internal static void Clear(RenderTexture sheet, Color background)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = sheet;
                // GL.Clear takes the colour as stored: the photoshoot camera's gamma colour is converted to match it.
                GL.Clear(true, true, Stored(background));
            }
            finally { RenderTexture.active = previous; }
        }

        /// <summary>Copies a rendered view into its column, under the names.</summary>
        internal static void Place(RenderTexture sheet, Texture view, int index)
        {
            var size = ViewSize(new Vector2Int(sheet.width, sheet.height));
            int x = index * size.x + (sheet.width - size.x * Views.Length) / 2;
            int y = Mathf.RoundToInt(sheet.height * Floor);
            Graphics.CopyTexture(view, 0, 0, 0, 0, Mathf.Min(size.x, view.width), Mathf.Min(size.y, view.height), sheet, 0, 0, x, y);
        }

        /// <summary>Writes each view's name, in italic grey, centred over its column.</summary>
        internal static void DrawNames(RenderTexture sheet)
        {
            var font = LabelFont();
            if (font == null) return;
            int pixels = Mathf.Max(8, Mathf.RoundToInt(sheet.height * LabelSize));
            float baseline = sheet.height * LabelBaseline, column = sheet.width / (float)Views.Length;
            var style = font.name.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0 ? FontStyle.Normal : FontStyle.Italic;
            foreach (string name in Views) font.RequestCharactersInTexture(name, pixels, style);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = sheet;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, sheet.width, sheet.height, 0);
                font.material.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Stored(LabelColor));
                for (int view = 0; view < Views.Length; view++)
                {
                    string name = Views[view];
                    float width = 0f;
                    foreach (char c in name) if (font.GetCharacterInfo(c, out var measured, pixels, style)) width += measured.advance;
                    float x = column * (view + 0.5f) - width * 0.5f;
                    foreach (char c in name)
                    {
                        if (!font.GetCharacterInfo(c, out var glyph, pixels, style)) continue;
                        // Glyph metrics go up from the baseline; the pixel matrix goes down from the top.
                        float left = x + glyph.minX, right = x + glyph.maxX, top = baseline - glyph.maxY, bottom = baseline - glyph.minY;
                        GL.TexCoord(glyph.uvTopLeft); GL.Vertex3(left, top, 0f);
                        GL.TexCoord(glyph.uvTopRight); GL.Vertex3(right, top, 0f);
                        GL.TexCoord(glyph.uvBottomRight); GL.Vertex3(right, bottom, 0f);
                        GL.TexCoord(glyph.uvBottomLeft); GL.Vertex3(left, bottom, 0f);
                        x += glyph.advance;
                    }
                }
                GL.End();
                GL.PopMatrix();
            }
            finally { RenderTexture.active = previous; }
        }

        // The editor's own italic (Inter), else its regular font slanted.
        private static Font LabelFont()
        {
            if (labelFont != null) return labelFont;
            labelFont = EditorGUIUtility.Load("Fonts/Inter/Inter-Italic.ttf") as Font;
            if (labelFont == null) labelFont = EditorStyles.standardFont;
            return labelFont;
        }

        private static Color Stored(Color color) => QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
    }
}
