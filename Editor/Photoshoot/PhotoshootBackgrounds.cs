using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>Backgrounds the user adds to the photoshoot: copies of their pictures in their own <see cref="PhotoshootLibrary{T}"/>.</summary>
    internal static class PhotoshootBackgrounds
    {
        private static readonly PhotoshootLibrary<Texture2D> Library =
            new PhotoshootLibrary<Texture2D>(PhotoshootLibrary.UserFolder("Backgrounds"), Read, ".png", ".jpg", ".jpeg");

        internal static bool IsPicture(string path) => Library.Holds(path);

        internal static IEnumerable<string> Files() => Library.Files();

        /// <summary>Copies a picture into the folder (a project texture is read from its file) and returns the copy's path.</summary>
        internal static string Add(string file)
        {
            if (!IsPicture(file)) throw new InvalidOperationException("Choose a PNG or JPEG picture.");
            string path = Library.NewPath(Path.GetFileNameWithoutExtension(file), Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, path);
            return path;
        }

        internal static void Remove(string path) => Library.Remove(path);

        /// <summary>The picture's texture, read again when its file changed; null when it can't be read.</summary>
        internal static Texture2D Load(string path) => Library.Load(path);

        private static Texture2D Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4
            };
            if (texture.LoadImage(bytes)) return texture;
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }
    }
}
