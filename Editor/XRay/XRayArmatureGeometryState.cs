using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    // Bone rotations are handled by skinning (each segment is skinned to its head bone). Topology, helper offsets and a
    // child bone moved from its parent (a segment that stretches) invalidate the mesh.
    internal sealed class XRayArmatureGeometryState
    {
        private readonly Transform rootBone;
        private readonly Transform[] bones;
        private readonly List<ChildState> children = new List<ChildState>();
        private readonly Transform[] parents;
        private readonly int[] childCounts;

        private readonly struct ChildState
        {
            public readonly Transform Child;
            public readonly bool IsHelper;
            public readonly Vector3 LocalPosition;
            public ChildState(Transform child, bool isHelper)
            {
                Child = child;
                IsHelper = isHelper;
                LocalPosition = child.localPosition;
            }
        }

        internal XRayArmatureGeometryState(SkinnedMeshRenderer renderer)
        {
            rootBone = renderer.rootBone;
            bones = renderer.bones;
            parents = new Transform[bones.Length];
            childCounts = new int[bones.Length];
            var boneSet = new HashSet<Transform>(bones);
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;
                parents[i] = bone.parent;
                childCounts[i] = bone.childCount;
                foreach (Transform child in bone)
                    children.Add(new ChildState(child, !boneSet.Contains(child)));
            }
        }

        internal bool Matches(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || rootBone != renderer.rootBone) return false;
            var current = renderer.bones;
            if (current.Length != bones.Length) return false;
            int childIndex = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = current[i];
                if (bone != bones[i]) return false;
                if (bone == null) continue;
                if (bone.parent != parents[i] || bone.childCount != childCounts[i]) return false;
                foreach (Transform child in bone)
                {
                    if (childIndex >= children.Count) return false;
                    var previous = children[childIndex++];
                    if (child != previous.Child ||
                        (child.localPosition - previous.LocalPosition).sqrMagnitude > 0.0000000001f) return false;
                }
            }
            return childIndex == children.Count;
        }
    }
}
