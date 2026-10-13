using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;
using Step = Orbiters.Toolkit.Editor.VRChat.HiddenAvatarBuild.Step;

// The avatar built as an upload builds it, in a hidden copy: its steps all in one call, the copy out of the open scene and
// the SDK panel, everything the steps made freed on release, and the caller always told when it ends.
public sealed class HiddenAvatarBuildTests
{
    private readonly List<Object> owned = new List<Object>();
    private readonly List<HiddenAvatarBuild> builds = new List<HiddenAvatarBuild>();
    private Scene scene;
    private GameObject source;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        source = EditorUtility.CreateGameObjectWithHideFlags("Avatar", HideFlags.HideAndDontSave);
        SceneManager.MoveGameObjectToScene(source, scene);
        source.hideFlags = HideFlags.None;
    }

    [TearDown] public void TearDown()
    {
        foreach (var build in builds) build.Release();
        builds.Clear();
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void EveryStepRunsInOneCallOnACopyOutOfTheOpenSceneAndTheSdkPanel()
    {
        var open = SceneManager.GetActiveScene();
        bool wasDirty = open.isDirty;
        var ran = new List<string>();
        var build = Start(Steps(("first", c => { ran.Add("first " + c.scene.handle); return true; }), ("second", c => { ran.Add("second"); return true; })));

        Assert.That(build.Copy, Is.Not.Null);
        Assert.That(ran, Is.Empty, "The steps wait for the next frame: the caller shows its progress first.");
        Assert.That(build.Copy.scene, Is.Not.EqualTo(open));
        Assert.That(build.Copy.hideFlags, Is.EqualTo(HideFlags.NotEditable), "The SDK panel lists every avatar whose object isn't flagged exactly so.");
        Assert.That(open.isDirty, Is.EqualTo(wasDirty), "Making the copy doesn't touch the open scene.");

        build.Run();

        Assert.That(ran, Is.EqualTo(new[] { "first " + build.Copy.scene.handle, "second" }));
        Assert.That(build.Finished && build.Error == null && build.Done == 2, Is.True);
    }

    [Test] public void ReleaseFreesWhatTheStepsMadeAndKeepsWhatTheAvatarShared()
    {
        var shared = Own(new Mesh { name = "Shared" });
        source.AddComponent<MeshFilter>().sharedMesh = shared;
        source.AddComponent<MeshRenderer>();
        Material made = null;
        Texture2D texture = null;
        var build = Start(Steps(("paint", copy =>
        {
            texture = new Texture2D(2, 2);
            made = new Material(Shader.Find("Standard")) { mainTexture = texture };
            copy.GetComponent<MeshRenderer>().sharedMaterial = made;
            return true;
        })));
        build.Run();
        Assert.That(made != null && texture != null, Is.True);

        build.Release();

        Assert.That(made == null, Is.True, "The step's material went with the copy.");
        Assert.That(texture == null, Is.True, "And the texture it showed.");
        Assert.That(shared != null, Is.True, "The avatar's own in-memory mesh stays.");
        Assert.That(build.Copy, Is.Null);
    }

    [Test] public void ReleasingARunningBuildTellsTheCallerOnce()
    {
        var build = Start(Steps(("never", _ => throw new InvalidOperationException("ran after release"))));
        int told = 0;
        build.Completed += () => told++;

        build.Release();
        build.Release();
        build.Run();

        Assert.That(told, Is.EqualTo(1));
        Assert.That(build.Finished && build.Cancelled && build.Error != null, Is.True);
        Assert.That(build.Copy, Is.Null);
    }

    [Test] public void PlayModeAndReloadsStopRunningBuildsAndSaySo()
    {
        var build = Start(Steps());
        string error = null;
        build.Completed += () => error = build.Error;

        // What entering Play Mode (with or without a domain reload) and a script reload do.
        HiddenAvatarBuild.ReleaseAll("Play Mode started");

        Assert.That(error, Is.EqualTo("Play Mode started"), "The caller stops showing it as running.");
        Assert.That(build.Cancelled, Is.False);
        Assert.That(build.Copy, Is.Null);
    }

    [Test] public void AFailingStepStopsTheBuildWithItsMessage()
    {
        bool after = false;
        var build = Start(Steps(("fine", _ => true), ("broken", _ => throw new InvalidOperationException("boom")), ("after", _ => after = true)));
        int told = 0;
        build.Completed += () => told++;
        LogAssert.Expect(LogType.Exception, new Regex("boom"));

        build.Run();

        Assert.That(build.Error, Does.Contain("boom"));
        Assert.That(after, Is.False);
        Assert.That(told, Is.EqualTo(1));
        Assert.That(build.Copy, Is.Null, "A failed build closes its copy.");
    }

    [Test] public void TheSdkListRunsInItsOrderWithoutUploadOnlySteps()
    {
        var steps = HiddenAvatarBuild.SdkSteps();

        Assert.That(steps.Any(s => HiddenAvatarBuild.LeftOut(s.Type)), Is.False);
        Assert.That(steps.Select(s => s.Order), Is.Ordered, "The SDK's order.");
        Assert.That(HiddenAvatarBuild.LeftOut(typeof(BuildCopyAssetsHook)), Is.True, "The build cache would make the steps' objects assets.");
        var blueprint = VrcFury.Find("VF.Hooks.WhenBlueprintIdReadyHook+VrcPreuploadHook");
        if (blueprint == null) Assert.Ignore("VRCFury isn't installed.");
        Assert.That(HiddenAvatarBuild.LeftOut(blueprint), Is.True, "Its Type.Name alone is “VrcPreuploadHook”: it would clear a pending upload's callbacks.");
        Assert.That(steps.Any(s => s.Type.Name == "RemoveAvatarEditorOnly"), Is.False, "VRCFury replaced the SDK's EditorOnly removal in the SDK's list.");
        Assert.That(steps.Any(s => s.Type.FullName == "VF.Hooks.VrcPreuploadHook"), Is.True, "VRCFury's own build stays.");
    }

    [Test] public void PoiyomisLockIsLeftOutUnderEitherNamespace()
    {
        // Thry.ThryEditor.ShaderOptimizer+LockMaterialsOnUpload now, Thry.ShaderOptimizer+LockMaterialsOnUpload in older
        // Poiyomi: without VRCFury blocking it, it would lock the avatar's real materials and write shaders.
        Assert.That(HiddenAvatarBuild.LeftOut(typeof(OldShaderOptimizer.LockMaterialsOnUpload)), Is.True);
        Assert.That(HiddenAvatarBuild.LeftOut(typeof(NewShaderOptimizer.LockMaterialsOnUpload)), Is.True);
        Assert.That(HiddenAvatarBuild.LeftOut(typeof(NewShaderOptimizer)), Is.False);
    }

    [Test] public void WhatVrcFuryWouldAskAboutIsReportedInsteadOfBuilt()
    {
        var vrcFury = VrcFury.Component;
        if (vrcFury == null) Assert.Ignore("VRCFury isn't installed.");
        source.AddComponent(vrcFury);
        var descriptor = source.AddComponent<VRCAvatarDescriptor>();
        descriptor.expressionParameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        descriptor.expressionParameters.parameters = Enumerable.Range(0, 8193)
            .Select(i => new VRCExpressionParameters.Parameter { name = "P" + i, valueType = VRCExpressionParameters.ValueType.Bool }).ToArray();
        for (int i = 0; i < 257; i++)
        {
            var contact = EditorUtility.CreateGameObjectWithHideFlags("Contact " + i, HideFlags.HideAndDontSave, typeof(VRCContactReceiver));
            contact.transform.SetParent(source.transform, false);
            contact.hideFlags = HideFlags.None;
        }

        var blockers = HiddenAvatarBuild.Blockers(source);
        var build = Start(Steps(("never", _ => throw new InvalidOperationException("VRCFury would have asked"))));

        Assert.That(blockers.Select(b => b.Title), Has.Some.Contains("257 contacts").And.Some.Contains("8193 expression parameters"));
        Assert.That(build.Finished && build.Copy == null, Is.True, "Not built: VRCFury would stop it with a dialog.");
        Assert.That(build.Error, Is.EqualTo(blockers[0].Title));
    }

    [Test] public void AVrcFuryTestCopyIsNotBuiltAgain()
    {
        var test = VrcFury.Find("VF.Model.VRCFuryTest");
        if (test == null) Assert.Ignore("VRCFury isn't installed.");
        source.AddComponent(test);

        var build = Start(Steps(("never", _ => throw new InvalidOperationException("built a test copy"))));

        Assert.That(build.Finished, Is.True);
        Assert.That(build.Error, Does.Contain("VRCFury test copy"), "VRCFury would refuse with a dialog.");
        Assert.That(build.Copy, Is.Null);
    }

    private HiddenAvatarBuild Start(List<Step> steps)
    {
        var build = HiddenAvatarBuild.Start(source, steps);
        builds.Add(build);
        return build;
    }

    private static List<Step> Steps(params (string Name, Func<GameObject, bool> Run)[] steps) =>
        steps.Select((s, i) => new Step(typeof(HiddenAvatarBuildTests), i, s.Run)).ToList();

    private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }

    // Stand-ins for Poiyomi's lock in both of its namespaces: only the step's own name tells it.
    private static class OldShaderOptimizer { public sealed class LockMaterialsOnUpload { } }
    private static class NewShaderOptimizer { public sealed class LockMaterialsOnUpload { } }
}
