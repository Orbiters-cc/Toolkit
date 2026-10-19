using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System.Linq;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.XRay
{
    /// <summary>
    /// Scene gizmos (armature, mesh edges, weight paint) live under the avatar. The build copy drops them before avatar
    /// tools measure it: VRCFury sized every renderer's bounds to include the armature gizmo.
    /// </summary>
    internal sealed class XRayBuildCopyHook : IVRCSDKPreprocessAvatarCallback
    {
        // Before every avatar tool (VRCFury runs at -10000).
        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            var gizmos = avatarRoot.GetComponentsInChildren<Transform>(true)
                .Where(t => XRayArmatureMeshGenerator.IsPackageGizmoObject(t.gameObject)).ToArray();
            foreach (var gizmo in gizmos)
                if (gizmo != null) Object.DestroyImmediate(gizmo.gameObject);
            return true;
        }
    }
}
