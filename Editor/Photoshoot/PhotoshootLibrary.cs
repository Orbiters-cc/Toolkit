using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Storage;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    internal static class PhotoshootLibrary
    {
        /// <summary>The user's own folder for one kind of photoshoot item, shared by every project.</summary>
        internal static string UserFolder(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Photoshoot", name);
    }

    /// <summary>
    /// What the user adds to the photoshoot (<see cref="PhotoshootBackgrounds"/>, <see cref="PhotoshootPoses"/>): files in a
    /// folder of their own, so every project offers them. Each is loaded once per editor session, and again when its file
    /// changes; the loaded objects belong to the library.
    /// </summary>
    internal sealed class PhotoshootLibrary<T> where T : UnityEngine.Object
    {
        private readonly Dictionary<string, (DateTime written, T item)> loaded = new Dictionary<string, (DateTime, T)>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] extensions;
        private readonly Func<string, T> read;

        /// <param name="read">Reads a file of the library; null when it can't be read.</param>
        internal PhotoshootLibrary(string folder, Func<string, T> read, params string[] extensions)
        {
            Folder = folder;
            this.read = read;
            this.extensions = extensions;
        }

        internal string Folder { get; }

        /// <summary>Whether <paramref name="path"/> has one of the library's extensions.</summary>
        internal bool Holds(string path) => extensions.Contains(Path.GetExtension(path ?? string.Empty).ToLowerInvariant());

        internal IEnumerable<string> Files()
        {
            if (!Directory.Exists(Folder)) return Enumerable.Empty<string>();
            return Directory.GetFiles(Folder).Where(Holds).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A free path in the folder (made if missing) for an item called <paramref name="name"/>: "name", else "name 2"…</summary>
        internal string NewPath(string name, string extension)
        {
            Directory.CreateDirectory(Folder);
            name = AssetPaths.FileName(name);
            string path = Path.Combine(Folder, name + extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Folder, name + " " + i + extension);
            return path;
        }

        /// <summary>Deletes one of the library's files (never a file elsewhere) and destroys what was loaded from it.</summary>
        internal void Remove(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (loaded.TryGetValue(path, out var known))
            {
                if (known.item) UnityEngine.Object.DestroyImmediate(known.item);
                loaded.Remove(path);
            }
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>The file's object, read again when the file changed; null when it can't be read.</summary>
        internal T Load(string path)
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (loaded.TryGetValue(path, out var known) && known.item && known.written == written) return known.item;
            if (known.item) UnityEngine.Object.DestroyImmediate(known.item);
            T item = null;
            try { item = read(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            if (item) loaded[path] = (written, item);
            else loaded.Remove(path);
            return item;
        }
    }
}
