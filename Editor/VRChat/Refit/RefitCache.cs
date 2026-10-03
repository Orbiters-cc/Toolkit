using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// Refits are slow; the same mesh, placed the same way on the same body with the same shapes and settings, gives the same
    /// result. Putting an outfit on a second avatar with the same custom base reuses the first result instead of computing it.
    /// </summary>
    internal static class RefitCache
    {
        internal const string Folder = "Assets/Orbiters/ReFit/Cache";
        // Changes when entries keep something new: older entries are then not found and made again.
        private const string Format = "3";

        /// <summary>What decides the result, hashed; null when an input is not a saved asset (nothing to find it again by).</summary>
        internal static string Key(RefitJob job, Transform avatarRoot, string engine)
        {
            var renderer = job.Renderer;
            if (renderer == null || job.Body == null || avatarRoot == null) return null;
            string mesh = Identity(renderer.sharedMesh), body = Identity(job.Body.sharedMesh);
            if (mesh == null || body == null) return null;
            var text = new StringBuilder();
            text.Append(Format).Append('|').Append(engine).Append('|').Append(job.Mode).Append('|').Append(Mathf.RoundToInt(job.Tightness * 100)).Append('|');
            text.Append(job.CoverDifferentBaseBody).Append('|');
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

        internal static RefitCacheEntry Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var entry = AssetDatabase.LoadAssetAtPath<RefitCacheEntry>(Folder + "/" + key + ".asset");
            if (entry == null) return null;
            // The mesh was deleted: forget the entry.
            if (entry.mesh == null) { AssetDatabase.DeleteAsset(Folder + "/" + key + ".asset"); return null; }
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
                EnsureFolder();
                string path = Folder + "/" + key + ".asset";
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
                return path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Debug.LogWarning("[Orbiters] Could not keep this refit for reuse: " + ex.Message);
                return null;
            }
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

        private static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(Folder)) return;
            string parent = "Assets";
            foreach (string part in Folder.Substring("Assets/".Length).Split('/'))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, part);
                parent = next;
            }
        }
    }
}
