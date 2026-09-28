using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Posing
{
    /// <summary>The avatar's own armature, as opposed to clothing armatures that are merged into it at build.</summary>
    public static class AvatarSkeleton
    {
        /// <summary>
        /// The highest ancestor of the avatar's hips (or of the body mesh's root bone) below the avatar root, usually
        /// "Armature". Null when neither is found under the avatar.
        /// </summary>
        public static Transform Root(Transform avatarRoot, SkinnedMeshRenderer body = null)
        {
            if (avatarRoot == null) return null;
            var animator = AvatarBoneIndex.FindHumanoid(avatarRoot);
            var start = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (start == null || !start.IsChildOf(avatarRoot)) start = body != null && body.rootBone != null && body.rootBone.IsChildOf(avatarRoot) ? body.rootBone : null;
            if (start == null || start == avatarRoot) return null;
            while (start.parent != null && start.parent != avatarRoot) start = start.parent;
            return start;
        }

        /// <summary>Every transform of the avatar's armature; the body's bones when no armature is found.</summary>
        public static HashSet<Transform> Bones(Transform avatarRoot, SkinnedMeshRenderer body = null)
        {
            var root = Root(avatarRoot, body);
            if (root != null) return new HashSet<Transform>(root.GetComponentsInChildren<Transform>(true));
            return body != null ? new HashSet<Transform>(body.bones.Where(b => b != null)) : new HashSet<Transform>();
        }

        /// <summary>Transforms of the humanoid rig (hips, spine, limbs, toes...), which must not be driven by physics.</summary>
        public static HashSet<Transform> HumanoidBones(Transform avatarRoot)
        {
            var result = new HashSet<Transform>();
            var animator = AvatarBoneIndex.FindHumanoid(avatarRoot);
            if (animator == null) return result;
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                var bone = animator.GetBoneTransform(id);
                if (bone != null) result.Add(bone);
            }
            return result;
        }
    }
}
