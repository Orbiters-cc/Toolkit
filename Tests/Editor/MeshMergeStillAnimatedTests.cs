using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

// Meshes always shown that animations set material properties on (MeshMerge.MergeStill): one whose every animation sets
// its materials exactly as another's do joins that one (MasculineCanine's tail and mohawk, both "Shininess 0"); one that
// another's animations would set differently stays apart.
public sealed class MeshMergeStillAnimatedTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private AnimatorController fx;
    private Transform[] bones;
    private SkinnedMeshRenderer body, hair, tail;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = New("Avatar");
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        fx = Own(new AnimatorController());
        fx.AddLayer("Base");
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        bones = new Transform[3];
        for (int i = 0; i < 3; i++)
        {
            bones[i] = New("Bone" + i).transform;
            bones[i].SetParent(i == 0 ? root.transform : bones[i - 1], false);
            bones[i].localPosition = i == 0 ? Vector3.zero : Vector3.up;
        }
        body = Skinned("Body", 0, 1);
        hair = Skinned("Hair", 2);
        tail = Skinned("Tail", 0);
        // The body is the viseme mesh (something points at it) and its own animation darkens it.
        descriptor.VisemeSkinnedMesh = body;
        // "Shininess 0" sets the hair's and the tail's gloss alike; the hair also has an animated shape.
        Clip("Shininess 0", ("Hair", "material._Glossiness", 0f), ("Tail", "material._Glossiness", 0f));
        Clip("Dark body", ("Body", "material._Metallic", 1f));
        Clip("Hair shape", ("Hair", "blendShape.Spiky", 100f));
    }

    [TearDown] public void TearDown()
    {
        AttachmentAnimationBuild.Release(root);
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void AMeshAnimatedAsAnotherJoinsIt()
    {
        Pose();
        var expected = World(hair).Concat(World(tail)).ToList();
        var tailMaterial = tail.sharedMaterial;
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeStill(new AvatarUsage(root), () => build);
        owned.AddRange(result.Meshes);

        Assert.That(result.Merged, Is.EqualTo(1));
        Assert.That(tail == null, "The tail joined.");
        Assert.That(body != null && hair != null);
        Assert.That(hair.sharedMaterials, Has.Member(tailMaterial), "The tail joined the hair, animated as it was.");
        Assert.That(body.sharedMaterials, Has.No.Member(tailMaterial), "The body's own animation would have set the tail's metal.");
        AssertSame(expected, World(hair));
        var shine = build.Clips.Select(c => c.Clip).Single(c => c.name == "Shininess 0");
        var bindings = AnimationUtility.GetCurveBindings(shine);
        Assert.That(bindings.Any(b => b.path == "Hair" && b.propertyName == "material._Glossiness"), "The hair's curve sets both materials now.");
        Assert.That(bindings.Any(b => b.path == "Tail"), Is.False, "The tail's copy left with its renderer.");
        Assert.That(BindingAudit.Of(build.Clips).Missing, Is.Empty);
        Assert.That(AnimationUtility.GetCurveBindings(Own("Shininess 0")).Any(b => b.path == "Tail"), "The avatar's own clip is untouched.");
    }

    // MasculineCanine's case: the body draws otherwise (or can't merge), so the tail (larger) and the mohawk are alone in
    // their group; the tail must join the mohawk, which can't join anything (its shape is animated), not take it in.
    [Test] public void ALargerMeshJoinsTheOneOnlyItCanFollow()
    {
        body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var larger = Skinned("Tail2", 0, 1, 2);
        AnimationUtility.SetEditorCurve(Own("Shininess 0"), EditorCurveBinding.FloatCurve("Tail2", typeof(SkinnedMeshRenderer), "material._Glossiness"), AnimationCurve.Constant(0, 1, 0));
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeStill(new AvatarUsage(root), () => build);
        owned.AddRange(result.Meshes);
        Assert.That(hair != null, "The hair stays: it takes the others in.");
        Assert.That(larger == null && tail == null, "Both tails joined the hair.");
        Assert.That(result.Merged, Is.EqualTo(2));
        Assert.That(BindingAudit.Of(build.Clips).Missing, Is.Empty);
    }

    [Test] public void AMeshAnimatedOtherwiseStaysApart()
    {
        // The tail's gloss goes to 0.2 where the hair's goes to 0: not the same animation.
        var shine = Own("Shininess 0");
        AnimationUtility.SetEditorCurve(shine, EditorCurveBinding.FloatCurve("Tail", typeof(SkinnedMeshRenderer), "material._Glossiness"), AnimationCurve.Constant(0, 1, .2f));
        var result = MeshMerge.MergeStill(new AvatarUsage(root), () => AttachmentAnimationBuild.Prepare(root));
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(0));
        Assert.That(tail != null);
    }

    [Test] public void AnEstimateMergesTheSameAndLeavesTheAnimationsAlone()
    {
        var result = MeshMerge.MergeStill(new AvatarUsage(root));
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(1));
        Assert.That(tail == null);
        Assert.That(AnimationUtility.GetCurveBindings(Own("Shininess 0")).Any(b => b.path == "Tail"));
    }

    // ---- Helpers ----

    private T Own<T>(T o) where T : Object { owned.Add(o); return o; }
    private AnimationClip Own(string clip) => owned.OfType<AnimationClip>().First(c => c.name == clip);

    private GameObject New(string name)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, scene);
        if (root != null) go.transform.SetParent(root.transform, false);
        return go;
    }

    private void Clip(string name, params (string Path, string Property, float Value)[] curves)
    {
        var clip = Own(new AnimationClip { name = name });
        foreach (var (path, property, value) in curves)
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), property), AnimationCurve.Constant(0, 1, value));
        fx.layers[0].stateMachine.AddState(name).motion = clip;
    }

    // A quad on each listed bone, weighted to it, with a "Spiky" shape on the hair.
    private SkinnedMeshRenderer Skinned(string name, params int[] on)
    {
        var renderer = New(name).AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(new Mesh { name = name });
        var vertices = new List<Vector3>(); var weights = new List<BoneWeight>(); var indices = new List<int>();
        foreach (int bone in on)
        {
            int first = vertices.Count;
            float z = name.Length * .01f;
            vertices.AddRange(new[] { new Vector3(0, bone, z), new Vector3(1, bone, z), new Vector3(0, bone + .5f, z), new Vector3(1, bone + .5f, z) });
            for (int c = 0; c < 4; c++) weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            indices.AddRange(new[] { first, first + 2, first + 1, first + 1, first + 2, first + 3 });
        }
        mesh.SetVertices(vertices); mesh.boneWeights = weights.ToArray(); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals();
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray();
        if (name == "Hair") mesh.AddBlendShapeFrame("Spiky", 100, vertices.Select(_ => Vector3.forward * .1f).ToArray(), null, null);
        renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0];
        renderer.sharedMaterial = Own(new Material(Shader.Find("Standard")) { name = name });
        return renderer;
    }

    private void Pose()
    {
        bones[1].localRotation = Quaternion.Euler(0, 0, 30);
        bones[2].localRotation = Quaternion.Euler(20, 0, 0);
    }

    private List<Vector3> World(SkinnedMeshRenderer renderer)
    {
        var baked = Own(new Mesh());
        renderer.BakeMesh(baked, true);
        return baked.vertices.Select(renderer.transform.TransformPoint).ToList();
    }

    private static void AssertSame(List<Vector3> expected, List<Vector3> actual)
    {
        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        foreach (var e in expected) Assert.That(actual.Min(a => Vector3.Distance(a, e)), Is.LessThan(1e-4f), "A vertex moved: " + e);
    }
}
