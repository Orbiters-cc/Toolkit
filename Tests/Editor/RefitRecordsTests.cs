using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Stands in for ReFit: gives the mesh a copy with the requested shapes, or fails when told to.</summary>
internal sealed class FakeRefitEngine : IRefitEngine
{
    public readonly List<RefitJob> Jobs = new List<RefitJob>();
    /// <summary>The mesh each job started from.</summary>
    public readonly List<Mesh> Inputs = new List<Mesh>();
    public readonly List<Mesh> Created = new List<Mesh>();
    public Func<RefitJob, bool> Fails = _ => false;
    /// <summary>Messages a successful refit reports, e.g. a rough fit's warning.</summary>
    public List<RefitMessage> Reports = new List<RefitMessage>();
    public string SaveMeshesIn;

    public string Name => "Fake engine";

    public IEnumerator Run(RefitJob job, Action<float, string> progress, Action<RefitOutcome> done, CancellationToken cancellation)
    {
        Jobs.Add(job);
        Inputs.Add(job.Renderer.sharedMesh);
        yield return null;
        if (Fails(job))
        {
            done(new RefitOutcome { Messages = { new RefitMessage { Severity = RefitSeverity.Error, Code = "fake", Text = "Fake failure" } } });
            yield break;
        }
        var mesh = Object.Instantiate(job.Renderer.sharedMesh);
        mesh.name = job.Renderer.sharedMesh.name + "_fitted";
        var deltas = new Vector3[mesh.vertexCount];
        var generated = new List<string>();
        foreach (string shape in job.Shapes)
        {
            string name = mesh.GetBlendShapeIndex(shape) < 0 ? shape : shape + "_2";
            mesh.AddBlendShapeFrame(name, 100, deltas, null, null);
            generated.Add(name);
        }
        string path = null;
        if (SaveMeshesIn != null)
        {
            path = AssetDatabase.GenerateUniqueAssetPath(SaveMeshesIn + "/fitted.asset");
            AssetDatabase.CreateAsset(mesh, path);
        }
        else Created.Add(mesh);
        Undo.RecordObject(job.Renderer, "Fake refit");
        job.Renderer.sharedMesh = mesh;
        done(new RefitOutcome { Success = true, Mesh = mesh, MeshPath = path, SourceShapes = job.Shapes.ToArray(), GeneratedShapes = generated.ToArray(), Messages = Reports.ToList() });
    }

    public string SaveMetadata(SkinnedMeshRenderer renderer) => null;
    public void LoadMetadata(SkinnedMeshRenderer renderer, string json) { }
    public void RemoveMetadata(SkinnedMeshRenderer renderer) { }
    public void OpenCommission(RefitJob job, RefitOutcome outcome) { }
}

public sealed class RefitRecordsTests
{
    private GameObject root, originalBase;
    private SkinnedMeshRenderer body, jacket;
    private Mesh bodyMesh, jacketMesh;
    private Transform bone;
    private IRefitEngine previousEngine;
    private FakeRefitEngine engine;
    private string folder;
    private Scene scene;
    private int undoGroup;
    private Scene userScene;
    private int[] userRoots;

    [SetUp]
    public void SetUp()
    {
        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        userScene = SceneManager.GetActiveScene();
        userRoots = userScene.GetRootGameObjects().Select(o => o.GetInstanceID()).ToArray();
        scene = EditorSceneManager.NewPreviewScene();
        folder = null;
        previousEngine = RefitEngine.Current;
        engine = new FakeRefitEngine();
        RefitEngine.Register(engine);
        root = new GameObject("Refit test avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        bone = new GameObject("Bone").transform;
        bone.SetParent(root.transform, false);
        bodyMesh = MakeMesh("Body", "Flex arms", "Muscles", "Smile", "Flex legs");
        jacketMesh = MakeMesh("Jacket", "Smile", "Native");
        body = AddRenderer("Body", bodyMesh);
        jacket = AddRenderer("Jacket", jacketMesh);
        jacket.bones = new[] { bone };
        jacket.rootBone = bone;
        originalBase = new GameObject("Original base");
        SceneManager.MoveGameObjectToScene(originalBase, scene);
        originalBase.AddComponent<SkinnedMeshRenderer>().sharedMesh = bodyMesh;
    }

    [TearDown]
    public void TearDown()
    {
        RefitEngine.Register(previousEngine);
        // Discard this fixture's Undo records before destroying their meshes and hierarchy.
        // Otherwise the test runner's later Undo can resurrect empty Jacket objects in the user's scene.
        Undo.FlushUndoRecordObjects();
        Undo.RevertAllDownToGroup(undoGroup);
        Object.DestroyImmediate(root);
        Object.DestroyImmediate(originalBase);
        foreach (var mesh in engine.Created.Concat(new[] { bodyMesh, jacketMesh }))
            if (mesh != null && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
        if (folder != null) AssetDatabase.DeleteAsset(folder);
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        Assert.That(userScene.GetRootGameObjects().Select(o => o.GetInstanceID()), Is.EquivalentTo(userRoots),
            "ReFit fixtures must not leave or restore objects in the user's scene.");
    }

    [Test]
    public void ShapesAreTheDeclaredOnesOnTheBodyThenItsFlexShapes()
    {
        Assert.That(CustomBases.Shapes(new[] { "Muscles", null, "Missing", "Flex legs", "Muscles" }, bodyMesh),
            Is.EqualTo(new[] { "Muscles", "Flex legs", "Flex arms" }));
        Assert.That(CustomBases.Shapes(null, bodyMesh), Is.EqualTo(new[] { "Flex arms", "Flex legs" }));
        Assert.That(CustomBases.Shapes(new[] { "Muscles" }, null), Is.EqualTo(new[] { "Muscles" }));
        Assert.That(CustomBases.Shapes(null, null), Is.Empty);
    }

    [TestCase("flex biceps", true)]
    [TestCase("biceps FLEX right", true)]
    [TestCase("prefixflexsuffix", true)]
    [TestCase("orbit muscles", false)]
    [TestCase(null, false)]
    public void FlexShapesAreFoundByName(string name, bool expected) => Assert.That(CustomBases.IsFlex(name), Is.EqualTo(expected));

    [Test]
    public void EditorHelpersAreNeverCandidates()
    {
        var overlay = new GameObject("__XRayGizmos_WeightPaint_123");
        overlay.transform.SetParent(root.transform, false);
        var helper = overlay.AddComponent<SkinnedMeshRenderer>();
        var hidden = new GameObject("GeneratedOverlay").AddComponent<SkinnedMeshRenderer>();
        hidden.transform.SetParent(root.transform, false);
        hidden.hideFlags = HideFlags.HideAndDontSave;
        var named = AddRenderer("XRayHarnessAccessory", jacketMesh);
        try
        {
            Assert.That(RefitCandidates.IsEditorHelper(helper), Is.True);
            Assert.That(RefitCandidates.IsEditorHelper(hidden), Is.True);
            Assert.That(RefitCandidates.IsEditorHelper(named), Is.False);
            Assert.That(RefitCandidates.IsEditorHelper(jacket), Is.False);
            Assert.That(RefitCandidates.Meshes(root, body), Is.EquivalentTo(new[] { jacket, named }));
        }
        finally { Object.DestroyImmediate(hidden.gameObject); }
    }

    [Test]
    public void MissingKeepsTheCreatorsOwnShapesUnderAnyNameForm()
    {
        var mesh = MakeMesh("Clothing", "Flex_Arms", "smile");
        try
        {
            Assert.That(RefitRunner.Missing(mesh, new[] { "Flex arms", "Smile", "Muscles", "Muscles", "" }), Is.EqualTo(new[] { "Muscles" }));
            Assert.That(RefitRunner.Missing(null, new[] { "Muscles" }), Is.EqualTo(new[] { "Muscles" }));
        }
        finally { Object.DestroyImmediate(mesh); }
    }

    [Test]
    public void RegisterSyncsWeightsAndRemoveRestoresTheOriginalState()
    {
        var changed = new List<SkinnedMeshRenderer>();
        Action<SkinnedMeshRenderer> listener = changed.Add;
        RefitRecords.Changed += listener;
        try
        {
            jacket.SetBlendShapeWeight(1, 30);
            bone.localPosition = new Vector3(0, 1, 0);
            var original = RefitRecords.Capture(root.transform, jacket);
            var fitted = MakeMesh("Jacket fitted", "Smile", "Native", "Flex arms_2");
            engine.Created.Add(fitted);
            jacket.sharedMesh = fitted;
            bone.localPosition = new Vector3(0, 2, 0);
            body.SetBlendShapeWeight(0, 42);

            var record = RefitRecords.Register(jacket, original, fitted, null, body, new[] { new RefitShape("Flex arms", "Flex arms_2") },
                OrbitersRefit.FitKind.Fitted, "base:1", "Muscle base 1.0", "Test");
            Assert.That(record.Applied, Is.True);
            Assert.That(RefitRecords.IsApplied(jacket), Is.True);
            Assert.That(jacket.GetBlendShapeWeight(2), Is.EqualTo(42), "The generated shape takes the body's weight.");
            Assert.That(RefitRecords.ShapeNames(root.transform, "flex ARMS"), Is.EqualTo(new[] { "flex ARMS", "Flex arms_2" }), "Renamed outputs follow their body shape.");
            Assert.That(RefitRecords.SetWeight(root.transform, new[] { body, jacket }, "Flex arms", 64), Is.True);
            Assert.That(jacket.GetBlendShapeWeight(2), Is.EqualTo(64));

            RefitRecords.Remove(record);
            Assert.That(jacket.GetComponent<OrbitersRefit>(), Is.Null);
            Assert.That(jacket.sharedMesh, Is.SameAs(jacketMesh));
            Assert.That(jacket.GetBlendShapeWeight(1), Is.EqualTo(30));
            Assert.That(bone.localPosition, Is.EqualTo(new Vector3(0, 1, 0)));
            Assert.That(changed, Is.EqualTo(new[] { jacket, jacket }));
        }
        finally { RefitRecords.Changed -= listener; }
    }

    [Test]
    public void TwoBodyShapesCannotDriveOneGeneratedShape()
    {
        var original = RefitRecords.Capture(root.transform, jacket);
        Assert.Throws<InvalidOperationException>(() => RefitRecords.Register(jacket, original, jacketMesh, null, body,
            new[] { new RefitShape("Flex arms", "Flex"), new RefitShape("Flex legs", "Flex") }, OrbitersRefit.FitKind.Shapes, null, null, "Test"));
    }

    [Test]
    public void RestoreRecreatesDeletedBonesFromTheirPaths()
    {
        var original = RefitRecords.Persistent(RefitRecords.Capture(root.transform, jacket));
        Object.DestroyImmediate(bone.gameObject);
        Assert.That(RefitRecords.Restore(root.transform, jacket, original, "Test"), Is.True);
        Assert.That(jacket.bones[0], Is.Not.Null);
        Assert.That(jacket.bones[0].name, Is.EqualTo("Bone"));
        Assert.That(jacket.rootBone, Is.SameAs(jacket.bones[0]));
    }

    [Test]
    public void SameNamedAccessoriesComeBackOnTheirOwnBonesWithoutMovingEachOther()
    {
        // Two accessories called "Jacket", each skinned to its own "Bone": restored from paths only, each one gets its own.
        var second = AddRenderer("Jacket", jacketMesh);
        var bones = new[] { jacket, second }.Select(renderer =>
        {
            var own = new GameObject("Bone").transform;
            own.SetParent(renderer.transform, false);
            renderer.bones = new[] { own };
            renderer.rootBone = own;
            return own;
        }).ToArray();
        jacket.transform.localPosition = Vector3.right;
        second.transform.localPosition = Vector3.right * 2;
        bones[1].localPosition = Vector3.up * 2;
        var saved = RefitRecords.Persistent(RefitRecords.Capture(root.transform, second));
        Assert.That(saved.rootBoneSiblingOrdinals, Is.EqualTo(new[] { 1, 0 }));
        Assert.That(RefitRecords.FindUnder(root.transform, saved.rootBonePath, saved.rootBoneSiblingOrdinals), Is.SameAs(bones[1]));
        Assert.That(RefitRecords.Capture(root.transform, jacket).transforms.Select(t => t.siblingOrdinals), Has.All.Empty,
            "The first of each name keeps plain paths.");

        var order = root.GetComponentsInChildren<Transform>(true);
        second.transform.localPosition = Vector3.zero;
        bones[1].localPosition = Vector3.zero;
        second.bones = new[] { bones[0] };
        second.rootBone = bones[0];
        Assert.That(RefitRecords.CanResolve(root.transform, saved), Is.True);
        Assert.That(RefitRecords.Restore(root.transform, second, saved, "Test"), Is.True);
        Assert.That(second.bones, Is.EqualTo(new[] { bones[1] }));
        Assert.That(second.rootBone, Is.SameAs(bones[1]));
        Assert.That(second.transform.localPosition, Is.EqualTo(Vector3.right * 2));
        Assert.That(bones[1].localPosition, Is.EqualTo(Vector3.up * 2));
        Assert.That(jacket.transform.localPosition, Is.EqualTo(Vector3.right), "The first jacket is not moved.");
        Assert.That(bones[0].localPosition, Is.EqualTo(Vector3.zero));
        Assert.That(root.GetComponentsInChildren<Transform>(true), Is.EqualTo(order), "Nothing changes places.");

        // Deleted since, the second jacket's bone is created again under it, not taken from the first.
        Object.DestroyImmediate(bones[1].gameObject);
        Assert.That(RefitRecords.CanResolve(root.transform, saved), Is.True);
        Assert.That(RefitRecords.Restore(root.transform, second, saved, "Test"), Is.True);
        Assert.That(second.rootBone, Is.Not.Null);
        Assert.That(second.rootBone.parent, Is.SameAs(second.transform));
        Assert.That(second.rootBone.localPosition, Is.EqualTo(Vector3.up * 2));
        Assert.That(second.bones, Is.EqualTo(new[] { second.rootBone }));
        Assert.That(jacket.transform.childCount, Is.EqualTo(1));
    }

    [Test]
    public void RunnerFitsOnceAndRefitsFromTheOriginalWithoutStacking()
    {
        var result = Run(Batch(RefitMode.Fit, "Flex arms", "Muscles", "Smile"));
        Assert.That(result.Refitted, Is.EqualTo(1));
        var record = jacket.GetComponent<OrbitersRefit>();
        Assert.That(record, Is.Not.Null);
        Assert.That(engine.Jobs[0].Shapes, Is.EqualTo(new[] { "Flex arms", "Muscles" }), "The creator's own Smile is kept.");
        Assert.That(engine.Jobs[0].SourceBody, Is.Not.Null);
        Assert.That(record.original.mesh, Is.SameAs(jacketMesh));
        Assert.That(record.kind, Is.EqualTo(OrbitersRefit.FitKind.Fitted));
        Assert.That(record.shapes.Select(s => s.generated), Is.EqualTo(new[] { "Flex arms", "Muscles" }));
        var firstFit = jacket.sharedMesh;

        Run(Batch(RefitMode.Fit, "Flex legs"));
        Assert.That(engine.Inputs[1], Is.SameAs(jacketMesh), "A refit starts again from the original mesh.");
        Assert.That(jacket.sharedMesh, Is.Not.SameAs(firstFit));
        Assert.That(engine.Jobs[1].Shapes, Is.EqualTo(new[] { "Flex arms", "Muscles", "Flex legs" }));
        Assert.That(jacket.GetComponent<OrbitersRefit>().original.mesh, Is.SameAs(jacketMesh));
    }

    [Test]
    public void AddingShapesKeepsTheFitAndItsOriginal()
    {
        Run(Batch(RefitMode.Fit, "Flex arms"));
        var fitted = jacket.sharedMesh;
        var result = Run(Batch(RefitMode.Shapes, "Flex arms", "Flex legs"));
        Assert.That(result.Refitted, Is.EqualTo(1));
        Assert.That(engine.Jobs[1].Mode, Is.EqualTo(RefitMode.Shapes));
        Assert.That(engine.Inputs[1], Is.SameAs(fitted), "Shapes are added on top of the fit.");
        Assert.That(engine.Jobs[1].Shapes, Is.EqualTo(new[] { "Flex legs" }));
        var record = jacket.GetComponent<OrbitersRefit>();
        Assert.That(record.kind, Is.EqualTo(OrbitersRefit.FitKind.Fitted));
        Assert.That(record.original.mesh, Is.SameAs(jacketMesh));
        Assert.That(record.shapes.Select(s => s.source), Is.EqualTo(new[] { "Flex arms", "Flex legs" }));
        Assert.That(jacket.sharedMesh, Is.Not.SameAs(fitted));

        var nothing = Run(Batch(RefitMode.Shapes, "Flex legs"));
        Assert.That(nothing.Items[0].Skipped, Is.True);
        Assert.That(engine.Jobs.Count, Is.EqualTo(2), "Nothing to add: the engine is not asked.");
    }

    [Test]
    public void AFailedRefitKeepsThePreviousOne()
    {
        Run(Batch(RefitMode.Fit, "Flex arms"));
        var fitted = jacket.sharedMesh;
        engine.Fails = _ => true;
        var result = Run(Batch(RefitMode.Fit, "Flex legs"));
        Assert.That(result.Failed.Count, Is.EqualTo(1));
        Assert.That(result.Failed[0].Outcome.Error, Is.EqualTo("Fake failure"));
        Assert.That(jacket.sharedMesh, Is.SameAs(fitted));
        Assert.That(RefitRecords.IsApplied(jacket), Is.True);
    }

    [Test]
    public void FitWithoutTheOriginalBaseFailsWithoutAskingTheEngine()
    {
        var batch = Batch(RefitMode.Fit, "Flex arms");
        batch.Original = null;
        var result = Run(batch);
        Assert.That(result.Failed.Count, Is.EqualTo(1));
        Assert.That(engine.Jobs, Is.Empty);
        Assert.That(jacket.sharedMesh, Is.SameAs(jacketMesh));
    }

    [Test]
    public void TheSameMeshOnAnIdenticalAvatarReusesTheCachedResult()
    {
        folder = "Assets/RefitTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        AssetDatabase.CreateAsset(bodyMesh, folder + "/body.asset");
        AssetDatabase.CreateAsset(jacketMesh, folder + "/jacket.asset");
        var coverageJob = new RefitJob { Renderer = jacket, Body = body, Mode = RefitMode.Shapes };
        string normalKey = RefitCache.Key(coverageJob, root.transform, "test-engine");
        coverageJob.CoverDifferentBaseBody = true;
        Assert.That(RefitCache.Key(coverageJob, root.transform, "test-engine"), Is.Not.EqualTo(normalKey), "Coverage requests must not reuse normal geometry.");
        string bodyOnly = RefitCache.Key(coverageJob, root.transform, "test-engine");
        var inner = AddRenderer("Shirt", jacketMesh);
        coverageJob.CoverageLayers.Add(inner);
        string layered = RefitCache.Key(coverageJob, root.transform, "test-engine");
        Assert.That(layered, Is.Not.EqualTo(bodyOnly), "Inner garments participate in cache identity.");
        inner.transform.localPosition = Vector3.forward * .01f;
        Assert.That(RefitCache.Key(coverageJob, root.transform, "test-engine"), Is.Not.EqualTo(layered), "Moving an inner garment invalidates the outer fit.");
        engine.SaveMeshesIn = folder;
        var createdFolders = new[] { "Assets/Orbiters", "Assets/Orbiters/ReFit", RefitCache.Folder }.Where(f => !AssetDatabase.IsValidFolder(f)).ToList();
        var before = new HashSet<string>(createdFolders.Count == 0 ? AssetDatabase.FindAssets("t:RefitCacheEntry", new[] { RefitCache.Folder }) : new string[0]);
        GameObject second = null;
        try
        {
            second = Object.Instantiate(root);
            SceneManager.MoveGameObjectToScene(second, scene);
            engine.Reports = new List<RefitMessage>
            {
                new RefitMessage { Severity = RefitSeverity.Warning, Code = "surface-coverage-limited", Text = "Short of the surface" },
                new RefitMessage { Severity = RefitSeverity.Info, Code = "phase-timing", Text = "Took a while" },
            };
            var first = Run(Batch(RefitMode.Shapes, "Flex arms"));
            Assert.That(first.Items[0].Reused, Is.False);
            Assert.That(first.Rough, Is.True);
            var secondJacket = second.transform.Find("Jacket").GetComponent<SkinnedMeshRenderer>();
            var batch = Batch(RefitMode.Shapes, "Flex arms");
            batch.Avatar = second.transform;
            batch.Body = second.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            batch.Renderers = new List<SkinnedMeshRenderer> { secondJacket };
            var reused = Run(batch);
            Assert.That(reused.Items[0].Reused, Is.True);
            Assert.That(engine.Jobs.Count, Is.EqualTo(1));
            // A rough fit stays rough when reused: its warnings come with it (not its diagnostics).
            Assert.That(reused.Rough, Is.True);
            Assert.That(reused.Items[0].Outcome.Warnings.Select(m => m.Code), Is.EqualTo(new[] { "surface-coverage-limited" }));
            Assert.That(reused.Items[0].Outcome.Messages.Any(m => m.Severity == RefitSeverity.Info), Is.False);
            Assert.That(secondJacket.sharedMesh, Is.SameAs(jacket.sharedMesh));
            Assert.That(secondJacket.GetComponent<OrbitersRefit>().shapes.Single().generated, Is.EqualTo("Flex arms"));
        }
        finally
        {
            // TearDown first clears Undo while this clone and its parents still exist,
            // then closes their preview scene. Destroying it here lets Undo resurrect its Jacket child.
            if (AssetDatabase.IsValidFolder(RefitCache.Folder))
                foreach (string guid in AssetDatabase.FindAssets("t:RefitCacheEntry", new[] { RefitCache.Folder }))
                    if (!before.Contains(guid)) AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
            for (int i = createdFolders.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(createdFolders[i]);
        }
    }

    [Test]
    public void AFitIsReusedOnlyForTheSameOriginalBodyPoseAndShape()
    {
        folder = "Assets/RefitTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        AssetDatabase.CreateAsset(bodyMesh, folder + "/body.asset");
        AssetDatabase.CreateAsset(jacketMesh, folder + "/jacket.asset");
        engine.SaveMeshesIn = folder;
        var createdFolders = new[] { "Assets/Orbiters", "Assets/Orbiters/ReFit", RefitCache.Folder }.Where(f => !AssetDatabase.IsValidFolder(f)).ToList();
        var before = new HashSet<string>(createdFolders.Count == 0 ? AssetDatabase.FindAssets("t:RefitCacheEntry", new[] { RefitCache.Folder }) : new string[0]);
        var original = originalBase.GetComponent<SkinnedMeshRenderer>();
        var hips = new GameObject("Hips").transform;
        hips.SetParent(originalBase.transform, false);
        original.bones = new[] { hips };
        original.rootBone = hips;
        try
        {
            Assert.That(Run(Batch(RefitMode.Fit, "Flex arms")).Items[0].Reused, Is.False);
            // Fitted again from the same original body: the same inputs, the first result.
            Assert.That(Run(Batch(RefitMode.Fit, "Flex arms")).Items[0].Reused, Is.True);
            Assert.That(engine.Jobs.Count, Is.EqualTo(1));

            // The original body posed or shaped differently gives another fit: computed, not reused.
            hips.localRotation = Quaternion.Euler(0, 0, 30);
            Assert.That(Run(Batch(RefitMode.Fit, "Flex arms")).Items[0].Reused, Is.False);
            hips.localRotation = Quaternion.identity;
            original.SetBlendShapeWeight(1, 50);
            Assert.That(Run(Batch(RefitMode.Fit, "Flex arms")).Items[0].Reused, Is.False);
            Assert.That(engine.Jobs.Count, Is.EqualTo(3));

            // Back as it was: the first result again.
            original.SetBlendShapeWeight(1, 0);
            Assert.That(Run(Batch(RefitMode.Fit, "Flex arms")).Items[0].Reused, Is.True);
            Assert.That(engine.Jobs.Count, Is.EqualTo(3));
        }
        finally
        {
            if (AssetDatabase.IsValidFolder(RefitCache.Folder))
                foreach (string guid in AssetDatabase.FindAssets("t:RefitCacheEntry", new[] { RefitCache.Folder }))
                    if (!before.Contains(guid)) AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
            for (int i = createdFolders.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(createdFolders[i]);
        }
    }

    [Test] public void MixedOutfitFitsInnerLayersFirstAndKeepsPropsOutOfCoverage()
    {
        var shirt = AddRenderer("Shirt", jacketMesh);
        var prop = AddRenderer("FishingRod", jacketMesh);
        var batch = Batch(RefitMode.Fit, "Flex arms");
        batch.Renderers = new List<SkinnedMeshRenderer> { jacket, prop, shirt };
        batch.CoverageByRenderer[jacket] = true;
        batch.CoverageByRenderer[shirt] = true;
        batch.CoverageByRenderer[prop] = false;
        var result = Run(batch);
        Assert.That(result.Failed, Is.Empty);
        var jacketJob = engine.Jobs.Single(j => j.Renderer == jacket);
        Assert.That(engine.Jobs.IndexOf(engine.Jobs.Single(j => j.Renderer == shirt)), Is.LessThan(engine.Jobs.IndexOf(jacketJob)));
        Assert.That(jacketJob.CoverageLayers, Is.EqualTo(new[] { shirt }));
        Assert.That(engine.Jobs.Single(j => j.Renderer == prop).CoverDifferentBaseBody, Is.False);
    }

    private RefitBatch Batch(RefitMode mode, params string[] shapes) => new RefitBatch
    {
        Avatar = root.transform, Body = body, Renderers = new List<SkinnedMeshRenderer> { jacket }, Shapes = shapes.ToList(), Mode = mode,
        Original = new CustomBaseOriginal { Avatar = originalBase, Body = originalBase.GetComponent<SkinnedMeshRenderer>() },
        BaseKey = "base:1", BaseName = "Muscle base 1.0", Tool = "Test", UseCache = folder != null,
    };

    internal static RefitBatchResult Run(RefitBatch batch)
    {
        RefitBatchResult result = null;
        Drain(RefitRunner.Run(batch, null, r => result = r, CancellationToken.None));
        Assert.That(result, Is.Not.Null);
        return result;
    }

    internal static void Drain(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        for (int guard = 0; stack.Count > 0 && guard < 100000; guard++)
        {
            var top = stack.Peek();
            if (top.MoveNext()) { if (top.Current is IEnumerator nested) stack.Push(nested); }
            else stack.Pop();
        }
    }

    private SkinnedMeshRenderer AddRenderer(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }

    internal static Mesh MakeMesh(string name, params string[] shapes)
    {
        var mesh = new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        foreach (string shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
        return mesh;
    }
}
