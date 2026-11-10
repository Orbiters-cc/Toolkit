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
    /// Where N4N0's contacts that only measure its owner are (screen version 16): in a room of their own, straight below the
    /// avatar (Orbiters contracts, Docs/n4n0.md, Pointing).
    /// <para>
    /// VRChat pairs contacts per cell of a world-fixed 5 m grid, sweeping along the world's X (VRC.Dynamics'
    /// CollisionBroadphase_HybridSAP.FindPairs). A cell stops at 1024 pairs, tags or not: every shape further along X finds nothing
    /// that frame, so a whole sender reads 0 on all its receivers while its beam, a shader, goes on showing. N4N0's 28 large
    /// receivers made 770 pairs of the 1024 on their own, so one other person near was enough to reach the end, and which of the
    /// fingertip, the aim point or the eyes fell off it depended on which way the wearer faced and where the hand and the
    /// screen were: a part of the screen went dead, and moving closer or at another angle brought it back. Measured with
    /// VRChat's own DLL (reports/menu-hands.md) and seen in a real recording (one sender silent for 9 s, its neighbour blinking).
    /// </para>
    /// <para>
    /// A contact's reading depends only on where sender and receiver are from each other. So each thing measured has a copy
    /// here, the same way up and in the same place but for one offset, and no one else's contact shares its cell. All the copies are
    /// children of one object, which is what makes the offset the same for all of them: a position constraint's offset is in its
    /// parent's axes. Everyone else's fingertips, which cannot be moved, keep their receivers on the real screen.
    /// </para>
    /// </summary>
    public static partial class N4N0Installer
    {
        internal const string ContactRoom = Shown + "/Contact room", RoomHead = ContactRoom + "/Head", RoomScreen = RoomHead + "/Screen", RoomGaze = RoomHead + "/Gaze", RoomPoint = RoomGaze + "/Point";
        internal static string RoomPointer(bool right) => RoomHead + "/Pointer " + (right ? "R" : "L");
        internal static string RoomAim(bool right) => RoomPointer(right) + "/Aim";
        // How far below: 20 cells of the grid (at 1.6 m eye height, as everything of N4N0 scales with the avatar), and
        // 15 microns of a float at this height, a third of what contacts already read to.
        internal const float RoomDrop = 100;

        /// <summary>
        /// The room, before the screen's receivers are added: the copies of the screen, the two pointers and the eyes, each on its
        /// source by constraints <see cref="Bind"/> needs nothing more for (they name their source from the start). The screen's
        /// copy and the pointers' have the contacts; shown only on its owner's client, by <see cref="RoomController"/>.
        /// </summary>
        private static void AddContactRoom(Transform n4n0, Transform[] pointers)
        {
            var shown = n4n0.Find(Shown);
            var room = Child(shown, "Contact room");
            room.gameObject.SetActive(false);
            var head = Child(room, "Head");
            foreach (bool right in Hands)
            {
                var copy = Copy(head, "Pointer " + (right ? "R" : "L"), pointers[right ? 1 : 0]);
                copy.gameObject.SetActive(false);
                Sender(copy.gameObject, TipTag(right));
                var aim = Child(copy, "Aim");
                aim.localPosition = Vector3.forward * AimDistance;
                Sender(aim.gameObject, AimTag(right));
            }
            var gaze = Copy(head, "Gaze", n4n0.Find(Gaze));
            var point = Child(gaze, "Point");
            point.localPosition = Vector3.forward * GazeDistance;
            foreach (var (at, tag) in new[] { (gaze, EyeTag), (point, GazeTag) })
            {
                var sender = at.gameObject.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = GazeRadius; HeldPropRig.LocalOnlySender(sender, true);
                sender.collisionTags.Add(tag);
            }
            // The view cone, at the eyes along the line of sight: local senders that cost nothing in the performance rank.
            for (int i = 0; i < ViewDistances.Length; i++)
            {
                var sphere = Child(gaze, "View " + i);
                sphere.localPosition = Vector3.forward * ViewDistances[i];
                var sender = sphere.gameObject.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = ViewDistances[i] * ViewWidth; HeldPropRig.LocalOnlySender(sender, true);
                sender.collisionTags.Add(ViewTag);
            }
        }

        /// <summary>The screen's copy in the room, with its receivers: called once the screen exists.</summary>
        private static Transform AddRoomScreen(Transform n4n0, Transform screen)
        {
            var copy = Copy(n4n0.Find(RoomHead), "Screen", screen);
            copy.gameObject.SetActive(false);
            return copy;
        }

        // A copy of `source`: as it is turned, where it is but for the room's drop. Its parent's axes make the drop's direction.
        private static Transform Copy(Transform parent, string name, Transform source)
        {
            var copy = Child(parent, name);
            var place = copy.gameObject.AddComponent<VRCPositionConstraint>();
            place.Sources.Add(new VRCConstraintSource(source, 1));
            place.PositionOffset = Vector3.down * RoomDrop;
            place.IsActive = true; place.Locked = true;
            var turn = copy.gameObject.AddComponent<VRCRotationConstraint>();
            turn.Sources.Add(new VRCConstraintSource(source, 1));
            turn.IsActive = true; turn.Locked = true;
            return copy;
        }

        /// <summary>Fits the room as N4N0 is fitted: its head as large as N4N0's, what is in it the same size as what it copies.</summary>
        private static void FitRoom(Transform n4n0, float size)
        {
            var head = n4n0.Find(RoomHead);
            if (head == null) return;
            Undo.RecordObject(head, "Fit N4N0's contact room");
            float parent = head.parent != null ? Mathf.Abs(head.parent.lossyScale.x) : 1;
            head.localScale = Vector3.one * (size / Mathf.Max(parent, 1e-4f));
        }

        /// <summary>The room exists only on its owner's client, whose contacts it is.</summary>
        private static void RoomController(AnimatorController controller)
        {
            var machine = Layer(controller, "Contact room");
            var hidden = new AnimationClip { name = "Contact room off" };
            Curve(hidden, ContactRoom, typeof(GameObject), "m_IsActive", 0);
            var shown = new AnimationClip { name = "Contact room on" };
            Curve(shown, ContactRoom, typeof(GameObject), "m_IsActive", 1);
            var off = HeldPropRig.State(controller, machine, "Off", hidden);
            var on = HeldPropRig.State(controller, machine, "On", shown);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            Transition(off, on, ("IsLocal", AnimatorConditionMode.If, 0));
        }
    }
}
