using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// Which bindings of animation clips find nothing to animate: no object at the path (from the object the clip is played
    /// from), no component of the bound type on it, or no blendshape of that name on its mesh. Unity resolves a path to the
    /// first child of each name, as an Animator does. Material properties and the like are only checked for their
    /// renderer. Read a built avatar (VRCFury and Modular Avatar create objects while building), with every clip and the
    /// object it plays from (<c>AvatarUsage.Clips</c> for a VRChat avatar).
    /// </summary>
    public static class BindingAudit
    {
        public enum Problem { NoObject, NoComponent, NoBlendShape }

        public readonly struct Miss
        {
            public readonly AnimationClip Clip;
            public readonly Transform AnimatorRoot;
            public readonly EditorCurveBinding Binding;
            public readonly Problem Problem;

            public Miss(AnimationClip clip, Transform animatorRoot, EditorCurveBinding binding, Problem problem)
            {
                Clip = clip; AnimatorRoot = animatorRoot; Binding = binding; Problem = problem;
            }

            public override string ToString() =>
                (Clip != null ? Clip.name : "?") + ": " + (string.IsNullOrEmpty(Binding.path) ? "(root)" : Binding.path) + " " +
                (Binding.type != null ? Binding.type.Name : "?") + "." + Binding.propertyName + " (" + Problem + ")";
        }

        public sealed class Result
        {
            /// <summary>Bindings read, counted once per clip and object it plays from.</summary>
            public int Bindings;
            public readonly List<Miss> Missing = new List<Miss>();
        }

        public static Result Of(IEnumerable<(AnimationClip Clip, Transform AnimatorRoot)> clips)
        {
            var result = new Result();
            var found = new Dictionary<(Transform, string), Transform>();
            foreach (var (clip, animatorRoot) in clips)
            {
                if (clip == null || animatorRoot == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip)) Check(result, found, clip, animatorRoot, binding);
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) Check(result, found, clip, animatorRoot, binding);
            }
            return result;
        }

        private static void Check(Result result, Dictionary<(Transform, string), Transform> found, AnimationClip clip, Transform animatorRoot, EditorCurveBinding binding)
        {
            result.Bindings++;
            var problem = ProblemOf(found, animatorRoot, binding);
            if (problem != null) result.Missing.Add(new Miss(clip, animatorRoot, binding, problem.Value));
        }

        /// <summary>What keeps <paramref name="binding"/>, played from <paramref name="animatorRoot"/>, from animating anything; null when it resolves.</summary>
        public static Problem? ProblemOf(Transform animatorRoot, EditorCurveBinding binding) =>
            ProblemOf(new Dictionary<(Transform, string), Transform>(), animatorRoot, binding);

        private static Problem? ProblemOf(Dictionary<(Transform, string), Transform> found, Transform animatorRoot, EditorCurveBinding binding)
        {
            string path = binding.path ?? "";
            if (!found.TryGetValue((animatorRoot, path), out var target))
                found[(animatorRoot, path)] = target = path.Length == 0 ? animatorRoot : animatorRoot.Find(path);
            if (target == null) return Problem.NoObject;
            var type = binding.type;
            if (type == null || type == typeof(GameObject) || type == typeof(Transform) || typeof(Transform).IsAssignableFrom(type)) return null;
            // Animator parameters (curves named after them, on the animator's own object) are bindings of the Animator type.
            var component = target.GetComponent(type);
            if (component == null) return Problem.NoComponent;
            const string shape = "blendShape.";
            if (component is SkinnedMeshRenderer skin && binding.propertyName.StartsWith(shape, StringComparison.Ordinal))
            {
                var mesh = skin.sharedMesh;
                if (mesh == null || mesh.GetBlendShapeIndex(binding.propertyName.Substring(shape.Length)) < 0) return Problem.NoBlendShape;
            }
            return null;
        }
    }
}
