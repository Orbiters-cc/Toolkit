using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

public sealed class AnimationPathsTests
{
    private Scene scene;
    private GameObject avatar;
    private Transform jacket, sleeve, speaker, body;
    private AnimatorController fx;
    private AnimatorState state;
    private AnimationClip clip;
    private AvatarMask mask;
    private VRCAnimatorPlayAudio audio;
    private string folder;
    private readonly List<string> readOnly = new List<string>();

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Paths avatar");
        SceneManager.MoveGameObjectToScene(avatar, scene);
        var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
        body = Child(avatar.transform, "Body");
        jacket = Child(avatar.transform, "Jacket");
        sleeve = Child(jacket, "Sleeve");
        speaker = Child(jacket, "Speaker");
        speaker.gameObject.AddComponent<AudioSource>();
        folder = "Assets/OrbitersAnimationPathsTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        fx = AnimatorController.CreateAnimatorControllerAtPath(folder + "/fx.controller");
        clip = NewClip("jacket");
        Curve(clip, "Jacket", typeof(GameObject), "m_IsActive");
        Curve(clip, "Jacket/Sleeve", typeof(Transform), "m_LocalPosition.x");
        state = fx.layers[0].stateMachine.AddState("Play");
        state.motion = clip;
        audio = state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>();
        audio.SourcePath = "Jacket/Speaker";
        mask = new AvatarMask { transformCount = 2 };
        mask.SetTransformPath(0, "Jacket"); mask.SetTransformActive(0, true);
        mask.SetTransformPath(1, "Jacket/Sleeve"); mask.SetTransformActive(1, true);
        AssetDatabase.CreateAsset(mask, folder + "/jacket.mask");
        descriptor.baseAnimationLayers = new[]
        {
            new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx, mask = mask, isDefault = false }
        };
        descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
        AssetDatabase.SaveAssets();
    }

    [TearDown] public void TearDown()
    {
        foreach (var path in readOnly)
            if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        readOnly.Clear();
        AnimationPathWatch.Accept(avatar);
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
    }

    private static Transform Child(Transform parent, string name)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); return t;
    }

    private AnimationClip NewClip(string name)
    {
        var made = new AnimationClip { name = name };
        AssetDatabase.CreateAsset(made, folder + "/" + name + ".anim");
        return made;
    }

    private static void Curve(AnimationClip target, string path, Type type, string property) =>
        AnimationUtility.SetEditorCurve(target, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, 1));

    private static string[] Paths(AnimationClip target) => AnimationUtility.GetCurveBindings(target).Select(b => b.path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private static string[] MaskPaths(AvatarMask target) => Enumerable.Range(0, target.transformCount).Select(target.GetTransformPath).ToArray();

    private RepathPlan Rename(Transform target, string name)
    {
        string former = AnimationUtility.CalculateTransformPath(target, avatar.transform);
        target.name = name;
        return AnimationPaths.Plan(avatar, new[] { (target, former) }, broken: false);
    }

    [Test] public void RenamingAParentRepathsItsChildrenClipsMasksAndStateAudio()
    {
        var plan = Rename(jacket, "Coat");
        Assert.AreEqual(5, plan.Fixable, plan.ToString());
        Assert.AreEqual(3, plan.FixableAssets, plan.ToString());
        var applied = AnimationPaths.Apply(plan);
        Assert.AreEqual(5, applied.Paths);
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, Paths(clip));
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, MaskPaths(mask));
        Assert.AreEqual("Coat/Speaker", audio.SourcePath);
        Assert.AreEqual(0, AnimationPaths.Broken(avatar).Changes.Count());
    }

    [Test] public void ReparentingKeepsTheBindingOnTheObject()
    {
        string former = AnimationUtility.CalculateTransformPath(sleeve, avatar.transform);
        sleeve.SetParent(body, false);
        var plan = AnimationPaths.Plan(avatar, new[] { (sleeve, former) }, broken: false);
        AnimationPaths.Apply(plan);
        CollectionAssert.AreEqual(new[] { "Body/Sleeve", "Jacket" }, Paths(clip));
        CollectionAssert.AreEqual(new[] { "Jacket", "Body/Sleeve" }, MaskPaths(mask));
    }

    [Test] public void UnrelatedChangesPlanNothing()
    {
        var plan = Rename(body, "Torso");
        Assert.AreEqual(0, plan.Fixable, plan.ToString());
    }

    // A clip's paths are kept between plans while the clip is unchanged: one edited since is read again.
    [Test] public void AClipEditedAfterAPlanIsReadAgain()
    {
        Assert.AreEqual(0, AnimationPaths.Broken(avatar).Changes.Count());
        int paths = AnimationPaths.CountPaths(avatar);
        Assert.AreEqual(paths, AnimationPaths.CountPaths(avatar), "the same answer from the kept paths");
        Curve(clip, "Gone/Thing", typeof(GameObject), "m_IsActive");
        Assert.That(AnimationPaths.Broken(avatar).Changes.Select(c => c.From), Has.Member("Gone/Thing"));
        Assert.AreEqual(paths + 1, AnimationPaths.CountPaths(avatar));
    }

    // The watcher keeps the hierarchy it compared with as the next snapshot: the same as reading it again.
    [Test] public void TheHierarchyAComparisonReadIsTheSnapshotOfNow()
    {
        var before = PathSnapshot.Of(avatar.transform);
        jacket.name = "Coat";
        var moves = before.Moves(out var now);
        Assert.That(moves.Select(m => m.FormerPath), Is.EquivalentTo(new[] { "Jacket", "Jacket/Sleeve", "Jacket/Speaker" }));
        CollectionAssert.AreEquivalent(PathSnapshot.Of(avatar.transform).Paths.ToList(), now.Paths.ToList());
        Assert.That(now.TryGet("Coat/Sleeve", out var found) && found == sleeve);
    }

    [Test] public void DuplicateSiblingNamesResolveToTheFirst()
    {
        var hat = Child(avatar.transform, "Hat");
        var other = Child(avatar.transform, "Hat");
        var scarf = Child(avatar.transform, "Scarf");
        Curve(clip, "Hat", typeof(GameObject), "m_IsActive");
        Curve(clip, "Scarf", typeof(GameObject), "m_IsActive");
        // The second "Hat" is renamed: the clip's "Hat" was the first one, it still is.
        Assert.AreEqual(0, Rename(other, "Cap").Fixable);
        other.name = "Hat";
        // The first one renamed: "Hat" now reaches the second one, the binding follows the first.
        var plan = Rename(hat, "Beret");
        var change = plan.Changes.Single(c => c.From == "Hat");
        Assert.AreEqual("Beret", change.To);
        Assert.AreSame(hat, change.Target);
        hat.name = "Hat";
        // Renamed after a sibling of that name: the new path would reach the sibling first.
        var ambiguous = Rename(scarf, "Hat").Changes.Single(c => c.From == "Scarf");
        Assert.IsFalse(ambiguous.Fixable);
        StringAssert.Contains("comes first", ambiguous.Problem);
    }

    [Test] public void UndoRestoresEveryAsset()
    {
        var plan = Rename(jacket, "Coat");
        Undo.IncrementCurrentGroup();
        AnimationPaths.Apply(plan, undoName: "Paths test");
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, Paths(clip));
        Undo.PerformUndo();
        CollectionAssert.AreEqual(new[] { "Jacket", "Jacket/Sleeve" }, Paths(clip));
        CollectionAssert.AreEqual(new[] { "Jacket", "Jacket/Sleeve" }, MaskPaths(mask));
        Assert.AreEqual("Jacket/Speaker", audio.SourcePath);
    }

    [Test] public void ASharedClipIsCopiedForThisAvatarOnly()
    {
        var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/other.controller");
        other.layers[0].stateMachine.AddState("Also").motion = clip;
        AssetDatabase.SaveAssets();
        var plan = Rename(jacket, "Coat");
        Assert.IsTrue(plan.Shared, plan.ToString());
        var entry = plan.Assets.Single(a => a.Asset == clip);
        CollectionAssert.Contains(entry.SharedWith, "other");
        var applied = AnimationPaths.Apply(plan);
        Assert.AreEqual(1, applied.Copies);
        // The original stays as it was for the other controller; this avatar's state plays the fixed copy.
        CollectionAssert.AreEqual(new[] { "Jacket", "Jacket/Sleeve" }, Paths(clip));
        var played = (AnimationClip)state.motion;
        Assert.AreNotSame(clip, played);
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, Paths(played));
        Assert.AreSame(clip, other.layers[0].stateMachine.states[0].state.motion);
    }

    [Test] public void ASharedClipFixedEverywhereChangesInPlace()
    {
        var other = AnimatorController.CreateAnimatorControllerAtPath(folder + "/other.controller");
        other.layers[0].stateMachine.AddState("Also").motion = clip;
        AssetDatabase.SaveAssets();
        var applied = AnimationPaths.Apply(Rename(jacket, "Coat"), everywhere: true);
        Assert.AreEqual(0, applied.Copies);
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, Paths(clip));
        Assert.AreSame(clip, state.motion);
    }

    [Test] public void LockedAssetsAreListedNeverChanged()
    {
        var packaged = AssetDatabase.LoadAssetAtPath<AnimationClip>("Packages/orbiters.toolkit/Editor/Photoshoot/BodyPoses/bow_pose.anim");
        Assert.IsNotNull(packaged, "fixture clip in the Toolkit package");
        Assert.AreEqual("it is in a package", AnimationPaths.Locked(packaged));
        string file = Path.GetFullPath(AssetDatabase.GetAssetPath(clip));
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        readOnly.Add(file);
        var plan = Rename(jacket, "Coat");
        var entry = plan.Assets.Single(a => a.Asset == clip);
        Assert.AreEqual("the file is read-only", entry.Locked);
        var applied = AnimationPaths.Apply(plan);
        CollectionAssert.AreEqual(new[] { "Jacket", "Jacket/Sleeve" }, Paths(clip));
        Assert.IsTrue(applied.Skipped.Any(s => s.StartsWith(clip.name, StringComparison.Ordinal)), string.Join("; ", applied.Skipped));
        // The mask and the state audio aren't locked: they follow.
        Assert.AreEqual("Coat/Speaker", audio.SourcePath);
    }

    [Test] public void BrokenPathsGuessTheOneObjectOfThatName()
    {
        var clothes = Child(avatar.transform, "Clothes");
        Child(clothes, "Boots");
        Curve(clip, "Old/Boots", typeof(GameObject), "m_IsActive");
        Curve(clip, "Gone", typeof(GameObject), "m_IsActive");
        var plan = AnimationPaths.Broken(avatar);
        var boots = plan.Changes.Single(c => c.From == "Old/Boots");
        Assert.IsTrue(boots.WasBroken);
        Assert.AreEqual("Clothes/Boots", boots.To);
        var gone = plan.Changes.Single(c => c.From == "Gone");
        Assert.IsFalse(gone.Fixable);
        Child(body, "Boots");
        Assert.IsFalse(AnimationPaths.Broken(avatar).Changes.Single(c => c.From == "Old/Boots").Fixable, "two objects named Boots, equally close");
        // The shared audit sees the same clip curves from the same root.
        var audit = BindingAudit.Of(avatar);
        Assert.AreEqual(4, audit.Bindings);
        CollectionAssert.AreEquivalent(new[] { "Old/Boots", "Gone" }, audit.Missing.Select(m => m.Binding.path).ToArray());
    }

    [Test] public void AMissingComponentIsBroken()
    {
        Curve(clip, "Body", typeof(SkinnedMeshRenderer), "blendShape.Smile");
        var change = AnimationPaths.Broken(avatar).Changes.Single(c => c.From == "Body");
        Assert.IsFalse(change.Fixable);
    }

    [Test] public void VrcFuryFullControllerPathsStartAtItsObjectAndLookUp()
    {
        var api = Type.GetType("com.vrcfury.api.FuryComponents, com.vrcfury.api");
        if (api == null) Assert.Ignore("VRCFury isn't installed.");
        var prop = Child(avatar.transform, "Prop");
        var light = Child(prop, "Light");
        var merged = AnimatorController.CreateAnimatorControllerAtPath(folder + "/prop.controller");
        var propClip = NewClip("prop");
        Curve(propClip, "Light", typeof(GameObject), "m_IsActive");
        Curve(propClip, "Body", typeof(GameObject), "m_IsActive");
        merged.layers[0].stateMachine.AddState("Prop").motion = propClip;
        var full = api.GetMethod("CreateFullController").Invoke(null, new object[] { prop.gameObject });
        full.GetType().GetMethod("AddController").Invoke(full, new object[] { merged, VRCAvatarDescriptor.AnimLayerType.FX });
        Assert.AreEqual(0, AnimationPaths.Broken(avatar).Changes.Count(c => c.From == "Light" || c.From == "Body"), "resolved from the component's object, then above it");
        string former = AnimationUtility.CalculateTransformPath(light, avatar.transform);
        light.name = "Lamp";
        string formerBody = AnimationUtility.CalculateTransformPath(body, avatar.transform);
        body.name = "Torso";
        var plan = AnimationPaths.Plan(avatar, new[] { (light, former), (body, formerBody) }, broken: false);
        AnimationPaths.Apply(plan);
        CollectionAssert.AreEqual(new[] { "Lamp", "Torso" }, Paths(propClip));
    }

    [Test] public void ModularAvatarRelativePathsStartAtTheirPathRoot()
    {
        var prop = Child(avatar.transform, "Prop");
        var light = Child(prop, "Light");
        var merged = AnimatorController.CreateAnimatorControllerAtPath(folder + "/ma.controller");
        var propClip = NewClip("ma");
        Curve(propClip, "Light", typeof(GameObject), "m_IsActive");
        merged.layers[0].stateMachine.AddState("Prop").motion = propClip;
        var holder = Child(avatar.transform, "Merge");
        // Unity adds no MonoBehaviour of an editor assembly to an object: the stand-in holds Merge Animator's fields, and
        // the VRChat part reads it as it reads the component on the holder.
        var merge = ScriptableObject.CreateInstance<nadena.dev.modular_avatar.core.tests.FakeModularAvatarMerge>();
        merge.animator = merged;
        merge.relativePathRoot = new nadena.dev.modular_avatar.core.tests.FakeModularAvatarMerge.Reference { referencePath = "Prop" };
        Func<GameObject, IEnumerable<AnimationSource>> provider = root =>
            root == avatar ? Orbiters.Toolkit.Editor.VRChat.AnimationPathsVrchat.ToolSources(merge, holder, false) : Enumerable.Empty<AnimationSource>();
        AnimationPaths.AddSources(provider);
        try
        {
            Assert.AreEqual(0, AnimationPaths.Broken(avatar).Changes.Count(c => c.From == "Light"));
            string former = AnimationUtility.CalculateTransformPath(light, avatar.transform);
            light.name = "Lamp";
            AnimationPaths.Apply(AnimationPaths.Plan(avatar, new[] { (light, former) }, broken: false));
            CollectionAssert.AreEqual(new[] { "Lamp" }, Paths(propClip));
            merge.pathMode = nadena.dev.modular_avatar.core.tests.FakeModularAvatarMerge.MergeAnimatorPathMode.Absolute;
            Assert.AreEqual(1, AnimationPaths.Broken(avatar).Changes.Count(c => c.From == "Lamp"), "absolute: from the avatar's root");
        }
        finally
        {
            AnimationPaths.RemoveSources(provider);
            Object.DestroyImmediate(merge);
        }
    }

    [Test] public void AJoinMovesShapesSwitchesAndTogglesToTheJoinedRenderer()
    {
        var a = Child(avatar.transform, "A").gameObject.AddComponent<SkinnedMeshRenderer>();
        var b = Child(avatar.transform, "B").gameObject.AddComponent<MeshRenderer>();
        var joined = Child(avatar.transform, "A joined").gameObject.AddComponent<SkinnedMeshRenderer>();
        var join = NewClip("join");
        Curve(join, "A", typeof(SkinnedMeshRenderer), "blendShape.Smile");
        Curve(join, "A", typeof(GameObject), "m_IsActive");
        Curve(join, "B", typeof(MeshRenderer), "m_Enabled");
        fx.layers[0].stateMachine.AddState("Join").motion = join;
        var retargets = new[] { a, (Renderer)b }.Select(r => new Retarget { From = r.transform, To = joined.transform }).ToList();
        var plan = AnimationPaths.Plan(avatar, (PathSnapshot)null, retargets, broken: false);
        Assert.AreEqual(3, plan.Fixable, plan.ToString());
        AnimationPaths.Apply(plan);
        var bindings = AnimationUtility.GetCurveBindings(join);
        Assert.IsTrue(bindings.All(x => x.path == "A joined"), string.Join(", ", bindings.Select(x => x.path)));
        Assert.IsTrue(bindings.Any(x => x.propertyName == "m_Enabled" && x.type == typeof(SkinnedMeshRenderer)));
        var toggles = AnimationPaths.Toggles(avatar, new Renderer[] { a, b });
        CollectionAssert.AreEquivalent(toggles[a], toggles[b], "nothing toggles A or B any more");
        Curve(join, "A", typeof(GameObject), "m_IsActive");
        AnimationPaths.Invalidate();
        Assert.AreNotEqual(AnimationPaths.Toggles(avatar, new Renderer[] { a, b })[a].Count, AnimationPaths.Toggles(avatar, new Renderer[] { a, b })[b].Count);
    }

    [Test] public void AToolsMoveAsksThenFixesAndLeaves()
    {
        string former = AnimationUtility.CalculateTransformPath(jacket, avatar.transform);
        jacket.name = "Coat";
        var notice = AnimationPathWatch.Check(avatar, new[] { (jacket, former) });
        Assert.IsNotNull(notice);
        Assert.AreEqual(3, notice.Plan.FixableAssets);
        StringAssert.Contains("Renaming “Jacket”", notice.Text);
        Assert.AreSame(notice, AnimationPathWatch.For(avatar));
        AnimationPathWatch.Fix(notice);
        Assert.IsNull(AnimationPathWatch.For(avatar));
        CollectionAssert.AreEqual(new[] { "Coat", "Coat/Sleeve" }, Paths(clip));
        // Nothing broken: no notice.
        former = AnimationUtility.CalculateTransformPath(body, avatar.transform);
        body.name = "Torso";
        Assert.IsNull(AnimationPathWatch.Check(avatar, new[] { (body, former) }));
    }

    [Test] public void SnapshotMovesListRenamedObjectsAndTheirChildren()
    {
        var before = AnimationPaths.Snapshot(avatar);
        jacket.name = "Coat";
        var moves = before.Moves();
        CollectionAssert.AreEquivalent(new[] { "Jacket", "Jacket/Sleeve", "Jacket/Speaker" }, moves.Select(m => m.FormerPath).ToArray());
        var rebuilt = PathSnapshot.Before(avatar.transform, new[] { (jacket, "Jacket") });
        Assert.IsTrue(rebuilt.TryGet("Jacket/Sleeve", out var t) && t == sleeve);
    }

    [Test] public void TheChecksEntryFixesWhatIsBrokenAsBuiltToo()
    {
        var clothes = Child(avatar.transform, "Clothes");
        Child(clothes, "Boots");
        Curve(clip, "Old/Boots", typeof(GameObject), "m_IsActive");
        var built = Object.Instantiate(avatar);
        SceneManager.MoveGameObjectToScene(built, scene);
        var issues = BrokenBindings.Find(avatar, built);
        var issue = issues.Single(i => i.Fix != null);
        StringAssert.Contains("Clothes/Boots", issue.Detail);
        StringAssert.StartsWith("Fixed 1", issue.Fix());
        Assert.IsTrue(Paths(clip).Contains("Clothes/Boots"));
        // Broken on the avatar but there once built (a build tool makes it): not shown.
        Curve(clip, "Made", typeof(GameObject), "m_IsActive");
        Child(built.transform, "Made");
        Assert.IsFalse(BrokenBindings.Find(avatar, built).Any(i => (i.Where ?? "").Contains("Made")));
    }
}
