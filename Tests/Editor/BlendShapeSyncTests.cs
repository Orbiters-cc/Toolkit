using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class BlendShapeSyncTests
{
    private GameObject root;
    private SkinnedMeshRenderer body, accessory;
    private string folder;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("Blendshape sync avatar");
        body = AddRenderer("Body", "Smile", "Breasts_Big", "Flex L", "a b", "ab", "Only body");
        accessory = AddRenderer("Accessory", "Smile", "breasts big", "FlexL", "A_B", "ab", "Only accessory");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var renderer in new[] { body, accessory }) if (renderer != null) Object.DestroyImmediate(renderer.sharedMesh);
        Object.DestroyImmediate(root);
        if (folder != null) AssetDatabase.DeleteAsset(folder);
    }

    [Test]
    public void PlanMatchesExactThenUnambiguousNormalizedNames()
    {
        var plan = BlendShapeSync.Plan(body, accessory);
        Assert.That(plan.Select(c => c.SourceShape + ">" + c.DestinationShape),
            Is.EqualTo(new[] { "Smile>Smile", "Breasts_Big>breasts big", "Flex L>FlexL", "ab>ab" }));
        Assert.That(plan.All(c => c.Source == body && c.Destination == accessory), Is.True);
        Assert.That(BlendShapeSync.Plan(body, body), Is.Empty);
        Assert.That(BlendShapeSync.Plan(body, null), Is.Empty);
    }

    [Test]
    public void CopyWeightsCopiesCurrentWeightsAndIsUndoable()
    {
        body.SetBlendShapeWeight(0, 40);
        body.SetBlendShapeWeight(1, 75);
        var plan = BlendShapeSync.Plan(body, accessory);
        Undo.IncrementCurrentGroup();
        Assert.That(BlendShapeSync.CopyWeights(plan, true), Is.EqualTo(2));
        Assert.That(accessory.GetBlendShapeWeight(0), Is.EqualTo(40));
        Assert.That(accessory.GetBlendShapeWeight(1), Is.EqualTo(75));
        Assert.That(BlendShapeSync.CopyWeights(plan, true), Is.Zero, "Unchanged weights are not written again.");
        Undo.PerformUndo();
        Assert.That(accessory.GetBlendShapeWeight(0), Is.Zero);
        Assert.That(accessory.GetBlendShapeWeight(1), Is.Zero);
    }

    [Test]
    public void ApplyWithoutBuiltControllersOnlyCopiesWeightsAndLeavesAuthoringControllersAlone()
    {
        CreateFolder();
        var controller = CreateController(false);
        var clip = CreateSmileClip();
        var state = controller.layers[0].stateMachine.AddState("Smile");
        state.motion = clip;
        body.SetBlendShapeWeight(0, 30);
        var result = BlendShapeSync.Apply(root, BlendShapeSync.Plan(body, accessory), "Accessory");
        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.AnimationsLinked, Is.False);
        Assert.That(result.WeightsCopied, Is.EqualTo(1));
        Assert.That(accessory.GetBlendShapeWeight(0), Is.EqualTo(30));
        Assert.That(state.motion, Is.SameAs(clip));
        Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1));
    }

    [Test]
    public void ApplyMakesAnimationsOfTheSourceShapeAnimateTheDestinationInBuiltControllers()
    {
        CreateFolder();
        var controller = CreateController(true);
        var clip = CreateSmileClip();
        var state = controller.layers[0].stateMachine.AddState("Smile");
        state.motion = clip;
        var result = BlendShapeSync.Apply(root, BlendShapeSync.Plan(body, accessory), "Accessory");
        Assert.That(result.Success && result.AnimationsLinked && result.Links.Success, Is.True, result.Message);
        var output = (AnimationClip)state.motion;
        Assert.That(output, Is.Not.SameAs(clip));
        Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1), "The authoring clip is not modified.");
        foreach (float time in new[] { 0f, 0.3f, 0.7f, 1f })
        {
            output.SampleAnimation(root, time);
            Assert.That(accessory.GetBlendShapeWeight(0), Is.EqualTo(body.GetBlendShapeWeight(0)).Within(0.0001f));
        }
        Assert.That(controller.parameters, Is.Empty, "Direct copies need no factor parameter.");
        Assert.That(BlendShapeLinkEngine.Applied[controller.GetInstanceID()].Any(r => r.Label == "Accessory" && r.Effect == "Smile"), Is.True);
        BlendShapeSync.Apply(root, BlendShapeSync.Plan(body, accessory), "Accessory");
        Assert.That(state.motion, Is.SameAs(output), "Repeated builds do not clone again.");
    }

    private AnimationClip CreateSmileClip()
    {
        var clip = new AnimationClip { name = "Smile" };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile"),
            AnimationCurve.Linear(0, 0, 1, 100));
        AssetDatabase.CreateAsset(clip, folder + "/smile.anim");
        return clip;
    }

    private void CreateFolder()
    {
        folder = "Assets/BlendShapeSyncTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
    }

    private AnimatorController CreateController(bool temporary)
    {
        string path = folder;
        if (temporary)
        {
            AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
            path += "/com.vrcfury.temp";
        }
        var controller = AnimatorController.CreateAnimatorControllerAtPath(path + "/fx.controller");
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        return controller;
    }

    private SkinnedMeshRenderer AddRenderer(string name, params string[] shapes)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        foreach (string shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }
}
