// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct MarkBorderEdgeVerticesJob : IJob
    {
        [ReadOnly]
        public Mesh.MeshData Mesh;
        [ReadOnly]
        public NativeHashSet<int2> Edges;
        public NativeBitArray VertexIsBorderEdgeBits;
        public void Execute()
        {
            VertexIsBorderEdgeBits.Resize(Mesh.vertexCount);
            VertexIsBorderEdgeBits.Clear();
            foreach (var pair in Edges)
            {
                var x = pair.x;
                var y = pair.y;
                if (!Edges.Contains(new(y, x)))
                {
                    VertexIsBorderEdgeBits.Set(x, true);
                    VertexIsBorderEdgeBits.Set(y, true);
                }
            }
        }
    }
}


