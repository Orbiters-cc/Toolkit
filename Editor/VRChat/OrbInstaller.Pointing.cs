using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Pointing at N4N0's screen (Orbiters contracts, Docs/orb.md, Pointing; screen version 4): a VRChat raycast from each
    /// index fingertip, along the finger, meets a thin solid box on the screen's face. Its owner sees a beam from the
    /// fingertip and a bead where the ray meets the screen at once (the avatar draws them), and the bead's contact on
    /// three of the screen's touch points tells Orbiters - N4N0 exactly where it is; the fingertip coming within 2.1 cm
    /// along the ray presses. Everything is on its owner's client only, in VR, while the screen is open: no synced bits,
    /// and contacts, the box and the raycasts cost nothing for anyone else.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Each hand's raycast parameter: VRChat adds <c>_Hit</c> and <c>_Ratio</c> (and <c>_Distance</c>).</summary>
        public const string RayPrefix = "N4N0/Screen/Ray/";
        /// <summary>The bead's contact tag, <c>N4N0PointerL</c> or <c>N4N0PointerR</c>: only that hand's touch points take it.</summary>
        public const string PointerTagPrefix = "N4N0Pointer";
        internal const string Pointers = Shown + "/Pointers", Surface = ScreenObject + "/Surface";
        // The ray's reach, the bead's contact and look, the beam's thickness (at 1.6 m eye height: they scale with the avatar).
        internal const float RayLength = .6f, PointRadius = .002f, BeadRadius = .0035f, BeamRadius = .0012f;
        // The screen's touch points a hand's bead is found from (the first three: they lie in the screen's plane).
        internal const int PointingTouchPoints = 3;
        // The solid box the rays meet: the screen's face, this deep behind it.
        internal const float SurfaceDepth = .002f;

        public static string Ray(bool right) => RayPrefix + (right ? "R" : "L");
        public static string RayHit(bool right) => Ray(right) + "_Hit";
        public static string RayRatio(bool right) => Ray(right) + "_Ratio";
        public static string PointerTag(bool right) => PointerTagPrefix + (right ? "R" : "L");
        internal static string Pointer(bool right) => Pointers + "/Pointer " + (right ? "R" : "L");
        private static readonly bool[] Hands = { false, true };

        /// <summary>
        /// Gives a placed orb's screen what pointing needs (with Undo), as a version 4 screen has it: the solid box on its
        /// face, three touch points per hand taking only that hand's bead, and a pointer per hand. Pointers wait for
        /// <see cref="Bind"/> to hold them to the fingertips. Nothing changes on a screen that has it all.
        /// </summary>
        internal static void AddPointing(Transform orb)
        {
            var screen = orb.Find(ScreenObject);
            var shown = orb.Find(Shown);
            var body = orb.Find(Body);
            if (screen == null || shown == null || body == null) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            if (screen.Find("Surface") == null)
            {
                var surface = new GameObject("Surface");
                Undo.RegisterCreatedObjectUndo(surface, "N4N0 pointing");
                surface.transform.SetParent(screen, false);
                surface.SetActive(false);
                // Solid (VRChat's raycasts pass through triggers), authored on Default; only its owner's client has it,
                // while the screen is open.
                var box = surface.AddComponent<BoxCollider>();
                box.size = new Vector3(ScreenWidth, ScreenHeight, SurfaceDepth);
                box.center = new Vector3(0, 0, SurfaceDepth / 2);
            }
            // The hands' touch points: the first three of each, taking that hand's bead.
            foreach (bool right in Hands)
            for (int i = 0; i < ScreenTouchPoints.Length; i++)
            {
                var point = screen.Find("Touch " + (right ? "R" : "L") + i);
                if (i >= PointingTouchPoints)
                {
                    if (point != null) Undo.DestroyObjectImmediate(point.gameObject);
                    continue;
                }
                var receiver = point != null ? point.GetComponent<VRCContactReceiver>() : null;
                if (receiver == null) continue;
                if (receiver.collisionTags.Count == 1 && receiver.collisionTags[0] == PointerTag(right)) continue;
                Undo.RecordObject(receiver, "N4N0 pointing");
                receiver.collisionTags.Clear();
                receiver.collisionTags.Add(PointerTag(right));
                PrefabUtility.RecordPrefabInstancePropertyModifications(receiver);
            }
            if (orb.Find(Pointers) != null) return;
            var pointers = new GameObject("Pointers");
            Undo.RegisterCreatedObjectUndo(pointers, "N4N0 pointing");
            pointers.transform.SetParent(shown, false);
            pointers.SetActive(false);
            var mesh = body.GetComponent<MeshFilter>().sharedMesh;
            var material = body.GetComponent<MeshRenderer>().sharedMaterials[0];
            foreach (bool right in Hands)
            {
                var pointer = Child(pointers.transform, "Pointer " + (right ? "R" : "L"));
                // Held to the index fingertip, along the finger (sources set by Bind).
                var hold = pointer.gameObject.AddComponent<VRCParentConstraint>();
                hold.Sources.Add(new VRCConstraintSource(null, 1));
                hold.IsActive = true; hold.Locked = true;
                var bead = Child(pointer, "Bead");
                var ray = pointer.gameObject.AddComponent<VRCRaycast>();
                ray.RaycastDirection = Vector3.forward; ray.Distance = RayLength; ray.ApplyTransformScale = true;
                // The box is authored on Default, which this mode tests; VRChat may move the avatar's objects to
                // PlayerLocal, which it tests too.
                ray.RaycastCollisionMode = VRCRaycast.CollisionMode.HitWorldsAndPlayers;
                ray.ResultTransform = bead; ray.ApplyRotation = false;
                ray.BehaviorOnMiss = VRCRaycast.MissBehavior.SnapToStart;
                ray.Parameter = Ray(right);
                // The bead: where the ray meets the screen, and its contact.
                bead.localScale = Vector3.one * BeadRadius;
                bead.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var beadRenderer = bead.gameObject.AddComponent<MeshRenderer>();
                beadRenderer.sharedMaterial = material;
                Quiet(beadRenderer);
                var sender = bead.gameObject.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = PointRadius / BeadRadius; sender.localOnly = true;
                sender.collisionTags.Add(PointerTag(right));
                bead.gameObject.SetActive(false);
                // The beam: a needle from the fingertip to the bead (its length set from the ray's ratio).
                var beam = Child(pointer, "Beam");
                beam.localScale = new Vector3(BeamRadius, BeamRadius, 0);
                beam.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var beamRenderer = beam.gameObject.AddComponent<MeshRenderer>();
                beamRenderer.sharedMaterial = material;
                Quiet(beamRenderer);
                beam.gameObject.SetActive(false);
            }
        }

        /// <summary>Whether a placed orb's screen points with rays (has its pointers and its box).</summary>
        internal static bool HasPointing(Transform orb) => orb != null && orb.Find(Pointers) != null && orb.Find(Surface) != null;

        /// <summary>
        /// Holds a placed orb's pointers to <paramref name="animator"/>'s index fingertips, along the fingers, as large as
        /// N4N0 (<paramref name="size"/>). An avatar without index fingers has no pointer for that hand.
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
                // Offsets in the finger bone's axes, unscaled.
                var turn = Quaternion.Inverse(distal.rotation);
                hold.Sources[0] = new VRCConstraintSource(distal, 1, turn * (pointer.position - distal.position), (turn * pointer.rotation).eulerAngles);
                EditorUtility.SetDirty(hold);
            }
        }

        /// <summary>
        /// The pointing layers and parameters of N4N0's controller: the pointers and the screen's box on while the
        /// screen is open for its owner in VR, and each hand's bead and beam shown while its ray meets the screen.
        /// </summary>
        private static void PointingController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters)
        {
            foreach (bool right in Hands)
            {
                controller.AddParameter(RayHit(right), AnimatorControllerParameterType.Bool);
                controller.AddParameter(RayRatio(right), AnimatorControllerParameterType.Float);
            }
            var machine = Layer(controller, "Pointing");
            var hidden = new AnimationClip { name = "Pointing off" };
            Curve(hidden, Pointers, typeof(GameObject), "m_IsActive", 0);
            Curve(hidden, Surface, typeof(GameObject), "m_IsActive", 0);
            var shown = new AnimationClip { name = "Pointing on" };
            Curve(shown, Pointers, typeof(GameObject), "m_IsActive", 1);
            Curve(shown, Surface, typeof(GameObject), "m_IsActive", 1);
            var off = HeldPropRig.State(controller, machine, "Off", hidden);
            var on = HeldPropRig.State(controller, machine, "On", shown);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            // As the screen opens for its owner in VR (Replace on).
            foreach (var trigger in new[] { ScreenOpen, Menu })
                Transition(off, on, ("IsLocal", AnimatorConditionMode.If, 0), (trigger, AnimatorConditionMode.If, 0), (VRMode, AnimatorConditionMode.Greater, 0),
                    (ScreenReplace, AnimatorConditionMode.If, 0));
            Transition(on, off, (ScreenOpen, AnimatorConditionMode.IfNot, 0), (Menu, AnimatorConditionMode.IfNot, 0));
            Transition(on, off, (VRMode, AnimatorConditionMode.Less, 1));

            foreach (bool right in Hands)
            {
                string hand = right ? "R" : "L", path = Pointer(right);
                var layer = Layer(controller, "Pointer " + hand);
                var gone = new AnimationClip { name = "Pointer " + hand + " gone" };
                Curve(gone, path + "/Bead", typeof(GameObject), "m_IsActive", 0);
                Curve(gone, path + "/Beam", typeof(GameObject), "m_IsActive", 0);
                // The beam from the fingertip to the bead: a needle along the ray, its length the ray's ratio of its reach.
                var beam = Hidden(new BlendTree { name = "Pointer " + hand + " beam", blendType = BlendTreeType.Simple1D, blendParameter = RayRatio(right), useAutomaticThresholds = false });
                foreach (float ratio in new[] { 0f, 1f })
                {
                    var clip = Hidden(new AnimationClip { name = "Pointer " + hand + " beam " + ratio });
                    Curve(clip, path + "/Bead", typeof(GameObject), "m_IsActive", 1);
                    Curve(clip, path + "/Beam", typeof(GameObject), "m_IsActive", 1);
                    Curve(clip, path + "/Beam", typeof(Transform), "m_LocalScale.z", RayLength * ratio / 2);
                    Curve(clip, path + "/Beam", typeof(Transform), "m_LocalPosition.z", RayLength * ratio / 2);
                    beam.AddChild(clip, ratio);
                }
                var away = HeldPropRig.State(controller, layer, "Away", gone);
                var meets = HeldPropRig.State(controller, layer, "Meets", beam);
                away.writeDefaultValues = true; meets.writeDefaultValues = true;
                layer.defaultState = away;
                Transition(away, meets, (RayHit(right), AnimatorConditionMode.If, 0));
                Transition(meets, away, (RayHit(right), AnimatorConditionMode.IfNot, 0));
            }
            foreach (bool right in Hands)
            {
                parameters.Add(Parameter(RayHit(right), synced: false));
                parameters.Add(Parameter(RayRatio(right), synced: false, type: VRCExpressionParameters.ValueType.Float));
            }
        }
    }
}
