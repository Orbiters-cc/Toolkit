using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Validation;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// What a built avatar loses on Quest (and other phones): shaders VRChat doesn't show there, components it removes,
    /// its rank; and, against the other platform's version of the same avatar, synced parameters that differ. Only the
    /// version Quest players see is checked for Quest (<see cref="ForQuest"/>).
    /// </summary>
    public static class QuestParity
    {
        /// <summary>The other platform's version of an avatar: the object uploaded there, and whether that is Quest.</summary>
        public sealed class Counterpart
        {
            public GameObject Avatar;
            public bool IsQuest;
        }

        // The components VRChat's whitelist allows on PC only (AvatarValidation.ComponentTypeWhiteListCommon and Sdk3, their
        // UNITY_STANDALONE parts): Quest removes them. By full type name, base types included (Joint covers every joint).
        private static readonly (string[] Types, string What, string Effect)[] PcOnly =
        {
            (new[] { "UnityEngine.Animations.AimConstraint", "UnityEngine.Animations.LookAtConstraint", "UnityEngine.Animations.ParentConstraint",
                     "UnityEngine.Animations.PositionConstraint", "UnityEngine.Animations.RotationConstraint", "UnityEngine.Animations.ScaleConstraint" },
             "Unity constraints", "What they hold stops following on Quest. Convert them to VRChat constraints (the SDK's build panel offers it): those work on both."),
            (new[] { "UnityEngine.Light" }, "lights", "They light nothing on Quest."),
            (new[] { "UnityEngine.AudioSource", "ONSPAudioSource", "VRC.SDK3.Avatars.Components.VRCSpatialAudioSource" }, "sound sources", "The avatar is silent on Quest."),
            (new[] { "UnityEngine.Cloth" }, "cloth simulations", "The cloth hangs still on Quest. PhysBones move on both."),
            (new[] { "UnityEngine.Rigidbody", "UnityEngine.Joint", "UnityEngine.BoxCollider", "UnityEngine.SphereCollider", "UnityEngine.CapsuleCollider" },
             "physics components", "Rigidbodies, joints and colliders don't work on Quest. PhysBones and contacts do."),
            (new[] { "UnityEngine.Camera" }, "cameras", "They show nothing on Quest."),
            (new[] { "RootMotion.FinalIK." }, "Final IK components", "Quest removes Final IK."),
        };

        public static List<AvatarIssue> Find(GameObject root)
        {
            var issues = new List<AvatarIssue>();
            var mobileShaders = MobileShaders();
            var materials = new HashSet<Material>(root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null));
            foreach (var clip in AvatarUsage.Controllers(root).SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        if (key.value is Material material) materials.Add(material);
            var desktop = materials.Where(m => m.shader != null && !mobileShaders.Contains(m.shader.name)).ToList();
            if (desktop.Count > 0)
            {
                var shaders = desktop.Select(m => ShaderName(m.shader)).Distinct().ToList();
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"{desktop.Count} material{(desktop.Count == 1 ? " uses a shader" : "s use shaders")} Quest can't show",
                    "Quest shows only VRChat's mobile shaders, and these use " + string.Join(", ", shaders.Take(3)) + (shaders.Count > 3 ? "..." : "") +
                    ". A Quest version needs VRChat/Mobile/Toon Standard, Toon Lit or Standard Lite.",
                    string.Join(", ", desktop.Take(4).Select(m => m.name)) + (desktop.Count > 4 ? "..." : "")));
            }

            var components = root.GetComponentsInChildren<Component>(true).Where(c => c != null).ToList();
            foreach (var (types, what, effect) in PcOnly)
            {
                var found = components.Where(c => Matches(c.GetType(), types)).ToList();
                if (found.Count == 0) continue;
                issues.Add(new AvatarIssue(IssueLevel.Warning, $"Quest removes {found.Count} {(found.Count == 1 ? Singular(what) : what)}", effect,
                    string.Join(", ", found.Select(c => c.name).Distinct().Take(4)) + (found.Count > 4 ? "..." : "")));
            }

            var stats = new AvatarPerformanceStats(true);
            AvatarPerformance.CalculatePerformanceStats(root.name, root, stats, true);
            var rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
            if (rating >= PerformanceRating.Poor)
            {
                var holding = new List<string>();
                foreach (AvatarPerformanceCategory category in Enum.GetValues(typeof(AvatarPerformanceCategory)))
                {
                    if (category == AvatarPerformanceCategory.None || category == AvatarPerformanceCategory.Overall || category == AvatarPerformanceCategory.AvatarPerformanceCategoryCount) continue;
                    if (stats.GetPerformanceRatingForCategory(category) == rating) holding.Add(AvatarPerformanceStats.GetPerformanceCategoryDisplayName(category).ToLowerInvariant());
                }
                bool veryPoor = rating >= PerformanceRating.VeryPoor;
                issues.Add(new AvatarIssue(veryPoor ? IssueLevel.Problem : IssueLevel.Note, (veryPoor ? "Very Poor" : "Poor") + " on Quest",
                    (veryPoor ? "Quest players see your fallback avatar unless they choose to show this one. " : "") +
                    (holding.Count > 0 ? "Set by " + string.Join(", ", holding.Take(4)) + "." : ""), "Quest rank"));
            }
            return issues;
        }

        /// <summary>
        /// The synced parameters of two built versions of one avatar (PC and Quest): VRChat matches them between versions
        /// by their place in the list, so both need the same parameters, of the same types, in the same order. VRCFury puts
        /// the Quest upload's in the PC upload's order itself (<see cref="VrcFury.MobileAlignment"/>): then only what the
        /// order can't fix counts.
        /// </summary>
        public static List<AvatarIssue> Compare(GameObject built, GameObject other, string otherName)
        {
            var issues = new List<AvatarIssue>();
            var mine = Synced(built);
            var theirs = Synced(other);
            string where = "Parameters, against “" + otherName + "”";
            foreach (var (name, _) in mine.Where(p => theirs.All(o => o.Name != p.Name)))
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{name}” is synced here but not on “{otherName}”",
                    "The two versions sync different parameters: players on the other platform see the wrong toggles and values. Give both the same synced parameters.", where));
            foreach (var (name, _) in theirs.Where(p => mine.All(o => o.Name != p.Name)))
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{name}” is synced on “{otherName}” but not here",
                    "The two versions sync different parameters: players on the other platform see the wrong toggles and values. Give both the same synced parameters.", where));
            foreach (var (name, type) in mine)
            {
                var match = theirs.FirstOrDefault(o => o.Name == name);
                if (match.Name != null && match.Type != type)
                    issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{name}” is {type} here, {match.Type} on “{otherName}”",
                        "VRChat sends it in one type and reads it in the other. Use the same type in both versions.", where));
            }
            // Each parameter both versions sync sits at the same place in both lists.
            var misplaced = mine.Select((p, i) => (p.Name, Index: i))
                .Where(x => theirs.FindIndex(o => o.Name == x.Name) is int j && j >= 0 && j != x.Index).Select(x => x.Name).ToList();
            if (misplaced.Count > 0)
            {
                string names = string.Join(", ", misplaced.Take(4).Select(n => "“" + n + "”")) + (misplaced.Count > 4 ? "..." : "");
                var (aligns, pcOrderKnown) = VrcFury.MobileAlignment(BlueprintId(built) ?? BlueprintId(other));
                if (!aligns)
                    issues.Add(new AvatarIssue(IssueLevel.Problem, $"{misplaced.Count} synced parameter{(misplaced.Count == 1 ? " sits" : "s sit")} at another place on “{otherName}”",
                        "VRChat matches synced parameters between versions by their place in the list (" + names + "): put the ones both versions share first, in the same order.", where));
                else if (!pcOrderKnown)
                    issues.Add(new AvatarIssue(IssueLevel.Note, $"VRCFury puts {misplaced.Count} synced parameter{(misplaced.Count == 1 ? "" : "s")} in the PC version's order",
                        "They sit at other places in the two versions (" + names + "). VRCFury fixes the order when the Quest version uploads, from the order this computer saved " +
                        "when it uploaded the PC version: upload the PC version first.", where));
            }
            return issues;
        }

        /// <summary>
        /// The other platform's version of <paramref name="root"/>, or null: the avatar its per-platform overrides upload for
        /// the other platform (or the one whose overrides upload this one there); else another avatar in the open scenes
        /// uploaded under the same blueprint whose materials are made for the other platform (a copy for the same platform,
        /// a VRCFury test copy or a hidden build is not one), the first in hierarchy order.
        /// </summary>
        public static Counterpart CounterpartOf(GameObject root)
        {
            if (root == null) return null;
            var own = Overrides(root);
            var mobile = own.Where(o => IsMobile(o.Platform)).OrderBy(o => o.Platform == BuildTarget.Android ? 0 : 1).FirstOrDefault();
            if (mobile.Avatar != null) return new Counterpart { Avatar = mobile.Avatar, IsQuest = true };
            var pc = own.FirstOrDefault(o => !IsMobile(o.Platform));
            if (pc.Avatar != null) return new Counterpart { Avatar = pc.Avatar, IsQuest = false };

            var avatars = OpenAvatars().Where(a => a != root).ToList();
            foreach (var main in avatars)
                foreach (var (avatar, platform) in Overrides(main))
                    if (avatar == root) return new Counterpart { Avatar = main, IsQuest = !IsMobile(platform) };

            string id = BlueprintId(root);
            if (string.IsNullOrEmpty(id)) return null;
            bool quest = LooksQuest(root);
            var other = avatars.FirstOrDefault(a => BlueprintId(a) == id && LooksQuest(a) != quest);
            return other != null ? new Counterpart { Avatar = other, IsQuest = !quest } : null;
        }

        /// <summary>
        /// Whether Quest players see <paramref name="root"/>: its other platform's version is the PC one; or it has none and
        /// the build target is Android or iOS, or most of its materials use VRChat's mobile shaders.
        /// </summary>
        public static bool ForQuest(GameObject root, Counterpart counterpart) =>
            counterpart != null ? !counterpart.IsQuest
                : EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS || LooksQuest(root);

        /// <summary>Whether most of the avatar's materials use VRChat's mobile shaders (it is made for Quest).</summary>
        public static bool LooksQuest(GameObject root)
        {
            var shaders = MobileShaders();
            var materials = root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader != null).Distinct().ToList();
            return materials.Count > 0 && materials.Count(m => shaders.Contains(m.shader.name)) * 2 > materials.Count;
        }

        private static HashSet<string> MobileShaders() => new HashSet<string>(AvatarValidation.ShaderWhiteList, StringComparer.Ordinal);

        private static bool IsMobile(BuildTarget platform) => platform == BuildTarget.Android || platform == BuildTarget.iOS;

        private static List<(GameObject Avatar, BuildTarget Platform)> Overrides(GameObject root) =>
            (PerPlatformOverrides.GetPlatformOverrides(root) ?? new List<PerPlatformOverrides.Option>())
            .Where(o => o.avatar != null && o.avatar.gameObject != root).Select(o => (o.avatar.gameObject, o.platform)).ToList();

        // The avatars of the open scenes in hierarchy order, without hidden objects (build copies) and VRCFury's test copies.
        private static IEnumerable<GameObject> OpenAvatars()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                var found = new List<VRCAvatarDescriptor>();
                foreach (var top in scene.GetRootGameObjects()) found.AddRange(top.GetComponentsInChildren<VRCAvatarDescriptor>(true));
                foreach (var descriptor in found)
                {
                    var avatar = descriptor.gameObject;
                    if (avatar.hideFlags != HideFlags.None || VrcFury.IsTestCopy(avatar)) continue;
                    yield return avatar;
                }
            }
        }

        private static string BlueprintId(GameObject root)
        {
            var pipeline = root != null ? root.GetComponent<PipelineManager>() : null;
            return pipeline != null && !string.IsNullOrEmpty(pipeline.blueprintId) ? pipeline.blueprintId : null;
        }

        private static List<(string Name, VRCExpressionParameters.ValueType Type)> Synced(GameObject root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || !descriptor.customExpressions || descriptor.expressionParameters == null || descriptor.expressionParameters.parameters == null)
                return new List<(string, VRCExpressionParameters.ValueType)>();
            return descriptor.expressionParameters.parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name) && AvatarParameterBudget.IsSynced(p))
                .Select(p => (p.name, p.valueType)).ToList();
        }

        // A shader as people know it: a locked copy ("Hidden/Locked/.poiyomi/Poiyomi 8.1/Poiyomi Toon/<hash>") by its own name.
        private static string ShaderName(Shader shader)
        {
            var parts = shader.name.Split('/').Where(p => p.Length > 0).ToList();
            if (parts.Count > 2 && parts[0] == "Hidden" && parts[1] == "Locked")
            {
                parts.RemoveRange(0, 2);
                if (parts.Count > 1 && parts[parts.Count - 1].Length >= 16 && parts[parts.Count - 1].All(Uri.IsHexDigit)) parts.RemoveAt(parts.Count - 1);
            }
            return parts.Count > 0 ? parts[parts.Count - 1] : shader.name;
        }

        private static bool Matches(Type type, string[] names)
        {
            for (var t = type; t != null && t != typeof(Component); t = t.BaseType)
            {
                string full = t.FullName ?? "";
                if (names.Any(n => n.EndsWith(".", StringComparison.Ordinal) ? full.StartsWith(n, StringComparison.Ordinal) : full == n)) return true;
            }
            return false;
        }

        private static string Singular(string what) =>
            what == "Unity constraints" ? "Unity constraint" : what == "lights" ? "light" : what == "sound sources" ? "sound source" :
            what == "cloth simulations" ? "cloth simulation" : what == "physics components" ? "physics component" : what == "cameras" ? "camera" : "Final IK component";
    }
}
