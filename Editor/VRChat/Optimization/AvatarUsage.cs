using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// What a built avatar uses, read once before an optimization removes anything: the properties its animations change
    /// (playable layers and Animator components), the objects its components point at, its humanoid bones. Read it after
    /// every build step that adds animations (VRCFury, Modular Avatar, MCB) has run. Editor-only components (markers, My
    /// Avatar's own) leave the upload: what they point at is not a use, unless <c>editorOnlyUses</c> says so (an avatar not
    /// built yet, whose VRCFury or Modular Avatar components still stand for the animations they will make).
    /// </summary>
    public sealed class AvatarUsage
    {
        public GameObject Root { get; }

        // Every animated property, and whether some key sets it above one half (an object shown, a component turned on).
        private readonly Dictionary<(string Path, string Property), List<(Type Type, bool On)>> animated =
            new Dictionary<(string, string), List<(Type, bool)>>();
        private readonly Dictionary<string, HashSet<string>> animatedPaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> transformPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> materialPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<Transform> humanoid = new HashSet<Transform>();
        // What each object is pointed at by: (the component pointing, the serialized property path).
        private readonly Dictionary<Object, List<(Component From, string Property)>> references = new Dictionary<Object, List<(Component, string)>>();
        // Objects holding a build tool's component, on an avatar not built yet.
        private readonly HashSet<Transform> pending = new HashSet<Transform>();
        private readonly HashSet<string> parameters = new HashSet<string>(StringComparer.Ordinal);

        public AvatarUsage(GameObject root, Func<Component, bool> editorOnlyUses = null)
        {
            Root = root;
            foreach (var controller in Controllers(root))
            {
                var source = controller is AnimatorOverrideController overrides ? overrides.runtimeAnimatorController : controller;
                if (source is AnimatorController editable) parameters.UnionWith(editable.parameters.Select(p => p.name));
            }
            foreach (var clip in Controllers(root).SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    bool on = false;
                    if (binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled")
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        on = curve != null && curve.keys.Any(k => k.value > .5f);
                    }
                    Add(binding, on);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    Add(binding, false);
                    if (binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) materialPaths.Add(binding.path);
                }
            }
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                    if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) is Transform t) humanoid.Add(t);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is IEditorOnly && (editorOnlyUses == null || !editorOnlyUses(component))) continue;
                if (component is IEditorOnly) pending.Add(component.transform);
                var property = new SerializedObject(component).GetIterator();
                while (property.Next(true))
                {
                    // Every component names its own object (m_GameObject): bookkeeping, not a use.
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_GameObject") continue;
                    var value = property.objectReferenceValue;
                    if (value == null || value == component) continue;
                    if (!references.TryGetValue(value, out var list)) references[value] = list = new List<(Component, string)>();
                    list.Add((component, property.propertyPath));
                }
            }
        }

        private void Add(EditorCurveBinding binding, bool on)
        {
            var key = (binding.path, binding.propertyName);
            if (!animated.TryGetValue(key, out var list)) animated[key] = list = new List<(Type, bool)>();
            list.Add((binding.type, on));
            if (!animatedPaths.TryGetValue(binding.path, out var properties)) animatedPaths[binding.path] = properties = new HashSet<string>(StringComparer.Ordinal);
            properties.Add(binding.propertyName);
            if (binding.type == typeof(Transform)) transformPaths.Add(binding.path);
        }

        /// <summary>The playable layers' controllers and those of Animator components below the root.</summary>
        public static IEnumerable<RuntimeAnimatorController> Controllers(GameObject root)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                    if (!layer.isDefault && layer.animatorController != null) yield return layer.animatorController;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null) yield return animator.runtimeAnimatorController;
        }

        public string PathOf(Transform transform) => AnimationUtility.CalculateTransformPath(transform, Root.transform);

        /// <summary>Whether some animation can show this object, or turn this component on.</summary>
        public bool CanTurnOn(Object target)
        {
            if (target is GameObject go) return On(PathOf(go.transform), "m_IsActive", typeof(GameObject));
            return target is Component component && On(PathOf(component.transform), "m_Enabled", component.GetType());
        }

        private bool On(string path, string property, Type type) =>
            animated.TryGetValue((path, property), out var list) && list.Any(b => b.On && b.Type.IsAssignableFrom(type));

        /// <summary>Whether some animation shows or hides this object or one above it (the root excepted).</summary>
        public bool Toggled(Transform transform)
        {
            for (var t = transform; t != null && t != Root.transform; t = t.parent)
                if (animated.ContainsKey((PathOf(t), "m_IsActive"))) return true;
            return false;
        }

        /// <summary>Whether some animation turns this component on or off.</summary>
        public bool Switched(Component component) =>
            animated.TryGetValue((PathOf(component.transform), "m_Enabled"), out var list) && list.Any(b => b.Type.IsAssignableFrom(component.GetType()));

        /// <summary>Whether any animation changes something on this object (any component, any property).</summary>
        public bool Animated(Transform transform) => animatedPaths.ContainsKey(PathOf(transform));

        /// <summary>The properties animations change on this object (on any of its components).</summary>
        public IReadOnlyCollection<string> AnimatedAt(Transform transform) =>
            animatedPaths.TryGetValue(PathOf(transform), out var properties) ? properties : (IReadOnlyCollection<string>)Array.Empty<string>();

        /// <summary>Whether an animation moves, turns or scales this transform or one below it.</summary>
        public bool Moves(Transform transform)
        {
            string path = PathOf(transform);
            return transformPaths.Any(p => p == path || path.Length == 0 || p.StartsWith(path + "/", StringComparison.Ordinal));
        }

        /// <summary>Whether an animation swaps this renderer's materials.</summary>
        public bool SwapsMaterials(Renderer renderer) => materialPaths.Contains(PathOf(renderer.transform));

        public bool Humanoid(Transform transform) => humanoid.Contains(transform);

        /// <summary>Whether one of the avatar's animators has a parameter of this name (something can read it).</summary>
        public bool Reads(string parameter) => !string.IsNullOrEmpty(parameter) && parameters.Contains(parameter);

        /// <summary>
        /// On an avatar not built yet: whether a build tool's component sits on this object, below it or above it (the root
        /// excepted). What the tool makes of it at upload (a toggle, a merge) isn't known yet, so it is left as it is.
        /// </summary>
        public bool Pending(Transform transform) =>
            pending.Any(t => t != null && (t.IsChildOf(transform) || t != Root.transform && transform.IsChildOf(t)));

        /// <summary>Whether this object or one below it is a humanoid bone.</summary>
        public bool HoldsHumanoid(Transform transform) => humanoid.Any(h => h.IsChildOf(transform));

        /// <summary>The components pointing at this object (a GameObject or a component), with the property they use.</summary>
        public IReadOnlyList<(Component From, string Property)> ReferencesTo(Object target) =>
            references.TryGetValue(target, out var list) ? list : (IReadOnlyList<(Component, string)>)Array.Empty<(Component, string)>();

        /// <summary>Whether a component still on the avatar points at this object.</summary>
        public bool Used(Object target) => ReferencesTo(target).Any(r => r.From != null);

        /// <summary>
        /// Whether a component outside <paramref name="subtree"/> points at it or at something inside it. Components gone
        /// since are not counted.
        /// </summary>
        public bool UsedFromOutside(Transform subtree, Func<Component, string, bool> ignore = null)
        {
            foreach (var pair in references)
            {
                // Objects destroyed since compare equal to null.
                if (pair.Key == null) continue;
                var target = pair.Key is GameObject go ? go.transform : pair.Key is Component c ? c.transform : null;
                if (target == null || !target.IsChildOf(subtree)) continue;
                foreach (var (from, property) in pair.Value)
                    if (from != null && !from.transform.IsChildOf(subtree) && (ignore == null || !ignore(from, property))) return true;
            }
            return false;
        }
    }
}
