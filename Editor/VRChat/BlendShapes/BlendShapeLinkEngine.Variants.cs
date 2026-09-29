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
        private static AnimationClip TryCreateVariantClip(AnimatorController controller, AnimationClip clip, BlendShapeLink link, GameObject animatorRoot)
        {
            if (clip == null) return null;
            if (link.EffectType == BlendShapeLinkEndpoint.Animation && link.EffectClip == null) return null;
            if (link.TriggerType == BlendShapeLinkEndpoint.Animation && !AnimationClipSignature.MatchesClip(clip, link.TriggerName, link.TriggerSignature)) return null;

            bool shapeTrigger = link.TriggerType == BlendShapeLinkEndpoint.BlendShape, shapeEffect = link.EffectType == BlendShapeLinkEndpoint.BlendShape;
            // A renderer on the animator's own object has the empty path.
            bool hasDestination = link.DestinationPath != null && !string.IsNullOrWhiteSpace(link.DestinationProperty);
            if (shapeTrigger && shapeEffect)
            {
                if (!TryGetBlendShapeCurve(clip, link.SourcePath, link.SourceProperty, out var source)) return null;
                // A direct copy already present (repeated preprocess) needs no new clip.
                if (link.DirectCopy && TryGetBlendShapeCurve(clip, link.DestinationPath, link.DestinationProperty, out var existing) &&
                    source.preWrapMode == existing.preWrapMode && source.postWrapMode == existing.postWrapMode && source.keys.SequenceEqual(existing.keys)) return null;
                if (!hasDestination) return null;
                return Variant(controller, clip, v => AnimationUtility.SetEditorCurve(v, ShapeBinding(link), source));
            }
            if (!shapeTrigger && shapeEffect)
            {
                if (!hasDestination) return null;
                float end = Mathf.Max(clip.length, 1f / 60f);
                return Variant(controller, clip, v => AnimationUtility.SetEditorCurve(v, ShapeBinding(link), new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(end, 100f))));
            }
            if (shapeTrigger)
            {
                if (!TryGetBlendShapeCurve(clip, link.SourcePath, link.SourceProperty, out var activation)) return null;
                float length = Mathf.Max(clip.length, link.EffectClip.length, 1f / 60f);
                return Variant(controller, clip, v =>
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(link.EffectClip))
                    {
                        var overlay = AnimationUtility.GetEditorCurve(link.EffectClip, binding);
                        if (overlay == null) continue;
                        // A property the trigger clip does not animate keeps the avatar's value, not 0.
                        float baseline = animatorRoot != null && AnimationUtility.GetFloatValue(animatorRoot, binding, out float value) ? value : 0f;
                        AnimationUtility.SetEditorCurve(v, binding, BlendByActivation(AnimationUtility.GetEditorCurve(clip, binding), baseline, overlay, activation, length));
                    }
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(link.EffectClip))
                    {
                        var overlay = AnimationUtility.GetObjectReferenceCurve(link.EffectClip, binding);
                        if (overlay == null || overlay.Length == 0) continue;
                        Object baseline = null;
                        if (animatorRoot != null) AnimationUtility.GetObjectReferenceValue(animatorRoot, binding, out baseline);
                        var keys = SwitchByActivation(AnimationUtility.GetObjectReferenceCurve(clip, binding), baseline, overlay, activation, length);
                        if (keys.Length > 0) AnimationUtility.SetObjectReferenceCurve(v, binding, keys);
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
            if (clip == null || path == null || string.IsNullOrWhiteSpace(property)) return false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || !string.Equals(binding.path, path, StringComparison.Ordinal) ||
                    !string.Equals(binding.propertyName, property, StringComparison.Ordinal)) continue;
                curve = AnimationUtility.GetEditorCurve(clip, binding);
                return curve != null;
            }
            return false;
        }

        // Lerps from the base curve (or the baseline without one) to the overlay by the trigger blendshape's activation (0..100).
        private static AnimationCurve BlendByActivation(AnimationCurve baseCurve, float baseline, AnimationCurve overlay, AnimationCurve activation, float length)
        {
            var times = new SortedSet<float> { 0f, length };
            foreach (var curve in new[] { baseCurve, overlay, activation })
                if (curve != null) foreach (var key in curve.keys) times.Add(key.time);
            // The product of two curves can change between their authored keys. Sample the combined result,
            // and use linear tangents so creating the output does not add an unintended ease-in/ease-out.
            for (int i = 1; i / 60f < length; i++) times.Add(i / 60f);
            var keys = times.Select(t => new Keyframe(t, Mathf.Lerp(baseCurve != null ? baseCurve.Evaluate(t) : baseline, overlay.Evaluate(t),
                Mathf.Clamp01(activation.Evaluate(t) / 100f)))).ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) keys[i].inTangent = (keys[i].value - keys[i - 1].value) / (keys[i].time - keys[i - 1].time);
                if (i + 1 < keys.Length) keys[i].outTangent = (keys[i + 1].value - keys[i].value) / (keys[i + 1].time - keys[i].time);
            }
            return new AnimationCurve(keys);
        }

        // Object references (material swaps) cannot blend: the overlay's reference shows while the trigger is at least half
        // active, else the base curve's (or the baseline without one). Sampled at 60 Hz to switch between keys too.
        private static ObjectReferenceKeyframe[] SwitchByActivation(ObjectReferenceKeyframe[] baseKeys, Object baseline,
            ObjectReferenceKeyframe[] overlay, AnimationCurve activation, float length)
        {
            var times = new SortedSet<float> { 0f, length };
            foreach (var key in activation.keys) times.Add(key.time);
            foreach (var key in overlay.Concat(baseKeys ?? Array.Empty<ObjectReferenceKeyframe>())) times.Add(key.time);
            for (int i = 1; i / 60f < length; i++) times.Add(i / 60f);
            var output = new List<ObjectReferenceKeyframe>();
            foreach (float time in times)
            {
                bool active = activation.Evaluate(time) >= 50f;
                // Null is a valid authored reference (e.g. clear a material slot), not a missing curve.
                var value = active ? ValueAt(overlay, time) : baseKeys != null && baseKeys.Length > 0 ? ValueAt(baseKeys, time) : baseline;
                if (output.Count > 0 && output[output.Count - 1].value == value) continue;
                output.Add(new ObjectReferenceKeyframe { time = time, value = value });
            }
            return output.ToArray();
        }

        private static Object ValueAt(ObjectReferenceKeyframe[] keys, float time)
        {
            if (keys == null || keys.Length == 0) return null;
            var value = keys[0].value;
            foreach (var key in keys.OrderBy(k => k.time))
                if (key.time <= time) value = key.value;
            return value;
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
