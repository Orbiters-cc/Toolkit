#nullable enable
// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;

namespace Orbiters.AlterMesh
{
    /// <summary>
    /// Represents what actually <see cref="MeshSimplificationTarget.Value"/> means.
    /// </summary>
    [Serializable]
    public enum MeshSimplificationTargetKind
    {
        /// <summary>
        /// The value is the target vertex count relative to the original mesh.
        /// </summary>
        RelativeVertexCount,
        /// <summary>
        /// The value is the target vertex count as an absolute number.
        /// </summary>
        AbsoluteVertexCount,
        /// <summary>
        /// The value is the max total error scaled by the original mesh's bounding box size and vertex count.
        /// </summary>
        ScaledTotalError,
        /// <summary>
        /// The value is the max total error as an absolute number.
        /// </summary>
        AbsoluteTotalError,
        /// <summary>
        /// The value is the target triangle count relative to the original mesh.
        /// </summary>
        RelativeTriangleCount,
        /// <summary>
        /// The value is the target triangle count as an absolute number.
        /// </summary>
        AbsoluteTriangleCount,
    }
}


