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
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Gives a build copy's objects, and the parameters only its animators use, the shortest names: the upload and the
    /// game keep every name. What something finds by name keeps its own: the avatar's root, the bones of the humanoid's
    /// model (the Animator binds them by name), Body (MMD dance worlds animate it by name), VRChat's own parameters and
    /// those of the expression menu or, without one, VRChat's default menu (the menu, OSC and saved values use them). The
    /// build's copies of the animations follow (paths, masks, conditions, blend trees, parameter drivers, audio), and so do
    /// PhysBones and contacts. A step that fails puts the names it changed back before the exception goes on.
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
        private static IReadOnlyList<string> PhysBoneSuffixes => VrcParameters.PhysBoneSuffixes;

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
            var owned = build.OwnedByAnimator.ToList();
            // The names animations use under each object, missing objects included: a new name never picks up a binding left
            // dangling.
            var bound = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var path in BoundPaths(usage, owned))
            {
                var parts = (path ?? "").Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    string parent = string.Join("/", parts, 0, i);
                    if (!bound.TryGetValue(parent, out var under)) bound[parent] = under = new HashSet<string>(StringComparer.Ordinal);
                    under.Add(parts[i]);
                }
            }
            var moves = new List<(Transform, string)>();
            var renamed = new List<(Transform Child, string Name)>();
            var sources = new List<(VRCAnimatorPlayAudio Audio, string Path)>();
            bool rewriting = false;
            try
            {
                foreach (var parent in all)
                {
                    var children = parent.Cast<Transform>().ToList();
                    // Every name there now stays taken: a name kept, or too short to shorten, never meets a new one.
                    var taken = new HashSet<string>(children.Select(c => c.name), StringComparer.Ordinal);
                    if (bound.TryGetValue(former[parent], out var used)) taken.UnionWith(used);
                    int next = 0;
                    foreach (var child in children.Where(c => !Keeps(c)))
                    {
                        string name;
                        do name = Short(next++); while (taken.Contains(name));
                        if (name.Length >= child.name.Length) continue;
                        result.Characters += child.name.Length - name.Length;
                        renamed.Add((child, child.name));
                        child.name = name;
                        result.Objects++;
                        moves.Add((child, former[child]));
                    }
                }
                if (moves.Count == 0) return;
                rewriting = true;
                build.Moved(moves);
                // Audio a state plays names its source by path, from the object its animator starts at.
                var byFormer = former.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
                foreach (var (copy, animatorRoot) in owned)
                {
                    if (!(copy is VRCAnimatorPlayAudio audio) || string.IsNullOrEmpty(audio.SourcePath) || animatorRoot == null) continue;
                    string from = former.TryGetValue(animatorRoot, out var at) && at.Length > 0 ? at + "/" + audio.SourcePath : audio.SourcePath;
                    if (!byFormer.TryGetValue(from, out var source) || source == null || !source.IsChildOf(animatorRoot)) continue;
                    sources.Add((audio, audio.SourcePath));
                    audio.SourcePath = AnimationUtility.CalculateTransformPath(source, animatorRoot);
                    EditorUtility.SetDirty(audio);
                }
            }
            catch
            {
                // The animations may already bind the new names: they are moved back with the objects.
                var back = rewriting ? renamed.Where(r => r.Child != null).Select(r => (r.Child, AnimationUtility.CalculateTransformPath(r.Child, root))).ToList() : null;
                foreach (var (child, name) in renamed) if (child != null) child.name = name;
                foreach (var (audio, path) in sources) audio.SourcePath = path;
                if (back != null && back.Count > 0)
                    try { build.Moved(back); }
                    catch (Exception undo) { Debug.LogException(undo); }
                result.Objects = 0; result.Characters = 0;
                throw;
            }
        }

        // Every path the build's animations bind (clips, masks), from the avatar's root.
        private static IEnumerable<string> BoundPaths(AvatarUsage usage, List<(Object Copy, Transform AnimatorRoot)> owned)
        {
            foreach (var (copy, animatorRoot) in owned)
            {
                if (copy is AnimationClip clip)
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip)) yield return usage.PathOf(animatorRoot, binding.path);
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) yield return usage.PathOf(animatorRoot, binding.path);
                }
                else if (copy is AvatarMask mask)
                    for (int i = 0; i < mask.transformCount; i++) yield return usage.PathOf(animatorRoot, mask.GetTransformPath(i));
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
            // Without custom expressions VRChat's default menu sets its own parameters (emotes, the face blend).
            if (descriptor != null && !descriptor.customExpressions) kept.UnionWith(VrcParameters.DefaultExpressionParameters);
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
            var back = map.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
            try { Apply(map); }
            catch
            {
                // New names never met an old one: mapping them back undoes what was done.
                Apply(back);
                throw;
            }
            result.Parameters = map.Count(p => !derived.Contains(p.Key));
            result.Characters += map.Where(p => names.Contains(p.Key) || bases.Contains(p.Key)).Sum(p => (long)(p.Key.Length - p.Value.Length));

            void Apply(Dictionary<string, string> to) => RenameAll(controllers, owned, physBones, receivers, name => name != null && to.TryGetValue(name, out var renamed) ? renamed : name);
        }

        private static void RenameAll(List<AnimatorController> controllers, List<Object> owned, List<VRCPhysBoneBase> physBones, List<ContactReceiver> receivers,
            Func<string, string> rename)
        {
            foreach (var controller in controllers)
            {
                var parameters = controller.parameters;
                foreach (var parameter in parameters) parameter.name = rename(parameter.name);
                controller.parameters = parameters;
                EditorUtility.SetDirty(controller);
            }
            foreach (var value in owned)
            {
                switch (value)
                {
                    case AnimatorTransitionBase transition:
                        var conditions = transition.conditions;
                        for (int i = 0; i < conditions.Length; i++) conditions[i].parameter = rename(conditions[i].parameter);
                        transition.conditions = conditions;
                        break;
                    case AnimatorState state:
                        state.speedParameter = rename(state.speedParameter); state.cycleOffsetParameter = rename(state.cycleOffsetParameter);
                        state.mirrorParameter = rename(state.mirrorParameter); state.timeParameter = rename(state.timeParameter);
                        break;
                    case BlendTree tree:
                        tree.blendParameter = rename(tree.blendParameter); tree.blendParameterY = rename(tree.blendParameterY);
                        var children = tree.children;
                        for (int i = 0; i < children.Length; i++) children[i].directBlendParameter = rename(children[i].directBlendParameter);
                        tree.children = children;
                        break;
                    case VRCAvatarParameterDriver driver:
                        foreach (var parameter in driver.parameters) { parameter.name = rename(parameter.name); parameter.source = rename(parameter.source); }
                        break;
                    case VRCAnimatorPlayAudio audio:
                        audio.ParameterName = rename(audio.ParameterName);
                        break;
                    case AnimationClip clip:
                        // Animations setting parameters (animator parameters driven by a clip).
                        var from = AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(Animator) && rename(b.propertyName) != b.propertyName).ToArray();
                        if (from.Length == 0) continue;
                        var curves = from.Select(b => AnimationUtility.GetEditorCurve(clip, b)).ToArray();
                        AnimationUtility.SetEditorCurves(clip, from, new AnimationCurve[from.Length]);
                        AnimationUtility.SetEditorCurves(clip, from.Select(b => EditorCurveBinding.FloatCurve(b.path, typeof(Animator), rename(b.propertyName))).ToArray(), curves);
                        break;
                    default: continue;
                }
                EditorUtility.SetDirty(value);
            }
            foreach (var physBone in physBones) physBone.parameter = rename(physBone.parameter);
            foreach (var receiver in receivers) receiver.parameter = rename(receiver.parameter);
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
