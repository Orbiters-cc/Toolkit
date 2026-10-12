using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// One culling box for the avatar's skinned meshes: measured around all of them as they stand, from the hips, with room
    /// for raised arms, kicks and sitting. A mesh whose own box was measured small, or from a bone far from where it shows
    /// (clothing bound to one bone, a tail's tip), no longer vanishes at the edge of the view, and every part of the avatar
    /// is culled together. Meshes that can leave the avatar (a bone below a constraint: a prop dropped in the world) keep
    /// their own box.
    /// </summary>
    public static class AvatarBounds
    {
        /// <summary>Room added on every side, as a share of the avatar's largest size.</summary>
        public const float Room = .3f;

        /// <summary>Gives the skinned meshes that move with the hips the avatar's box; returns how many changed.</summary>
        public static int Unify(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) return 0;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.sharedMesh.vertexCount > 0 && r.bones.Length > 0 && r.bones.All(b => b != null && Follows(b, hips))).ToList();
            if (renderers.Count == 0) return 0;
            var toHips = hips.worldToLocalMatrix;
            Bounds? box = null;
            var baked = new Mesh();
            try
            {
                foreach (var renderer in renderers)
                {
                    renderer.BakeMesh(baked);
                    var matrix = toHips * SkinnedMeshBounds.BakedToWorld(renderer);
                    foreach (var vertex in baked.vertices)
                    {
                        var point = matrix.MultiplyPoint3x4(vertex);
                        if (box == null) box = new Bounds(point, Vector3.zero);
                        else { var b = box.Value; b.Encapsulate(point); box = b; }
                    }
                }
            }
            finally { Object.DestroyImmediate(baked); }
            var bounds = box.Value;
            float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            bounds.Expand(2f * Room * size);
            int changed = 0;
            foreach (var renderer in renderers)
            {
                if (renderer.rootBone == hips && renderer.localBounds == bounds) continue;
                renderer.rootBone = hips;
                renderer.localBounds = bounds;
                changed++;
            }
            return changed;
        }

        // Whether a bone moves with the hips: it is below them and nothing constrains it or what it hangs from.
        private static bool Follows(Transform bone, Transform hips)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (t.GetComponent<IConstraint>() != null || t.GetComponent<VRCConstraintBase>() != null) return false;
                if (t == hips) return true;
            }
            return false;
        }
    }
}
