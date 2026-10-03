using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Posing;
using UnityEditor;
using UnityEngine;

public class MirrorPoseTests
{
    [Test]
    public void ExplicitRotationAndPositionMirrorWithoutChangingUneditedBones()
    {
        var root = new GameObject("Mirror fixture");
        var mesh = new Mesh();
        bool enabled = MirrorPoseService.Enabled;
        try
        {
            var left = new GameObject("LeftArm").transform; left.SetParent(root.transform, false);
            var right = new GameObject("RightArm").transform; right.SetParent(root.transform, false);
            left.localPosition = Vector3.left; right.localPosition = Vector3.right;
            var skin = root.AddComponent<SkinnedMeshRenderer>();
            skin.bones = new[] { left, right }; skin.sharedMesh = mesh;
            mesh.bindposes = new[] { left.worldToLocalMatrix, right.worldToLocalMatrix };
            Assert.IsTrue(MirrorPoseService.Enable(root.transform, skin));
            left.localRotation = Quaternion.Euler(15, 20, 30);
            left.localPosition += new Vector3(.1f, .2f, .3f);
            var edits = new[] { Edit(left, "m_LocalRotation.x"), Edit(left, "m_LocalPosition.x") };
            typeof(MirrorPoseService).GetMethod("MirrorEdits", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { edits });
            Assert.Less(Quaternion.Angle(right.localRotation, new Quaternion(left.localRotation.x, -left.localRotation.y, -left.localRotation.z, left.localRotation.w)), .001f);
            Assert.Less(Vector3.Distance(right.localPosition, new Vector3(.9f, .2f, .3f)), .0001f);
            Assert.AreEqual(Vector3.zero, root.transform.position);
        }
        finally
        {
            MirrorPoseService.Disable();
            Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
            if (enabled) MirrorPoseService.Enable();
        }
    }

    [Test]
    public void PlayPoseHoldPreservesOnlyEditedChannelsAndClearReleasesThem()
    {
        var bone = new GameObject("Pose hold fixture").transform;
        try
        {
            var desired = Quaternion.Euler(20, 30, 40);
            bone.localRotation = desired;
            PlayModePoseOverrides.Hold(bone, 1);
            bone.localRotation = Quaternion.identity;
            bone.localPosition = Vector3.up;
            var apply = typeof(PlayModePoseOverrides).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);
            apply.Invoke(null, null);
            Assert.Less(Quaternion.Angle(desired, bone.localRotation), .001f);
            Assert.AreEqual(Vector3.up, bone.localPosition);
            PlayModePoseOverrides.Clear();
            bone.localRotation = Quaternion.identity;
            apply.Invoke(null, null);
            Assert.Less(Quaternion.Angle(Quaternion.identity, bone.localRotation), .001f);
        }
        finally { PlayModePoseOverrides.Clear(); Object.DestroyImmediate(bone.gameObject); }
    }

    private static UndoPropertyModification Edit(Transform target, string path) => new UndoPropertyModification
    { currentValue = new PropertyModification { target = target, propertyPath = path } };
}
