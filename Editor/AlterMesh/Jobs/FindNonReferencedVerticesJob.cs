// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct FindNonReferencedVerticesJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        public NativeBitArray VertexIsDiscardedBits;
        public void Execute()
        {
            VertexIsDiscardedBits.Resize(Mesh.vertexCount);


            for (int vertex = 0; vertex < Mesh.vertexCount; vertex++)
            {
                VertexIsDiscardedBits.Set(vertex, !VertexContainingTriangles.ContainsKey(vertex));
            }

            for (int subMeshIndex = 0; subMeshIndex < Mesh.subMeshCount; subMeshIndex++)
            {
                var subMeshDescriptor = Mesh.GetSubMesh(subMeshIndex);
                if (subMeshDescriptor.topology is not MeshTopology.Triangles)
                {
                    VertexIsDiscardedBits.SetBits(subMeshDescriptor.firstVertex, false, subMeshDescriptor.vertexCount);
                }
            }
        }
    }
}


