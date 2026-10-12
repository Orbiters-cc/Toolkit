using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Bones that never move against their parent: nothing animates, simulates or constrains them, they aren't humanoid,
    /// and nothing but skinned meshes, colliders and contacts points at them (clothing armatures reparented under the body's
    /// bones, attachment bones). Their vertices follow the nearest bone that can move instead, through a bind pose that
    /// keeps them exactly where they were, and they leave the meshes' bone lists: VRChat counts each distinct bone listed.
    /// The transforms themselves stay.
    /// </summary>
    public static class StaticBones
    {
        public sealed class Result
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();
            /// <summary>How many distinct bones no mesh lists any more.</summary>
            public int Bones;
        }

        public static Result Fold(AvatarUsage usage)
        {
            var result = new Result();
            var root = usage.Root.transform;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.bones.Length > 0 && r.GetComponent<Cloth>() == null).ToList();
            var listedBefore = new HashSet<Transform>(renderers.SelectMany(r => r.bones).Where(b => b != null));
            var moving = Moving(usage);
            var target = new Dictionary<Transform, Transform>();
            Transform Target(Transform bone)
            {
                if (bone == null || bone == root || moving.Contains(bone)) return bone;
                if (target.TryGetValue(bone, out var known)) return known;
                // Rigid all the way up to the first bone that can move.
                var at = bone.parent;
                while (at != null && at != root && !moving.Contains(at) && Static(at, usage)) at = at.parent;
                return target[bone] = at ?? root;
            }
            foreach (var renderer in renderers)
            {
                var bones = renderer.bones;
                var poses = renderer.sharedMesh.bindposes;
                if (poses.Length != bones.Length) continue;
                var folded = new Transform[bones.Length];
                var newPoses = new Matrix4x4[bones.Length];
                bool changed = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = bones[i];
                    var to = bone != null && Static(bone, usage) && !moving.Contains(bone) ? Target(bone) : bone;
                    folded[i] = to; newPoses[i] = poses[i];
                    if (to == bone) continue;
                    // Where the vertex follows "to" from: the bone's own place under it, as it stands.
                    newPoses[i] = to.worldToLocalMatrix * bone.localToWorldMatrix * poses[i];
                    changed = true;
                }
                if (!changed) continue;
                var mesh = Rebind(renderer, folded, newPoses);
                if (mesh != null) result.Meshes.Add(mesh);
            }
            var listedAfter = new HashSet<Transform>(renderers.SelectMany(r => r.bones).Where(b => b != null));
            result.Bones = listedBefore.Count(b => !listedAfter.Contains(b));
            return result;
        }

        // Whether a bone stays put against its parent, by itself.
        private static bool Static(Transform bone, AvatarUsage usage)
        {
            if (bone.parent == null || bone == usage.Root.transform || usage.Humanoid(bone) || usage.Pending(bone)) return false;
            if (usage.AnimatedAt(bone).Any(p => p.StartsWith("m_Local", StringComparison.Ordinal) || p.StartsWith("localEulerAngles", StringComparison.Ordinal))) return false;
            if (bone.GetComponents<Component>().Any(c => !(c is Transform || c is VRCPhysBoneColliderBase || c is ContactBase))) return false;
            bool Allowed(Component from, string property) => from == null || from is SkinnedMeshRenderer || from is VRCPhysBoneColliderBase || from is ContactBase ||
                                                             from is VRCPhysBoneBase && property.StartsWith("ignoreTransforms", StringComparison.Ordinal);
            return usage.ReferencesTo(bone).All(r => Allowed(r.From, r.Property)) && usage.ReferencesTo(bone.gameObject).All(r => Allowed(r.From, r.Property));
        }

        // Bones something else moves: PhysBone chains and what constraints drive.
        private static HashSet<Transform> Moving(AvatarUsage usage)
        {
            var moving = new HashSet<Transform>();
            foreach (var bone in usage.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                var chain = bone.rootTransform != null ? bone.rootTransform : bone.transform;
                var ignored = (bone.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).ToList();
                moving.UnionWith(chain.GetComponentsInChildren<Transform>(true).Where(t => !ignored.Any(t.IsChildOf)));
            }
            foreach (var constraint in usage.Root.GetComponentsInChildren<VRCConstraintBase>(true))
                moving.Add(constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform);
            foreach (var constraint in usage.Root.GetComponentsInChildren<IConstraint>(true))
                if (constraint is Component component) moving.Add(component.transform);
            // What moves carries what is below it along, but those still stay put against it: only the movers themselves.
            return moving;
        }

        // A copy of the mesh with the new bind poses, entries of one bone at one bind pose merged.
        private static Mesh Rebind(SkinnedMeshRenderer renderer, Transform[] bones, Matrix4x4[] poses)
        {
            var mesh = renderer.sharedMesh;
            var keptBones = new List<Transform>();
            var keptPoses = new List<Matrix4x4>();
            var map = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                int at = -1;
                for (int k = 0; k < keptBones.Count && at < 0; k++)
                    if (keptBones[k] == bones[i] && Close(keptPoses[k], poses[i])) at = k;
                if (at < 0) { at = keptBones.Count; keptBones.Add(bones[i]); keptPoses.Add(poses[i]); }
                map[i] = at;
            }
            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var newCounts = new byte[counts.Length];
            var newWeights = new List<BoneWeight1>(weights.Length);
            var merged = new List<BoneWeight1>(8);
            for (int v = 0, k = 0; v < counts.Length; v++)
            {
                merged.Clear();
                for (int n = 0; n < counts[v]; n++, k++)
                {
                    int bone = weights[k].boneIndex < map.Length ? map[weights[k].boneIndex] : weights[k].boneIndex;
                    int found = merged.FindIndex(m => m.boneIndex == bone);
                    if (found >= 0) merged[found] = new BoneWeight1 { boneIndex = bone, weight = merged[found].weight + weights[k].weight };
                    else merged.Add(new BoneWeight1 { boneIndex = bone, weight = weights[k].weight });
                }
                merged.Sort((a, b) => b.weight.CompareTo(a.weight));
                newCounts[v] = (byte)merged.Count;
                newWeights.AddRange(merged);
            }
            var copy = MeshOptimization.Copy(mesh);
            using (var perVertex = new NativeArray<byte>(newCounts, Allocator.Temp))
            using (var all = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
                copy.SetBoneWeights(perVertex, all);
            copy.bindposes = keptPoses.ToArray();
            MeshOptimization.SetMesh(renderer, copy);
            renderer.bones = keptBones.ToArray();
            return copy;
        }

        private static bool Close(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }
    }
}
