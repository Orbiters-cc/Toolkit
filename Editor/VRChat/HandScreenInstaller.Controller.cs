using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class HandScreenInstaller
    {
        // Who carries each handle: 0 its home, 1 the left wrist, 2 the right wrist (its constraint's sources).
        private sealed class Hold
        {
            public string Name; public int Left, Right; public bool Grabbable;
            public (string, AnimatorConditionMode, float)[] When;
        }

        private static readonly Hold[] Holds =
        {
            // One hand carries the whole screen and leaves the other free to take the second handle.
            new Hold { Name = "Left hand", Left = 1, Right = 1, Grabbable = true, When = Held(true, false) },
            new Hold { Name = "Right hand", Left = 2, Right = 2, Grabbable = true, When = Held(false, true) },
            new Hold { Name = "Both hands", Left = 1, Right = 2, When = Held(true, true).Append((Crossed, AnimatorConditionMode.IfNot, 0f)).ToArray() },
            new Hold { Name = "Both hands crossed", Left = 2, Right = 1, When = Held(true, true).Append((Crossed, AnimatorConditionMode.If, 0f)).ToArray() },
        };

        private static (string, AnimatorConditionMode, float)[] Held(bool left, bool right) => new[]
        {
            (HeldLeft, left ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f),
            (HeldRight, right ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f)
        };

        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Hand screen.controller");
            foreach (var parameter in new[] { Enabled, HeldLeft, HeldRight, Crossed, Drop, GrabLeftHandle + "_IsGrabbed", GrabRightHandle + "_IsGrabbed",
                LeftAtLeft, RightAtLeft, LeftAtRight, RightAtRight, "IsLocal" })
                controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            Placement(controller);
            OwnerGrip(controller);
            Install(root, folder, controller, MenuPath, "Screen menu", new List<VRCExpressionsMenu.Control>
            {
                Control("Show screen", VRCExpressionsMenu.Control.ControlType.Toggle, Enabled),
                Control("Drop screen", VRCExpressionsMenu.Control.ControlType.Button, Drop)
            }, new[] { Parameter(Enabled), Parameter(HeldLeft), Parameter(HeldRight), Parameter(Crossed), Parameter(Drop, synced: false) });
        }

        // ---- Where the handles are: at home, dropped in the world, or carried ----

        private static void Placement(AnimatorController controller)
        {
            var machine = controller.layers[0].stateMachine;
            var off = State(controller, machine, "Off", Pose(false, false, 0, 0, false, toHome: true));
            var spawn = State(controller, machine, "Spawn", Pose(true, false, 0, 0, true, toHome: true));
            var dropped = State(controller, machine, "Dropped", Pose(true, true, 0, 0, true));
            var held = Holds.Select(h => State(controller, machine, h.Name, Pose(true, false, h.Left, h.Right, h.Grabbable))).ToArray();
            // One update world-frozen before following other wrists: unfreezing measures each handle's offset where it is,
            // so nothing jumps (also between one hand and both).
            var caught = Holds.Select(h => State(controller, machine, h.Name + " catch", Pose(true, true, h.Left, h.Right, false))).ToArray();
            machine.defaultState = off;
            Transition(off, spawn, (Enabled, AnimatorConditionMode.If, 0));
            foreach (var state in new[] { spawn, dropped }.Concat(held).Concat(caught))
                Transition(state, off, (Enabled, AnimatorConditionMode.IfNot, 0));
            // The next update: the handles are home, then they wait there world-fixed.
            Transition(spawn, dropped, (Enabled, AnimatorConditionMode.If, 0));
            var none = Held(false, false);
            for (int i = 0; i < Holds.Length; i++)
            {
                // Dropped is already frozen.
                Transition(dropped, held[i], Holds[i].When);
                Transition(caught[i], held[i], Holds[i].When);
                for (int j = 0; j < Holds.Length; j++)
                    if (j != i) { Transition(held[j], caught[i], Holds[i].When); Transition(caught[j], caught[i], Holds[i].When); }
                Transition(held[i], dropped, none);
                Transition(caught[i], dropped, none);
            }
        }

        private static AnimationClip Pose(bool visible, bool frozen, int left, int right, bool grabbable, bool toHome = false)
        {
            var clip = new AnimationClip();
            foreach (var (handle, holder) in new[] { (LeftHandle, left), (RightHandle, right) })
            {
                // A hold leaves the offsets measured where it was grabbed; back home, each handle sits on its home again.
                if (toHome)
                    foreach (string offset in new[] { "ParentPositionOffset", "ParentRotationOffset" })
                    foreach (string axis in new[] { "x", "y", "z" })
                        Curve(clip, handle, typeof(VRCParentConstraint), "Sources.source0." + offset + "." + axis, 0);
                Curve(clip, handle, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1 : 0);
                for (int source = 0; source < 3; source++)
                    Curve(clip, handle, typeof(VRCParentConstraint), "Sources.source" + source + ".Weight", source == holder ? 1 : 0);
                Curve(clip, handle + "/Grab", typeof(VRCPhysBone), "m_Enabled", grabbable ? 1 : 0);
            }
            Curve(clip, Frame, typeof(GameObject), "m_IsActive", visible ? 1 : 0);
            return clip;
        }

        // ---- The owner's hands ----

        /// <summary>
        /// A PhysBone grab on a handle starts the owner's hold, which is latched (and synced, so everyone follows the
        /// owner's wrists) rather than left to the grab: Touch's grab and pose inputs can release a PhysBone mid-squeeze.
        /// The contacts tell which hand took which handle. While one hand holds the screen, the other takes the other
        /// handle to stretch it; relaxing a hand lets go.
        /// </summary>
        private static void OwnerGrip(AnimatorController controller)
        {
            var machine = Layer(controller, "Owner grip");
            var free = State(controller, machine, "Free", new AnimationClip());
            Drive(free, (HeldLeft, false), (HeldRight, false), (Crossed, false));
            var both = State(controller, machine, "Hold both", new AnimationClip());
            Drive(both, (HeldLeft, true), (HeldRight, true), (Crossed, false));
            var crossed = State(controller, machine, "Hold both crossed", new AnimationClip());
            Drive(crossed, (HeldLeft, true), (HeldRight, true), (Crossed, true));
            machine.defaultState = free;
            var hands = new[] { (left: true, name: "left", gesture: "GestureLeft"), (left: false, name: "right", gesture: "GestureRight") };
            var handles = new[] { (left: true, name: "left", grabbed: GrabLeftHandle + "_IsGrabbed"), (left: false, name: "right", grabbed: GrabRightHandle + "_IsGrabbed") };
            string At(bool leftHand, bool leftHandle) => leftHand ? (leftHandle ? LeftAtLeft : LeftAtRight) : (leftHandle ? RightAtLeft : RightAtRight);
            // One hand on one handle. The hand that picked the screen up may keep its native grab (Touch only lets go of it
            // on a full fist), so the other hand counts as soon as it takes the other handle: that grab is never the first one.
            var holding = new AnimatorState[2, 2];
            foreach (var hand in hands)
            foreach (var handle in handles)
            {
                var state = State(controller, machine, "Hold " + hand.name + " at " + handle.name + " handle", new AnimationClip());
                Drive(state, (HeldLeft, hand.left), (HeldRight, !hand.left), (Crossed, false));
                holding[hand.left ? 0 : 1, handle.left ? 0 : 1] = state;
                Transition(free, state, new[] { ("IsLocal", AnimatorConditionMode.If, 0f), (Enabled, AnimatorConditionMode.If, 0f), (Drop, AnimatorConditionMode.IfNot, 0f),
                    (handle.grabbed, AnimatorConditionMode.If, 0f), (At(hand.left, handle.left), AnimatorConditionMode.If, 0f) }.Concat(Gripping(hand.gesture)).ToArray());
                Transition(state, free, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(state, free, (Drop, AnimatorConditionMode.If, 0));
                LetGo(state, free, hand.gesture);
                // Crossed when the left hand ends up on the right handle.
                var other = handles[handle.left ? 1 : 0];
                Transition(state, hand.left == handle.left ? both : crossed, Gripping(hands[hand.left ? 1 : 0].gesture)
                    .Concat(new[] { (other.grabbed, AnimatorConditionMode.If, 0f), (At(!hand.left, other.left), AnimatorConditionMode.If, 0f) }).ToArray());
            }
            foreach (var (state, isCrossed) in new[] { (both, false), (crossed, true) })
            {
                Transition(state, free, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(state, free, (Drop, AnimatorConditionMode.If, 0));
                foreach (float relaxed in new[] { 0f, 2f }) LetGo(state, free, "GestureLeft", ("GestureRight", AnimatorConditionMode.Equals, relaxed));
                // One hand lets go: the other carries the whole screen again, from the handle it holds.
                LetGo(state, holding[0, isCrossed ? 1 : 0], "GestureRight");
                LetGo(state, holding[1, isCrossed ? 0 : 1], "GestureLeft");
            }
        }
    }
}
