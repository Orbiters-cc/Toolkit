// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct ComputeVertexErrorQuadricsJob : IJobParallelForDefer
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<int3> Triangles;
        [ReadOnly]
        public NativeParallelMultiHashMap<int, int> VertexContainingTriangles;
        [ReadOnly]
        public NativeHashSet<int2> Edges;
        [ReadOnly]
        public NativeArray<ErrorQuadric> TriangleErrorQuadrics;
        [WriteOnly]
        public NativeArray<ErrorQuadric> VertexErrorQuadrics;
        public void Execute(int vertexIndex)
        {
            var vertexErrorQuadric = new ErrorQuadric();
            var vertexPosition = VertexPositionBuffer[vertexIndex];
            foreach (var triangleIndex in VertexContainingTriangles.GetValuesForKey(vertexIndex))
            {
                var triangle = Triangles[triangleIndex];

                int2x2 belongingEdges;
                if (vertexIndex == triangle.x)
                {
                    belongingEdges = new(triangle.xy, triangle.zx);
                }
                else if (vertexIndex == triangle.y)
                {
                    belongingEdges = new(triangle.xy, triangle.yz);
                }
                else if (vertexIndex == triangle.z)
                {
                    belongingEdges = new(triangle.yz, triangle.zx);
                }
                else
                {
                    throw new Exception();
                }


                if (!Edges.Contains(belongingEdges.c0.yx) || !Edges.Contains(belongingEdges.c1.yx))
                {
                    vertexErrorQuadric += new ErrorQuadric(new Plane(math.right(), vertexPosition));

                    vertexErrorQuadric += new ErrorQuadric(new Plane(math.up(), vertexPosition));

                    vertexErrorQuadric += new ErrorQuadric(new Plane(math.forward(), vertexPosition));
                }
                else
                {
                    vertexErrorQuadric += TriangleErrorQuadrics[triangleIndex];
                }
            }
            VertexErrorQuadrics[vertexIndex] = vertexErrorQuadric;
        }
    }
}


