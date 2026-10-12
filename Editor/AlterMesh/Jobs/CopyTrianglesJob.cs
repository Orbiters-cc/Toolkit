// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct CopyTrianglesJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;

        public NativeList<int3> Triangles;

        public void Execute()
        {

            var triangleCount = Mesh.GetTriangleCount();
            Triangles.Clear();
            Triangles.Capacity = triangleCount;

            switch (Mesh.indexFormat)
            {
                case IndexFormat.UInt16:
                    {
                        var indexBuffer = Mesh.GetIndexData<ushort>();
                        for (int subMeshIndex = 0; subMeshIndex < Mesh.subMeshCount; subMeshIndex++)
                        {
                            var subMesh = Mesh.GetSubMesh(subMeshIndex);
                            if (subMesh.topology == MeshTopology.Triangles)
                            {
                                var subMeshIndexBuffer = indexBuffer.GetSubArray(subMesh.indexStart, subMesh.indexCount);
                                var subMeshTriangleCount = subMesh.indexCount / 3;
                                for (int subMeshTriangleIndex = 0; subMeshTriangleIndex < subMeshTriangleCount; subMeshTriangleIndex++)
                                {
                                    int3 triangle = new()
                                    {
                                        x = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 0],
                                        y = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 1],
                                        z = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 2],
                                    };
                                    Triangles.AddNoResize(triangle);
                                }
                            }
                        }
                    }
                    break;
                case IndexFormat.UInt32:
                    {
                        var indexBuffer = Mesh.GetIndexData<int>();
                        for (int subMeshIndex = 0; subMeshIndex < Mesh.subMeshCount; subMeshIndex++)
                        {
                            var subMesh = Mesh.GetSubMesh(subMeshIndex);
                            if (subMesh.topology == MeshTopology.Triangles)
                            {
                                var subMeshIndexBuffer = indexBuffer.GetSubArray(subMesh.indexStart, subMesh.indexCount);
                                var subMeshTriangleCount = subMesh.indexCount / 3;
                                for (int subMeshTriangleIndex = 0; subMeshTriangleIndex < subMeshTriangleCount; subMeshTriangleIndex++)
                                {
                                    int3 triangle = new()
                                    {
                                        x = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 0],
                                        y = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 1],
                                        z = subMesh.baseVertex + subMeshIndexBuffer[subMeshTriangleIndex * 3 + 2],
                                    };
                                    Triangles.AddNoResize(triangle);
                                }
                            }
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }
        }
    }
}


