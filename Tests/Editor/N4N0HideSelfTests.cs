using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

// Recording without the wearer (contracts Docs/rig-frames.md): the layer the build adds, run on Unity's own Animator, the
// one VRChat runs. A stuck invisible avatar is the failure this layer must never have, so most of this is how it lets go.
public class N4N0HideSelfTests
{
    private const float Frame = 1f / 60;
    private string folder;
    private readonly List<Object> owned = new List<Object>();

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/__OrbitersHideSelfTest-" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        AssetDatabase.DeleteAsset(folder);
    }

    private sealed class Avatar
    {
        public GameObject Root;
        public Animator Animator;
        public VRCAvatarDescriptor Descriptor;
        public Renderer Body, Off, Skinned, Particles, Trail, Line, Inactive, Toggled, Sparse, Kit, Rig;
        public AnimatorController Source;
        /// <summary>As VRChat sets a parameter: whatever the controller's type for it.</summary>
        public void Set(string name, bool value)
        {
            var parameter = Animator.parameters.First(p => p.name == name);
            if (parameter.type == AnimatorControllerParameterType.Float) Animator.SetFloat(name, value ? 1 : 0);
            else Animator.SetBool(name, value);
        }
        public void Step(float seconds)
        {
            for (float t = 0; t < seconds; t += Frame) Animator.Update(Frame);
        }
        public IEnumerable<Renderer> Hideable => new[] { Body, Skinned, Particles, Trail, Line, Inactive };
    }

    private static T Part<T>(GameObject root, string path, bool enabled = true, bool active = true) where T : Renderer
    {
        var t = root.transform;
        foreach (string name in path.Split('/'))
        {
            var next = t.Find(name);
            if (next == null) { next = new GameObject(name).transform; next.SetParent(t, false); }
            t = next;
        }
        var r = t.gameObject.AddComponent<T>();
        r.enabled = enabled;
        t.gameObject.SetActive(active);
        return r;
    }

    private static AnimationClip Enabled(AnimatorController controller, string name, string path, Type type, float value)
    {
        var clip = new AnimationClip { name = name };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, "m_Enabled"), AnimationCurve.Constant(0, Frame, value));
        AssetDatabase.AddObjectToAsset(clip, controller);
        return clip;
    }

    private static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion, bool writeDefaults)
    {
        var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = writeDefaults; return state;
    }

    private static void Link(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, string parameter)
    {
        var t = from.AddTransition(to); t.hasExitTime = false; t.duration = 0; t.AddCondition(mode, 0, parameter);
    }

    /// <summary>
    /// An avatar as a build leaves it: every kind of renderer, N4N0's capture part, and an FX controller whose own layer
    /// animates <c>Toggled</c> (both its states write it) and <c>Sparse</c> (its "off" state alone writes it). All its
    /// states with Write Defaults as <paramref name="writeDefaults"/> says: real avatars have both.
    /// </summary>
    private Avatar Make(bool writeDefaults, bool floats = false)
    {
        var root = new GameObject("Hide self test avatar"); owned.Add(root);
        var a = new Avatar { Root = root };
        a.Body = Part<MeshRenderer>(root, "Body");
        a.Off = Part<MeshRenderer>(root, "Off by default", enabled: false);
        a.Skinned = Part<SkinnedMeshRenderer>(root, "Armature/Hips/Skirt");
        a.Particles = Part<ParticleSystemRenderer>(root, "Armature/Hips/Sparks");
        a.Trail = Part<TrailRenderer>(root, "Armature/Hips/Trail");
        a.Line = Part<LineRenderer>(root, "Line");
        a.Inactive = Part<MeshRenderer>(root, "Inactive prop", active: false);
        a.Toggled = Part<MeshRenderer>(root, "Toggled");
        a.Sparse = Part<MeshRenderer>(root, "Sparse");
        // What FindAll knows N4N0 by, with its capture part.
        var n4n0 = N4N0Installer.CreatePrefab(folder);
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(n4n0));
        PrefabUtility.UnpackPrefabInstance(placed, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        placed.transform.SetParent(root.transform, false);
        a.Kit = placed.transform.Find(N4N0Installer.CaptureKitPath).GetComponent<Renderer>();
        a.Rig = placed.transform.Find(N4N0Installer.RigRendererPath).GetComponent<Renderer>();

        a.Animator = root.AddComponent<Animator>();
        a.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        a.Descriptor = root.AddComponent<VRCAvatarDescriptor>();
        var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
        controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
        // VRCFury's build turns FX's bools into floats: the sample avatar's IsLocal and Record are, in its upload.
        foreach (string p in new[] { N4N0Installer.CaptureRecord, "IsLocal" }) controller.AddParameter(p, floats ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool);
        controller.AddLayer("A toggle of the avatar's");
        var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
        var machine = controller.layers[1].stateMachine;
        var on = State(machine, "On", Enabled(controller, "toggled on", "Toggled", typeof(MeshRenderer), 1), writeDefaults);
        var offClip = new AnimationClip { name = "toggled off" };
        AnimationUtility.SetEditorCurve(offClip, EditorCurveBinding.FloatCurve("Toggled", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, Frame, 0));
        AnimationUtility.SetEditorCurve(offClip, EditorCurveBinding.FloatCurve("Sparse", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, Frame, 0));
        AssetDatabase.AddObjectToAsset(offClip, controller);
        var off = State(machine, "Off", offClip, writeDefaults);
        machine.defaultState = on;
        Link(on, off, AnimatorConditionMode.IfNot, "Toggle");
        Link(off, on, AnimatorConditionMode.If, "Toggle");
        a.Source = controller;
        a.Descriptor.baseAnimationLayers = new[]
        {
            new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false },
        };
        a.Descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
        var list = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        list.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Toggle", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true } };
        AssetDatabase.CreateAsset(list, folder + "/Parameters.asset");
        a.Descriptor.expressionParameters = list;

        N4N0Installer.BuildHideSelf(root, false, folder + "/Kits");
        a.Animator.runtimeAnimatorController = a.Descriptor.baseAnimationLayers[0].animatorController;
        a.Animator.Rebind();
        a.Set("Toggle", true);
        a.Set("IsLocal", true);
        a.Step(0.1f);
        return a;
    }

    private static void Hides(Avatar a)
    {
        a.Set(N4N0Installer.CaptureRecord, true);
        a.Set(N4N0Installer.CaptureHideSelf, false);
        a.Step(0.1f);
        a.Set(N4N0Installer.CaptureHideSelf, true);
        a.Step(0.1f);
    }

    /// <summary>What a beat of the app does, four times a second, for <paramref name="seconds"/>.</summary>
    private static void Beats(Avatar a, float seconds)
    {
        for (float t = 0; t < seconds; t += 0.25f)
        {
            a.Set(N4N0Installer.CaptureHideSelf, !a.Animator.GetBool(N4N0Installer.CaptureHideSelf));
            a.Step(0.25f);
        }
    }

    private static void AssertHidden(Avatar a, string when)
    {
        foreach (var r in a.Hideable.Append(a.Toggled).Append(a.Sparse).Append(a.Off)) Assert.IsFalse(r.enabled, r.name + " " + when);
    }

    private static void AssertShown(Avatar a, string when)
    {
        foreach (var r in a.Hideable.Append(a.Toggled).Append(a.Sparse)) Assert.IsTrue(r.enabled, r.name + " " + when);
        Assert.IsFalse(a.Off.enabled, "off by default stays off " + when);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HidesEveryRendererWhileBeatingAndShowsThemAgainWhenTheRecordingEnds(bool writeDefaults)
    {
        var a = Make(writeDefaults);
        AssertShown(a, "before");
        Hides(a);
        AssertHidden(a, "while hidden");
        Beats(a, 10f);
        AssertHidden(a, "after ten seconds of beats");
        a.Set(N4N0Installer.CaptureRecord, false);
        a.Step(0.05f);
        a.Step(0.2f);
        AssertShown(a, "as the recording ends");
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void AnAppThatDiesLeavesTheAvatarShownWithinTwoSecondsAndHiddenNeverAgain(bool writeDefaults, bool stuckOn)
    {
        var a = Make(writeDefaults);
        Hides(a);
        Beats(a, 2f);
        // No more flips, the value stuck where it is, Record stuck on.
        a.Set(N4N0Installer.CaptureHideSelf, stuckOn);
        a.Step(0.3f);
        AssertHidden(a, "for a moment without beats: a dropped datagram shows nothing");
        a.Step(1.7f);
        AssertShown(a, "two seconds after the last beat");
        a.Step(30f);
        AssertShown(a, "and it stays that way");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheAvatarsOwnTogglesStillWorkAfterwardsAndWhileHiddenStayHidden(bool writeDefaults)
    {
        var a = Make(writeDefaults);
        a.Set("Toggle", false);
        a.Step(0.1f);
        Assert.IsFalse(a.Toggled.enabled, "the toggle works before");
        a.Set("Toggle", true);
        a.Step(0.1f);
        Assert.IsTrue(a.Toggled.enabled);
        Hides(a);
        a.Set("Toggle", false);
        Beats(a, 0.5f);
        Assert.IsFalse(a.Toggled.enabled);
        a.Set("Toggle", true);
        Beats(a, 0.5f);
        Assert.IsFalse(a.Toggled.enabled, "the toggle turned on while hidden does not show it");
        a.Set(N4N0Installer.CaptureRecord, false);
        a.Step(0.3f);
        Assert.IsTrue(a.Toggled.enabled, "as the toggle says, after");
        a.Set("Toggle", false);
        a.Step(0.1f);
        Assert.IsFalse(a.Toggled.enabled, "the toggle still works");
        a.Set("Toggle", true);
        a.Step(0.1f);
        Assert.IsTrue(a.Toggled.enabled, "both ways");
    }

    /// <summary>The layer on a controller VRCFury has built, whose IsLocal and Record are floats: found on the real avatar's build.</summary>
    [Test]
    public void ItWorksWhereVrcFuryTurnedTheBoolsIntoFloats()
    {
        var a = Make(false, floats: true);
        AssertShown(a, "before");
        Hides(a);
        AssertHidden(a, "while hidden");
        Beats(a, 2f);
        AssertHidden(a, "with beats");
        a.Set(N4N0Installer.CaptureRecord, false);
        a.Step(0.3f);
        AssertShown(a, "as the recording ends");
        // And on another client, nothing.
        a.Set("IsLocal", false);
        Hides(a);
        Beats(a, 1f);
        AssertShown(a, "on another client");
    }

    [Test]
    public void ARendererAnotherLayerWritesOnlyNowAndThenComesBackToo()
    {
        var a = Make(false);
        Hides(a);
        Beats(a, 1f);
        a.Set(N4N0Installer.CaptureRecord, false);
        a.Step(0.3f);
        Assert.IsTrue(a.Sparse.enabled, "no layer writes it in this state: the way back must");
    }

    [Test]
    public void TheKitAndTheRigAreNeverTouched()
    {
        var a = Make(false);
        var fx = (AnimatorController)a.Descriptor.baseAnimationLayers[0].animatorController;
        var layer = fx.layers.Single(l => l.name == N4N0Installer.HideSelfLayer);
        foreach (var state in layer.stateMachine.states)
            foreach (var binding in AnimationUtility.GetCurveBindings((AnimationClip)state.state.motion))
                StringAssert.DoesNotContain(N4N0Installer.Capture + "/", binding.path);
        Assert.AreEqual(fx.layers.Length - 1, Array.FindIndex(fx.layers, l => l.name == N4N0Installer.HideSelfLayer), "the last layer: nothing overrides it");
        Assert.AreEqual(1f, layer.defaultWeight);
    }

    [Test]
    public void OnAnyOtherClientNothingEverHides()
    {
        var a = Make(false);
        a.Set("IsLocal", false);
        a.Set(N4N0Installer.CaptureRecord, true);
        a.Set(N4N0Installer.CaptureHideSelf, false);
        a.Step(0.1f);
        a.Set(N4N0Installer.CaptureHideSelf, true);
        a.Step(0.1f);
        Beats(a, 1f);
        AssertShown(a, "on another client, even with its values forced");
    }

    [Test]
    public void TheParameterIsLocalAndCostsNoSyncedBitsAndAProjectsAssetsStayAsTheyWere()
    {
        var a = Make(false);
        var list = a.Descriptor.expressionParameters;
        var parameter = list.FindParameter(N4N0Installer.CaptureHideSelf);
        Assert.IsNotNull(parameter);
        Assert.IsFalse(parameter.networkSynced, "local");
        Assert.IsFalse(parameter.saved, "never saved");
        Assert.AreEqual(1, list.CalcTotalCost(), "the avatar's one synced Bool: no more");
        var project = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.IsNull(project.FindParameter(N4N0Installer.CaptureHideSelf), "the project's list");
        Assert.IsFalse(a.Source.layers.Any(l => l.name == N4N0Installer.HideSelfLayer), "the project's controller");
        Assert.AreNotSame(a.Source, a.Descriptor.baseAnimationLayers[0].animatorController);
    }

    [Test]
    public void TheAvatarsRankDoesNotChange()
    {
        var a = Make(false);
        var with = new AvatarPerformanceStats(false);
        AvatarPerformance.CalculatePerformanceStats("with", a.Root, with, false);
        var layer = (AnimatorController)a.Descriptor.baseAnimationLayers[0].animatorController;
        var without = new AvatarPerformanceStats(false);
        a.Descriptor.baseAnimationLayers[0].animatorController = a.Source;
        AvatarPerformance.CalculatePerformanceStats("without", a.Root, without, false);
        Assert.AreNotSame(layer, a.Source);
        foreach (AvatarPerformanceCategory category in Enum.GetValues(typeof(AvatarPerformanceCategory)))
            if (category > AvatarPerformanceCategory.None && category < AvatarPerformanceCategory.AvatarPerformanceCategoryCount)
                Assert.AreEqual(without.GetPerformanceRatingForCategory(category), with.GetPerformanceRatingForCategory(category), category.ToString());
    }

    [Test]
    public void TwoSiblingsWithOneNameAreBothHidden()
    {
        var root = new GameObject("Twins"); owned.Add(root);
        var first = Part<MeshRenderer>(root, "Twin");
        var twin = new GameObject("Twin"); twin.transform.SetParent(root.transform, false);
        var second = twin.AddComponent<MeshRenderer>();
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.baseAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
        descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
        var clips = N4N0Installer.MakeHideSelfClips(root, descriptor);
        var paths = AnimationUtility.GetCurveBindings(clips.Hidden).Select(b => b.path).ToList();
        CollectionAssert.AllItemsAreUnique(paths);
        Assert.AreEqual(2, paths.Count);
        Assert.AreEqual("Twin", first.name);
        Assert.AreNotEqual("Twin", second.name);
    }

    [Test]
    public void AnAvatarWithoutTheCapturePartIsLeftAlone()
    {
        var root = new GameObject("No N4N0"); owned.Add(root);
        Part<MeshRenderer>(root, "Body");
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.baseAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
        descriptor.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];
        N4N0Installer.BuildHideSelf(root, false, folder + "/Kits");
        Assert.IsNull(descriptor.expressionParameters);
    }
}
