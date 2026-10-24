using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Meshes a tool shows through a working copy while it edits them (Mixer's sessions): the copy is no asset, so tools
    /// that read a renderer's asset path, model file or fingerprint look through it to the mesh it stands for.
    /// </summary>
    public static class WorkingMeshes
    {
        /// <summary>Set by the editing tool: the original mesh behind a working copy, null for any other mesh.</summary>
        public static Func<Mesh, Mesh> OriginalOf;

        /// <summary>The mesh as an asset: the original behind a working copy, else the mesh itself.</summary>
        public static Mesh AssetOf(Mesh mesh)
        {
            var original = mesh != null ? OriginalOf?.Invoke(mesh) : null;
            return original != null ? original : mesh;
        }
    }
}
