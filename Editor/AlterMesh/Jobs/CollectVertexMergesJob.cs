// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    unsafe struct CollectVertexMergesJob : IJob
    {
        [ReadOnly]
        public NativeArray<VertexMerge> UnorderedDirtyVertexMerges;
        public NativeMinPriorityQueue<VertexMerge> VertexMerges;
        public void Execute()
        {
            ref var vertexMerges = ref VertexMerges.GetUnsafePriorityQueue()->nodes;
            vertexMerges.Clear();
            vertexMerges.SetCapacity(UnorderedDirtyVertexMerges.Length);

            for (int i = 0; i < UnorderedDirtyVertexMerges.Length; i++)
            {
                var merge = UnorderedDirtyVertexMerges[i];
                if (!float.IsPositiveInfinity(merge.Cost))
                {
                    vertexMerges.AddNoResize(merge);
                }
            }
            VertexMerges.GetUnsafePriorityQueue()->Heapify();
        }
    }
}


