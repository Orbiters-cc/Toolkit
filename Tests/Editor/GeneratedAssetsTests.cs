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
}
