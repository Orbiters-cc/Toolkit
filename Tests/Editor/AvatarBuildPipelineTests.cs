using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Build;
using Orbiters.Toolkit.Editor.VRChat.Manifest;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.Editor.VRChat.SurfaceFollow;
using Orbiters.Toolkit.Editor.VRChat.XRay;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

// Orbiters' avatar build steps run in phases at fixed SDK orders. Moving a step changes what it sees without any compile
// error: these pin each Toolkit step's place against every other tool's build callback as of 2026-11-10, when the Toolkit's
// steps were still SDK callbacks of their own.
public sealed class AvatarBuildPipelineTests
{
    // Every other avatar build callback around Orbiters' steps, with its number (and, where it is known, its type's full
    // name, checked against the loaded callbacks). Ties: the order the SDK ran them in.
    private static readonly (string Name, int Order)[] Others =
    {
        ("Orbiters.Mixer.Editor.VRChat.MixerBuildHook", -20001),
        ("Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Capture", -11010),
        ("NDMF preprocess", -11000),
        ("Orbiters.Toolkit.Editor.VRChat.N4N0PlatformHook", -10200),
        ("NativeMeshAvatarBuildHook", -10200),
        ("Orbiters.Toolkit.Editor.VRChat.N4N0CaptureHook", -10150),
        ("Orbiters.Toolkit.Editor.VRChat.N4N0MirrorHook", -10140),
        ("Orbiters.Toolkit.Editor.VRChat.N4N0PicturesHook", -10140),
        ("Orbiters.Toolkit.Editor.VRChat.Refit.RefitCaptureHook", -10110),
        ("Orbiters.Toolkit.Editor.VRChat.Refit.RefitKeptFitsHook", -10105),
        ("VersionCustomizationCaptureHook", -10105),
        ("Orbiters.MyAvatar.Editor.NovabeastOptions+Restore", -10060),
        ("Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Capture", -10050),
        ("VF.Hooks.VrcPreuploadHook", -10000),
        ("VersionCustomizationSurveyHook", -9500),
        ("Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Apply", -9010),
        ("Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Apply", -9005),
        ("BlendShapeLinkPostVrcfuryHook", -9000),
        ("VersionCustomizationLockHook", -8970),
        ("Orbiters.Toolkit.Editor.VRChat.Refit.RefitLinkHook", -8960),
        ("Orbiters.Toolkit.Editor.VRChat.Refit.RefitShrinkHook", -8956),
        ("VersionCustomizationApplyHook", -8800),
        ("Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Structure", -1100),
        ("NDMF optimize", -1025),
        ("VF.Hooks.VrcsdkFixes.KeepEditorOnlyComponentsLongerHook+VrcfRemoveEditorOnlyObjects", -1024),
        ("Lazy Optimiser's first pass", -100),
        ("Lazy Optimiser's last pass", -90),
        ("Poiyomi's material lock", 4),
        ("Poiyomi's lock check", 100),
        ("Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Materials", 9000),
        ("VF.Hooks.ParameterCompressorHook", int.MaxValue - 100),
    };

    // The Toolkit's steps as they were (SDK callbacks), with their numbers, in the order they ran. SkinStickHook and
    // ReFit's RefitLinkHook both had −8960: SkinStickHook ran first (the SDK lists an assembly's types in order, and
    // Attachments/ comes before Refit/); now it has a phase of its own just before.
    private static readonly (Type Step, int Order)[] Former =
    {
        (typeof(XRayBuildCopyHook), -20000),
        (typeof(AttachmentBuildHook), -10100),
        (typeof(VrcFuryBuiltAnimationsHook), -9999),
        (typeof(SkinStickHook), -8960),
        (typeof(SurfaceFollowHook), -8950),
        (typeof(AttachmentFinishHook), -8900),
        (typeof(LazyOptimiserOffHook), -101),
        (typeof(LazyOptimiserBackHook), -89),
        (typeof(BuildCopyAssetsHook), 10000),
        (typeof(AvatarManifestExporter), int.MaxValue),
    };

    // Toolkit callbacks left as they are this round: N4N0's (its own stream) and ReFit's.
    private static readonly string[] StillCallbacks =
    {
        "Orbiters.Toolkit.Editor.VRChat.N4N0PlatformHook", "Orbiters.Toolkit.Editor.VRChat.N4N0CaptureHook",
        "Orbiters.Toolkit.Editor.VRChat.N4N0MirrorHook", "Orbiters.Toolkit.Editor.VRChat.N4N0PicturesHook",
        "Orbiters.Toolkit.Editor.VRChat.Refit.RefitCaptureHook", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitKeptFitsHook",
        "Orbiters.Toolkit.Editor.VRChat.Refit.RefitLinkHook", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitShrinkHook",
    };

    private static int Order(Type step) => BuildPhases.Order(AvatarBuildPipeline.Steps.Single(s => s.GetType() == step).Phase);

    [Test] public void EveryToolkitStepKeepsItsPlaceAmongTheOtherToolsCallbacks()
    {
        foreach (var (step, former) in Former)
        {
            int now = Order(step);
            foreach (var (name, other) in Others)
            {
                Assert.That(now, Is.Not.EqualTo(other), step.Name + " shares its order with " + name + ": the SDK would order them by type.");
                // The one former tie, broken as it ran.
                int was = former != other ? Math.Sign(former - other) : step == typeof(SkinStickHook) && name.EndsWith("RefitLinkHook") ? -1 : 0;
                Assert.That(was, Is.Not.Zero, "unexpected former tie of " + step.Name + " with " + name);
                Assert.That(Math.Sign(now - other), Is.EqualTo(was), step.Name + " moved to the other side of " + name + " (" + other + ")");
            }
        }
    }

    [Test] public void ToolkitStepsRunInTheirFormerOrder()
    {
        var types = new HashSet<Type>(Former.Select(f => f.Step));
        var ran = AvatarBuildPipeline.Steps.Where(s => types.Contains(s.GetType())).Select(s => s.GetType()).ToList();
        CollectionAssert.AreEqual(Former.Select(f => f.Step).ToList(), ran);
        // And so it reads today, phase by phase.
        Assert.That(Order(typeof(XRayBuildCopyHook)), Is.EqualTo(-20000));
        Assert.That(Order(typeof(AttachmentBuildHook)), Is.EqualTo(-10100));
        Assert.That(Order(typeof(VrcFuryBuiltAnimationsHook)), Is.EqualTo(-9999));
        Assert.That(Order(typeof(SkinStickHook)), Is.EqualTo(-8961));
        Assert.That(Order(typeof(SurfaceFollowHook)), Is.EqualTo(-8950));
        Assert.That(Order(typeof(AttachmentFinishHook)), Is.EqualTo(-8950), "after SurfaceFollowHook, in its phase");
        Assert.That(Order(typeof(LazyOptimiserOffHook)), Is.EqualTo(-101));
        Assert.That(Order(typeof(LazyOptimiserBackHook)), Is.EqualTo(-89));
        Assert.That(Order(typeof(BuildCopyAssetsHook)), Is.EqualTo(10000));
        Assert.That(Order(typeof(AvatarManifestExporter)), Is.EqualTo(int.MaxValue));
    }

    [Test] public void EveryToolkitStepIsPinnedHere()
    {
        var toolkit = typeof(BuildStep).Assembly;
        var steps = AvatarBuildPipeline.Steps.Select(s => s.GetType()).Where(t => t.Assembly == toolkit).ToList();
        CollectionAssert.AreEquivalent(Former.Select(f => f.Step).ToList(), steps, "a new Toolkit step: add it to Former, with where it must run");
    }

    [Test] public void TheSdkRunsOnePhaseCallbackPerPhaseAndTheToolkitNoOtherCallbackButN4N0sAndReFits()
    {
        var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var callbacks = (field?.GetValue(null) as IEnumerable<IVRCSDKPreprocessAvatarCallback>)?.ToList();
        Assert.IsNotNull(callbacks, "The SDK lists its build steps where Orbiters looks for them");
        var phases = callbacks.OfType<BuildPhaseCallback>().ToList();
        CollectionAssert.AreEquivalent(BuildPhases.All, phases.Select(p => p.Phase).ToList(), "one SDK callback per phase");
        foreach (var phase in phases) Assert.That(phase.callbackOrder, Is.EqualTo(BuildPhases.Order(phase.Phase)));

        var toolkit = typeof(BuildStep).Assembly;
        var own = toolkit.GetTypes().Where(t => !t.IsAbstract && typeof(IVRCSDKPreprocessAvatarCallback).IsAssignableFrom(t) && !typeof(BuildPhaseCallback).IsAssignableFrom(t))
            .Select(t => t.FullName).ToList();
        CollectionAssert.AreEquivalent(StillCallbacks, own, "the Toolkit's own build work is a BuildStep");
    }

    [Test] public void OtherToolsCallbacksStillHaveTheNumbersPinnedHere()
    {
        var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var callbacks = (field?.GetValue(null) as IEnumerable<IVRCSDKPreprocessAvatarCallback>)?.ToList() ?? new List<IVRCSDKPreprocessAvatarCallback>();
        var pinned = Others.ToLookup(o => o.Name, o => o.Order);
        foreach (var callback in callbacks)
        {
            var name = callback.GetType().FullName;
            if (!pinned.Contains(name)) continue;
            Assert.That(pinned[name], Has.Member(callback.callbackOrder), name + " changed its number: check the Toolkit steps' phases against it and update Others.");
        }
    }

    [Test] public void NoTwoPhasesShareAnOrder()
    {
        var orders = BuildPhases.All.Select(BuildPhases.Order).ToList();
        CollectionAssert.AllItemsAreUnique(orders);
        CollectionAssert.IsOrdered(orders, "phases are declared in the order they run");
    }

    // ---- Sorting ----

    private class TestStep : BuildStep
    {
        private readonly BuildPhase phase;
        private readonly Type[] after, before;
        public readonly List<string> Ran;

        // No parameterless constructor: the pipeline never finds it among real steps.
        protected TestStep(BuildPhase phase, Type[] after = null, Type[] before = null, List<string> ran = null)
        {
            this.phase = phase; this.after = after ?? Type.EmptyTypes; this.before = before ?? Type.EmptyTypes; Ran = ran;
        }

        public override BuildPhase Phase => phase;
        public override IReadOnlyList<Type> After => after;
        public override IReadOnlyList<Type> Before => before;
        public override bool Run(BuildContext context)
        {
            Ran?.Add(GetType().Name);
            return true;
        }
    }

    private sealed class Alpha : TestStep { public Alpha(BuildPhase phase, Type[] after = null, Type[] before = null, List<string> ran = null) : base(phase, after, before, ran) { } }
    private sealed class Beta : TestStep { public Beta(BuildPhase phase, Type[] after = null, Type[] before = null, List<string> ran = null) : base(phase, after, before, ran) { } }
    private sealed class Gamma : TestStep { public Gamma(BuildPhase phase, Type[] after = null, Type[] before = null, List<string> ran = null) : base(phase, after, before, ran) { } }

    private static List<string> Names(IEnumerable<BuildStep> steps) => steps.Select(s => s.GetType().Name).ToList();

    [Test] public void StepsRunByPhaseThenAsTheySayThenByName()
    {
        var sorted = AvatarBuildPipeline.Sort(new BuildStep[]
        {
            new Gamma(BuildPhase.AfterVrcFury),
            new Beta(BuildPhase.Start),
            new Alpha(BuildPhase.Start, after: new[] { typeof(Beta) }),
        });
        CollectionAssert.AreEqual(new[] { "Beta", "Alpha", "Gamma" }, Names(sorted));

        sorted = AvatarBuildPipeline.Sort(new BuildStep[] { new Gamma(BuildPhase.Start, before: new[] { typeof(Alpha) }), new Alpha(BuildPhase.Start), new Beta(BuildPhase.Start) });
        CollectionAssert.AreEqual(new[] { "Beta", "Gamma", "Alpha" }, Names(sorted), "Gamma before Alpha; Beta, free, first by name");

        // Unknown types and steps of earlier phases constrain nothing.
        sorted = AvatarBuildPipeline.Sort(new BuildStep[] { new Alpha(BuildPhase.Last, after: new[] { typeof(string), typeof(Beta) }), new Beta(BuildPhase.Start) });
        CollectionAssert.AreEqual(new[] { "Beta", "Alpha" }, Names(sorted));
    }

    [Test] public void ACycleIsAnErrorNamingItsSteps()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AvatarBuildPipeline.Sort(new BuildStep[]
        {
            new Alpha(BuildPhase.Structure, after: new[] { typeof(Gamma) }),
            new Beta(BuildPhase.Structure, after: new[] { typeof(Alpha) }),
            new Gamma(BuildPhase.Structure, after: new[] { typeof(Beta) }),
        }));
        StringAssert.Contains("Structure", error.Message);
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" }) StringAssert.Contains(name, error.Message);
    }

    [Test] public void AStepCannotAskToRunAfterOneOfALaterPhase()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AvatarBuildPipeline.Sort(new BuildStep[]
        {
            new Alpha(BuildPhase.Start, after: new[] { typeof(Beta) }),
            new Beta(BuildPhase.Last),
        }));
        StringAssert.Contains("Beta", error.Message);
        StringAssert.Contains("Alpha", error.Message);
    }

    // ---- The build's context ----

    [Test] public void APhaseThatBeginsAgainIsANewBuild()
    {
        var avatar = new GameObject("Context test");
        try
        {
            var prepared = BuildContext.For(avatar);
            prepared.KeepShapes(new[] { "smile" });
            var first = BuildContext.Enter(avatar, BuildPhase.Start, true);
            Assert.AreSame(prepared, first, "what the caller prepared before the first phase is the build's");
            Assert.AreSame(first, BuildContext.Enter(avatar, BuildPhase.Start, false), "another step of the same phase");
            Assert.AreSame(first, BuildContext.Enter(avatar, BuildPhase.AfterVrcFury, true));
            Assert.AreSame(first, BuildContext.Of(avatar), "other tools' callbacks between phases find it");
            var again = BuildContext.Enter(avatar, BuildPhase.Start, true);
            Assert.AreNotSame(first, again, "the same object built again");
            Assert.IsEmpty(again.KeptShapes);
            BuildContext.End(avatar);
            Assert.IsNull(BuildContext.Of(avatar));
        }
        finally { Object.DestroyImmediate(avatar); }
    }

    [Test] public void StepsShareStateThroughTheContext()
    {
        var avatar = new GameObject("State test");
        try
        {
            var context = BuildContext.For(avatar);
            context.State<List<int>>().Add(3);
            Assert.That(BuildContext.Of(avatar).State<List<int>>(), Is.EqualTo(new[] { 3 }));
            BuildContext.End(avatar);
            Assert.That(BuildContext.For(avatar).State<List<int>>(), Is.Empty, "a new build starts empty");
            BuildContext.End(avatar);
        }
        finally { Object.DestroyImmediate(avatar); }
    }
}
