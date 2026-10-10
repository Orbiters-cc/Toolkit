using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Reads VRCFury components without referencing VRCFury: its models are internal. Writing goes through
    /// <see cref="Writer"/>, which the optional VRCFury assembly registers with VRCFury's public API.
    /// </summary>
    public static class VrcFury
    {
        /// <summary>Creates VRCFury components; implemented by Orbiters.Toolkit.Editor.VRCFury when VRCFury is installed.</summary>
        public interface IWriter
        {
            /// <summary>A recursive Armature Link from <paramref name="from"/> to a humanoid bone, or to <paramref name="toObject"/> when not humanoid.</summary>
            Component ArmatureLink(GameObject host, GameObject from, HumanBodyBones toBone, GameObject toObject);
            /// <summary>A saved menu toggle turning <paramref name="target"/> on and off.</summary>
            Component Toggle(GameObject host, string menuPath, GameObject target, bool defaultOn);
            /// <summary>Merges a prop's controller, menu and parameters without changing the avatar's authored assets.</summary>
            Component FullController(GameObject host, RuntimeAnimatorController controller, ScriptableObject menu, ScriptableObject parameters);
        }

        public static IWriter Writer { get; set; }
        public static bool Installed => Component != null;

        private static Type component;
        public static Type Component => component ??= Find("VF.Model.VRCFury");

        public static Type Find(string fullName)
        {
            foreach (var assembly in new[] { "VRCFury-Runtime", "VRCFury-Editor", "VRCFury" })
            {
                var type = Type.GetType(fullName + ", " + assembly);
                if (type != null) return type;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        public static object Content(Component vrcFury) => vrcFury != null ? Component?.GetField("content")?.GetValue(vrcFury) : null;

        /// <summary>
        /// Whether VRCFury builds this avatar (it has a VRCFury component other than debug info, VRCFuryBuilder.ShouldRun):
        /// VRCFury then also rewrites the animation paths of objects moved earlier in the build.
        /// </summary>
        public static bool Builds(GameObject avatarRoot)
        {
            var type = avatarRoot != null ? Find("VF.Component.VRCFuryComponent") : null;
            if (type == null) return false;
            var debug = Find("VF.Model.VRCFuryDebugInfo");
            return avatarRoot.GetComponentsInChildren(type, true).Any(c => debug == null || !debug.IsInstanceOfType(c));
        }

        /// <summary>Each VRCFury component under <paramref name="root"/> with its feature, e.g. "ArmatureLink", "Toggle", "FullController".</summary>
        public static IEnumerable<(Component component, object feature, string kind)> Features(GameObject root)
        {
            if (root == null || Component == null) yield break;
            foreach (var c in root.GetComponentsInChildren(Component, true))
            {
                var feature = Content(c);
                if (feature != null) yield return (c, feature, feature.GetType().Name);
            }
        }

        // Walks base types too: the model version is a private field of VRCFury's base feature class.
        public static object Field(object model, string name)
        {
            for (var type = model?.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(model);
            }
            return null;
        }

        /// <summary>
        /// Whether a Full Controller (its feature) shares <paramref name="name"/> with the avatar (its "globalParams" rules:
        /// names, "prefix*" wildcards, "!" exceptions) instead of giving it its own namespace.
        /// </summary>
        public static bool IsGlobalParameter(object fullController, string name)
        {
            var rules = Field(fullController, "globalParams") as IEnumerable<string>;
            bool global = false;
            foreach (string rule in rules ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(rule)) continue;
                bool negative = rule.StartsWith("!", StringComparison.Ordinal);
                string match = negative ? rule.Substring(1) : rule;
                bool wildcard = match.EndsWith("*", StringComparison.Ordinal);
                if (wildcard) match = match.Substring(0, match.Length - 1);
                if (name == match || (wildcard && name.StartsWith(match, StringComparison.Ordinal)))
                {
                    if (negative) return false;
                    global = true;
                }
            }
            return global;
        }

        /// <summary>The object behind VRCFury's GuidWrapper fields (controllers, menus, parameter assets).</summary>
        public static UnityEngine.Object ObjectReference(object wrapper)
        {
            for (var type = wrapper?.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("objRef", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) return field.GetValue(wrapper) as UnityEngine.Object;
            }
            return null;
        }

        /// <summary>An Armature Link as VRCFury will apply it.</summary>
        public sealed class Link
        {
            /// <summary>A humanoid bone, an object or else the avatar root; then <see cref="Offset"/>, a path from it.</summary>
            public struct Target
            {
                public bool UseBone, UseObject;
                public HumanBodyBones Bone;
                public GameObject Object;
                public string Offset;
            }

            public Component Component;
            public GameObject From;
            /// <summary>In order: the first one found on the avatar is used.</summary>
            public readonly List<Target> Targets = new List<Target>();
            public string Suffix;
            public bool Recursive, AlignPosition, AlignRotation, AlignScale, ForceOneWorldScale;
            /// <summary>Scale alignment multiplier: fixed, or from both roots' scale when <see cref="AutoScaleFactor"/>.</summary>
            public float ScaleFactor = 1;
            public bool AutoScaleFactor, ScaleFactorPowersOf10;

            /// <summary>The avatar object VRCFury links <see cref="From"/> to: the first target that resolves inside the avatar.</summary>
            public Transform Resolve(Transform avatarRoot, Func<HumanBodyBones, Transform> humanoid)
            {
                foreach (var target in Targets)
                {
                    var t = target.UseBone ? humanoid(target.Bone) : !target.UseObject ? avatarRoot : target.Object != null ? target.Object.transform : null;
                    if (t != null && !string.IsNullOrWhiteSpace(target.Offset)) t = FindPath(t, target.Offset);
                    if (t != null && t.IsChildOf(avatarRoot)) return t;
                }
                return null;
            }

            /// <summary>The multiplier of the target's scale VRCFury gives linked bones when aligning scale (ArmatureLinkService.GetScalingFactor).</summary>
            public float ScalingFactor(Transform avatarMain)
            {
                if (!AutoScaleFactor) return ScaleFactor;
                if (!Recursive || From == null || avatarMain == null) return 1;
                float factor = Mathf.Abs(From.transform.lossyScale.x) / Mathf.Abs(avatarMain.lossyScale.x);
                if (!ScaleFactorPowersOf10) return factor;
                double log = Math.Log10(factor), fraction = (log % 1 + 1) % 1;
                return (float)Math.Pow(10, fraction > 0.75 ? Math.Ceiling(log) : Math.Floor(log));
            }

            // VRCFury's relative paths: "a/b", "..", "." (ClipRewritersService.Join); a leading "/" starts from the scene root.
            private static Transform FindPath(Transform from, string path)
            {
                var t = path.StartsWith("/", StringComparison.Ordinal) ? null : from;
                bool absolute = t == null;
                foreach (var part in path.Split('/'))
                {
                    if (part.Length == 0 || part == ".") continue;
                    if (absolute) { t = from.root.name == part ? from.root : null; absolute = false; }
                    else t = part == ".." ? t.parent : t.Find(part);
                    if (t == null) return null;
                }
                return t;
            }
        }

        // Whether a skin outside the linked object uses its bones: VRCFury's "auto" merge rule.
        private static bool ExternalSkinUses(Transform obj)
        {
            var avatar = obj.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var root = avatar != null ? avatar.transform : obj.root;
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.transform.IsChildOf(obj)) continue;
                if (skin.rootBone != null && skin.rootBone.IsChildOf(obj)) return true;
                foreach (var bone in skin.bones) if (bone != null && bone.IsChildOf(obj)) return true;
            }
            return false;
        }

        public static IEnumerable<Link> ArmatureLinks(GameObject root)
        {
            foreach (var (c, feature, kind) in Features(root))
            {
                if (kind != "ArmatureLink") continue;
                // Without a Link From object VRCFury skips the link.
                var link = new Link
                {
                    Component = c,
                    From = Field(feature, "propBone") as GameObject,
                    Recursive = Field(feature, "recursive") as bool? ?? false,
                    Suffix = Field(feature, "removeBoneSuffix") as string,
                    ForceOneWorldScale = Field(feature, "forceOneWorldScale") as bool? ?? false,
                    ScaleFactor = Field(feature, "skinRewriteScalingFactor") as float? ?? 1,
                    AutoScaleFactor = Field(feature, "autoScaleFactor") as bool? ?? true,
                    ScaleFactorPowersOf10 = Field(feature, "scalingFactorPowersOf10Only") as bool? ?? true,
                };
                // Links saved by older VRCFury versions are only upgraded when VRCFury builds or shows them: read them the
                // way its upgrade does (Model/Feature/ArmatureLink.cs).
                int version = Field(feature, "version") as int? ?? -1;
                if (version >= 0 && version < 7)
                {
                    string mode = version < 1 ? Field(feature, "useBoneMerging") as bool? == true ? "SkinRewrite" : "MergeAsChildren" : Field(feature, "linkMode")?.ToString();
                    if (version < 2) link.ScaleFactor = 1;
                    if (version < 4 && mode != "SkinRewrite") link.ScaleFactor = 0;
                    if (version < 5 && mode == "MergeAsChildren") link.ScaleFactorPowersOf10 = false;
                    link.Recursive = mode == "ReparentRoot" ? false : mode == "Auto" ? link.From != null && ExternalSkinUses(link.From.transform) : true;
                    string keep = version < 3 ? Field(feature, "keepBoneOffsets") as bool? == true ? "Yes" : "No" : Field(feature, "keepBoneOffsets2")?.ToString();
                    link.AlignPosition = link.AlignRotation = link.AlignScale = keep == "Auto" || keep == null ? link.Recursive : keep == "No";
                    link.AutoScaleFactor = link.ScaleFactor <= 0 && link.Recursive;
                    if (link.ScaleFactor <= 0) link.ScaleFactor = 1;
                }
                else
                {
                    link.AlignPosition = Field(feature, "alignPosition") as bool? ?? link.Recursive;
                    link.AlignRotation = Field(feature, "alignRotation") as bool? ?? link.Recursive;
                    link.AlignScale = Field(feature, "alignScale") as bool? ?? link.Recursive;
                }
                if (version >= 0 && version < 6)
                {
                    var path = Field(feature, "bonePathOnAvatar") as string;
                    if (!string.IsNullOrWhiteSpace(path)) link.Targets.Add(new Link.Target { Offset = path });
                    else
                    {
                        link.Targets.Add(new Link.Target { UseBone = true, Bone = Field(feature, "boneOnAvatar") as HumanBodyBones? ?? HumanBodyBones.Hips });
                        if (Field(feature, "fallbackBones") is IEnumerable<HumanBodyBones> fallbacks)
                            foreach (var bone in fallbacks) link.Targets.Add(new Link.Target { UseBone = true, Bone = bone });
                    }
                }
                else if (Field(feature, "linkTo") is System.Collections.IEnumerable targets)
                    foreach (var target in targets)
                        if (target != null)
                            link.Targets.Add(new Link.Target
                            {
                                UseBone = Field(target, "useBone") as bool? == true, UseObject = Field(target, "useObj") as bool? == true,
                                Bone = Field(target, "bone") as HumanBodyBones? ?? HumanBodyBones.Hips, Object = Field(target, "obj") as GameObject,
                                Offset = Field(target, "offset") as string,
                            });
                yield return link;
            }
        }

        /// <summary>Objects a VRCFury Toggle turns on or off.</summary>
        public static IEnumerable<GameObject> ToggledObjects(object toggle)
        {
            var state = Field(toggle, "state");
            if (!(Field(state, "actions") is System.Collections.IEnumerable actions)) yield break;
            foreach (var action in actions)
                if (action != null && action.GetType().Name == "ObjectToggleAction" && Field(action, "obj") is GameObject go && go != null)
                    yield return go;
        }

        public static bool Any(GameObject root, params string[] kinds) => Features(root).Any(f => kinds.Contains(f.kind));
    }
}
