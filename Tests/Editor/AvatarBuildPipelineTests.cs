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
    // Every other avatar build callback around Orbiters' steps, with its number: its type's full name and the assembly (by
    // name prefix) it comes with, or only a description where the tool is not in this project (checked by number only).
    // Ties: the order the SDK ran them in.
    private static readonly (string Assembly, string Name, int Order)[] Others =
    {
        ("Orbiters.Mixer", "Orbiters.Mixer.Editor.VRChat.MixerBuildHook", -20001),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Capture", -11010),
        (null, "NDMF preprocess", -11000),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.N4N0PlatformHook", -10200),
        ("mcb", "NativeMeshAvatarBuildHook", -10200),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.N4N0CaptureHook", -10150),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.N4N0MirrorHook", -10140),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.N4N0PicturesHook", -10140),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitCaptureHook", -10110),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitKeptFitsHook", -10105),
        ("mcb", "VersionCustomizationCaptureHook", -10105),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.NovabeastOptions+Restore", -10060),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Capture", -10050),
        ("VRCFury", "VF.Hooks.VrcPreuploadHook", -10000),
        ("mcb", "VersionCustomizationSurveyHook", -9500),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Apply", -9010),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Apply", -9005),
        ("mcb", "BlendShapeLinkPostVrcfuryHook", -9000),
        ("mcb", "VersionCustomizationLockHook", -8970),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitLinkHook", -8960),
        ("Orbiters.Toolkit", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitShrinkHook", -8956),
        ("mcb", "VersionCustomizationApplyHook", -8800),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Structure", -1100),
        (null, "NDMF optimize", -1025),
        ("VRCFury", "VF.Hooks.VrcsdkFixes.KeepEditorOnlyComponentsLongerHook+VrcfRemoveEditorOnlyObjects", -1024),
        (null, "Lazy Optimiser's first pass", -100),
        (null, "Lazy Optimiser's last pass", -90),
        ("Thry", "Thry.ThryEditor.UploadCallbacks.VRCAutoAnchor", 0),
        ("Thry", "Thry.ThryEditor.ShaderOptimizer+LockMaterialsOnUpload", 100),
        ("Orbiters.MyAvatar", "Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Materials", 9000),
        ("VRCFury", "VF.Hooks.ParameterCompressorHook", int.MaxValue - 100),
    };

    // The Toolkit's steps as they were (SDK callbacks), with their numbers, in the order they ran. SkinStickHook and
    // ReFit's RefitLinkHook both had -8960. The SDK lists the callbacks of each assembly with Assembly.GetTypes (metadata
    // order) and sorts them with a stable OrderBy, so the first type ran first: in the Toolkit assembly Unity built last,
    // RefitLinkHook is type row 0x0037 and SkinStickHook 0x009B. RefitLink ran first, and must: it copies the refitted
    // clothing's blendshape weights, which SkinStick bakes to find its skin spot. Now SkinStick has a phase of its own
    // just after it (-8959), before ReFit's shrinks (-8956).
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
            foreach (var (_, name, other) in Others)
            {
                Assert.That(now, Is.Not.EqualTo(other), step.Name + " shares its order with " + name + ": the SDK would order them by type.");
                // The one former tie, broken as it ran.
                int was = former != other ? Math.Sign(former - other) : step == typeof(SkinStickHook) && name.EndsWith("RefitLinkHook") ? 1 : 0;
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
        Assert.That(Order(typeof(SkinStickHook)), Is.EqualTo(-8959));
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

    private static List<IVRCSDKPreprocessAvatarCallback> SdkCallbacks()
    {
        var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var callbacks = (field?.GetValue(null) as IEnumerable<IVRCSDKPreprocessAvatarCallback>)?.ToList();
        Assert.IsNotNull(callbacks, "The SDK lists its build steps where Orbiters looks for them");
        // The Mixer console's timing probes sit at every order and change nothing.
        return callbacks.Where(c => c != null && c.GetType().Name != "LoggerBuildStepProbe").ToList();
    }

    private static bool Loaded(string assembly) =>
        assembly != null && AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name.StartsWith(assembly, StringComparison.Ordinal));

    [Test] public void OtherToolsCallbacksArePresentWithTheNumbersPinnedHere()
    {
        var callbacks = SdkCallbacks();
        foreach (var (assembly, name, order) in Others)
        {
            // A tool not installed here is skipped.
            if (!Loaded(assembly)) continue;
            var found = callbacks.Where(c => c.GetType().FullName == name).ToList();
            Assert.That(found, Is.Not.Empty, name + " is gone while its package is installed: check the Toolkit steps' phases against what replaced it.");
            Assert.That(found.Select(c => c.callbackOrder), Has.All.EqualTo(order), name + " changed its number: check the Toolkit steps' phases against it and update Others.");
        }
    }

    // A callback this file doesn't know, among Orbiters' phases: where it must run against them is unknown.
    [Test] public void NoUnknownCallbackRunsAmongOrbitersPhases()
    {
        var known = new HashSet<string>(Others.Select(o => o.Name).Concat(StillCallbacks), StringComparer.Ordinal);
        int first = BuildPhases.Order(BuildPhase.Start), last = BuildPhases.Order(BuildPhase.SaveBuildData);
        var unknown = SdkCallbacks().Where(c => !(c is BuildPhaseCallback) && !known.Contains(c.GetType().FullName) && c.callbackOrder >= first && c.callbackOrder <= last)
            .Select(c => c.GetType().FullName + " (" + c.callbackOrder + ")").ToList();
        Assert.That(unknown, Is.Empty, "pin these in Others, with the side of each Toolkit step they must run on");
    }

    // The SDK's own sequence: callbacks listed assembly by assembly, each assembly's types in metadata order, then a stable
    // sort by order. Each Toolkit step lands on its former side of every callback actually there, ties included.
    [Test] public void TheSdksSequenceKeepsEveryToolkitStepOnItsFormerSide()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
        var listed = SdkCallbacks().OrderBy(c => assemblies.IndexOf(c.GetType().Assembly)).ThenBy(c => c.GetType().MetadataToken).ToList();
        var sequence = listed.OrderBy(c => c.callbackOrder).ToList();
        // Ties at int.MaxValue (VRCFury's last steps) run in assembly order: the last phase is where the manifest's own
        // callback was, the Toolkit's only one there.
        var lastOnes = sequence.Where(c => c.callbackOrder == int.MaxValue).ToList();
        Assert.That(lastOnes.OfType<BuildLastCallback>().Count(), Is.EqualTo(1));
        Assert.That(lastOnes.Count(c => c.GetType().Assembly == typeof(BuildStep).Assembly), Is.EqualTo(1), "the Toolkit has one callback at the end, as before");

        foreach (var (step, former) in Former)
        {
            var phase = AvatarBuildPipeline.Steps.Single(s => s.GetType() == step).Phase;
            int at = sequence.FindIndex(c => c is BuildPhaseCallback p && p.Phase == phase);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), step.Name + "'s phase is not in the SDK's list");
            for (int i = 0; i < sequence.Count; i++)
            {
                var other = sequence[i];
                if (other is BuildPhaseCallback) continue;
                int order = other.callbackOrder;
                string name = other.GetType().FullName;
                int was = former != order ? Math.Sign(former - order) : step == typeof(SkinStickHook) && name.EndsWith("RefitLinkHook") ? 1 : 0;
                // A former tie with another tool's int.MaxValue callback: the old callback was in this assembly's place.
                if (was == 0 && order == int.MaxValue) continue;
                Assert.That(was, Is.Not.Zero, "unexpected former tie of " + step.Name + " with " + name);
                Assert.That(Math.Sign(at - i), Is.EqualTo(was), step.Name + " runs on the other side of " + name + " (" + order + ")");
            }
        }
    }

    // The phase each of MCB's and My Avatar's callbacks can move into as a BuildStep and run where it runs today. Ties
    // with another package's callback (MCB's native meshes and N4N0's platform step, MCB's version capture and ReFit's
    // kept fits) did not define an order; each now gets a side.
    private static readonly (string Name, BuildPhase Phase)[] Landings =
    {
        ("Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Capture", BuildPhase.BeforeNdmf),
        ("NativeMeshAvatarBuildHook", BuildPhase.AfterNdmf),
        ("VersionCustomizationCaptureHook", BuildPhase.AfterRefitCaptures),
        ("Orbiters.MyAvatar.Editor.NovabeastOptions+Restore", BuildPhase.LastBeforeVrcFury),
        ("Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Capture", BuildPhase.LastBeforeVrcFury),
        ("VersionCustomizationSurveyHook", BuildPhase.AfterBuiltAnimations),
        ("Orbiters.MyAvatar.Editor.FaceTracking.FaceTrackingBuild+Apply", BuildPhase.BeforeBlendShapeLinks),
        ("Orbiters.MyAvatar.Editor.Expressions.MenuUploadBuild+Apply", BuildPhase.BeforeBlendShapeLinks),
        ("BlendShapeLinkPostVrcfuryHook", BuildPhase.BlendShapeLinks),
        ("VersionCustomizationLockHook", BuildPhase.BlendShapeLinks),
        ("VersionCustomizationApplyHook", BuildPhase.Versions),
        ("Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Structure", BuildPhase.Structure),
        ("Orbiters.MyAvatar.Editor.Optimization.UploadOptimizer+Materials", BuildPhase.Materials),
    };

    // Each landing phase keeps its callback on its former side of every other tool's callback, of every Toolkit step and
    // of every other landing (steps sharing a phase are ordered by their package, which also owns both).
    [Test] public void MoreOrbitersCallbacksHaveAPhaseThatKeepsTheirPlace()
    {
        int FormerOf(string name) => Others.Single(o => o.Name == name).Order;
        var formerTies = new HashSet<(string, string)>
        {
            ("NativeMeshAvatarBuildHook", "Orbiters.Toolkit.Editor.VRChat.N4N0PlatformHook"),
            ("VersionCustomizationCaptureHook", "Orbiters.Toolkit.Editor.VRChat.Refit.RefitKeptFitsHook"),
        };
        foreach (var (name, phase) in Landings)
        {
            int now = BuildPhases.Order(phase), was = FormerOf(name);
            foreach (var (_, other, order) in Others)
            {
                if (other == name) continue;
                var landed = Landings.Where(l => l.Name == other).Select(l => (BuildPhase?)l.Phase).SingleOrDefault();
                if (landed == phase) continue;
                int otherNow = landed.HasValue ? BuildPhases.Order(landed.Value) : order;
                Assert.That(now, Is.Not.EqualTo(otherNow), name + " would share its order with " + other);
                if (was == order)
                {
                    Assert.That(formerTies.Contains((name, other)) || formerTies.Contains((other, name)), Is.True, "unexpected former tie of " + name + " with " + other);
                    continue;
                }
                Assert.That(Math.Sign(now - otherNow), Is.EqualTo(Math.Sign(was - order)), name + " in " + phase + " moves to the other side of " + other);
            }
            foreach (var (step, former) in Former)
                Assert.That(Math.Sign(now - Order(step)), Is.EqualTo(Math.Sign(was - former)), name + " in " + phase + " moves to the other side of " + step.Name);
            Assert.That(AvatarBuildPipeline.StepsIn(phase).Any(s => s.GetType().Assembly == typeof(BuildStep).Assembly), Is.False,
                phase + " holds a Toolkit step: " + name + " could not name it to keep their order");
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

    private sealed class Stopping : TestStep
    {
        public Stopping(BuildPhase phase) : base(phase) { }
        public override bool Run(BuildContext context)
        {
            context.State<List<int>>().Add(1);
            return false;
        }
    }

    private sealed class Throwing : TestStep
    {
        public Throwing(BuildPhase phase) : base(phase) { }
        public override bool Run(BuildContext context) => throw new InvalidOperationException("stopped");
    }

    // A step that stops the build ends it: the next build of the same object starts from nothing.
    [Test] public void AStoppedBuildForgetsWhatItsStepsShared()
    {
        var avatar = new GameObject("Stopped build");
        try
        {
            Assert.That(AvatarBuildPipeline.RunPhase(BuildPhase.AfterVrcFury, avatar, new BuildStep[] { new Stopping(BuildPhase.AfterVrcFury) }), Is.False);
            Assert.That(BuildContext.Of(avatar), Is.Null, "a false return ends the build");
            Assert.Throws<InvalidOperationException>(() => AvatarBuildPipeline.RunPhase(BuildPhase.Start, avatar, new BuildStep[] { new Throwing(BuildPhase.Start) }));
            Assert.That(BuildContext.Of(avatar), Is.Null, "so does an exception");

            var ran = new List<string>();
            Assert.That(AvatarBuildPipeline.RunPhase(BuildPhase.Start, avatar, new BuildStep[] { new Alpha(BuildPhase.Start, ran: ran) }), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "Alpha" }));
            Assert.That(BuildContext.Of(avatar).State<List<int>>(), Is.Empty, "the next build starts empty");
        }
        finally
        {
            BuildContext.End(avatar);
            Object.DestroyImmediate(avatar);
        }
    }

    // Another tool's callback stopped the build between two phases: the SDK's end of the build ends Orbiters' too, and
    // leaves a build a caller only prepared.
    [Test] public void TheSdksBuildEndEndsTheBuildsThatStarted()
    {
        var started = new GameObject("Started build");
        var prepared = new GameObject("Prepared build");
        try
        {
            AvatarBuildPipeline.RunPhase(BuildPhase.Start, started, Array.Empty<BuildStep>());
            BuildContext.For(prepared).KeepShapes(new[] { "smile" });
            VrcSdkBuilds.OnBuildFinish(null, "failed");
            Assert.That(BuildContext.Of(started), Is.Null);
            Assert.That(BuildContext.Of(prepared)?.KeptShapes, Is.EquivalentTo(new[] { "smile" }));
        }
        finally
        {
            BuildContext.End(started);
            BuildContext.End(prepared);
            Object.DestroyImmediate(started);
            Object.DestroyImmediate(prepared);
        }
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
