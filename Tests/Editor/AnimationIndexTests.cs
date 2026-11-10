using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Build;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

// The build's index of what its animations bind: filed by the path from the avatar's root, right after edits made through
// it or behind its back, and giving the same answers as reading every clip again.
public sealed class AnimationIndexTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;

    [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

    [TearDown] public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    private GameObject New(string name, Transform parent = null)
    {
        var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.hideFlags = HideFlags.None;
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    private static EditorCurveBinding Shape(string path, string shape) => EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape);

    private AnimatorController Controller(params AnimationClip[] clips)
    {
        var controller = Own(new AnimatorController());
        controller.AddLayer("Base");
        foreach (var clip in clips) controller.layers[0].stateMachine.AddState(clip.name).motion = clip;
        return controller;
    }

    private AnimationClip Clip(string name, params (EditorCurveBinding Binding, AnimationCurve Curve)[] curves)
    {
        var clip = Own(new AnimationClip { name = name });
        foreach (var (binding, curve) in curves) AnimationUtility.SetEditorCurve(clip, binding, curve);
        return clip;
    }

    // An avatar with an FX clip, a nested Animator's clip and a shared clip: skin "Skin" (not Body: no MMD morphs).
    private (GameObject Root, SkinnedMeshRenderer Skin, SkinnedMeshRenderer Ears, VRCAvatarDescriptor Descriptor) Avatar()
    {
        var root = New("Avatar");
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        var skin = New("Skin", root.transform).AddComponent<SkinnedMeshRenderer>();
        var prop = New("Prop", root.transform);
        var ears = New("Ears", prop.transform).AddComponent<SkinnedMeshRenderer>();
        foreach (var renderer in new[] { skin, ears })
        {
            var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } });
            foreach (var name in new[] { "a", "b", "held", "never", "unused" }) mesh.AddBlendShapeFrame(name, 100, new[] { Vector3.forward, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            renderer.sharedMesh = mesh;
            renderer.SetBlendShapeWeight(2, 40);
        }
        var fx = Clip("FX", (Shape("Skin", "a"), AnimationCurve.Linear(0, 0, 1, 100)), (Shape("Skin", "held"), AnimationCurve.Constant(0, 1, 40)),
            (Shape("Prop/Ears", "b"), AnimationCurve.Constant(0, 1, 100)), (EditorCurveBinding.FloatCurve("Prop", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1)));
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(fx) } };
        var wiggle = Clip("Wiggle", (Shape("Ears", "a"), AnimationCurve.Constant(0, 1, 100)), (Shape("Ears", "held"), AnimationCurve.Constant(0, 1, 40)));
        prop.AddComponent<Animator>().runtimeAnimatorController = Controller(wiggle);
        return (root, skin, ears, descriptor);
    }

    // The answer before the index: every clip of the avatar read again (BlendShapePruning.Used's animation part, 0.4.17).
    private static HashSet<string> ReadingEveryClip(GameObject avatarRoot, SkinnedMeshRenderer renderer, bool constantsUnused)
    {
        var mesh = renderer.sharedMesh;
        var used = new HashSet<string>();
        var changing = new HashSet<string>();
        foreach (var (clip, animatorRoot) in AvatarUsage.Clips(avatarRoot))
        {
            if (!renderer.transform.IsChildOf(animatorRoot)) continue;
            string relative = AnimationUtility.CalculateTransformPath(renderer.transform, animatorRoot);
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.path != relative || binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                string name = binding.propertyName.Substring("blendShape.".Length);
                used.Add(name);
                if (!constantsUnused || changing.Contains(name)) continue;
                int index = mesh.GetBlendShapeIndex(name);
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (index < 0 || curve == null || curve.keys.Any(k => Mathf.Abs(k.value - renderer.GetBlendShapeWeight(index)) > .01f)) changing.Add(name);
            }
        }
        if (constantsUnused) used.IntersectWith(changing);
        return used;
    }

    private static void SameAsReadingEveryClip(GameObject root, AvatarUsage usage, params SkinnedMeshRenderer[] renderers)
    {
        foreach (var renderer in renderers)
            foreach (bool constants in new[] { false, true })
            {
                var expected = ReadingEveryClip(root, renderer, constants);
                Assert.That(BlendShapePruning.Used(root, renderer, constants), Is.EquivalentTo(expected), renderer.name + (constants ? ", constants unused" : ""));
                Assert.That(BlendShapePruning.Used(usage, renderer, constants), Is.EquivalentTo(expected), renderer.name + " from the usage" + (constants ? ", constants unused" : ""));
            }
    }

    [Test] public void FilesEveryBindingByThePathFromTheRoot()
    {
        var (root, _, _, _) = Avatar();
        var index = AvatarUsage.AnimationsOf(root);
        Assert.That(index.At("Skin").Select(b => b.Value.propertyName), Is.EquivalentTo(new[] { "blendShape.a", "blendShape.held" }));
        var ears = index.At("Prop/Ears");
        Assert.That(ears.Select(b => b.Value.propertyName), Is.EquivalentTo(new[] { "blendShape.b", "blendShape.a", "blendShape.held" }), "the FX clip's and the prop's own Animator's");
        Assert.That(ears.Single(b => b.Value.path == "Ears" && b.Value.propertyName == "blendShape.a").AnimatorRoot.name, Is.EqualTo("Prop"));
        Assert.That(index.At("Prop").Single().Value.propertyName, Is.EqualTo("m_IsActive"));
        Assert.That(index.At("Nothing"), Is.Empty);
        var fx = index.Clips.First().Clip;
        Assert.That(index.Bindings(fx).TryFind("Skin", typeof(SkinnedMeshRenderer), "blendShape.held", out var held), Is.True);
        Assert.That(held.propertyName, Is.EqualTo("blendShape.held"));
        Assert.That(index.Bindings(fx).TryFind("Skin", typeof(MeshRenderer), "blendShape.held", out _), Is.False, "the component type counts");
    }

    [Test] public void RangesAreTheLowestAndHighestKeys()
    {
        var (root, _, _, _) = Avatar();
        var index = AvatarUsage.AnimationsOf(root);
        var a = index.At("Skin").Single(b => b.Value.propertyName == "blendShape.a");
        Assert.That(index.Bindings(a.Clip).Range(a.Index, out float min, out float max), Is.True);
        Assert.That((min, max), Is.EqualTo((0f, 100f)));
        var held = index.At("Skin").Single(b => b.Value.propertyName == "blendShape.held");
        Assert.That(index.Bindings(held.Clip).Range(held.Index, out min, out max), Is.True);
        Assert.That((min, max), Is.EqualTo((40f, 40f)));
    }

    [Test] public void UsedShapesAreTheSameAsReadingEveryClip()
    {
        var (root, skin, ears, _) = Avatar();
        SameAsReadingEveryClip(root, new AvatarUsage(root), skin, ears);
    }

    [Test] public void EditsThroughTheIndexAreSeenAtOnce()
    {
        var (root, skin, ears, _) = Avatar();
        var usage = new AvatarUsage(root);
        var index = usage.Animations;
        var fx = index.Clips.First().Clip;
        // A step moves the ears' "b" onto the skin, as a merge does.
        index.SetCurves(fx, new[] { Shape("Prop/Ears", "b"), Shape("Skin", "b") }, new[] { null, AnimationCurve.Constant(0, 1, 100) });
        Assert.That(index.At("Skin").Select(b => b.Value.propertyName), Has.Member("blendShape.b"));
        Assert.That(index.At("Prop/Ears").Select(b => b.Value.propertyName), Has.No.Member("blendShape.b"));
        SameAsReadingEveryClip(root, usage, skin, ears);
    }

    // Other tools' steps change clips with AnimationUtility: Unity raises the clip's dirty count, and the index reads it again.
    [Test] public void EditsBehindItsBackAreSeenOnSync()
    {
        var (root, skin, ears, descriptor) = Avatar();
        var usage = new AvatarUsage(root);
        var fx = usage.Animations.Clips.First().Clip;
        int dirty = EditorUtility.GetDirtyCount(fx);
        AnimationUtility.SetEditorCurve(fx, Shape("Skin", "unused"), AnimationCurve.Constant(0, 1, 100));
        Assert.That(EditorUtility.GetDirtyCount(fx), Is.Not.EqualTo(dirty), "Unity marks a clip changed by a curve edit (the index relies on it)");
        SameAsReadingEveryClip(root, usage, skin, ears);
        Assert.That(BlendShapePruning.Used(usage, skin), Has.Member("unused"));

        // A step plays other clips (a copied controller): the clip list is read again too.
        var copy = Clip("FX copy", (Shape("Skin", "never"), AnimationCurve.Constant(0, 1, 100)));
        var layers = descriptor.baseAnimationLayers;
        layers[0].animatorController = Controller(copy);
        descriptor.baseAnimationLayers = layers;
        SameAsReadingEveryClip(root, usage, skin, ears);
        Assert.That(BlendShapePruning.Used(usage, skin), Is.EquivalentTo(new[] { "never" }));
    }

    [Test] public void DuringABuildUsedReadsTheBuildsIndex()
    {
        var (root, skin, ears, _) = Avatar();
        var context = BuildContext.For(root);
        try
        {
            var index = context.Clips;
            Assert.That(context.Clips, Is.SameAs(index), "one index per build");
            SameAsReadingEveryClip(root, new AvatarUsage(root), skin, ears);
            context.KeepShapes(new[] { "unused" });
            Assert.That(BlendShapePruning.Used(root, skin), Has.Member("unused"), "shapes the caller keeps for this build");
        }
        finally { BuildContext.End(root); }
        Assert.That(BlendShapePruning.Used(root, skin), Has.No.Member("unused"), "forgotten with the build");
    }
}
