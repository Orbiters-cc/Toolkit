using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar gun's controller (Docs/avatar-gun.md). Four layers:
    /// <list type="bullet">
    /// <item>Gun: put away, in the hand (VRMode 1) or before the eyes (desktop mode), on every client.</item>
    /// <item>Emitter: dim while <see cref="Loaded"/> is off, lit while it is on, on every client.</item>
    /// <item>Trigger, on its owner's client only: after the gun comes out (and after each shot) the trigger must be let go
    /// first; then a pull (the gun hand's fist past <see cref="TriggerOn"/>, or <see cref="Pull"/>) fires a loaded gun:
    /// <see cref="Fire"/> on for <see cref="FireSeconds"/>, off for <see cref="RearmSeconds"/> before the trigger counts
    /// again. An unloaded gun only clicks.</item>
    /// <item>Shot, on every client from the synced <see cref="Fire"/>: the senders, the beam fading out and the recoil.
    /// Nothing there reads IsLocal: on the hit player's client the shooter's copy of the avatar does the hitting.</item>
    /// </list>
    /// </summary>
    public static partial class AvatarGunInstaller
    {
        internal static void BuildController(GameObject root, string folder)
        {
            var controller = Controller(folder, false);
            var icon = Icon(folder, "Avatar gun icon", GunIcon);
            Install(root, folder, controller, MenuPath, "Avatar gun menu", new List<VRCExpressionsMenu.Control>
            {
                WithIcon(Control("Avatar gun", VRCExpressionsMenu.Control.ControlType.Toggle, Out), icon),
                WithIcon(Control("Choose avatar", VRCExpressionsMenu.Control.ControlType.Button, Choose), Icon(folder, "Choose avatar icon", AvatarIcon)),
            }, Parameters(), Parameters().Select(p => p.name), icon);
        }

        /// <summary>The gun's expression parameters: three synced bits, the rest local; none saved, all off at first.</summary>
        internal static VRCExpressionParameters.Parameter[] Parameters() => new[]
        {
            Parameter(Out), Parameter(Loaded), Parameter(Fire),
            // Local, but expression parameters all the same: OSC reads and sets those only.
            Parameter(Pull, synced: false), Parameter(Choose, synced: false), Parameter(Hit, synced: false),
        };

        /// <summary>
        /// The controller in <paramref name="folder"/> (with the emitter's materials it swaps, those already there kept); the
        /// left hand's (<paramref name="left"/>) reads the left gestures and holds the gun on the left palm.
        /// </summary>
        internal static AnimatorController Controller(string folder, bool left)
        {
            var (dim, lit) = EmitterMaterials(folder);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Avatar gun.controller");
            string gesture = left ? "GestureLeft" : "GestureRight";
            foreach (var name in new[] { Out, Loaded, Fire, Pull, Choose, Hit, "IsLocal" })
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            controller.AddParameter("VRMode", AnimatorControllerParameterType.Int);
            controller.AddParameter(gesture, AnimatorControllerParameterType.Int);
            controller.AddParameter(gesture + "Weight", AnimatorControllerParameterType.Float);

            // ---- Gun: where it is ----
            var machine = controller.layers[0].stateMachine;
            var off = State(controller, machine, "Put away", HoldClip(false, InRightHand));
            var hand = State(controller, machine, left ? "Left hand" : "Right hand", HoldClip(true, left ? InLeftHand : InRightHand));
            var desktop = State(controller, machine, "Before the eyes", HoldClip(true, AtDesktop));
            machine.defaultState = off;
            Transition(off, hand, (Out, AnimatorConditionMode.If, 0), ("VRMode", AnimatorConditionMode.Equals, 1));
            Transition(off, desktop, (Out, AnimatorConditionMode.If, 0), ("VRMode", AnimatorConditionMode.NotEqual, 1));
            Transition(hand, desktop, ("VRMode", AnimatorConditionMode.NotEqual, 1));
            Transition(desktop, hand, ("VRMode", AnimatorConditionMode.Equals, 1));
            foreach (var state in new[] { hand, desktop }) Transition(state, off, (Out, AnimatorConditionMode.IfNot, 0));

            // ---- Emitter: loaded or not ----
            var emitter = Layer(controller, "Emitter");
            var unloaded = State(controller, emitter, "Unloaded", EmitterClip(dim));
            var loaded = State(controller, emitter, "Loaded", EmitterClip(lit));
            emitter.defaultState = unloaded;
            Transition(unloaded, loaded, (Loaded, AnimatorConditionMode.If, 0));
            Transition(loaded, unloaded, (Loaded, AnimatorConditionMode.IfNot, 0));

            // ---- Trigger: the owner's pulls ----
            var trigger = Layer(controller, "Trigger");
            var wait = State(controller, trigger, "Let go first", ClickClip(false));
            var ready = State(controller, trigger, "Ready", ClickClip(false));
            var fire = State(controller, trigger, "Fire", Timed(ClickClip(false), FireSeconds));
            var rearm = State(controller, trigger, "Rearm", Timed(ClickClip(false), RearmSeconds));
            var click = State(controller, trigger, "Empty click", ClickClip(true));
            trigger.defaultState = wait;
            Drive(fire, (Fire, true));
            Drive(rearm, (Fire, false));
            // Let go: no Pull, and (outside VR, or the fist relaxed, or squeezed less than TriggerOff).
            var outLocal = new[] { ("IsLocal", AnimatorConditionMode.If, 0f), (Out, AnimatorConditionMode.If, 0f), (Pull, AnimatorConditionMode.IfNot, 0f) };
            foreach (var released in new[] { ("VRMode", AnimatorConditionMode.NotEqual, 1f), (gesture, AnimatorConditionMode.NotEqual, 1f), (gesture + "Weight", AnimatorConditionMode.Less, TriggerOff) })
                Transition(wait, ready, outLocal.Append(released).ToArray());
            // A pull: Pull, or in VR the fist squeezed past TriggerOn. Loaded fires; unloaded clicks.
            foreach (var (to, mode) in new[] { (fire, AnimatorConditionMode.If), (click, AnimatorConditionMode.IfNot) })
            {
                Transition(ready, to, (Out, AnimatorConditionMode.If, 0), (Loaded, mode, 0), (Pull, AnimatorConditionMode.If, 0));
                Transition(ready, to, (Out, AnimatorConditionMode.If, 0), (Loaded, mode, 0), ("VRMode", AnimatorConditionMode.Equals, 1),
                    (gesture, AnimatorConditionMode.Equals, 1), (gesture + "Weight", AnimatorConditionMode.Greater, TriggerOn));
            }
            Transition(ready, wait, (Out, AnimatorConditionMode.IfNot, 0));
            // Fire stays on for its clip, then off for the next one's; each shot wants the trigger let go again.
            AfterClip(fire, rearm);
            AfterClip(rearm, wait);
            AfterClip(click, wait);

            // ---- Shot: everyone's copy follows Fire ----
            var shot = Layer(controller, "Shot");
            var idle = State(controller, shot, "Idle", ShotClip(false));
            var firing = State(controller, shot, "Firing", ShotClip(true));
            shot.defaultState = idle;
            Transition(idle, firing, (Fire, AnimatorConditionMode.If, 0));
            Transition(firing, idle, (Fire, AnimatorConditionMode.IfNot, 0));
            return controller;
        }

        // The next state once this one's clip has played once.
        private static void AfterClip(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = true; transition.exitTime = 1; transition.hasFixedDuration = true; transition.duration = 0;
        }

        // The gun shown or not, and which of its constraint's sources holds it.
        private static AnimationClip HoldClip(bool shown, int source)
        {
            var clip = new AnimationClip();
            Curve(clip, Gun, typeof(GameObject), "m_IsActive", shown ? 1 : 0);
            for (int i = 0; i < HoldSources; i++) Curve(clip, Gun, typeof(VRCParentConstraint), "Sources.source" + i + ".Weight", i == source ? 1 : 0);
            return clip;
        }

        private static AnimationClip EmitterClip(Material material)
        {
            var clip = new AnimationClip();
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(Model, typeof(MeshRenderer), "m_Materials.Array.data[1]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = material } });
            return clip;
        }

        // The model at rest, or its empty click: a small twitch up and back (its owner's alone).
        private static AnimationClip ClickClip(bool click)
        {
            var clip = new AnimationClip();
            if (!click)
            {
                Curve(clip, Model, typeof(Transform), "localEulerAnglesRaw.x", 0);
                Curve(clip, Model, typeof(Transform), "m_LocalPosition.z", 0);
                return clip;
            }
            clip.SetCurve(Model, typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(new Keyframe(0, 0), new Keyframe(.04f, -2.5f), new Keyframe(.16f, 0), new Keyframe(.3f, 0)));
            clip.SetCurve(Model, typeof(Transform), "m_LocalPosition.z", new AnimationCurve(new Keyframe(0, 0), new Keyframe(.04f, -.002f), new Keyframe(.16f, 0), new Keyframe(.3f, 0)));
            return clip;
        }

        // A clip as long as `seconds`: its curves held to the end.
        private static AnimationClip Timed(AnimationClip clip, float seconds)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                float value = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(0);
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, seconds, value));
            }
            return clip;
        }

        // Between shots: the senders off, the beam hidden, the gun at rest. A shot: both senders on, the beam lit and
        // fading out over FireSeconds, the gun kicking up and back.
        private static AnimationClip ShotClip(bool firing)
        {
            var clip = new AnimationClip();
            foreach (var sender in new[] { RayEnd, Close }) Curve(clip, sender, typeof(VRCContactSender), "m_Enabled", firing ? 1 : 0);
            Curve(clip, Beam, typeof(GameObject), "m_IsActive", firing ? 1 : 0);
            if (!firing)
            {
                Curve(clip, Beam, typeof(SkinnedMeshRenderer), "material._Fire", 0);
                Curve(clip, Recoil, typeof(Transform), "localEulerAnglesRaw.x", 0);
                Curve(clip, Recoil, typeof(Transform), "m_LocalPosition.z", 0);
                return clip;
            }
            Timed(clip, FireSeconds);
            var fade = new AnimationCurve(new Keyframe(0, 1, 0, 0), new Keyframe(.06f, .85f), new Keyframe(FireSeconds, 0, -2, 0));
            clip.SetCurve(Beam, typeof(SkinnedMeshRenderer), "material._Fire", fade);
            clip.SetCurve(Recoil, typeof(Transform), "localEulerAnglesRaw.x", new AnimationCurve(new Keyframe(0, 0), new Keyframe(.035f, -7), new Keyframe(.2f, 0), new Keyframe(FireSeconds, 0)));
            clip.SetCurve(Recoil, typeof(Transform), "m_LocalPosition.z", new AnimationCurve(new Keyframe(0, 0), new Keyframe(.03f, -.008f), new Keyframe(.18f, 0), new Keyframe(FireSeconds, 0)));
            return clip;
        }

        // ---- The menu's icons ----

        private static VRCExpressionsMenu.Control WithIcon(VRCExpressionsMenu.Control control, Texture2D icon)
        {
            control.icon = icon;
            return control;
        }

        // A white 64 × 64 picture as Orbiters' menu icons are, from a signed distance in pixels (negative inside).
        private static Texture2D Icon(string folder, string name, System.Func<Vector2, float> distance)
        {
            var icon = Png(folder, name, 64, 64, (x, y) => new Color(1, 1, 1, Mathf.Clamp01(.5f - distance(new Vector2(x * 64, y * 64)))));
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(icon));
            importer.mipmapEnabled = false; importer.maxTextureSize = 64; importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(icon));
        }

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        // A box of half size `half` round `c`, turned by `degrees`, its corners rounded by `round`.
        private static float Box(Vector2 p, Vector2 c, Vector2 half, float round, float degrees = 0)
        {
            var d = p - c;
            float a = -degrees * Mathf.Deg2Rad;
            d = new Vector2(d.x * Mathf.Cos(a) - d.y * Mathf.Sin(a), d.x * Mathf.Sin(a) + d.y * Mathf.Cos(a));
            var q = new Vector2(Mathf.Abs(d.x), Mathf.Abs(d.y)) - half + Vector2.one * round;
            return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - round;
        }

        // The gun in profile, pointing right: N4N0's ball at the back, the body, three fins, the emitter's ring, the grip.
        private static float GunIcon(Vector2 p)
        {
            float d = Circle(p, new Vector2(17, 38), 9);
            d = Mathf.Min(d, Box(p, new Vector2(31, 38), new Vector2(13, 5.5f), 3));
            for (int i = 0; i < 3; i++) d = Mathf.Min(d, Box(p, new Vector2(38 + 4.5f * i, 38), new Vector2(1.3f, 8 - 1.2f * i), 1));
            d = Mathf.Min(d, Box(p, new Vector2(52, 38), new Vector2(3, 6.5f), 2));
            // The emitter: a ring standing off the front.
            d = Mathf.Min(d, Mathf.Abs(Box(p, new Vector2(58.5f, 38), new Vector2(1.5f, 7), 1.5f)) - .9f);
            d = Mathf.Min(d, Box(p, new Vector2(22, 24), new Vector2(4.5f, 11), 3, -14));
            // The trigger guard.
            d = Mathf.Min(d, Mathf.Abs(Circle(p, new Vector2(30.5f, 29), 5.5f)) - 1.2f);
            // A gap round the ball: N4N0's white stroke.
            return Mathf.Max(d, -(Mathf.Abs(Circle(p, new Vector2(17, 38), 5.5f)) - 1.1f));
        }

        // An avatar to pick: a bust on a card, as the picker shows them.
        private static float AvatarIcon(Vector2 p)
        {
            float card = Mathf.Abs(Box(p, new Vector2(32, 32), new Vector2(23, 27), 7)) - 2.2f;
            float head = Circle(p, new Vector2(32, 39), 8);
            float shoulders = Mathf.Max(Box(p, new Vector2(32, 15), new Vector2(14, 12), 10), p.y - 25);
            float bust = Mathf.Max(Mathf.Min(head, shoulders), Box(p, new Vector2(32, 32), new Vector2(19, 23), 4.5f));
            return Mathf.Min(card, bust);
        }
    }
}
