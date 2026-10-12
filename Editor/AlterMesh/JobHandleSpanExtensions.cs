// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
namespace Orbiters.AlterMesh
{
    internal static class JobHandleSpanExtensions
    {
        public static unsafe JobHandle CombineDependencies(this ReadOnlySpan<JobHandle> jobHandles)
        {
            fixed (JobHandle* jobHandlesPtr = jobHandles)
            {
                return JobHandleUnsafeUtility.CombineDependencies(jobHandlesPtr, jobHandles.Length);
            }
        }
        public static unsafe JobHandle CombineDependencies(this Span<JobHandle> jobHandles) => ((ReadOnlySpan<JobHandle>)jobHandles).CombineDependencies();
    }
}


