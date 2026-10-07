using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>The photoshoot's depth of field focuses where the avatar sees from: its VRChat Avatar Descriptor's view position.</summary>
    internal static class PhotoshootVrcView
    {
        [InitializeOnLoadMethod]
        private static void Register() => PhotoshootService.ViewPosition = ViewPosition;

        private static Vector3? ViewPosition(GameObject avatar)
        {
            var descriptor = avatar != null ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
            return descriptor != null ? descriptor.ViewPosition : (Vector3?)null;
        }
    }
}
