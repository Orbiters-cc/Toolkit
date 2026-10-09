using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class DrawingPenInstaller
    {
        // Trigger pressure (the fist's gesture weight) that starts and stops the ink.
        internal const float InkOn = .5f, InkOff = .4f;

        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Drawing pen.controller");
            foreach (var parameter in new[] { Enabled, Grab + "_IsGrabbed", Left, Right, Clear, HeldLeft, HeldRight, Drop, "IsLocal" })
                controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            controller.AddParameter("GestureLeftWeight", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureRightWeight", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var off = State(controller, machine, "Off", PropClip(false, false, false, true));
            var spawn = State(controller, machine, "Spawn", PropClip(true, false, false, true, resetGrip: true));
            var dropped = State(controller, machine, "Dropped", PropClip(true, false, true, false));
            var held = State(controller, machine, "Guest held", PropClip(true, true, false, false));
            var left = State(controller, machine, "Left hand", PropClip(true, false, false, false, 1));
            var right = State(controller, machine, "Right hand", PropClip(true, false, false, false, 2));
            // One frame world-frozen between following the grabbed bone and following the wrist: unfreezing measures the
            // wrist's offset where the pen is, so it never jumps into a set pose in the hand.
            var catchLeft = State(controller, machine, "Left catch", PropClip(true, false, true, false, 1));
            var catchRight = State(controller, machine, "Right catch", PropClip(true, false, true, false, 2));
            machine.defaultState = off;
            Transition(off, spawn, (Enabled, AnimatorConditionMode.If, 0));
            foreach (var state in new[] { spawn, dropped, held, left, right, catchLeft, catchRight })
                Transition(state, off, (Enabled, AnimatorConditionMode.IfNot, 0));
            // The next update: a transition only waits for exit time when its clip loops (0.2 s here).
            Transition(spawn, dropped, (Enabled, AnimatorConditionMode.If, 0));
            // Dropped is already frozen; held follows the grabbed bone and is frozen for a frame first.
            Transition(dropped, left, (HeldLeft, AnimatorConditionMode.If, 0));
            Transition(dropped, right, (HeldRight, AnimatorConditionMode.If, 0));
            Transition(held, catchLeft, (HeldLeft, AnimatorConditionMode.If, 0));
            Transition(held, catchRight, (HeldRight, AnimatorConditionMode.If, 0));
            Transition(catchLeft, left, (HeldLeft, AnimatorConditionMode.If, 0));
            Transition(catchRight, right, (HeldRight, AnimatorConditionMode.If, 0));
            Transition(dropped, held, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0),
                (HeldLeft, AnimatorConditionMode.IfNot, 0), (HeldRight, AnimatorConditionMode.IfNot, 0));
            Transition(held, dropped, (Grab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0));
            Transition(left, dropped, (HeldLeft, AnimatorConditionMode.IfNot, 0));
            Transition(right, dropped, (HeldRight, AnimatorConditionMode.IfNot, 0));

            // A PhysBone starts the owner's pickup, but must not own the continuing hold:
            // Touch's grab/pose inputs can release that grab while the fist is being squeezed.
            // Latch the hand locally and sync it so observers also follow the owner's wrist.
            var ownership = Layer(controller, "Owner grip");
            var free = State(controller, ownership, "Free", new AnimationClip());
            var ownerLeft = State(controller, ownership, "Hold left", new AnimationClip());
            var ownerRight = State(controller, ownership, "Hold right", new AnimationClip());
            ownership.defaultState = free;
            HoldParameters(free, false, false);
            HoldParameters(ownerLeft, true, false);
            HoldParameters(ownerRight, false, true);
            foreach (var side in new[] { (state: ownerLeft, contact: Left, gesture: "GestureLeft"),
                (state: ownerRight, contact: Right, gesture: "GestureRight") })
            {
                Transition(free, side.state, new[] { ("IsLocal", AnimatorConditionMode.If, 0f), (Enabled, AnimatorConditionMode.If, 0f),
                    (Drop, AnimatorConditionMode.IfNot, 0f), (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0f),
                    (side.contact, AnimatorConditionMode.If, 0f) }.Concat(Gripping(side.gesture)).ToArray());
                Transition(side.state, free, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(side.state, free, (Drop, AnimatorConditionMode.If, 0));
                LetGo(side.state, free, side.gesture);
            }

            var ink = Layer(controller, "Ink");
            var erased = State(controller, ink, "Erase", InkClip(false, 0));
            var idle = State(controller, ink, "Ready", InkClip(false, 10800));
            var drawLeft = State(controller, ink, "Draw left", InkClip(true, 10800));
            var drawRight = State(controller, ink, "Draw right", InkClip(true, 10800));
            var guest = State(controller, ink, "Guest draw", InkClip(true, 10800));
            ink.defaultState = erased;
            // Disabling always clears the native trail, rather than hiding it for the next enable.
            Transition(erased, idle, (Enabled, AnimatorConditionMode.If, 0), (Clear, AnimatorConditionMode.IfNot, 0));
            foreach (var state in new[] { idle, drawLeft, drawRight, guest })
            {
                Transition(state, erased, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(state, erased, (Clear, AnimatorConditionMode.If, 0));
            }
            // The index finger down draws: a squeezed fist (1, trigger pressure) or a thumbs up (7). Ink stops as soon as
            // the trigger eases off past half way, not once it is nearly released: a narrow band keeps the stroke steady.
            foreach (var side in new[] { (state: drawLeft, held: HeldLeft, gesture: "GestureLeft"),
                (state: drawRight, held: HeldRight, gesture: "GestureRight") })
            {
                Transition(idle, side.state, (side.held, AnimatorConditionMode.If, 0), (side.gesture, AnimatorConditionMode.Equals, 1),
                    (side.gesture + "Weight", AnimatorConditionMode.Greater, InkOn));
                Transition(idle, side.state, (side.held, AnimatorConditionMode.If, 0), (side.gesture, AnimatorConditionMode.Equals, 7));
                Transition(side.state, idle, (side.held, AnimatorConditionMode.IfNot, 0));
                Transition(side.state, idle, (side.gesture, AnimatorConditionMode.NotEqual, 1), (side.gesture, AnimatorConditionMode.NotEqual, 7));
                Transition(side.state, idle, (side.gesture, AnimatorConditionMode.Equals, 1), (side.gesture + "Weight", AnimatorConditionMode.Less, InkOff));
            }
            Transition(idle, guest, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0),
                (HeldLeft, AnimatorConditionMode.IfNot, 0), (HeldRight, AnimatorConditionMode.IfNot, 0),
                (Left, AnimatorConditionMode.IfNot, 0), (Right, AnimatorConditionMode.IfNot, 0));
            Transition(guest, idle, (Grab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0));
            Transition(guest, idle, (HeldLeft, AnimatorConditionMode.If, 0));
            Transition(guest, idle, (HeldRight, AnimatorConditionMode.If, 0));

            Install(root, folder, controller, MenuPath, "Pen menu", new List<VRCExpressionsMenu.Control>
            {
                Control("Enable pen", VRCExpressionsMenu.Control.ControlType.Toggle, Enabled),
                Control("Clear drawing", VRCExpressionsMenu.Control.ControlType.Button, Clear),
                Control("Drop pen", VRCExpressionsMenu.Control.ControlType.Button, Drop)
            }, new[] { Parameter(Enabled), Parameter(Clear), Parameter(HeldLeft), Parameter(HeldRight), Parameter(Drop) });
        }

        private static void HoldParameters(AnimatorState state, bool left, bool right) => Hold(state, HeldLeft, HeldRight, left, right);

        private static AnimationClip PropClip(bool visible, bool baseFrozen, bool penFrozen, bool atHome, int hand = 0, bool resetGrip = false) =>
            HeldPropRig.PropClip("Pen", "Pen/Model", visible, baseFrozen, penFrozen, atHome, hand == 1, hand == 2, visible && hand == 0, resetGrip);

        private static AnimationClip InkClip(bool draw, float lifetime)
        {
            var clip = new AnimationClip();
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Emitting", draw ? 1 : 0);
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Time", lifetime);
            return clip;
        }
    }
}
