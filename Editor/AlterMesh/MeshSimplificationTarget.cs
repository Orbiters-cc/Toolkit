#nullable enable
// AlterMesh: Orbiters' mesh simplifier, derived from Meshia Mesh Simplification 3.2.0
// (https://github.com/RamType0/Meshia.MeshSimplification), Copyright (c) 2025 Ram.Type-0, MIT License: see
// LICENSE-Meshia.md next to this file. Changes are listed in README.md.
using System;
using UnityEngine;

namespace Orbiters.AlterMesh
{
    /// <summary>
    /// Represents a target for mesh simplification.
    /// </summary>
    [Serializable]
    public struct MeshSimplificationTarget : IEquatable<MeshSimplificationTarget>
    {
        /// <summary>
        /// Represents what actually <see cref="Value"/> means.
        /// </summary>
        public MeshSimplificationTargetKind Kind;

        /// <summary>
        /// The target value for mesh simplification. <see cref="Kind"/> determines what this value represents.
        /// </summary>
        /// <seealso cref="MeshSimplificationTargetKind"/>
        [Min(0)]
        public float Value;

        public override readonly bool Equals(object obj)
        {
            return obj is MeshSimplificationTarget target && Equals(target);
        }

        public readonly bool Equals(MeshSimplificationTarget other)
        {
            return Kind == other.Kind &&
                   Value == other.Value;
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(Kind, Value);
        }

        public static bool operator ==(MeshSimplificationTarget left, MeshSimplificationTarget right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MeshSimplificationTarget left, MeshSimplificationTarget right)
        {
            return !(left == right);
        }
    }
}


