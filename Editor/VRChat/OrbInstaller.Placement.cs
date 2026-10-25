using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Where N4N0's screen is (screen version 7): it stays where it is in the world, as a dialog of a headset's own menus
    /// does, so its owner can look and point around it, and glides back in front of their eyes only once it has been out
    /// of their sight for a moment (<see cref="LookAwayTime"/>). Its owner moves it by its handle, as the hand screen's:
    /// grabbed, it rides the palm from where it was taken, turned towards the head; let go, it stays there.
    /// <para>
    /// The screen is held to its glide (<see cref="ScreenGlide"/>), which follows its place (<see cref="ScreenPlace"/>) and,
    /// while it glides back, blends over to its home before the eyes (<see cref="ScreenHome"/>, upright, facing them). The
    /// place is frozen in the world while the screen stays (<see cref="ScreenPlaced"/>), follows the home while it does
    /// not, and rides a palm's carry frame while a hand holds the handle (<see cref="ScreenHeldLeft"/>,
    /// <see cref="ScreenHeldRight"/>; unfreezing measures where it is from that frame). Three synced bools say so, so
    /// everyone sees it where its owner does (a little later); the owner's animator alone decides them, from a local
    /// contact: the screen's middle within a cone of four spheres along the line of sight (<see cref="ScreenInView"/>).
    /// Its size is a synced float Orbiters - N4N0 sets (<see cref="ScreenSize"/>).
    /// </para>
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Synced, set by its owner's animator: the screen stays where it is (else it glides back before the eyes).</summary>
        public const string ScreenPlaced = "N4N0/Screen/Placed";
        /// <summary>Synced, the owner's latch: a hand holds the screen's handle.</summary>
        public const string ScreenHeldLeft = "N4N0/Screen/HeldLeft", ScreenHeldRight = "N4N0/Screen/HeldRight";
        /// <summary>Local, a contact: the screen's middle is in its owner's sight.</summary>
        public const string ScreenInView = "N4N0/Screen/InView";
        /// <summary>
        /// Synced, set by Orbiters - N4N0 (Settings): the screen's size, 0 to 1 for <see cref="SmallestScreen"/> to
        /// <see cref="LargestScreen"/> times; its default is the size 1.
        /// </summary>
        public const string ScreenSize = "N4N0/Screen/Size";
        /// <summary>The handle's grab (VRChat adds <c>_IsGrabbed</c>) and which hand is at it (local contacts).</summary>
        public const string ScreenGrab = "N4N0/Screen/Grab", ScreenHandL = "N4N0/Screen/HandL", ScreenHandR = "N4N0/Screen/HandR";
        /// <summary>The view cone's contact tag: only the screen's middle takes it.</summary>
        public const string ViewTag = "N4N0View";
        internal const string ScreenHome = Gaze + "/Screen home", ScreenPlace = Shown + "/Screen place", ScreenGlide = Shown + "/Screen glide";
        internal const string ScreenGrips = Shown + "/Screen grips", ScreenHandle = ScreenObject + "/Handle", ScreenView = ScreenObject + "/View";
        internal const string Carry = "Carry";
        internal static string ScreenGrip(bool right) => ScreenGrips + "/" + (right ? "Right" : "Left") + " grip";
        // The place's sources: its home, the left and right palms' carry frames.
        internal const int PlaceHome = 0, PlaceLeft = 1, PlaceRight = 2;
        // The home from the eyes (right, up, forward, turned with the head: a little below where they look), the screen's
        // size at the ends of ScreenSize, how long it glides back, how long it may be out of sight first.
        internal static readonly Vector3 ScreenHomeFromEyes = new Vector3(0, -.06f, .45f);
        internal const float LargestScreen = 1.8f, SmallestScreen = .6f, GlideTime = .45f, LookAwayTime = .4f;
        internal static readonly float DefaultScreenSize = (1 - SmallestScreen) / (LargestScreen - SmallestScreen);
        // The view cone: spheres along the line of sight, each as wide as this share of its distance (about 58°).
        internal static readonly float[] ViewDistances = { .35f, .7f, 1.2f, 2f };
        internal const float ViewWidth = .85f;
        // The handle under the screen's middle: its bar, the grab's reach and the hands' (as the hand screen's).
        internal static readonly Vector3 HandleFromScreen = new Vector3(0, -ScreenHeight / 2 - .03f, 0);
        internal const float HandleRadius = .06f, HandleHandRadius = .22f;

        /// <summary>
        /// The screen's place, glide, home and grips under a placed orb, before the screen (held to the glide) exists.
        /// <see cref="Bind"/> fits the home to the eyes and the grips to the palms.
        /// </summary>
        private static Transform AddPlacement(Transform orb)
        {
            var shown = orb.Find(Shown);
            var gaze = orb.Find(Gaze);
            var head = orb.Find(Head);
            // Before the eyes, upright as the world is, facing them.
            var home = Child(gaze, "Screen home");
            home.localPosition = ScreenHomeFromEyes;
            var face = home.gameObject.AddComponent<VRCAimConstraint>();
            face.Sources.Add(new VRCConstraintSource(gaze, 1));
            face.AimAxis = Vector3.back; face.UpAxis = Vector3.up; face.WorldUp = VRCConstraintBase.WorldUpType.SceneUp;
            face.IsActive = true; face.Locked = true;
            // The palms' carry frames: in the middle of each palm, facing the head, upright as the avatar stands.
            var grips = Child(shown, "Screen grips");
            var carries = Hands.Select(right =>
            {
                var grip = Child(grips, (right ? "Right" : "Left") + " grip");
                var follow = grip.gameObject.AddComponent<VRCParentConstraint>();
                follow.Sources.Add(new VRCConstraintSource(null, 1));
                follow.IsActive = true; follow.Locked = true;
                var carry = Child(grip, Carry);
                var aim = carry.gameObject.AddComponent<VRCAimConstraint>();
                aim.Sources.Add(new VRCConstraintSource(gaze, 1));
                aim.AimAxis = Vector3.back; aim.UpAxis = Vector3.up;
                aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectRotationUp; aim.WorldUpTransform = head; aim.WorldUpVector = Vector3.up;
                aim.IsActive = true; aim.Locked = true;
                return carry;
            }).ToArray();
            // The place: unfreezing measures each source's offset where it is; the animator sets the home's back to 0.
            var place = Child(shown, "Screen place");
            var hold = place.gameObject.AddComponent<VRCParentConstraint>();
            hold.Sources.Add(new VRCConstraintSource(home, 1));
            hold.Sources.Add(new VRCConstraintSource(carries[0], 0));
            hold.Sources.Add(new VRCConstraintSource(carries[1], 0));
            hold.IsActive = true; hold.Locked = true; hold.RebakeOffsetsWhenUnfrozen = true;
            // The glide: the place, or the home while gliding back.
            var glide = Child(shown, "Screen glide");
            var blend = glide.gameObject.AddComponent<VRCParentConstraint>();
            blend.Sources.Add(new VRCConstraintSource(place, 1));
            blend.Sources.Add(new VRCConstraintSource(home, 0));
            blend.IsActive = true; blend.Locked = true;
            // The view cone, at the eyes along the line of sight: local senders that cost nothing in the performance rank.
            for (int i = 0; i < ViewDistances.Length; i++)
            {
                var sphere = Child(gaze, "View " + i);
                sphere.localPosition = Vector3.forward * ViewDistances[i];
                var sender = sphere.gameObject.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = ViewDistances[i] * ViewWidth; sender.localOnly = true;
                sender.collisionTags.Add(ViewTag);
            }
            return glide;
        }

        /// <summary>
        /// The screen's handle and its middle's view receiver (on the screen, so they go where it goes): a small bar under
        /// the screen, the short PhysBone chain only its owner grabs (it moves nothing: it only says the grab), the hands'
        /// contacts saying which hand took it.
        /// </summary>
        private static void AddHandle(Transform orb, Transform screen, string folder)
        {
            var handle = Child(screen, "Handle");
            handle.localPosition = HandleFromScreen;
            var bar = Child(handle, "Bar");
            bar.localScale = new Vector3(.12f, .012f, .012f);
            bar.gameObject.AddComponent<MeshFilter>().sharedMesh = orb.Find(Body).GetComponent<MeshFilter>().sharedMesh;
            var barRenderer = bar.gameObject.AddComponent<MeshRenderer>();
            barRenderer.sharedMaterial = PointerMaterial(folder, true);
            Quiet(barRenderer);
            var bone = Child(handle, "Grab");
            Child(bone, "Grip").localPosition = Vector3.up * .02f;
            var grab = GrabChain(bone, HandleRadius, ScreenGrab, othersGrab: false);
            grab.enabled = false;
            HandContact(handle, "Left hand", ScreenHandL, true, HandleHandRadius, localOnly: true);
            HandContact(handle, "Right hand", ScreenHandR, false, HandleHandRadius, localOnly: true);
            handle.gameObject.SetActive(false);
            var view = Child(screen, "View").gameObject.AddComponent<VRCContactReceiver>();
            view.shapeType = ContactBase.ShapeType.Sphere; view.radius = .01f;
            view.allowSelf = true; view.allowOthers = false; view.localOnly = true;
            view.receiverType = ContactReceiver.ReceiverType.Constant; view.parameter = ScreenInView;
            view.collisionTags.Add(ViewTag);
        }

        /// <summary>Whether a placed orb's screen is placed as screen version 7's (its place, glide and handle).</summary>
        internal static bool HasPlacement(Transform orb) => orb != null && orb.Find(ScreenPlace) != null && orb.Find(ScreenGlide) != null &&
            orb.Find(ScreenHome) != null && orb.Find(ScreenHandle) != null && Hands.All(right => orb.Find(ScreenGrip(right) + "/" + Carry) != null);

        /// <summary>
        /// Fits a placed orb's screen to <paramref name="avatar"/>: the grips to the middle of each palm, the place, the glide
        /// and the screen to the home before the eyes. The caller records Undo for the orb's fit; this records its own.
        /// </summary>
        private static void FitPlacement(Transform orb, Transform avatar, Animator animator)
        {
            if (!HasPlacement(orb)) return;
            foreach (bool right in Hands) FollowPalm(orb.Find(ScreenGrip(right)), animator, avatar, !right, "N4N0");
            var home = orb.Find(ScreenHome);
            var gaze = orb.Find(Gaze);
            var place = orb.Find(ScreenPlace);
            var glide = orb.Find(ScreenGlide);
            var screen = orb.Find(ScreenObject);
            Undo.RecordObjects(new Object[] { home, place, glide, screen }, "Fit orb screen");
            // As the aim constraint turns it: facing the eyes, upright.
            var toEyes = gaze.position - home.position;
            home.rotation = Quaternion.LookRotation(-toEyes, Vector3.up);
            place.SetPositionAndRotation(home.position, home.rotation);
            glide.SetPositionAndRotation(home.position, home.rotation);
            screen.SetPositionAndRotation(home.position, home.rotation);
            var hold = screen.GetComponent<VRCParentConstraint>();
            if (hold != null && hold.Sources.Count > 0)
            {
                Undo.RecordObject(hold, "Fit orb screen");
                hold.Sources[0] = new VRCConstraintSource(glide, 1, Vector3.zero, Vector3.zero);
                EditorUtility.SetDirty(hold);
            }
        }

        // ---- The animator ----

        /// <summary>
        /// The screen's placement layers and parameters: where it is (every client, from the synced bools), whether it stays
        /// (its owner's, from the view contact), its owner's hold on the handle, its size and the handle shown to its owner
        /// while it is open.
        /// </summary>
        private static void PlacementController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters)
        {
            foreach (var name in new[] { ScreenPlaced, ScreenHeldLeft, ScreenHeldRight, ScreenInView, ScreenGrab + "_IsGrabbed", ScreenHandL, ScreenHandR })
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenSize, type = AnimatorControllerParameterType.Float, defaultFloat = DefaultScreenSize });
            foreach (var gesture in new[] { "GestureLeft", "GestureRight" })
                if (!controller.parameters.Any(p => p.name == gesture)) controller.AddParameter(gesture, AnimatorControllerParameterType.Int);
            PlaceLayer(controller);
            StaysLayer(controller);
            HandleGripLayer(controller);
            SizeLayer(controller);
            HandleLayer(controller);
            parameters.Add(Parameter(ScreenPlaced));
            parameters.Add(Parameter(ScreenHeldLeft));
            parameters.Add(Parameter(ScreenHeldRight));
            parameters.Add(Parameter(ScreenSize, type: VRCExpressionParameters.ValueType.Float, defaultValue: DefaultScreenSize));
        }

        /// <summary>
        /// Where the screen is, on every client: following its home (not placed), frozen where it is (placed), gliding back
        /// to its home (no longer placed: the glide blends from the place to the home, then the place catches up with the
        /// home before the glide follows it again), or riding a palm (held: frozen for an update first, so unfreezing
        /// measures where the screen is from that palm's carry frame).
        /// </summary>
        private static void PlaceLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Screen place");
            var follow = HeldPropRig.State(controller, machine, "Home", PlaceClip("Screen home", false, PlaceHome, 1));
            var placed = HeldPropRig.State(controller, machine, "Placed", PlaceClip("Screen placed", true, PlaceHome, 1));
            var glide = HeldPropRig.State(controller, machine, "Glide", GlideClip());
            var caught = HeldPropRig.State(controller, machine, "Glide end", PlaceClip("Screen glide end", false, PlaceHome, 0, .05f));
            var catchLeft = HeldPropRig.State(controller, machine, "Left catch", PlaceClip("Screen left catch", true, PlaceLeft, 1));
            var catchRight = HeldPropRig.State(controller, machine, "Right catch", PlaceClip("Screen right catch", true, PlaceRight, 1));
            var left = HeldPropRig.State(controller, machine, "Left hand", PlaceClip("Screen left hand", false, PlaceLeft, 1));
            var right = HeldPropRig.State(controller, machine, "Right hand", PlaceClip("Screen right hand", false, PlaceRight, 1));
            machine.defaultState = follow;
            var heldLeft = (ScreenHeldLeft, AnimatorConditionMode.If, 0f);
            var heldRight = (ScreenHeldRight, AnimatorConditionMode.If, 0f);
            var notLeft = (ScreenHeldLeft, AnimatorConditionMode.IfNot, 0f);
            var notRight = (ScreenHeldRight, AnimatorConditionMode.IfNot, 0f);
            foreach (var state in new[] { follow, placed, glide, caught })
            {
                Transition(state, catchLeft, heldLeft);
                Transition(state, catchRight, heldRight, notLeft);
            }
            Transition(follow, placed, (ScreenPlaced, AnimatorConditionMode.If, 0));
            Transition(placed, glide, (ScreenPlaced, AnimatorConditionMode.IfNot, 0));
            Exit(glide, caught);
            Exit(caught, placed, (ScreenPlaced, AnimatorConditionMode.If, 0));
            Exit(caught, follow, (ScreenPlaced, AnimatorConditionMode.IfNot, 0));
            // The next update: frozen where it was taken, then riding the palm from there.
            Transition(catchLeft, left, heldLeft);
            Transition(catchRight, right, heldRight);
            Transition(catchLeft, placed, notLeft);
            Transition(catchRight, placed, notRight);
            // Let go: it stays where it is (its owner's latch keeps it placed); taken by the other hand: caught again.
            Transition(left, placed, notLeft, notRight);
            Transition(right, placed, notLeft, notRight);
            Transition(left, catchRight, notLeft, heldRight);
            Transition(right, catchLeft, notRight, heldLeft);
            foreach (var state in new[] { follow, placed, glide, caught, catchLeft, catchRight, left, right }) state.writeDefaultValues = true;
        }

        // Leaves `from` once its clip has played once (and the conditions hold).
        private static void Exit(AnimatorState from, AnimatorState to, params (string name, AnimatorConditionMode mode, float value)[] conditions)
        {
            var transition = Transition(from, to, conditions);
            transition.hasExitTime = true; transition.exitTime = 1;
        }

        // The place frozen or not with one source weighed in (its home's offsets back to 0), and the glide on the place
        // (`onPlace` 1) or on the home (0).
        private static AnimationClip PlaceClip(string name, bool frozen, int source, float onPlace, float length = .2f)
        {
            var clip = new AnimationClip { name = name };
            void Set(string path, Type type, string property, float value) =>
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, length, value));
            foreach (string offset in new[] { "ParentPositionOffset", "ParentRotationOffset" })
            foreach (string axis in new[] { "x", "y", "z" })
                Set(ScreenPlace, typeof(VRCParentConstraint), "Sources.source" + PlaceHome + "." + offset + "." + axis, 0);
            Set(ScreenPlace, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1 : 0);
            for (int s = PlaceHome; s <= PlaceRight; s++) Set(ScreenPlace, typeof(VRCParentConstraint), "Sources.source" + s + ".Weight", s == source ? 1 : 0);
            Set(ScreenGlide, typeof(VRCParentConstraint), "Sources.source0.Weight", onPlace);
            Set(ScreenGlide, typeof(VRCParentConstraint), "Sources.source1.Weight", 1 - onPlace);
            if (length < .2f) Once(clip);
            return clip;
        }

        // Gliding back: the place frozen where the screen stayed, the glide easing from it over to the home.
        private static AnimationClip GlideClip()
        {
            var clip = PlaceClip("Screen glide", true, PlaceHome, 1);
            var ease = AnimationCurve.EaseInOut(0, 1, GlideTime, 0);
            var rise = AnimationCurve.EaseInOut(0, 0, GlideTime, 1);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(ScreenGlide, typeof(VRCParentConstraint), "Sources.source0.Weight"), ease);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(ScreenGlide, typeof(VRCParentConstraint), "Sources.source1.Weight"), rise);
            // The other curves end with the glide.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.path == ScreenGlide) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, GlideTime, curve.keys[0].value));
            }
            Once(clip);
            return clip;
        }

        // A clip that plays once (its state's exit time then counts its own length).
        private static void Once(AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        /// <summary>
        /// Whether the screen stays, its owner's call (synced as <see cref="ScreenPlaced"/>): it does once its middle is in
        /// their sight, and goes on staying until it has been out of their sight for <see cref="LookAwayTime"/> (a closed
        /// screen is out of sight, so it opens again before the eyes) or while a hand holds it.
        /// </summary>
        private static void StaysLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Screen stays");
            var following = HeldPropRig.State(controller, machine, "Following", new AnimationClip { name = "Screen following" });
            var staying = HeldPropRig.State(controller, machine, "Staying", new AnimationClip { name = "Screen staying" });
            var away = new AnimationClip { name = "Screen looked away" };
            // Its length is the wait: a parameter of its own, which nothing reads.
            AnimationUtility.SetEditorCurve(away, EditorCurveBinding.FloatCurve("", typeof(Animator), One), AnimationCurve.Constant(0, LookAwayTime, 1));
            Once(away);
            var lookingAway = HeldPropRig.State(controller, machine, "Looking away", away);
            var holding = HeldPropRig.State(controller, machine, "Holding", new AnimationClip { name = "Screen holding" });
            machine.defaultState = following;
            Drive(following, (ScreenPlaced, false));
            Drive(staying, (ScreenPlaced, true));
            Drive(holding, (ScreenPlaced, true));
            var local = ("IsLocal", AnimatorConditionMode.If, 0f);
            Transition(following, staying, local, (ScreenInView, AnimatorConditionMode.If, 0));
            Transition(staying, lookingAway, (ScreenInView, AnimatorConditionMode.IfNot, 0));
            Transition(lookingAway, staying, (ScreenInView, AnimatorConditionMode.If, 0));
            Exit(lookingAway, following, (ScreenInView, AnimatorConditionMode.IfNot, 0));
            foreach (var state in new[] { following, staying, lookingAway })
            {
                Transition(state, holding, local, (ScreenHeldLeft, AnimatorConditionMode.If, 0));
                Transition(state, holding, local, (ScreenHeldRight, AnimatorConditionMode.If, 0));
            }
            Transition(holding, staying, (ScreenHeldLeft, AnimatorConditionMode.IfNot, 0), (ScreenHeldRight, AnimatorConditionMode.IfNot, 0));
        }

        /// <summary>
        /// Its owner's hold on the handle, latched and synced (as the hand screen's): a grab with the hand at the handle
        /// gripping takes it, the hand relaxing lets go. A PhysBone grab only starts the hold: Touch controllers' grab and
        /// pose inputs can release it mid-squeeze.
        /// </summary>
        private static void HandleGripLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Screen grip");
            var free = HeldPropRig.State(controller, machine, "Free", new AnimationClip { name = "Screen grip free" });
            var holdLeft = HeldPropRig.State(controller, machine, "Hold left", new AnimationClip { name = "Screen grip left" });
            var holdRight = HeldPropRig.State(controller, machine, "Hold right", new AnimationClip { name = "Screen grip right" });
            machine.defaultState = free;
            Hold(free, ScreenHeldLeft, ScreenHeldRight, false, false);
            Hold(holdLeft, ScreenHeldLeft, ScreenHeldRight, true, false);
            Hold(holdRight, ScreenHeldLeft, ScreenHeldRight, false, true);
            foreach (var (state, hand, gesture) in new[] { (holdLeft, ScreenHandL, "GestureLeft"), (holdRight, ScreenHandR, "GestureRight") })
            {
                Transition(free, state, new[] { ("IsLocal", AnimatorConditionMode.If, 0f), (ScreenGrab + "_IsGrabbed", AnimatorConditionMode.If, 0f),
                    (hand, AnimatorConditionMode.If, 0f) }.Concat(Gripping(gesture)).ToArray());
                LetGo(state, free, gesture);
            }
        }

        /// <summary>The screen's size from <see cref="ScreenSize"/>, on every client (it is synced).</summary>
        private static void SizeLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Screen size");
            var size = new BlendTree { name = "Screen size", blendType = BlendTreeType.Simple1D, blendParameter = ScreenSize, useAutomaticThresholds = false };
            foreach (var (at, scale) in new[] { (0f, SmallestScreen), (1f, LargestScreen) })
            {
                var clip = new AnimationClip { name = "Screen size " + scale };
                foreach (var axis in new[] { "x", "y", "z" }) Curve(clip, ScreenObject, typeof(Transform), "m_LocalScale." + axis, scale);
                size.AddChild(clip, at);
            }
            var state = HeldPropRig.State(controller, machine, "Size", size);
            state.writeDefaultValues = true;
            machine.defaultState = state;
        }

        /// <summary>The handle, shown to its owner and grabbable while the screen is open.</summary>
        private static void HandleLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Screen handle");
            AnimationClip Clip(string name, bool on)
            {
                var clip = new AnimationClip { name = name };
                Curve(clip, ScreenHandle, typeof(GameObject), "m_IsActive", on ? 1 : 0);
                Curve(clip, ScreenHandle + "/Grab", typeof(VRCPhysBone), "m_Enabled", on ? 1 : 0);
                return clip;
            }
            var off = HeldPropRig.State(controller, machine, "Off", Clip("Screen handle hidden", false));
            var on = HeldPropRig.State(controller, machine, "On", Clip("Screen handle shown", true));
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            foreach (var trigger in new[] { ScreenOpen, Menu })
                Transition(off, on, ("IsLocal", AnimatorConditionMode.If, 0), (trigger, AnimatorConditionMode.If, 0));
            Transition(on, off, (ScreenOpen, AnimatorConditionMode.IfNot, 0), (Menu, AnimatorConditionMode.IfNot, 0));
        }
    }
}
