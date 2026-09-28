using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    public static partial class BlendShapeLinkEngine
    {
        // Returns a clone of the clip with the link applied, or null when the clip does not trigger the link.
        private static AnimationClip TryCreateVariantClip(AnimatorController controller, AnimationClip clip, BlendShapeLink link)
        {
            if (clip == null) return null;
            if (link.EffectType == BlendShapeLinkEndpoint.Animation && link.EffectClip == null) return null;
            if (link.TriggerType == BlendShapeLinkEndpoint.Animation && !AnimationClipSignature.MatchesClip(clip, link.TriggerName, link.TriggerSignature)) return null;

            bool shapeTrigger = link.TriggerType == BlendShapeLinkEndpoint.BlendShape, shapeEffect = link.EffectType == BlendShapeLinkEndpoint.BlendShape;
            if (shapeTrigger && shapeEffect)
            {
                if (!TryGetBlendShapeCurve(clip, link.SourcePath, link.SourceProperty, out var source)) return null;
                // A direct copy already present (repeated preprocess) needs no new clip.
                if (link.DirectCopy && TryGetBlendShapeCurve(clip, link.DestinationPath, link.DestinationProperty, out var existing) &&
                    source.preWrapMode == existing.preWrapMode && source.postWrapMode == existing.postWrapMode && source.keys.SequenceEqual(existing.keys)) return null;
                if (string.IsNullOrWhiteSpace(link.DestinationPath) || string.IsNullOrWhiteSpace(link.DestinationProperty)) return null;
                return Variant(controller, clip, v => AnimationUtility.SetEditorCurve(v, ShapeBinding(link), source));
            }
            if (!shapeTrigger && shapeEffect)
            {
                if (string.IsNullOrWhiteSpace(link.DestinationPath) || string.IsNullOrWhiteSpace(link.DestinationProperty)) return null;
                float end = Mathf.Max(clip.length, 1f / 60f);
                return Variant(controller, clip, v => AnimationUtility.SetEditorCurve(v, ShapeBinding(link), new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(end, 100f))));
            }
            if (shapeTrigger)
            {
                if (!TryGetBlendShapeCurve(clip, link.SourcePath, link.SourceProperty, out var activation)) return null;
                return Variant(controller, clip, v =>
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(link.EffectClip))
                    {
                        var overlay = AnimationUtility.GetEditorCurve(link.EffectClip, binding);
                        if (overlay == null) continue;
                        AnimationUtility.SetEditorCurve(v, binding, BlendByActivation(AnimationUtility.GetEditorCurve(clip, binding), overlay, activation, clip.length, link.EffectClip.length));
                    }
                });
            }
            return Variant(controller, clip, v =>
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(link.EffectClip))
                {
                    var overlay = AnimationUtility.GetEditorCurve(link.EffectClip, binding);
                    if (overlay != null) AnimationUtility.SetEditorCurve(v, binding, overlay);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(link.EffectClip))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(link.EffectClip, binding);
                    if (curve != null && curve.Length > 0) AnimationUtility.SetObjectReferenceCurve(v, binding, curve);
                }
            });
        }

        private static EditorCurveBinding ShapeBinding(BlendShapeLink link) =>
            new EditorCurveBinding { path = link.DestinationPath, type = typeof(SkinnedMeshRenderer), propertyName = link.DestinationProperty };

        private static AnimationClip Variant(AnimatorController controller, AnimationClip source, Action<AnimationClip> edit)
        {
            var variant = Object.Instantiate(source);
            variant.name = BuildName(VariantPrefix, source.name);
            variant.hideFlags = HideFlags.HideInHierarchy;
            edit(variant);
            AttachAsSubAsset(controller, variant);
            EditorUtility.SetDirty(variant);
            return variant;
        }

        private static bool TryGetBlendShapeCurve(AnimationClip clip, string path, string property, out AnimationCurve curve)
        {
            curve = null;
            if (clip == null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(property)) return false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || !string.Equals(binding.path, path, StringComparison.Ordinal) ||
                    !string.Equals(binding.propertyName, property, StringComparison.Ordinal)) continue;
                curve = AnimationUtility.GetEditorCurve(clip, binding);
                return curve != null;
            }
            return false;
        }

        // Lerps from the base curve to the overlay by the trigger blendshape's activation (0..100).
        private static AnimationCurve BlendByActivation(AnimationCurve baseCurve, AnimationCurve overlay, AnimationCurve activation, float sourceLength, float overlayLength)
        {
            var times = new SortedSet<float> { 0f, Mathf.Max(sourceLength, overlayLength, 1f / 60f) };
            foreach (var curve in new[] { baseCurve, overlay, activation })
                if (curve != null) foreach (var key in curve.keys) times.Add(key.time);
            var keys = times.Select(t => new Keyframe(t, Mathf.Lerp(baseCurve != null ? baseCurve.Evaluate(t) : 0f, overlay.Evaluate(t),
                Mathf.Clamp01(activation.Evaluate(t) / 100f)))).ToArray();
            return new AnimationCurve(keys);
        }

        private static BlendTree CreateWrapperTree(AnimatorController controller, AnimationClip original, AnimationClip variant, string factor)
        {
            var tree = new BlendTree
            {
                name = BuildName(WrapperPrefix, original.name), blendType = BlendTreeType.Simple1D, blendParameter = factor,
                useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy,
                children = new[]
                {
                    new ChildMotion { motion = original, threshold = 0f, timeScale = 1f },
                    new ChildMotion { motion = variant, threshold = 1f, timeScale = 1f }
                }
            };
            AttachAsSubAsset(controller, tree);
            EditorUtility.SetDirty(tree);
            return tree;
        }

        private static string BuildName(string prefix, string sourceName) => prefix + (string.IsNullOrWhiteSpace(sourceName) ? "Clip" : sourceName);

        private static void AttachAsSubAsset(AnimatorController controller, Object obj)
        {
            if (controller == null || obj == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller)) || AssetDatabase.Contains(obj)) return;
            AssetDatabase.AddObjectToAsset(obj, controller);
        }
    }
}
