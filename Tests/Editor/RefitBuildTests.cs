using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Animations of body blendshapes drive the shapes generated from them on refitted meshes, on the build copy only.</summary>
public sealed class RefitBuildTests
{
    private GameObject root;
    private SkinnedMeshRenderer body, accessory;
    private Mesh bodyMesh, accessoryMesh;
    private UnityEngine.SceneManagement.Scene scene;
    private string folder;

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Refit build avatar");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        bodyMesh = RefitRecordsTests.MakeMesh("Body", "orbit muscles", "biceps flex right", "FLEX left", "Smile");
        accessoryMesh = RefitRecordsTests.MakeMesh("Accessory", "orbit muscles", "refit_biceps flex right", "FLEX left", "Native flex");
        body = AddRenderer("Body", bodyMesh);
        accessory = AddRenderer("Accessory", accessoryMesh);
        Record(accessory);
        folder = "Assets/RefitBuildTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(root);
        AssetDatabase.DeleteAsset(folder);
        Object.DestroyImmediate(bodyMesh);
        Object.DestroyImmediate(accessoryMesh);
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test]
    public void OnlyAppliedRefitsWithTheirRecordedShapesAreLinked()
    {
        var links = RefitBuild.Collect(root);
        Assert.That(links.Count, Is.EqualTo(1));
        Assert.That(links[0].Shapes.Select(s => s.generated), Is.EqualTo(new[] { "orbit muscles", "refit_biceps flex right", "FLEX left", "missing" }), "A mapping whose shape is gone is skipped when linking.");
        accessory.gameObject.SetActive(false); // An outfit toggle can turn it on in game.
        Assert.That(RefitBuild.Collect(root).Count, Is.EqualTo(1));
        accessory.sharedMesh = bodyMesh; // The user replaced the refitted mesh.
        Assert.That(RefitBuild.Collect(root), Is.Empty);
    }

    [Test]
    public void DirectLinksCopyAnimatedCurvesThroughNestedTreesWithoutTouchingAuthoringClips()
    {
        var second = AddRenderer("Second accessory", accessoryMesh);
        Record(second);
        var controller = CreateController(true);
        var source = new AnimationClip { name = "Flex animation" };
        var curve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.4f, 85), new Keyframe(1, 20)) { postWrapMode = WrapMode.PingPong };
        var sourceBinding = EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right");
        var destinationBinding = EditorCurveBinding.FloatCurve("Accessory", typeof(SkinnedMeshRenderer), "blendShape.refit_biceps flex right");
        AnimationUtility.SetEditorCurve(source, sourceBinding, curve);
        AnimationUtility.SetEditorCurve(source, EditorCurveBinding.FloatCurve("Unrelated", typeof(SkinnedMeshRenderer), "blendShape.FLEX left"), curve);
        AssetDatabase.CreateAsset(source, folder + "/source.anim");
        var tree = new BlendTree { name = "Nested", blendParameter = "Existing" };
        tree.children = new[] { new ChildMotion { motion = source, timeScale = 1 } };
        AssetDatabase.AddObjectToAsset(tree, controller);
        controller.AddParameter("Existing", AnimatorControllerParameterType.Float);
        controller.layers[0].stateMachine.AddState("Flex").motion = tree;
        body.SetBlendShapeWeight(1, 63);

        var result = RefitBuild.Apply(root);
        Assert.That(result.Links.Success, Is.True, result.Message);
        Assert.That(accessory.GetBlendShapeWeight(1), Is.EqualTo(63), "The build copy starts with the body's weights.");
        var output = (AnimationClip)tree.children[0].motion;
        Assert.That(output, Is.Not.SameAs(source));
        var copied = AnimationUtility.GetEditorCurve(output, destinationBinding);
        var original = AnimationUtility.GetEditorCurve(source, sourceBinding);
        Assert.That(copied, Is.Not.Null);
        Assert.That(copied.keys, Is.EqualTo(original.keys));
        Assert.That(copied.postWrapMode, Is.EqualTo(original.postWrapMode));
        foreach (float time in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            output.SampleAnimation(root, time);
            Assert.That(accessory.GetBlendShapeWeight(1), Is.EqualTo(body.GetBlendShapeWeight(1)).Within(0.0001f));
            Assert.That(second.GetBlendShapeWeight(1), Is.EqualTo(body.GetBlendShapeWeight(1)).Within(0.0001f));
        }
        Assert.That(AnimationUtility.GetEditorCurve(source, destinationBinding), Is.Null, "The authoring clip is untouched.");
        Assert.That(AnimationUtility.GetEditorCurve(output, EditorCurveBinding.FloatCurve("Accessory", typeof(SkinnedMeshRenderer), "blendShape.FLEX left")), Is.Null);
        Assert.That(controller.parameters.Select(p => p.name), Is.EqualTo(new[] { "Existing" }), "Direct copies add no parameter.");
        Assert.That(RefitBuild.IsLinked(root, accessory, "refit_biceps flex right"), Is.True);
        RefitBuild.Apply(root);
        Assert.That(tree.children[0].motion, Is.SameAs(output), "Repeated preprocess must not create another clip.");
    }

    [Test]
    public void AuthoringControllersAreNeverModified()
    {
        var controller = CreateController(false);
        var clip = new AnimationClip { name = "Authoring" };
        AssetDatabase.CreateAsset(clip, folder + "/authoring.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right"),
            AnimationCurve.Linear(0, 0, 1, 100));
        var state = controller.layers[0].stateMachine.AddState("Flex");
        state.motion = clip;
        var result = RefitBuild.Apply(root);
        Assert.That(result.AnimationsLinked, Is.False);
        Assert.That(state.motion, Is.SameAs(clip));
        Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1));
        Assert.That(controller.parameters, Is.Empty);
    }

    [Test]
    public void CaptureSurvivesRendererReparentingAndMeshCloningOnTheBuildCopy()
    {
        GameObject clone = null;
        Mesh clonedMesh = null;
        try
        {
            clone = Object.Instantiate(root);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
            Assert.That(new RefitCaptureHook().callbackOrder, Is.LessThan(-10100), "Before attachments and VRCFury move meshes.");
            Assert.That(new RefitLinkHook().callbackOrder, Is.InRange(-9000, -8900), "After MCB's correctives, before attachment syncs.");
            Assert.That(new RefitCaptureHook().OnPreprocessAvatar(clone), Is.True);
            var copiedAccessory = clone.transform.Find("Accessory").GetComponent<SkinnedMeshRenderer>();
            clonedMesh = Object.Instantiate(accessoryMesh);
            copiedAccessory.sharedMesh = clonedMesh;
            copiedAccessory.name = "Moved accessory";
            copiedAccessory.transform.SetParent(clone.transform.Find("Body"), false);
            var controller = CreateController(true, clone);
            var clip = new AnimationClip { name = "Source" };
            AssetDatabase.CreateAsset(clip, folder + "/source.anim");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right"),
                AnimationCurve.Linear(0, 0, 1, 100));
            var state = controller.layers[0].stateMachine.AddState("Flex");
            state.motion = clip;
            Assert.That(new RefitLinkHook().OnPreprocessAvatar(clone), Is.True);
            var output = (AnimationClip)state.motion;
            Assert.That(AnimationUtility.GetEditorCurve(output, EditorCurveBinding.FloatCurve(
                "Body/Moved accessory", typeof(SkinnedMeshRenderer), "blendShape.refit_biceps flex right")), Is.Not.Null);
            output.SampleAnimation(clone, 0.65f);
            Assert.That(copiedAccessory.GetBlendShapeWeight(1), Is.EqualTo(65).Within(0.001f));
            Assert.That(accessory.GetBlendShapeWeight(1), Is.Zero, "The scene avatar is untouched.");
        }
        finally
        {
            if (clone != null) Object.DestroyImmediate(clone);
            if (clonedMesh != null) Object.DestroyImmediate(clonedMesh);
        }
    }

    [Test]
    public void AGeneratedShapeRemovedDuringTheBuildFailsItWhenItsBodyShapeIsAnimated()
    {
        GameObject clone = null;
        Mesh stripped = null;
        try
        {
            clone = Object.Instantiate(root);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
            RefitBuild.Capture(clone);
            stripped = RefitRecordsTests.MakeMesh("Stripped", "orbit muscles", "FLEX left");
            clone.transform.Find("Accessory").GetComponent<SkinnedMeshRenderer>().sharedMesh = stripped;
            var controller = CreateController(true, clone);
            Assert.That(RefitBuild.Apply(clone).Success, Is.True, "Nothing animates the removed shape's body shape: nothing is lost.");
            var clip = new AnimationClip { name = "Source" };
            AssetDatabase.CreateAsset(clip, folder + "/flex.anim");
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.biceps flex right"),
                AnimationCurve.Linear(0, 0, 1, 100));
            controller.layers[0].stateMachine.AddState("Flex").motion = clip;
            Assert.Throws<InvalidOperationException>(() => RefitBuild.Apply(clone));
        }
        finally
        {
            if (clone != null) Object.DestroyImmediate(clone);
            if (stripped != null) Object.DestroyImmediate(stripped);
        }
    }

    private void Record(SkinnedMeshRenderer mesh)
    {
        var record = mesh.gameObject.AddComponent<OrbitersRefit>();
        record.body = body;
        record.mesh = mesh.sharedMesh;
        record.shapes = new List<RefitShape>
        {
            new RefitShape("orbit muscles", "orbit muscles"), new RefitShape("biceps flex right", "refit_biceps flex right"),
            new RefitShape("FLEX left", "FLEX left"), new RefitShape("missing", "missing"),
        };
    }

    private AnimatorController CreateController(bool temporary, GameObject avatar = null)
    {
        string controllerFolder = folder;
        if (temporary)
        {
            if (!AssetDatabase.IsValidFolder(folder + "/com.vrcfury.temp")) AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
            controllerFolder += "/com.vrcfury.temp";
        }
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetDatabase.GenerateUniqueAssetPath(controllerFolder + "/fx.controller"));
        (avatar ?? root).AddComponent<Animator>().runtimeAnimatorController = controller;
        return controller;
    }

    private SkinnedMeshRenderer AddRenderer(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }
}
