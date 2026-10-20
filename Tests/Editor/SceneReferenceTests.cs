using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class SceneReferenceTests
    {
        // Names holding "/" are found by their path as it reads, and an ID answers only in the scenes looked in.
        [Test]
        public void NamesWithSlashesAndIdsHonourTheScenesGiven()
        {
            var first = EditorSceneManager.NewPreviewScene();
            var second = EditorSceneManager.NewPreviewScene();
            try
            {
                var outfit = new GameObject("Shirt/Pants");
                SceneManager.MoveGameObjectToScene(outfit, first);
                var belt = new GameObject("Belt");
                belt.transform.SetParent(outfit.transform, false);
                var other = new GameObject("Other");
                SceneManager.MoveGameObjectToScene(other, second);
                var scenes = new[] { first };

                Assert.That(SceneReference.Resolve("Shirt/Pants", scenes), Is.EqualTo(outfit));
                Assert.That(SceneReference.Resolve("Shirt/Pants/Belt", scenes), Is.EqualTo(belt));
                Assert.That(SceneReference.Resolve("Shirt", scenes), Is.Null, "a part of a name is no name");
                string id = other.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                Assert.That(SceneReference.Resolve(id, scenes), Is.Null, "an object of another scene, by its ID, is not in the scenes given");
                Assert.That(SceneReference.Resolve(id, new[] { first, second }), Is.EqualTo(other));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(first);
                EditorSceneManager.ClosePreviewScene(second);
            }
        }

        // A header announcing a huge image over a few bytes is refused before 1 GiB of pixels is allocated; 32-bit pixels
        // whose header declares no alpha bits are opaque.
        [Test]
        public void TgaImagesAreCheckedBeforeTheirPixelsAreMade()
        {
            byte[] Header(byte type, byte depth, byte descriptor, int width, int height) => new byte[]
            {
                0, 0, type, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), depth, descriptor
            };
            Assert.Throws<InvalidDataException>(() => TgaImage.Read(Header(2, 32, 8, 16384, 16384).Concat(new byte[64]).ToArray()));
            Assert.Throws<InvalidDataException>(() => TgaImage.Read(Header(10, 32, 8, 16384, 16384).Concat(new byte[64]).ToArray()), "run-length data that cannot hold that many pixels");

            var noAlpha = TgaImage.Read(Header(2, 32, 0, 1, 1).Concat(new byte[] { 10, 20, 30, 0 }).ToArray());
            Assert.That(noAlpha.Rgba, Is.EqualTo(new byte[] { 30, 20, 10, 255 }));
            var withAlpha = TgaImage.Read(Header(2, 32, 8, 1, 1).Concat(new byte[] { 10, 20, 30, 40 }).ToArray());
            Assert.That(withAlpha.Rgba, Is.EqualTo(new byte[] { 30, 20, 10, 40 }));
        }
    }
}
