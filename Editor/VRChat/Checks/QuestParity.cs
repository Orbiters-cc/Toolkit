using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Validation;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// What a built avatar loses on Quest (and other phones): shaders VRChat doesn't show there, components it removes,
    /// its rank; and, against the other version of the same avatar (the same blueprint), synced parameters that differ.
    /// </summary>
    public static class QuestParity
    {
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
            var mobileShaders = new HashSet<string>(AvatarValidation.ShaderWhiteList, StringComparer.Ordinal);
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
        /// by their place in the list, so both need the same parameters, of the same types, in the same order.
        /// </summary>
        public static List<AvatarIssue> Compare(GameObject built, GameObject other, string otherName)
        {
            var issues = new List<AvatarIssue>();
            var mine = Synced(built);
            var theirs = Synced(other);
            string where = "Parameters, against “" + otherName + "”";
            foreach (var (name, type) in mine.Where(p => theirs.All(o => o.Name != p.Name)))
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{name}” is synced here but not on “{otherName}”",
                    "The two versions sync different parameters: players on the other platform see the wrong toggles and values. Give both the same synced parameters.", where));
            foreach (var (name, type) in theirs.Where(p => mine.All(o => o.Name != p.Name)))
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
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"{misplaced.Count} synced parameter{(misplaced.Count == 1 ? " sits" : "s sit")} at another place on “{otherName}”",
                    "VRChat matches synced parameters between versions by their place in the list (" + string.Join(", ", misplaced.Take(4).Select(n => "“" + n + "”")) +
                    (misplaced.Count > 4 ? "..." : "") + "): put the ones both versions share first, in the same order.", where));
            return issues;
        }

        /// <summary>The other avatars in the open scenes uploaded under the same blueprint (the other platform's version).</summary>
        public static List<GameObject> Counterparts(GameObject root)
        {
            var pipeline = root.GetComponent<PipelineManager>();
            string id = pipeline != null ? pipeline.blueprintId : null;
            if (string.IsNullOrEmpty(id)) return new List<GameObject>();
            return UnityEngine.Object.FindObjectsByType<PipelineManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(p => p != null && p.gameObject != root && p.blueprintId == id && p.GetComponent<VRCAvatarDescriptor>() != null && !EditorUtility.IsPersistent(p))
                .Select(p => p.gameObject).ToList();
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
