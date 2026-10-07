using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// Backgrounds the user adds to the photoshoot: copies of their pictures in a folder of their own, so every project
    /// offers them. Their textures are loaded once per editor session.
    /// </summary>
    internal static class PhotoshootBackgrounds
    {
        private static readonly Dictionary<string, (DateTime written, Texture2D texture)> Loaded =
            new Dictionary<string, (DateTime, Texture2D)>(StringComparer.OrdinalIgnoreCase);

        internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Photoshoot", "Backgrounds");

        internal static bool IsPicture(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        }

        internal static IEnumerable<string> Files()
        {
            if (!Directory.Exists(Folder)) return Enumerable.Empty<string>();
            return Directory.GetFiles(Folder).Where(IsPicture).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Copies a picture into the folder (a project texture is read from its file) and returns the copy's path.</summary>
        internal static string Add(string file)
        {
            if (!IsPicture(file)) throw new InvalidOperationException("Choose a PNG or JPEG picture.");
            Directory.CreateDirectory(Folder);
            string name = Path.GetFileNameWithoutExtension(file), extension = Path.GetExtension(file).ToLowerInvariant();
            string path = Path.Combine(Folder, name + extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Folder, name + " " + i + extension);
            File.Copy(file, path);
            return path;
        }

        internal static void Remove(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (Loaded.TryGetValue(path, out var known))
            {
                if (known.texture) UnityEngine.Object.DestroyImmediate(known.texture);
                Loaded.Remove(path);
            }
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>The picture's texture, read again when its file changed; null when it can't be read.</summary>
        internal static Texture2D Load(string path)
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (Loaded.TryGetValue(path, out var known) && known.texture && known.written == written) return known.texture;
            if (known.texture) UnityEngine.Object.DestroyImmediate(known.texture);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4
            };
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                Loaded.Remove(path);
                return null;
            }
            Loaded[path] = (written, texture);
            return texture;
        }
    }
}
