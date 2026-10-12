// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct CopyVertexPositionBufferJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        public NativeList<float3> VertexPositionBuffer;
        public void Execute()
        {
            VertexPositionBuffer.Resize(Mesh.vertexCount, NativeArrayOptions.UninitializedMemory);
            Mesh.GetVertices(VertexPositionBuffer.AsArray().Reinterpret<Vector3>());
        }
    }
    [BurstCompile]
    struct CopyVertexNormalBufferJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        public NativeList<float3> VertexNormalBuffer;
        public void Execute()
        {
            if (Mesh.HasVertexAttribute(VertexAttribute.Normal))
            {
                VertexNormalBuffer.Resize(Mesh.vertexCount, NativeArrayOptions.UninitializedMemory);
                Mesh.GetNormals(VertexNormalBuffer.AsArray().Reinterpret<Vector3>());
            }
            else
            {
                VertexNormalBuffer.Clear();
            }
        }

    }

    [BurstCompile]
    struct CopyVertexAttributeBufferAsFloat4Job : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        public VertexAttribute VertexAttribute;
        public NativeList<float4> VertexAttributeBuffer;

        public void Execute()
        {
            if (Mesh.HasVertexAttribute(VertexAttribute))
            {

                VertexAttributeBuffer.Resize(Mesh.vertexCount, NativeArrayOptions.UninitializedMemory);
                Mesh.GetVertexAttributeDataAsFloat4(VertexAttribute, VertexAttributeBuffer.AsArray().AsSpan());
            }
            else
            {
                VertexAttributeBuffer.Clear();
            }
        }
    }
    [BurstCompile]
    struct CopyVertexBlendWeightBufferJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        public NativeList<float> VertexBlendWeightBuffer;
        public void Execute()
        {


            if (Mesh.HasVertexAttribute(VertexAttribute.BlendWeight))
            {
                VertexBlendWeightBuffer.Resize(Mesh.vertexCount * Mesh.GetVertexAttributeDimension(VertexAttribute.BlendWeight), NativeArrayOptions.UninitializedMemory);
                Mesh.GetVertexAttributeDataAsFloats(VertexAttribute.BlendWeight, VertexBlendWeightBuffer.AsArray());
            }
            else
            {
                VertexBlendWeightBuffer.Clear();
            }
        }
    }
    [BurstCompile]
    struct CopyVertexBlendIndicesBufferJob : IJob
    {
        [ReadOnly] public Mesh.MeshData Mesh;
        public NativeList<uint> VertexBlendIndicesBuffer;
        public void Execute()
        {


            if (Mesh.HasVertexAttribute(VertexAttribute.BlendIndices))
            {
                VertexBlendIndicesBuffer.Resize(Mesh.vertexCount * Mesh.GetVertexAttributeDimension(VertexAttribute.BlendIndices), NativeArrayOptions.UninitializedMemory);
                Mesh.GetVertexAttributeDataAsInts(VertexAttribute.BlendIndices, VertexBlendIndicesBuffer.AsArray().Reinterpret<int>());
            }
            else
            {
                VertexBlendIndicesBuffer.Clear();
            }
        }
    }
}

