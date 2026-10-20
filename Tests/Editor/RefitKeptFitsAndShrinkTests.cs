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
/// records (the build's blendshape on the body that follows the clothing's toggle). Builds run on clones only. Fits are made
/// as the refit engines make them: the original mesh with the fit in its "refit" blendshape, worn at 100.
/// </summary>
public sealed class RefitKeptFitsAndShrinkTests
{
    private Scene scene;
    private GameObject root;
    private SkinnedMeshRenderer body, jacket;
    private Mesh jacketOriginal;
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
        jacketOriginal = Mesh("Jacket");
        jacket = AddRenderer("Jacket", jacketOriginal);
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
    public void UsingAnotherKeptFitDropsTheBodyShrinkPlannedUnderTheWornOne()
    {
        var loose = Fit("Loose fit", .01f);
        var record = Register(loose, "base:1");
        var kept = RefitRecords.KeepFit(record, "Loose");
        var tight = Fit("Tight fit", -.01f);
        record = Register(tight, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 1 }, new[] { Vector3.back * .002f });
        Assert.That(RefitRecords.UseFit(record, kept), Is.True);
        Assert.That(record.HasBodyShrink, Is.False, "The shrink was planned under the tight fit.");
    }

    [Test]
    public void TheFitShapeIsTheOneTheOriginalLacksThatFollowsNoBodyShape()
    {
        var fit = Fit("Fit", .01f);
        var record = Register(fit, "base:1");
        Assert.That(RefitRecords.FitShape(record), Is.EqualTo("refit"));
        Assert.That(RefitRecords.FitShape(fit, jacketOriginal, new[] { new RefitShape("Flex", "refit") }), Is.Null,
            "A shape that follows the body is not the fit.");
        var shapesOnly = Mesh("Shapes only", "Flex");
        Assert.That(RefitRecords.FitShape(shapesOnly, jacketOriginal, new[] { new RefitShape("Flex", "Flex") }), Is.Null);
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
        Assert.That(deltas[0].magnitude + deltas[2].magnitude, Is.EqualTo(0f).Within(1e-6f), "Where the fits agree, nothing moves.");
        Assert.That(copy.sharedMesh.GetBlendShapeIndex("ReFit fit: Tight"), Is.LessThan(0), "The worn fit is the mesh itself.");
        // Picked in the menu, the kept shape at 100 over the worn fit (its "refit" shape still at 100) shows the kept fit.
        var shown = copy.sharedMesh.vertices;
        int fitShape = copy.sharedMesh.GetBlendShapeIndex("refit");
        var fitDeltas = new Vector3[shown.Length];
        copy.sharedMesh.GetBlendShapeFrameVertices(fitShape, 0, fitDeltas, null, null);
        Assert.That(shown[1].x + fitDeltas[1].x + deltas[1].x, Is.EqualTo(jacketOriginal.vertices[1].x + .01f).Within(1e-6f));
        Assert.That(copy.GetComponents(VrcFury.Component).Length, Is.EqualTo(2), "One toggle per fit.");
        Assert.That(tight.blendShapeCount, Is.EqualTo(1), "The project's mesh is untouched: only its fit shape.");
        Assert.That(jacket.GetComponents(VrcFury.Component), Is.Empty, "The scene avatar is untouched.");
    }

    [Test]
    public void AKeptFitSkinnedToAnotherArmatureIsLeftOutOfTheMenu()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        var other = Fit("Other armature fit", .01f);
        other.bindposes = new[] { Matrix4x4.Translate(Vector3.up * .1f) };
        var record = Register(other, "base:1");
        RefitRecords.KeepFit(record, "Other");
        var tight = Fit("Tight fit", -.01f);
        record = Register(tight, "base:1");
        RefitRecords.KeepFit(record, "Tight");
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        var warnings = new List<string>();
        Assert.That(RefitKeptFitsBuild.Apply(clone, warnings), Is.Zero);
        Assert.That(warnings.Any(w => w.Contains("another armature")), Is.True, string.Join("; ", warnings));
        Assert.That(clone.transform.Find("Jacket").GetComponent<SkinnedMeshRenderer>().sharedMesh, Is.SameAs(tight), "Nothing to add: the mesh stays.");
    }

    [Test]
    public void ABodyShrinkIsLeftOutOnABodyMeshItWasNotPlannedOn()
    {
        var fit = Fit("Fit", 0f);
        var record = Register(fit, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 1 }, new[] { Vector3.back * .002f });
        // Another version of the body with as many vertices.
        body.sharedMesh = Mesh("Body 2", "Flex");
        var warnings = new List<string>();
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        Assert.That(RefitShrinkBuild.Apply(clone, warnings), Is.Zero);
        Assert.That(warnings.Any(w => w.Contains("another body mesh")), Is.True, string.Join("; ", warnings));

        // Its planned mesh deleted: left out too, never applied to whatever body mesh is there.
        record.bodyShrinkMesh = null;
        warnings.Clear();
        var second = Object.Instantiate(root);
        created.Add(second);
        SceneManager.MoveGameObjectToScene(second, scene);
        Assert.That(RefitShrinkBuild.Apply(second, warnings), Is.Zero);
        Assert.That(warnings.Any(w => w.Contains("is gone")), Is.True, string.Join("; ", warnings));
    }

    [Test]
    public void ABodyShrinkCheckedWhenTheBuildStartsSurvivesAnotherStepCopyingTheBody()
    {
        var fit = Fit("Fit", 0f);
        var record = Register(fit, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 1 }, new[] { Vector3.back * .002f });
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        RefitShrinkBuild.Capture(clone);
        // A later build step works on its own copy of the body mesh.
        var copiedBody = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        copiedBody.sharedMesh = Object.Instantiate(copiedBody.sharedMesh);
        created.Add(copiedBody.sharedMesh);
        var warnings = new List<string>();
        Assert.That(RefitShrinkBuild.Apply(clone, warnings), Is.EqualTo(1), string.Join("; ", warnings));
        created.Add(copiedBody.sharedMesh);
    }

    [Test]
    public void TwoPiecesOfTheSameNameEachKeepTheirShrink()
    {
        var fit = Fit("Fit", 0f);
        var record = Register(fit, "base:1");
        RefitRecords.SetBodyShrink(record, body.sharedMesh, new[] { 0 }, new[] { Vector3.back * .002f });
        var twin = AddRenderer("Jacket", Mesh("Jacket twin"));
        var twinFit = Mesh("Twin fit");
        twin.sharedMesh = twinFit;
        var twinRecord = RefitRecords.Register(twin, new RefitRendererState(), twinFit, null, body, new RefitShape[0], OrbitersRefit.FitKind.Fitted, "base:1", "base:1", "Test");
        RefitRecords.SetBodyShrink(twinRecord, body.sharedMesh, new[] { 2 }, new[] { Vector3.left * .003f });
        var clone = Object.Instantiate(root);
        created.Add(clone);
        SceneManager.MoveGameObjectToScene(clone, scene);
        var warnings = new List<string>();
        Assert.That(RefitShrinkBuild.Apply(clone, warnings), Is.EqualTo(2), string.Join("; ", warnings));
        var copiedBody = clone.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        created.Add(copiedBody.sharedMesh);
        string name = RefitShrinkBuild.ShapeName(jacket);
        Assert.That(copiedBody.sharedMesh.GetBlendShapeIndex(name), Is.GreaterThanOrEqualTo(0));
        Assert.That(copiedBody.sharedMesh.GetBlendShapeIndex(name + " 2"), Is.GreaterThanOrEqualTo(0), "The second piece's shrink is not lost.");
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
        RefitRecords.Register(jacket, new RefitRendererState { mesh = jacketOriginal }, fit, null, body, new RefitShape[0], OrbitersRefit.FitKind.Fitted,
            baseKey, baseKey, "Test");

    // A fit of the jacket as the refit engines make one: the original mesh, its middle vertex moved along x by the "refit"
    // blendshape, worn at 100.
    private Mesh Fit(string name, float offset)
    {
        var mesh = Object.Instantiate(jacketOriginal);
        mesh.name = name;
        created.Add(mesh);
        var deltas = new Vector3[mesh.vertexCount];
        deltas[1] = Vector3.right * offset;
        mesh.AddBlendShapeFrame(RefitRecords.DefaultFitShape, 100f, deltas, null, null);
        jacket.sharedMesh = mesh;
        jacket.SetBlendShapeWeight(mesh.GetBlendShapeIndex(RefitRecords.DefaultFitShape), 100f);
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
