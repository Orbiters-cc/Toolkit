using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Fits kept for a refitted renderer (kept, used again, forgotten; the build's pick-one menu) and the body shrink a refit
/// records (the build's blendshape on the body that follows the clothing's toggle). Builds run on clones only.
/// </summary>
public sealed class RefitKeptFitsAndShrinkTests
{
    private Scene scene;
    private GameObject root;
    private SkinnedMeshRenderer body, jacket;
    private readonly List<Object> created = new List<Object>();
    private string folder;
    private int undoGroup;

    [SetUp]
    public void SetUp()
    {
        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Kept fits avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        body = AddRenderer("Body", Mesh("Body", "Flex"));
        jacket = AddRenderer("Jacket", Mesh("Jacket"));
        folder = null;
    }

    [TearDown]
    public void TearDown()
    {
        Undo.FlushUndoRecordObjects();
        Undo.RevertAllDownToGroup(undoGroup);
        Object.DestroyImmediate(root);
        foreach (var o in created) if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
        if (folder != null) AssetDatabase.DeleteAsset(folder);
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test]
    public void FitsAreKeptUsedAgainAndForgotten()
    {
        var loose = Fit("Loose fit", .01f);
        var record = Register(loose, "base:1");
        Assert.That(RefitRecords.CurrentFit(record), Is.Null, "Nothing is kept until asked.");
        var kept = RefitRecords.KeepFit(record, "Loose");
        Assert.That(kept.Mesh, Is.SameAs(loose));
        Assert.That(RefitRecords.CurrentFit(record), Is.SameAs(kept));

        // A new refit adds a fit instead of replacing the kept one.
        var tight = Fit("Tight fit", -.01f);
        jacket.sharedMesh = tight;
        Register(tight, "base:1");
        Assert.That(record.keptFits.Count, Is.EqualTo(1));
        Assert.That(RefitRecords.CurrentFit(record), Is.Null);

        Assert.That(RefitRecords.UseFit(record, kept), Is.True);
        Assert.That(jacket.sharedMesh, Is.SameAs(loose));
        Assert.That(record.mesh, Is.SameAs(loose));
        Assert.That(record.Applied, Is.True);
        Assert.That(record.keptFits.Select(f => f.Mesh), Is.EquivalentTo(new[] { loose, tight }), "The fit worn before is kept, not lost.");
        Assert.That(RefitRecords.KeepFit(record, "Roomy").name, Is.EqualTo("Roomy"), "Keeping the worn fit again renames it.");
        Assert.That(record.keptFits.Count, Is.EqualTo(2));
        Assert.That(RefitRecords.KeepFit(record, record.keptFits.First(f => f.Mesh == tight).name).name, Is.Not.EqualTo(
            record.keptFits.First(f => f.Mesh == tight).name), "Two fits never share a name.");

        RefitRecords.RemoveKeptFit(record, record.keptFits.First(f => f.Mesh == tight));
        Assert.That(record.keptFits.Select(f => f.Mesh), Is.EqualTo(new[] { loose }));
        Assert.That(jacket.sharedMesh, Is.SameAs(loose), "Forgetting a fit never changes what is worn.");

        // Fits kept for one custom base do not carry over to another.
        var other = Fit("Other base fit", 0f);
        jacket.sharedMesh = other;
        Register(other, "base:2");
        Assert.That(record.keptFits, Is.Empty);
    }

    [Test]
    public void ANewFitDropsTheBodyShrinkPlannedForTheOldOne()
    {
        var first = Fit("First", 0f);
        var record = Register(first, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 1 }, new[] { Vector3.back * .002f });
        Assert.That(record.HasBodyShrink, Is.True);
        Register(first, "base:1");
        Assert.That(record.HasBodyShrink, Is.True, "Registering the same fit again keeps it.");
        var second = Fit("Second", .005f);
        jacket.sharedMesh = second;
        Register(second, "base:1");
        Assert.That(record.HasBodyShrink, Is.False);
    }

    [Test]
    public void KeptFitsBecomeShapesAndAPickOneMenuOnTheBuildCopyOnly()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        var loose = Fit("Loose fit", .01f);
        var record = Register(loose, "base:1");
        RefitRecords.KeepFit(record, "Loose");
        var tight = Fit("Tight fit", -.01f);
        jacket.sharedMesh = tight;
        record = Register(tight, "base:1");
        RefitRecords.KeepFit(record, "Tight");

        Assert.That(new RefitKeptFitsHook().callbackOrder, Is.InRange(-10109, -10001), "After the refit capture, before VRCFury builds the menu.");
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        var warnings = new List<string>();
        Assert.That(RefitKeptFitsBuild.Apply(clone, warnings), Is.EqualTo(1), string.Join("; ", warnings));
        var copy = clone.transform.Find("Jacket").GetComponent<SkinnedMeshRenderer>();
        created.Add(copy.sharedMesh);
        Assert.That(copy.sharedMesh, Is.Not.SameAs(tight), "The build adds its shapes to a copy of the mesh.");
        Assert.That(copy.GetComponent<OrbitersRefit>().Applied, Is.True, "The build copy's record follows its mesh for the later steps.");
        int shape = copy.sharedMesh.GetBlendShapeIndex("ReFit fit: Loose");
        Assert.That(shape, Is.GreaterThanOrEqualTo(0));
        var deltas = new Vector3[copy.sharedMesh.vertexCount];
        copy.sharedMesh.GetBlendShapeFrameVertices(shape, 0, deltas, null, null);
        Assert.That(deltas[1].x, Is.EqualTo(.02f).Within(1e-6f), "The shape takes the worn fit to the kept one.");
        Assert.That(copy.sharedMesh.GetBlendShapeIndex("ReFit fit: Tight"), Is.LessThan(0), "The worn fit is the mesh itself.");
        Assert.That(copy.GetComponents(VrcFury.Component).Length, Is.EqualTo(2), "One toggle per fit.");
        Assert.That(tight.blendShapeCount, Is.Zero, "The project's mesh is untouched.");
        Assert.That(jacket.GetComponents(VrcFury.Component), Is.Empty, "The scene avatar is untouched.");
    }

    [Test]
    public void TheBodyShrinkFollowsTheClothingsToggleOnTheBuildCopy()
    {
        var fit = Fit("Fit", 0f);
        var record = Register(fit, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 0, 2 }, new[] { Vector3.back * .002f, Vector3.left * .003f });
        folder = "Assets/RefitShrinkTest-" + System.Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        AssetDatabase.CreateFolder(folder, "com.vrcfury.temp");
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/com.vrcfury.temp/fx.controller");
        clone.AddComponent<Animator>().runtimeAnimatorController = controller;
        var toggle = new AnimationClip { name = "Jacket off" };
        AssetDatabase.CreateAsset(toggle, folder + "/com.vrcfury.temp/off.anim");
        AnimationUtility.SetEditorCurve(toggle, EditorCurveBinding.FloatCurve("Jacket", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 0, 0));
        controller.layers[0].stateMachine.AddState("Off").motion = toggle;

        var warnings = new List<string>();
        Assert.That(RefitShrinkBuild.Apply(clone, warnings), Is.EqualTo(1), string.Join("; ", warnings));
        var copiedBody = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        created.Add(copiedBody.sharedMesh);
        string name = RefitShrinkBuild.ShapeName(jacket);
        int shape = copiedBody.sharedMesh.GetBlendShapeIndex(name);
        Assert.That(shape, Is.GreaterThanOrEqualTo(0));
        var deltas = new Vector3[copiedBody.sharedMesh.vertexCount];
        copiedBody.sharedMesh.GetBlendShapeFrameVertices(shape, 0, deltas, null, null);
        Assert.That(deltas, Is.EqualTo(new[] { Vector3.back * .002f, Vector3.zero, Vector3.left * .003f }));
        Assert.That(copiedBody.GetBlendShapeWeight(shape), Is.EqualTo(100), "On as the clothing starts shown.");
        var follow = AnimationUtility.GetEditorCurve(toggle, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + name));
        Assert.That(follow, Is.Not.Null, "The clothing's toggle sets the shrink too.");
        Assert.That(follow.Evaluate(0), Is.EqualTo(0), "Off while the clothing is off.");
        Assert.That(body.sharedMesh.GetBlendShapeIndex(name), Is.LessThan(0), "The scene body's mesh is untouched.");
    }

    private OrbitersRefit Register(Mesh fit, string baseKey) =>
        RefitRecords.Register(jacket, new RefitRendererState(), fit, null, body, new RefitShape[0], OrbitersRefit.FitKind.Fitted, baseKey, baseKey, "Test");

    // A fit of the jacket: its middle vertex moved along x.
    private Mesh Fit(string name, float offset)
    {
        var mesh = Mesh(name);
        var vertices = mesh.vertices;
        vertices[1] += Vector3.right * offset;
        mesh.vertices = vertices;
        jacket.sharedMesh = mesh;
        return mesh;
    }

    private Mesh Mesh(string name, params string[] shapes)
    {
        var mesh = RefitRecordsTests.MakeMesh(name, shapes);
        created.Add(mesh);
        return mesh;
    }

    private SkinnedMeshRenderer AddRenderer(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { go.transform };
        return renderer;
    }
}
