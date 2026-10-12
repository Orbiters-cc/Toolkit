using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Gives a build copy's objects, and the parameters only its animators use, the shortest names: the upload and the
    /// game keep every name. What something finds by name keeps its own: the avatar's root, the bones of the humanoid's
    /// model (the Animator binds them by name), Body (MMD dance worlds animate it by name), VRChat's own parameters and
    /// those of the expression menu (the menu, OSC and saved values use them). The build's copies of the animations follow
    /// (paths, masks, conditions, blend trees, parameter drivers, audio), and so do PhysBones and contacts.
    /// </summary>
    public static class ShortNames
    {
        public sealed class Result
        {
            public int Objects, Parameters;
            /// <summary>The characters the new names save.</summary>
            public long Characters;
        }

        private const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        // The parameters a PhysBone sets from its own: its parameter with these after it.
        private static readonly string[] PhysBoneSuffixes = { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        /// <summary>The <paramref name="index"/>th shortest name: a, b, ..., 9, aa, ab...</summary>
        public static string Short(int index)
        {
            var name = "";
            int length = 1;
            for (int count = Letters.Length; index >= count; count *= Letters.Length) { index -= count; length++; }
            for (int i = 0; i < length; i++) { name = Letters[index % Letters.Length] + name; index /= Letters.Length; }
            return name;
        }

        public static Result Rename(AvatarUsage usage, AttachmentAnimationBuild build)
        {
            var result = new Result();
            Objects(usage, build, result);
            Parameters(usage, build, result);
            return result;
        }

        // ---- Objects ----

        private static void Objects(AvatarUsage usage, AttachmentAnimationBuild build, Result result)
        {
            var root = usage.Root.transform;
            var skeleton = new HashSet<string>(StringComparer.Ordinal);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.avatar != null) skeleton.UnionWith(animator.avatar.humanDescription.skeleton.Select(b => b.name));
            bool Keeps(Transform t) => t == root || usage.Humanoid(t) || skeleton.Contains(t.name) || t.parent == root && t.name == "Body";

            var all = root.GetComponentsInChildren<Transform>(true);
            var former = all.ToDictionary(t => t, t => AnimationUtility.CalculateTransformPath(t, root));
            var moves = new List<(Transform, string)>();
            foreach (var parent in all)
            {
                var children = parent.Cast<Transform>().ToList();
                // Every name there now stays taken: a name kept, or too short to shorten, never meets a new one.
                var taken = new HashSet<string>(children.Select(c => c.name), StringComparer.Ordinal);
                int next = 0;
                foreach (var child in children.Where(c => !Keeps(c)))
                {
                    string name;
                    do name = Short(next++); while (taken.Contains(name));
                    if (name.Length >= child.name.Length) continue;
                    result.Characters += child.name.Length - name.Length;
                    child.name = name;
                    result.Objects++;
                    moves.Add((child, former[child]));
                }
            }
            if (moves.Count == 0) return;
            build.Moved(moves);
            // Audio a state plays names its source by path.
            var byFormer = former.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
            foreach (var audio in build.Owned.OfType<VRCAnimatorPlayAudio>())
                if (!string.IsNullOrEmpty(audio.SourcePath) && byFormer.TryGetValue(audio.SourcePath, out var source) && source != null)
                {
                    audio.SourcePath = AnimationUtility.CalculateTransformPath(source, root);
                    EditorUtility.SetDirty(audio);
                }
        }

        // ---- Parameters ----

        private static void Parameters(AvatarUsage usage, AttachmentAnimationBuild build, Result result)
        {
            var root = usage.Root;
            var owned = build.Owned.ToList();
            var controllers = owned.OfType<AnimatorController>().ToList();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (descriptor != null && descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null)
                kept.UnionWith(descriptor.expressionParameters.parameters.Where(p => p != null).Select(p => p.name));
            if (descriptor != null) kept.UnionWith(MenuParameters(descriptor.expressionsMenu));
            bool Keeps(string name) => VrcParameters.Protected(name) || kept.Contains(name);

            var names = new HashSet<string>(controllers.SelectMany(c => c.parameters.Select(p => p.name)), StringComparer.Ordinal);
            var physBones = root.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => !string.IsNullOrEmpty(p.parameter)).ToList();
            var receivers = root.GetComponentsInChildren<ContactReceiver>(true).Where(c => !string.IsNullOrEmpty(c.parameter)).ToList();
            var bases = physBones.Select(p => p.parameter).Distinct(StringComparer.Ordinal).ToList();
            var derived = new HashSet<string>(bases.SelectMany(b => PhysBoneSuffixes.Select(s => b + s)), StringComparer.Ordinal);
            // Every name there now: a new name never meets one, whether it stays or changes too.
            var reserved = new HashSet<string>(names.Concat(kept).Concat(receivers.Select(c => c.parameter)).Concat(bases).Concat(derived), StringComparer.Ordinal);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int next = 0;
            string New()
            {
                string name;
                do name = Short(next++); while (reserved.Contains(name) || PhysBoneSuffixes.Any(s => reserved.Contains(name + s)));
                reserved.Add(name);
                return name;
            }

            // A PhysBone's parameters change together, or not at all.
            foreach (var name in bases)
            {
                if (Keeps(name) || PhysBoneSuffixes.Any(s => Keeps(name + s))) continue;
                string to = New();
                if (to.Length >= name.Length) continue;
                map[name] = to;
                foreach (var suffix in PhysBoneSuffixes) map[name + suffix] = to + suffix;
            }
            foreach (var name in receivers.Select(c => c.parameter).Concat(names).Distinct(StringComparer.Ordinal))
            {
                if (Keeps(name) || derived.Contains(name) || map.ContainsKey(name)) continue;
                string to = New();
                if (to.Length < name.Length) map[name] = to;
            }
            if (map.Count == 0) return;
            string Rename(string name) => name != null && map.TryGetValue(name, out var to) ? to : name;

            foreach (var controller in controllers)
            {
                var parameters = controller.parameters;
                foreach (var parameter in parameters) parameter.name = Rename(parameter.name);
                controller.parameters = parameters;
                EditorUtility.SetDirty(controller);
            }
            foreach (var value in owned)
            {
                switch (value)
                {
                    case AnimatorTransitionBase transition:
                        var conditions = transition.conditions;
                        for (int i = 0; i < conditions.Length; i++) conditions[i].parameter = Rename(conditions[i].parameter);
                        transition.conditions = conditions;
                        break;
                    case AnimatorState state:
                        state.speedParameter = Rename(state.speedParameter); state.cycleOffsetParameter = Rename(state.cycleOffsetParameter);
                        state.mirrorParameter = Rename(state.mirrorParameter); state.timeParameter = Rename(state.timeParameter);
                        break;
                    case BlendTree tree:
                        tree.blendParameter = Rename(tree.blendParameter); tree.blendParameterY = Rename(tree.blendParameterY);
                        var children = tree.children;
                        for (int i = 0; i < children.Length; i++) children[i].directBlendParameter = Rename(children[i].directBlendParameter);
                        tree.children = children;
                        break;
                    case VRCAvatarParameterDriver driver:
                        foreach (var parameter in driver.parameters) { parameter.name = Rename(parameter.name); parameter.source = Rename(parameter.source); }
                        break;
                    case VRCAnimatorPlayAudio audio:
                        audio.ParameterName = Rename(audio.ParameterName);
                        break;
                    case AnimationClip clip:
                        // Animations setting parameters (animator parameters driven by a clip).
                        var from = AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(Animator) && map.ContainsKey(b.propertyName)).ToArray();
                        if (from.Length == 0) continue;
                        var curves = from.Select(b => AnimationUtility.GetEditorCurve(clip, b)).ToArray();
                        AnimationUtility.SetEditorCurves(clip, from, new AnimationCurve[from.Length]);
                        AnimationUtility.SetEditorCurves(clip, from.Select(b => EditorCurveBinding.FloatCurve(b.path, typeof(Animator), map[b.propertyName])).ToArray(), curves);
                        break;
                    default: continue;
                }
                EditorUtility.SetDirty(value);
            }
            foreach (var physBone in physBones) physBone.parameter = Rename(physBone.parameter);
            foreach (var receiver in receivers) receiver.parameter = Rename(receiver.parameter);
            result.Parameters = map.Count(p => !derived.Contains(p.Key));
            result.Characters += map.Where(p => names.Contains(p.Key) || bases.Contains(p.Key)).Sum(p => (long)(p.Key.Length - p.Value.Length));
        }

        private static IEnumerable<string> MenuParameters(VRCExpressionsMenu menu)
        {
            var seen = new HashSet<VRCExpressionsMenu>();
            var stack = new Stack<VRCExpressionsMenu>();
            if (menu != null) stack.Push(menu);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (next == null || !seen.Add(next)) continue;
                foreach (var control in next.controls ?? new List<VRCExpressionsMenu.Control>())
                {
                    if (control.parameter != null && !string.IsNullOrEmpty(control.parameter.name)) yield return control.parameter.name;
                    foreach (var parameter in control.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0])
                        if (parameter != null && !string.IsNullOrEmpty(parameter.name)) yield return parameter.name;
                    if (control.subMenu != null) stack.Push(control.subMenu);
                }
            }
        }
    }
}
