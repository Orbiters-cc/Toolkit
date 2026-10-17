using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// An avatar's meshes were changed for good by another tool (MCB applying an export from Blender): what was made from
    /// them elsewhere (My Avatar's Substance 3D Painter project) can be made again. Tools raise it once the change is done,
    /// with the avatar's root.
    /// </summary>
    public static class AvatarMeshEdits
    {
        /// <summary>Raised on the main thread, with the avatar root, after the meshes changed.</summary>
        public static event Action<Transform> Applied;

        /// <summary>Tells every listener; one that fails is logged and the others still hear it.</summary>
        public static void NotifyApplied(Transform avatarRoot)
        {
            if (avatarRoot == null || Applied == null) return;
            foreach (Action<Transform> listener in Applied.GetInvocationList())
            {
                try { listener(avatarRoot); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
