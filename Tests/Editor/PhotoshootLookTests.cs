using System;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEngine;
using Object = UnityEngine.Object;

// "Look at the camera" on an avatar without humanoid eye bones: the eyes a tool reports (VRChat's eye-look settings),
// with a straight rotation that differs from how the eye is posed, look straight at the camera however far it is, and
// switching the look off returns them to the pose.
public class PhotoshootLookTests
{
    private GameObject root;
    private Func<GameObject, (Transform eye, Quaternion? straight)[]> previous;

    private float gain;

    // Exact geometry here: the extra turn eyes get to read as looking at the camera is left out.
    [SetUp] public void KeepFallback() { previous = PhotoshootLook.EyeFallback; gain = PhotoshootLook.EyeGain; PhotoshootLook.EyeGain = 1f; }

    [TearDown]
    public void Clean()
    {
        PhotoshootLook.EyeFallback = previous;
        PhotoshootLook.EyeGain = gain;
        if (root != null) Object.DestroyImmediate(root);
    }

    [Test]
    public void ReportedEyesLookAtTheCameraAndReturnToThePose()
    {
        root = new GameObject("Avatar");
        var head = new GameObject("Head").transform; head.SetParent(root.transform, false); head.localPosition = new Vector3(0, 1.6f, 0);
        var eye = new GameObject("Eye").transform; eye.SetParent(head, false); eye.localPosition = new Vector3(.03f, .05f, .08f);
        // The eye bone points up its own Y axis; looking straight it faces the avatar's forward (+Z).
        var straight = Quaternion.FromToRotation(Vector3.up, Vector3.forward);
        eye.localRotation = Quaternion.Euler(0, 0, 10) * straight;
        PhotoshootLook.EyeFallback = avatar => new (Transform, Quaternion?)[] { (eye, straight) };

        var rig = PhotoshootLook.Prepare(root);
        Assert.That(rig.Eyes, Has.Length.EqualTo(1));
        PhotoshootLook.Capture(rig);
        var posed = eye.localRotation;

        // A camera in front and 20° to the side.
        var camera = eye.position + Quaternion.Euler(0, 20, 0) * Vector3.forward * 2f;
        Assert.That(PhotoshootLook.Apply(rig, camera, true, 1f), Is.True);
        var looking = eye.rotation * Vector3.up;
        Assert.That(Vector3.Angle(looking, camera - eye.position), Is.LessThan(.5f), "The eye looks at the camera.");

        // Far to the side: with the eyes doing all the looking, they still reach the camera.
        var side = eye.position + Quaternion.Euler(0, 75, 0) * Vector3.forward * 2f;
        PhotoshootLook.Apply(rig, side, true, 1f);
        Assert.That(Vector3.Angle(eye.rotation * Vector3.up, side - eye.position), Is.LessThan(.5f), "The eyes look all the way.");

        PhotoshootLook.Apply(rig, camera, false, 1f);
        Assert.That(Quaternion.Angle(eye.localRotation, posed), Is.LessThan(.01f), "Switching the look off returns to the pose.");
    }
}
