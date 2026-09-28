using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Armature
{
    /// <summary>An avatar's bones by normalized name and humanoid role, for matching another armature onto it.</summary>
    public sealed class AvatarBoneIndex
    {
        private readonly HashSet<Transform> bones = new HashSet<Transform>();
        private readonly Dictionary<string, List<Transform>> byName = new Dictionary<string, List<Transform>>();
        private readonly Dictionary<HumanBodyBones, Transform> humanoid = new Dictionary<HumanBodyBones, Transform>();
        private readonly Dictionary<Transform, HumanBodyBones> humanOf = new Dictionary<Transform, HumanBodyBones>();
        internal readonly Dictionary<Transform, BodySide> Sides = new Dictionary<Transform, BodySide>();
        private static readonly IReadOnlyList<Transform> None = new Transform[0];

        public Transform Root { get; private set; }
        public IReadOnlyCollection<Transform> Bones => bones;
        internal IEnumerable<KeyValuePair<string, List<Transform>>> Names => byName;

        /// <summary>
        /// Indexes <paramref name="bones"/>. Humanoid roles come from <paramref name="humanoid"/>, else from the first valid
        /// humanoid Animator under <paramref name="avatarRoot"/>, else from the bone names.
        /// </summary>
        public static AvatarBoneIndex Build(Transform avatarRoot, IEnumerable<Transform> bones, Animator humanoid = null)
        {
            var index = new AvatarBoneIndex { Root = avatarRoot };
            // Shallowest first, so the first bone of a name or role is the one nearest the root.
            foreach (var bone in (bones ?? Enumerable.Empty<Transform>()).Where(b => b != null).Distinct().OrderBy(Depth))
            {
                index.bones.Add(bone);
                var key = BoneNames.Normalize(bone.name);
                if (key.Length > 0)
                {
                    if (!index.byName.TryGetValue(key, out var list)) index.byName[key] = list = new List<Transform>();
                    list.Add(bone);
                }
                index.Sides[bone] = BoneNames.Side(bone.name);
            }

            if (humanoid == null) humanoid = FindHumanoid(avatarRoot);
            if (humanoid != null && humanoid.isHuman)
                for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
                {
                    var bone = humanoid.GetBoneTransform(id);
                    if (bone != null) index.Add(id, bone);
                }
            else
                foreach (var bone in index.bones.OrderBy(Depth))
                    if (BoneNames.TryInferHumanoid(bone.name, out var id) && !index.humanoid.ContainsKey(id)) index.Add(id, bone);
            return index;
        }

        /// <summary>The first humanoid Animator with a valid avatar under <paramref name="avatarRoot"/>, itself included.</summary>
        public static Animator FindHumanoid(Transform avatarRoot) =>
            avatarRoot != null ? avatarRoot.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.isHuman && a.avatar != null && a.avatar.isValid) : null;

        public Transform Humanoid(HumanBodyBones bone) => humanoid.TryGetValue(bone, out var transform) ? transform : null;

        public bool TryGetHumanoid(Transform bone, out HumanBodyBones id)
        {
            id = HumanBodyBones.LastBone;
            return bone != null && humanOf.TryGetValue(bone, out id);
        }

        public IReadOnlyList<Transform> WithName(string normalizedName) =>
            normalizedName != null && byName.TryGetValue(normalizedName, out var list) ? list : None;

        internal static int Depth(Transform transform)
        {
            int depth = 0;
            for (; transform != null; transform = transform.parent) depth++;
            return depth;
        }

        private void Add(HumanBodyBones id, Transform bone)
        {
            humanoid[id] = bone;
            if (!humanOf.ContainsKey(bone)) humanOf[bone] = id;
        }
    }
}
