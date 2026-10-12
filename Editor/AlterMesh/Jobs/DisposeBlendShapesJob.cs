// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Orbiters.AlterMesh
{
    [BurstCompile]
    struct DisposeBlendShapesJob : IJob
    {
        public NativeList<BlendShapeData> BlendShapes;

        public void Execute()
        {
            for (int simplifiedBlendShapesIndex = 0; simplifiedBlendShapesIndex < BlendShapes.Length; simplifiedBlendShapesIndex++)
            {
                var blendShape = BlendShapes[simplifiedBlendShapesIndex];
                blendShape.Dispose();
            }
        }
    }
}


