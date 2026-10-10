using System.Collections.Generic;
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
            HeldParameters(controller, Held);
            controller.AddParameter(Clear, AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureLeftWeight", AnimatorControllerParameterType.Float);
            controller.AddParameter("GestureRightWeight", AnimatorControllerParameterType.Float);
            HeldLayers(controller, Held);

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

        private static readonly Names Held = new Names
            { Body = "Pen", Model = "Pen/Model", Enabled = Enabled, Grab = Grab, Left = Left, Right = Right, HeldLeft = HeldLeft, HeldRight = HeldRight, Drop = Drop };

        private static AnimationClip InkClip(bool draw, float lifetime)
        {
            var clip = new AnimationClip();
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Emitting", draw ? 1 : 0);
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Time", lifetime);
            return clip;
        }
    }
}
