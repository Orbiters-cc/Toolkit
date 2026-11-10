using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// Generated files stay while something uses them, go once nothing does, and come back with the object an Undo brings back.
public sealed class GeneratedAssetsTests
{
    private Scene scene;
    private string folder;
    private GameObject user;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        folder = "Assets/OrbitersGeneratedTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
    }

    [TearDown] public void TearDown()
    {
        foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>().Where(f => f && f.name == "Generated mesh user" && !EditorUtility.IsPersistent(f)))
        { Undo.ClearUndo(filter.gameObject); Object.DestroyImmediate(filter.gameObject); }
        EditorSceneManager.ClosePreviewScene(scene);
        // Only the folder this test created.
        if (folder.StartsWith("Assets/OrbitersGeneratedTest-", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
    }

    // A sweep reads every asset of the project: give it time.
    private static IEnumerator Wait(Task task)
    {
        double deadline = EditorApplication.timeSinceStartup + 300;
        while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
        Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
        if (task.IsFaulted) throw task.Exception.GetBaseException();
    }

    [UnityTest] public IEnumerator UnusedGeneratedMeshGoesAndUndoBringsItBack()
    {
        string path = folder + "/Fit.asset";
        var mesh = new Mesh { name = "Fit" };
        AssetDatabase.CreateAsset(mesh, path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        GeneratedAssets.Track(path);
        Assert.True(GeneratedAssets.IsTracked(path));
        user = new GameObject("Generated mesh user");
        SceneManager.MoveGameObjectToScene(user, scene);
        user.AddComponent<MeshFilter>().sharedMesh = mesh;

        var sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Not.Contain(path), "Used by an object of a loaded scene.");
        Assert.True(File.Exists(path));

        Undo.IncrementCurrentGroup();
        Undo.DestroyObjectImmediate(user);
        Undo.IncrementCurrentGroup();
        sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Contain(path), "Nothing uses it any more.");
        Assert.False(File.Exists(path));

        Undo.PerformUndo();
        Assert.True(File.Exists(path), "Undo brought back its user: the file comes back.");
        Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path), "with its GUID, so the object finds it again");
        var restored = Resources.FindObjectsOfTypeAll<MeshFilter>().Single(f => f.name == "Generated mesh user" && !EditorUtility.IsPersistent(f));
        Assert.NotNull(restored.sharedMesh);
    }

    // GAL-18 (audit 2026-10-28): every kind of asset that can use another is read, not a list of types: a clip only an
    // override controller uses is used (AnimatorOverrideController is no AnimatorController or ScriptableObject).
    [UnityTest] public IEnumerator AClipOnlyAnOverrideControllerUsesCountsAsUsed()
    {
        var placeholder = new AnimationClip { name = "Placeholder" };
        AssetDatabase.CreateAsset(placeholder, folder + "/Placeholder.anim");
        var clip = new AnimationClip { name = "Wave" };
        AssetDatabase.CreateAsset(clip, folder + "/Wave.anim");
        var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPathWithClip(folder + "/Base.controller", placeholder);
        var overrides = new AnimatorOverrideController(controller) { name = "Override" };
        overrides[placeholder] = clip;
        AssetDatabase.CreateAsset(overrides, folder + "/Override.overrideController");
        AssetDatabase.SaveAssets();
        var scan = ProjectReferences.ScanAsync();
        yield return Wait(scan);
        Assert.That(scan.Result.UsersOf(folder + "/Wave.anim"), Does.Contain(folder + "/Override.overrideController"));
        Assert.That(ProjectReferences.MayUseOthers(folder + "/Override.overrideController"), Is.True);
        Assert.That(ProjectReferences.MayUseOthers(folder + "/Picture.png"), Is.False);
    }

    // Review of TKV-28: a scan cancelled after an import never lets the map from before the import decide a sweep; a
    // prefab on disk (not loaded) that uses a tracked file keeps it.
    [UnityTest] public IEnumerator ACancelledScanNeverLeavesAnOldMapToSweepWith()
    {
        string path = folder + "/Fit.asset";
        var mesh = new Mesh { name = "Fit" };
        AssetDatabase.CreateAsset(mesh, path);
        GeneratedAssets.Track(path);
        var before = ProjectReferences.ScanAsync();
        yield return Wait(before);
        Assert.That(before.Result.UsersOf(path), Is.Empty);

        var go = new GameObject("Generated mesh user");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        PrefabUtility.SaveAsPrefabAsset(go, folder + "/User.prefab");
        Object.DestroyImmediate(go);
        using (var cancelled = new System.Threading.CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = ProjectReferences.ScanAsync(null, cancelled.Token);
            yield return Wait(stopped);
            Assert.That(stopped.IsCanceled, Is.True);
        }
        var sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Not.Contain(path), "the prefab saved before the cancelled scan uses it");
        Assert.True(File.Exists(path));
    }

    // Review 2 of TKV-28: a prefab edited on disk outside Unity to use a tracked file keeps it once Unity has imported
    // the edit (the sweep reads what Unity's import says; an edit not imported yet is not seen, as on master).
    [UnityTest] public IEnumerator AnEditedPrefabKeepsWhatItNowUses()
    {
        string path = folder + "/Fit.asset";
        var mesh = new Mesh { name = "Fit" };
        AssetDatabase.CreateAsset(mesh, path);
        GeneratedAssets.Track(path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        var go = new GameObject("Generated mesh user");
        go.AddComponent<MeshFilter>();
        string prefab = folder + "/User.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, prefab);
        Object.DestroyImmediate(go);
        var before = ProjectReferences.ScanAsync();
        yield return Wait(before);
        Assert.That(before.Result.UsersOf(path), Is.Empty);

        string text = File.ReadAllText(prefab);
        StringAssert.Contains("m_Mesh: {fileID: 0}", text);
        File.WriteAllText(prefab, text.Replace("m_Mesh: {fileID: 0}", "m_Mesh: {fileID: 4300000, guid: " + guid + ", type: 2}"));
        AssetDatabase.ImportAsset(prefab, ImportAssetOptions.ForceSynchronousImport);
        var sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Not.Contain(path), "the prefab uses it now");
        Assert.True(File.Exists(path));
    }

    // Review 3 of TKV-28: a sweep is skipped while nothing changed since the last complete one, and runs after each kind
    // of change the editor reports, and after a file is tracked.
    [UnityTest] public IEnumerator ASweepIsSkippedUntilSomethingChanges()
    {
        // Settle: the first sweeps may put aside the project's own unused files, which is a change too.
        bool ran = true;
        for (int i = 0; i < 4 && ran; i++)
        {
            var settle = GeneratedAssets.SweepIfChangedAsync();
            yield return Wait(settle);
            ran = settle.Result;
        }
        Assert.That(ran, Is.False, "nothing changed since the last complete sweep");

        var kinds = new (string Name, Action<EditorChanges> Report)[]
        {
            ("properties", c => c.AddProperties(1)), ("structure", c => c.AddStructure(1)), ("subtree", c => c.AddSubtree(1)),
            ("asset", c => c.AddAsset("0123456789abcdef0123456789abcdef")), ("import", c => c.AddImports(new[] { folder + "/x.asset" }, null, null, null)),
            ("undo", c => c.SetUndoRedo()), ("everything", c => c.SetEverything()), ("hierarchy", c => c.SetHierarchy()), ("project", c => c.SetProject()),
        };
        foreach (var (name, report) in kinds)
        {
            EditorChangeBus.Report(report);
            var after = GeneratedAssets.SweepIfChangedAsync();
            yield return Wait(after);
            Assert.That(after.Result, Is.True, "runs after a change: " + name);
            var again = GeneratedAssets.SweepIfChangedAsync();
            yield return Wait(again);
            Assert.That(again.Result, Is.False, "skipped once it stands: " + name);
        }

        // A tracked file a loaded object uses (so the sweep keeps it and changes nothing).
        string path = folder + "/Used.asset";
        var mesh = new Mesh { name = "Used" };
        AssetDatabase.CreateAsset(mesh, path);
        user = new GameObject("Generated mesh user");
        SceneManager.MoveGameObjectToScene(user, scene);
        user.AddComponent<MeshFilter>().sharedMesh = mesh;
        var settled = GeneratedAssets.SweepIfChangedAsync();
        yield return Wait(settled);
        GeneratedAssets.Track(path);
        var tracked = GeneratedAssets.SweepIfChangedAsync();
        yield return Wait(tracked);
        Assert.That(tracked.Result, Is.True, "runs after a file is tracked");
        Assert.True(File.Exists(path));
    }
}
