using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Armature
{
    /// <summary>Where skinned bones stand at rest, from the meshes' bind poses rather than the current pose.</summary>
    public static class ArmatureRest
    {
        /// <summary>World rest frame of each skinned bone: renderer.localToWorld * bindpose.inverse. The first renderer
        /// binding a bone wins; degenerate bind poses are skipped.</summary>
        public static Dictionary<Transform, Matrix4x4> Frames(IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var frames = new Dictionary<Transform, Matrix4x4>();
            foreach (var renderer in renderers)
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null) continue;
                var bindposes = mesh.bindposes;
                var bones = renderer.bones;
                var meshFrame = renderer.transform.localToWorldMatrix;
                for (int i = 0; i < Mathf.Min(bindposes.Length, bones.Length); i++)
                {
                    if (bones[i] == null || frames.ContainsKey(bones[i]) || Mathf.Abs(bindposes[i].determinant) < 1e-8f) continue;
                    frames[bones[i]] = meshFrame * bindposes[i].inverse;
                }
            }
            return frames;
        }

        /// <summary>The smallest ancestor holding the renderer, its root bone and all its bones, below
        /// <paramref name="stopAt"/>; null when only <paramref name="stopAt"/> or above holds them.</summary>
        public static Transform CommonRoot(SkinnedMeshRenderer renderer, Transform stopAt)
        {
            if (renderer == null) return null;
            var bones = renderer.bones.Append(renderer.rootBone).Where(b => b != null).ToList();
            for (var candidate = renderer.transform; candidate != null && candidate != stopAt; candidate = candidate.parent)
                if (bones.All(b => b.IsChildOf(candidate))) return candidate;
            return null;
        }
    }
}
