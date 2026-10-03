using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    public enum FitAdvice
    {
        /// <summary>Nothing to do: no custom blendshape moves the body near the item, or it already has them.</summary>
        None,
        /// <summary>The item fits: add the body's blendshapes it lacks (its creator's own shapes stay).</summary>
        AddShapes,
        /// <summary>Unknown: ask whether it fits the custom body, then add shapes or refit it from the original base.</summary>
        AskFit,
        /// <summary>Its creator says it was made for the original base: refit it to the custom base.</summary>
        Refit,
    }

    public sealed class FitSuggestion
    {
        public FitAdvice Advice;
        public CustomBaseInfo Base;
        /// <summary>For each mesh of the item, the custom blendshapes it lacks among those moving the body near it.</summary>
        public readonly Dictionary<SkinnedMeshRenderer, List<string>> Missing = new Dictionary<SkinnedMeshRenderer, List<string>>();
        /// <summary>The item already has some of the custom base's blendshapes: its creator adapted it.</summary>
        public bool CreatorAdapted;
        /// <summary>Its creator marked it as made for the original base (<see cref="OrbitersFitInfo"/>).</summary>
        public bool MadeForOriginal;

        public int ShapeCount => Missing.Values.SelectMany(s => s).Distinct().Count();
        public List<SkinnedMeshRenderer> Meshes => Missing.Keys.ToList();
    }

    /// <summary>
    /// Whether an item put on an avatar with a known custom base needs anything: only the item's skinned meshes close to skin
    /// a custom blendshape moves count; rigid pieces and far away items are left alone, without asking.
    /// </summary>
    public static class FitCheck
    {
        public static async Task<FitSuggestion> CheckAsync(GameObject item, CustomBaseState state, CancellationToken cancellation)
        {
            var suggestion = new FitSuggestion { Base = state?.Info };
            if (item == null || state == null || !state.Known) return suggestion;
            var info = state.Info;
            var meshes = RefitCandidates.Meshes(item, info.Body);
            if (meshes.Count == 0) return suggestion;
            var near = await RefitRelevance.AnalyzeAsync(state.Map, meshes, cancellation);
            if (item == null) return suggestion;

            var fitInfo = item.GetComponentInParent<OrbitersFitInfo>(true) ?? item.GetComponentInChildren<OrbitersFitInfo>(true);
            var excluded = new HashSet<string>(fitInfo != null ? fitInfo.excludedShapes : new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in meshes)
            {
                if (mesh == null || !near.TryGetValue(mesh, out var shapes)) continue;
                shapes = shapes.Where(s => !excluded.Contains(s)).ToList();
                if (shapes.Count == 0) continue;
                var missing = RefitRunner.Missing(mesh.sharedMesh, shapes);
                if (missing.Count < shapes.Count) suggestion.CreatorAdapted = true;
                if (missing.Count > 0) suggestion.Missing[mesh] = missing;
            }
            if (suggestion.Missing.Count == 0)
            {
                // Armature fitting cannot establish surface fit. A garment adapted from another rig
                // still deserves the fit question even when no nearby custom shape is missing.
                var attachment = item.GetComponent<OrbitersAttachment>();
                if (info.CanFit && Attachments.AttachmentFit.Fitted(attachment) && !RefitCandidates.Refitted(meshes) &&
                    !(fitInfo != null && fitInfo.MadeForCustomBase && fitInfo.customBaseAssetId == info.AssetId))
                    suggestion.Advice = FitAdvice.AskFit;
                return suggestion;
            }

            if (fitInfo != null && fitInfo.MadeForCustomBase && fitInfo.customBaseAssetId == info.AssetId)
                suggestion.Advice = FitAdvice.AddShapes;
            else if (fitInfo != null && !fitInfo.MadeForCustomBase)
            {
                suggestion.MadeForOriginal = true;
                suggestion.Advice = info.CanFit ? FitAdvice.Refit : FitAdvice.AddShapes;
            }
            // Some of its shapes already follow the custom base: made or adapted for it, only complete them.
            else if (suggestion.CreatorAdapted || RefitCandidates.Refitted(suggestion.Meshes)) suggestion.Advice = FitAdvice.AddShapes;
            else suggestion.Advice = info.CanFit ? FitAdvice.AskFit : FitAdvice.AddShapes;
            return suggestion;
        }
    }

    /// <summary>The meshes of an avatar or item that a refit can adapt.</summary>
    public static class RefitCandidates
    {
        private const string XRayObjectPrefix = "__XRayGizmos_";
        private const string XRayMeshPrefix = "XRayArmatureMesh";

        /// <summary>The item's skinned meshes, except the body and editor helpers (X-Ray gizmos, hidden previews).</summary>
        public static List<SkinnedMeshRenderer> Meshes(GameObject item, SkinnedMeshRenderer body) =>
            item == null ? new List<SkinnedMeshRenderer>() : item.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r.sharedMesh != null && r != body && !IsEditorHelper(r)).ToList();

        /// <summary>A renderer an editor tool created for display: never clothing.</summary>
        public static bool IsEditorHelper(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) return false;
            if (Hidden(renderer.hideFlags) || Hidden(renderer.gameObject.hideFlags)) return true;
            for (var t = renderer.transform; t != null; t = t.parent)
                if (t.name.StartsWith(XRayObjectPrefix, StringComparison.Ordinal)) return true;
            string mesh = renderer.sharedMesh != null ? renderer.sharedMesh.name : null;
            return !string.IsNullOrEmpty(mesh) && (mesh.StartsWith(XRayMeshPrefix, StringComparison.Ordinal) ||
                                                    mesh.EndsWith("_XRayMeshEdges", StringComparison.Ordinal) ||
                                                    mesh.EndsWith("_XRayWeightPaint", StringComparison.Ordinal));
        }

        internal static bool Refitted(IEnumerable<SkinnedMeshRenderer> meshes) => meshes.Any(RefitRecords.IsApplied);

        private static bool Hidden(HideFlags flags) => flags == HideFlags.HideAndDontSave || flags == HideFlags.DontSave;
    }
}
