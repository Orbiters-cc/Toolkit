using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The photoshoot's "look at the camera" for avatars without humanoid eye bones: the eyes set in the VRChat Avatar
    /// Descriptor's eye look, with their "looking straight" rotations.
    /// </summary>
    internal static class PhotoshootVrcEyes
    {
        [InitializeOnLoadMethod]
        private static void Register() => PhotoshootLook.EyeFallback = Eyes;

        private static (Transform eye, Quaternion? straight)[] Eyes(GameObject avatar)
        {
            var descriptor = avatar != null ? avatar.GetComponent<VRCAvatarDescriptor>() : null;
            if (descriptor == null || !descriptor.enableEyeLook) return null;
            var settings = descriptor.customEyeLookSettings;
            var eyes = new List<(Transform, Quaternion?)>();
            if (settings.leftEye != null) eyes.Add((settings.leftEye, Straight(settings.eyesLookingStraight?.left)));
            if (settings.rightEye != null) eyes.Add((settings.rightEye, Straight(settings.eyesLookingStraight?.right)));
            return eyes.ToArray();
        }

        // An unset rotation (all zeros) means the eye looks straight as modelled.
        private static Quaternion? Straight(Quaternion? rotation) =>
            rotation.HasValue && (rotation.Value.x != 0f || rotation.Value.y != 0f || rotation.Value.z != 0f || rotation.Value.w != 0f) ? rotation : null;
    }
}
