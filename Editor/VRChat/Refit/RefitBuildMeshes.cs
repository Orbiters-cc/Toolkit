using System.Collections.Generic;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>Copies of a build copy's meshes that the refit build steps add blendshapes to (never the project's own).</summary>
    internal static class RefitBuildMeshes
    {
        // A mesh whose blendshapes take more than this (an estimate: vertices x shapes x 40 bytes) is not copied: Unity's own
        // allocation overflows past 2 GB and the editor dies.
        private const double MaxShapeBytes = 1.5e9;

        /// <summary>
        /// The mesh of <paramref name="renderer"/>, copied once per <paramref name="copies"/> and given to the renderer, to add
        /// <paramref name="adding"/> blendshapes to; null (and a warning naming <paramref name="purpose"/>) when it is too large
        /// to copy safely.
        /// </summary>
        public static Mesh Own(SkinnedMeshRenderer renderer, Dictionary<SkinnedMeshRenderer, Mesh> copies, int adding, string purpose, List<string> warnings)
        {
            if (copies.TryGetValue(renderer, out var copied)) return copied;
            var mesh = renderer.sharedMesh;
            if (mesh == null) return null;
            if ((double)mesh.vertexCount * (mesh.blendShapeCount + adding) * 40 > MaxShapeBytes)
            {
                warnings.Add($"{renderer.name}'s mesh has too many blendshapes to copy safely ({mesh.blendShapeCount} over {mesh.vertexCount} vertices): {purpose} left out.");
                return null;
            }
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copies[renderer] = copy;
            renderer.sharedMesh = copy;
            // The build copy's record follows: it still applies to the renderer for the later steps.
            if (renderer.TryGetComponent<OrbitersRefit>(out var record) && record.mesh == mesh) record.mesh = copy;
            return copy;
        }
    }
}
