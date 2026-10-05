using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

public sealed class AttachmentAnimationBuildTests
{
    private Scene scene;
    private GameObject avatar;
    private Transform bone, accessory, follower;
    private AnimatorController source;
    private AnimationClip clip;
    private AvatarMask mask;
    private string folder;
    private readonly List<GameObject> builds = new List<GameObject>();

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Source avatar");
        SceneManager.MoveGameObjectToScene(avatar, scene);
        var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
        bone = Child(avatar.transform, "Bone");
        bone.localPosition = new Vector3(1, 2, 3);
        bone.localRotation = Quaternion.Euler(0, 30, 0);
        accessory = Child(avatar.transform, "Coat");
        accessory.localPosition = new Vector3(.2f, .5f, .8f);
        follower = Child(accessory, "Sleeve");
        follower.localPosition = new Vector3(.1f, .2f, .3f);
        var attachment = accessory.gameObject.AddComponent<OrbitersAttachment>();
        attachment.mode = OrbitersAttachment.AttachMode.Merge;
        attachment.syncBlendShapes = false;
        attachment.links.Add(new OrbitersAttachment.BoneLink { from = follower, to = bone });
        folder = "Assets/OrbitersAttachmentTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        source = AnimatorController.CreateAnimatorControllerAtPath(folder + "/source.controller");
        clip = new AnimationClip { name = "Source animation" };
        AssetDatabase.CreateAsset(clip, folder + "/source.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Coat/Sleeve", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, .1f, 1, .9f));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
        source.layers[0].stateMachine.AddState("Play").motion = clip;
        mask = new AvatarMask { transformCount = 2 };
        mask.SetTransformPath(0, "Coat"); mask.SetTransformActive(0, true);
        mask.SetTransformPath(1, "Coat/Sleeve"); mask.SetTransformActive(1, true);
        AssetDatabase.CreateAsset(mask, folder + "/source.mask");
        var layers = source.layers; layers[0].avatarMask = mask; source.layers = layers;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = source, mask = mask, isDefault = false } };
        descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
        AssetDatabase.SaveAssets();
    }

    [TearDown] public void TearDown()
    {
        foreach (var build in builds) AttachmentAnimationBuild.Release(build);
        builds.Clear();
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
    }

    private static Transform Child(Transform parent, string name)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); return t;
    }
    private GameObject Build()
    {
        var copy = Object.Instantiate(avatar);
        SceneManager.MoveGameObjectToScene(copy, scene);
        builds.Add(copy);
        AttachmentBuild.Apply(copy);
        return copy;
    }
    private static AnimatorController Controller(GameObject copy) => (AnimatorController)copy.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers[0].animatorController;
    private static Transform Sleeve(GameObject copy) => copy.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Sleeve");
    private static AnimationClip Clip(GameObject copy) => Controller(copy).animationClips.Single();

    [Test] public void MovesOnlyTheBuildCopyWithoutAddingConstraintsAndKeepsScaleFollowing()
    {
        var before = follower.position;
        var copy = Build(); var moved = Sleeve(copy);
        Assert.AreEqual(0, copy.GetComponentsInChildren<VRCParentConstraint>(true).Length);
        Assert.True(moved.IsChildOf(copy.transform.Find("Bone")));
        Assert.AreEqual(accessory, follower.parent);
        Assert.Less(Vector3.Distance(before, moved.position), .0001f);
        var oldScale = moved.lossyScale;
        copy.transform.Find("Bone").localScale = Vector3.one * 2;
        Assert.Less(Vector3.Distance(oldScale * 2, moved.lossyScale), .0001f);
        Assert.AreEqual(Vector3.one, bone.localScale);
    }

    [Test] public void LocalMotionAndAncestorTogglePlayAfterReparenting()
    {
        var copy = Build(); var moved = Sleeve(copy);
        var oldPath = "Coat/Sleeve";
        var newPath = AnimationUtility.CalculateTransformPath(moved, copy.transform);
        var bindings = AnimationUtility.GetCurveBindings(Clip(copy));
        Assert.True(bindings.Any(b => b.path == newPath && b.propertyName == "m_LocalPosition.x"));
        Assert.False(bindings.Any(b => b.path == oldPath));
        Clip(copy).SampleAnimation(copy, .5f);
        Assert.AreEqual(.5f, moved.localPosition.x, .0001f);
        Assert.False(moved.gameObject.activeInHierarchy, "ancestor toggle continues to hide the detached branch");
        Assert.True(follower.gameObject.activeInHierarchy, "source is unchanged");
    }

    [Test] public void BuildGraphsAndMasksArePrivateAndTheSourceFilesRemainIdentical()
    {
        var before = System.IO.File.ReadAllBytes(folder + "/source.controller");
        var beforeClip = System.IO.File.ReadAllBytes(folder + "/source.anim");
        var first = Build(); var second = Build();
        Assert.AreNotSame(source, Controller(first));
        Assert.AreNotSame(Controller(first), Controller(second));
        Assert.AreNotSame(source.layers[0].stateMachine, Controller(first).layers[0].stateMachine);
        Assert.AreNotSame(Clip(first), Clip(second));
        Assert.True(AttachmentAnimationBuild.Owns(Controller(first)));
        Assert.Contains(Controller(first), BlendShapeLinkEngine.CollectBuiltControllers(first));
        var copiedMask = Controller(first).layers[0].avatarMask;
        Assert.AreNotSame(mask, copiedMask);
        Assert.AreEqual(AnimationUtility.CalculateTransformPath(Sleeve(first), first.transform), copiedMask.GetTransformPath(1));
        AssetDatabase.SaveAssets();
        CollectionAssert.AreEqual(before, System.IO.File.ReadAllBytes(folder + "/source.controller"));
        CollectionAssert.AreEqual(beforeClip, System.IO.File.ReadAllBytes(folder + "/source.anim"));
        Assert.AreEqual("Coat/Sleeve", mask.GetTransformPath(1));
    }

    [Test] public void ObjectReferenceCurvesKeepNullKeysAndPointAtTheMovedRenderer()
    {
        follower.gameObject.AddComponent<MeshRenderer>();
        var binding = EditorCurveBinding.PPtrCurve("Coat/Sleeve", typeof(MeshRenderer), "m_Materials.Array.data[0]");
        AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = null }, new ObjectReferenceKeyframe { time = 1, value = null } });
        var copy = Build();
        var actual = AnimationUtility.GetObjectReferenceCurveBindings(Clip(copy)).Single();
        Assert.AreEqual(AnimationUtility.CalculateTransformPath(Sleeve(copy), copy.transform), actual.path);
        Assert.AreEqual(2, AnimationUtility.GetObjectReferenceCurve(Clip(copy), actual).Length);
        Assert.AreEqual("Coat/Sleeve", AnimationUtility.GetObjectReferenceCurveBindings(clip).Single().path);
    }

    [Test] public void OverrideControllersUseTheirEffectiveClipAndCanBeLinked()
    {
        var replacement = Object.Instantiate(clip); replacement.name = "Replacement";
        AssetDatabase.CreateAsset(replacement, folder + "/override.anim");
        AnimationUtility.SetEditorCurve(replacement, EditorCurveBinding.FloatCurve("Coat/Sleeve", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, .75f));
        var overrides = new AnimatorOverrideController(source);
        overrides[clip] = replacement;
        AssetDatabase.CreateAsset(overrides, folder + "/override.overrideController");
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        var layers = descriptor.baseAnimationLayers; layers[0].animatorController = overrides; descriptor.baseAnimationLayers = layers;
        var copy = Build();
        Clip(copy).SampleAnimation(copy, .5f);
        Assert.AreEqual(.75f, Sleeve(copy).localPosition.x, .0001f);
        Assert.True(AttachmentAnimationBuild.Owns(Controller(copy)));
        Assert.AreSame(replacement, overrides[clip]);
    }

    [Test] public void NestedBlendTreeAndTransitionsAreClonedWithoutSharingMutableGraphObjects()
    {
        var state = source.layers[0].stateMachine.states.Single().state;
        var tree = new BlendTree { name = "Outer", blendParameter = "Blend", children = new[] { new ChildMotion { motion = clip, timeScale = 1 } } };
        AssetDatabase.AddObjectToAsset(tree, source); state.motion = tree;
        var next = source.layers[0].stateMachine.AddState("Next"); next.motion = clip;
        state.AddTransition(next).hasExitTime = true;
        var copy = Build();
        var states = Controller(copy).layers[0].stateMachine.states.Select(s => s.state).ToArray();
        var cloned = states.Single(s => s.name == "Play");
        Assert.AreNotSame(tree, cloned.motion);
        Assert.AreSame(states.Single(s => s.name == "Next"), cloned.transitions[0].destinationState);
        Assert.AreNotSame(state.transitions[0], cloned.transitions[0]);
        Assert.AreNotSame(clip, ((BlendTree)cloned.motion).children[0].motion);
    }

    [Test] public void InitiallyHiddenAncestorRemainsHiddenWithoutAnAnimationCurve()
    {
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive"), null);
        accessory.gameObject.SetActive(false);
        var copy = Build();
        Assert.False(Sleeve(copy).gameObject.activeInHierarchy);
    }

    [Test] public void RewrittenGraphSurvivesPrefabSaveAndReload()
    {
        var copy = Build();
        var expected = AnimationUtility.CalculateTransformPath(Sleeve(copy), copy.transform);
        AttachmentBuild.Finish(copy);
        var path = folder + "/built.prefab";
        PrefabUtility.SaveAsPrefabAsset(copy, path);
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(Controller(copy)), ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        var loaded = PrefabUtility.LoadPrefabContents(path);
        try
        {
            Assert.AreEqual(expected, AnimationUtility.CalculateTransformPath(Sleeve(loaded), loaded.transform));
            Assert.True(AnimationUtility.GetCurveBindings(Clip(loaded)).Any(b => b.path == expected && b.propertyName == "m_LocalPosition.x"));
            Assert.False(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(Clip(loaded))));
        }
        finally { PrefabUtility.UnloadPrefabContents(loaded); }
    }

    [Test] public void MovedClothingReceivesAnimatedBodyBlendshapesWithoutVrcFury()
    {
        var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, new Vector3[3], new Vector3[3]);
        AssetDatabase.CreateAsset(mesh, folder + "/mesh.asset");
        var body = Child(avatar.transform, "Body").gameObject.AddComponent<SkinnedMeshRenderer>(); body.sharedMesh = mesh;
        var clothing = follower.gameObject.AddComponent<SkinnedMeshRenderer>(); clothing.sharedMesh = mesh;
        var attachment = accessory.GetComponent<OrbitersAttachment>(); attachment.body = body; attachment.syncBlendShapes = true;
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Linear(0, 0, 1, 100));
        var copy = Build();
        AttachmentBuild.Finish(copy);
        var moved = Sleeve(copy).GetComponent<SkinnedMeshRenderer>();
        var movedPath = AnimationUtility.CalculateTransformPath(moved.transform, copy.transform);
        var output = Controller(copy).animationClips.Single(c => AnimationUtility.GetCurveBindings(c).Any(b => b.path == movedPath && b.propertyName == "blendShape.Smile"));
        foreach (float time in new[] { 0f, .25f, .5f, .75f, 1f })
        {
            output.SampleAnimation(copy, time);
            Assert.AreEqual(time * 100, moved.GetBlendShapeWeight(0), .0001f);
        }
        Assert.False(AnimationUtility.GetCurveBindings(clip).Any(b => b.path == "Coat/Sleeve" && b.propertyName == "blendShape.Smile"));
        Assert.IsEmpty(copy.GetComponentsInChildren<VRCParentConstraint>(true));
    }

    [Test] public void AnimatedFormerParentScaleKeepsAffectingTheMovedBranch()
    {
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive"), null);
        foreach (var axis in new[] { "x", "y", "z" })
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Coat", typeof(Transform), "m_LocalScale." + axis), AnimationCurve.Linear(0, 1, 1, 2));
        var copy = Build(); var moved = Sleeve(copy);
        Clip(copy).SampleAnimation(copy, .5f);
        Assert.Less(Vector3.Distance(Vector3.one * 1.5f, moved.lossyScale), .0001f);
        Assert.AreEqual(Vector3.one, follower.lossyScale);
        Assert.AreEqual(0, copy.GetComponentsInChildren<VRCParentConstraint>(true).Length);
    }

    [Test] public void ActualAnimatorMaskKeepsAncestorToggleOnTheMovedBranch()
    {
        var copy = Build();
        var animator = copy.AddComponent<Animator>();
        animator.runtimeAnimatorController = Controller(copy);
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind(); animator.Update(0); animator.Update(.5f);
        Assert.False(Sleeve(copy).gameObject.activeInHierarchy, "Evaluate the real Animator with its mask, not just SampleAnimation.");
    }

    // A tool moved a bone before the build (MCB applying a version's skeleton): clips written for the original hierarchy
    // keep animating it and its children, and the authored clip keeps its path.
    [Test] public void AnimationsOfABoneMovedBeforeTheBuildFollowItsFormerPath()
    {
        Child(bone, "Tip");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Old/Bone/Tip", typeof(Transform), "m_LocalPosition.y"), AnimationCurve.Constant(0, 1, .25f));
        var copy = Object.Instantiate(avatar);
        SceneManager.MoveGameObjectToScene(copy, scene);
        builds.Add(copy);
        AttachmentAnimationBuild.Prepare(copy).Moved(new[] { (copy.transform.Find("Bone"), "Old/Bone") });
        var bindings = AnimationUtility.GetCurveBindings(Clip(copy));
        Assert.True(bindings.Any(b => b.path == "Bone/Tip" && b.propertyName == "m_LocalPosition.y"));
        Assert.False(bindings.Any(b => b.path.StartsWith("Old/", StringComparison.Ordinal)));
        Assert.True(bindings.Any(b => b.path == "Coat/Sleeve"), "paths that exist are left alone");
        Assert.True(AnimationUtility.GetCurveBindings(clip).Any(b => b.path == "Old/Bone/Tip"), "the authored clip is unchanged");
    }

    [Test] public void TargetPhysBoneDoesNotAcquireTheNewAttachmentBranch()
    {
        var physics = bone.gameObject.AddComponent<VRCPhysBone>();
        physics.rootTransform = bone;
        var copy = Build(); var copiedBone = copy.transform.Find("Bone");
        var copiedPhysics = copiedBone.GetComponent<VRCPhysBone>();
        Assert.True(copiedPhysics.ignoreTransforms.Any(t => Sleeve(copy).IsChildOf(t)));
        Assert.IsEmpty(physics.ignoreTransforms);
    }
}
