using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>One transform that follows an avatar bone, at a fixed offset in that bone's space.</summary>
    public struct FollowLink
    {
        public Transform Follower, Target;
        public Matrix4x4 Offset;
        /// <summary>The follower's world scale as a multiple of the target's, or null to keep its own scale.</summary>
        public Vector3? Scale;
        /// <summary>Which rule produced the link, for the accessory list ("VRCFury", "My Avatar", "Name match").</summary>
        public string Source;
        public Transform Accessory;

        public void Apply()
        {
            if (Scale.HasValue)
            {
                // Per axis, like VRCFury's world scale.
                var world = Vector3.Scale(Target.lossyScale, Scale.Value);
                var parent = Follower.parent != null ? Follower.parent.lossyScale : Vector3.one;
                Follower.localScale = new Vector3(world.x / parent.x, world.y / parent.y, world.z / parent.z);
            }
            // Rotation composed separately: a non-uniformly scaled avatar would skew the matrix's rotation.
            Follower.SetPositionAndRotation(Target.TransformPoint(Offset.GetColumn(3)), Target.rotation * Offset.rotation);
        }
    }

    /// <summary>
    /// How every accessory under an avatar follows it once built: My Avatar attachments, VRCFury Armature Links, and for
    /// clothing without either, the bone matcher. The posing preview and the build share these rules.
    /// </summary>
    public static class AttachmentFollow
    {
        public static List<FollowLink> Links(Transform avatarRoot, bool matchUnlinkedClothing = true)
        {
            var result = new List<FollowLink>();
            if (avatarRoot == null) return result;
            var body = AttachmentPlanner.Body(avatarRoot);
            var skeleton = AvatarSkeleton.Bones(avatarRoot, body);
            var avatarRest = ArmatureRest.Frames(avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.bones.Any(b => b != null && skeleton.Contains(b))));
            var index = AvatarBoneIndex.Build(avatarRoot, skeleton);
            var handled = new HashSet<Transform>();

            foreach (var attachment in avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                var root = attachment.transform;
                if (attachment.mode == OrbitersAttachment.AttachMode.Merge)
                {
                    var rest = ArmatureRest.Frames(attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                    foreach (var link in attachment.links)
                        if (link.from != null && link.to != null)
                            result.Add(new FollowLink { Follower = link.from, Target = link.to, Offset = RestOffset(link.from, link.to, rest, avatarRest), Source = "My Avatar", Accessory = root });
                    handled.UnionWith(root.GetComponentsInChildren<Transform>(true));
                }
                else if (attachment.mode == OrbitersAttachment.AttachMode.Parent && attachment.parent != null)
                {
                    result.Add(new FollowLink { Follower = root, Target = attachment.parent, Offset = Current(root, attachment.parent), Source = "My Avatar", Accessory = root });
                    handled.UnionWith(root.GetComponentsInChildren<Transform>(true));
                }
            }

            foreach (var link in VrcFury.ArmatureLinks(avatarRoot.gameObject))
            {
                if (link.From == null || handled.Contains(link.From.transform)) continue;
                var target = link.Resolve(avatarRoot, index.Humanoid);
                if (target == null) continue;
                var accessory = Accessory(link.From.transform, avatarRoot);
                var from = link.From.transform;
                float scaleFactor = link.ScalingFactor(target);
                result.Add(VrcFuryLink(link, from, target, scaleFactor, accessory));
                handled.Add(from);
                if (!link.Recursive) { handled.UnionWith(from.GetComponentsInChildren<Transform>(true)); continue; }
                // VRCFury merges children with the same name, after removing the suffix the link root adds to its bone name.
                string suffix = !string.IsNullOrWhiteSpace(link.Suffix) ? link.Suffix : from.name != target.name && from.name.Contains(target.name) ? from.name.Replace(target.name, "") : "";
                var queue = new Queue<(Transform prop, Transform avatar)>();
                queue.Enqueue((from, target));
                while (queue.Count > 0)
                {
                    var (prop, avatar) = queue.Dequeue();
                    foreach (Transform child in prop)
                    {
                        handled.Add(child);
                        var match = avatar != null ? avatar.Find(!string.IsNullOrWhiteSpace(suffix) ? child.name.Replace(suffix, "") : child.name) : null;
                        if (match != null) result.Add(VrcFuryLink(link, child, match, scaleFactor, accessory));
                        queue.Enqueue((child, match));
                    }
                }
            }

            if (!matchUnlinkedClothing) return result;
            // Clothing armatures nothing links yet (merged by hand or by another tool): follow the bone matcher.
            foreach (var group in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                         .Where(r => r.sharedMesh != null && r.bones.Length > 0 && r.bones.Where(b => b != null).All(b => !skeleton.Contains(b)) && !handled.Contains(r.transform))
                         .GroupBy(r => ArmatureRest.CommonRoot(r, avatarRoot)))
            {
                if (group.Key == null) continue;
                var bones = group.SelectMany(r => r.bones).Where(b => b != null && !handled.Contains(b)).Distinct().ToList();
                var rest = ArmatureRest.Frames(group);
                var accessory = Accessory(group.Key, avatarRoot);
                foreach (var match in BoneMatcher.MatchHierarchy(bones, index, BoneMatcher.DetectAffixes(bones, index)))
                    if (match.Matched)
                        result.Add(new FollowLink { Follower = match.Source, Target = match.Target, Offset = RestOffset(match.Source, match.Target, rest, avatarRest), Source = "Name match", Accessory = accessory });
            }
            return result;
        }

        /// <summary>
        /// The follower's offset from its target at rest, from both meshes' bind poses, so it does not depend on the current
        /// pose; the current offset when either bone is not skinned.
        /// </summary>
        public static Matrix4x4 RestOffset(Transform follower, Transform target, Dictionary<Transform, Matrix4x4> followerRest, Dictionary<Transform, Matrix4x4> targetRest)
        {
            if (followerRest.TryGetValue(follower, out var f) && targetRest.TryGetValue(target, out var t) && Mathf.Abs(t.determinant) > 1e-8f)
                return t.inverse * f;
            return Current(follower, target);
        }

        public static Matrix4x4 Current(Transform follower, Transform target) => target.worldToLocalMatrix * follower.localToWorldMatrix;

        // A bone VRCFury links is first put on its avatar bone for each of position, rotation and scale the link aligns
        // (each bone on its own, ArmatureLinkService.ApplyOne), then stays where it is relative to that bone.
        private static FollowLink VrcFuryLink(VrcFury.Link link, Transform follower, Transform target, float scaleFactor, Transform accessory)
        {
            var position = link.AlignPosition ? target.position : follower.position;
            var rotation = link.AlignRotation ? target.rotation : follower.rotation;
            var targetScale = target.lossyScale;
            var followerScale = follower.lossyScale;
            return new FollowLink
            {
                Follower = follower, Target = target, Source = "VRCFury", Accessory = accessory,
                Offset = Matrix4x4.TRS(target.InverseTransformPoint(position), Quaternion.Inverse(target.rotation) * rotation, Vector3.one),
                Scale = link.ForceOneWorldScale ? new Vector3(1 / targetScale.x, 1 / targetScale.y, 1 / targetScale.z)
                    : link.AlignScale ? Vector3.one * scaleFactor
                    : new Vector3(followerScale.x / targetScale.x, followerScale.y / targetScale.y, followerScale.z / targetScale.z),
            };
        }

        // The object directly under the avatar root holding it: what the user calls the accessory.
        private static Transform Accessory(Transform t, Transform avatarRoot)
        {
            while (t.parent != null && t.parent != avatarRoot) t = t.parent;
            return t;
        }
    }
}
