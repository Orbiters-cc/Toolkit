// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using Unity.Mathematics;
namespace Orbiters.AlterMesh
{
    struct VertexMerge : IComparable<VertexMerge>
    {
        public int VertexAIndex, VertexBIndex;
        public int VertexAVersion, VertexBVersion;
        public float3 Position;
        public float Cost;

        public int CompareTo(VertexMerge other)
        {
            return Cost.CompareTo(other.Cost);
        }
    }
}


