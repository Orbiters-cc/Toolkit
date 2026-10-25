using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's link card (Orbiters contracts, Docs/orb.md, Link card): Orbiters - N4N0 drops its screen in the world for a
    /// few seconds with a link on it (its thumbnail, title, the link written out and "Grab to copy link"). Its owner's screen
    /// stays where it is while <see cref="ScreenDrop"/> is on; everyone else's from the text channel's
    /// <see cref="PointerCardDropped"/> until <see cref="PointerScreenClose"/> closes it. While dropped the screen carries a
    /// contact sender (<see cref="CardTag"/>) that every N4N0's hands feel (<see cref="CardTouch"/>, local receivers that
    /// cost nothing in the performance rank): a grab tells Orbiters - N4N0 to copy the link, its own card's or, through
    /// Orbiters, the one someone else dropped in the same instance.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Local, set by Orbiters - N4N0: its owner's screen is a dropped card, held where it is.</summary>
        public const string ScreenDrop = "N4N0/Screen/Drop";
        /// <summary>Local, one per hand: the hand touches a link card (its own N4N0's or someone else's).</summary>
        public const string CardTouchPrefix = "N4N0/Card/Touch";
        /// <summary>The dropped card's contact tag.</summary>
        public const string CardTag = "N4N0Card";
        /// <summary><see cref="TextPointer"/>: everyone else's screen is a dropped card, held where it is until it closes.</summary>
        public const int PointerCardDropped = 253;
        internal const string CardSender = ScreenObject + "/Card", CardHands = Shown + "/Card hands";
        // How far around the middle of a hand a card is felt (at 1.6 m eye height: it scales with the avatar).
        internal const float CardHandRadius = .07f;

        public static string CardTouch(bool right) => CardTouchPrefix + (right ? "R" : "L");
        internal static string CardHand(bool right) => CardHands + "/Hand " + (right ? "R" : "L");

        /// <summary>Whether a placed orb has the link card's sender and hands.</summary>
        internal static bool HasCard(Transform orb) => orb != null && orb.Find(CardSender) != null && orb.Find(CardHands) != null;

        /// <summary>
        /// Gives a placed orb's screen the link card's contact sender (off until a card is dropped) and its owner's hands
        /// their receivers (with Undo). The hands wait for <see cref="Bind"/> to hold them to the avatar's hands.
        /// </summary>
        internal static void AddCard(Transform orb)
        {
            var screen = orb.Find(ScreenObject);
            var shown = orb.Find(Shown);
            if (screen == null || shown == null) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            if (screen.Find("Card") == null)
            {
                var card = new GameObject("Card");
                Undo.RegisterCreatedObjectUndo(card, "N4N0 link card");
                card.transform.SetParent(screen, false);
                card.SetActive(false);
                // Everyone's hands feel it: not local. A capsule across the screen, a little thicker than it.
                var sender = card.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Capsule;
                sender.radius = ScreenHeight / 2; sender.height = ScreenWidth; sender.rotation = Quaternion.Euler(0, 0, 90);
                sender.localOnly = false;
                sender.collisionTags.Add(CardTag);
            }
            if (orb.Find(CardHands) != null) return;
            var hands = new GameObject("Card hands");
            Undo.RegisterCreatedObjectUndo(hands, "N4N0 link card");
            hands.transform.SetParent(shown, false);
            foreach (bool right in Hands)
            {
                var hand = Child(hands.transform, "Hand " + (right ? "R" : "L"));
                // Held to the hand (sources set by Bind).
                var hold = hand.gameObject.AddComponent<VRCParentConstraint>();
                hold.Sources.Add(new VRCConstraintSource(null, 1));
                hold.IsActive = true; hold.Locked = true;
                var receiver = hand.gameObject.AddComponent<VRCContactReceiver>();
                receiver.shapeType = ContactBase.ShapeType.Sphere; receiver.radius = CardHandRadius;
                receiver.allowSelf = true; receiver.allowOthers = true; receiver.localOnly = true;
                receiver.receiverType = ContactReceiver.ReceiverType.Constant; receiver.parameter = CardTouch(right);
                receiver.collisionTags.Add(CardTag);
            }
        }

        /// <summary>
        /// Holds a placed orb's card hands to <paramref name="animator"/>'s hands, in the middle of each (between the wrist
        /// and the middle finger's first joint), as large as N4N0 (<paramref name="size"/>).
        /// </summary>
        private static void BindCardHands(Transform orb, Animator animator, float size)
        {
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

        /// <summary>
        /// The link card's layer and parameters: the screen held where it is and its sender on while a card is dropped (its
        /// owner's from <see cref="ScreenDrop"/>, everyone else's from <see cref="PointerCardDropped"/> until the screen
        /// closes); the hands' touches, local.
        /// </summary>
        private static void CardController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters)
        {
            foreach (bool right in Hands) controller.AddParameter(CardTouch(right), AnimatorControllerParameterType.Bool);
            var machine = Layer(controller, "Card");
            var follows = new AnimationClip { name = "Card off" };
            Curve(follows, ScreenObject, typeof(VRCParentConstraint), "FreezeToWorld", 0);
            Curve(follows, CardSender, typeof(GameObject), "m_IsActive", 0);
            var dropped = new AnimationClip { name = "Card dropped" };
            Curve(dropped, ScreenObject, typeof(VRCParentConstraint), "FreezeToWorld", 1);
            Curve(dropped, CardSender, typeof(GameObject), "m_IsActive", 1);
            var off = HeldPropRig.State(controller, machine, "Off", follows);
            var on = HeldPropRig.State(controller, machine, "Dropped", dropped);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            Transition(off, on, ("IsLocal", AnimatorConditionMode.If, 0), (ScreenDrop, AnimatorConditionMode.If, 0));
            Transition(on, off, ("IsLocal", AnimatorConditionMode.If, 0), (ScreenDrop, AnimatorConditionMode.IfNot, 0));
            Transition(off, on, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Equals, PointerCardDropped));
            Transition(on, off, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Equals, PointerScreenClose));

            parameters.Add(Parameter(ScreenDrop, synced: false));
            foreach (bool right in Hands) parameters.Add(Parameter(CardTouch(right), synced: false));
        }
    }
}
