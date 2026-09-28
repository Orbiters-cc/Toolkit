using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    /// <summary>The destination shape follows the source shape.</summary>
    public struct BlendShapeCopy
    {
        public SkinnedMeshRenderer Source;
        public string SourceShape;
        public SkinnedMeshRenderer Destination;
        public string DestinationShape;
    }

    public struct BlendShapeSyncResult
    {
        /// <summary>False only when nothing could be processed (no avatar root or no valid copy).</summary>
        public bool Success;
        public int WeightsCopied;
        /// <summary>Copies left out because a renderer or shape is missing or outside the avatar.</summary>
        public int Skipped;
        /// <summary>False when VRCFury built no controllers: only the weights were copied.</summary>
        public bool AnimationsLinked;
        public BlendShapeLinkResult Links;
        public string Message;
    }

    /// <summary>Keeps accessory blendshapes in sync with the body: same weights, and the same animations after VRCFury built the avatar.</summary>
    public static class BlendShapeSync
    {
        /// <summary>Every destination shape with a source shape of the same name, else of the same normalized name (unambiguous only).</summary>
        public static List<BlendShapeCopy> Plan(SkinnedMeshRenderer source, SkinnedMeshRenderer destination)
        {
            var copies = new List<BlendShapeCopy>();
            var sourceMesh = source != null ? source.sharedMesh : null;
            var destinationMesh = destination != null ? destination.sharedMesh : null;
            if (sourceMesh == null || destinationMesh == null || source == destination) return copies;
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sourceMesh.blendShapeCount; i++)
            {
                string name = sourceMesh.GetBlendShapeName(i), key = NormalizeShapeName(name);
                if (key.Length == 0) continue;
                if (normalized.ContainsKey(key)) ambiguous.Add(key);
                else normalized[key] = name;
            }
            for (int i = 0; i < destinationMesh.blendShapeCount; i++)
            {
                string name = destinationMesh.GetBlendShapeName(i), match = null;
                if (sourceMesh.GetBlendShapeIndex(name) >= 0) match = name;
                else
                {
                    string key = NormalizeShapeName(name);
                    if (!ambiguous.Contains(key)) normalized.TryGetValue(key, out match);
                }
                if (match != null) copies.Add(new BlendShapeCopy { Source = source, SourceShape = match, Destination = destination, DestinationShape = name });
            }
            return copies;
        }

        /// <summary>Lowercase letters and digits only: "Breasts_Big", "breasts big" and "BreastsBig" are one shape.</summary>
        public static string NormalizeShapeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>Copies the current source weights; returns how many destination weights changed.</summary>
        public static int CopyWeights(IReadOnlyList<BlendShapeCopy> copies, bool recordUndo)
        {
            if (copies == null) return 0;
            var recorded = new HashSet<SkinnedMeshRenderer>();
            int changed = 0;
            foreach (var copy in copies)
            {
                if (!TryIndices(copy, out int sourceIndex, out int destinationIndex)) continue;
                float weight = copy.Source.GetBlendShapeWeight(sourceIndex);
                if (Mathf.Approximately(copy.Destination.GetBlendShapeWeight(destinationIndex), weight)) continue;
                if (recordUndo && recorded.Add(copy.Destination)) Undo.RecordObject(copy.Destination, "Sync Blendshapes");
                copy.Destination.SetBlendShapeWeight(destinationIndex, weight);
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// Build time, after VRCFury (-10000) built its controllers, on the build copy: copies the weights and makes every
        /// animation of a source shape also animate its destination shape (direct copy, no factor), in VRCFury-built
        /// controllers only. Without such controllers only the weights are copied.
        /// </summary>
        public static BlendShapeSyncResult Apply(GameObject avatarRoot, IReadOnlyList<BlendShapeCopy> copies, string label)
        {
            if (avatarRoot == null) return new BlendShapeSyncResult { Message = "Avatar root is null." };
            var valid = (copies ?? Array.Empty<BlendShapeCopy>())
                .Where(c => TryIndices(c, out _, out _) && c.Source.transform.IsChildOf(avatarRoot.transform) && c.Destination.transform.IsChildOf(avatarRoot.transform))
                .ToList();
            int skipped = (copies?.Count ?? 0) - valid.Count;
            if (valid.Count == 0)
                return new BlendShapeSyncResult { Skipped = skipped, Message = $"{label}: no blendshape to sync ({skipped} skipped)." };

            var result = new BlendShapeSyncResult { Success = true, Skipped = skipped, WeightsCopied = CopyWeights(valid, false) };
            var controllers = BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot);
            if (controllers.Count == 0)
            {
                result.Message = $"{label}: no VRCFury-built controllers, copied {result.WeightsCopied} blendshape weight(s) only.";
                return result;
            }

            // Only shapes some clip animates: linking the others would walk every controller for nothing.
            var animated = new HashSet<(string, string)>();
            foreach (var clip in controllers.SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.type == typeof(SkinnedMeshRenderer)) animated.Add((binding.path, binding.propertyName));
            var links = new List<BlendShapeLink>();
            foreach (var copy in valid)
            {
                string sourcePath = AnimationUtility.CalculateTransformPath(copy.Source.transform, avatarRoot.transform);
                if (!animated.Contains((sourcePath, "blendShape." + copy.SourceShape))) continue;
                links.Add(BlendShapeLink.Copy(sourcePath, copy.SourceShape,
                    AnimationUtility.CalculateTransformPath(copy.Destination.transform, avatarRoot.transform), copy.DestinationShape));
            }
            result.AnimationsLinked = true;
            if (links.Count == 0)
            {
                result.Message = $"{label}: copied {result.WeightsCopied} blendshape weight(s); no source shape is animated.";
                return result;
            }
            result.Links = BlendShapeLinkEngine.Apply(avatarRoot, links, label);
            result.Message = $"{label}: copied {result.WeightsCopied} blendshape weight(s); " + result.Links.Message;
            return result;
        }

        private static bool TryIndices(BlendShapeCopy copy, out int sourceIndex, out int destinationIndex)
        {
            sourceIndex = destinationIndex = -1;
            if (copy.Source == null || copy.Destination == null || copy.Source.sharedMesh == null || copy.Destination.sharedMesh == null ||
                string.IsNullOrEmpty(copy.SourceShape) || string.IsNullOrEmpty(copy.DestinationShape)) return false;
            sourceIndex = copy.Source.sharedMesh.GetBlendShapeIndex(copy.SourceShape);
            destinationIndex = copy.Destination.sharedMesh.GetBlendShapeIndex(copy.DestinationShape);
            return sourceIndex >= 0 && destinationIndex >= 0;
        }
    }
}
