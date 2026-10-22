using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// One of the bones a follower stuck to the skin is blended between: its share and the follower's offset from the
    /// bone's position and rotation (<see cref="SkinStick.Frame"/>, without its scale, as a parent constraint measures it).
    /// </summary>
    public struct FollowBlend
    {
        public Transform Target;
        public float Weight;
        public Matrix4x4 Offset;
    }

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
        /// <summary>
        /// Stuck to the skin (<see cref="SkinStick"/>): the follower is blended between these bones, as the build's VRChat
        /// Parent Constraint holds it, instead of following <see cref="Target"/> alone. Null for the others.
        /// </summary>
        public FollowBlend[] Blend;

        public void Apply()
        {
            if (Blend != null && Blend.Length > 0) { ApplyBlend(); return; }
            if (Scale.HasValue)
            {
                // Per axis, like VRCFury's world scale.
                var world = Vector3.Scale(Target.lossyScale, Scale.Value);
                var parent = Follower.parent != null ? Follower.parent.lossyScale : Vector3.one;
                var local = new Vector3(world.x / parent.x, world.y / parent.y, world.z / parent.z);
                // A bone scaled to zero (hidden body parts) has no scale to follow.
                if (float.IsFinite(local.x) && float.IsFinite(local.y) && float.IsFinite(local.z)) Follower.localScale = local;
            }
            // Rotation composed separately: a non-uniformly scaled avatar would skew the matrix's rotation.
            Follower.SetPositionAndRotation(Target.TransformPoint(Offset.GetColumn(3)), Target.rotation * Offset.rotation);
        }

        // Each bone's placement of the follower, averaged by its share: positions linearly, rotations as quaternions turned
        // to the first one's side.
        private void ApplyBlend()
        {
            var position = Vector3.zero;
            var sum = new Vector4();
            Quaternion first = default;
            float total = 0f;
            for (int i = 0; i < Blend.Length; i++)
            {
                var blend = Blend[i];
                if (blend.Target == null || blend.Weight <= 0f) continue;
                position += SkinStick.Frame(blend.Target).MultiplyPoint3x4(blend.Offset.GetColumn(3)) * blend.Weight;
                var q = blend.Target.rotation * blend.Offset.rotation;
                if (total == 0f) first = q;
                float side = Quaternion.Dot(first, q) < 0f ? -blend.Weight : blend.Weight;
                sum += new Vector4(q.x, q.y, q.z, q.w) * side;
                total += blend.Weight;
            }
            if (total <= 0f) return;
            sum /= sum.magnitude;
            Follower.SetPositionAndRotation(position / total, new Quaternion(sum.x, sum.y, sum.z, sum.w));
        }

        /// <summary>The link with its offsets moved by as much as the follower's rest moved (from <paramref name="before"/> to now).</summary>
        public FollowLink Rebased(Matrix4x4 before)
        {
            var change = before.inverse * Follower.localToWorldMatrix;
            var link = this;
            link.Offset = Offset * change;
            if (Blend != null)
            {
                link.Blend = (FollowBlend[])Blend.Clone();
                for (int i = 0; i < link.Blend.Length; i++) link.Blend[i].Offset = Blend[i].Offset * change;
            }
            return link;
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
                // A creator's recursive link only recognizes exact child names. A garment fitted to a
                // different base needs the same anatomical matches used by the fit, including its arms.
                // Use the same fitted offsets in the preview and on the build copy.
                if (AttachmentFit.Fitted(attachment) &&
                    (attachment.mode == OrbitersAttachment.AttachMode.Configured || attachment.mode == OrbitersAttachment.AttachMode.VrcFury))
                {
                    var fit = AttachmentFit.Compare(attachment.gameObject, avatarRoot);
                    if (fit != null)
                    {
                        foreach (var pair in fit.Pairs)
                        {
                            result.Add(new FollowLink { Follower = pair.source, Target = pair.target,
                                Offset = Current(pair.source, pair.target), Source = "Fitted clothing", Accessory = root });
                            handled.Add(pair.source);
                        }
                        // Hood strings, tentacle chains and other unmatched deforming bones inherit their
                        // fitted parents. Do not reinterpret them as a second unlinked humanoid armature.
                        handled.UnionWith(attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.transform));
                    }
                }
                if (attachment.mode == OrbitersAttachment.AttachMode.Merge)
                {
                    var rest = ArmatureRest.Frames(attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                    foreach (var link in attachment.links)
                        if (link.from != null && link.to != null)
                            result.Add(new FollowLink { Follower = link.from, Target = link.to, Offset = AttachmentFit.Fitted(attachment) ? Current(link.from, link.to) : RestOffset(link.from, link.to, rest, avatarRest), Source = "My Avatar", Accessory = root });
                    handled.UnionWith(root.GetComponentsInChildren<Transform>(true));
                }
                else if (attachment.mode == OrbitersAttachment.AttachMode.Parent && attachment.parent != null)
                {
                    result.Add(new FollowLink { Follower = root, Target = attachment.parent, Offset = Current(root, attachment.parent), Source = "My Avatar", Accessory = root,
                        Blend = SkinStick.Blend(attachment, root.localToWorldMatrix) });
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
                var followed = VrcFuryLink(link, from, target, scaleFactor, accessory);
                // A creator's prop link that leaves the prop where it stands can stick it to the skin (SkinStick.Holder).
                var stuck = accessory.GetComponent<OrbitersAttachment>();
                if (stuck != null && stuck.stickToSkin && !link.Recursive) followed.Blend = SkinStick.Blend(stuck, from.localToWorldMatrix);
                result.Add(followed);
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
