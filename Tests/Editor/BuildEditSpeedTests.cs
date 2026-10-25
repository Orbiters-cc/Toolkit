using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

/// <summary>
/// Build steps that edit big controllers: edits in a <see cref="ControllerEdits"/> batch don't rebuild the controller each
/// time, and controllers another build step made for the copy (VRCFury's working copies) are changed in place instead of
/// copied whole again.
/// </summary>
public sealed class BuildEditSpeedTests
{
    private const string CacheFolder = "Assets/OrbitersToolkitBuildCache";
    private Scene scene;
    private string folder;
    private bool cacheExisted;
    private readonly List<string> files = new List<string>();
    private readonly List<GameObject> builds = new List<GameObject>();
    private readonly List<Object> owned = new List<Object>();

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        folder = "Assets/OrbitersBuildEditTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        cacheExisted = AssetDatabase.IsValidFolder(CacheFolder);
    }

    [TearDown] public void TearDown()
    {
        foreach (var build in builds) AttachmentAnimationBuild.Release(build);
        builds.Clear();
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        foreach (var file in files) AssetDatabase.DeleteAsset(file);
        files.Clear();
        AssetDatabase.DeleteAsset(folder);
        if (!cacheExisted && AssetDatabase.IsValidFolder(CacheFolder) && AssetDatabase.FindAssets("", new[] { CacheFolder }).Length == 0)
            AssetDatabase.DeleteAsset(CacheFolder);
    }

    [Test] public void EditsInABatchKeepTheirResultAndEveryLayerGetsItsStateMachineAndMaskBack()
    {
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/big.controller");
        controller.AddParameter("On", AnimatorControllerParameterType.Bool);
        var mask = new AvatarMask { name = "Mask", transformCount = 1 };
        mask.SetTransformPath(0, "Hat");
        AssetDatabase.AddObjectToAsset(mask, controller);
        var machine = controller.layers[0].stateMachine;
        var sub = machine.AddStateMachine("Folder");
        var states = Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? machine : sub).AddState("State " + i)).ToList();
        foreach (var state in states) state.AddTransition(state).AddCondition(AnimatorConditionMode.If, 0, "On");
        var layers = controller.layers; layers[0].avatarMask = mask; controller.layers = layers;
        var synced = new AnimatorControllerLayer { name = "Synced", syncedLayerIndex = 0, defaultWeight = 1, stateMachine = new AnimatorStateMachine { name = "Synced" } };
        AssetDatabase.AddObjectToAsset(synced.stateMachine, controller);
        var clip = new AnimationClip { name = "Override" };
        AssetDatabase.AddObjectToAsset(clip, controller);
        synced.SetOverrideMotion(states[0], clip);
        controller.layers = controller.layers.Append(synced).ToArray();
        var before = controller.layers.Select(l => (l.name, l.stateMachine, l.avatarMask, l.syncedLayerIndex)).ToList();

        using (ControllerEdits.Batch(new[] { controller, controller, null }))
        {
            Assert.AreNotSame(machine, controller.layers[0].stateMachine, "detached meanwhile");
            Assert.Null(controller.layers[0].avatarMask);
            foreach (var state in states)
            {
                var transition = state.transitions[0];
                var conditions = transition.conditions;
                conditions[0].parameter = "Shown";
                transition.conditions = conditions;
                state.speedParameter = "Speed";
            }
            mask.SetTransformPath(0, "Cap");
            var parameters = controller.parameters; parameters[0].name = "Shown"; controller.parameters = parameters;
        }

        CollectionAssert.AreEqual(before, controller.layers.Select(l => (l.name, l.stateMachine, l.avatarMask, l.syncedLayerIndex)).ToList());
        Assert.AreSame(clip, controller.layers[1].GetOverrideMotion(states[0]), "a synced layer keeps its own motions");
        Assert.AreEqual(20, sub.states.Length);
        Assert.True(states.All(s => s.transitions[0].conditions[0].parameter == "Shown" && s.speedParameter == "Speed"));
        Assert.AreEqual("Cap", mask.GetTransformPath(0));
        Assert.AreEqual("Shown", controller.parameters[0].name);
        Assert.False(Resources.FindObjectsOfTypeAll<AnimatorStateMachine>().Any(m => m.name == "Orbiters edits"), "the stand-in is gone");
        AssetDatabase.SaveAssetIfDirty(controller);
        Assert.False(AssetDatabase.LoadAllAssetsAtPath(folder + "/big.controller").OfType<AnimatorStateMachine>().Any(m => m.name == "Orbiters edits"),
            "and never became part of the file");
    }

    [Test] public void EditsInABatchDontRebuildABigControllerEachTime()
    {
        var controller = new AnimatorController();
        owned.Add(controller);
        controller.AddLayer("Base");
        controller.AddParameter("On", AnimatorControllerParameterType.Bool);
        var machine = controller.layers[0].stateMachine;
        var states = Enumerable.Range(0, 1200).Select(i => machine.AddState("State " + i)).ToList();
        foreach (var state in states) state.AddTransition(state).AddCondition(AnimatorConditionMode.If, 0, "On");
        long Rename(IEnumerable<AnimatorState> some, string to)
        {
            var watch = Stopwatch.StartNew();
            foreach (var state in some)
            {
                var transition = state.transitions[0];
                var conditions = transition.conditions;
                conditions[0].parameter = to;
                transition.conditions = conditions;
            }
            return watch.ElapsedMilliseconds;
        }
        long alone = Rename(states.Take(200), "A");
        long batched;
        using (ControllerEdits.Batch(new[] { controller })) batched = Rename(states.Skip(200).Take(200), "B");
        Assert.Less(batched * 5, alone + 20, $"200 edits: {alone} ms one by one, {batched} ms in a batch");
        Assert.True(states.Skip(200).Take(200).All(s => s.transitions[0].conditions[0].parameter == "B"));
    }

    [Test] public void ABuildControllerAnotherStepMadeIsChangedInPlaceAndOnlyTheProjectsClipsAreCopied()
    {
        var avatar = new GameObject("Built avatar");
        SceneManager.MoveGameObjectToScene(avatar, scene);
        var bone = new GameObject("Bone").transform; bone.SetParent(avatar.transform, false);
        var clip = new AnimationClip { name = "Authored" };
        AssetDatabase.CreateAsset(clip, folder + "/authored.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Old/Bone", typeof(Transform), "m_LocalPosition.y"), AnimationCurve.Constant(0, 1, .25f));
        // A controller a build step made for this copy, as VRCFury's working copies are (this test keeps it in the cache folder).
        if (!AssetDatabase.IsValidFolder(CacheFolder)) AssetDatabase.CreateFolder("Assets", CacheFolder.Substring("Assets/".Length));
        string path = CacheFolder + "/edit-speed-test-" + Guid.NewGuid().ToString("N") + ".controller";
        files.Add(path);
        var built = AnimatorController.CreateAnimatorControllerAtPath(path);
        var state = built.layers[0].stateMachine.AddState("Play");
        state.motion = clip;
        var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = built, isDefault = false } };
        descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
        builds.Add(avatar);

        var build = AttachmentAnimationBuild.Prepare(avatar);
        Assert.AreSame(built, descriptor.baseAnimationLayers[0].animatorController, "changed in place, not copied");
        Assert.AreSame(state, built.layers[0].stateMachine.states.Single().state, "its states too, and the layer has its own state machine back");
        Assert.True(AttachmentAnimationBuild.Owns(built));
        var played = (AnimationClip)state.motion;
        Assert.AreNotSame(clip, played, "the project's clip is copied for the build");
        Assert.True(AttachmentAnimationBuild.IsBuildData(played));

        build.Moved(new[] { (bone, "Old/Bone") });
        Assert.True(AnimationUtility.GetCurveBindings(played).Any(b => b.path == "Bone"));
        Assert.True(AnimationUtility.GetCurveBindings(clip).Any(b => b.path == "Old/Bone"), "the authored clip is unchanged");
    }
}
