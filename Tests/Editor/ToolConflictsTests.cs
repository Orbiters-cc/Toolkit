using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.VRChat;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// A stand-in for Lazy Optimiser's switch (the real one is LazyOptimiser.Util._shouldOptimise, a cache of its PlayerPrefs
// setting): Orbiters finds every loaded copy of that class by name.
namespace LazyOptimiser
{
    internal static class Util
    {
        private static bool? _shouldOptimise;
        internal static bool? Switch { get => _shouldOptimise; set => _shouldOptimise = value; }
    }
}

// ToolConflicts' entries decided from the packages installed (pretended here), and what Orbiters does about them; found by
// the compatibility project.
public sealed class ToolConflictsTests
{
    private const string LazyKey = "lazyoptimiser.shouldOptimise";

    // params profile, 2026-11-02: Lazy Optimiser deleted N4N0's capture rig and kit (switched on through their renderers).
    [Test] public void LazyOptimiserIsTurnedOffForTheUploadsOfOrbitersAvatarsAndTheChecksSaySo()
    {
        var avatar = new GameObject("Lazy avatar");
        var item = new GameObject("N4N0");
        item.transform.SetParent(avatar.transform);
        int was = PlayerPrefs.GetInt(LazyKey, 1);
        PlayerPrefs.SetInt(LazyKey, 1);
        LazyOptimiser.Util.Switch = null;
        try
        {
            using (InstalledTools.Pretend((Orbiters.Toolkit.Editor.VRChat.Optimization.LazyOptimiser.PackageId, "0.2.6"), (InstalledTools.VrcFuryId, "1.1434.0")))
            {
                Assert.AreEqual("Lazy Optimiser deletes objects that animations switch on through a component", ToolConflicts.Find(avatar).Find(i => i.Title.Contains("Lazy Optimiser")).Title,
                    "an avatar without Orbiters components keeps Lazy Optimiser: a note");
                Assert.IsFalse(new LazyOptimiserOffHook().OnPreprocessAvatar(avatar) && LazyOptimiser.Util.Switch == false, "not turned off for it");
                Assert.IsNull(LazyOptimiser.Util.Switch);

                item.AddComponent<OrbitersAttachment>();
                var issue = ToolConflicts.Find(avatar).Find(i => i.Title.Contains("Lazy Optimiser"));
                Assert.AreEqual("Orbiters turns Lazy Optimiser off for this avatar's uploads", issue.Title);
                Assert.AreEqual(IssueLevel.Note, issue.Level);

                // Around its passes (−100 … −90): off, then exactly what it held (unread here), its setting untouched.
                Assert.IsTrue(new LazyOptimiserOffHook().OnPreprocessAvatar(avatar));
                Assert.AreEqual(false, LazyOptimiser.Util.Switch);
                Assert.IsTrue(Orbiters.Toolkit.Editor.VRChat.Optimization.LazyOptimiser.Holding);
                Assert.IsTrue(new LazyOptimiserBackHook().OnPreprocessAvatar(avatar));
                Assert.IsNull(LazyOptimiser.Util.Switch);
                Assert.AreEqual(1, PlayerPrefs.GetInt(LazyKey, 1));

                // A build stopped between the two gives it back at the next one.
                LazyOptimiser.Util.Switch = true;
                new LazyOptimiserOffHook().OnPreprocessAvatar(avatar);
                new LazyOptimiserOffHook().OnPreprocessAvatar(avatar);
                new LazyOptimiserBackHook().OnPreprocessAvatar(avatar);
                Assert.AreEqual(true, LazyOptimiser.Util.Switch);
            }
            using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsFalse(ToolConflicts.Find(avatar).Exists(i => i.Title.Contains("Lazy Optimiser")), "not installed: nothing");
        }
        finally
        {
            Orbiters.Toolkit.Editor.VRChat.Optimization.LazyOptimiser.Restore();
            LazyOptimiser.Util.Switch = null;
            PlayerPrefs.SetInt(LazyKey, was);
            Object.DestroyImmediate(avatar);
        }
    }

    // ndmf-ma-vrcfury profile, 2026-11-02: VRCFury asked about Write Defaults at every MasculineCanine build once NDMF was
    // installed (default Base/Gesture layers, Write Defaults off, next to the avatar's own FX with Write Defaults on).
    [Test] public void VrcFurysWriteDefaultsQuestionWithNdmfIsANote()
    {
        if (VrcFury.Component == null) Assert.Ignore("VRCFury's types aren't loaded: no VRCFury component to put on the avatar.");
        var avatar = new GameObject("WD avatar");
        var fx = new AnimatorController();
        fx.AddLayer("Toggles");
        fx.layers[0].stateMachine.AddState("On").writeDefaultValues = true;
        fx.layers[0].stateMachine.AddState("Off").writeDefaultValues = true;
        try
        {
            avatar.AddComponent(VrcFury.Component);
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = new[]
            {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Base, isDefault = true },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = fx },
            };
            const string title = "VRCFury may ask about Write Defaults at every upload";
            using (InstalledTools.Pretend((InstalledTools.NdmfId, "1.14.8"), (InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsTrue(ToolConflicts.Find(avatar).Exists(i => i.Title == title && i.Level == IssueLevel.Note));
            using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsFalse(ToolConflicts.Find(avatar).Exists(i => i.Title == title), "without NDMF the default layers stay empty");
            foreach (var state in fx.layers[0].stateMachine.states) state.state.writeDefaultValues = false;
            using (InstalledTools.Pretend((InstalledTools.NdmfId, "1.14.8"), (InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsFalse(ToolConflicts.Find(avatar).Exists(i => i.Title == title), "Write Defaults off everywhere: no mix");
        }
        finally
        {
            Object.DestroyImmediate(avatar);
            Object.DestroyImmediate(fx);
        }
    }
}
