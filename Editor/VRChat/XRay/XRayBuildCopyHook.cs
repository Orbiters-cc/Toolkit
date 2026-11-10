using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Build;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.XRay
{
    /// <summary>
    /// Scene gizmos (armature, mesh edges, weight paint) live under the avatar. The build copy drops them before avatar
    /// tools measure it: VRCFury sized every renderer's bounds to include the armature gizmo.
    /// </summary>
    internal sealed class XRayBuildCopyHook : BuildStep
    {
        // Before every avatar tool (VRCFury runs at -10000).
        public override BuildPhase Phase => BuildPhase.Start;

        public override bool Run(BuildContext context)
        {
            var avatarRoot = context.Avatar;
            var gizmos = avatarRoot.GetComponentsInChildren<Transform>(true)
                .Where(t => XRayArmatureMeshGenerator.IsPackageGizmoObject(t.gameObject)).ToArray();
            foreach (var gizmo in gizmos)
                if (gizmo != null) Object.DestroyImmediate(gizmo.gameObject);
            return true;
        }
    }
}
