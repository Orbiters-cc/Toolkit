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

    // Round 2 (reference scan limits, a): files of kinds that never use another asset by Unity's account are read too:
    // a .meta names what its file uses (a script's default references, here an importer's user data), and a text file's
    // content names GUIDs (a JSON or text manifest of a tool).
    [UnityTest] public IEnumerator MetaFilesAndTextFilesNameWhatTheyUse()
    {
        string path = folder + "/Fit.asset";
        AssetDatabase.CreateAsset(new Mesh { name = "Fit" }, path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        string json = folder + "/Settings.json", notes = folder + "/Notes.txt";
        File.WriteAllText(json, "{ \"name\": \"settings\" }");
        File.WriteAllText(notes, "Generated for " + guid + "\n");
        AssetDatabase.ImportAsset(json, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(notes, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(json);
        importer.userData = guid;
        importer.SaveAndReimport();
        StringAssert.Contains(guid, File.ReadAllText(json + ".meta"));

        var scan = ProjectReferences.ScanAsync();
        yield return Wait(scan);
        Assert.That(scan.Result.UsersOf(path), Does.Contain(json), "its .meta names the mesh");
        Assert.That(scan.Result.UsersOf(path), Does.Contain(notes), "its text names the mesh");
        Assert.That(scan.Result.UsersOf(path), Does.Not.Contain(path), "a file is not its own user");
    }

    // Round 2 (b): what users can edit outside Assets/ counts: ProjectSettings/ and the embedded and local packages.
    [Test] public void ProjectSettingsAndEditablePackagesAreRead()
    {
        var roots = ProjectReferences.Roots();
        Assert.That(roots.Select(r => r.Prefix), Does.Contain("Assets").And.Contain("ProjectSettings"));
        foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                     .Where(p => p.source == UnityEditor.PackageManager.PackageSource.Embedded || p.source == UnityEditor.PackageManager.PackageSource.Local))
            Assert.That(roots.Select(r => r.Prefix), Does.Contain(package.assetPath.TrimEnd('/')), package.name);
        Assert.That(roots.Single(r => r.Prefix == "ProjectSettings").Imported, Is.False);

        // A root outside Assets/ is read as it is on disk: a file with its .meta, names Unity leaves out skipped.
        string temporary = Path.Combine(Path.GetTempPath(), "OrbitersReferences-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary, "Prefabs"));
            Directory.CreateDirectory(Path.Combine(temporary, "Samples~"));
            const string used = "0123456789abcdef0123456789abcdef", inMeta = "fedcba9876543210fedcba9876543210";
            File.WriteAllText(Path.Combine(temporary, "Prefabs", "User.prefab"), "%YAML 1.1\n  m_Mesh: {fileID: 4300000, guid: " + used + ", type: 2}\n");
            File.WriteAllText(Path.Combine(temporary, "Prefabs", "User.prefab.meta"), "fileFormatVersion: 2\nuserData: " + inMeta + "\n");
            File.WriteAllText(Path.Combine(temporary, "Samples~", "Ignored.prefab"), "guid: " + used);
            File.WriteAllBytes(Path.Combine(temporary, "Binary.bytes"), new byte[] { 1, 0, 2, 0 });
            var root = new ProjectReferences.Root("Packages/orbiters.test-local", temporary, false);
            var read = new ProjectReferences.Reader(new System.Collections.Generic.List<ProjectReferences.Root> { root }, default).Run();
            var prefab = read.Single(f => f.Path == "Packages/orbiters.test-local/Prefabs/User.prefab");
            Assert.That(prefab.Guids, Is.EquivalentTo(new[] { used, inMeta }));
            Assert.That(prefab.ContentRead, Is.True);
            Assert.That(read.Any(f => f.Path.Contains("Samples~")), Is.False, "Unity leaves out names ending in ~");
            Assert.That(read.Single(f => f.Path.EndsWith("Binary.bytes", StringComparison.Ordinal)).ContentRead, Is.False, "a NUL byte: not text");
        }
        finally { if (temporary.Contains("OrbitersReferences-") && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    // Exactly 32 hexadecimal digits make a GUID; longer runs (a SHA-256) and shorter ones do not, at any chunk boundary.
    [Test] public void GuidTokensAreExactRunsOf32HexDigits()
    {
        string file = Path.Combine(Path.GetTempPath(), "OrbitersTokens-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            const string guid = "0123456789ABCDEF0123456789abcdef";
            // The GUID straddles the reader's 64 KB chunk boundary.
            string padding = new string(' ', 64 * 1024 - 88);
            File.WriteAllText(file, "guid: " + new string('a', 64) + " x" + padding + guid + "\n" + new string('b', 31) + " end " + guid.Substring(1));
            var found = new System.Collections.Generic.HashSet<string>();
            Assert.That(ProjectReferences.Reader.Tokens(file, found, true, default), Is.True);
            Assert.That(found, Is.EquivalentTo(new[] { guid.ToLowerInvariant() }));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    // Round 2 (c): a file in Unity's binary format changed on disk since Unity imported it cannot say what it uses: the
    // scan says so, and a sweep keeps everything until Unity imports the file again.
    [UnityTest] public IEnumerator ABinaryFileChangedSinceItsImportKeepsEverything()
    {
        string path = folder + "/Fit.asset";
        AssetDatabase.CreateAsset(new Mesh { name = "Fit" }, path);
        GeneratedAssets.Track(path);
        string binary = folder + "/Binary.asset";
        UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { new Mesh { name = "Binary" } }, binary, false);
        AssetDatabase.ImportAsset(binary, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        Assert.That(File.ReadAllBytes(binary).Take(8192).Contains((byte)0), Is.True, "written in Unity's binary format");

        var fresh = ProjectReferences.ScanAsync();
        yield return Wait(fresh);
        Assert.That(fresh.Result.UsersOf(path), Is.Empty);

        File.SetLastWriteTimeUtc(binary, DateTime.UtcNow.AddMinutes(5));
        var stale = ProjectReferences.ScanAsync();
        while (!stale.IsCompleted) yield return null;
        Assert.That(stale.Exception?.GetBaseException(), Is.InstanceOf<UncertainReferencesException>());
        var sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Is.Empty, "nothing goes while a file's import is out of date");
        Assert.True(File.Exists(path));

        AssetDatabase.ImportAsset(binary, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Contain(path), "imported again: what it uses is known, and nothing uses the mesh");
    }

    // Round 2 (c, text): a prefab edited on disk and not imported yet already counts with what it now uses.
    [UnityTest] public IEnumerator APrefabEditedAndNotImportedKeepsWhatItUses()
    {
        string path = folder + "/Fit.asset";
        AssetDatabase.CreateAsset(new Mesh { name = "Fit" }, path);
        GeneratedAssets.Track(path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        var go = new GameObject("Generated mesh user");
        go.AddComponent<MeshFilter>();
        string prefab = folder + "/User.prefab";
        PrefabUtility.SaveAsPrefabAsset(go, prefab);
        Object.DestroyImmediate(go);
        string text = File.ReadAllText(prefab);
        StringAssert.Contains("m_Mesh: {fileID: 0}", text);
        File.WriteAllText(prefab, text.Replace("m_Mesh: {fileID: 0}", "m_Mesh: {fileID: 4300000, guid: " + guid + ", type: 2}"));
        var sweep = GeneratedAssets.SweepAsync();
        yield return Wait(sweep);
        Assert.That(sweep.Result, Does.Not.Contain(path), "the prefab's text uses it");
        Assert.True(File.Exists(path));
    }

    // Round 2 (d): a file written after the scan read the project, before the deletion, is read again in the step that
    // deletes: a prefab saved meanwhile keeps the file it uses.
    [UnityTest] public IEnumerator AFileWrittenBetweenTheScanAndTheDeleteKeepsWhatItUses()
    {
        string path = folder + "/Fit.asset";
        AssetDatabase.CreateAsset(new Mesh { name = "Fit" }, path);
        GeneratedAssets.Track(path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        string late = folder + "/Late.prefab";
        GeneratedAssets.AfterScan = () => File.WriteAllText(late,
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!33 &1\nMeshFilter:\n  m_Mesh: {fileID: 4300000, guid: " + guid + ", type: 2}\n");
        try
        {
            var sweep = GeneratedAssets.SweepAsync();
            yield return Wait(sweep);
            Assert.That(sweep.Result, Does.Not.Contain(path), "the prefab written after the scan uses it");
            Assert.True(File.Exists(path));
        }
        finally { GeneratedAssets.AfterScan = null; }
    }

    // Review of round 2: a deletion is decided by Removable in one call: candidates used only by each other go together,
    // one kept by the caller keeps what it uses, and none goes when the project changed too much to check again at once.
    [UnityTest] public IEnumerator RemovableDecidesInOneCallAndAnswersNoneWhenUncertain()
    {
        var texture = new Texture2D(2, 2);
        AssetDatabase.CreateAsset(texture, folder + "/Picture.asset");
        var material = new Material(Shader.Find("Standard")) { mainTexture = texture };
        AssetDatabase.CreateAsset(material, folder + "/Look.mat");
        AssetDatabase.SaveAssets();
        string picture = folder + "/Picture.asset", look = folder + "/Look.mat";

        var scan = ProjectReferences.ScanAsync();
        yield return Wait(scan);
        var map = scan.Result;
        Assert.That(map.UsersOf(picture), Does.Contain(look));
        Assert.That(map.Removable(new[] { picture }), Is.Empty, "the material outside the candidates uses it");
        Assert.That(map.Removable(new[] { picture, look }), Is.EquivalentTo(new[] { picture, look }), "used only by a candidate: both go");
        Assert.That(map.Removable(new[] { picture, look }, p => p == look), Is.Empty, "the kept material keeps its picture");

        for (int i = 0; i <= ProjectReferences.RecheckFiles; i++) File.WriteAllText(folder + "/Note" + i + ".txt", "note " + i);
        Assert.That(map.Removable(new[] { picture, look }, null, out string uncertain), Is.Empty, "too many files changed to check again at once");
        Assert.That(uncertain, Is.Not.Null);
    }

    // Review of round 2: a text file with a byte order mark is read in its own encoding; a text file that does not read
    // as text (UTF-16 without one) cannot say what it names, which is not "nothing".
    [Test] public void TextInOtherEncodingsIsDecodedOrUncertain()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "OrbitersEncodings-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(temporary);
            const string guid = "0123456789abcdef0123456789abcdef";
            File.WriteAllText(Path.Combine(temporary, "Marked16.json"), "{\"guid\": \"" + guid + "\"}", new System.Text.UnicodeEncoding(false, true));
            File.WriteAllText(Path.Combine(temporary, "Marked32.txt"), "uses " + guid, new System.Text.UTF32Encoding(true, true));
            File.WriteAllBytes(Path.Combine(temporary, "Bare16.json"), new System.Text.UnicodeEncoding(false, false).GetBytes("{\"guid\": \"" + guid + "\"}"));
            var root = new ProjectReferences.Root("Packages/orbiters.test-encodings", temporary, false);
            var read = new ProjectReferences.Reader(new System.Collections.Generic.List<ProjectReferences.Root> { root }, default).Run();
            Assert.That(read.Single(f => f.Path.EndsWith("Marked16.json", StringComparison.Ordinal)).Guids, Is.EquivalentTo(new[] { guid }));
            Assert.That(read.Single(f => f.Path.EndsWith("Marked32.txt", StringComparison.Ordinal)).Guids, Is.EquivalentTo(new[] { guid }));
            Assert.That(read.Single(f => f.Path.EndsWith("Bare16.json", StringComparison.Ordinal)).Failure, Is.Not.Null, "cannot be decoded: uncertain");
        }
        finally { if (temporary.Contains("OrbitersEncodings-") && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
