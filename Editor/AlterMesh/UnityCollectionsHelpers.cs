// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
namespace Orbiters.AlterMesh
{
    internal static class UnityCollectionsHelpers
    {
        public static unsafe ref T ElementAt<T>(this NativeArray<T> array, int i)
            where T : unmanaged
        {
            if (Hint.Likely((uint)i < (uint)array.Length))
            {

                return ref ((T*)array.GetUnsafePtr())[i];
            }
            else
            {
                throw new IndexOutOfRangeException();
            }
        }

        public static unsafe Span<T> AsSpan<T>(this UnsafeList<T> list)
            where T : unmanaged
        {
            return new Span<T>(list.Ptr, list.Length);
        }

    }
}


