using System.Reflection;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar the VRChat SDK's builder panel shows, read and set the same way on every SDK the Orbiters packages
    /// support. IVRCSdkAvatarBuilderApi has SelectedAvatar and SelectAvatar since VRChat SDK 3.8.1; 3.7.x keeps the
    /// selection in VRCSdkControlPanelAvatarBuilder (a protected static descriptor, set through its static SelectAvatar).
    /// </summary>
    public static class VrcSdkAvatarBuilder
    {
#if !ORBITERS_VRCSDK_3_8_1_OR_NEWER
        private static readonly FieldInfo SelectedField =
            typeof(VRCSdkControlPanelAvatarBuilder).GetField("_selectedAvatar", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
#endif

        /// <summary>The avatar the builder shows; null when none, or when it was destroyed (the SDK's getter throws then).</summary>
        public static GameObject Selected(IVRCSdkAvatarBuilderApi builder)
        {
            if (builder == null) return null;
            try
            {
#if ORBITERS_VRCSDK_3_8_1_OR_NEWER
                return builder.SelectedAvatar;
#else
                var descriptor = SelectedField?.GetValue(null) as Component;
                return descriptor ? descriptor.gameObject : null;
#endif
            }
            catch (MissingReferenceException)
            {
                return null;
            }
        }

        /// <summary>Shows <paramref name="avatar"/> in the builder.</summary>
        public static void Select(IVRCSdkAvatarBuilderApi builder, GameObject avatar)
        {
#if ORBITERS_VRCSDK_3_8_1_OR_NEWER
            builder?.SelectAvatar(avatar);
#else
            var descriptor = avatar ? avatar.GetComponent<VRC_AvatarDescriptor>() : null;
            if (descriptor) VRCSdkControlPanelAvatarBuilder.SelectAvatar(descriptor);
#endif
        }
    }
}
