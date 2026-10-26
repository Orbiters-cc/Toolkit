using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
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
            /// <summary>A saved menu toggle turning <paramref name="target"/> on and off, with Orbiters' default icon.</summary>
            Component Toggle(GameObject host, string menuPath, GameObject target, bool defaultOn);
            /// <summary>Sets the menu icon of a toggle made here (Orbiters' default one when <paramref name="icon"/> is null), without Undo.</summary>
            void Icon(Component toggle, Texture2D icon);
            /// <summary>
            /// Merges a prop's controller, menu and parameters without changing the avatar's authored assets. VRCFury gives a
            /// Full Controller's parameters names of their own in the avatar (VF##_…), except <paramref name="globalParameters"/>
            /// ("Name", "Prefix*", "!Exception"): those keep their names, so that OSC apps find them.
            /// </summary>
            Component FullController(GameObject host, RuntimeAnimatorController controller, ScriptableObject menu, ScriptableObject parameters,
                IEnumerable<string> globalParameters = null);
            /// <summary>
            /// For a build copy (no Undo): a saved menu toggle, one at a time among those sharing <paramref name="exclusiveTag"/>,
            /// setting <paramref name="shape"/> of <paramref name="renderer"/> to 100; with <paramref name="shape"/> null, the choice
            /// that is on while all the others are off.
            /// </summary>
            Component ShapeChoice(GameObject host, string menuPath, string exclusiveTag, Renderer renderer, string shape);
        }

        public static IWriter Writer { get; set; }
        public static bool Installed => Component != null;

        public static Type Component => Find("VF.Model.VRCFury");

        // Types by full name, those not found too: a package installed later comes with a domain reload, which empties it.
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>A type by its full name, from VRCFury's assemblies or any loaded one (Modular Avatar's...); null when none has it.</summary>
        public static Type Find(string fullName)
        {
            lock (Types)
            {
                if (Types.TryGetValue(fullName, out var known)) return known;
                return Types[fullName] = Search(fullName);
            }
        }

        private static Type Search(string fullName)
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

        /// <summary>Whether a component is one of VRCFury's (what it holds builds at upload).</summary>
        public static bool IsComponent(Component value) =>
            value != null && Find("VF.Component.VRCFuryComponent") is Type type && type.IsInstanceOfType(value);

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

        /// <summary>Whether this is a copy VRCFury built already (its test copies): VRCFury refuses to build it again.</summary>
        public static bool IsTestCopy(GameObject avatarRoot) =>
            avatarRoot != null && Find("VF.Model.VRCFuryTest") is Type test && avatarRoot.GetComponent(test) != null;

        /// <summary>
        /// What VRCFury holds around each of its build steps (VRCFuryBuildContext): the assets it writes are imported once,
        /// at the end, without running the project's asset postprocessors. Dispose it when the step is done. Its build steps
        /// called directly, without their error dialog, run inside one.
        /// </summary>
        public static IDisposable BuildContext() =>
            (IDisposable)Activator.CreateInstance(Find("VF.Utils.VRCFuryBuildContext") ?? throw new InvalidOperationException("This VRCFury version has no build context."));

        /// <summary>
        /// VRCFury's own build of <paramref name="avatar"/> (its upload entry point, VRCFuryBuilder.RunMain), as one of its
        /// build steps runs it, without the SDK and without its error dialog: its errors are thrown. Then, as in every build,
        /// its controllers get copies of the project's clips and blend trees they play (<see cref="VrcFuryBuiltAnimations"/>),
        /// so the steps after it may change them.
        /// </summary>
        public static void RunMain(GameObject avatar)
        {
            var builder = Find("VF.Builder.VRCFuryBuilder") ?? throw new InvalidOperationException("VRCFury is not installed.");
            var wrapper = Find("VF.Utils.VFGameObject");
            var convert = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "op_Implicit" && m.ReturnType == wrapper && m.GetParameters()[0].ParameterType == typeof(GameObject));
            var run = builder.GetMethod("RunMain", BindingFlags.NonPublic | BindingFlags.Static);
            using (BuildContext())
            {
                try { run.Invoke(null, new[] { convert.Invoke(null, new object[] { avatar }) }); }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
            }
            VrcFuryBuiltAnimations.Own(avatar);
        }

        /// <summary>
        /// Makes VRCFury leave a build copy's Write Defaults as they are without asking: on a broken mix it would open a
        /// question in the middle of a check and save the answer on the copy, which is thrown away. Only on an avatar
        /// VRCFury builds whose own Fix Write Defaults doesn't decide already.
        /// </summary>
        public static void LeaveWriteDefaults(GameObject buildCopy)
        {
            var fix = Find("VF.Model.Feature.FixWriteDefaults");
            if (fix == null || Component == null || !Builds(buildCopy) || Features(buildCopy).Any(f => fix.IsInstanceOfType(f.feature))) return;
            var feature = Activator.CreateInstance(fix, true);
            var mode = fix.GetField("mode");
            mode.SetValue(feature, Enum.Parse(mode.FieldType, "Disabled"));
            Component.GetField("content").SetValue(buildCopy.AddComponent(Component), feature);
        }

        /// <summary>
        /// Whether VRCFury puts the synced parameters of this blueprint's Android and iOS uploads in the order of its PC
        /// upload (its "align mobile parameters" setting, on by default), and whether it can yet: it reads the PC order
        /// that this computer saved when it uploaded the PC version.
        /// </summary>
        public static (bool Aligns, bool PcOrderKnown) MobileAlignment(string blueprintId)
        {
            if (!Installed || !EditorPrefs.GetBool("com.vrcfury.alignMobile", true)) return (false, false);
            if (string.IsNullOrEmpty(blueprintId)) return (true, false);
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return (true, !string.IsNullOrEmpty(data) && File.Exists(Path.Combine(data, "VRCFury", "DesktopSyncData", blueprintId + ".json")));
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

            /// <summary>
            /// The avatar object VRCFury links <see cref="From"/> to: the first target that resolves inside the avatar, then,
            /// after the first humanoid bone given without an offset, the bones VRCFury falls back to when the avatar lacks
            /// it (<see cref="FallbackBones"/>).
            /// </summary>
            public Transform Resolve(Transform avatarRoot, Func<HumanBodyBones, Transform> humanoid)
            {
                var attempts = Targets.AsEnumerable();
                var firstBone = Targets.FirstOrDefault(t => t.UseBone && string.IsNullOrWhiteSpace(t.Offset));
                if (firstBone.UseBone) attempts = attempts.Concat(FallbackBones(firstBone.Bone).Select(b => new Target { UseBone = true, Bone = b }));
                foreach (var target in attempts)
                {
                    var t = target.UseBone ? humanoid(target.Bone) : !target.UseObject ? avatarRoot : target.Object != null ? target.Object.transform : null;
                    if (t != null && !string.IsNullOrWhiteSpace(target.Offset)) t = FindPath(t, target.Offset, avatarRoot);
                    if (t != null && t.IsChildOf(avatarRoot)) return t;
                }
                return null;
            }

            /// <summary>
            /// The bones VRCFury links to in order when the avatar lacks <paramref name="bone"/> (FallbackBoneUtils): a
            /// finger's joints nearer the hand, the joints of its neighbour fingers, then the hand; any other bone, its parents
            /// up to the hips (eyes and jaw: the head; a shoulder: the upper chest).
            /// </summary>
            public static IEnumerable<HumanBodyBones> FallbackBones(HumanBodyBones bone)
            {
                int value = (int)bone;
                bool left = value >= (int)HumanBodyBones.LeftThumbProximal && value <= (int)HumanBodyBones.LeftLittleDistal;
                bool right = value >= (int)HumanBodyBones.RightThumbProximal && value <= (int)HumanBodyBones.RightLittleDistal;
                if (left || right)
                {
                    int first = right ? (int)HumanBodyBones.RightThumbProximal : (int)HumanBodyBones.LeftThumbProximal;
                    int finger = (value - first) / 3, segment = (value - first) % 3;
                    for (int s = segment - 1; s >= 0; s--) yield return (HumanBodyBones)(first + finger * 3 + s);
                    var nearby = finger == 0 ? new[] { 1, 2, 3, 4 } : finger == 1 ? new[] { 2, 3, 4, 0 } : finger == 2 ? new[] { 1, 3, 4, 0 }
                        : finger == 3 ? new[] { 2, 4, 1, 0 } : new[] { 3, 2, 1, 0 };
                    foreach (int other in nearby)
                        for (int s = segment; s >= 0; s--) yield return (HumanBodyBones)(first + other * 3 + s);
                    var hand = right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand;
                    yield return hand;
                    foreach (var fallback in FallbackBones(hand)) yield return fallback;
                    yield break;
                }
                HumanBodyBones? parent;
                switch (bone)
                {
                    case HumanBodyBones.Jaw: case HumanBodyBones.LeftEye: case HumanBodyBones.RightEye: parent = HumanBodyBones.Head; break;
                    case HumanBodyBones.Head: parent = HumanBodyBones.Neck; break;
                    case HumanBodyBones.Neck: parent = HumanBodyBones.UpperChest; break;
                    case HumanBodyBones.UpperChest: parent = HumanBodyBones.Chest; break;
                    case HumanBodyBones.Chest: parent = HumanBodyBones.Spine; break;
                    case HumanBodyBones.Spine: parent = HumanBodyBones.Hips; break;
                    case HumanBodyBones.LeftHand: parent = HumanBodyBones.LeftLowerArm; break;
                    case HumanBodyBones.RightHand: parent = HumanBodyBones.RightLowerArm; break;
                    case HumanBodyBones.LeftLowerArm: parent = HumanBodyBones.LeftUpperArm; break;
                    case HumanBodyBones.RightLowerArm: parent = HumanBodyBones.RightUpperArm; break;
                    case HumanBodyBones.LeftUpperArm: parent = HumanBodyBones.LeftShoulder; break;
                    case HumanBodyBones.RightUpperArm: parent = HumanBodyBones.RightShoulder; break;
                    case HumanBodyBones.LeftShoulder: case HumanBodyBones.RightShoulder: parent = HumanBodyBones.UpperChest; break;
                    case HumanBodyBones.LeftToes: parent = HumanBodyBones.LeftFoot; break;
                    case HumanBodyBones.RightToes: parent = HumanBodyBones.RightFoot; break;
                    case HumanBodyBones.LeftFoot: parent = HumanBodyBones.LeftLowerLeg; break;
                    case HumanBodyBones.RightFoot: parent = HumanBodyBones.RightLowerLeg; break;
                    case HumanBodyBones.LeftLowerLeg: parent = HumanBodyBones.LeftUpperLeg; break;
                    case HumanBodyBones.RightLowerLeg: parent = HumanBodyBones.RightUpperLeg; break;
                    case HumanBodyBones.LeftUpperLeg: case HumanBodyBones.RightUpperLeg: parent = HumanBodyBones.Hips; break;
                    default: parent = null; break;
                }
                if (parent == null) yield break;
                yield return parent.Value;
                foreach (var fallback in FallbackBones(parent.Value)) yield return fallback;
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

            // VRCFury's relative paths (VRCFObjectPathCache.Find): "a/b", "..", "."; a leading "/" starts from the avatar's root,
            // and ".." never leaves it.
            private static Transform FindPath(Transform from, string path, Transform avatarRoot)
            {
                var t = path.StartsWith("/", StringComparison.Ordinal) ? avatarRoot : from;
                foreach (var part in path.Split('/'))
                {
                    if (part.Length == 0 || part == ".") continue;
                    if (part == "..")
                    {
                        if (t == avatarRoot) return null;
                        t = t.parent;
                    }
                    else t = t.Find(part);
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
