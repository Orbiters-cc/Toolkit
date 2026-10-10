using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    public static partial class VotingSignInstaller
    {
        private static readonly Names Held = new Names
            { Body = Body, Model = Model, Enabled = Enabled, Grab = Grab, Left = Left, Right = Right, HeldLeft = HeldLeft, HeldRight = HeldRight, Drop = Drop };

        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Voting sign.controller");
            HeldParameters(controller, Held);
            controller.AddParameter(Vote, AnimatorControllerParameterType.Int);
            HeldLayers(controller, Held);

            // The score follows the synced vote from any state; values without a cell keep the last one.
            var scores = Layer(controller, "Score");
            var blank = State(controller, scores, "No vote", ScoreClip(Blank));
            scores.defaultState = blank;
            foreach (var (state, value) in new[] { (blank, 0) }.Concat(Choices.Select(c => (State(controller, scores, c.Name, ScoreClip(c.Cell)), c.Value))))
            {
                var show = scores.AddAnyStateTransition(state);
                show.duration = 0; show.hasExitTime = false; show.canTransitionToSelf = false;
                show.AddCondition(AnimatorConditionMode.Equals, value, Vote);
            }

            // Each score is a toggle: choosing it again clears the sign.
            var votes = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            votes.controls = Choices.Where(c => c.Value <= 10).Select(c => VoteControl(folder, c)).ToList();
            AssetDatabase.CreateAsset(votes, folder + "/Vote menu.asset");
            var controls = new List<VRCExpressionsMenu.Control>
            {
                Control("Show sign", VRCExpressionsMenu.Control.ControlType.Toggle, Enabled),
                new VRCExpressionsMenu.Control { name = "Vote", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = votes, icon = IconOf(folder, "Sign") },
            };
            controls.AddRange(Choices.Where(c => c.Value > 10).Select(c => VoteControl(folder, c)));
            controls.Add(Control("Drop sign", VRCExpressionsMenu.Control.ControlType.Button, Drop));
            Install(root, folder, controller, MenuPath, "Sign menu", controls, new[]
            {
                Parameter(Enabled),
                new VRCExpressionParameters.Parameter { name = Vote, valueType = VRCExpressionParameters.ValueType.Int, defaultValue = 0, saved = false, networkSynced = true },
                Parameter(HeldLeft), Parameter(HeldRight), Parameter(Drop),
            });
        }

        private static VRCExpressionsMenu.Control VoteControl(string folder, Choice choice)
        {
            var control = Icon(Control(choice.Name, VRCExpressionsMenu.Control.ControlType.Toggle, Vote), folder, choice.Name);
            control.value = choice.Value;
            return control;
        }

        private static VRCExpressionsMenu.Control Icon(VRCExpressionsMenu.Control control, string folder, string name)
        {
            control.icon = IconOf(folder, name);
            return control;
        }

        private static Texture2D IconOf(string folder, string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Icons/" + name + ".png");

        // The score sheet's cell on the score plane.
        private static AnimationClip ScoreClip(Vector2 cell)
        {
            var clip = new AnimationClip();
            var st = new[] { CellSize.x, CellSize.y, cell.x, cell.y };
            for (int i = 0; i < 4; i++) Curve(clip, Score, typeof(MeshRenderer), "material._MainTex_ST." + "xyzw"[i], st[i]);
            return clip;
        }
    }
}
