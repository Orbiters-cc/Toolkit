using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// What a built avatar uses, read once before an optimization removes anything: the properties its animations change
    /// (playable layers, Animator components and legacy Animation components, each from the object its paths start at),
    /// the objects its components point at, its humanoid bones and the parameters its animators read. Read it after every
    /// build step that adds animations (VRCFury, Modular Avatar, MCB) has run. Editor-only components (markers, My Avatar's
    /// own) leave the upload: what they point at is not a use, unless <c>editorOnlyUses</c> says so (an avatar not built
    /// yet, whose VRCFury or Modular Avatar components still stand for the animations they will make) or they belong to an
    /// optimizer that builds later (<see cref="AvatarOptimizers"/>).
    /// </summary>
    public sealed class AvatarUsage
    {
        public GameObject Root { get; }

        // Every animated property, by its path from the avatar's root, and whether some key sets it above one half (an
        // object shown, a component turned on).
        private readonly Dictionary<(string Path, string Property), List<(Type Type, bool On)>> animated =
            new Dictionary<(string, string), List<(Type, bool)>>();
        private readonly Dictionary<string, HashSet<string>> animatedPaths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> transformPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> materialPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<Transform> humanoid = new HashSet<Transform>();
        // What each object is pointed at by: (the component pointing, the serialized property path).
        private readonly Dictionary<Object, List<(Component From, string Property)>> references = new Dictionary<Object, List<(Component, string)>>();
        // The objects pointed at, by the transform holding them (their GameObject's, or their own).
        private readonly Dictionary<Transform, List<Object>> held = new Dictionary<Transform, List<Object>>();
        // Objects holding a build tool's component: on an avatar not built yet, or an optimizer's that builds later.
        private readonly HashSet<Transform> pending = new HashSet<Transform>();
        private readonly HashSet<string> parameters = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> playableParameters = new HashSet<string>(StringComparer.Ordinal);

        public AvatarUsage(GameObject root, Func<Component, bool> editorOnlyUses = null)
        {
            Root = root;
            foreach (var (controller, _, playable) in Animators(root))
            {
                var names = Parameters(controller).ToList();
                parameters.UnionWith(names);
                if (playable) playableParameters.UnionWith(names);
            }
            foreach (var (clip, animatorRoot) in Clips(root))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    bool on = false;
                    if (binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled")
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        on = curve != null && curve.keys.Any(k => k.value > .5f);
                    }
                    Add(PathOf(animatorRoot, binding.path), binding, on);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    string path = PathOf(animatorRoot, binding.path);
                    Add(path, binding, false);
                    if (binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) materialPaths.Add(path);
                }
            }
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                    if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) is Transform t) humanoid.Add(t);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component is IEditorOnly)
                {
                    if (!AvatarOptimizers.IsComponent(component) && (editorOnlyUses == null || !editorOnlyUses(component))) continue;
                    pending.Add(component.transform);
                }
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        // Every component names its own object (m_GameObject): bookkeeping, not a use.
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_GameObject") continue;
                        var value = property.objectReferenceValue;
                        if (value == null || value == component) continue;
                        if (!references.TryGetValue(value, out var list))
                        {
                            references[value] = list = new List<(Component, string)>();
                            var holder = value is GameObject go ? go.transform : value is Component c ? c.transform : null;
                            if (holder != null)
                            {
                                if (!held.TryGetValue(holder, out var objects)) held[holder] = objects = new List<Object>();
                                objects.Add(value);
                            }
                        }
                        list.Add((component, property.propertyPath));
                    }
                }
            }
        }

        private void Add(string path, EditorCurveBinding binding, bool on)
        {
            var key = (path, binding.propertyName);
            if (!animated.TryGetValue(key, out var list)) animated[key] = list = new List<(Type, bool)>();
            list.Add((binding.type, on));
            if (!animatedPaths.TryGetValue(path, out var properties)) animatedPaths[path] = properties = new HashSet<string>(StringComparer.Ordinal);
            properties.Add(binding.propertyName);
            if (binding.type == typeof(Transform)) transformPaths.Add(path);
        }

        // An override controller reads the parameters of the controller it overrides.
        private static IEnumerable<string> Parameters(RuntimeAnimatorController controller)
        {
            for (int depth = 0; controller is AnimatorOverrideController overrides && depth < 16; depth++) controller = overrides.runtimeAnimatorController;
            return controller is AnimatorController editable ? editable.parameters.Select(p => p.name) : Enumerable.Empty<string>();
        }

        /// <summary>
        /// The avatar's animator controllers, each with the object its clips' paths start from: the descriptor's playable
        /// layers (VRChat plays them on the avatar's own Animator and sets their parameters) and those of Animator components
        /// below the root (VRChat sets none of theirs).
        /// </summary>
        public static IEnumerable<(RuntimeAnimatorController Controller, Transform AnimatorRoot, bool PlayableLayer)> Animators(GameObject root)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                    if (!layer.isDefault && layer.animatorController != null) yield return (layer.animatorController, descriptor.transform, true);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null) yield return (animator.runtimeAnimatorController, animator.transform, false);
        }

        /// <summary>The playable layers' controllers and those of Animator components below the root.</summary>
        public static IEnumerable<RuntimeAnimatorController> Controllers(GameObject root) => Animators(root).Select(a => a.Controller);

        /// <summary>
        /// Every clip the avatar can play (its animators' and its legacy Animation components'), each with the object its
        /// paths start from.
        /// </summary>
        public static IEnumerable<(AnimationClip Clip, Transform AnimatorRoot)> Clips(GameObject root)
        {
            var seen = new HashSet<(AnimationClip, Transform)>();
            foreach (var (controller, animatorRoot, _) in Animators(root))
                foreach (var clip in controller.animationClips)
                    if (clip != null && seen.Add((clip, animatorRoot))) yield return (clip, animatorRoot);
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                foreach (var clip in LegacyAnimationClips.Of(animation))
                    if (seen.Add((clip, animation.transform))) yield return (clip, animation.transform);
        }

        public string PathOf(Transform transform) => AnimationUtility.CalculateTransformPath(transform, Root.transform);

        /// <summary>The path from the avatar's root of what a clip played from <paramref name="animatorRoot"/> binds to.</summary>
        public string PathOf(Transform animatorRoot, string bindingPath)
        {
            if (animatorRoot == null || animatorRoot == Root.transform) return bindingPath;
            string prefix = PathOf(animatorRoot);
            return string.IsNullOrEmpty(bindingPath) ? prefix : prefix + "/" + bindingPath;
        }

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

        /// <summary>
        /// Whether a renderer is shown from the start and for good: on, active up to the avatar's root (what holds a hidden
        /// copy of it does not count), not EditorOnly, and no animation hides it or switches it off.
        /// </summary>
        public bool AlwaysShown(Renderer renderer)
        {
            if (!renderer.enabled || Toggled(renderer.transform) || Switched(renderer)) return false;
            for (var t = renderer.transform; t != null; t = t == Root.transform ? null : t.parent)
                if (!t.gameObject.activeSelf || t.CompareTag("EditorOnly")) return false;
            return true;
        }

        /// <summary>Whether any animation changes something on this object (any component, any property).</summary>
        public bool Animated(Transform transform) => animatedPaths.ContainsKey(PathOf(transform));

        /// <summary>The properties animations change on this object (on any of its components).</summary>
        public IReadOnlyCollection<string> AnimatedAt(Transform transform) =>
            animatedPaths.TryGetValue(PathOf(transform), out var properties) ? properties : (IReadOnlyCollection<string>)Array.Empty<string>();

        /// <summary>The properties animations change on this object's components of <paramref name="type"/>.</summary>
        public IReadOnlyCollection<string> AnimatedAt(Transform transform, Type type)
        {
            string path = PathOf(transform);
            if (!animatedPaths.TryGetValue(path, out var properties)) return Array.Empty<string>();
            return properties.Where(p => animated.TryGetValue((path, p), out var list) && list.Any(b => b.Type.IsAssignableFrom(type))).ToList();
        }

        /// <summary>Whether an animation moves, turns or scales this transform or one below it.</summary>
        public bool Moves(Transform transform)
        {
            string path = PathOf(transform);
            return transformPaths.Any(p => p == path || path.Length == 0 || p.StartsWith(path + "/", StringComparison.Ordinal));
        }

        /// <summary>Whether an animation moves, turns or scales this transform itself.</summary>
        public bool Transformed(Transform transform) => transformPaths.Contains(PathOf(transform));

        /// <summary>Whether an animation swaps this renderer's materials.</summary>
        public bool SwapsMaterials(Renderer renderer) => materialPaths.Contains(PathOf(renderer.transform));

        public bool Humanoid(Transform transform) => humanoid.Contains(transform);

        /// <summary>Whether one of the avatar's animators has a parameter of this name (something can read it).</summary>
        public bool Reads(string parameter) => !string.IsNullOrEmpty(parameter) && parameters.Contains(parameter);

        /// <summary>
        /// Whether one of the descriptor's playable layers has a parameter of this name: the animator VRChat sets expression,
        /// PhysBone and contact parameters on. Animator components elsewhere on the avatar never get them.
        /// </summary>
        public bool ReadsInPlayableLayers(string parameter) => !string.IsNullOrEmpty(parameter) && playableParameters.Contains(parameter);

        /// <summary>
        /// Whether a build tool's component sits on this object, below it or above it (the root excepted): on an avatar not
        /// built yet, or an optimizer's that builds later. What the tool makes of it (a toggle, a merge) isn't known yet, so it
        /// is left as it is.
        /// </summary>
        public bool Pending(Transform transform) =>
            pending.Any(t => t != null && (t.IsChildOf(transform) || t != Root.transform && transform.IsChildOf(t)));

        /// <summary>Whether this object or one below it is a humanoid bone.</summary>
        public bool HoldsHumanoid(Transform transform) => humanoid.Any(h => h.IsChildOf(transform));

        /// <summary>
        /// Transforms something moves against their parent besides the humanoid's animation, as the avatar is now: every
        /// PhysBone chain's bones (<see cref="PhysBoneChain"/>) and what constraints drive (<see cref="ConstraintTargets"/>).
        /// What they carry along only follows them.
        /// </summary>
        public HashSet<Transform> Driven()
        {
            var driven = ConstraintTargets(Root);
            foreach (var bone in Root.GetComponentsInChildren<VRCPhysBoneBase>(true)) driven.UnionWith(PhysBoneChain(bone));
            return driven;
        }

        /// <summary>
        /// A PhysBone's chain: its root and every transform below, without the ignored ones and what is below them (those
        /// follow their parent).
        /// </summary>
        public static List<Transform> PhysBoneChain(VRCPhysBoneBase bone)
        {
            var root = bone.rootTransform != null ? bone.rootTransform : bone.transform;
            var ignored = (bone.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).ToList();
            return root.GetComponentsInChildren<Transform>(true).Where(t => !ignored.Any(t.IsChildOf)).ToList();
        }

        /// <summary>The transforms VRChat's and Unity's constraints move: a VRChat constraint's target, its own object otherwise.</summary>
        public static HashSet<Transform> ConstraintTargets(GameObject root)
        {
            var moved = new HashSet<Transform>();
            foreach (var constraint in root.GetComponentsInChildren<VRCConstraintBase>(true))
                moved.Add(constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform);
            foreach (var constraint in root.GetComponentsInChildren<IConstraint>(true))
                if (constraint is Component component) moved.Add(component.transform);
            return moved;
        }

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
            foreach (var t in subtree.GetComponentsInChildren<Transform>(true))
            {
                if (!held.TryGetValue(t, out var objects)) continue;
                foreach (var target in objects)
                {
                    // Objects destroyed since compare equal to null.
                    if (target == null) continue;
                    foreach (var (from, property) in references[target])
                        if (from != null && !from.transform.IsChildOf(subtree) && (ignore == null || !ignore(from, property))) return true;
                }
            }
            return false;
        }
    }
}
