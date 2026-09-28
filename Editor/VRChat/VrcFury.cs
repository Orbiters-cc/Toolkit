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
            public Component Component;
            public GameObject From;
            public HumanBodyBones ToBone = HumanBodyBones.LastBone;
            public GameObject ToObject;
            public string ToPath, Suffix;
            public bool Recursive, Align;
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
                var link = new Link
                {
                    Component = c,
                    From = Field(feature, "propBone") as GameObject ?? c.gameObject,
                    Recursive = Field(feature, "recursive") as bool? ?? false,
                    Suffix = Field(feature, "removeBoneSuffix") as string,
                };
                // Links saved by older VRCFury versions are only upgraded when VRCFury builds or shows them: read them the
                // way its upgrade does (Model/Feature/ArmatureLink.cs, versions 6 and 7).
                int version = Field(feature, "version") as int? ?? -1;
                if (version >= 0 && version < 7)
                {
                    string mode = Field(feature, "linkMode")?.ToString();
                    link.Recursive = mode == "ReparentRoot" ? false : mode == "Auto" ? ExternalSkinUses(link.From.transform) : true;
                    string keep = Field(feature, "keepBoneOffsets2")?.ToString();
                    link.Align = version < 3 ? !(Field(feature, "keepBoneOffsets") as bool? ?? false) : keep == "Auto" || keep == null ? link.Recursive : keep == "No";
                }
                else link.Align = Field(feature, "alignPosition") as bool? ?? link.Recursive;
                if (version >= 0 && version < 6)
                {
                    var path = Field(feature, "bonePathOnAvatar") as string;
                    if (string.IsNullOrWhiteSpace(path)) link.ToBone = (HumanBodyBones)Field(feature, "boneOnAvatar");
                    else link.ToPath = path;
                    yield return link;
                    continue;
                }
                // The first usable target wins, as in VRCFury.
                if (Field(feature, "linkTo") is System.Collections.IEnumerable targets)
                    foreach (var target in targets)
                    {
                        if (Field(target, "useBone") as bool? == true) link.ToBone = (HumanBodyBones)Field(target, "bone");
                        else if (Field(target, "useObj") as bool? == true) link.ToObject = Field(target, "obj") as GameObject;
                        else link.ToPath = Field(target, "offset") as string;
                        if (link.ToBone != HumanBodyBones.LastBone || link.ToObject != null || !string.IsNullOrEmpty(link.ToPath)) break;
                    }
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
