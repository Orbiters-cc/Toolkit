using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using LocalPose = Orbiters.Toolkit.VRChat.OrbitersAttachment.LocalPose;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Clothing made for another avatar (other height, other proportions) fitted onto this one's armature: its key bones are
    /// matched to the avatar's humanoid bones and compared. When its size differs by more than <see cref="ScaleThreshold"/>
    /// or it stands more than <see cref="OffsetThreshold"/> of the avatar's height away, it is resized to the avatar's
    /// size, moved onto its hips, and each matched key bone is put on its avatar bone (parents first), so the clothing also
    /// takes the avatar's proportions. What changes is recorded on the attachment: <see cref="Cancel"/> puts it back.
    /// </summary>
    public static class AttachmentFit
    {
        public const float ScaleThreshold = 0.08f, OffsetThreshold = 0.05f;

        // The bones that give an armature its size and proportions, and the segments measured between them.
        private static readonly HumanBodyBones[] Keys =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        private static readonly (HumanBodyBones from, HumanBodyBones to)[] Segments =
        {
            (HumanBodyBones.Hips, HumanBodyBones.Spine), (HumanBodyBones.Spine, HumanBodyBones.Chest), (HumanBodyBones.Chest, HumanBodyBones.Neck),
            (HumanBodyBones.Neck, HumanBodyBones.Head), (HumanBodyBones.Hips, HumanBodyBones.Chest), (HumanBodyBones.Hips, HumanBodyBones.Head),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm),
            (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm), (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm), (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
        };

        /// <summary>How an accessory's armature compares with the avatar's.</summary>
        public sealed class Measure
        {
            /// <summary>Its key bones (clothing bone → avatar bone), parents first.</summary>
            public readonly List<(Transform source, Transform target)> Pairs = new List<(Transform, Transform)>();
            /// <summary>Avatar size / accessory size, from the segments both have.</summary>
            public float Scale = 1f;
            /// <summary>How far its first key bone stands from the avatar's, as a share of the avatar's height.</summary>
            public float Offset;
            public int Segments;
            public float PoseAngle;
            internal readonly Dictionary<Transform, (Transform source, Transform target)> Tips = new Dictionary<Transform, (Transform, Transform)>();
            internal readonly Dictionary<Transform, HumanBodyBones> Roles = new Dictionary<Transform, HumanBodyBones>();

            public bool NeedsFit => Pairs.Count > 0 && (Segments > 0 && Mathf.Abs(Scale - 1f) > ScaleThreshold || Offset > OffsetThreshold || PoseAngle > 2f);
        }

        /// <summary>Compares the armature of <paramref name="root"/> (under the avatar) with the avatar's; null without key bones in common.</summary>
        public static Measure Compare(GameObject root, Transform avatarRoot)
        {
            if (root == null || avatarRoot == null) return null;
            var body = AttachmentPlanner.Body(avatarRoot, root.transform);
            var skeleton = AvatarSkeleton.Bones(avatarRoot, body);
            skeleton.RemoveWhere(b => b.IsChildOf(root.transform));
            var index = AvatarBoneIndex.Build(avatarRoot, skeleton);
            var bones = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
                .SelectMany(r => r.bones).Where(b => b != null && b.IsChildOf(root.transform) && b != root.transform).Distinct().ToList();
            if (bones.Count == 0) return null;

            var byRole = new Dictionary<HumanBodyBones, (Transform source, Transform target)>();
            foreach (var match in BoneMatcher.MatchHierarchy(bones, index, BoneMatcher.DetectAffixes(bones, index)))
            {
                if (!match.Matched || match.Ambiguous || !index.TryGetHumanoid(match.Target, out var id) || !Keys.Contains(id) || byRole.ContainsKey(id)) continue;
                byRole[id] = (match.Source, match.Target);
            }
            if (byRole.Count == 0) return null;

            var measure = new Measure();
            measure.Pairs.AddRange(byRole.Values.OrderBy(p => Depth(p.source)));
            foreach (var role in byRole) measure.Roles[role.Value.source] = role.Key;
            // Exported terminal limbs often retain only an unweighted *_end marker (a sleeve has no hand).
            // Use it for direction only; never move or link the marker to the avatar.
            foreach (var role in byRole)
            {
                var pair = role.Value;
                var next = NextLimb(role.Key);
                var targetTip = next != HumanBodyBones.LastBone ? index.Humanoid(next) : null;
                if (targetTip == null || pair.source.childCount != 1) continue;
                var tip = pair.source.GetChild(0);
                if (tip.name.EndsWith("_end", System.StringComparison.OrdinalIgnoreCase)) measure.Tips[pair.source] = (tip, targetTip);
            }
            foreach (var pair in measure.Pairs)
            {
                var child = ChildPair(measure, pair.source);
                if (child.source != null)
                    measure.PoseAngle = Mathf.Max(measure.PoseAngle, Vector3.Angle(child.source.position - pair.source.position, child.target.position - pair.target.position));
            }
            float avatarLength = 0f, ownLength = 0f;
            foreach (var (from, to) in Segments)
            {
                if (!byRole.TryGetValue(from, out var a) || !byRole.TryGetValue(to, out var b)) continue;
                float own = Vector3.Distance(a.source.position, b.source.position), avatar = Vector3.Distance(a.target.position, b.target.position);
                if (own < 1e-4f || avatar < 1e-4f) continue;
                ownLength += own; avatarLength += avatar; measure.Segments++;
            }
            if (measure.Segments > 0) measure.Scale = avatarLength / ownLength;
            float height = Height(index, avatarRoot);
            var first = measure.Pairs[0];
            measure.Offset = height > 0f ? Vector3.Distance(first.source.position, first.target.position) / height : 0f;
            return measure;
        }

        /// <summary>
        /// Fits the attachment's armature to the avatar when it needs it (see <see cref="Measure.NeedsFit"/>), with Undo.
        /// Returns the measure that led to the fit, null when nothing was changed.
        /// </summary>
        public static Measure Fit(OrbitersAttachment attachment, Transform avatarRoot)
        {
            if (attachment == null) return null;
            var measure = Compare(attachment.gameObject, avatarRoot);
            if (measure == null || !measure.NeedsFit) return null;
            var root = attachment.transform;
            var touched = new List<Transform> { root };
            touched.AddRange(measure.Pairs.Select(p => p.source).Where(t => t != root));
            var before = touched.ToDictionary(t => t, Pose);
            Undo.RecordObjects(touched.ToArray(), "Fit " + attachment.name + " to the avatar");

            // Its size first, then onto the avatar where its first key bone (the hips, usually) is.
            if (measure.Segments > 0) root.localScale *= measure.Scale;
            var (anchor, anchorTarget) = measure.Pairs[0];
            root.position += anchorTarget.position - anchor.position;
            // Aim along the anatomical segment, not the target's local axes (exporters use different bone rolls).
            // Read the child before moving it. Parent rotation carries terminal and unmatched extra bones naturally.
            foreach (var (source, target) in measure.Pairs)
            {
                if (source == root) continue;
                var child = ChildPair(measure, source);
                if (child.source != null)
                {
                    var from = child.source.position - source.position;
                    var to = child.target.position - target.position;
                    if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                        source.rotation = Quaternion.FromToRotation(from, to) * source.rotation;
                }
                source.position = target.position;
            }

            Undo.RecordObject(attachment, "Fit " + attachment.name + " to the avatar");
            foreach (var t in touched)
            {
                int existing = attachment.fitted.FindIndex(f => f.transform == t);
                var pose = new OrbitersAttachment.FittedPose { transform = t, before = existing >= 0 ? attachment.fitted[existing].before : before[t], after = Pose(t) };
                if (existing >= 0) attachment.fitted[existing] = pose;
                else attachment.fitted.Add(pose);
                PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            }
            AttachmentInstaller.Dirty(attachment);
            AttachmentVolumeFit.Apply(attachment, avatarRoot, measure);
            return measure;
        }

        public static bool Fitted(OrbitersAttachment attachment) => attachment != null && attachment.fitted.Count > 0;

        // Prefer the nearest matched descendant. At a branch, the central spine/neck wins over shoulders or legs.
        private static (Transform source, Transform target) ChildPair(Measure measure, Transform source)
        {
            var child = measure.Pairs.Where(p => p.source != source && p.source.IsChildOf(source))
                .OrderBy(p => Depth(p.source))
                .ThenBy(p => Mathf.Abs(source.InverseTransformPoint(p.source.position).x))
                .FirstOrDefault();
            return child.source != null ? child : measure.Tips.TryGetValue(source, out var tip) ? tip : default;
        }

        private static HumanBodyBones NextLimb(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.LeftUpperArm: return HumanBodyBones.LeftLowerArm;
                case HumanBodyBones.RightUpperArm: return HumanBodyBones.RightLowerArm;
                case HumanBodyBones.LeftLowerArm: return HumanBodyBones.LeftHand;
                case HumanBodyBones.RightLowerArm: return HumanBodyBones.RightHand;
                case HumanBodyBones.LeftUpperLeg: return HumanBodyBones.LeftLowerLeg;
                case HumanBodyBones.RightUpperLeg: return HumanBodyBones.RightLowerLeg;
                case HumanBodyBones.LeftLowerLeg: return HumanBodyBones.LeftFoot;
                case HumanBodyBones.RightLowerLeg: return HumanBodyBones.RightFoot;
                default: return HumanBodyBones.LastBone;
            }
        }

        /// <summary>Puts back what the fit changed, with Undo, where it is still as the fit left it.</summary>
        public static void Cancel(OrbitersAttachment attachment)
        {
            if (!Fitted(attachment)) return;
            AttachmentVolumeFit.Cancel(attachment);
            var still = attachment.fitted.Where(f => f.transform != null && Same(Pose(f.transform), f.after)).ToList();
            if (still.Count > 0) Undo.RecordObjects(still.Select(f => (Object)f.transform).ToArray(), "Cancel the fit of " + attachment.name);
            // Children first: each is put back in its own parent's space, so the order does not matter for local values.
            foreach (var f in still)
            {
                f.transform.localPosition = f.before.position;
                f.transform.localRotation = f.before.rotation;
                f.transform.localScale = f.before.scale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(f.transform);
            }
            Undo.RecordObject(attachment, "Cancel the fit of " + attachment.name);
            attachment.fitted.Clear();
            AttachmentInstaller.Dirty(attachment);
        }

        // Hips to head, else the body's height.
        private static float Height(AvatarBoneIndex index, Transform avatarRoot)
        {
            var hips = index.Humanoid(HumanBodyBones.Hips);
            var head = index.Humanoid(HumanBodyBones.Head);
            if (hips != null && head != null) return Vector3.Distance(hips.position, head.position) * 2f;
            var body = AttachmentPlanner.Body(avatarRoot);
            return body != null ? body.bounds.size.y : 0f;
        }

        private static LocalPose Pose(Transform t) => new LocalPose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };

        private static bool Same(LocalPose a, LocalPose b) =>
            (a.position - b.position).sqrMagnitude < 1e-8f && (a.scale - b.scale).sqrMagnitude < 1e-8f && Quaternion.Angle(a.rotation, b.rotation) < 0.01f;

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }
    }
}
