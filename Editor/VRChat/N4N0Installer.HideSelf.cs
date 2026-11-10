using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Records a world without the wearer in it (Orbiters contracts' Docs/rig-frames.md, Recording without the wearer).
    /// The capture kit is part of the avatar, so VRChat's camera mask "Local User" culls it with the avatar: with it off
    /// there is no strip, no pose, no keyframe. So Orbiters - N4N0 leaves Local User on, and while it records has this
    /// layer turn off every other renderer of the avatar on the wearer's own client. The kit and the rig go on drawing,
    /// and nothing of the wearer is there to erase afterwards: no body, no shadow, nothing in the rig's six faces.
    /// <para>
    /// <b>The avatar always comes back by itself.</b> The layer only stays hidden while it sees
    /// <see cref="CaptureHideSelf"/> change, which Orbiters - N4N0 flips about four times a second: when the app dies, OSC
    /// drops or the value sticks, each hidden state's own clock runs out after <see cref="HideSelfTimeout"/> seconds and
    /// the avatar shows. Coming in again takes a change, so a value stuck on never hides it again. Not recording, or any
    /// client but the wearer's (<c>IsLocal</c>), leaves at once; the parameter is local and unsynced on top of that, and
    /// never saved, so an avatar loaded anew starts shown.
    /// </para>
    /// <para>
    /// <b>How it lets go</b> was measured on Unity 2022.3.22f1's Animator, as VRChat runs it: a last layer whose shown state
    /// has an empty clip or no motion, Write Defaults on or off, left a renderer nobody else animates off for good in most
    /// combinations (those with Write Defaults off near it, which real avatars have), and a shown state writing every
    /// renderer took the avatar's own toggles away. So a renderer no other layer animates is written by every shown state,
    /// at the value it has in the upload; one another layer animates is left to that layer while shown, and written for a
    /// moment on the way back, in case that layer only writes it now and then.
    /// </para>
    /// </summary>
    public static partial class N4N0Installer
    {
        /// <summary>Flipped by Orbiters - N4N0 while it records without the wearer (local, unsynced, never saved).</summary>
        public const string CaptureHideSelf = "N4N0/Capture/HideSelf";
        internal const string HideSelfLayer = "Capture hide self";
        /// <summary>How long the avatar stays hidden without a flip: six of the app's beats (0.25 s), so a dropped datagram or a busy moment never shows it, and a dead app does soon.</summary>
        internal const float HideSelfTimeout = 1.5f;
        // How long the state after a hidden one writes every renderer: a few frames at any frame rate VRChat runs.
        private const float HideSelfRestore = 0.1f;

        /// <summary>What the layer's states write.</summary>
        internal readonly struct HideSelfClips
        {
            /// <summary>Every renderer off.</summary>
            public readonly AnimationClip Hidden;
            /// <summary>Renderers no other layer animates, as uploaded.</summary>
            public readonly AnimationClip Shown;
            /// <summary>Every renderer as uploaded.</summary>
            public readonly AnimationClip Restore;
            public HideSelfClips(AnimationClip hidden, AnimationClip shown, AnimationClip restore) { Hidden = hidden; Shown = shown; Restore = restore; }
        }

        /// <summary>
        /// The renderers the layer hides: every one of the build copy, in any state, but those of N4N0's capture part (the
        /// kit and the rig's, which draw what is recorded). Particles, trails and lines are renderers too. Lights are left
        /// on: they draw nothing of the wearer, and N4N0's light is the wearer's own choice.
        /// </summary>
        internal static List<Renderer> HideSelfRenderers(GameObject avatarRoot)
        {
            var captures = FindAll(avatarRoot).Select(n => n.Find(Capture)).Where(c => c != null).ToList();
            return avatarRoot.GetComponentsInChildren<Renderer>(true).Where(r => !captures.Any(c => r.transform.IsChildOf(c))).ToList();
        }

        /// <summary>A clip's curve only reaches the first of two siblings with one name: the later ones get names of their own (no clip could reach them by that path either).</summary>
        private static void MakePathsUnique(IEnumerable<Renderer> renderers, Transform root)
        {
            foreach (var renderer in renderers)
                for (var t = renderer.transform; t != root && t.parent != null; t = t.parent)
                {
                    var first = t.parent.Cast<Transform>().First(s => s.name == t.name);
                    if (first == t) continue;
                    int n = 2;
                    string name = t.name;
                    while (t.parent.Find(name + " " + n) != null) n++;
                    t.name = name + " " + n;
                }
        }

        /// <summary>Every binding of <c>m_Enabled</c> in the clips of the avatar's playable layers.</summary>
        private static List<EditorCurveBinding> AnimatedEnabled(VRCAvatarDescriptor descriptor)
        {
            var bindings = new List<EditorCurveBinding>();
            foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
                if (layer.animatorController != null)
                    foreach (var clip in layer.animatorController.animationClips)
                        if (clip != null)
                            bindings.AddRange(AnimationUtility.GetCurveBindings(clip).Where(b => b.propertyName == "m_Enabled"));
            return bindings;
        }

        private static void Constant(AnimationClip clip, Renderer renderer, GameObject root, float seconds, float value) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), renderer.GetType(), "m_Enabled"), AnimationCurve.Constant(0, seconds, value));

        internal static HideSelfClips MakeHideSelfClips(GameObject avatarRoot, VRCAvatarDescriptor descriptor)
        {
            var renderers = HideSelfRenderers(avatarRoot);
            MakePathsUnique(renderers, avatarRoot.transform);
            var elsewhere = AnimatedEnabled(descriptor);
            var hidden = new AnimationClip { name = "Capture hide self: hidden" };
            var shown = new AnimationClip { name = "Capture hide self: shown" };
            var restore = new AnimationClip { name = "Capture hide self: back" };
            foreach (var renderer in renderers)
            {
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot.transform);
                float value = renderer.enabled ? 1 : 0;
                Constant(hidden, renderer, avatarRoot, HideSelfTimeout, 0);
                Constant(restore, renderer, avatarRoot, HideSelfRestore, value);
                if (!elsewhere.Any(b => b.path == path && b.type.IsInstanceOfType(renderer))) Constant(shown, renderer, avatarRoot, HideSelfRestore, value);
            }
            return new HideSelfClips(hidden, shown, restore);
        }

        /// <summary>
        /// Adds the layer at the end of <paramref name="controller"/>: the last layer of the last playable layer (FX), so
        /// nothing overrides it, and with nothing after it, no other layer's index moves.
        /// </summary>
        internal static void AddHideSelfLayer(AnimatorController controller, HideSelfClips clips)
        {
            foreach (string name in new[] { CaptureHideSelf, CaptureRecord, "IsLocal" })
                if (!controller.parameters.Any(p => p.name == name))
                    controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            var layer = HeldPropRig.Layer(controller, HideSelfLayer);
            var shown = HeldPropRig.State(controller, layer, "Shown", clips.Shown);
            var armed = HeldPropRig.State(controller, layer, "Armed", clips.Shown);
            var hiddenOn = HeldPropRig.State(controller, layer, "Hidden, value on", clips.Hidden);
            var hiddenOff = HeldPropRig.State(controller, layer, "Hidden, value off", clips.Hidden);
            var back = HeldPropRig.State(controller, layer, "Coming back", clips.Restore);
            layer.defaultState = shown;
            // In: a change of the value, off then on, while recording on the wearer's client.
            Go(controller, shown, armed, (CaptureRecord, true), (CaptureHideSelf, false), ("IsLocal", true));
            Go(controller, armed, hiddenOn, (CaptureHideSelf, true), (CaptureRecord, true), ("IsLocal", true));
            Go(controller, armed, shown, (CaptureRecord, false));
            Go(controller, armed, shown, ("IsLocal", false));
            // Staying: every change of the value starts the clock again.
            Go(controller, hiddenOn, hiddenOff, (CaptureHideSelf, false));
            Go(controller, hiddenOff, hiddenOn, (CaptureHideSelf, true));
            foreach (var hidden in new[] { hiddenOn, hiddenOff })
            {
                // Out: at once when the recording ends or this is not the wearer; by itself when the clock runs out.
                Go(controller, hidden, back, (CaptureRecord, false));
                Go(controller, hidden, back, ("IsLocal", false));
                var timeout = hidden.AddTransition(back);
                timeout.hasExitTime = true; timeout.exitTime = 1f; timeout.duration = 0;
            }
            var done = back.AddTransition(shown);
            done.hasExitTime = true; done.exitTime = 1f; done.duration = 0;
        }

        /// <summary>
        /// A transition on parameters, whatever their type in this controller: VRCFury's build turns FX's bools into floats
        /// (in the sample avatar's upload, <c>IsLocal</c> and <see cref="CaptureRecord"/> among them), VRChat sets them all the
        /// same, and a condition for a bool on a float never holds as it should.
        /// </summary>
        private static void Go(AnimatorController controller, AnimatorState from, AnimatorState to, params (string name, bool on)[] conditions)
        {
            var transition = from.AddTransition(to); transition.duration = 0; transition.hasExitTime = false;
            foreach (var (name, on) in conditions)
            {
                switch (controller.parameters.First(p => p.name == name).type)
                {
                    case AnimatorControllerParameterType.Float: transition.AddCondition(on ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less, 0.5f, name); break;
                    case AnimatorControllerParameterType.Int: transition.AddCondition(on ? AnimatorConditionMode.NotEqual : AnimatorConditionMode.Equals, 0, name); break;
                    default: transition.AddCondition(on ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, name); break;
                }
            }
        }

        /// <summary>
        /// Gives the avatar being built the layer, once every avatar tool is done with its renderers and controllers. An
        /// avatar it cannot be added to uploads without: the parameter is then missing too, which is how Orbiters - N4N0
        /// knows and says so. A failure here never stops an upload.
        /// </summary>
        internal static void BuildHideSelf(GameObject avatarRoot, bool mobile, string root = CaptureKitFolder)
        {
            if (mobile || !FindAll(avatarRoot).Any(n => n.Find(Capture) != null)) return;
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || HideSelfRenderers(avatarRoot).Count == 0) return;
            try
            {
                string folder = root + "/" + SafeFolderName(BuiltAvatarName(avatarRoot));
                EnsureFolder(folder);
                var clips = MakeHideSelfClips(avatarRoot, descriptor);
                var controller = OwnedFx(descriptor, folder);
                if (controller == null)
                {
                    Debug.LogWarning("N4N0: " + avatarRoot.name + "'s FX layer is no animator controller, so it uploads unable to record without the wearer.");
                    return;
                }
                AddHideSelfLayer(controller, clips);
                OwnParameter(descriptor, folder);
                EditorUtility.SetDirty(controller);
            }
            catch (Exception e)
            {
                Debug.LogWarning("N4N0: " + avatarRoot.name + " uploads unable to record without the wearer: " + e.Message);
                RemoveHideSelf(descriptor);
            }
        }

        // A half-made layer must not ship: the avatar then uploads as it would have, and the app says the rest.
        private static void RemoveHideSelf(VRCAvatarDescriptor descriptor)
        {
            foreach (var layer in descriptor.baseAnimationLayers)
            {
                if (layer.type != VRCAvatarDescriptor.AnimLayerType.FX || !(layer.animatorController is AnimatorController controller)) continue;
                var layers = controller.layers;
                int at = Array.FindLastIndex(layers, l => l.name == HideSelfLayer);
                if (at >= 0) controller.RemoveLayer(at);
            }
            var list = descriptor.expressionParameters;
            if (list != null && list.parameters != null && list.parameters.Any(p => p.name == CaptureHideSelf))
            {
                var copy = Object.Instantiate(list);
                copy.parameters = list.parameters.Where(p => p.name != CaptureHideSelf).ToArray();
                descriptor.expressionParameters = copy;
            }
        }

        // The FX controller of this build alone: VRCFury's build copy as it is, anything else copied (an upload never changes a project's own asset).
        private static AnimatorController OwnedFx(VRCAvatarDescriptor descriptor, string folder)
        {
            for (int i = 0; i < descriptor.baseAnimationLayers.Length; i++)
            {
                if (descriptor.baseAnimationLayers[i].type != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                if (!(descriptor.baseAnimationLayers[i].animatorController is AnimatorController controller)) return null;
                string path = AssetDatabase.GetAssetPath(controller);
                if (path.StartsWith("Packages/com.vrcfury.temp", StringComparison.Ordinal)) return controller;
                string copy = folder + "/FX of the build.controller";
                AssetDatabase.DeleteAsset(copy);
                if (string.IsNullOrEmpty(path))
                {
                    AssetDatabase.CreateAsset(controller, copy);
                    return controller;
                }
                if (!AssetDatabase.CopyAsset(path, copy)) throw new InvalidOperationException("could not copy " + path);
                var owned = AssetDatabase.LoadAssetAtPath<AnimatorController>(copy);
                descriptor.baseAnimationLayers[i].animatorController = owned;
                return owned;
            }
            return null;
        }

        // Local, unsynced, never saved: none of the 256 synced bits. The list of the build: the project's stays as it was.
        private static void OwnParameter(VRCAvatarDescriptor descriptor, string folder)
        {
            var list = descriptor.expressionParameters;
            if (list != null && list.parameters != null && list.parameters.Any(p => p.name == CaptureHideSelf)) return;
            var copy = list != null ? Object.Instantiate(list) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
            copy.name = "Parameters of the build";
            copy.parameters = (copy.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).Append(HeldPropRig.Parameter(CaptureHideSelf, synced: false)).ToArray();
            string path = folder + "/Parameters of the build.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(copy, path);
            descriptor.expressionParameters = copy;
        }
    }

    /// <summary>
    /// Gives the avatar being built the layer that hides it while it records without the wearer
    /// (<see cref="N4N0Installer.BuildHideSelf"/>).
    /// </summary>
    internal sealed class N4N0HideSelfHook : IVRCSDKPreprocessAvatarCallback
    {
        // After every tool that adds or merges renderers or rewrites controllers (VRCFury -10000, d4rk's optimizer -1025 and
        // -15, My Avatar's materials 9000), before Toolkit keeps the build's in-memory assets (10000).
        public int callbackOrder => 9500;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            N4N0Installer.BuildHideSelf(avatarRoot, N4N0Installer.MobileTarget);
            return true;
        }
    }
}
