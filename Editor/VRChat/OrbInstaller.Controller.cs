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
    public static partial class OrbInstaller
    {
        // The text's own animator parameters (never avatar parameters): the block the sync values write, one update late,
        // and the values, ValueLag updates later still; what each cell holds, as the float its glyph was sent as; a
        // constant 1.
        internal const string Block = "Orb/Text/Block", One = "Orb/One";
        internal static string Value(int index) => "Orb/Text/Value" + index;
        // How many updates later than the pointer the sync values reach the cells: VRChat may apply the values a frame
        // or two before the pointer sent with them, which wrote each block with the next one's letters. The values go
        // through ValueLag copies, an update each, before the value the cells read (TextLate(i, ValueLag)).
        internal const int ValueLag = 2;
        internal static string TextLate(int index, int stage) => "Orb/Text/Late" + stage + "/" + index;
        internal static string Cell(int cell) => "Orb/Text/Cell" + cell;
        // The panel's cell properties (the text shader's _Char0 to _Char127).
        internal static string CellProperty(int cell) => "material._Char" + cell;
        // How long a change of state takes to show (the eyes morph meanwhile).
        internal const float StateBlend = .18f;

        /// <summary>How a state looks: the orb shader's properties, the projector beam, and whether the orb hops.</summary>
        internal sealed class Look
        {
            public float Glow = 1, Open = 1, Wide = 1, Curve, Lid, Tilt, LookX, LookY, Anger, Ring, Talk, Blink = 1, Bob = 1, Sleep, Beam;
            public bool Hops;

            public IEnumerable<(string property, float value)> Values(float pulse) => new[]
            {
                ("_Glow", Glow), ("_Open", Open), ("_Wide", Wide), ("_Curve", Curve), ("_Lid", Lid), ("_Tilt", Tilt), ("_LookX", LookX),
                ("_LookY", LookY), ("_Anger", Anger), ("_Pulse", pulse), ("_Ring", Ring), ("_Talk", Talk), ("_Blink", Blink), ("_Bob", Bob), ("_Sleep", Sleep),
            };
        }

        // Docs/orb.md's states, in their order.
        internal static readonly Look[] Looks =
        {
            // Idle: a calm glow, a slow bob, a blink now and then.
            new Look(),
            // Listening: brighter, eyes wide, glowing up with its owner's voice.
            new Look { Glow = 1.3f, Open = 1.18f, Wide = 1.08f, LookY = .02f, Bob = .6f },
            // Thinking: narrowed eyes under flat lids looking up aside, a ring turning around it.
            new Look { Glow = 1.05f, Open = .75f, Lid = .3f, LookX = .08f, LookY = .08f, Ring = 1, Blink = 0, Bob = .8f },
            // Talking: the beam that projects the panel, a speaking shimmer.
            new Look { Glow = 1.15f, LookY = -.03f, Talk = 1, Beam = 1 },
            // Sleeping (its owner is away): dim, eyes closed into smiles, a slow low bob.
            new Look { Glow = .38f, Open = 0, Curve = 1, LookY = -.04f, Blink = 0, Bob = .5f, Sleep = 1 },
            // Happy: eyes as arches.
            new Look { Glow = 1.2f, Open = 0, Wide = 1.08f, Curve = -1.3f, LookY = .02f, Blink = 0, Bob = 1.2f },
            // Annoyed: eyes as flat slits, their inner ends low, a red tint.
            new Look { Open = .28f, Wide = 1.1f, Tilt = .45f, Anger = 1, Blink = 0, Bob = .5f },
            // Surprised: round wide eyes and a short jump.
            new Look { Glow = 1.3f, Open = 1.1f, Wide = 1.45f, LookY = .03f, Blink = 0, Hops = true },
        };

        /// <param name="walkBoost">Adds the Walk boost radial (<see cref="WalkBoost"/>, local and saved): Orbiters desktop's walk boost.</param>
        /// <param name="replace">The projected screen shows Orb's menu in VR too, instead of Orbiters desktop's SteamVR
        /// overlay (<see cref="ScreenReplace"/>'s default). Every orb has the screen (<see cref="ScreenController"/>).</param>
        /// <param name="thumbnailBytes">The bytes of each thumbnail the screen keeps, 72 or 128 (<see cref="ScreenThumbnailBytes"/>'s default).</param>
        internal static void BuildController(GameObject root, string folder, int textSync, bool walkBoost = false, bool replace = false, int thumbnailBytes = DefaultThumbnailBytes)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Orb.controller");
            controller.AddParameter(new AnimatorControllerParameter { name = Visible, type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter(State, AnimatorControllerParameterType.Int);
            controller.AddParameter(Talk, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Touch, AnimatorControllerParameterType.Bool);
            // Read by Orbiters desktop over OSC only, as the walk boost.
            controller.AddParameter(Photo3D, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Menu, AnimatorControllerParameterType.Bool);
            foreach (var button in new[] { CallMenu, CallGo, CallEnd }) controller.AddParameter(button, AnimatorControllerParameterType.Bool);
            controller.AddParameter(CallPick, AnimatorControllerParameterType.Float);
            controller.AddParameter(TextVisible, AnimatorControllerParameterType.Bool);
            controller.AddParameter(TextPointer, AnimatorControllerParameterType.Int);
            for (int i = 0; i < textSync; i++) controller.AddParameter(TextSync(i), AnimatorControllerParameterType.Float);
            // VRChat's: how loud its owner's voice is.
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter(Block, AnimatorControllerParameterType.Float);
            for (int i = 0; i < textSync; i++) controller.AddParameter(Value(i), AnimatorControllerParameterType.Float);
            for (int stage = 1; stage <= ValueLag; stage++)
            for (int i = 0; i < textSync; i++) controller.AddParameter(TextLate(i, stage), AnimatorControllerParameterType.Float);
            for (int c = 0; c < Cells; c++) controller.AddParameter(Cell(c), AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = One, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            // Read by Orbiters desktop over OSC only: no layer uses it.
            if (walkBoost) controller.AddParameter(WalkBoost, AnimatorControllerParameterType.Float);

            Toggled(controller, controller.layers[0].stateMachine, Visible, Shown);
            States(controller);
            Toggled(controller, Layer(controller, "Text visible"), TextVisible, Panel);
            Blocks(controller, textSync);
            Text(controller, textSync);

            var parameters = new List<VRCExpressionParameters.Parameter>
            {
                Parameter(Visible, saved: true, defaultValue: 1),
                Parameter(State, type: VRCExpressionParameters.ValueType.Int),
                // Local, but expression parameters all the same: OSC reads and sets those only.
                Parameter(Talk, synced: false),
                Parameter(Touch, synced: false),
                Parameter(Photo3D, synced: false),
                Parameter(Menu, synced: false),
                Parameter(CallMenu, synced: false),
                Parameter(CallPick, synced: false, type: VRCExpressionParameters.ValueType.Float),
                Parameter(CallGo, synced: false),
                Parameter(CallEnd, synced: false),
                Parameter(TextVisible),
                Parameter(TextPointer, type: VRCExpressionParameters.ValueType.Int),
            };
            for (int i = 0; i < textSync; i++) parameters.Add(Parameter(TextSync(i), type: VRCExpressionParameters.ValueType.Float));
            ScreenController(controller, textSync, parameters, replace, thumbnailBytes);
            var controls = new List<VRCExpressionsMenu.Control>
            {
                Control("Show Orb", VRCExpressionsMenu.Control.ControlType.Toggle, Visible),
                // Opens Orb's menu directly (the other triggers go through Orbiters desktop: a binding, a word, a touch).
                Control("Menu", VRCExpressionsMenu.Control.ControlType.Toggle, Menu),
                Control("Talk", VRCExpressionsMenu.Control.ControlType.Button, Talk),
                Control("3D picture", VRCExpressionsMenu.Control.ControlType.Button, Photo3D),
                CallSubmenu(folder),
            };
            if (walkBoost)
            {
                parameters.Add(Parameter(WalkBoost, synced: false, type: VRCExpressionParameters.ValueType.Float, saved: true));
                // A radial sets its first sub-parameter; its own parameter (none here) is only on while it is open.
                var radial = Control("Walk boost", VRCExpressionsMenu.Control.ControlType.RadialPuppet, "");
                radial.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = WalkBoost } };
                controls.Add(radial);
            }
            Install(root, folder, controller, MenuPath, "Orb menu", controls, parameters.ToArray(), parameters.Select(p => p.name));
        }

        /// <summary>
        /// Orb › Call (Orbiters contracts' Docs/orb-calls.md): the contacts on the panel, a radial choosing one, call or
        /// answer, decline or hang up. All read by Orbiters desktop over OSC.
        /// </summary>
        private static VRCExpressionsMenu.Control CallSubmenu(string folder)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            var choose = Control("Choose", VRCExpressionsMenu.Control.ControlType.RadialPuppet, "");
            choose.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = CallPick } };
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                Control("Contacts", VRCExpressionsMenu.Control.ControlType.Button, CallMenu),
                choose,
                Control("Call / Answer", VRCExpressionsMenu.Control.ControlType.Button, CallGo),
                Control("Hang up", VRCExpressionsMenu.Control.ControlType.Button, CallEnd),
            };
            AssetDatabase.CreateAsset(menu, AssetDatabase.GenerateUniqueAssetPath(folder + "/Orb call menu.asset"));
            var call = Control("Call", VRCExpressionsMenu.Control.ControlType.SubMenu, "");
            call.subMenu = menu;
            return call;
        }

        // An object shown while a bool is on: in "Shown" by default (the orb), else "Hidden" (the panel).
        private static void Toggled(AnimatorController controller, AnimatorStateMachine machine, string parameter, string path)
        {
            var hidden = new AnimationClip(); Curve(hidden, path, typeof(GameObject), "m_IsActive", 0);
            var shown = new AnimationClip(); Curve(shown, path, typeof(GameObject), "m_IsActive", 1);
            var off = HeldPropRig.State(controller, machine, "Hidden", hidden);
            var on = HeldPropRig.State(controller, machine, "Shown", shown);
            machine.defaultState = parameter == Visible ? on : off;
            Transition(off, on, (parameter, AnimatorConditionMode.If, 0));
            Transition(on, off, (parameter, AnimatorConditionMode.IfNot, 0));
        }

        // ---- What Orb is doing ----

        // One state for each value of Orb/State, reached from any other; each sets everything the layer animates, so states
        // blend into one another and leave nothing behind (Write Defaults on or off).
        private static void States(AnimatorController controller)
        {
            var machine = Layer(controller, "State");
            var states = new AnimatorState[Looks.Length];
            for (int s = 0; s < Looks.Length; s++)
            {
                string name = ((OrbState)s).ToString();
                Motion motion = Clip(name, Looks[s], 0);
                // Listening glows up with its owner's voice.
                if (s == (int)OrbState.Listening)
                {
                    var voice = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = "Voice", useAutomaticThresholds = false };
                    voice.AddChild(motion, 0);
                    voice.AddChild(Clip(name + " loud", Looks[s], 1), 1);
                    motion = voice;
                }
                states[s] = HeldPropRig.State(controller, machine, name, motion);
                if (s > 0) Enter(machine, states[s], (State, AnimatorConditionMode.Equals, s));
            }
            machine.defaultState = states[0];
            // Idle for 0 and for any value that is no state.
            Enter(machine, states[0], (State, AnimatorConditionMode.Less, 1));
            Enter(machine, states[0], (State, AnimatorConditionMode.Greater, Looks.Length - 1));
        }

        private static AnimationClip Clip(string name, Look look, float pulse)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (property, value) in look.Values(pulse)) Curve(clip, Body, typeof(MeshRenderer), "material." + property, value);
            Curve(clip, Panel, typeof(SkinnedMeshRenderer), "material._Beam", look.Beam);
            foreach (char axis in "xz") Curve(clip, Hop, typeof(Transform), "m_LocalPosition." + axis, 0);
            if (!look.Hops) Curve(clip, Hop, typeof(Transform), "m_LocalPosition.y", 0);
            else
            {
                // Up 3.5 cm and back down in 0.36 s, then still.
                var jump = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.14f, .035f), new Keyframe(.36f, 0), new Keyframe(.6f, 0));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Hop, typeof(Transform), "m_LocalPosition.y"), jump);
            }
            return clip;
        }

        // A transition from any state (not to itself) that blends for StateBlend.
        private static void Enter(AnimatorStateMachine machine, AnimatorState to, params (string name, AnimatorConditionMode mode, float value)[] conditions)
        {
            var transition = machine.AddAnyStateTransition(to);
            transition.hasExitTime = false; transition.hasFixedDuration = true; transition.duration = StateBlend;
            transition.canTransitionToSelf = false;
            foreach (var condition in conditions) transition.AddCondition(condition.mode, condition.value, condition.name);
        }

        // ---- The text ----

        /// <summary>
        /// Orb/Text/Pointer as a float (<see cref="Block"/>, for the text's blend trees): one state per block, clearing and
        /// nothing, each setting it. It is one update late (the values the text layer copies are later still).
        /// </summary>
        private static void Blocks(AnimatorController controller, int textSync)
        {
            var machine = Layer(controller, "Text block");
            AnimatorState Set(string name, float block) => HeldPropRig.State(controller, machine, name, ParameterClip(name, Block, block));
            var none = Set("None", PointerNone);
            machine.defaultState = none;
            int blocks = Cells / textSync;
            for (int p = 1; p <= blocks; p++) Jump(machine, Set("Block " + p, p), (TextPointer, AnimatorConditionMode.Equals, p));
            Jump(machine, Set("Clear", PointerClear), (TextPointer, AnimatorConditionMode.Equals, PointerClear));
            // Nothing for 0 and for any other pointer.
            Jump(machine, none, (TextPointer, AnimatorConditionMode.Less, 1));
            Jump(machine, none, (TextPointer, AnimatorConditionMode.Greater, blocks), (TextPointer, AnimatorConditionMode.Less, PointerClear));
            Jump(machine, none, (TextPointer, AnimatorConditionMode.Greater, PointerClear));
        }

        // A transition from any other state, at once.
        private static void Jump(AnimatorStateMachine machine, AnimatorState to, params (string name, AnimatorConditionMode mode, float value)[] conditions)
        {
            var transition = machine.AddAnyStateTransition(to);
            transition.hasExitTime = false; transition.duration = 0; transition.canTransitionToSelf = false;
            foreach (var condition in conditions) transition.AddCondition(condition.mode, condition.value, condition.name);
        }

        /// <summary>
        /// The panel's cells (Docs/orb.md, Text): sync value i of block p writes cell (p − 1) × N + i for as long as the
        /// pointer stays at p; every other cell keeps what it holds; pointer 255 clears them all. KillFrenzy Avatar Text keeps
        /// cells with Write Defaults off, but VRCFury turns a Full Controller's layers to the avatar's setting, and with Write
        /// Defaults on a cell would fall back to a space as soon as its pointer moves on. Here each cell's glyph is kept in an
        /// animator parameter (<see cref="Cell"/>) that one direct blend tree writes back every update, Write Defaults on (as
        /// VRCFury keeps direct blend trees): it holds the cell, takes the sync value while its block is pointed at, or
        /// clears, and sets the cell's material property from it. The pointer is one update late (<see cref="Block"/>) and
        /// the values it reads <see cref="ValueLag"/> updates later still (<see cref="Value"/>), so neither a new pointer
        /// nor values VRChat applied a little before their pointer write the previous block's cells with the new values.
        /// Signed values go through 1D trees between −1 and 1 (direct blend weights can't be negative).
        /// </summary>
        private static void Text(AnimatorController controller, int textSync)
        {
            var machine = Layer(controller, "Text");
            var tree = Hidden(new BlendTree { name = "Text", blendType = BlendTreeType.Direct });
            var children = new List<ChildMotion>();
            void Add(Motion motion) => children.Add(new ChildMotion { motion = motion, directBlendParameter = One, timeScale = 1 });
            for (int i = 0; i < textSync; i++)
            {
                string from = TextSync(i);
                for (int stage = 1; stage <= ValueLag; stage++)
                {
                    Add(Copy("Value " + i + " late " + stage, from, Clip(TextLate(i, stage), -1), Clip(TextLate(i, stage), 1)));
                    from = TextLate(i, stage);
                }
                Add(Copy("Value " + i, from, Clip(Value(i), -1), Clip(Value(i), 1)));
            }
            int blocks = Cells / textSync;
            for (int c = 0; c < Cells; c++)
            {
                int block = c / textSync + 1;
                // −127 and 127 on the panel, the ends of a glyph's float.
                var low = Hidden(new AnimationClip { name = "Cell " + c + " low" });
                Curve(low, "", typeof(Animator), Cell(c), -1); Curve(low, Panel, typeof(SkinnedMeshRenderer), CellProperty(c), -127);
                var high = Hidden(new AnimationClip { name = "Cell " + c + " high" });
                Curve(high, "", typeof(Animator), Cell(c), 1); Curve(high, Panel, typeof(SkinnedMeshRenderer), CellProperty(c), 127);
                var clear = Hidden(new AnimationClip { name = "Cell " + c + " clear" });
                Curve(clear, "", typeof(Animator), Cell(c), 0); Curve(clear, Panel, typeof(SkinnedMeshRenderer), CellProperty(c), 0);
                var keep = Copy("Cell " + c + " keeps", Cell(c), low, high);
                var write = Copy("Cell " + c + " takes value " + c % textSync, Value(c % textSync), low, high);
                // By the block: kept everywhere but at its own block (written) and at 255 (cleared).
                var cell = Hidden(new BlendTree { name = "Cell " + c, blendType = BlendTreeType.Simple1D, blendParameter = Block, useAutomaticThresholds = false });
                cell.AddChild(keep, PointerNone);
                if (block - 1 > PointerNone) cell.AddChild(keep, block - 1);
                cell.AddChild(write, block);
                if (block + 1 < PointerClear - 1) cell.AddChild(keep, block + 1);
                cell.AddChild(keep, PointerClear - 1);
                cell.AddChild(clear, PointerClear);
                Add(cell);
            }
            tree.children = children.ToArray();
            var state = HeldPropRig.State(controller, machine, "Text", tree);
            state.writeDefaultValues = true;
        }

        // A clip setting the animator parameter that holds a value or a cell.
        private static AnimationClip Clip(string parameter, float value)
        {
            var clip = Hidden(new AnimationClip { name = parameter + " = " + value });
            Curve(clip, "", typeof(Animator), parameter, value);
            return clip;
        }

        // What `low` and `high` set, as far between them as `from` is between −1 and 1.
        private static BlendTree Copy(string name, string from, Motion low, Motion high)
        {
            var tree = Hidden(new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = from, useAutomaticThresholds = false });
            tree.AddChild(low, -1);
            tree.AddChild(high, 1);
            return tree;
        }

        // The text's hundreds of motions stay out of the Project window's view of the controller.
        private static T Hidden<T>(T motion) where T : Motion
        {
            motion.hideFlags = HideFlags.HideInHierarchy;
            return motion;
        }
    }
}
