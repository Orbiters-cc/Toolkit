using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
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
}
