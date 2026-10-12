using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Mesh changes for a build copy that keep what the avatar shows. Each gives the renderer a copy of its mesh (in
    /// memory, for the build to keep) and returns it, or returns null and changes nothing. The source mesh is never edited.
    /// </summary>
    public static class MeshOptimization
    {
        /// <summary>
        /// Drops the bones no vertex of the mesh is weighted to from the renderer's bone list: VRChat ranks every bone a
        /// skinned mesh lists, weighted or not (clothing often lists the whole armature for a few weighted bones).
        /// A mesh with a vertex weighted to nothing is left alone.
        /// </summary>
        public static Mesh PruneBones(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            if (mesh == null || bones.Length == 0 || renderer.GetComponent<Cloth>() != null) return null;
            var bindposes = mesh.bindposes;
            if (bindposes.Length != bones.Length) return null;
            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (counts.Length == 0 || counts.Length != mesh.vertexCount) return null;
            var used = new bool[bones.Length];
            for (int v = 0, k = 0; v < counts.Length; v++)
            {
                bool weighted = false;
                for (int n = 0; n < counts[v]; n++, k++)
                {
                    var weight = weights[k];
                    if (weight.weight <= 0f) continue;
                    if (weight.boneIndex < 0 || weight.boneIndex >= used.Length) return null;
                    used[weight.boneIndex] = true;
                    weighted = true;
                }
                if (!weighted) return null;
            }
            if (used.All(u => u)) return null;

            var map = new int[bones.Length];
            var keptBones = new List<Transform>();
            var keptPoses = new List<Matrix4x4>();
            for (int i = 0; i < bones.Length; i++)
            {
                map[i] = -1;
                if (!used[i]) continue;
                map[i] = keptBones.Count;
                keptBones.Add(bones[i]);
                keptPoses.Add(bindposes[i]);
            }
            // Weights of zero on dropped bones go with them; Unity keeps each vertex's weights heaviest first.
            var newCounts = new byte[counts.Length];
            var newWeights = new List<BoneWeight1>(weights.Length);
            for (int v = 0, k = 0; v < counts.Length; v++)
            {
                int start = newWeights.Count;
                for (int n = 0; n < counts[v]; n++, k++)
                {
                    var weight = weights[k];
                    if (map[weight.boneIndex] < 0) continue;
                    newWeights.Add(new BoneWeight1 { boneIndex = map[weight.boneIndex], weight = weight.weight });
                }
                newCounts[v] = (byte)(newWeights.Count - start);
            }
            var copy = Copy(mesh);
            using (var perVertex = new NativeArray<byte>(newCounts, Allocator.Temp))
            using (var all = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
                copy.SetBoneWeights(perVertex, all);
            copy.bindposes = keptPoses.ToArray();
            SetMesh(renderer, copy);
            renderer.bones = keptBones.ToArray();
            return copy;
        }

        /// <summary>
        /// Fewer material slots on one renderer: submeshes without triangles go, and submeshes that use the same opaque or
        /// cutout material become one (each slot is a draw call, and VRChat ranks slots). Transparent ones keep their order.
        /// A renderer whose materials an animation swaps, or with more materials than submeshes (a material drawn twice),
        /// is left alone.
        /// </summary>
        public static Mesh MergeSlots(Renderer renderer, AvatarUsage usage)
        {
            var mesh = MeshOf(renderer);
            var materials = renderer.sharedMaterials;
            if (mesh == null || materials.Length != mesh.subMeshCount || mesh.subMeshCount < 1) return null;
            if (usage != null && usage.SwapsMaterials(renderer)) return null;
            if (renderer.GetComponent<Cloth>() != null) return null;

            // Each kept slot and the submeshes it draws, in their order.
            var slots = new List<(Material Material, MeshTopology Topology, List<int> Submeshes)>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var descriptor = mesh.GetSubMesh(i);
                if (descriptor.indexCount == 0) continue;
                var material = materials[i];
                int into = material != null && Opaque(material)
                    ? slots.FindIndex(s => s.Material == material && s.Topology == descriptor.topology)
                    : -1;
                if (into >= 0) slots[into].Submeshes.Add(i);
                else slots.Add((material, descriptor.topology, new List<int> { i }));
            }
            // Never every slot: a renderer without one would draw nothing at all.
            if (slots.Count == mesh.subMeshCount || slots.Count == 0) return null;

            var copy = Copy(mesh);
            var indices = slots.Select(s => s.Submeshes.SelectMany(i => mesh.GetIndices(i, true)).ToArray()).ToList();
            copy.subMeshCount = slots.Count;
            for (int k = 0; k < slots.Count; k++)
                copy.SetIndices(indices[k], slots[k].Topology, k, false, 0);
            copy.bounds = mesh.bounds;
            SetMesh(renderer, copy);
            renderer.sharedMaterials = slots.Select(s => s.Material).ToArray();
            return copy;
        }

        // Opaque and cutout materials draw in any order; transparent ones would change how they layer.
        private static bool Opaque(Material material) => material.renderQueue <= (int)RenderQueue.GeometryLast;

        public static Mesh MeshOf(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
            renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;

        /// <summary>Gives the renderer another mesh with the same blendshapes, keeping their weights.</summary>
        public static void SetMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                var old = skinned.sharedMesh;
                int shapes = old != null && mesh != null && old.blendShapeCount == mesh.blendShapeCount ? mesh.blendShapeCount : 0;
                var weights = Enumerable.Range(0, shapes).Select(skinned.GetBlendShapeWeight).ToArray();
                skinned.sharedMesh = mesh;
                for (int i = 0; i < weights.Length; i++) skinned.SetBlendShapeWeight(i, weights[i]);
            }
            else if (renderer.TryGetComponent<MeshFilter>(out var filter)) filter.sharedMesh = mesh;
        }

        /// <summary>An in-memory copy under the same name, so the build data reads like the avatar's own.</summary>
        public static Mesh Copy(Mesh mesh)
        {
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            return copy;
        }
    }
}
