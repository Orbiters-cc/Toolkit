// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct CollectVertexContainingSubMeshIndicesJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;

        public NativeList<ulong> VertexContainingSubMeshIndices;

        public void Execute()
        {
            VertexContainingSubMeshIndices.Resize(Mesh.vertexCount, NativeArrayOptions.ClearMemory);
            for (int subMeshIndex = 0; subMeshIndex < Mesh.subMeshCount; subMeshIndex++)
            {
                ProcessSubMesh(subMeshIndex);
            }
        }

        private void ProcessSubMesh(int subMeshIndex)
        {
            var subMesh = Mesh.GetSubMesh(subMeshIndex);
            // A bit per submesh, up to 64 (MeshSimplifier refuses more): a 32-bit mask wrapped submesh 32 onto 0.
            ulong bit = 1UL << subMeshIndex;

            var subMeshVertexContainingSubMeshIndices = VertexContainingSubMeshIndices.AsArray().GetSubArray(subMesh.firstVertex, subMesh.vertexCount).AsSpan();

            for (int subMeshVertexIndex = 0; subMeshVertexIndex < subMeshVertexContainingSubMeshIndices.Length; subMeshVertexIndex++)
            {
#if UNITY_BURST_EXPERIMENTAL_LOOP_INTRINSICS
                Loop.ExpectVectorized();
#endif
                subMeshVertexContainingSubMeshIndices[subMeshVertexIndex] |= bit;
            }

        }
    }
}