// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    unsafe struct CollectVertexMergeOpponmentsJob : IJob
    {
        [ReadOnly]
        public NativeArray<VertexMerge> UnorderedDirtyVertexMerges;
        public NativeParallelMultiHashMap<int, int> VertexMergeOpponentVertices;
        public void Execute()
        {
            VertexMergeOpponentVertices.Clear();
            VertexMergeOpponentVertices.Capacity = UnorderedDirtyVertexMerges.Length * 2;
            for (int index = 0; index < UnorderedDirtyVertexMerges.Length; index++)
            {
                var merge = UnorderedDirtyVertexMerges[index];
                if (float.IsPositiveInfinity(merge.Cost))
                {
                    continue;
                }
                var vertexA = merge.VertexAIndex;
                var vertexB = merge.VertexBIndex;
                VertexMergeOpponentVertices.Add(vertexA, vertexB);
                VertexMergeOpponentVertices.Add(vertexB, vertexA);
            }
        }
    }
}


