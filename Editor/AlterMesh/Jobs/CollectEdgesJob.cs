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
    struct CollectEdgesJob : IJob
    {
        [ReadOnly]
        public NativeArray<int3> Triangles;

        public NativeHashSet<int2> Edges;

        public void Execute()
        {
            Edges.Clear();
            var maxEdgeCount = Triangles.Length * 3;
            if (Edges.Capacity < maxEdgeCount)
            {
                Edges.Capacity = maxEdgeCount;
            }
            foreach (var triangle in Triangles)
            {
                Edges.Add(triangle.xy);
                Edges.Add(triangle.yz);
                Edges.Add(triangle.zx);
            }
        }
    }
}


