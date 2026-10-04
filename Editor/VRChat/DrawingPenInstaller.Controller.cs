using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class DrawingPenInstaller
    {
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
            var spawn = State(controller, machine, "Spawn", PropClip(true, false, false, true));
            var dropped = State(controller, machine, "Dropped", PropClip(true, false, true, false));
            var held = State(controller, machine, "Guest held", PropClip(true, true, false, false));
            var left = State(controller, machine, "Left hand", PropClip(true, false, false, false, 1));
            var right = State(controller, machine, "Right hand", PropClip(true, false, false, false, 2));
            machine.defaultState = off;
            Transition(off, spawn, (Enabled, AnimatorConditionMode.If, 0));
            foreach (var state in new[] { spawn, dropped, held, left, right })
                Transition(state, off, (Enabled, AnimatorConditionMode.IfNot, 0));
            var settle = Transition(spawn, dropped); settle.hasExitTime = true; settle.exitTime = 1;
            foreach (var state in new[] { dropped, held })
            {
                Transition(state, left, (HeldLeft, AnimatorConditionMode.If, 0));
                Transition(state, right, (HeldRight, AnimatorConditionMode.If, 0));
            }
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
                Transition(free, side.state, ("IsLocal", AnimatorConditionMode.If, 0), (Enabled, AnimatorConditionMode.If, 0),
                    (Drop, AnimatorConditionMode.IfNot, 0), (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0),
                    (side.contact, AnimatorConditionMode.If, 0), (side.gesture, AnimatorConditionMode.NotEqual, 2));
                Transition(side.state, free, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(side.state, free, (Drop, AnimatorConditionMode.If, 0));
                Transition(side.state, free, (side.gesture, AnimatorConditionMode.Equals, 2));
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
            foreach (var side in new[] { (state: drawLeft, held: HeldLeft, gesture: "GestureLeft"),
                (state: drawRight, held: HeldRight, gesture: "GestureRight") })
            {
                Transition(idle, side.state, (side.held, AnimatorConditionMode.If, 0), (side.gesture, AnimatorConditionMode.Equals, 1),
                    (side.gesture + "Weight", AnimatorConditionMode.Greater, .35f));
                Transition(side.state, idle, (side.held, AnimatorConditionMode.IfNot, 0));
                Transition(side.state, idle, (side.gesture, AnimatorConditionMode.NotEqual, 1));
                Transition(side.state, idle, (side.gesture + "Weight", AnimatorConditionMode.Less, .2f));
            }
            Transition(idle, guest, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0),
                (HeldLeft, AnimatorConditionMode.IfNot, 0), (HeldRight, AnimatorConditionMode.IfNot, 0),
                (Left, AnimatorConditionMode.IfNot, 0), (Right, AnimatorConditionMode.IfNot, 0));
            Transition(guest, idle, (Grab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0));
            Transition(guest, idle, (HeldLeft, AnimatorConditionMode.If, 0));
            Transition(guest, idle, (HeldRight, AnimatorConditionMode.If, 0));

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[] { Parameter(Enabled), Parameter(Clear), Parameter(HeldLeft), Parameter(HeldRight), Parameter(Drop) };
            AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
            var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            sub.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control { name = "Enable pen", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = Enabled }, value = 1 },
                new VRCExpressionsMenu.Control { name = "Clear drawing", type = VRCExpressionsMenu.Control.ControlType.Button, parameter = new VRCExpressionsMenu.Control.Parameter { name = Clear }, value = 1 },
                new VRCExpressionsMenu.Control { name = "Drop pen", type = VRCExpressionsMenu.Control.ControlType.Button, parameter = new VRCExpressionsMenu.Control.Parameter { name = Drop }, value = 1 }
            };
            AssetDatabase.CreateAsset(sub, folder + "/Pen menu.asset");
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control> { new VRCExpressionsMenu.Control { name = MenuPath, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub } };
            AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
            VrcFury.Writer.FullController(root, controller, menu, parameters);
        }

        private static VRCExpressionParameters.Parameter Parameter(string name) => new VRCExpressionParameters.Parameter
        { name = name, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = false, networkSynced = true };

        private static void HoldParameters(AnimatorState state, bool left, bool right)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = HeldLeft, value = left ? 1 : 0 });
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = HeldRight, value = right ? 1 : 0 });
        }

        private static AnimatorStateMachine Layer(AnimatorController controller, string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        private static AnimationClip PropClip(bool visible, bool baseFrozen, bool penFrozen, bool atHome, int hand = 0)
        {
            var clip = new AnimationClip();
            Curve(clip, "Pen/Model", typeof(GameObject), "m_IsActive", visible ? 1 : 0);
            Curve(clip, "Grab base/Bone", typeof(VRCPhysBone), "m_Enabled", visible && hand == 0 ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "FreezeToWorld", baseFrozen ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "Sources.source0.Weight", atHome ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "Sources.source1.Weight", atHome ? 0 : 1);
            Curve(clip, "Pen", typeof(VRCParentConstraint), "FreezeToWorld", penFrozen ? 1 : 0);
            for (int source = 0; source < 3; source++)
                Curve(clip, "Pen", typeof(VRCParentConstraint), "Sources.source" + source + ".Weight", source == hand ? 1 : 0);
            return clip;
        }

        private static AnimationClip InkClip(bool draw, float lifetime)
        {
            var clip = new AnimationClip();
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Emitting", draw ? 1 : 0);
            Curve(clip, "Pen/Ink", typeof(TrailRenderer), "m_Time", lifetime);
            return clip;
        }

        private static void Curve(AnimationClip clip, string path, Type type, string property, float value) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, .2f, value));

        private static AnimatorState State(AnimatorController controller, AnimatorStateMachine machine, string name, AnimationClip clip)
        {
            clip.name = name; AssetDatabase.AddObjectToAsset(clip, controller);
            var state = machine.AddState(name); state.motion = clip; state.writeDefaultValues = false; return state;
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, params (string name, AnimatorConditionMode mode, float value)[] conditions)
        {
            var transition = from.AddTransition(to); transition.duration = 0; transition.hasExitTime = false;
            foreach (var condition in conditions) transition.AddCondition(condition.mode, condition.value, condition.name);
            return transition;
        }
    }
}
