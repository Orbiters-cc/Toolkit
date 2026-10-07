using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// The Animation Extractor's service. An FBX cannot be made in a test; the service reads any asset holding clips the same way,
// so the source is a controller with clips stored inside it.
public sealed class AnimationExtractionTests
{
    private string folder, source, external;
    private AnimationClip walk, run;
    private EditorCurveBinding height, muscle;

    [SetUp] public void SetUp()
    {
        folder = "Assets/AnimationExtractionTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        source = folder + "/Moves.controller";
        var controller = AnimatorController.CreateAnimatorControllerAtPath(source);

        height = EditorCurveBinding.FloatCurve("Hips", typeof(Transform), "m_LocalPosition.y");
        walk = new AnimationClip { name = "Walk", frameRate = 30 };
        AnimationUtility.SetEditorCurve(walk, height, AnimationCurve.Linear(0, 0, 1, 0.5f));
        AnimationUtility.SetAnimationEvents(walk, new[]
        {
            new AnimationEvent { time = 0.25f, functionName = "Step" },
            new AnimationEvent { time = 0.75f, functionName = "Step", intParameter = 1 },
        });
        muscle = EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[0]);
        run = new AnimationClip { name = "Armature|Run:Fast", frameRate = 24 };
        AnimationUtility.SetEditorCurve(run, muscle, AnimationCurve.Linear(0, -0.2f, 0.5f, 0.3f));
        SetLoop(run, true);
        AssetDatabase.AddObjectToAsset(walk, controller);
        AssetDatabase.AddObjectToAsset(run, controller);
        AssetDatabase.AddObjectToAsset(new AnimationClip { name = "__preview__Walk" }, controller);
        AssetDatabase.SaveAssets();
    }

    [TearDown] public void TearDown()
    {
        if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
        if (external != null && external.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(external))
            Directory.Delete(external, true);
    }

    private ExtractionJob Job(string clip, string name = null, string output = "Out/Nested") =>
        new ExtractionJob { Source = source, Clip = clip, Name = name ?? clip, Folder = folder + "/" + output };

    private static List<ExtractionResult> Extract(ExistingAnimation existing, params ExtractionJob[] jobs) =>
        AnimationExtraction.Extract(jobs, new ExtractionOptions { Existing = existing });

    private static AnimationClip Load(string path) => AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

    private static bool LoopTime(AnimationClip clip) => AnimationUtility.GetAnimationClipSettings(clip).loopTime;

    private static void SetLoop(AnimationClip clip, bool on)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = on;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
    }

    private static (float, float)[] Keys(AnimationClip clip, EditorCurveBinding binding) =>
        AnimationUtility.GetEditorCurve(clip, binding).keys.Select(k => (k.time, k.value)).ToArray();

    [Test] public void ListsClipsWithTheirTimingLoopAndEventsWithoutUnitysPreviewClips()
    {
        var info = AnimationExtraction.Read(source);

        CollectionAssert.AreEquivalent(new[] { "Walk", "Armature|Run:Fast" }, info.Clips.Select(c => c.Name));
        Assert.That(info.Rig, Is.Null, "A controller is not a model.");
        Assert.That(info.IsClipFile, Is.False);
        var walkInfo = info.Clips.Single(c => c.Name == "Walk");
        Assert.That(walkInfo.Length, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(walkInfo.FrameRate, Is.EqualTo(30f));
        Assert.That(walkInfo.Frames, Is.EqualTo(30));
        Assert.That(walkInfo.Events, Is.EqualTo(2));
        Assert.That(walkInfo.Loop, Is.False);
        Assert.That(walkInfo.Humanoid, Is.EqualTo(walk.humanMotion));
        var runInfo = info.Clips.Single(c => c.Name == "Armature|Run:Fast");
        Assert.That(runInfo.Frames, Is.EqualTo(12));
        Assert.That(runInfo.Loop, Is.True);
    }

    [Test] public void SavesAStandaloneCopyWithCurvesEventsAndSettings()
    {
        var result = Extract(ExistingAnimation.Replace, Job("Walk")).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Done), result.Message);
        Assert.That(result.Replaced, Is.False);
        Assert.That(result.Path, Is.EqualTo(folder + "/Out/Nested/Walk.anim"));
        var copy = Load(result.Path);
        Assert.That(copy, Is.Not.Null.And.Not.SameAs(walk));
        Assert.That(AssetDatabase.IsMainAsset(copy), Is.True);
        Assert.That(copy.name, Is.EqualTo("Walk"));
        Assert.That(copy.hideFlags, Is.EqualTo(HideFlags.None));
        CollectionAssert.AreEquivalent(AnimationUtility.GetCurveBindings(walk), AnimationUtility.GetCurveBindings(copy));
        CollectionAssert.AreEqual(Keys(walk, height), Keys(copy, height));
        var events = AnimationUtility.GetAnimationEvents(copy);
        CollectionAssert.AreEqual(new[] { ("Step", 0.25f, 0), ("Step", 0.75f, 1) }, events.Select(e => (e.functionName, e.time, e.intParameter)));
        Assert.That(copy.frameRate, Is.EqualTo(30f));
        Assert.That(LoopTime(copy), Is.False);
        Assert.That(AnimationExtraction.Clips(source), Has.Count.EqualTo(2), "The source keeps its clips.");
    }

    [Test] public void KeepsHumanoidMuscleCurves()
    {
        var result = Extract(ExistingAnimation.Replace, Job("Armature|Run:Fast", "Run")).Single();

        var copy = Load(result.Path);
        Assert.That(AnimationUtility.GetCurveBindings(copy), Does.Contain(muscle));
        CollectionAssert.AreEqual(Keys(run, muscle), Keys(copy, muscle));
        Assert.That(copy.humanMotion, Is.EqualTo(run.humanMotion));
        Assert.That(LoopTime(copy), Is.True);
    }

    [Test] public void LoopTimeIsKeptOrForced()
    {
        AnimationClip Extracted(LoopOverride loop, string clip)
        {
            var result = AnimationExtraction.Extract(new[] { Job(clip, "Clip", loop.ToString()) }, new ExtractionOptions { Loop = loop }).Single();
            return Load(result.Path);
        }

        Assert.That(LoopTime(Extracted(LoopOverride.Keep, "Walk")), Is.False);
        Assert.That(LoopTime(Extracted(LoopOverride.On, "Walk")), Is.True);
        Assert.That(LoopTime(Extracted(LoopOverride.Off, "Armature|Run:Fast")), Is.False);
        Assert.That(LoopTime(walk), Is.False, "The source clip is left as it is.");
        Assert.That(LoopTime(run), Is.True, "The source clip is left as it is.");
    }

    [Test] public void ReplacingUpdatesTheExistingClipInPlaceSoItsReferencesSurvive()
    {
        string path = Extract(ExistingAnimation.Replace, Job("Walk")).Single().Path;
        var first = Load(path);
        string guid = AssetDatabase.AssetPathToGUID(path);
        string user = folder + "/User.controller";
        AnimatorController.CreateAnimatorControllerAtPath(user).AddMotion(first);
        AssetDatabase.SaveAssets();
        var sway = EditorCurveBinding.FloatCurve("Hips", typeof(Transform), "m_LocalPosition.x");
        AnimationUtility.SetEditorCurve(walk, sway, AnimationCurve.Constant(0, 1, 2));

        var result = Extract(ExistingAnimation.Replace, Job("Walk")).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Done), result.Message);
        Assert.That(result.Replaced, Is.True);
        Assert.That(result.Path, Is.EqualTo(path));
        Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
        Assert.That(Load(path), Is.SameAs(first), "Copied into the existing clip, not deleted and created again.");
        Assert.That(AnimationUtility.GetCurveBindings(first), Does.Contain(sway));
        Assert.That(first.name, Is.EqualTo("Walk"));
        Assert.That(EditorUtility.IsDirty(first), Is.False, "The replaced clip is saved.");
        Assert.That(AssetDatabase.GetDependencies(user, false), Does.Contain(path));
        Assert.That(AssetPaths.Exists(folder + "/Out/Nested/Walk 1.anim"), Is.False);
    }

    [Test] public void KeepBothSavesTheNewClipUnderANumberedName()
    {
        string first = Extract(ExistingAnimation.KeepBoth, Job("Walk")).Single().Path;
        var result = Extract(ExistingAnimation.KeepBoth, Job("Walk")).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Done), result.Message);
        Assert.That(result.Path, Is.EqualTo(folder + "/Out/Nested/Walk 1.anim"));
        Assert.That(Load(result.Path).name, Is.EqualTo("Walk 1"));
        Assert.That(Load(first), Is.Not.Null);
        Assert.That(AssetDatabase.AssetPathToGUID(result.Path), Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(first)));
    }

    [Test] public void SkipLeavesTheExistingClipAlone()
    {
        string path = Extract(ExistingAnimation.Skip, Job("Walk")).Single().Path;
        var before = File.ReadAllBytes(path);
        AnimationUtility.SetEditorCurve(walk, height, AnimationCurve.Constant(0, 1, 3));

        var result = Extract(ExistingAnimation.Skip, Job("Walk")).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Skipped));
        Assert.That(result.Path, Is.EqualTo(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.That(AssetPaths.Exists(folder + "/Out/Nested/Walk 1.anim"), Is.False);
    }

    [Test] public void ClipsGivenTheSameNameInOneExtractionNeverReplaceEachOther()
    {
        var results = Extract(ExistingAnimation.Replace, Job("Walk", "Move"), Job("Armature|Run:Fast", "Move"));

        CollectionAssert.AreEqual(new[] { folder + "/Out/Nested/Move.anim", folder + "/Out/Nested/Move 1.anim" }, results.Select(r => r.Path));
        Assert.That(results.All(r => r.Status == ExtractionStatus.Done && !r.Replaced), Is.True);
        Assert.That(AnimationUtility.GetCurveBindings(Load(results[1].Path)), Does.Contain(muscle));

        var again = Extract(ExistingAnimation.Replace, Job("Walk", "Move"), Job("Armature|Run:Fast", "Move"));

        CollectionAssert.AreEqual(results.Select(r => r.Path), again.Select(r => r.Path), "The same jobs land on the same files.");
        Assert.That(again.All(r => r.Replaced), Is.True);
    }

    [Test] public void ClipNamesBecomeValidFileNames()
    {
        Assert.That(AssetPaths.FileName("Armature|Run:Fast"), Is.EqualTo("Armature_Run_Fast"));
        Assert.That(AssetPaths.FileName("a/b\\c<d>e?f*g\"h"), Is.EqualTo("a_b_c_d_e_f_g_h"));
        Assert.That(AssetPaths.FileName("tab\there"), Is.EqualTo("tab_here"));
        Assert.That(AssetPaths.FileName("  Idle.. "), Is.EqualTo("Idle"));
        Assert.That(AssetPaths.FileName("CON"), Is.EqualTo("CON_"));
        Assert.That(AssetPaths.FileName("nul.take"), Is.EqualTo("nul_.take"));
        Assert.That(AssetPaths.FileName(" . ", "Animation"), Is.EqualTo("Animation"));

        var result = Extract(ExistingAnimation.Replace, Job("Armature|Run:Fast")).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Done), result.Message);
        Assert.That(result.Path, Is.EqualTo(folder + "/Out/Nested/Armature_Run_Fast.anim"));
        Assert.That(Load(result.Path).name, Is.EqualTo("Armature_Run_Fast"));
    }

    [Test] public void AMissingClipFailsWithoutStoppingTheOthers()
    {
        var results = Extract(ExistingAnimation.Replace, Job("Jump"), Job("Walk"));

        Assert.That(results[0].Status, Is.EqualTo(ExtractionStatus.Failed));
        Assert.That(results[0].Message, Does.Contain("Jump"));
        Assert.That(results[1].Status, Is.EqualTo(ExtractionStatus.Done), results[1].Message);
    }

    [Test] public void AnAnimFileIsAClipFileAndNeverReplacesItself()
    {
        string path = Extract(ExistingAnimation.Replace, Job("Walk", output: "Out")).Single().Path;
        var info = AnimationExtraction.Read(path);
        Assert.That(info.IsClipFile, Is.True);
        Assert.That(info.Clips.Select(c => c.Name), Is.EqualTo(new[] { "Walk" }));

        var result = AnimationExtraction.Extract(new[] { new ExtractionJob { Source = path, Clip = "Walk", Name = "Walk", Folder = folder + "/Out" } },
            new ExtractionOptions()).Single();

        Assert.That(result.Status, Is.EqualTo(ExtractionStatus.Skipped));
    }

    [Test] public void DefaultFolderAndNamesFollowTheModel()
    {
        Assert.That(AnimationExtraction.DefaultFolder("Assets/Models/Wolf.fbx"), Is.EqualTo("Assets/Models/Wolf Animations"));
        Assert.That(AnimationExtraction.DefaultFolder("Assets/Wolf.fbx"), Is.EqualTo("Assets/Wolf Animations"));
        var single = new AnimationSourceInfo { Name = "Running", Clips = { new AnimationClipInfo { Name = "mixamo.com" } } };
        Assert.That(AnimationExtraction.DefaultName(single, single.Clips[0]), Is.EqualTo("Running"), "Mixamo names every take mixamo.com.");
        var takes = new AnimationSourceInfo { Name = "Wolf", Clips = { new AnimationClipInfo { Name = "Armature|Howl" }, new AnimationClipInfo { Name = "Sit" } } };
        Assert.That(AnimationExtraction.DefaultName(takes, takes.Clips[0]), Is.EqualTo("Armature_Howl"));
    }

    [Test] public void ProjectPathsOfFilesOnDisk()
    {
        Assert.That(AssetPaths.FromFullPath(Path.GetFullPath(source)), Is.EqualTo(source));
        Assert.That(AssetPaths.FromFullPath(Path.GetFullPath(folder).Replace('/', '\\') + "\\"), Is.EqualTo(folder));
        Assert.That(AssetPaths.FromFullPath(Path.GetTempPath()), Is.Null);
        Assert.That(AssetPaths.FromFullPath(Path.GetDirectoryName(Application.dataPath)), Is.Null, "The project root is not an asset folder.");
    }

    [Test] public void FilesFromOutsideAreCopiedOnceAndThoseWithoutClipsAreRemovedAgain()
    {
        string plain = Extract(ExistingAnimation.Replace, Job("Walk", output: "A")).Single().Path;
        string looping = AnimationExtraction.Extract(new[] { Job("Walk", output: "B") }, new ExtractionOptions { Loop = LoopOverride.On }).Single().Path;
        external = Path.Combine(Path.GetTempPath(), "OrbitersAnimationExtraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        string dropped = Path.Combine(external, "Walk.anim"), notes = Path.Combine(external, "notes.txt");
        File.Copy(plain, dropped);
        File.WriteAllText(notes, "Not an animation.");
        string imports = folder + "/Imported";
        var problems = new List<string>();

        var imported = AnimationExtraction.ImportExternal(new[] { dropped, notes }, imports, problems);

        Assert.That(imported, Is.EqualTo(new[] { imports + "/Walk.anim" }));
        Assert.That(Load(imports + "/Walk.anim"), Is.Not.Null);
        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain("notes.txt"));
        Assert.That(AssetPaths.Exists(imports + "/notes.txt"), Is.False, "A copy holding no clip is removed again.");

        Assert.That(AnimationExtraction.ImportExternal(new[] { dropped }, imports, problems), Is.EqualTo(new[] { imports + "/Walk.anim" }), "The same file is not copied twice.");
        Assert.That(AssetPaths.Exists(imports + "/Walk 1.anim"), Is.False);

        File.Copy(looping, dropped, true);
        Assert.That(AnimationExtraction.ImportExternal(new[] { dropped }, imports, problems), Is.EqualTo(new[] { imports + "/Walk 1.anim" }), "Another file with that name gets a numbered name.");
    }
}
