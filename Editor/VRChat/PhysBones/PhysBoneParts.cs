using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.Posing;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Orbiters.Toolkit.Editor.VRChat.PhysBones
{
    public enum PhysBonePart { Hair, Ears, Tail, Toes }

    /// <summary>Who may grab or pose a PhysBone in VRChat. The SDK has no friends-only setting.</summary>
    public enum PhysBoneAccess { Nobody, OnlyMe, Everyone }

    /// <summary>The PhysBones of one part of the avatar, their shared settings and the chains that have no physics yet.</summary>
    public sealed class PhysBonePartInfo
    {
        public PhysBonePart Part;
        public readonly List<VRCPhysBone> PhysBones = new List<VRCPhysBone>();
        /// <summary>First bone of each chain named like this part that no PhysBone drives.</summary>
        public readonly List<Transform> Unphysicked = new List<Transform>();
        /// <summary>Null when the PhysBones disagree or use a setting outside the three choices.</summary>
        public PhysBoneAccess? Grabbing, Posing;
        /// <summary>Null when the PhysBones disagree.</summary>
        public float? MaxStretch;
    }

    /// <summary>
    /// Finds an avatar's hair, ear, tail and toe PhysBones by name, reads and changes who may grab and pose them and how
    /// far they stretch, and adds PhysBones to chains of those parts that have none.
    /// </summary>
    public static class PhysBoneParts
    {
        public const string ContainerName = "PhysBones";
        /// <summary>Largest stretch offered, as a fraction of the chain's length (2 = three times as long).</summary>
        public const float MaxStretchLimit = 2f;

        private static readonly string[] HairWords = { "hair", "bang", "fringe", "ponytail", "pigtail", "twintail", "braid", "ahoge", "sidelock", "forelock", "mane", "kami" };
        private static readonly Regex Words = new Regex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+");

        public static string Label(PhysBonePart part) =>
            part == PhysBonePart.Hair ? "Hair" : part == PhysBonePart.Ears ? "Ears" : part == PhysBonePart.Tail ? "Tail" : "Toes";

        /// <summary>One chain of the part, in a sentence: "2 ear chains".</summary>
        public static string Noun(PhysBonePart part) =>
            part == PhysBonePart.Hair ? "hair" : part == PhysBonePart.Ears ? "ear" : part == PhysBonePart.Tail ? "tail" : "toe";

        public static string Label(PhysBoneAccess access) => access == PhysBoneAccess.Nobody ? "Nobody" : access == PhysBoneAccess.OnlyMe ? "Only me" : "Everyone";

        /// <summary>The part a bone or object name describes: "BackHairA1", "Ponytail.002", "Ear_L", "Tail_Rt", "Toe_L", "PhysBeans"...</summary>
        public static bool TryClassify(string name, out PhysBonePart part)
        {
            part = default;
            if (string.IsNullOrEmpty(name)) return false;
            var lower = name.ToLowerInvariant();
            if (lower.Contains("collider") || lower.Contains("contact")) return false;
            // Hair words first: "ponytail" and "twintail" are hair, not tails.
            if (HairWords.Any(lower.Contains)) { part = PhysBonePart.Hair; return true; }
            var words = Words.Matches(name).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToList();
            // A word of its own: "Ear_L", "LeftEar", "Ears.001", but not "Gear", "Earring" or "Earth".
            if (words.Any(w => w.StartsWith("ear") && !w.StartsWith("earring") && !w.StartsWith("earth"))) { part = PhysBonePart.Ears; return true; }
            if (lower.Contains("tail")) { part = PhysBonePart.Tail; return true; }
            if (lower.Contains("bean") || words.Any(w => w.StartsWith("toe"))) { part = PhysBonePart.Toes; return true; }
            return false;
        }

        /// <summary>A PhysBone's part, from the bone it starts at, then from the object that holds it.</summary>
        public static bool TryClassify(VRCPhysBone physBone, out PhysBonePart part)
        {
            part = default;
            if (physBone == null) return false;
            var root = physBone.GetRootTransform();
            return root != null && TryClassify(root.name, out part) || TryClassify(physBone.name, out part);
        }

        public static List<PhysBonePartInfo> Scan(Transform avatarRoot)
        {
            var parts = new Dictionary<PhysBonePart, PhysBonePartInfo>();
            PhysBonePartInfo Info(PhysBonePart part) => parts.TryGetValue(part, out var info) ? info : parts[part] = new PhysBonePartInfo { Part = part };
            if (avatarRoot == null) return new List<PhysBonePartInfo>();

            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            foreach (var physBone in physBones)
                if (TryClassify(physBone, out var part)) Info(part).PhysBones.Add(physBone);

            // Chains without physics: bones of the avatar's own armature named like a part, outside every PhysBone,
            // that are not humanoid bones nor driven by something else. Only the first bone of each chain is listed.
            var covered = SimulatedTransforms(physBones);
            var humanoid = AvatarSkeleton.HumanoidBones(avatarRoot);
            foreach (var bone in AvatarSkeleton.Bones(avatarRoot))
            {
                if (covered.Contains(bone) || humanoid.Contains(bone) || IsEndBone(bone) || !TryClassify(bone.name, out var part)) continue;
                if (bone.parent != null && !covered.Contains(bone.parent) && !humanoid.Contains(bone.parent) && TryClassify(bone.parent.name, out var parentPart) && parentPart == part) continue;
                if (bone.GetComponents<Component>().Length > 1) continue;
                // An accessory placed on a bone (its own armature and mesh, maybe with physics inside) is not a chain.
                if (bone.GetComponentInChildren<Renderer>(true) != null || bone.GetComponentsInChildren<Transform>(true).Any(covered.Contains)) continue;
                Info(part).Unphysicked.Add(bone);
            }

            foreach (var info in parts.Values)
            {
                info.Grabbing = Shared(info.PhysBones.Select(p => Access(p.allowGrabbing, p.grabFilter)));
                info.Posing = Shared(info.PhysBones.Select(p => Access(p.allowPosing, p.poseFilter)));
                var stretches = info.PhysBones.Select(p => p.maxStretch).Distinct().ToList();
                info.MaxStretch = stretches.Count == 1 ? stretches[0] : (float?)null;
            }
            return parts.Values.OrderBy(i => i.Part).ToList();
        }

        internal static HashSet<Transform> SimulatedTransforms(IEnumerable<VRCPhysBone> physBones)
        {
            var covered = new HashSet<Transform>();
            foreach (var physBone in physBones)
            {
                var root = physBone.GetRootTransform();
                if (root == null) continue;
                var ignored = new HashSet<Transform>(physBone.ignoreTransforms ?? new List<Transform>());
                var pending = new Stack<Transform>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (ignored.Contains(current)) continue; // Ignoring a transform excludes its whole branch.
                    covered.Add(current);
                    foreach (Transform child in current) pending.Push(child);
                }
            }
            return covered;
        }

        public static void SetGrabbing(IEnumerable<VRCPhysBone> physBones, PhysBoneAccess access)
        {
            Edit(physBones, "Change who can grab", physBone =>
            {
                var filter = physBone.grabFilter;
                physBone.allowGrabbing = Apply(access, ref filter);
                physBone.grabFilter = filter;
                // Posing happens while grabbing, so nobody can pose more than they can grab.
                var posing = Access(physBone.allowPosing, physBone.poseFilter);
                if (posing.HasValue ? posing.Value > access : access != PhysBoneAccess.Everyone)
                {
                    var poseFilter = physBone.poseFilter;
                    physBone.allowPosing = Apply(access, ref poseFilter);
                    physBone.poseFilter = poseFilter;
                }
            });
        }

        public static void SetPosing(IEnumerable<VRCPhysBone> physBones, PhysBoneAccess access)
        {
            Edit(physBones, "Change who can pose", physBone =>
            {
                var filter = physBone.poseFilter;
                physBone.allowPosing = Apply(access, ref filter);
                physBone.poseFilter = filter;
            });
        }

        public static void SetMaxStretch(IEnumerable<VRCPhysBone> physBones, float maxStretch)
        {
            maxStretch = Mathf.Max(0f, maxStretch);
            Edit(physBones, "Change stretch", physBone => physBone.maxStretch = maxStretch);
        }

        /// <summary>
        /// Adds a PhysBone to each chain, held by "PhysBones/&lt;Part&gt;" under the avatar root. Settings suit the part;
        /// who can grab and pose, and the stretch, copy <paramref name="like"/> when given.
        /// </summary>
        public static List<VRCPhysBone> AddPhysics(Transform avatarRoot, PhysBonePart part, IEnumerable<Transform> chains, PhysBonePartInfo like = null)
        {
            var created = new List<VRCPhysBone>();
            var roots = chains.Where(c => c != null && c.IsChildOf(avatarRoot)).Distinct().ToList();
            if (avatarRoot == null || roots.Count == 0) return created;
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Add {Label(part).ToLowerInvariant()} physics");

            var container = Child(avatarRoot, ContainerName);
            var host = Child(container, Label(part));
            foreach (var root in roots)
            {
                var physBone = Undo.AddComponent<VRCPhysBone>(host.gameObject);
                physBone.rootTransform = root;
                physBone.version = VRCPhysBoneBase.Version.Version_1_1;
                physBone.integrationType = VRCPhysBoneBase.IntegrationType.Advanced;
                physBone.limitType = VRCPhysBoneBase.LimitType.Angle;
                switch (part)
                {
                    case PhysBonePart.Hair: Motion(physBone, pull: 0.2f, spring: 0.2f, stiffness: 0.2f, gravity: 0.1f, angle: 60f); break;
                    case PhysBonePart.Ears: Motion(physBone, pull: 0.25f, spring: 0.3f, stiffness: 0.35f, gravity: 0f, angle: 45f); break;
                    case PhysBonePart.Tail: Motion(physBone, pull: 0.15f, spring: 0.35f, stiffness: 0.2f, gravity: 0.05f, angle: 75f); break;
                    // Toes are short and the feet move fast: soft enough to wiggle when walking, springy enough to settle.
                    default: Motion(physBone, pull: 0.3f, spring: 0.45f, stiffness: 0.15f, gravity: 0.05f, angle: 45f); break;
                }
                // Several branches (a toe root and its digits) each swing on their own instead of averaging into one.
                physBone.multiChildType = root.childCount > 1 ? VRCPhysBoneBase.MultiChildType.Ignore : VRCPhysBoneBase.MultiChildType.First;
                // The last bone of a chain has no length to swing unless an end bone follows it: without one, add a
                // virtual tip that continues the bone's direction, or the tips (every digit of a toe) never move.
                var leaf = Leaf(root);
                if (!IsEndBone(leaf) && leaf.parent != null)
                    physBone.endpointPosition = leaf.InverseTransformVector(leaf.position - leaf.parent.position) * 0.5f;
                // Hands grab what is within the radius: at 0 a chain can only be grabbed at its exact line.
                physBone.radius = Mathf.Max(0.005f, ChainLength(root) * 0.15f) / Mathf.Max(1e-4f, root.lossyScale.x);
                var grab = like?.Grabbing ?? PhysBoneAccess.Everyone;
                var pose = like?.Posing ?? PhysBoneAccess.OnlyMe;
                var grabFilter = physBone.grabFilter; physBone.allowGrabbing = Apply(grab, ref grabFilter); physBone.grabFilter = grabFilter;
                var poseFilter = physBone.poseFilter; physBone.allowPosing = Apply(pose > grab ? grab : pose, ref poseFilter); physBone.poseFilter = poseFilter;
                physBone.maxStretch = like?.MaxStretch ?? 0f;
                created.Add(physBone);
            }
            Undo.CollapseUndoOperations(group);
            return created;
        }

        // The deepest bone along the first branch: its length is what an end point continues.
        private static Transform Leaf(Transform root)
        {
            var bone = root;
            while (bone.childCount > 0) bone = bone.GetChild(0);
            return bone;
        }

        // Longest distance from the root to a bone of the chain, in world units.
        private static float ChainLength(Transform root) =>
            root.GetComponentsInChildren<Transform>(true).Max(t => Vector3.Distance(root.position, t.position));

        private static void Motion(VRCPhysBone physBone, float pull, float spring, float stiffness, float gravity, float angle)
        {
            physBone.pull = pull;
            physBone.spring = spring;
            physBone.stiffness = stiffness;
            physBone.gravity = gravity;
            physBone.gravityFalloff = 0.5f;
            physBone.maxAngleX = angle;
        }

        private static Transform Child(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing;
            var created = new GameObject(name).transform;
            Undo.RegisterCreatedObjectUndo(created.gameObject, "Add physics");
            Undo.SetTransformParent(created, parent, "Add physics");
            created.localPosition = Vector3.zero;
            created.localRotation = Quaternion.identity;
            created.localScale = Vector3.one;
            return created;
        }

        private static bool IsEndBone(Transform bone) =>
            bone.childCount == 0 && Regex.IsMatch(bone.name, @"(^|[\s._-])end(\.\d+)?$|End(\.\d+)?$", RegexOptions.IgnoreCase);

        private static void Edit(IEnumerable<VRCPhysBone> physBones, string undoName, Action<VRCPhysBone> edit)
        {
            var list = physBones.Where(p => p != null).ToList();
            if (list.Count == 0) return;
            Undo.RecordObjects(list.Cast<UnityEngine.Object>().ToArray(), undoName);
            foreach (var physBone in list)
            {
                edit(physBone);
                PrefabUtility.RecordPrefabInstancePropertyModifications(physBone);
            }
        }

        private static PhysBoneAccess? Access(VRCPhysBoneBase.AdvancedBool allowed, VRCPhysBoneBase.PermissionFilter filter)
        {
            switch (allowed)
            {
                case VRCPhysBoneBase.AdvancedBool.False: return PhysBoneAccess.Nobody;
                case VRCPhysBoneBase.AdvancedBool.True: return PhysBoneAccess.Everyone;
                default:
                    if (filter.allowSelf && filter.allowOthers) return PhysBoneAccess.Everyone;
                    if (filter.allowSelf) return PhysBoneAccess.OnlyMe;
                    if (!filter.allowOthers) return PhysBoneAccess.Nobody;
                    return null;
            }
        }

        private static VRCPhysBoneBase.AdvancedBool Apply(PhysBoneAccess access, ref VRCPhysBoneBase.PermissionFilter filter)
        {
            filter.allowSelf = access != PhysBoneAccess.Nobody;
            filter.allowOthers = access == PhysBoneAccess.Everyone;
            return access == PhysBoneAccess.Nobody ? VRCPhysBoneBase.AdvancedBool.False
                : access == PhysBoneAccess.Everyone ? VRCPhysBoneBase.AdvancedBool.True
                : VRCPhysBoneBase.AdvancedBool.Other;
        }

        private static PhysBoneAccess? Shared(IEnumerable<PhysBoneAccess?> values)
        {
            var distinct = values.Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }
    }
}
