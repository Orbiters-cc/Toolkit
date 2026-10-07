using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

// The user's own photoshoot poses: what is saved from a humanoid animation, the folder they live in, and going back to the
// default pose once the chosen one is removed.
public sealed class PhotoshootPosesTests
{
    private readonly List<Object> owned = new List<Object>();
    private string folder;

    [SetUp] public void SetUp() => folder = Path.Combine(Path.GetTempPath(), "OrbitersPhotoshootPoses-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    [Test]
    public void SavedPoseIsTheClipsStart()
    {
        var clip = Own(new AnimationClip());
        var spine = EditorCurveBinding.FloatCurve("", typeof(Animator), "Spine Front-Back");
        var finger = EditorCurveBinding.FloatCurve("", typeof(Animator), "LeftHand.Index.1 Stretched");
        var smile = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile");
        AnimationUtility.SetEditorCurves(clip, new[] { spine, finger, smile }, new[]
        {
            AnimationCurve.Linear(0f, .3f, 1f, .9f), AnimationCurve.Constant(0f, 1f, -.5f), AnimationCurve.Linear(0f, 100f, 1f, 0f)
        });
        Assert.That(clip.humanMotion, Is.True);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Wave.json");
        PhotoshootPoses.Write(clip, file);

        var pose = Own(PhotoshootPoses.Read(file));
        Assert.That(pose.name, Is.EqualTo("Wave"));
        Assert.That(pose.humanMotion, Is.True, "The saved pose still poses humanoid avatars.");
        float At(EditorCurveBinding binding) => AnimationUtility.GetEditorCurve(pose, binding).Evaluate(0f);
        Assert.That(At(spine), Is.EqualTo(.3f).Within(1e-5f));
        Assert.That(At(finger), Is.EqualTo(-.5f).Within(1e-5f));
        Assert.That(At(smile), Is.EqualTo(100f).Within(1e-3f), "Curves besides the muscles are kept.");
        Assert.That(AnimationUtility.GetCurveBindings(pose), Has.Length.EqualTo(3));
    }

    [Test]
    public void AnimationsWithoutHumanoidCurvesAreRefused()
    {
        var clip = Own(new AnimationClip { name = "Spin" });
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.y"), AnimationCurve.Constant(0f, 1f, 2f));
        int before = PhotoshootPoses.Files().Count();
        Assert.Throws<InvalidOperationException>(() => PhotoshootPoses.Add(clip));
        Assert.That(PhotoshootPoses.Files().Count(), Is.EqualTo(before), "Nothing is saved.");
    }

    [Test]
    public void LibraryNumbersNamesReadsChangedFilesAgainAndRemovesOnlyItsOwn()
    {
        var library = new PhotoshootLibrary<AnimationClip>(folder, PhotoshootPoses.Read, ".json");
        var clip = Own(new AnimationClip());
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Spine Front-Back"), AnimationCurve.Constant(0f, 1f, .2f));

        string first = library.NewPath("Pose", ".json");
        PhotoshootPoses.Write(clip, first);
        string second = library.NewPath("Pose", ".json");
        Assert.That(Path.GetFileName(second), Is.EqualTo("Pose 2.json"));
        PhotoshootPoses.Write(clip, second);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a pose");
        Assert.That(library.Files().Select(Path.GetFileName), Is.EqualTo(new[] { "Pose 2.json", "Pose.json" }));

        var loaded = library.Load(first);
        Assert.That(library.Load(first), Is.SameAs(loaded), "Loaded once while the file stays the same.");
        File.SetLastWriteTimeUtc(first, File.GetLastWriteTimeUtc(first).AddSeconds(5));
        var reloaded = library.Load(first);
        Assert.That(reloaded, Is.Not.SameAs(loaded));
        Assert.That(loaded == null, Is.True, "The stale clip is destroyed.");

        string outside = Path.Combine(Path.GetTempPath(), "OrbitersPhotoshootPoses-outside-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outside, "{}");
        try
        {
            library.Remove(outside);
            Assert.That(File.Exists(outside), Is.True, "A file outside the library stays.");
        }
        finally { File.Delete(outside); }

        library.Remove(first);
        Assert.That(File.Exists(first), Is.False);
        Assert.That(reloaded == null, Is.True, "Removing destroys what was loaded from it.");
    }

    [Test]
    public void RemovedPoseGoesBackToTheDefaultPose()
    {
        var root = Own(new GameObject("Pose avatar"));
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(root.transform, false);
        var clip = new AnimationClip();
        using (var session = new PhotoshootService.LivePreviewSession())
        {
            var sceneField = typeof(PhotoshootService.LivePreviewSession).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic);
            var previewScene = EditorSceneManager.NewPreviewScene();
            sceneField.SetValue(session, previewScene);
            try
            {
                var ensure = typeof(PhotoshootService.LivePreviewSession).GetMethod("EnsureAvatarCopy", BindingFlags.Instance | BindingFlags.NonPublic);
                bool PoseChanged(AnimationClip pose)
                {
                    var arguments = new object[] { root, pose, false };
                    ensure.Invoke(session, arguments);
                    return (bool)arguments[2];
                }

                PoseChanged(clip);
                // Removing one of the user's poses destroys its clip, and the photoshoot goes back to the default pose (none).
                Object.DestroyImmediate(clip);
                Assert.That(PoseChanged(null), Is.True, "The removed pose is taken off the avatar.");
                Assert.That(PoseChanged(null), Is.False);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
                sceneField.SetValue(session, default(UnityEngine.SceneManagement.Scene));
            }
        }
    }
}
