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
        internal const string Block = "N4N0/Text/Block", One = "N4N0/One";
        internal static string Value(int index) => "N4N0/Text/Value" + index;
        // How many updates later than the pointer the sync values reach the cells: VRChat may apply the values a frame
        // or two before the pointer sent with them, which wrote each block with the next one's letters. The values go
        // through ValueLag copies, an update each, before the value the cells read (TextLate(i, ValueLag)).
        internal const int ValueLag = 2;
        internal static string TextLate(int index, int stage) => "N4N0/Text/Late" + stage + "/" + index;
        internal static string Cell(int cell) => "N4N0/Text/Cell" + cell;
        // The panel's cell properties (the text shader's _Char0 to _Char127).
        internal static string CellProperty(int cell) => "material._Char" + cell;
        // How long a change of state takes to show (the eyes morph meanwhile).
        internal const float StateBlend = .18f;

        /// <summary>
        /// How a state looks: the model's expression (its blendshapes' weights, out of 100), whether its eyes blink now and
        /// then and its mouth moves, its bob (1 for 1.1 cm at a 3.2 s pace; slow is 5.5 s) or Surprised's jump, the projector
        /// beam, and anger (the Colour layer's red).
        /// </summary>
        internal sealed class Look
        {
            public readonly Dictionary<string, float> Shapes = new Dictionary<string, float>();
            public float Bob = 1, Beam, Anger, Loud;
            public bool Blinks, Talks, Hops, Slow;

            /// <summary>A blendshape's weight, at <paramref name="pulse"/> 1 its owner's voice at its loudest (Listening's eyes widen up to <see cref="Loud"/>).</summary>
            public float Weight(string shape, float pulse)
            {
                Shapes.TryGetValue(shape, out float weight);
                return shape == "Surprised" && Loud > 0 ? Mathf.Lerp(weight, Loud, pulse) : weight;
            }
        }

        // Docs/orb.md's states, in their order.
        internal static readonly Look[] Looks =
        {
            // Idle: calm, a slow bob, a blink now and then.
            new Look { Blinks = true },
            // Listening: eyes a little wider, more with its owner's voice.
            new Look { Shapes = { ["Surprised"] = 20 }, Loud = 45, Blinks = true, Bob = .6f },
            // Thinking: one eye raised, the mouth aside.
            new Look { Shapes = { ["Thinking"] = 100 }, Bob = .8f },
            // Talking: the mouth moving, the beam that projects the panel.
            new Look { Talks = true, Blinks = true, Beam = 1 },
            // Sleeping (its owner is away): flat lids, a slow low bob.
            new Look { Shapes = { ["Sleeping"] = 100 }, Bob = .5f, Slow = true },
            // Happy: flat smiling eyes and an open smile.
            new Look { Shapes = { ["Happy"] = 100 }, Bob = 1.2f },
            // Annoyed: brows down, a frown, a red tint.
            new Look { Shapes = { ["Annoyed"] = 100 }, Anger = 1, Bob = .5f },
            // Surprised: tall eyes, a round mouth and a short jump.
            new Look { Shapes = { ["Surprised"] = 100 }, Hops = true },
        };

        // The anger the PC State layer hands its Colour layer: an animated animator parameter, never an avatar one (the
        // mobile N4N0's is MobileAnger, which tells its controller apart).
        internal const string Anger = "N4N0/Anger";
        // When the blinking states blink in their 9.6 s loop, and for how long.
        private static readonly float[] Blinks = { 3.1f, 7.6f };
        private const float BlinkLength = .1f;
        // The patterns' glow in the Light option's colour: how dark and how bright its brightness makes it; annoyed red.
        internal const float TintDim = .8f, TintBright = 1.2f;
        private static readonly Color AngerRed = new Color(1, .1f, .07f);

        /// <param name="walkBoost">Adds the Walk boost radial (<see cref="WalkBoost"/>, local and saved): Orbiters - N4N0's walk boost.</param>
        /// <param name="replace">The projected screen shows N4N0's menu in VR too, instead of Orbiters - N4N0's SteamVR
        /// overlay (<see cref="ScreenReplace"/>'s default). Every N4N0 has the screen (<see cref="ScreenController"/>).</param>
        /// <param name="thumbnailBytes">The bytes of each thumbnail the screen keeps, 72 or 128 (<see cref="ScreenThumbnailBytes"/>'s default).</param>
        /// <param name="light">Adds the light's parameters, layer and submenu (<see cref="LightController"/>).</param>
        internal static void BuildController(GameObject root, string folder, int textSync, bool walkBoost = false, bool replace = false, int thumbnailBytes = DefaultThumbnailBytes,
            bool light = false)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/N4N0.controller");
            controller.AddParameter(new AnimatorControllerParameter { name = Visible, type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter(State, AnimatorControllerParameterType.Int);
            controller.AddParameter(Talk, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Touch, AnimatorControllerParameterType.Bool);
            // Read by Orbiters - N4N0 over OSC only, as the walk boost.
            controller.AddParameter(Photo3D, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Menu, AnimatorControllerParameterType.Bool);
            foreach (var button in new[] { CallMenu, CallGo, CallEnd }) controller.AddParameter(button, AnimatorControllerParameterType.Bool);
            controller.AddParameter(CallPick, AnimatorControllerParameterType.Float);
            controller.AddParameter(TextVisible, AnimatorControllerParameterType.Bool);
            controller.AddParameter(TextPointer, AnimatorControllerParameterType.Int);
            for (int i = 0; i < textSync; i++) controller.AddParameter(TextSync(i), AnimatorControllerParameterType.Float);
            // VRChat's: how loud its owner's voice is, and whether this client is its owner's.
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            // The Light option's, which the Colour layer reads (an N4N0 without the option never sets them: white).
            controller.AddParameter(Light, AnimatorControllerParameterType.Bool);
            controller.AddParameter(LightColor, AnimatorControllerParameterType.Int);
            controller.AddParameter(new AnimatorControllerParameter { name = LightLevel, type = AnimatorControllerParameterType.Float, defaultFloat = LightLevelDefault });
            controller.AddParameter(Anger, AnimatorControllerParameterType.Float);
            controller.AddParameter(Block, AnimatorControllerParameterType.Float);
            for (int i = 0; i < textSync; i++) controller.AddParameter(Value(i), AnimatorControllerParameterType.Float);
            for (int stage = 1; stage <= ValueLag; stage++)
            for (int i = 0; i < textSync; i++) controller.AddParameter(TextLate(i, stage), AnimatorControllerParameterType.Float);
            for (int c = 0; c < Cells; c++) controller.AddParameter(Cell(c), AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter { name = One, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            // Read by Orbiters - N4N0 over OSC only: no layer uses it.
            if (walkBoost) controller.AddParameter(WalkBoost, AnimatorControllerParameterType.Float);

            Toggled(controller, controller.layers[0].stateMachine, Visible, Shown);
            ModelStates(controller, "State", Body, Hop, Anger, beam: true);
            TintLayer(controller, "Colour", Body, Anger);
            FacingLayer(controller, "Facing", Body);
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
                Control("Show N4N0", VRCExpressionsMenu.Control.ControlType.Toggle, Visible),
                // Opens N4N0's menu directly (the other triggers go through Orbiters - N4N0: a binding, a word, a touch).
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
            if (light) LightController(controller, folder, parameters, controls);
            CaptureController(controller, parameters, controls);
            Install(root, folder, controller, MenuPath, "N4N0 menu", controls, parameters.ToArray(), parameters.Select(p => p.name));
        }

        /// <summary>
        /// N4N0 › Call (Orbiters contracts' Docs/orb-calls.md): the contacts on the panel, a radial choosing one, call or
        /// answer, decline or hang up. All read by Orbiters - N4N0 over OSC.
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
            AssetDatabase.CreateAsset(menu, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 call menu.asset"));
            var call = Control("Call", VRCExpressionsMenu.Control.ControlType.SubMenu, "");
            call.subMenu = menu;
            return call;
        }

        // An object shown while a bool is on: in "Shown" by default (N4N0), else "Hidden" (the panel).
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

        // ---- What N4N0 is doing ----

        /// <summary>
        /// One state for each value of N4N0/State, reached from any other and blending for <see cref="StateBlend"/>; each
        /// sets everything the layer animates (the model at <paramref name="body"/>, floating on <paramref name="lift"/>, its
        /// anger in <paramref name="anger"/>, and, with <paramref name="beam"/>, the panel's beam), so states blend into one
        /// another and leave nothing behind (Write Defaults on or off). Listening widens its eyes with its owner's voice.
        /// </summary>
        private static void ModelStates(AnimatorController controller, string layer, string body, string lift, string anger, bool beam = false)
        {
            var machine = Layer(controller, layer);
            var states = new AnimatorState[Looks.Length];
            for (int s = 0; s < Looks.Length; s++)
            {
                string name = ((OrbState)s).ToString();
                Motion motion = ModelClip(name, Looks[s], 0, body, lift, anger, beam);
                if (Looks[s].Loud > 0)
                {
                    var voice = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = "Voice", useAutomaticThresholds = false };
                    voice.AddChild(motion, 0);
                    voice.AddChild(ModelClip(name + " loud", Looks[s], 1, body, lift, anger, beam), 1);
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

        /// <summary>
        /// A state's clip, looping whole bobs (three of 3.2 s, two of 5.5 s when slow): every blendshape's weight (the blinks
        /// and the talking mouth moving in it), the bob or Surprised's jump (which plays once), the anger and the beam.
        /// </summary>
        private static AnimationClip ModelClip(string name, Look look, float pulse, string body, string lift, string anger, bool beam)
        {
            float period = look.Slow ? 5.5f : 3.2f, length = period * (look.Slow ? 2 : 3);
            var clip = new AnimationClip { name = name };
            foreach (var shape in ModelShapes)
            {
                string property = "blendShape." + shape;
                if (shape == "Blink" && look.Blinks) SetCurve(clip, body, typeof(SkinnedMeshRenderer), property, Steps(length, 0, 100, Blinks));
                else if (shape == "Talk" && look.Talks)
                    SetCurve(clip, body, typeof(SkinnedMeshRenderer), property, Sampled(length, t => 100 * Mathf.Clamp01(.2f + .55f * Mathf.Sin(t * 17) + .35f * Mathf.Sin(t * 7.3f + 1))));
                else Curve(clip, body, typeof(SkinnedMeshRenderer), property, look.Weight(shape, pulse));
            }
            Curve(clip, "", typeof(Animator), anger, look.Anger);
            if (beam) Curve(clip, Panel, typeof(SkinnedMeshRenderer), "material._Beam", look.Beam);
            // Up 3.5 cm and back down in 0.36 s, over the bob.
            var jump = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.14f, .035f), new Keyframe(.36f, 0), new Keyframe(.6f, 0));
            float bob = .22f * look.Bob * Radius;
            SetCurve(clip, lift, typeof(Transform), "m_LocalPosition.y",
                Sampled(length, t => bob * Mathf.Sin(t * 2 * Mathf.PI / period) + (look.Hops && t <= .6f ? jump.Evaluate(t) : 0)));
            foreach (char axis in "xz") Curve(clip, lift, typeof(Transform), "m_LocalPosition." + axis, 0);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = !look.Hops;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        /// <summary>
        /// The patterns' glow (their emission, which the shell's material doesn't use): white while the light is off, else
        /// the light's colour, darker or brighter with its brightness; red while annoyed (<paramref name="anger"/>). One state
        /// per colour, as the Light layer's, each setting every colour the layer animates. Without the Light option its
        /// parameters stay off: white.
        /// </summary>
        private static void TintLayer(AnimatorController controller, string layer, string body, string anger)
        {
            var machine = Layer(controller, layer);
            var off = HeldPropRig.State(controller, machine, "Off", TintTree("N4N0", PatternColour, 1, body, anger));
            machine.defaultState = off;
            Enter(machine, off, (Light, AnimatorConditionMode.IfNot, 0));
            for (int i = 0; i < LightColors.Count; i++)
            {
                var (name, color) = LightColors[i];
                var level = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = LightLevel, useAutomaticThresholds = false };
                level.AddChild(TintTree(name + " dim", Color.Lerp(color, Color.white, .35f), TintDim, body, anger), 0);
                level.AddChild(TintTree(name + " bright", Color.Lerp(color, Color.white, .35f), TintBright, body, anger), 1);
                var state = HeldPropRig.State(controller, machine, name, level);
                Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Equals, i));
                // A colour past the last is the first.
                if (i == 0) Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Greater, LightColors.Count - 1));
            }
        }

        // A glow and its annoyed red, by anger.
        private static BlendTree TintTree(string name, Color colour, float brightness, string body, string anger)
        {
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = anger, useAutomaticThresholds = false };
            tree.AddChild(TintClip(name, colour * brightness, body), 0);
            tree.AddChild(TintClip(name + " annoyed", Color.Lerp(colour, AngerRed, .9f) * brightness, body), 1);
            return tree;
        }

        private static AnimationClip TintClip(string name, Color glow, string body)
        {
            var clip = new AnimationClip { name = name };
            Curve(clip, body, typeof(SkinnedMeshRenderer), "material._EmissionColor.r", glow.r);
            Curve(clip, body, typeof(SkinnedMeshRenderer), "material._EmissionColor.g", glow.g);
            Curve(clip, body, typeof(SkinnedMeshRenderer), "material._EmissionColor.b", glow.b);
            Curve(clip, body, typeof(SkinnedMeshRenderer), "material._EmissionColor.a", 1);
            return clip;
        }

        /// <summary>
        /// The face (the object at <paramref name="path"/>) looks ahead, at whoever is in front; on its owner's client, at
        /// its owner's eyes.
        /// </summary>
        private static void FacingLayer(AnimatorController controller, string layer, string path)
        {
            var machine = Layer(controller, layer);
            AnimationClip Turned(string name, Quaternion turn)
            {
                var clip = new AnimationClip { name = name };
                Curve(clip, path, typeof(Transform), "m_LocalRotation.x", turn.x);
                Curve(clip, path, typeof(Transform), "m_LocalRotation.y", turn.y);
                Curve(clip, path, typeof(Transform), "m_LocalRotation.z", turn.z);
                Curve(clip, path, typeof(Transform), "m_LocalRotation.w", turn.w);
                return clip;
            }
            var ahead = HeldPropRig.State(controller, machine, "Ahead", Turned("Face ahead", Quaternion.identity));
            var owner = HeldPropRig.State(controller, machine, "At its owner", Turned("Face its owner", OwnerFacing));
            machine.defaultState = ahead;
            Transition(ahead, owner, ("IsLocal", AnimatorConditionMode.If, 0));
            Transition(owner, ahead, ("IsLocal", AnimatorConditionMode.IfNot, 0));
        }

        private static void SetCurve(AnimationClip clip, string path, Type type, string property, AnimationCurve curve) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);

        // A value that holds, then jumps to `shut` for BlinkLength at each blink, and back.
        private static AnimationCurve Steps(float length, float open, float shut, IEnumerable<float> blinks)
        {
            var keys = new List<Keyframe> { new Keyframe(0, open) };
            foreach (float t in blinks) { keys.Add(new Keyframe(t, shut)); keys.Add(new Keyframe(t + BlinkLength, open)); }
            keys.Add(new Keyframe(length, open));
            var curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
            return curve;
        }

        // A value over `length`, sampled 30 times a second, straight between samples.
        private static AnimationCurve Sampled(float length, Func<float, float> value)
        {
            int count = Mathf.CeilToInt(length * 30);
            var curve = new AnimationCurve(Enumerable.Range(0, count + 1).Select(i => { float t = Mathf.Min(length, i / 30f); return new Keyframe(t, value(t)); }).ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            return curve;
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
        /// N4N0/Text/Pointer as a float (<see cref="Block"/>, for the text's blend trees): one state per block, clearing and
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
