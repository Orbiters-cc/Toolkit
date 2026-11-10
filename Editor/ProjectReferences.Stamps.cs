using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Experimental;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Orbiters.Toolkit.Editor
{
    public sealed partial class ProjectReferences
    {
        /// <summary>
        /// Each imported file's stamp (size and write time of the file and its .meta) as it was when Unity imported it, with
        /// Unity's dependency hash then, kept in Library/Orbiters across sessions. A file whose content the scan cannot read
        /// is trusted only while it is exactly as Unity imported it: an edit on disk that Unity has not imported yet makes
        /// Unity's dependencies out of date.
        /// </summary>
        internal static class ImportStamps
        {
            private const int Schema = 1;
            private static Dictionary<string, (Stamp Stamp, string Hash)> entries;
            private static bool dirty, saving;
            private static string overridePath;

            private static string FilePath => overridePath ?? Path.Combine(ProjectFolder, "Library", "Orbiters", "ImportStamps.bin");

            // Kinds the scan always reads as text: never needed, never kept.
            private static readonly HashSet<string> Text = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".txt", ".json", ".md", ".xml", ".yaml", ".yml", ".html", ".htm", ".py", ".csv", ".shader", ".cginc", ".hlsl",
                ".compute", ".uss", ".uxml", ".tss", ".asmdef", ".asmref", ".rsp", ".meta",
            };

            /// <summary>
            /// True when <paramref name="path"/> is as Unity last imported it: its stamp equals the one recorded at its last
            /// import (and Unity's dependency hash is the one then), or, for a file imported before any was recorded, Unity's
            /// import result was written after the file and its .meta last were.
            /// </summary>
            public static bool Current(string path, Stamp stamp)
            {
                Load();
                string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                if (entries.TryGetValue(path, out var known)) return known.Stamp.Equals(stamp) && known.Hash == hash;
                return ImportedAfter(path, stamp);
            }

            // Unity's import result (in Library/Artifacts) is newer than the file and its .meta.
            private static bool ImportedAfter(string path, Stamp stamp)
            {
                try
                {
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) return false;
                    var artifact = AssetDatabaseExperimental.LookupArtifact(new ArtifactKey(new GUID(guid)));
                    if (!artifact.isValid || !AssetDatabaseExperimental.GetArtifactPaths(artifact, out string[] paths) || paths == null || paths.Length == 0) return false;
                    long newest = Math.Max(stamp.Ticks, stamp.MetaTicks);
                    foreach (string written in paths)
                    {
                        var info = new FileInfo(written);
                        if (!info.Exists || info.LastWriteTimeUtc.Ticks < newest) return false;
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return false; }
            }

            /// <summary>Records what Unity just imported, and forgets what it deleted or moved away.</summary>
            public static void Imported(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                bool any = false;
                foreach (string path in imported) any |= Wanted(path);
                foreach (string path in deleted) any |= Wanted(path);
                foreach (string path in movedFrom) any |= Wanted(path);
                foreach (string path in moved) any |= Wanted(path);
                if (!any) return;
                Load();
                foreach (string path in deleted) Forget(path);
                foreach (string path in movedFrom) Forget(path);
                foreach (string path in imported) Record(path);
                foreach (string path in moved) Record(path);
            }

            private static bool Wanted(string path) => !string.IsNullOrEmpty(path) && !Text.Contains(Path.GetExtension(path));

            private static void Forget(string path)
            {
                if (Wanted(path) && entries.Remove(path)) MarkDirty();
            }

            private static void Record(string path)
            {
                if (!Wanted(path)) return;
                string full = FullPath(path);
                if (full == null) return;
                var file = new FileInfo(full);
                if (!file.Exists) { Forget(path); return; }
                entries[path] = (Stamp.Of(file, new FileInfo(full + ".meta")), AssetDatabase.GetAssetDependencyHash(path).ToString());
                MarkDirty();
            }

            internal static string FullPath(string path)
            {
                if (path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var package = PackageInfo.FindForAssetPath(path);
                    if (package == null || string.IsNullOrEmpty(package.resolvedPath) || string.IsNullOrEmpty(package.assetPath)) return null;
                    return Path.Combine(package.resolvedPath, path.Substring(package.assetPath.TrimEnd('/').Length).TrimStart('/'));
                }
                return Path.Combine(ProjectFolder, path);
            }

            private static void MarkDirty()
            {
                dirty = true;
                if (saving) return;
                // Kept in memory and written before the scripts reload or the editor quits.
                saving = true;
                OrbitersEditorHost.BeforeReload += Save;
            }

            public static void Load()
            {
                if (entries != null) return;
                entries = new Dictionary<string, (Stamp, string)>(StringComparer.Ordinal);
                try
                {
                    if (!File.Exists(FilePath)) return;
                    using (var reader = new BinaryReader(File.OpenRead(FilePath)))
                    {
                        if (reader.ReadInt32() != Schema) return;
                        for (int i = 0, count = reader.ReadInt32(); i < count; i++)
                        {
                            string path = reader.ReadString();
                            var stamp = new Stamp(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                            entries[path] = (stamp, reader.ReadString());
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Nothing recorded: files the scan cannot read are trusted only through Unity's import results.
                    entries.Clear();
                }
            }

            private static void Save()
            {
                if (!dirty || entries == null) return;
                string temporary = FilePath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    using (var writer = new BinaryWriter(File.Create(temporary)))
                    {
                        writer.Write(Schema);
                        writer.Write(entries.Count);
                        foreach (var pair in entries)
                        {
                            writer.Write(pair.Key);
                            writer.Write(pair.Value.Stamp.Size); writer.Write(pair.Value.Stamp.Ticks);
                            writer.Write(pair.Value.Stamp.MetaSize); writer.Write(pair.Value.Stamp.MetaTicks);
                            writer.Write(pair.Value.Hash ?? "");
                        }
                    }
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                    else File.Move(temporary, FilePath);
                    dirty = false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Lost records only make more files untrusted until Unity imports them again.
                    Debug.LogWarning("[Orbiters] Could not save the import stamps: " + ex.Message);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }

            /// <summary>Tests: records go to <paramref name="path"/> (starting empty) until disposed.</summary>
            internal static IDisposable Use(string path)
            {
                Save();
                overridePath = Path.GetFullPath(path);
                entries = null;
                dirty = false;
                return new Scope();
            }

            private sealed class Scope : IDisposable
            {
                public void Dispose()
                {
                    overridePath = null;
                    entries = null;
                    dirty = false;
                }
            }
        }
    }

    /// <summary>Hands every import, deletion and move to <see cref="ProjectReferences.ImportStamps"/>.</summary>
    internal sealed class ProjectReferencesImports : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) =>
            ProjectReferences.ImportStamps.Imported(imported, deleted, moved, movedFrom);
    }
}
