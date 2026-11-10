using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The watcher's switch: an Orbiters feature (on by default, in the settings window), stored in the user's EditorPrefs;
/// off, it drops its notices and holds none, while tools that move objects themselves still ask.
/// </summary>
public sealed class AnimationPathWatchSwitchTests
{
    private Scene scene;
    private GameObject avatar;
    private Transform jacket;
    private string folder;
    private bool hadPref, prefBefore;
    private OrbitersFeature feature;

    [SetUp] public void SetUp()
    {
        feature = OrbitersFeatures.Find(AnimationPathWatch.Feature);
        Assert.IsNotNull(feature, "the watcher registers its switch");
        // The user's own choice comes back after the test.
        hadPref = EditorPrefs.HasKey(feature.PrefKey);
        prefBefore = EditorPrefs.GetBool(feature.PrefKey, feature.Default);
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Switch avatar");
        SceneManager.MoveGameObjectToScene(avatar, scene);
        jacket = new GameObject("Jacket").transform;
        jacket.SetParent(avatar.transform, false);
        folder = "Assets/OrbitersPathWatchSwitchTests_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/fx.controller");
        var clip = new AnimationClip { name = "jacket" };
        AssetDatabase.CreateAsset(clip, folder + "/jacket.anim");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Jacket", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
        controller.layers[0].stateMachine.AddState("Play").motion = clip;
        avatar.AddComponent<Animator>().runtimeAnimatorController = controller;
    }

    [TearDown] public void TearDown()
    {
        AnimationPathWatch.Accept(avatar);
        if (hadPref) OrbitersFeatures.SetEnabled(AnimationPathWatch.Feature, prefBefore);
        else { OrbitersFeatures.SetEnabled(AnimationPathWatch.Feature, feature.Default); EditorPrefs.DeleteKey(feature.PrefKey); }
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
    }

    private AnimationPathWatch.Notice Rename()
    {
        string former = AnimationUtility.CalculateTransformPath(jacket, avatar.transform);
        jacket.name = jacket.name == "Jacket" ? "Coat" : "Jacket";
        return AnimationPathWatch.Check(avatar, new[] { (jacket, former) });
    }

    [Test] public void TheSwitchIsAStableFeatureOnByDefault()
    {
        Assert.AreEqual(FeatureStage.Stable, feature.Stage);
        Assert.IsTrue(feature.Default);
        Assert.AreEqual("Toolkit", feature.Product);
        StringAssert.Contains("rename", feature.Label.ToLowerInvariant());
        EditorPrefs.DeleteKey(feature.PrefKey);
        Assert.IsTrue(OrbitersFeatures.IsEnabled(AnimationPathWatch.Feature));
    }

    [Test] public void SwitchingItOffDropsTheNoticesAndStoresTheChoice()
    {
        AnimationPathWatch.Enabled = true;
        var notice = Rename();
        Assert.IsNotNull(notice);
        Assert.AreSame(notice, AnimationPathWatch.For(avatar));
        bool told = false;
        Action listen = () => told = true;
        AnimationPathWatch.Changed += listen;
        try { AnimationPathWatch.Enabled = false; }
        finally { AnimationPathWatch.Changed -= listen; }
        Assert.IsTrue(told, "the card and Mixer hear that the notice went");
        Assert.IsFalse(AnimationPathWatch.Enabled);
        Assert.IsNull(AnimationPathWatch.For(avatar));
        Assert.IsFalse(AnimationPathWatch.Notices.Any(n => n.Avatar == avatar));
        Assert.IsFalse(EditorPrefs.GetBool(feature.PrefKey, true), "kept in the user's EditorPrefs");
        // Through the settings window's own path too.
        OrbitersFeatures.SetEnabled(AnimationPathWatch.Feature, true);
        Assert.IsTrue(AnimationPathWatch.Enabled);
    }

    // Component edits rename nothing and are ignored, except on an avatar holding a notice: another controller on its
    // Animator may play the right paths, and the notice is checked again.
    [Test] public void AComponentChangeChecksOnlyAnAvatarHoldingANotice()
    {
        AnimationPathWatch.Enabled = true;
        var animator = avatar.GetComponent<Animator>();
        Assert.IsTrue(AnimationPathWatch.Marks(jacket.gameObject), "a GameObject's name holds a path");
        Assert.IsFalse(AnimationPathWatch.Marks(animator), "no notice: a component's change renames nothing");
        var notice = Rename();
        Assert.IsNotNull(notice);
        Assert.IsTrue(AnimationPathWatch.Marks(animator), "the controller it plays may have changed");
        Assert.IsFalse(AnimationPathWatch.Marks(jacket), "a transform moved changes no path");
        AnimationPathWatch.Leave(notice);
        Assert.IsFalse(AnimationPathWatch.Marks(animator));
    }

    [Test] public void OffAToolsOwnMoveStillAsks()
    {
        AnimationPathWatch.Enabled = false;
        var notice = Rename();
        Assert.IsNotNull(notice, "Mixer's rename asks whatever the watcher's switch");
        AnimationPathWatch.Leave(notice);
    }
}
