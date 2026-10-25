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
    /// N4N0's link card (Orbiters contracts, Docs/orb.md, Link card; screen version 7): an object of its own beside N4N0's
    /// screen. Orbiters - N4N0 puts it out (<see cref="CardOut"/>, synced): it appears in front of its owner, N4N0's beam
    /// projecting it for a moment, and stays there in the world, wherever N4N0 and its owner go, until Orbiters - N4N0
    /// takes it back. Anyone may grab it and move it (its PhysBone pickup; its owner's hand latches it to the palm, as the
    /// hand screen's), and every N4N0's hands feel it (<see cref="CardTag"/>, <see cref="CardTouch"/>, local receivers):
    /// a grab tells Orbiters - N4N0 to copy the link, its own card's or, through Orbiters, the one someone else dropped in
    /// the same instance. It is drawn by the screen's own shader from a memory of its own: the words of the screen's memory
    /// the card page reads (contracts orb::card), written to every client, its owner's too, through the text channel's
    /// pointers past the screen's (<see cref="CardPointers"/>; <see cref="PointerCardClear"/> clears it).
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Synced, set by Orbiters - N4N0: the card is out.</summary>
        public const string CardOut = "N4N0/Card/Out";
        /// <summary>Synced, the owner's latch: a hand of theirs holds the card.</summary>
        public const string CardHeldLeft = "N4N0/Card/HeldLeft", CardHeldRight = "N4N0/Card/HeldRight";
        /// <summary>The card's pickup (VRChat adds <c>_IsGrabbed</c>) and which of the owner's hands is at it (local contacts).</summary>
        public const string CardGrab = "N4N0/Card/Grab", CardHandL = "N4N0/Card/HandL", CardHandR = "N4N0/Card/HandR";
        /// <summary>Local, one per hand: the hand touches a link card (its own N4N0's or someone else's).</summary>
        public const string CardTouchPrefix = "N4N0/Card/Touch";
        /// <summary>The card's contact tag.</summary>
        public const string CardTag = "N4N0Card";
        /// <summary><see cref="TextPointer"/>: every client's card memory clears (before a new card's blocks).</summary>
        public const int PointerCardClear = 252;
        /// <summary><see cref="TextPointer"/>: the highest pointer anything writes (253 and 254 belong to older screens).</summary>
        public const int PointerCardDropped = 253;
        internal const string CardRoot = Shown + "/Card", CardHome = Head + "/Card home", CardPlace = CardRoot + "/Place";
        internal const string CardModel = CardPlace + "/Model", CardSender = CardModel + "/Sender", CardGrabBase = CardRoot + "/Grab base";
        internal const string CardHands = Shown + "/Card hands";
        // The card's place's sources: its home before the owner, the palms' carry frames, the pickup's grip.
        internal const int CardAtHome = 0, CardInLeft = 1, CardInRight = 2, CardOnGrip = 3;
        // How far around the middle of a hand a card is felt; the card's size (times the screen's); where it appears from
        // the eyes (right, up, forward, at 1.6 m eye height: N4N0's side, low); how long N4N0's beam projects it.
        internal const float CardHandRadius = .07f, CardScale = .7f, CardBeamTime = 1.5f, CardGrabRadius = .12f;
        internal static readonly Vector3 CardFromEyes = new Vector3(.12f, -.22f, .55f);
        // The card's animator parameters: its block (one update late), its digits (the text's late values), its words.
        internal const string CardBlock = "N4N0/Card/Block";
        internal static string CardDigit(int i) => "N4N0/Card/Digit" + i;
        internal static string CardWord(int j) => "N4N0/Card/Word" + j;

        public static string CardTouch(bool right) => CardTouchPrefix + (right ? "R" : "L");
        internal static string CardHand(bool right) => CardHands + "/Hand " + (right ? "R" : "L");

        /// <summary>Whether a placed orb has the card of its own and its owner's hands' receivers.</summary>
        internal static bool HasCard(Transform orb) => orb != null && orb.Find(CardModel) != null && orb.Find(CardHands) != null;

        // ---- Its memory (contracts orb::card) ----

        /// <summary>The screen memory's bytes with thumbnails of <paramref name="thumbnailBytes"/>.</summary>
        internal static int ScreenMemoryBytes(int thumbnailBytes) => 384 + 5 * thumbnailBytes;

        /// <summary>
        /// The screen memory's words the card keeps, in its order: the page's, the thumbnails' layer counts', the card page's
        /// text (bytes 24 to 109) and, with <paramref name="picture"/>, thumbnail 0's.
        /// </summary>
        internal static List<int> CardWords(int thumbnailBytes, bool picture)
        {
            var words = new List<int> { 0, 8 };
            words.AddRange(Enumerable.Range(12, 43));
            if (picture) words.AddRange(Enumerable.Range(192, thumbnailBytes / 2));
            return words;
        }

        /// <summary>The card's first pointer (the next past the screen's) and its blocks, for <paramref name="textSync"/> values.</summary>
        internal static (int first, int blocks) CardPointers(int textSync, int thumbnailBytes, bool picture) =>
            (Cells / textSync + 1 + (ScreenMemoryBytes(thumbnailBytes) + textSync - 1) / textSync, (2 * CardWords(thumbnailBytes, picture).Count + textSync - 1) / textSync);

        /// <summary>Whether the card keeps its picture: its blocks with it stay below <see cref="PointerCardClear"/>.</summary>
        internal static bool CardPictureFits(int textSync, int thumbnailBytes)
        {
            var (first, blocks) = CardPointers(textSync, thumbnailBytes, true);
            return first + blocks <= PointerCardClear;
        }

        // ---- On the prop ----

        /// <summary>
        /// The card under a placed orb as its prefab is built (its screen's place and grips exist): its home before the eyes,
        /// its place, its model drawn by the screen's mesh and materials (<paramref name="mesh"/>, <paramref name="pages"/>),
        /// its sender, its pickup and its owner's hands' contacts; and the owner's hands' receivers for every card.
        /// </summary>
        internal static void AddCard(Transform orb, Mesh mesh, Material pages)
        {
            var shown = orb.Find(Shown);
            var head = orb.Find(Head);
            var emitter = orb.Find(Emitter);
            if (shown == null || head == null || emitter == null) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            var home = Child(head, "Card home");
            home.localPosition = DefaultEyes + CardFromEyes;
            var root = Child(shown, "Card");
            // Its place: unfreezing measures where it is from each source (the animator sets its home's back to 0).
            var place = Child(root, "Place");
            var hold = place.gameObject.AddComponent<VRCParentConstraint>();
            hold.Sources.Add(new VRCConstraintSource(home, 1));
            hold.Sources.Add(new VRCConstraintSource(orb.Find(ScreenGrip(false) + "/" + Carry), 0));
            hold.Sources.Add(new VRCConstraintSource(orb.Find(ScreenGrip(true) + "/" + Carry), 0));
            hold.Sources.Add(new VRCConstraintSource(null, 0));
            hold.IsActive = true; hold.Locked = true; hold.RebakeOffsetsWhenUnfrozen = true;
            // The pickup: a short chain under the card's middle that anyone grabs; it moves nothing by itself (the place
            // follows its grip while someone else holds it, the grab base then frozen).
            var basis = Child(root, "Grab base");
            var follow = basis.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(place, 1));
            follow.IsActive = true; follow.Locked = true;
            var bone = Child(basis, "Bone");
            var grip = Child(bone, "Grip"); grip.localPosition = Vector3.up * .02f;
            var grab = GrabChain(bone, CardGrabRadius, CardGrab, othersGrab: true);
            grab.enabled = false;
            var source = hold.Sources[CardOnGrip];
            source.SourceTransform = grip;
            hold.Sources[CardOnGrip] = source;
            // Its model: the screen's mesh and materials (the page, N4N0's beam to its corners, no rays), as large as
            // CardScale of the screen, its memory animated on it.
            var model = Child(place, "Model");
            model.localScale = Vector3.one * CardScale;
            model.gameObject.SetActive(false);
            var renderer = model.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { model, emitter, model, model }; renderer.rootBone = model; renderer.quality = SkinQuality.Bone1;
            SetScreenMaterials(renderer, pages, false);
            renderer.localBounds = new Bounds(new Vector3(0, .1f, -.4f), Vector3.one * 1.6f);
            Quiet(renderer);
            // Everyone's hands feel it: not local. A capsule across the card, a little thicker than it.
            var sender = Child(model, "Sender").gameObject.AddComponent<VRCContactSender>();
            sender.shapeType = ContactBase.ShapeType.Capsule;
            sender.radius = ScreenHeight / 2; sender.height = ScreenWidth; sender.rotation = Quaternion.Euler(0, 0, 90);
            sender.localOnly = false;
            sender.collisionTags.Add(CardTag);
            HandContact(model, "Left hand", CardHandL, true, .25f, localOnly: true);
            HandContact(model, "Right hand", CardHandR, false, .25f, localOnly: true);

            // The owner's hands, feeling every card (its own and everyone else's).
            var hands = Child(shown, "Card hands");
            foreach (bool right in Hands)
            {
                var hand = Child(hands, "Hand " + (right ? "R" : "L"));
                // Held to the hand (sources set by Bind).
                var handHold = hand.gameObject.AddComponent<VRCParentConstraint>();
                handHold.Sources.Add(new VRCConstraintSource(null, 1));
                handHold.IsActive = true; handHold.Locked = true;
                var receiver = hand.gameObject.AddComponent<VRCContactReceiver>();
                receiver.shapeType = ContactBase.ShapeType.Sphere; receiver.radius = CardHandRadius;
                receiver.allowSelf = true; receiver.allowOthers = true; receiver.localOnly = true;
                receiver.receiverType = ContactReceiver.ReceiverType.Constant; receiver.parameter = CardTouch(right);
                receiver.collisionTags.Add(CardTag);
            }
        }

        /// <summary>
        /// Fits a placed orb's card to <paramref name="animator"/>: its home before the eyes (N4N0's side, low), its owner's
        /// hands' receivers in the middle of each hand, as large as N4N0 (<paramref name="size"/>).
        /// </summary>
        private static void BindCardHands(Transform orb, Animator animator, float size)
        {
            var home = orb.Find(CardHome);
            var gaze = orb.Find(Gaze);
            if (home != null && gaze != null)
            {
                Undo.RecordObject(home, "Fit N4N0 card");
                home.localPosition = home.parent.InverseTransformPoint(gaze.position) + CardFromEyes;
                home.localRotation = Quaternion.identity;
                foreach (var path in new[] { CardPlace, CardGrabBase })
                {
                    var t = orb.Find(path);
                    if (t == null) continue;
                    Undo.RecordObject(t, "Fit N4N0 card");
                    t.SetPositionAndRotation(home.position, home.rotation);
                }
            }
            foreach (bool right in Hands)
            {
                var hand = orb.Find(CardHand(right));
                var hold = hand != null ? hand.GetComponent<VRCParentConstraint>() : null;
                if (hold == null || hold.Sources.Count < 1) continue;
                var wrist = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
                Undo.RecordObjects(new Object[] { hand, hold }, "Fit N4N0 card hands");
                if (wrist == null)
                {
                    hold.Sources[0] = new VRCConstraintSource(null, 1);
                    hand.gameObject.SetActive(false);
                    continue;
                }
                hand.gameObject.SetActive(true);
                var knuckle = animator.GetBoneTransform(right ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
                hand.SetPositionAndRotation(knuckle != null ? (wrist.position + knuckle.position) / 2 : wrist.position, wrist.rotation);
                float parent = hand.parent != null ? Mathf.Abs(hand.parent.lossyScale.x) : 1;
                hand.localScale = Vector3.one * (size / Mathf.Max(parent, 1e-4f));
                // Offsets in the hand bone's axes, unscaled.
                var turn = Quaternion.Inverse(wrist.rotation);
                hold.Sources[0] = new VRCConstraintSource(wrist, 1, turn * (hand.position - wrist.position), Vector3.zero);
                EditorUtility.SetDirty(hold);
            }
        }

        // ---- The animator ----

        /// <summary>
        /// The card's layers and parameters: where it is (every client, from the synced bools and its pickup), its owner's
        /// hold, N4N0's beam as it appears, its memory (its blocks from the text channel, on every client); the hands'
        /// touches, local.
        /// </summary>
        private static void CardController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters, int textSync, int thumbnailBytes)
        {
            foreach (var name in new[] { CardOut, CardHeldLeft, CardHeldRight, CardGrab + "_IsGrabbed", CardHandL, CardHandR, CardTouch(false), CardTouch(true) })
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            foreach (var gesture in new[] { "GestureLeft", "GestureRight" })
                if (!controller.parameters.Any(p => p.name == gesture)) controller.AddParameter(gesture, AnimatorControllerParameterType.Int);
            CardPlaceLayer(controller);
            CardGripLayer(controller);
            CardBeamLayer(controller);
            CardMemoryLayers(controller, textSync, thumbnailBytes);
            parameters.Add(Parameter(CardOut));
            parameters.Add(Parameter(CardHeldLeft));
            parameters.Add(Parameter(CardHeldRight));
            foreach (bool right in Hands) parameters.Add(Parameter(CardTouch(right), synced: false));
        }

        // Where the card is: off (waiting at its home), out and frozen, held by someone's pickup (its grab base frozen, the
        // card on the grip), or on its owner's palm; a frozen update before each hold, so unfreezing measures it from there.
        private static void CardPlaceLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Card place");
            AnimationClip Clip(string name, bool shown, bool frozen, int source, bool baseFrozen)
            {
                var clip = new AnimationClip { name = name };
                foreach (string offset in new[] { "ParentPositionOffset", "ParentRotationOffset" })
                foreach (string axis in new[] { "x", "y", "z" })
                    Curve(clip, CardPlace, typeof(VRCParentConstraint), "Sources.source" + CardAtHome + "." + offset + "." + axis, 0);
                Curve(clip, CardPlace, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1 : 0);
                for (int s = CardAtHome; s <= CardOnGrip; s++) Curve(clip, CardPlace, typeof(VRCParentConstraint), "Sources.source" + s + ".Weight", s == source ? 1 : 0);
                Curve(clip, CardGrabBase, typeof(VRCParentConstraint), "FreezeToWorld", baseFrozen ? 1 : 0);
                Curve(clip, CardGrabBase + "/Bone", typeof(VRCPhysBone), "m_Enabled", shown ? 1 : 0);
                Curve(clip, CardModel, typeof(GameObject), "m_IsActive", shown ? 1 : 0);
                return clip;
            }
            var off = HeldPropRig.State(controller, machine, "Off", Clip("Card off", false, false, CardAtHome, false));
            var appear = HeldPropRig.State(controller, machine, "Appear", Clip("Card appears", true, false, CardAtHome, false));
            var placed = HeldPropRig.State(controller, machine, "Out", Clip("Card out", true, true, CardAtHome, false));
            var guestCatch = HeldPropRig.State(controller, machine, "Grip catch", Clip("Card grip catch", true, true, CardOnGrip, true));
            var guest = HeldPropRig.State(controller, machine, "On the grip", Clip("Card on the grip", true, false, CardOnGrip, true));
            var leftCatch = HeldPropRig.State(controller, machine, "Left catch", Clip("Card left catch", true, true, CardInLeft, false));
            var left = HeldPropRig.State(controller, machine, "Left hand", Clip("Card left hand", true, false, CardInLeft, false));
            var rightCatch = HeldPropRig.State(controller, machine, "Right catch", Clip("Card right catch", true, true, CardInRight, false));
            var right = HeldPropRig.State(controller, machine, "Right hand", Clip("Card right hand", true, false, CardInRight, false));
            var all = new[] { off, appear, placed, guestCatch, guest, leftCatch, left, rightCatch, right };
            foreach (var state in all) state.writeDefaultValues = true;
            machine.defaultState = off;
            var isOut = (CardOut, AnimatorConditionMode.If, 0f);
            foreach (var state in all.Skip(1)) Transition(state, off, (CardOut, AnimatorConditionMode.IfNot, 0));
            Transition(off, appear, isOut);
            // The next update: where it appeared, frozen there.
            Transition(appear, placed, isOut);
            var grabbed = (CardGrab + "_IsGrabbed", AnimatorConditionMode.If, 0f);
            var free = (CardGrab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0f);
            var heldLeft = (CardHeldLeft, AnimatorConditionMode.If, 0f);
            var heldRight = (CardHeldRight, AnimatorConditionMode.If, 0f);
            var notLeft = (CardHeldLeft, AnimatorConditionMode.IfNot, 0f);
            var notRight = (CardHeldRight, AnimatorConditionMode.IfNot, 0f);
            Transition(placed, guestCatch, isOut, grabbed, notLeft, notRight);
            Transition(guestCatch, guest, isOut, grabbed);
            foreach (var state in new[] { guestCatch, guest }) Transition(state, placed, isOut, free);
            foreach (var state in new[] { placed, guestCatch, guest })
            {
                Transition(state, leftCatch, isOut, heldLeft);
                Transition(state, rightCatch, isOut, heldRight, notLeft);
            }
            Transition(leftCatch, left, isOut, heldLeft);
            Transition(rightCatch, right, isOut, heldRight);
            Transition(leftCatch, placed, isOut, notLeft);
            Transition(rightCatch, placed, isOut, notRight);
            Transition(left, placed, isOut, notLeft);
            Transition(right, placed, isOut, notRight);
        }

        // The owner's hold, latched and synced: their pickup grab with the hand at the card gripping; relaxing lets go.
        private static void CardGripLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Card grip");
            var free = HeldPropRig.State(controller, machine, "Free", new AnimationClip { name = "Card grip free" });
            var holdLeft = HeldPropRig.State(controller, machine, "Hold left", new AnimationClip { name = "Card grip left" });
            var holdRight = HeldPropRig.State(controller, machine, "Hold right", new AnimationClip { name = "Card grip right" });
            machine.defaultState = free;
            Hold(free, CardHeldLeft, CardHeldRight, false, false);
            Hold(holdLeft, CardHeldLeft, CardHeldRight, true, false);
            Hold(holdRight, CardHeldLeft, CardHeldRight, false, true);
            foreach (var (state, hand, gesture) in new[] { (holdLeft, CardHandL, "GestureLeft"), (holdRight, CardHandR, "GestureRight") })
            {
                Transition(free, state, new[] { ("IsLocal", AnimatorConditionMode.If, 0f), (CardOut, AnimatorConditionMode.If, 0f), (CardGrab + "_IsGrabbed", AnimatorConditionMode.If, 0f),
                    (hand, AnimatorConditionMode.If, 0f) }.Concat(Gripping(gesture)).ToArray());
                LetGo(state, free, gesture);
                Transition(state, free, (CardOut, AnimatorConditionMode.IfNot, 0));
            }
        }

        // N4N0's beam projects the card as it appears, fading out, then the card stands alone.
        private static void CardBeamLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Card beam");
            var none = new AnimationClip { name = "Card beam off" };
            Curve(none, CardModel, typeof(SkinnedMeshRenderer), "material._Beam", 0);
            var fade = new AnimationClip { name = "Card beam projects" };
            AnimationUtility.SetEditorCurve(fade, EditorCurveBinding.FloatCurve(CardModel, typeof(SkinnedMeshRenderer), "material._Beam"),
                new AnimationCurve(new Keyframe(0, 1), new Keyframe(CardBeamTime * .4f, .8f), new Keyframe(CardBeamTime, 0)));
            Once(fade);
            var hidden = HeldPropRig.State(controller, machine, "Off", none);
            var projecting = HeldPropRig.State(controller, machine, "Projecting", fade);
            var alone = HeldPropRig.State(controller, machine, "Alone", none);
            foreach (var state in new[] { hidden, projecting, alone }) state.writeDefaultValues = true;
            machine.defaultState = hidden;
            Transition(hidden, projecting, (CardOut, AnimatorConditionMode.If, 0));
            Exit(projecting, alone);
            Transition(projecting, hidden, (CardOut, AnimatorConditionMode.IfNot, 0));
            Transition(alone, hidden, (CardOut, AnimatorConditionMode.IfNot, 0));
        }

        /// <summary>
        /// The card's memory, on every client: the text channel's pointer as the card's block (one update late; 252 clears
        /// it), and one direct blend tree that keeps every word, writes a block's words from the text's late values while
        /// its block is pointed at, or clears them all, and sets the card model's word properties from them.
        /// </summary>
        private static void CardMemoryLayers(AnimatorController controller, int textSync, int thumbnailBytes)
        {
            bool picture = CardPictureFits(textSync, thumbnailBytes);
            var words = CardWords(thumbnailBytes, picture);
            var (first, blocks) = CardPointers(textSync, thumbnailBytes, picture);
            controller.AddParameter(CardBlock, AnimatorControllerParameterType.Float);
            for (int i = 0; i < textSync; i++) controller.AddParameter(CardDigit(i), AnimatorControllerParameterType.Float);
            for (int j = 0; j < words.Count; j++) controller.AddParameter(CardWord(j), AnimatorControllerParameterType.Float);

            var blockMachine = Layer(controller, "Card block");
            AnimatorState Set(string name, float block) => HeldPropRig.State(controller, blockMachine, name, ParameterClip("Card " + name, CardBlock, block));
            var none = Set("None", 0);
            blockMachine.defaultState = none;
            for (int b = 1; b <= blocks; b++) Jump(blockMachine, Set("Block " + b, b), (TextPointer, AnimatorConditionMode.Equals, first + b - 1));
            Jump(blockMachine, Set("Clear", ScreenLocalClear), (TextPointer, AnimatorConditionMode.Equals, PointerCardClear));
            Jump(blockMachine, none, (TextPointer, AnimatorConditionMode.Less, first));
            Jump(blockMachine, none, (TextPointer, AnimatorConditionMode.Greater, first + blocks - 1), (TextPointer, AnimatorConditionMode.NotEqual, PointerCardClear));

            AnimationClip WordClip(int j, float value)
            {
                var clip = Hidden(new AnimationClip { name = "Card word " + j + " " + value });
                Curve(clip, "", typeof(Animator), CardWord(j), value);
                Curve(clip, CardModel, typeof(SkinnedMeshRenderer), WordProperty(words[j]), value);
                return clip;
            }
            var plus = Enumerable.Range(0, words.Count).Select(j => WordClip(j, 1)).ToArray();
            var minus = Enumerable.Range(0, words.Count).Select(j => WordClip(j, -1)).ToArray();
            var high = Enumerable.Range(0, words.Count).Select(j => WordClip(j, 256)).ToArray();
            void Add(List<ChildMotion> list, Motion motion, string weight) => list.Add(new ChildMotion { motion = motion, directBlendParameter = weight, timeScale = 1 });
            var children = new List<ChildMotion>();
            for (int i = 0; i < textSync; i++) Add(children, Digit(CardDigit(i), TextLate(i, ValueLag)), One);
            var keep = Hidden(new BlendTree { name = "Card keeps", blendType = BlendTreeType.Direct });
            var keeps = new List<ChildMotion>();
            for (int j = 0; j < words.Count; j++) Add(keeps, plus[j], CardWord(j));
            keep.children = keeps.ToArray();
            Add(children, keep, One);
            var writes = Hidden(new BlendTree { name = "Card writes", blendType = BlendTreeType.Simple1D, blendParameter = CardBlock, useAutomaticThresholds = false });
            writes.AddChild(Hidden(new AnimationClip { name = "Card writes none" }), 0);
            for (int b = 1; b <= blocks; b++)
            {
                var block = Hidden(new BlendTree { name = "Card writes " + b, blendType = BlendTreeType.Direct });
                var parts = new List<ChildMotion>();
                for (int k = 0; k < textSync / 2; k++)
                {
                    int j = (b - 1) * textSync / 2 + k;
                    if (j >= words.Count) break;
                    Add(parts, minus[j], CardWord(j));
                    Add(parts, plus[j], CardDigit(2 * k));
                    Add(parts, high[j], CardDigit(2 * k + 1));
                }
                block.children = parts.ToArray();
                writes.AddChild(block, b);
            }
            var clear = Hidden(new BlendTree { name = "Card writes clear", blendType = BlendTreeType.Direct });
            var cleared = new List<ChildMotion>();
            for (int j = 0; j < words.Count; j++) Add(cleared, minus[j], CardWord(j));
            clear.children = cleared.ToArray();
            writes.AddChild(clear, ScreenLocalClear);
            Add(children, writes, One);
            var tree = Hidden(new BlendTree { name = "Card", blendType = BlendTreeType.Direct, children = children.ToArray() });
            var memory = Layer(controller, "Card");
            var state = HeldPropRig.State(controller, memory, "Card", tree);
            state.writeDefaultValues = true;
            memory.defaultState = state;
        }
    }
}
