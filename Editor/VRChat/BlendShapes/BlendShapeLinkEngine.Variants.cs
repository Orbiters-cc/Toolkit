using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    public static partial class BlendShapeLinkEngine
    {
        // One Apply: what every link asks of the clips, read once. Each clip's bindings come from the index (the build's
        // during a build), and each effect clip's curves and the avatar's values they start from are read once, not once
        // per clip they are copied into.
        private sealed class Pass
        {
            private readonly AnimationIndex index;
            public readonly GameObject AnimatorRoot;
            public int ClipsWrapped, StatesRewritten;
            // The blendshape curves some clip of the controllers animates (path, property), clips made since included, and
            // the factors some wrapper tree there already blends.
            private readonly HashSet<(string Path, string Property)> shapes = new HashSet<(string, string)>();
            private readonly HashSet<string> wrapperFactors = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<AnimationClip, Effect> effects = new Dictionary<AnimationClip, Effect>();
            private readonly Dictionary<EditorCurveBinding, float> baselines = new Dictionary<EditorCurveBinding, float>();
            private readonly Dictionary<EditorCurveBinding, Object> objectBaselines = new Dictionary<EditorCurveBinding, Object>();

            public sealed class Effect
            {
                public EditorCurveBinding[] Floats;
                public AnimationCurve[] Curves;
                public EditorCurveBinding[] Objects;
                public ObjectReferenceKeyframe[][] Keys;
            }

            public Pass(AnimationIndex index, GameObject animatorRoot)
            {
                this.index = index;
                AnimatorRoot = animatorRoot;
            }

            // Before the controllers are detached: their clips, and the wrapper trees their states hold.
            public void Survey(List<AnimatorController> controllers, Dictionary<AnimatorController, AnimatorControllerLayer[]> layersOf)
            {
                foreach (var controller in controllers)
                {
                    foreach (var clip in controller.animationClips)
                        if (clip != null) Learn(clip);
                    var machines = new HashSet<AnimatorStateMachine>();
                    var trees = new HashSet<BlendTree>();
                    foreach (var layer in layersOf[controller])
                        if (layer?.stateMachine != null) Visit(layer.stateMachine, machines, trees);
                }
            }

            private void Visit(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> machines, HashSet<BlendTree> trees)
            {
                if (!machines.Add(machine)) return;
                foreach (var child in machine.states)
                    if (child.state != null) Visit(child.state.motion, trees);
                foreach (var child in machine.stateMachines)
                    if (child.stateMachine != null) Visit(child.stateMachine, machines, trees);
            }

            private void Visit(Motion motion, HashSet<BlendTree> trees)
            {
                if (!(motion is BlendTree tree) || !trees.Add(tree)) return;
                if (tree.name.StartsWith(WrapperPrefix, StringComparison.Ordinal) && tree.blendType == BlendTreeType.Simple1D && tree.children != null &&
                    tree.children.Length == 2 && tree.blendParameter != null) wrapperFactors.Add(tree.blendParameter);
                foreach (var child in tree.children) Visit(child.motion, trees);
            }

            /// <summary>A clip the controllers play now (made by a link, say): its blendshape curves may trigger later links.</summary>
            public void Learn(AnimationClip clip)
            {
                foreach (var binding in index.Bindings(clip).Floats)
                    if (binding.type == typeof(SkinnedMeshRenderer)) shapes.Add((binding.path, binding.propertyName));
            }

            public BlendTree Wrapped(BlendTree wrapper)
            {
                wrapperFactors.Add(wrapper.blendParameter);
                return wrapper;
            }

            /// <summary>
            /// Whether walking the controllers' states for <paramref name="link"/> can change anything: a blendshape
            /// trigger some clip animates, a wrapper of its factor (a state holding one is rewritten even without a new
            /// match), or a trigger matched by clip, which only reading each clip tells.
            /// </summary>
            public bool MayMatch(BlendShapeLink link) =>
                link.TriggerType != BlendShapeLinkEndpoint.BlendShape || shapes.Contains((link.SourcePath, link.SourceProperty)) ||
                link.FactorParameter != null && wrapperFactors.Contains(link.FactorParameter);

            /// <summary>The clip's blendshape curve at this path and property, or null.</summary>
            public AnimationCurve ShapeCurve(AnimationClip clip, string path, string property)
            {
                if (clip == null || path == null || string.IsNullOrWhiteSpace(property)) return null;
                return index.Bindings(clip).TryFind(path, typeof(SkinnedMeshRenderer), property, out var binding) ? AnimationUtility.GetEditorCurve(clip, binding) : null;
            }

            public EditorCurveBinding[] FloatBindings(AnimationClip clip) => index.Bindings(clip).Floats;

            public Effect EffectOf(AnimationClip clip)
            {
                if (effects.TryGetValue(clip, out var effect)) return effect;
                var bindings = index.Bindings(clip);
                effect = new Effect
                {
                    Floats = bindings.Floats,
                    Curves = bindings.Floats.Select(b => AnimationUtility.GetEditorCurve(clip, b)).ToArray(),
                    Objects = bindings.Objects,
                    Keys = bindings.Objects.Select(b => AnimationUtility.GetObjectReferenceCurve(clip, b)).ToArray(),
                };
                effects[clip] = effect;
                return effect;
            }

            // The avatar's own value of a property a trigger clip does not animate.
            public float Baseline(EditorCurveBinding binding)
            {
                if (baselines.TryGetValue(binding, out float known)) return known;
                float baseline = AnimatorRoot != null && AnimationUtility.GetFloatValue(AnimatorRoot, binding, out float value) ? value : 0f;
                baselines[binding] = baseline;
                return baseline;
            }

            public Object ObjectBaseline(EditorCurveBinding binding)
            {
                if (objectBaselines.TryGetValue(binding, out var known)) return known;
                Object baseline = null;
                if (AnimatorRoot != null) AnimationUtility.GetObjectReferenceValue(AnimatorRoot, binding, out baseline);
                objectBaselines[binding] = baseline;
                return baseline;
            }
        }

        // Returns a clone of the clip with the link applied, or null when the clip does not trigger the link.
        private static AnimationClip TryCreateVariantClip(Pass pass, AnimatorController controller, AnimationClip clip, BlendShapeLink link)
        {
            if (clip == null) return null;
            if (link.EffectType == BlendShapeLinkEndpoint.Animation && link.EffectClip == null) return null;
            if (link.TriggerType == BlendShapeLinkEndpoint.Animation && !AnimationClipSignature.MatchesClip(clip, link.TriggerName, link.TriggerSignature)) return null;

            bool shapeTrigger = link.TriggerType == BlendShapeLinkEndpoint.BlendShape, shapeEffect = link.EffectType == BlendShapeLinkEndpoint.BlendShape;
            // A renderer on the animator's own object has the empty path.
            bool hasDestination = link.DestinationPath != null && !string.IsNullOrWhiteSpace(link.DestinationProperty);
            if (shapeTrigger && shapeEffect)
            {
                var source = pass.ShapeCurve(clip, link.SourcePath, link.SourceProperty);
                if (source == null) return null;
                // A direct copy already present (repeated preprocess) needs no new clip.
                if (link.DirectCopy && pass.ShapeCurve(clip, link.DestinationPath, link.DestinationProperty) is AnimationCurve existing &&
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
            var effect = pass.EffectOf(link.EffectClip);
            if (shapeTrigger)
            {
                var activation = pass.ShapeCurve(clip, link.SourcePath, link.SourceProperty);
                if (activation == null) return null;
                float length = Mathf.Max(clip.length, link.EffectClip.length, 1f / 60f);
                return Variant(controller, clip, v =>
                {
                    var bindings = new List<EditorCurveBinding>();
                    var curves = new List<AnimationCurve>();
                    for (int i = 0; i < effect.Floats.Length; i++)
                    {
                        var overlay = effect.Curves[i];
                        if (overlay == null) continue;
                        var binding = effect.Floats[i];
                        // A property the trigger clip does not animate keeps the avatar's value, not 0.
                        bindings.Add(binding);
                        curves.Add(BlendByActivation(AnimationUtility.GetEditorCurve(clip, binding), pass.Baseline(binding), overlay, activation, length));
                    }
                    if (bindings.Count > 0) AnimationUtility.SetEditorCurves(v, bindings.ToArray(), curves.ToArray());
                    for (int i = 0; i < effect.Objects.Length; i++)
                    {
                        var overlay = effect.Keys[i];
                        if (overlay == null || overlay.Length == 0) continue;
                        var binding = effect.Objects[i];
                        var keys = SwitchByActivation(AnimationUtility.GetObjectReferenceCurve(clip, binding), pass.ObjectBaseline(binding), overlay, activation, length);
                        if (keys.Length > 0) AnimationUtility.SetObjectReferenceCurve(v, binding, keys);
                    }
                });
            }
            return Variant(controller, clip, v =>
            {
                var bindings = new List<EditorCurveBinding>();
                var curves = new List<AnimationCurve>();
                for (int i = 0; i < effect.Floats.Length; i++)
                    if (effect.Curves[i] != null)
                    {
                        bindings.Add(effect.Floats[i]);
                        curves.Add(effect.Curves[i]);
                    }
                if (bindings.Count > 0) AnimationUtility.SetEditorCurves(v, bindings.ToArray(), curves.ToArray());
                for (int i = 0; i < effect.Objects.Length; i++)
                {
                    var curve = effect.Keys[i];
                    if (curve != null && curve.Length > 0) AnimationUtility.SetObjectReferenceCurve(v, effect.Objects[i], curve);
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
