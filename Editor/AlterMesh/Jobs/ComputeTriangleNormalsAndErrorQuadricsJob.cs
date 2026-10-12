// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;
namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct ComputeTriangleNormalsAndErrorQuadricsJob : IJobParallelForDefer
    {
        [ReadOnly]
        public NativeArray<float3> VertexPositionBuffer;
        [ReadOnly]
        public NativeArray<int3> Triangles;
        [WriteOnly]
        public NativeArray<float3> TriangleNormals;
        [WriteOnly]
        public NativeArray<ErrorQuadric> TriangleErrorQuadrics;
        public void Execute(int triangleIndex)
        {
            var triangle = Triangles[triangleIndex];
            var x = VertexPositionBuffer[triangle.x];
            var y = VertexPositionBuffer[triangle.y];
            var z = VertexPositionBuffer[triangle.z];

            var xy = y - x;
            var xz = z - x;
            var plane = new Plane(math.cross(xy, xz), x);

            TriangleNormals[triangleIndex] = plane.Normal;

            TriangleErrorQuadrics[triangleIndex] = new ErrorQuadric(plane);
        }
    }
}


