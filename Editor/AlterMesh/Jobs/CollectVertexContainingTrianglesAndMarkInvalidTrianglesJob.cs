// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct CollectVertexContainingTrianglesAndMarkInvalidTrianglesJob : IJob
    {
        [ReadOnly] public NativeArray<int3> Triangles;
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        public NativeBitArray TriangleIsDiscardedBits;
        public void Execute()
        {
            VertexContainingTriangles.Clear();
            VertexContainingTriangles.Capacity = Triangles.Length * 3;
            TriangleIsDiscardedBits.Resize(Triangles.Length);

            for (int triangle = 0; triangle < Triangles.Length; triangle++)
            {
                var vertices = Triangles[triangle];

                if (vertices.x == vertices.y | vertices.y == vertices.z | vertices.z == vertices.x)
                {
                    TriangleIsDiscardedBits.Set(triangle, true);
                }
                else
                {
                    TriangleIsDiscardedBits.Set(triangle, false);

                    VertexContainingTriangles.Add(vertices.x, triangle);
                    VertexContainingTriangles.Add(vertices.y, triangle);
                    VertexContainingTriangles.Add(vertices.z, triangle);
                }
            }
        }
    }
}


