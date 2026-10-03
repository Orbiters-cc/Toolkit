using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class DrawingPenInstaller
    {
        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Drawing pen.controller");
            foreach (var parameter in new[] { Enabled, Grab + "_IsGrabbed", Left, Right, Clear })
                controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var off = State(controller, machine, "Off", PropClip(false, false, false, true));
            var spawn = State(controller, machine, "Spawn", PropClip(true, false, false, true));
            var dropped = State(controller, machine, "Dropped", PropClip(true, false, true, false));
            var held = State(controller, machine, "Held", PropClip(true, true, false, false));
            machine.defaultState = off;
            Transition(off, spawn, (Enabled, AnimatorConditionMode.If, 0));
            var settle = Transition(spawn, dropped); settle.hasExitTime = true; settle.exitTime = 1;
            Transition(dropped, held, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0));
            Transition(held, dropped, (Grab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0));
            foreach (var state in new[] { spawn, dropped, held }) Transition(state, off, (Enabled, AnimatorConditionMode.IfNot, 0));

            controller.AddLayer("Ink"); var layers = controller.layers; layers[1].defaultWeight = 1; controller.layers = layers;
            var ink = layers[1].stateMachine;
            var erased = State(controller, ink, "Erase", InkClip(false, 0));
            var idle = State(controller, ink, "Ready", InkClip(false, 10800));
            var draw = State(controller, ink, "Draw", InkClip(true, 10800));
            ink.defaultState = erased;
            // Disabling always clears the native trail, rather than hiding it for the next enable.
            Transition(erased, idle, (Enabled, AnimatorConditionMode.If, 0), (Clear, AnimatorConditionMode.IfNot, 0));
            foreach (var state in new[] { idle, draw })
            {
                Transition(state, erased, (Enabled, AnimatorConditionMode.IfNot, 0));
                Transition(state, erased, (Clear, AnimatorConditionMode.If, 0));
            }
            Transition(idle, draw, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0), (Left, AnimatorConditionMode.If, 0), ("GestureLeft", AnimatorConditionMode.Equals, 1));
            Transition(idle, draw, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0), (Right, AnimatorConditionMode.If, 0), ("GestureRight", AnimatorConditionMode.Equals, 1));
            Transition(idle, draw, (Grab + "_IsGrabbed", AnimatorConditionMode.If, 0), (Left, AnimatorConditionMode.IfNot, 0), (Right, AnimatorConditionMode.IfNot, 0));
            Transition(draw, idle, (Grab + "_IsGrabbed", AnimatorConditionMode.IfNot, 0));
            Transition(draw, idle, (Left, AnimatorConditionMode.If, 0), ("GestureLeft", AnimatorConditionMode.NotEqual, 1), (Right, AnimatorConditionMode.IfNot, 0));
            Transition(draw, idle, (Right, AnimatorConditionMode.If, 0), ("GestureRight", AnimatorConditionMode.NotEqual, 1), (Left, AnimatorConditionMode.IfNot, 0));
            Transition(draw, idle, (Left, AnimatorConditionMode.If, 0), ("GestureLeft", AnimatorConditionMode.NotEqual, 1), (Right, AnimatorConditionMode.If, 0), ("GestureRight", AnimatorConditionMode.NotEqual, 1));

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[] { Parameter(Enabled), Parameter(Clear) };
            AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
            var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            sub.controls = new List<VRCExpressionsMenu.Control>
            {
                new VRCExpressionsMenu.Control { name = "Enable pen", type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = Enabled }, value = 1 },
                new VRCExpressionsMenu.Control { name = "Clear drawing", type = VRCExpressionsMenu.Control.ControlType.Button, parameter = new VRCExpressionsMenu.Control.Parameter { name = Clear }, value = 1 }
            };
            AssetDatabase.CreateAsset(sub, folder + "/Pen menu.asset");
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control> { new VRCExpressionsMenu.Control { name = MenuPath, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub } };
            AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
            VrcFury.Writer.FullController(root, controller, menu, parameters);
        }

        private static VRCExpressionParameters.Parameter Parameter(string name) => new VRCExpressionParameters.Parameter
        { name = name, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = false, networkSynced = true };

        private static AnimationClip PropClip(bool visible, bool baseFrozen, bool penFrozen, bool atHome)
        {
            var clip = new AnimationClip();
            Curve(clip, "Pen/Model", typeof(GameObject), "m_IsActive", visible ? 1 : 0);
            Curve(clip, "Grab base/Bone", typeof(VRCPhysBone), "m_Enabled", visible ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "FreezeToWorld", baseFrozen ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "Sources.source0.Weight", atHome ? 1 : 0);
            Curve(clip, "Grab base", typeof(VRCParentConstraint), "Sources.source1.Weight", atHome ? 0 : 1);
            Curve(clip, "Pen", typeof(VRCParentConstraint), "FreezeToWorld", penFrozen ? 1 : 0);
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
