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

        private static string ProjectFolder => Storage.ProjectPaths.Root;

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
            // The GUIDs its text and its .meta name (a function of their bytes alone).
            public readonly HashSet<string> Guids = new HashSet<string>(StringComparer.Ordinal);
            // Unity's part (Add): its importer's name, its own GUID, the GUIDs of its direct dependencies and the dependency
            // hash they were asked under. From the kept scan, reused while that hash stands; asked again otherwise.
            public string Importer, OwnGuid, DependencyHash;
            public string[] Dependencies;
            public bool UnityKnown;
            // Everything it uses: Guids and Dependencies, less its own GUID (what usedBy holds for it).
            public HashSet<string> Uses;
            // Taken from the kept scan, with the listing entry it stands for (to read it again when Unity's hash moved).
            public bool FromKept;
            public Listing.Entry Entry;

            /// <summary>A copy of a kept reading for a new scan (Unity's part still checked against the dependency hash).</summary>
            public FileRead Reuse(Listing.Entry entry)
            {
                var copy = new FileRead
                {
                    Path = Path, Imported = Imported, Stamp = Stamp, ContentRead = ContentRead, Importer = Importer, OwnGuid = OwnGuid,
                    DependencyHash = DependencyHash, Dependencies = Dependencies, UnityKnown = UnityKnown, FromKept = true, Entry = entry,
                };
                copy.Guids.UnionWith(Guids);
                return copy;
            }
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

        // The extensions of Unity's own serialized files: assets, prefabs, scenes and the kinds of asset with an extension of
        // their own. Only these name assets in their bytes when they are binary.
        private static readonly HashSet<string> UnitySerializedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".unity", ".mat", ".anim", ".controller", ".overrideController", ".mask", ".playable", ".physicMaterial",
            ".physicsMaterial2D", ".lighting", ".spriteatlas", ".spriteatlasv2", ".terrainlayer", ".mixer", ".renderTexture", ".cubemap",
            ".flare", ".fontsettings", ".guiskin", ".giparams", ".brush", ".preset", ".signal", ".shadervariants", ".scenetemplate",
        };

        // Marks, in a reading's importer name, an importer a tool wrote (a ScriptedImporter): its files are in that tool's
        // own format.
        private const string ScriptedImporterPrefix = "ScriptedImporter:";

        // Of those, the ones Unity's dependency list says nothing more about than their .meta.
        private static readonly HashSet<string> NoDependencies = new HashSet<string>(StringComparer.Ordinal)
        {
            "TextureImporter", "IHVImageFormatImporter", "AudioImporter", "VideoClipImporter", "TrueTypeFontImporter", "PluginImporter",
            "DefaultImporter", "TextScriptImporter", "MonoImporter",
        };

        // The main-thread part of reading a file: Unity's dependencies, and whether a file not read as text is still as
        // Unity imported it.
        private void Add(FileRead read)
        {
            if (read.Failure != null) throw new UncertainReferencesException(read.Path + " could not be read: " + read.Failure);
            if (read.Imported)
            {
                // Unity's answers stand while the file's dependency hash does (its import and its importer's inputs). A hash
                // that cannot be had means asking Unity again, never reusing; one that moved since the kept reading also
                // reads the file again, even with equal stamps.
                string hash = null;
                try
                {
                    var value = AssetDatabase.GetAssetDependencyHash(read.Path);
                    if (value.isValid) hash = value.ToString();
                }
                catch (Exception ex) when (!(ex is UncertainReferencesException)) { hash = null; }
                if (read.FromKept && (hash == null || read.DependencyHash != hash))
                {
                    var fresh = Reader.ReadOne(read.Entry, CancellationToken.None);
                    Stats.Rereads++;
                    if (fresh == null) return;
                    if (fresh.Failure != null) throw new UncertainReferencesException(fresh.Path + " could not be read: " + fresh.Failure);
                    read = fresh;
                }
                if (!read.UnityKnown || hash == null || read.DependencyHash != hash)
                {
                    Stats.UnityAsked++;
                    var type = AssetDatabase.GetImporterType(read.Path);
                    read.Importer = type == null ? null
                        : (typeof(UnityEditor.AssetImporters.ScriptedImporter).IsAssignableFrom(type) ? ScriptedImporterPrefix : "") + type.Name;
                    read.OwnGuid = AssetDatabase.AssetPathToGUID(read.Path);
                    var dependencies = new List<string>();
                    if (type != null && !NoDependencies.Contains(type.Name))
                        foreach (string dependency in AssetDatabase.GetDependencies(read.Path, false))
                        {
                            if (string.Equals(dependency, read.Path, StringComparison.OrdinalIgnoreCase)) continue;
                            string used = AssetDatabase.AssetPathToGUID(dependency);
                            if (!string.IsNullOrEmpty(used)) dependencies.Add(used);
                        }
                    read.Dependencies = dependencies.ToArray();
                    read.DependencyHash = hash;
                    read.UnityKnown = true;
                }
            }
            // Files whose own content is not read: pictures, sounds, models, libraries (their importers never take a GUID
            // from inside the file; their .meta is read), and binary files. In a Force Text project (VRChat's default) Unity
            // writes few of those itself: types marked [PreferBinarySerialization] (LightingData, NavMesh data, and tools'
            // own caches), terrain data, files brought in by a .unitypackage or copied from a Force Binary project that
            // Unity has not written since. Only those can make the scan uncertain, and only while they are not as Unity
            // last imported them (checked afresh every time); a binary file a tool's own importer reads never does.
            if (!read.ContentRead && read.Stamp.Size >= 0)
            {
                // A file not yet imported keeps the importer its kind gets by default.
                bool plain = !string.IsNullOrEmpty(read.Importer) ? NoInnerReferences.Contains(read.Importer) : Media.Contains(Path.GetExtension(read.Path));
                // A binary file read by a tool's own importer (a Substance Painter project, a VRM) is in that tool's format:
                // it names no asset in its bytes, and what its import uses comes from Unity's dependencies and its .meta.
                bool foreign = read.Importer != null && read.Importer.StartsWith(ScriptedImporterPrefix, StringComparison.Ordinal);
                // Unity's own serialized files are told by their extension: Unity names the importer of an asset and of a
                // file it has no importer for alike, so the importer's name cannot tell a binary asset from a Substance
                // Painter project. Only a serialized file whose bytes are not known to be what Unity imported may use what
                // its import does not say.
                bool serialized = UnitySerializedExtensions.Contains(Path.GetExtension(read.Path));
                if (serialized && !plain && !foreign && !ImportStamps.Current(read.Path, read.Stamp))
                    throw new UncertainReferencesException(string.IsNullOrEmpty(read.Importer)
                        ? read.Path + " is in a binary format Unity has not imported: what it uses cannot be read."
                        : read.Path + " changed since Unity last imported it: what it uses is known once Unity imports it again.");
            }
            var uses = new HashSet<string>(read.Guids, StringComparer.Ordinal);
            if (read.Dependencies != null) uses.UnionWith(read.Dependencies);
            if (!string.IsNullOrEmpty(read.OwnGuid)) uses.Remove(read.OwnGuid);
            read.Uses = uses;
            files[read.Path] = read;
            foreach (string used in uses)
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

            public static Listing Of(IEnumerable<Root> roots, CancellationToken cancellation) => Of(roots, cancellation, double.PositiveInfinity, null);

            /// <summary>The listing, or Uncertain once it took longer than <paramref name="limitMilliseconds"/> on <paramref name="clock"/>.</summary>
            public static Listing Of(IEnumerable<Root> roots, CancellationToken cancellation, double limitMilliseconds, System.Diagnostics.Stopwatch clock)
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
                        if (clock != null && clock.Elapsed.TotalMilliseconds > limitMilliseconds) { listing.Uncertain = "Listing the project's files again took too long."; return listing; }
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

        // Kinds that are text: one that does not read as text is an encoding the scan cannot decode.
        private static readonly HashSet<string> TextKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".xml", ".yaml", ".yml", ".csv", ".md", ".html", ".htm", ".cs", ".uxml", ".uss", ".tss", ".shader", ".cginc", ".hlsl",
            ".asmdef", ".asmref",
        };

        /// <summary>Lists and reads every file on a worker thread: no Unity call.</summary>
        internal sealed class Reader
        {
            private const int Chunk = 1024 * 1024;
            private readonly List<Root> roots;
            private readonly CancellationToken cancellation;
            // The kept scan's results by path (read only here): a file whose stamp equals its entry's is not read again.
            internal Dictionary<string, FileRead> Kept;
            private int read, found;

            public Reader(List<Root> roots, CancellationToken cancellation) { this.roots = roots; this.cancellation = cancellation; }

            public ScanStats Stats = new ScanStats();

            public int Read => Volatile.Read(ref read);
            public int Found => Volatile.Read(ref found);
            public float Fraction => Found == 0 ? 0f : Mathf.Clamp01((float)Read / Found);

            public List<FileRead> Run()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var listing = Listing.Of(roots, cancellation);
                if (listing.Uncertain != null) throw new UncertainReferencesException(listing.Uncertain);
                Stats.Listed = listing.Entries.Count;
                Stats.ListingMilliseconds = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                Volatile.Write(ref found, listing.Entries.Count);
                var result = new List<FileRead>(listing.Entries.Count);
                foreach (var entry in listing.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    FileRead one;
                    // Same path, same size and write time of the file and of its .meta, same reader (the kept file's
                    // version): the same bytes as far as Unity itself can tell, so the same GUIDs.
                    if (Kept != null && Kept.TryGetValue(entry.Path, out var known) && known.Stamp.Equals(entry.Stamp) && known.Imported == entry.Imported)
                    {
                        one = known.Reuse(entry);
                        Stats.FromKept++;
                    }
                    else
                    {
                        one = ReadOne(entry, cancellation);
                        Stats.Read++;
                        Stats.BytesRead += Math.Max(0, entry.Stamp.Size) + Math.Max(0, entry.Stamp.MetaSize);
                        Stats.ReadPaths.Add(entry.Path);
                    }
                    if (one != null) result.Add(one);
                    Interlocked.Increment(ref read);
                }
                Stats.ReadingMilliseconds = clock.Elapsed.TotalMilliseconds;
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
                    {
                        result.ContentRead = !Media.Contains(file.Extension) && Tokens(file.FullName, result.Guids, true, cancellation);
                        // A text file that is not text as read (UTF-16 or UTF-32 without a byte order mark, say) may name GUIDs
                        // the scan cannot see: not "names nothing".
                        if (!result.ContentRead && TextKinds.Contains(file.Extension)) result.Failure = "it is text in an encoding that cannot be told";
                    }
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
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk))
                {
                    var encoding = Marked(stream);
                    if (encoding != null) return DecodedTokens(stream, encoding, into, cancellation);
                    var buffer = new byte[Chunk];
                    // Each run of digits accumulates into two 64-bit halves: a GUID repeated thousands of times in a scene
                    // costs a set lookup, not a string, and the long digit runs of a text-serialized mesh cost nothing.
                    var seen = new HashSet<(ulong, ulong)>();
                    ulong high = 0, low = 0;
                    int length = 0;
                    long offset = 0;
                    for (int count; (count = stream.Read(buffer, 0, buffer.Length)) > 0; offset += count)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (binaryStops && offset < 8192)
                            for (int i = 0, end = (int)Math.Min(count, 8192 - offset); i < end; i++)
                                if (buffer[i] == 0) return false;
                        for (int i = 0; i < count; i++)
                        {
                            int digit = Digit(buffer[i]);
                            if (digit >= 0)
                            {
                                if (length < 16) high = (high << 4) | (uint)digit;
                                else if (length < 32) low = (low << 4) | (uint)digit;
                                length++;
                                continue;
                            }
                            if (length == 32) seen.Add((high, low));
                            length = 0; high = 0; low = 0;
                        }
                    }
                    if (length == 32) seen.Add((high, low));
                    foreach (var (h, l) in seen) into.Add(h.ToString("x16") + l.ToString("x16"));
                    return true;
                }
            }

            // A hexadecimal digit's value, or -1.
            private static int Digit(byte b)
            {
                if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
                if (b >= (byte)'a' && b <= (byte)'f') return b - 'a' + 10;
                if (b >= (byte)'A' && b <= (byte)'F') return b - 'A' + 10;
                return -1;
            }

            // A digit of a run: kept (lower case) while the run is at most 32 long.
            private static bool Hex(char c, char[] run, ref int length)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
                if (length < 32) run[length] = char.ToLowerInvariant(c);
                length++;
                return true;
            }

            // The encoding a byte order mark names, the stream left after it; null (stream at its start) for none or UTF-8's
            // (UTF-8 is read as bytes: a GUID's digits are the same bytes).
            private static System.Text.Encoding Marked(FileStream stream)
            {
                var start = new byte[4];
                int n = stream.Read(start, 0, 4);
                System.Text.Encoding encoding = null;
                int skip = 0;
                if (n >= 4 && start[0] == 0xFF && start[1] == 0xFE && start[2] == 0 && start[3] == 0) { encoding = new System.Text.UTF32Encoding(false, false); skip = 4; }
                else if (n >= 4 && start[0] == 0 && start[1] == 0 && start[2] == 0xFE && start[3] == 0xFF) { encoding = new System.Text.UTF32Encoding(true, false); skip = 4; }
                else if (n >= 2 && start[0] == 0xFF && start[1] == 0xFE) { encoding = new System.Text.UnicodeEncoding(false, false); skip = 2; }
                else if (n >= 2 && start[0] == 0xFE && start[1] == 0xFF) { encoding = new System.Text.UnicodeEncoding(true, false); skip = 2; }
                stream.Position = encoding != null ? skip : 0;
                return encoding;
            }

            private static bool DecodedTokens(FileStream stream, System.Text.Encoding encoding, HashSet<string> into, CancellationToken cancellation)
            {
                var run = new char[32];
                int length = 0;
                var buffer = new char[Chunk];
                using (var reader = new StreamReader(stream, encoding, false, Chunk, true))
                    for (int count; (count = reader.Read(buffer, 0, buffer.Length)) > 0;)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int i = 0; i < count; i++)
                        {
                            if (Hex(buffer[i], run, ref length)) continue;
                            if (length == 32) into.Add(new string(run));
                            length = 0;
                        }
                    }
                if (length == 32) into.Add(new string(run));
                return true;
            }
        }
    }
}
