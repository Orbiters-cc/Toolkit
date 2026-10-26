using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

/// <summary>What Orbiters Toolkit does around VRCFury's build: its controllers' own animations, its Armature Links' targets.</summary>
public sealed class VrcFuryBuildTests
{
    private Scene scene;
    private GameObject root;
    private string folder;
    private TestUndoSandbox sandbox;

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("VRCFury build avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        folder = "Assets/VrcFuryBuildTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
    }

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        sandbox.End();
    }

    [Test]
    public void VrcFuryControllersGetTheirOwnCopiesOfTheProjectsClipsAndTrees()
    {
        // As VRCFury saves it: its own controller, playing the project's tree (and clips) it left unchanged.
        var smile = Asset(Clip("Smile"), "smile.anim");
        var frown = Asset(Clip("Frown"), "frown.anim");
        var tree = new BlendTree { name = "Mood", blendType = BlendTreeType.Simple1D, blendParameter = "Mood", useAutomaticThresholds = false };
        Asset(tree, "mood.asset");
        tree.AddChild(smile, 0);
        tree.AddChild(frown, 1);
        var fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/com.vrcfury.temp/fx.controller");
        var machine = fx.layers[0].stateMachine;
        machine.AddState("Mood").motion = tree;
        machine.AddState("Smile again").motion = smile;
        var own = Clip("Own");
        AssetDatabase.AddObjectToAsset(own, fx);
        machine.AddState("Own").motion = own;
        Descriptor(fx);

        int copied = VrcFuryBuiltAnimations.Own(root);

        Assert.That(copied, Is.EqualTo(3), "the tree and its two clips, each once");
        var states = machine.states.ToDictionary(s => s.state.name, s => s.state);
        var treeCopy = (BlendTree)states["Mood"].motion;
        Assert.That(treeCopy, Is.Not.SameAs(tree));
        Assert.That(AssetDatabase.GetAssetPath(treeCopy), Is.EqualTo(AssetDatabase.GetAssetPath(fx)), "the copy lives in VRCFury's controller");
        var smileCopy = (AnimationClip)treeCopy.children[0].motion;
        Assert.That(smileCopy, Is.Not.SameAs(smile));
        Assert.That(smileCopy.name, Is.EqualTo(smile.name));
        Assert.That(AssetDatabase.GetAssetPath(smileCopy), Is.EqualTo(AssetDatabase.GetAssetPath(fx)));
        Assert.That(states["Smile again"].motion, Is.SameAs(smileCopy), "a clip played twice is copied once");
        Assert.That(states["Own"].motion, Is.SameAs(own), "what VRCFury made stays as it is");
        Assert.That(tree.children.Select(c => c.motion), Is.EqualTo(new Motion[] { smile, frown }), "the project's tree is untouched");
        Assert.That(VrcFuryBuiltAnimations.Own(root), Is.Zero, "nothing of the project is left to copy");
    }

    [Test]
    public void ControllersOutsideVrcFurysBuildsAreLeftAlone()
    {
        var clip = Asset(Clip("Wave"), "wave.anim");
        var authored = AnimatorController.CreateAnimatorControllerAtPath(folder + "/authored.controller");
        authored.layers[0].stateMachine.AddState("Wave").motion = clip;
        Descriptor(authored);

        Assert.That(VrcFuryBuiltAnimations.Own(root), Is.Zero);
        Assert.That(authored.layers[0].stateMachine.states[0].state.motion, Is.SameAs(clip));
    }

    [Test]
    public void ALinkToAMissingHumanoidBoneFallsBackToItsParentsAsVrcFuryDoes()
    {
        var hips = Child("Hips", root.transform);
        var chest = Child("Chest", Child("Spine", hips));
        var bones = new Dictionary<HumanBodyBones, Transform> { [HumanBodyBones.Hips] = hips, [HumanBodyBones.Chest] = chest };
        var link = new VrcFury.Link();
        link.Targets.Add(new VrcFury.Link.Target { UseBone = true, Bone = HumanBodyBones.UpperChest });

        Assert.That(link.Resolve(root.transform, b => bones.TryGetValue(b, out var t) ? t : null), Is.SameAs(chest));
    }

    [Test]
    public void AMissingFingerFallsBackToItsOwnJointsThenItsNeighboursThenTheHand()
    {
        var fallbacks = VrcFury.Link.FallbackBones(HumanBodyBones.LeftIndexDistal).ToList();

        Assert.That(fallbacks.Take(5), Is.EqualTo(new[]
        {
            HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexProximal,
            HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleProximal,
        }));
        // Its two other joints, then three joints of each of the four other fingers.
        int hand = fallbacks.IndexOf(HumanBodyBones.LeftHand);
        Assert.That(hand, Is.EqualTo(2 + 4 * 3));
        Assert.That(fallbacks.Skip(hand + 1), Is.EqualTo(new[]
        {
            HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftShoulder, HumanBodyBones.UpperChest,
            HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips,
        }), "then up the arm and the body");
    }

    [Test]
    public void LinkOffsetsStartFromTheAvatarsRootAndNeverLeaveIt()
    {
        var hips = Child("Hips", Child("Armature", root.transform));
        var tail = Child("Tail", hips);
        var link = new VrcFury.Link();
        link.Targets.Add(new VrcFury.Link.Target { UseObject = true, Object = hips.gameObject, Offset = "/Armature/Hips/Tail" });
        var above = new VrcFury.Link();
        above.Targets.Add(new VrcFury.Link.Target { Offset = ".." });

        Assert.That(link.Resolve(root.transform, b => null), Is.SameAs(tail));
        Assert.That(above.Resolve(root.transform, b => null), Is.Null);
    }

    private void Descriptor(AnimatorController fx)
    {
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
    }

    private static AnimationClip Clip(string name)
    {
        var clip = new AnimationClip { name = name };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + name), AnimationCurve.Constant(0, 1, 100));
        return clip;
    }

    private T Asset<T>(T value, string file) where T : Object
    {
        AssetDatabase.CreateAsset(value, folder + "/" + file);
        return value;
    }

    private static Transform Child(string name, Transform parent)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }
}
