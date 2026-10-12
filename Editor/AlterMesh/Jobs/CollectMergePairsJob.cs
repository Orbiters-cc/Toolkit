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
    struct CollectMergePairsJob : IJob
    {
        [ReadOnly]
        public NativeHashSet<int2> Edges;
        [ReadOnly]
        public NativeHashSet<int2> SmartLinks;
        public NativeList<int2> MergePairs;

        public void Execute()
        {

            var maxPairCount = Edges.Count + SmartLinks.Count;


            MergePairs.Clear();
            if (MergePairs.Capacity < maxPairCount)
            {
                MergePairs.Capacity = maxPairCount;
            }

            foreach (var link in SmartLinks)
            {
                if (!(Edges.Contains(link) || Edges.Contains(link.yx)))
                {
                    MergePairs.AddNoResize(link);
                }
            }

            foreach (var pair in Edges)
            {
                var x = pair.x;
                var y = pair.y;
                if (pair.x <= pair.y)
                {
                    MergePairs.AddNoResize(pair);
                }
                else
                {
                    if (!Edges.Contains(pair.yx))
                    {
                        MergePairs.AddNoResize(pair.yx);
                    }


                }
            }
        }
    }
}


