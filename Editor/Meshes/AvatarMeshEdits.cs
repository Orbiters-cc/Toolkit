using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// An avatar's meshes were changed by another tool: for good (<see cref="Applied"/>: a Mixer commit, MCB applying an
    /// export from Blender), or for a while (<see cref="Previewed"/>: a tool showing edits live on temporary meshes, as
    /// Mixer's working copies, or putting the avatar's own back). What was made from them elsewhere (My Avatar's Substance
    /// 3D Painter project) can follow. Tools raise them once the change is done, with the avatar's root; a live preview
    /// raises <see cref="NotifyPreviewed"/> at each change (listeners wait for the edits to pause).
    /// </summary>
    public static class AvatarMeshEdits
    {
        /// <summary>Raised on the main thread, with the avatar root, after the meshes changed.</summary>
        public static event Action<Transform> Applied;

        /// <summary>Raised on the main thread, with the avatar root, each time the meshes it shows changed for a while.</summary>
        public static event Action<Transform> Previewed;

        /// <summary>Tells every listener; one that fails is logged and the others still hear it.</summary>
        public static void NotifyApplied(Transform avatarRoot) => Notify(Applied, avatarRoot);

        /// <summary>The avatar shows other meshes for a while (a live preview), or its own again.</summary>
        public static void NotifyPreviewed(Transform avatarRoot) => Notify(Previewed, avatarRoot);

        /// <summary>
        /// Asked by work that reads an avatar's meshes and can wait (My Avatar's optimization estimate): true while a tool
        /// edits them live (Mixer's sculpt or paint modes), when every frame matters to the user and the meshes shown are
        /// temporary. Tools add a test answering for an avatar root.
        /// </summary>
        public static event Func<Transform, bool> LiveEditing;

        /// <summary>Whether a tool edits <paramref name="avatarRoot"/>'s meshes live (see <see cref="LiveEditing"/>).</summary>
        public static bool IsLiveEditing(Transform avatarRoot)
        {
            if (avatarRoot == null || LiveEditing == null) return false;
            foreach (Func<Transform, bool> test in LiveEditing.GetInvocationList())
            {
                try { if (test(avatarRoot)) return true; }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            return false;
        }

        private static void Notify(Action<Transform> listeners, Transform avatarRoot)
        {
            if (avatarRoot == null || listeners == null) return;
            foreach (Action<Transform> listener in listeners.GetInvocationList())
            {
                try { listener(avatarRoot); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
