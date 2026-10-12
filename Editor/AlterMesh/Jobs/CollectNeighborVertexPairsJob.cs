// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct CollectNeighborVertexPairsJob : IJobParallelForDefer
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeBitArray VertexIsDiscardedBits;
        public MeshSimplifierOptions Options;
        public AllocatorManager.AllocatorHandle SubMeshSmartLinkListAllocator;
        public NativeArray<UnsafeList<int2>> SubMeshSmartLinkLists;

        public void Execute(int subMeshIndex)
        {
            var subMeshDescriptor = Mesh.GetSubMesh(subMeshIndex);
            var subMeshVertexPositions = VertexPositionBuffer.GetSubArray(subMeshDescriptor.firstVertex, subMeshDescriptor.vertexCount);
            ref var subMeshSmartLinkList = ref SubMeshSmartLinkLists.ElementAt(subMeshIndex);
            subMeshSmartLinkList = new(256, SubMeshSmartLinkListAllocator);
            UnsafeKdTree kdTree = new(Allocator.Temp);
            UnsafeList<int> linkOpponentVertices = new(16, Allocator.Temp);
            kdTree.Initialize(subMeshVertexPositions);

            for (int subMeshVertexIndex = 0; subMeshVertexIndex < subMeshVertexPositions.Length; subMeshVertexIndex++)
            {
                if (VertexIsDiscardedBits.IsSet(subMeshDescriptor.firstVertex + subMeshVertexIndex))
                {
                    continue;
                }
                var vertexPosition = subMeshVertexPositions[subMeshVertexIndex];

                kdTree.QueryPointsInSphere(subMeshVertexPositions, vertexPosition, Options.VertexLinkDistance, ref linkOpponentVertices);

                foreach (var linkOpponentVertexIndex in linkOpponentVertices)
                {
                    if (subMeshVertexIndex < linkOpponentVertexIndex && !VertexIsDiscardedBits.IsSet(subMeshDescriptor.firstVertex + linkOpponentVertexIndex))
                    {
                        var pair = new int2(subMeshVertexIndex, linkOpponentVertexIndex) + subMeshDescriptor.firstVertex;
                        subMeshSmartLinkList.Add(pair);
                    }
                }

                linkOpponentVertices.Clear();
            }

            linkOpponentVertices.Dispose();
            kdTree.Dispose();
        }
    }
}


