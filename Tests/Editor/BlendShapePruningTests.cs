using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

public sealed class BlendShapePruningTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void KeepsShapesTheAvatarUsesAndBakesTheRestAtTheirWeight()
    {
        var root = Own(new GameObject("Avatar"));
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
        body.transform.SetParent(root.transform, false);
        var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } });
        mesh.RecalculateNormals();
        // A two-frame shape at 75 is halfway between its 50 and 100 frames.
        foreach (var name in new[] { "animated", "viseme aa", "blink", "ｳｨﾝｸ２右", "sculpt", "two frames", "unused" })
        {
            var offset = name == "two frames" ? Vector3.up : Vector3.forward;
            if (name == "two frames") mesh.AddBlendShapeFrame(name, 50, new[] { offset, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            mesh.AddBlendShapeFrame(name, 100, new[] { name == "two frames" ? offset * 3 : offset, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
        }
        body.sharedMesh = mesh;
        int Index(string name) => mesh.GetBlendShapeIndex(name);
        body.SetBlendShapeWeight(Index("animated"), 20);
        body.SetBlendShapeWeight(Index("sculpt"), 50);
        body.SetBlendShapeWeight(Index("two frames"), 75);

        var clip = Own(new AnimationClip());
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.animated"), AnimationCurve.Constant(0, 1, 100));
        var controller = Own(new AnimatorController()); controller.AddLayer("Base");
        controller.layers[0].stateMachine.AddState("On").motion = clip;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller } };
        descriptor.lipSync = VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape;
        descriptor.VisemeSkinnedMesh = body;
        descriptor.VisemeBlendShapes = new[] { "viseme aa" };
        descriptor.enableEyeLook = true;
        var eyes = descriptor.customEyeLookSettings;
        eyes.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
        eyes.eyelidsSkinnedMesh = body;
        eyes.eyelidsBlendshapes = new[] { Index("blink"), -1, -1 };
        descriptor.customEyeLookSettings = eyes;

        Assert.That(BlendShapePruning.Used(root, body), Is.EquivalentTo(new[] { "animated", "viseme aa", "blink", "ｳｨﾝｸ２右" }),
            "Animated, visemes, eyelids and a standard MMD morph (half-width) on Body.");
        var copy = Own(BlendShapePruning.Prune(root, body));

        Assert.That(body.sharedMesh, Is.SameAs(copy));
        Assert.That(Enumerable.Range(0, copy.blendShapeCount).Select(copy.GetBlendShapeName), Is.EqualTo(new[] { "animated", "viseme aa", "blink", "ｳｨﾝｸ２右" }));
        Assert.That(body.GetBlendShapeWeight(0), Is.EqualTo(20), "Kept shapes keep their weight.");
        Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes, Is.EqualTo(new[] { 2, -1, -1 }), "Eyelid indices follow the kept shapes.");
        Assert.That(copy.vertices[0].z, Is.EqualTo(.5f).Within(1e-5), "A removed shape at 50 is baked at half its offset.");
        Assert.That(copy.vertices[0].y, Is.EqualTo(2f).Within(1e-5), "Between two frames, the offset interpolates (75: halfway from 1 to 3).");
        Assert.That(mesh.blendShapeCount, Is.EqualTo(7), "The source mesh is never changed.");
        Assert.That(BlendShapePruning.Prune(root, body), Is.Null, "Nothing left to remove.");
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
