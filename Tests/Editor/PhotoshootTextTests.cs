using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class PhotoshootTextTests
    {
        [Test]
        public void TheTextIsDrawnUprightWhereItWasPlaced()
        {
            var text = new PhotoshootText { value = "T", center = new Vector2(.25f, .75f), size = .3f, style = PhotoshootTextStyle.Clean, color = Color.white };

            var image = PhotoshootTextRenderer.Render(text, 400, 300, out var bounds);
            try
            {
                Assert.NotNull(image);
                Assert.AreEqual(100f, bounds.center.x, 1f, "a quarter of the width from the left");
                Assert.AreEqual(225f, bounds.center.y, 1f, "three quarters of the height from the top");
                // A "T" covers more of its top rows (the bar) than of its bottom rows (the stem); textures count rows from the bottom.
                float top = 0f, bottom = 0f;
                for (int x = 0; x < image.width; x++)
                {
                    for (int y = image.height * 3 / 4; y < image.height; y++) top += image.GetPixel(x, y).a;
                    for (int y = 0; y < image.height / 4; y++) bottom += image.GetPixel(x, y).a;
                }
                Assert.Greater(top, bottom * 1.5f);
            }
            finally { Object.DestroyImmediate(image); }
        }

        [Test]
        public void EachLookAddsItsEdgeAroundTheLetters()
        {
            var clean = new PhotoshootText { value = "Hi", size = .3f, style = PhotoshootTextStyle.Clean };
            var outlined = clean.Clone(); outlined.style = PhotoshootTextStyle.Outline;
            var a = PhotoshootTextRenderer.Render(clean, 400, 300, out var cleanBounds);
            var b = PhotoshootTextRenderer.Render(outlined, 400, 300, out var outlineBounds);
            try
            {
                Assert.Greater(outlineBounds.width, cleanBounds.width, "the outline needs room around the letters");
                Assert.Greater(Covered(b), Covered(a));
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        [Test]
        public void CapturingDrawsTheTextIntoThePictureOnly()
        {
            var picture = new Texture2D(200, 150, TextureFormat.RGBA32, false);
            var pixels = new Color[200 * 150];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.black;
            picture.SetPixels(pixels);
            var text = new PhotoshootText { value = "OK", center = new Vector2(.5f, .5f), size = .3f, style = PhotoshootTextStyle.Clean, color = Color.white };
            try
            {
                PhotoshootTextRenderer.Composite(picture, text);
                Assert.Greater(picture.GetPixel(100, 75).r + picture.GetPixel(95, 75).r + picture.GetPixel(105, 75).r + picture.GetPixel(100, 70).r, 0f, "white letters in the middle");
                Assert.AreEqual(Color.black, picture.GetPixel(2, 2), "the rest of the picture is left alone");
                Assert.AreEqual(1f, picture.GetPixel(100, 75).a, 1e-3f);
            }
            finally { Object.DestroyImmediate(picture); }
        }

        [Test]
        public void NoTextDrawsNothing()
        {
            Assert.IsNull(PhotoshootTextRenderer.Render(new PhotoshootText { value = "  " }, 400, 300, out _));
        }

        [Test]
        public void TheTextComesBackAsItWasLeft()
        {
            var text = new PhotoshootText { value = "Orbit", font = "Packages/x/Fonts/Pirata.ttf", center = new Vector2(.3f, .9f), size = .2f, color = Color.red, style = PhotoshootTextStyle.Glow };

            var back = PhotoshootText.FromJson(text.ToJson());

            Assert.AreEqual("Orbit", back.value);
            Assert.AreEqual(text.font, back.font);
            Assert.AreEqual(text.center, back.center);
            Assert.AreEqual(.2f, back.size);
            Assert.AreEqual(Color.red, back.color);
            Assert.AreEqual(PhotoshootTextStyle.Glow, back.style);
            Assert.IsNull(PhotoshootText.FromJson("not json"));
            Assert.AreEqual(PhotoshootText.DefaultFont, PhotoshootText.LoadFont(""));
        }

        private static float Covered(Texture2D image)
        {
            float sum = 0f;
            foreach (var pixel in image.GetPixels()) sum += pixel.a;
            return sum;
        }
    }
}
