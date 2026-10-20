using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Storage;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// Refits are slow; the same mesh, placed the same way on the same body with the same shapes and settings, gives the same
    /// result. Putting an outfit on a second avatar with the same custom base reuses the first result instead of computing it.
    /// Entries are kept per engine version (a folder each): an engine finds only its own, so once a newer version of an
    /// engine stored a result, or loaded with the editor, the older versions' entries go (the meshes they point to stay).
    /// </summary>
    internal static class RefitCache
    {
        internal const string Folder = "Assets/Orbiters/ReFit/Cache";
        // Changes when entries keep something new: older entries are then not found and made again.
        private const string Format = "3";

        /// <summary>
        /// What decides the result, hashed; null when an input is not a saved asset (nothing to find it again by).
        /// <paramref name="inputs"/>: what else the engine read from the scene for this job (<see cref="IRefitInputs"/>).
        /// </summary>
        internal static string Key(RefitJob job, Transform avatarRoot, string engine, string inputs = null)
        {
            var renderer = job.Renderer;
            if (renderer == null || job.Body == null || avatarRoot == null) return null;
            string mesh = Identity(renderer.sharedMesh), body = Identity(job.Body.sharedMesh);
            if (mesh == null || body == null) return null;
            var text = new StringBuilder();
            text.Append(Format).Append('|').Append(engine).Append('|').Append(job.Mode).Append('|').Append(Mathf.RoundToInt(job.Tightness * 100)).Append('|');
            text.Append(job.CoverDifferentBaseBody).Append('|').Append(job.MadeForAnotherBody).Append('|');
            text.Append(inputs ?? "").Append('|');
            foreach (var layer in job.CoverageLayers)
            {
                string identity = layer != null ? Identity(layer.sharedMesh) : null;
                if (identity == null) return null;
                text.Append(identity).Append('|');
                AppendPose(text, avatarRoot, layer.transform);
                foreach (var bone in layer.bones) AppendPose(text, avatarRoot, bone);
                for (int s = 0; s < layer.sharedMesh.blendShapeCount; s++) text.Append(Mathf.RoundToInt(layer.GetBlendShapeWeight(s) * 10)).Append(',');
            }
            text.Append(mesh).Append('|').Append(body).Append('|');
            if (job.Mode == RefitMode.Fit)
            {
                string source = Identity(job.SourceBody != null ? job.SourceBody.sharedMesh : null);
                if (source == null) return null;
                text.Append(source).Append('|');
                // The original body as the engine reads it: its place, its bones' pose and its shape, like the avatar's body.
                var sourceRoot = job.SourceAvatar != null ? job.SourceAvatar.transform : null;
                AppendPose(text, sourceRoot, job.SourceBody.transform);
                foreach (var bone in job.SourceBody.bones ?? Array.Empty<Transform>()) AppendPose(text, sourceRoot, bone);
                for (int i = 0; i < job.SourceBody.sharedMesh.blendShapeCount; i++)
                    text.Append(Mathf.RoundToInt(job.SourceBody.GetBlendShapeWeight(i) * 10)).Append(',');
                text.Append('|');
            }
            foreach (string shape in job.Shapes) text.Append(shape).Append(';');
            text.Append('|');
            // The pose of the mesh and the body under the avatar, and the body's shape (its blendshape weights).
            AppendPose(text, avatarRoot, renderer.transform);
            foreach (var bone in renderer.bones ?? Array.Empty<Transform>()) AppendPose(text, avatarRoot, bone);
            AppendPose(text, avatarRoot, job.Body.transform);
            foreach (var bone in job.Body.bones ?? Array.Empty<Transform>()) AppendPose(text, avatarRoot, bone);
            for (int i = 0; i < job.Body.sharedMesh.blendShapeCount; i++)
                text.Append(Mathf.RoundToInt(job.Body.GetBlendShapeWeight(i) * 10)).Append(',');
            for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                text.Append(Mathf.RoundToInt(renderer.GetBlendShapeWeight(i) * 10)).Append(',');
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The folder of one engine version's entries.</summary>
        internal static string EngineFolder(string engine) => Folder + "/" + SafePaths.FileName(string.IsNullOrEmpty(engine) ? "none" : engine, "none");

        private static string EntryPath(string key, string engine) => EngineFolder(engine) + "/" + key + ".asset";

        internal static RefitCacheEntry Find(string key, string engine)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string path = EntryPath(key, engine);
            var entry = AssetDatabase.LoadAssetAtPath<RefitCacheEntry>(path);
            if (entry == null) return null;
            // The mesh was deleted: forget the entry.
            if (entry.mesh == null) { AssetDatabase.DeleteAsset(path); return null; }
            return entry;
        }

        /// <summary>Puts a cached result on the renderer like the engine does (with Undo): its mesh, the fit shape at 100, its binding data.</summary>
        internal static RefitOutcome Apply(RefitCacheEntry entry, SkinnedMeshRenderer renderer)
        {
            Undo.RecordObject(renderer, "Refit");
            renderer.sharedMesh = entry.mesh;
            int primary = string.IsNullOrEmpty(entry.primaryShape) ? -1 : entry.mesh.GetBlendShapeIndex(entry.primaryShape);
            if (primary >= 0) renderer.SetBlendShapeWeight(primary, 100f);
            EditorUtility.SetDirty(renderer);
            if (!string.IsNullOrEmpty(entry.metadata)) RefitEngine.Current?.LoadMetadata(renderer, entry.metadata);
            return new RefitOutcome
            {
                Success = true, Mesh = entry.mesh, MeshPath = AssetDatabase.GetAssetPath(entry.mesh), PrimaryShape = entry.primaryShape,
                SourceShapes = entry.shapes.Select(s => s.source).ToArray(), GeneratedShapes = entry.shapes.Select(s => s.generated).ToArray(),
                Messages = entry.messages?.ToList() ?? new List<RefitMessage>(),
            };
        }

        /// <summary>Keeps a successful result under its key; returns the entry's path, or null when it could not be saved.</summary>
        internal static string Store(string key, RefitOutcome outcome, SkinnedMeshRenderer renderer, string engine)
        {
            if (string.IsNullOrEmpty(key) || outcome?.Mesh == null || !EditorUtility.IsPersistent(outcome.Mesh)) return null;
            try
            {
                EnsureFolder(EngineFolder(engine));
                string path = EntryPath(key, engine);
                var entry = AssetDatabase.LoadAssetAtPath<RefitCacheEntry>(path);
                bool created = entry == null;
                if (created) entry = ScriptableObject.CreateInstance<RefitCacheEntry>();
                entry.mesh = outcome.Mesh;
                entry.primaryShape = outcome.PrimaryShape;
                entry.shapes = Pairs(outcome).ToList();
                entry.metadata = RefitEngine.Current?.SaveMetadata(renderer);
                entry.messages = outcome.Messages.Where(m => m.Severity != RefitSeverity.Info).ToList();
                entry.engine = engine;
                if (created) AssetDatabase.CreateAsset(entry, path);
                else EditorUtility.SetDirty(entry);
                AssetDatabase.SaveAssetIfDirty(entry);
                Prune(engine);
                return path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Debug.LogWarning("[Orbiters] Could not keep this refit for reuse: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Removes the entries no engine finds any more: the folders of the other versions of <paramref name="engine"/> (same
        /// name up to its first space: "ReFit 0.7.1 r10" for "ReFit 0.7.2 r11") and, with <paramref name="legacy"/>, the entries
        /// of the layout before engine folders (directly in <paramref name="folder"/>; other assets there stay). Other engines'
        /// entries stay; the meshes entries point to are never touched. Returns the paths removed.
        /// </summary>
        internal static List<string> Prune(string engine, string folder = Folder, bool legacy = false)
        {
            var removed = new List<string>();
            if (string.IsNullOrEmpty(engine) || !AssetDatabase.IsValidFolder(folder)) return removed;
            string root = Path.GetDirectoryName(Application.dataPath);
            string own = SafePaths.FileName(engine, "none"), product = Product(engine);
            try
            {
                // Only cache entries: whatever else a user or another tool keeps there stays.
                if (legacy)
                    foreach (string file in Directory.GetFiles(Path.Combine(root, folder), "*.asset", SearchOption.TopDirectoryOnly))
                    {
                        string path = folder + "/" + Path.GetFileName(file);
                        if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(RefitCacheEntry)) removed.Add(path);
                    }
                foreach (string directory in Directory.GetDirectories(Path.Combine(root, folder)))
                {
                    string name = Path.GetFileName(directory);
                    if (name != own && Product(name) == product) removed.Add(folder + "/" + name);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new List<string>(); }
            if (removed.Count == 0) return removed;
            var failed = new List<string>();
            AssetDatabase.DeleteAssets(removed.ToArray(), failed);
            removed.RemoveAll(failed.Contains);
            if (removed.Count > 0) Debug.Log($"[Orbiters] Removed {removed.Count} refit cache entries (or folders) no engine finds any more.");
            return removed;
        }

        // An engine's name up to its version: "ReFit" of "ReFit 0.7.2 r11".
        private static string Product(string engine)
        {
            int space = engine.IndexOf(' ');
            return space > 0 ? engine.Substring(0, space) : engine;
        }

        // Once the editor has loaded (the engine registered with it): the entries of the engine's earlier versions go, and
        // those of the layout before engine folders.
        [InitializeOnLoadMethod]
        private static void PruneAfterLoad()
        {
            if (Application.isBatchMode) return;
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                EditorApplication.update -= once;
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                string engine = RefitEngine.Current?.Name;
                try { if (!string.IsNullOrEmpty(engine)) Prune(engine, legacy: true); }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] Could not clean the refit cache: " + ex.Message); }
            };
            EditorApplication.update += once;
        }

        internal static IEnumerable<RefitShape> Pairs(RefitOutcome outcome)
        {
            int count = Math.Min(outcome.SourceShapes?.Length ?? 0, outcome.GeneratedShapes?.Length ?? 0);
            for (int i = 0; i < count; i++)
                if (!string.IsNullOrEmpty(outcome.SourceShapes[i]) && !string.IsNullOrEmpty(outcome.GeneratedShapes[i]))
                    yield return new RefitShape(outcome.SourceShapes[i], outcome.GeneratedShapes[i]);
        }

        // An asset's GUID and file ID, with its dependency hash: reimporting the model changes it.
        private static string Identity(Object asset)
        {
            if (asset == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id)) return null;
            return guid + ":" + id + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GUIDToAssetPath(guid));
        }

        // Relative matrix rounded to 0.1 mm (and the same step for rotation and scale terms).
        private static void AppendPose(StringBuilder text, Transform root, Transform transform)
        {
            if (transform == null) { text.Append("-;"); return; }
            var matrix = root != null ? root.worldToLocalMatrix * transform.localToWorldMatrix : transform.localToWorldMatrix;
            for (int i = 0; i < 12; i++) text.Append(Mathf.RoundToInt(matrix[i % 3, i / 3] * 10000f)).Append(',');
            text.Append(';');
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = "Assets";
            foreach (string part in folder.Substring("Assets/".Length).Split('/'))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
        }
    }
}
