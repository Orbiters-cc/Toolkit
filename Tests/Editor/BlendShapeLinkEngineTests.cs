using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

public sealed class BlendShapeLinkEngineTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private string folder;
    private TestUndoSandbox sandbox;

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Blendshape link avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        folder = "Assets/BlendShapeLinkTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
        BlendShapeLinkEngine.BeginBuild();
    }

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        AssetDatabase.DeleteAsset(folder);
        // The controller APIs record Undo steps ("Layer added", "State added"): they leave with the test.
        sandbox.End();
    }

    [Test]
    public void LinkKeepsGestureMasksAndClearsOnlyTheFxMask()
    {
        AddRenderer(Child("Body"), "Smile", "Fix");
        var hands = Asset(new AvatarMask(), "hands.mask");
        var fxMask = Asset(new AvatarMask(), "fx.mask");
        var gesture = Controller("gesture");
        gesture.AddLayer("Hands");
        var gestureLayers = gesture.layers;
        gestureLayers[1].avatarMask = hands;
        gesture.layers = gestureLayers;
        gesture.layers[1].stateMachine.AddState("Smile").motion = Clip("smile", Shape("Body", "Smile", 0, 100));
        var fx = Controller("fx");
        var fxLayers = fx.layers;
        fxLayers[0].avatarMask = fxMask;
        fx.layers = fxLayers;
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.baseAnimationLayers = new[]
        {
            new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Gesture, animatorController = gesture, mask = hands },
            new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx, mask = fxMask }
        };

        var result = BlendShapeLinkEngine.Apply(root, new[] { FactorLink(BlendShapeLinkEndpoint.BlendShape, "Fix", null) }, "test");

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(gesture.layers[1].avatarMask, Is.SameAs(hands), "The Gesture layer keeps limiting its bones.");
        Assert.That(descriptor.baseAnimationLayers[0].mask, Is.SameAs(hands));
        var copy = fx.layers.Single(l => l.name == BlendShapeLinkEngine.FxLayerPrefix + "Hands");
        Assert.That(copy.avatarMask, Is.Null, "The FX copy plays the blendshape curves unmasked.");
        Assert.That(copy.stateMachine, Is.SameAs(gesture.layers[1].stateMachine));
        Assert.That(fx.layers[0].avatarMask, Is.SameAs(fxMask), "Untouched FX layers keep their masks.");
        Assert.That(descriptor.baseAnimationLayers[1].mask, Is.Null);
    }

    [Test]
    public void SignatureMatchesOnlyClipsWithEveryChannel()
    {
        var trigger = Own(Clip(null, Shape("Body", "Smile", 100, 100), Pose("Armature/Hips", 30)));
        trigger.name = "Wave";
        var otherPose = Own(Clip(null, Shape("Body", "Smile", 100, 100), Pose("Armature/Hips", -20)));
        otherPose.name = "Point";
        var remappedCopy = Own(Clip(null, Shape("Remapped/Body", "Smile", 100, 100), Pose("Armature/Hips", 30), Shape("Clothes", "Smile", 100, 100)));
        remappedCopy.name = "VF Copy";
        var signature = AnimationClipSignature.Build(trigger);

        Assert.That(signature.Matches(trigger), Is.True);
        Assert.That(signature.Matches(remappedCopy), Is.True, "Remapped paths and added curves still match.");
        Assert.That(signature.Matches(otherPose), Is.False, "One shared channel is not enough.");
        Assert.That(AnimationClipSignature.MatchesClip(otherPose, "Wave", signature), Is.False);
    }

    [Test]
    public void ShapeTriggeredEffectStartsFromTheAvatarsValue()
    {
        var hat = Child("Hat");
        var effect = Clip("grow", Curve("Hat", typeof(Transform), "m_LocalScale.x", 1.2f), Curve("Hat", typeof(Transform), "m_LocalScale.y", 1.2f),
            Curve("Hat", typeof(Transform), "m_LocalScale.z", 1.2f));
        var variant = ApplyShapeTriggeredEffect(effect);

        foreach (var (time, scale) in new[] { (0f, 1f), (0.25f, 1.05f), (0.5f, 1.1f), (0.75f, 1.15f), (1f, 1.2f) })
        {
            variant.SampleAnimation(root, time);
            Assert.That(hat.localScale.x, Is.EqualTo(scale).Within(0.0001f), $"Scale at {time}s.");
        }
    }

    [Test]
    public void ShapeTriggeredMaterialSwapSwitchesAtHalfActivation()
    {
        var renderer = Child("Hat").gameObject.AddComponent<MeshRenderer>();
        var normal = Asset(new Material(Shader.Find("Standard")), "normal.mat");
        var swapped = Asset(new Material(Shader.Find("Standard")), "swapped.mat");
        renderer.sharedMaterial = normal;
        var slot = EditorCurveBinding.PPtrCurve("Hat", typeof(MeshRenderer), "m_Materials.Array.data[0]");
        var effect = Clip("swap");
        AnimationUtility.SetObjectReferenceCurve(effect, slot, new[] { new ObjectReferenceKeyframe { time = 0f, value = swapped } });
        var keys = AnimationUtility.GetObjectReferenceCurve(ApplyShapeTriggeredEffect(effect), slot);

        Assert.That(keys, Is.Not.Null.And.Not.Empty, "The material swap reaches the generated clip.");
        foreach (var (time, material) in new[] { (0f, normal), (0.4f, normal), (0.5f, swapped), (1f, swapped) })
            // Unity may hand back another wrapper of the same material: compare as Unity objects.
            Assert.That(keys.Last(k => k.time <= time).value, Is.EqualTo(material), $"Material at {time}s.");
    }

    [Test]
    public void SignatureRequiresDistinctMatchesForRemappedChannels()
    {
        var trigger = Own(Clip(null, Shape("Body", "Smile", 100, 100), Shape("Clothes", "Smile", 100, 100)));
        var missing = Own(Clip(null, Shape("Body", "Smile", 100, 100), Pose("Unrelated", 30)));
        var remapped = Own(Clip(null, Shape("Remapped/Body", "Smile", 100, 100), Shape("Remapped/Clothes", "Smile", 100, 100)));
        var signature = AnimationClipSignature.Build(trigger);
        Assert.That(signature.Matches(missing), Is.False, "A single curve cannot satisfy both source channels.");
        Assert.That(signature.Matches(remapped), Is.True, "Distinct remapped channels still match.");
    }

    [Test]
    public void ShapeTriggeredEffectSamplesCurveProductsBetweenKeys()
    {
        var hat = Child("Hat");
        var effect = Clip("grow", (EditorCurveBinding.FloatCurve("Hat", typeof(Transform), "m_LocalScale.x"),
            AnimationCurve.Linear(0f, 1f, 1f, 2f)));
        var variant = ApplyShapeTriggeredEffect(effect);
        // Baseline 1, overlay 1+t, activation t: 1+t*t, not interpolation of the two endpoint products.
        foreach (float time in new[] { 0.1f, 0.25f, 0.4f, 0.6f, 0.75f, 0.9f })
        {
            variant.SampleAnimation(root, time);
            Assert.That(hat.localScale.x, Is.EqualTo(1f + time * time).Within(0.0002f), $"Curve product at {time}s.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ShapeTriggeredMaterialSwapPreservesNullReferences(bool clearWhenActive)
    {
        var renderer = Child("Hat").gameObject.AddComponent<MeshRenderer>();
        var material = Asset(new Material(Shader.Find("Standard")), "material.mat");
        renderer.sharedMaterial = clearWhenActive ? material : null;
        var slot = EditorCurveBinding.PPtrCurve("Hat", typeof(MeshRenderer), "m_Materials.Array.data[0]");
        var effect = Clip("swap");
        AnimationUtility.SetObjectReferenceCurve(effect, slot, new[]
        {
            new ObjectReferenceKeyframe { time = 0f, value = clearWhenActive ? null : material }
        });
        var keys = AnimationUtility.GetObjectReferenceCurve(ApplyShapeTriggeredEffect(effect), slot);
        Assert.That(keys.First().time, Is.Zero);
        Assert.That(keys.Last(k => k.time <= 0.25f).value, Is.EqualTo(clearWhenActive ? material : null));
        Assert.That(keys.Last(k => k.time <= 0.75f).value, Is.EqualTo(clearWhenActive ? null : material));
    }

    [Test]
    public void SyncLinksAnimationsOfARendererOnTheAvatarRoot()
    {
        var body = AddRenderer(root, "Smile");
        var accessory = AddRenderer(Child("Accessory"), "Smile");
        var controller = Controller("fx");
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        var state = controller.layers[0].stateMachine.AddState("Smile");
        state.motion = Clip("smile", Shape("", "Smile", 0, 100));

        var result = BlendShapeSync.Apply(root, BlendShapeSync.Plan(body, accessory), "Accessory");

        Assert.That(result.Links.Success, Is.True, result.Message);
        var output = (AnimationClip)state.motion;
        foreach (float time in new[] { 0f, 0.5f, 1f })
        {
            output.SampleAnimation(root, time);
            Assert.That(accessory.GetBlendShapeWeight(0), Is.EqualTo(body.GetBlendShapeWeight(0)).Within(0.0001f));
        }
    }

    // A build preview (My Avatar's face tracking test) plays an in-memory copy of an authored controller: the links apply to
    // the copy, as on an upload's VRCFury controller, and the authored one stays as it is.
    [Test]
    public void LinksApplyToAnInMemoryCopyOnTheDescriptor()
    {
        AddRenderer(Child("Body"), "Smile", "Fix");
        var authored = AnimatorController.CreateAnimatorControllerAtPath(folder + "/authored.controller");
        var smile = Clip("smile", Shape("Body", "Smile", 0, 100));
        authored.layers[0].stateMachine.AddState("Smile").motion = smile;
        var copy = Orbiters.Toolkit.Editor.Animations.AnimatorControllerCopy.Of(authored);
        try
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = copy.Controller } };

            var result = BlendShapeLinkEngine.Apply(root, new[] { FactorLink(BlendShapeLinkEndpoint.BlendShape, "Fix", null) }, "test");

            Assert.That(result.Success, Is.True, result.Message);
            var wrapper = copy.Controller.layers[0].stateMachine.states[0].state.motion as BlendTree;
            Assert.That(wrapper, Is.Not.Null, "The copy's smile is wrapped with the fix.");
            Assert.That(wrapper.blendParameter, Is.EqualTo("UP_Test_Factor"));
            var fixedClip = (AnimationClip)wrapper.children[1].motion;
            Assert.That(AnimationUtility.GetCurveBindings(fixedClip).Any(b => b.propertyName == "blendShape.Fix"), Is.True);
            Assert.That(AssetDatabase.Contains(fixedClip), Is.False, "Nothing made for the preview becomes an asset.");
            Assert.That(authored.layers[0].stateMachine.states[0].state.motion, Is.SameAs(smile), "The authored controller is untouched.");
            Assert.That(copy.Controller.layers[0].stateMachine.states[0].state, Is.Not.SameAs(authored.layers[0].stateMachine.states[0].state));
        }
        finally { copy.Destroy(); }
    }

    // Body/Smile goes from 0 to 100 over a second and triggers the effect through a factor link.
    private AnimationClip ApplyShapeTriggeredEffect(AnimationClip effect)
    {
        AddRenderer(Child("Body"), "Smile");
        var controller = Controller("fx");
        root.AddComponent<Animator>().runtimeAnimatorController = controller;
        var state = controller.layers[0].stateMachine.AddState("Smile");
        state.motion = Clip("smile", Shape("Body", "Smile", 0, 100));
        var result = BlendShapeLinkEngine.Apply(root, new[] { FactorLink(BlendShapeLinkEndpoint.Animation, effect.name, effect) }, "test");
        Assert.That(result.Success, Is.True, result.Message);
        return (AnimationClip)((BlendTree)state.motion).children[1].motion;
    }

    private static BlendShapeLink FactorLink(BlendShapeLinkEndpoint effectType, string effect, AnimationClip effectClip) => new BlendShapeLink
    {
        TargetRendererPath = "Body", TriggerType = BlendShapeLinkEndpoint.BlendShape, TriggerName = "Smile",
        EffectType = effectType, EffectName = effect, EffectClip = effectClip,
        SourcePath = "Body", SourceProperty = "blendShape.Smile",
        DestinationPath = effectType == BlendShapeLinkEndpoint.BlendShape ? "Body" : string.Empty,
        DestinationProperty = effectType == BlendShapeLinkEndpoint.BlendShape ? "blendShape." + effect : string.Empty,
        FactorParameter = "UP_Test_Factor"
    };

    private static (EditorCurveBinding, AnimationCurve) Shape(string path, string shape, float from, float to) =>
        (EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Linear(0, from, 1, to));

    private static (EditorCurveBinding, AnimationCurve) Pose(string path, float angle) =>
        (EditorCurveBinding.FloatCurve(path, typeof(Transform), "localEulerAnglesRaw.x"), AnimationCurve.Constant(0, 1, angle));

    private static (EditorCurveBinding, AnimationCurve) Curve(string path, Type type, string property, float value) =>
        (EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));

    // An asset in the test folder when named, else in memory.
    private AnimationClip Clip(string name, params (EditorCurveBinding binding, AnimationCurve curve)[] curves)
    {
        var clip = new AnimationClip { name = name ?? "Clip" };
        foreach (var (binding, curve) in curves) AnimationUtility.SetEditorCurve(clip, binding, curve);
        return name != null ? Asset(clip, name + ".anim") : clip;
    }

    private T Asset<T>(T value, string file) where T : Object
    {
        AssetDatabase.CreateAsset(value, folder + "/" + file);
        return value;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    private AnimatorController Controller(string name) =>
        AnimatorController.CreateAnimatorControllerAtPath(folder + "/com.vrcfury.temp/" + name + ".controller");

    private Transform Child(string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(root.transform, false);
        return child;
    }

    private SkinnedMeshRenderer AddRenderer(Component owner, params string[] shapes) => AddRenderer(owner.gameObject, shapes);

    private SkinnedMeshRenderer AddRenderer(GameObject owner, params string[] shapes)
    {
        var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } });
        foreach (string shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
        var renderer = owner.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }
}
