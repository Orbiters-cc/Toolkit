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
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Pointing at N4N0's screen (Orbiters contracts, Docs/orb.md, Pointing; screen version 7). Each hand carries a pointer
    /// where its index fingertip is with the finger straight, along the finger (held to the hand, so curling the finger to
    /// pull a trigger moves nothing), with two local contact senders: one at the fingertip, one further along
    /// the finger (<see cref="AimDistance"/>). Four receivers per sender on the screen tell Orbiters - N4N0 where both are,
    /// so it knows the ray the finger points along and where it meets the screen: hovered there, pressed with the trigger,
    /// or by the fingertip touching the screen. The beam and its dot are drawn by the screen itself, from the pointers'
    /// bones (OrbRay.shader.txt), at once and exactly. Everyone else's index fingertips (VRChat's FingerIndex contacts)
    /// have four receivers of their own: a touch presses too. Everything is local to its owner's client: no synced bits,
    /// nothing in the performance rank. VRChat's raycasts never meet their own avatar's colliders, which is why screens of
    /// version 4 to 6 never showed their beam.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Where the fingertip is: four receivers per hand, as <see cref="ScreenTouchPrefix"/> (<c>N4N0/Screen/Touch/L0</c> …).</summary>
        public const string TipTagPrefix = "N4N0Tip";
        /// <summary>Where the point further along the finger is: <c>N4N0/Screen/Aim/L0</c> … (tag <see cref="AimTagPrefix"/>).</summary>
        public const string ScreenAimPrefix = "N4N0/Screen/Aim/", AimTagPrefix = "N4N0Aim";
        /// <summary>Everyone else's index fingertip near the screen: <c>N4N0/Screen/Others0</c> … (VRChat's FingerIndex tag).</summary>
        public const string ScreenOthersPrefix = "N4N0/Screen/Others", OthersTag = "FingerIndex";
        internal const string Pointers = Shown + "/Pointers";
        // The senders' radius, how far along the finger the aim sender is, and how far the owner's receivers reach (at
        // 1.6 m eye height: they scale with the avatar; the receivers with the screen's size too).
        internal const float PointRadius = .002f, AimDistance = .3f, PointingReach = 1.6f;

        public static string TipTag(bool right) => TipTagPrefix + (right ? "R" : "L");
        public static string AimTag(bool right) => AimTagPrefix + (right ? "R" : "L");
        public static string ScreenAim(bool right, int index) => ScreenAimPrefix + (right ? "R" : "L") + index;
        public static string ScreenOthers(int index) => ScreenOthersPrefix + index;
        internal static string Pointer(bool right) => Pointers + "/Pointer " + (right ? "R" : "L");
        private static readonly bool[] Hands = { false, true };

        /// <summary>
        /// The pointers (under Shown, before the screen exists: the screen's renderer is bound to them): one per index
        /// fingertip, with its tip and aim senders. <see cref="Bind"/> holds them to the fingertips.
        /// </summary>
        private static Transform[] AddPointers(Transform orb)
        {
            var shown = orb.Find(Shown);
            var pointers = Child(shown, "Pointers");
            pointers.gameObject.SetActive(false);
            return Hands.Select(right =>
            {
                var pointer = Child(pointers, "Pointer " + (right ? "R" : "L"));
                // Held to the index fingertip, its forward along the finger (sources set by Bind).
                var hold = pointer.gameObject.AddComponent<VRCParentConstraint>();
                hold.Sources.Add(new VRCConstraintSource(null, 1));
                hold.IsActive = true; hold.Locked = true;
                Sender(pointer.gameObject, TipTag(right));
                var aim = Child(pointer, "Aim");
                aim.localPosition = Vector3.forward * AimDistance;
                Sender(aim.gameObject, AimTag(right));
                return pointer;
            }).ToArray();
        }

        // A small local sphere: the receivers find its middle.
        private static void Sender(GameObject at, string tag)
        {
            var sender = at.AddComponent<VRCContactSender>();
            sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = PointRadius; sender.localOnly = true;
            sender.collisionTags.Add(tag);
        }

        /// <summary>
        /// The screen's receivers for pointing: four per sender of each hand (the fingertip's and the aim's), as far as
        /// <see cref="PointingReach"/>, and four for everyone else's fingertips, at the screen's four touch points.
        /// </summary>
        private static void AddPointingReceivers(Transform screen)
        {
            foreach (bool right in Hands)
            for (int i = 0; i < ScreenTouchPoints.Length; i++)
            {
                Receiver(screen, "Touch " + (right ? "R" : "L") + i, i, ScreenTouch(right, i), TipTag(right), PointingReach, self: true);
                Receiver(screen, "Aim " + (right ? "R" : "L") + i, i, ScreenAim(right, i), AimTag(right), PointingReach, self: true);
            }
            for (int i = 0; i < ScreenTouchPoints.Length; i++)
                Receiver(screen, "Others " + i, i, ScreenOthers(i), OthersTag, ScreenTouchRadius, self: false);
        }

        // A local proximity receiver at touch point `point`: its owner's own senders, or (not `self`) everyone else's.
        private static void Receiver(Transform screen, string name, int point, string parameter, string tag, float radius, bool self)
        {
            var at = Child(screen, name);
            at.localPosition = ScreenTouchPoints[point];
            var receiver = at.gameObject.AddComponent<VRCContactReceiver>();
            receiver.shapeType = ContactBase.ShapeType.Sphere; receiver.radius = radius;
            receiver.allowSelf = self; receiver.allowOthers = !self; receiver.localOnly = true;
            receiver.receiverType = ContactReceiver.ReceiverType.Proximity; receiver.parameter = parameter;
            receiver.collisionTags.Add(tag);
        }

        /// <summary>Whether a placed orb points as screen version 7 does (its pointers have aim senders).</summary>
        internal static bool HasPointing(Transform orb) => orb != null && Hands.All(right => orb.Find(Pointer(right) + "/Aim") != null);

        /// <summary>
        /// Holds a placed orb's pointers to <paramref name="animator"/>'s hands where the index fingertips are as the avatar
        /// stands (fingers straight), along the fingers, as large as N4N0 (<paramref name="size"/>): pulling a trigger curls
        /// the index but leaves the pointer where it aims. An avatar without index fingers has no pointer for that hand.
        /// </summary>
        private static void BindPointers(Transform orb, Transform avatar, Animator animator, float size)
        {
            foreach (bool right in Hands)
            {
                var pointer = orb.Find(Pointer(right));
                var hold = pointer != null ? pointer.GetComponent<VRCParentConstraint>() : null;
                if (hold == null || hold.Sources.Count < 1) continue;
                var distal = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
                var middle = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
                var hand = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand) ?? distal;
                Undo.RecordObjects(new Object[] { pointer, hold }, "Fit orb pointers");
                if (distal == null || middle == null)
                {
                    hold.Sources[0] = new VRCConstraintSource(null, 1);
                    pointer.gameObject.SetActive(false);
                    continue;
                }
                pointer.gameObject.SetActive(true);
                // The fingertip: the last finger bone's end (its child), else as far past it as that bone is long.
                var tip = distal.childCount > 0 ? distal.GetChild(0).position : distal.position + (distal.position - middle.position) * .8f;
                var along = tip - middle.position;
                if (along.sqrMagnitude < 1e-8f) along = avatar.forward;
                var up = Mathf.Abs(Vector3.Dot(along.normalized, avatar.up)) > .95f ? avatar.forward : avatar.up;
                pointer.SetPositionAndRotation(tip, Quaternion.LookRotation(along, up));
                float parent = pointer.parent != null ? Mathf.Abs(pointer.parent.lossyScale.x) : 1;
                pointer.localScale = Vector3.one * (size / Mathf.Max(parent, 1e-4f));
                // Offsets in the hand bone's axes, unscaled.
                var turn = Quaternion.Inverse(hand.rotation);
                hold.Sources[0] = new VRCConstraintSource(hand, 1, turn * (pointer.position - hand.position), (turn * pointer.rotation).eulerAngles);
                EditorUtility.SetDirty(hold);
            }
        }

        /// <summary>
        /// The pointing layer and parameters of N4N0's controller: the pointers held to the fingertips and the screen's
        /// rays drawn while its owner is in VR (VRChat syncs VRMode, so everyone sees the beams on the screen too); the
        /// receivers' parameters, local.
        /// </summary>
        private static void PointingController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters)
        {
            var names = Hands.SelectMany(right => Enumerable.Range(0, ScreenTouchPoints.Length).SelectMany(i => new[] { ScreenTouch(right, i), ScreenAim(right, i) }))
                .Concat(Enumerable.Range(0, ScreenTouchPoints.Length).Select(ScreenOthers)).ToList();
            foreach (var name in names) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            var machine = Layer(controller, "Pointing");
            var hidden = new AnimationClip { name = "Pointing off" };
            Curve(hidden, Pointers, typeof(GameObject), "m_IsActive", 0);
            Curve(hidden, ScreenObject, typeof(SkinnedMeshRenderer), "material._Rays", 0);
            var shown = new AnimationClip { name = "Pointing on" };
            Curve(shown, Pointers, typeof(GameObject), "m_IsActive", 1);
            Curve(shown, ScreenObject, typeof(SkinnedMeshRenderer), "material._Rays", 1);
            var off = HeldPropRig.State(controller, machine, "Off", hidden);
            var on = HeldPropRig.State(controller, machine, "On", shown);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            Transition(off, on, (VRMode, AnimatorConditionMode.Greater, 0));
            Transition(on, off, (VRMode, AnimatorConditionMode.Less, 1));
            foreach (var name in names) parameters.Add(Parameter(name, synced: false, type: VRCExpressionParameters.ValueType.Float));
        }
    }
}
