using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace Orbiters.Toolkit.Editor
{
    public sealed partial class ProjectReferences
    {
        // A folder whose files can use project assets: its asset-style prefix, where it is on disk, and whether Unity imports
        // it (Assets/ and packages do; ProjectSettings/ does not).
        internal readonly struct Root
        {
            public readonly string Prefix, Folder;
            public readonly bool Imported;
            public Root(string prefix, string folder, bool imported) { Prefix = prefix; Folder = folder; Imported = imported; }
        }

        private static string ProjectFolder => Path.GetDirectoryName(Application.dataPath);

        /// <summary>
        /// Assets/, ProjectSettings/ and every embedded or local package (the packages a user can edit; registry, Git and
        /// built-in packages are read-only copies that cannot know a project's files).
        /// </summary>
        internal static List<Root> Roots()
        {
            var roots = new List<Root>
            {
                new Root("Assets", Application.dataPath, true),
                new Root("ProjectSettings", Path.Combine(ProjectFolder, "ProjectSettings"), false),
            };
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
                if (package != null && (package.source == PackageSource.Embedded || package.source == PackageSource.Local) &&
                    !string.IsNullOrEmpty(package.assetPath) && !string.IsNullOrEmpty(package.resolvedPath))
                    roots.Add(new Root(package.assetPath.TrimEnd('/'), package.resolvedPath, true));
            return roots;
        }

        private static bool SameRoots(List<Root> a, List<Root> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => x.Prefix == y.Prefix && string.Equals(Path.GetFullPath(x.Folder), Path.GetFullPath(y.Folder), StringComparison.OrdinalIgnoreCase)).All(same => same);

        /// <summary>A file's and its .meta's size and write time (-1 for a size that is not there).</summary>
        internal readonly struct Stamp : IEquatable<Stamp>
        {
            public readonly long Size, Ticks, MetaSize, MetaTicks;
            public Stamp(long size, long ticks, long metaSize, long metaTicks) { Size = size; Ticks = ticks; MetaSize = metaSize; MetaTicks = metaTicks; }
            public static Stamp Of(FileSystemInfo file, FileInfo meta) => new Stamp(
                file is FileInfo f && f.Exists ? f.Length : -1, file != null && file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
                meta != null && meta.Exists ? meta.Length : -1, meta != null && meta.Exists ? meta.LastWriteTimeUtc.Ticks : 0);
            public bool Equals(Stamp other) => Size == other.Size && Ticks == other.Ticks && MetaSize == other.MetaSize && MetaTicks == other.MetaTicks;
            public override bool Equals(object obj) => obj is Stamp other && Equals(other);
            public override int GetHashCode() => (Size, Ticks, MetaSize, MetaTicks).GetHashCode();
        }

        internal sealed class FileRead
        {
            public string Path;
            public bool Imported;
            public Stamp Stamp;
            // Its own content was read as text (otherwise only its .meta was: a picture, a model, a binary file).
            public bool ContentRead;
            public string Failure;
            public readonly HashSet<string> Guids = new HashSet<string>(StringComparer.Ordinal);
        }

        // Files whose content never names another asset: not read (their .meta is). Only a time saving: what Unity's
        // importer of each says decides whether the file needs to be as Unity imported it.
        private static readonly HashSet<string> Media = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".iff", ".pict", ".dds", ".ktx", ".astc",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".mod", ".it", ".s3m", ".xm",
            ".mp4", ".mov", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".wmv", ".ogv", ".vp8",
            ".ttf", ".otf", ".ttc", ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb", ".dxf",
            ".dll", ".so", ".dylib", ".a", ".lib", ".pdb", ".mdb", ".exe", ".zip", ".7z", ".rar", ".unitypackage", ".gz",
        };

        // Importers whose import depends only on the file and its .meta, never on a GUID named inside the file.
        private static readonly HashSet<string> NoInnerReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "TextureImporter", "IHVImageFormatImporter", "AudioImporter", "VideoClipImporter", "TrueTypeFontImporter", "PluginImporter",
            "ModelImporter", "DefaultImporter", "TextScriptImporter", "MonoImporter", "AssemblyDefinitionImporter",
            "AssemblyDefinitionReferenceImporter", "PackageManifestImporter",
        };

        // Of those, the ones Unity's dependency list says nothing more about than their .meta.
        private static readonly HashSet<string> NoDependencies = new HashSet<string>(StringComparer.Ordinal)
        {
            "TextureImporter", "IHVImageFormatImporter", "AudioImporter", "VideoClipImporter", "TrueTypeFontImporter", "PluginImporter",
            "DefaultImporter", "TextScriptImporter", "MonoImporter",
        };

        // Binary assets only Unity's own bakes write, which Unity imports as it writes them: their references are taken
        // from the import even before an import stamp exists for them.
        private static readonly HashSet<string> BakedOnly = new HashSet<string>(StringComparer.Ordinal) { "LightingDataAsset", "NavMeshData" };

        // The main-thread part of reading a file: Unity's dependencies, and whether a file not read as text is still as
        // Unity imported it.
        private void Add(FileRead read)
        {
            if (read.Failure != null) throw new UncertainReferencesException(read.Path + " could not be read: " + read.Failure);
            var importer = read.Imported ? AssetDatabase.GetImporterType(read.Path) : null;
            string guid = read.Imported ? AssetDatabase.AssetPathToGUID(read.Path) : null;
            if (importer != null && !NoDependencies.Contains(importer.Name))
                foreach (string dependency in AssetDatabase.GetDependencies(read.Path, false))
                {
                    if (string.Equals(dependency, read.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    string used = AssetDatabase.AssetPathToGUID(dependency);
                    if (!string.IsNullOrEmpty(used)) read.Guids.Add(used);
                }
            if (!read.ContentRead && read.Stamp.Size >= 0)
            {
                // A file not yet imported keeps the importer its kind gets by default.
                bool plain = importer != null ? NoInnerReferences.Contains(importer.Name) : Media.Contains(Path.GetExtension(read.Path));
                if (!plain && !ImportStamps.Current(read.Path, read.Stamp) && !BakedOnly.Contains(AssetDatabase.GetMainAssetTypeAtPath(read.Path)?.Name ?? ""))
                    throw new UncertainReferencesException(importer == null
                        ? read.Path + " is in a binary format Unity has not imported: what it uses cannot be read."
                        : read.Path + " changed since Unity last imported it: what it uses is known once Unity imports it again.");
            }
            if (!string.IsNullOrEmpty(guid)) read.Guids.Remove(guid);
            files[read.Path] = read;
            foreach (string used in read.Guids)
            {
                if (!usedBy.TryGetValue(used, out var users)) usedBy[used] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                users.Add(read.Path);
            }
        }

        /// <summary>Every file of the roots with its .meta and stamp, as Unity sees them (hidden and "~" names left out).</summary>
        internal sealed class Listing
        {
            internal readonly struct Entry
            {
                public readonly string Path;
                public readonly FileSystemInfo File;
                public readonly FileInfo Meta;
                public readonly bool Imported;
                public readonly Stamp Stamp;
                public Entry(string path, FileSystemInfo file, FileInfo meta, bool imported)
                {
                    Path = path; File = file; Meta = meta; Imported = imported; Stamp = Stamp.Of(file, meta);
                }
            }

            private const int MaxDepth = 64;
            public readonly List<Entry> Entries = new List<Entry>();
            public string Uncertain;

            // Unity leaves these out of the project.
            internal static bool Ignored(string name) =>
                name.Length == 0 || name[0] == '.' || name[name.Length - 1] == '~' || string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

            public static Listing Of(IEnumerable<Root> roots, CancellationToken cancellation)
            {
                var listing = new Listing();
                foreach (var root in roots)
                {
                    var top = new DirectoryInfo(root.Folder);
                    if (!top.Exists) continue;
                    var pending = new Stack<(DirectoryInfo Folder, string Path, int Depth)>();
                    pending.Push((top, root.Prefix, 0));
                    while (pending.Count > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var (folder, path, depth) = pending.Pop();
                        if (depth > MaxDepth) { listing.Uncertain = "Folders nest too deep at " + path + " to be checked."; return listing; }
                        FileSystemInfo[] children;
                        try { children = folder.GetFileSystemInfos(); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        {
                            listing.Uncertain = path + " could not be listed: " + ex.Message;
                            return listing;
                        }
                        var byName = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
                        foreach (var child in children) byName[child.Name] = child;
                        foreach (var child in children)
                        {
                            string name = child.Name;
                            bool isMeta = name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && child is FileInfo;
                            string owner = isMeta ? name.Substring(0, name.Length - 5) : name;
                            if (Ignored(owner)) continue;
                            if (isMeta)
                            {
                                // A folder's .meta, or one whose file is gone: still read, under its asset's name.
                                if (byName.TryGetValue(owner, out var asset) && asset is FileInfo) continue;
                                // A folder's own write time moves with its children: only its .meta is stamped.
                                listing.Entries.Add(new Entry(path + "/" + owner, null, (FileInfo)child, root.Imported));
                            }
                            else if (child is DirectoryInfo sub) pending.Push((sub, path + "/" + name, depth + 1));
                            else listing.Entries.Add(new Entry(path + "/" + name, child, byName.TryGetValue(name + ".meta", out var meta) ? meta as FileInfo : null, root.Imported));
                        }
                    }
                }
                return listing;
            }
        }

        /// <summary>Lists and reads every file on a worker thread: no Unity call.</summary>
        internal sealed class Reader
        {
            private const int Chunk = 64 * 1024;
            private readonly List<Root> roots;
            private readonly CancellationToken cancellation;
            private int read, found;

            public Reader(List<Root> roots, CancellationToken cancellation) { this.roots = roots; this.cancellation = cancellation; }

            public int Read => Volatile.Read(ref read);
            public int Found => Volatile.Read(ref found);
            public float Fraction => Found == 0 ? 0f : Mathf.Clamp01((float)Read / Found);

            public List<FileRead> Run()
            {
                var listing = Listing.Of(roots, cancellation);
                if (listing.Uncertain != null) throw new UncertainReferencesException(listing.Uncertain);
                Volatile.Write(ref found, listing.Entries.Count);
                var result = new List<FileRead>(listing.Entries.Count);
                foreach (var entry in listing.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var one = ReadOne(entry, cancellation);
                    if (one != null) result.Add(one);
                    Interlocked.Increment(ref read);
                }
                return result;
            }

            /// <summary>The GUIDs a file's text and its .meta name; null when the file went away meanwhile.</summary>
            public static FileRead ReadOne(Listing.Entry entry, CancellationToken cancellation)
            {
                var result = new FileRead { Path = entry.Path, Imported = entry.Imported, Stamp = entry.Stamp, ContentRead = true };
                try
                {
                    if (entry.Meta != null) Tokens(entry.Meta.FullName, result.Guids, false, cancellation);
                    if (entry.File is FileInfo file && file.Exists)
                        result.ContentRead = !Media.Contains(file.Extension) && Tokens(file.FullName, result.Guids, true, cancellation);
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) { return null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { result.Failure = ex.Message; }
                return result;
            }

            /// <summary>
            /// Adds every run of exactly 32 hexadecimal digits in the file (a GUID, as YAML, JSON, UXML, USS or code writes
            /// it), lower case. With <paramref name="binaryStops"/>, a NUL byte in the first 8 KB means the file is not text:
            /// false, and nothing more is read.
            /// </summary>
            internal static bool Tokens(string fullPath, HashSet<string> into, bool binaryStops, CancellationToken cancellation)
            {
                var buffer = new byte[Chunk];
                var run = new char[32];
                int length = 0;
                long offset = 0;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk))
                {
                    for (int count; (count = stream.Read(buffer, 0, buffer.Length)) > 0; offset += count)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int i = 0; i < count; i++)
                        {
                            byte b = buffer[i];
                            if (b == 0 && binaryStops && offset + i < 8192) return false;
                            char c = (char)b;
                            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                            if (hex)
                            {
                                if (length < 32) run[length] = char.ToLowerInvariant(c);
                                length++;
                                continue;
                            }
                            if (length == 32) into.Add(new string(run));
                            length = 0;
                        }
                    }
                }
                if (length == 32) into.Add(new string(run));
                return true;
            }
        }
    }
}
