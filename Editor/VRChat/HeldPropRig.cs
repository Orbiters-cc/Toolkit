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
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The grab rig of a held avatar prop (the drawing pen, the hand screen), on native avatar components only. The prop
    /// waits at Spawn; the end of a short PhysBone chain picks it up; the owner's wrist then carries it from where it was
    /// grabbed (one frame world-frozen, then unfrozen so each source's offset is measured where the prop is); let go, it
    /// stays world-fixed. The rig is avatar-independent: <see cref="Bind"/> fits it to an avatar's head and wrists.
    /// Also the controller pieces the props build their state machines from.
    /// </summary>
    internal static class HeldPropRig
    {
        internal const string Spawn = "Spawn", GrabBase = "Grab base", GrabBone = "Grab base/Bone";
        // The grab chain's root sits this far below the grip it moves.
        private static readonly Vector3 GripRise = Vector3.up * .02f;

        internal sealed class Parts
        {
            public Transform Home, Basis, Grip, Body;
            public VRCPhysBone Grab;
            public VRCParentConstraint Follow;
        }

        /// <summary>
        /// Builds the rig below <paramref name="root"/>: Spawn, the grab chain (its PhysBone reports
        /// <paramref name="grab"/>) and the carried <paramref name="body"/>, whose Left hand and Right hand contacts tell
        /// which hand picked it up.
        /// </summary>
        internal static Parts Build(Transform root, string body, Vector3 spawn, string grab, float grabRadius,
            string left, string right, float handRadius, bool othersGrab, bool localContacts)
        {
            var home = Home(root, spawn);
            var basis = Child(root, GrabBase); basis.localPosition = home.localPosition - GripRise;
            var bone = Child(basis, "Bone");
            var end = Child(bone, "Grip"); end.localPosition = GripRise;
            var carried = Child(root, body); carried.localPosition = home.localPosition;

            // Only the last part of this short chain is grabbable. A separate constraint carries the visible prop,
            // so stretching the grab chain never stretches its model.
            var phys = GrabChain(bone, grabRadius, grab, othersGrab);

            var baseConstraint = basis.gameObject.AddComponent<VRCParentConstraint>();
            baseConstraint.Sources.Add(new VRCConstraintSource(home, 1, -GripRise, Vector3.zero));
            baseConstraint.Sources.Add(new VRCConstraintSource(carried, 0, -GripRise, Vector3.zero));
            baseConstraint.IsActive = true; baseConstraint.Locked = true;
            baseConstraint.RebakeOffsetsWhenUnfrozen = false;
            // Sources 1 and 2 are the left and right wrists, set by Bind on the avatar. The prop is held as it was
            // grabbed: it is world-frozen until then, and unfreezing measures again where it sits from each source.
            var follow = carried.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(end, 1));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.IsActive = true; follow.Locked = true; follow.RebakeOffsetsWhenUnfrozen = true;

            HandContact(carried, "Left hand", left, true, handRadius, localContacts);
            HandContact(carried, "Right hand", right, false, handRadius, localContacts);
            return new Parts { Home = home, Basis = basis, Grip = end, Body = carried, Grab = phys, Follow = follow };
        }

        /// <summary>
        /// A PhysBone chain on <paramref name="bone"/> whose tip (its child Grip) can be grabbed and pulled anywhere; it
        /// moves nothing by itself and reports the grab in <paramref name="parameter"/>.
        /// </summary>
        internal static VRCPhysBone GrabChain(Transform bone, float radius, string parameter, bool othersGrab)
        {
            var phys = bone.gameObject.AddComponent<VRCPhysBone>();
            phys.rootTransform = bone; phys.endpointPosition = new Vector3(0, .001f, 0);
            phys.pull = 1; phys.spring = 0; phys.immobile = 1;
            phys.radius = radius; phys.radiusCurve = AnimationCurve.Constant(0, .49f, 0);
            phys.radiusCurve.AddKey(new Keyframe(.5f, 1)); phys.radiusCurve.AddKey(new Keyframe(1, 1));
            phys.maxStretch = 1000; phys.maxSquish = 1; phys.grabMovement = 1;
            phys.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            phys.allowGrabbing = othersGrab ? VRCPhysBoneBase.AdvancedBool.True : VRCPhysBoneBase.AdvancedBool.Other;
            if (!othersGrab) phys.grabFilter = new VRCPhysBoneBase.PermissionFilter { allowSelf = true, allowOthers = false, contentTypes = DynamicsUsageFlags.Everything };
            phys.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            phys.snapToHand = false; phys.resetWhenDisabled = true; phys.parameter = parameter;
            return phys;
        }

        /// <summary>
        /// Fits a placed prop to <paramref name="avatar"/>: it waits at <paramref name="fromHead"/> (in the avatar's
        /// facing) and follows either wrist, from wherever it was grabbed. Recorded with Undo; binding again (another
        /// avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        internal static void Bind(Transform root, string body, Transform avatar, Vector3 fromHead, string prop)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var animator = Humanoid(avatar, prop);
            var home = root.Find(Spawn); var basis = root.Find(GrabBase); var carried = root.Find(body);
            var follow = carried != null ? carried.GetComponent<VRCParentConstraint>() : null;
            if (home == null || basis == null || follow == null || follow.Sources.Count < 3) throw new InvalidOperationException("This " + prop + " was modified: add it again.");
            Undo.RecordObjects(new Object[] { home, basis, carried, follow }, "Fit " + prop);
            FollowHead(home, animator, avatar, fromHead, prop);
            basis.localPosition = home.localPosition - GripRise;
            carried.localPosition = home.localPosition;
            Wrists(follow, animator);
        }

        /// <summary>
        /// Where a prop waits while it is off: <see cref="FollowHead"/> keeps it in front of the face, so turning it on
        /// puts it in view however the owner stands, sits or looks.
        /// </summary>
        internal static Transform Home(Transform root, Vector3 spawn)
        {
            var home = Child(root, Spawn); home.localPosition = spawn;
            var follow = home.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(null, 1));
            follow.IsActive = true; follow.Locked = true;
            return home;
        }

        internal static Animator Humanoid(Transform avatar, string prop)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) throw new InvalidOperationException("The " + prop + " needs a humanoid avatar.");
            return animator;
        }

        /// <summary>
        /// Ties <paramref name="home"/> (made by <see cref="Home"/>) to the head bone, <paramref name="offset"/> away in the
        /// avatar's facing and turned like the avatar as it stands now. The caller records Undo.
        /// </summary>
        internal static void FollowHead(Transform home, Animator animator, Transform avatar, Vector3 offset, string prop)
        {
            var follow = home.GetComponent<VRCParentConstraint>();
            if (follow == null || follow.Sources.Count < 1) throw new InvalidOperationException("This " + prop + " was modified: add it again.");
            var head = animator.GetBoneTransform(HumanBodyBones.Head) ?? animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Hips);
            Undo.RecordObject(follow, "Fit " + prop);
            home.SetPositionAndRotation(head.position + avatar.rotation * offset, avatar.rotation);
            // Offsets are in the head's axes, unscaled: whatever way the rig's head bone points or is scaled.
            var turn = Quaternion.Inverse(head.rotation);
            follow.Sources[0] = new VRCConstraintSource(head, 1, turn * (home.position - head.position), (turn * home.rotation).eulerAngles);
            EditorUtility.SetDirty(follow);
        }

        /// <summary>Sources 1 and 2 of <paramref name="follow"/> become the left and right wrists (source 0 stays).</summary>
        internal static void Wrists(VRCParentConstraint follow, Animator animator)
        {
            follow.Sources[1] = new VRCConstraintSource(animator.GetBoneTransform(HumanBodyBones.LeftHand), 0);
            follow.Sources[2] = new VRCConstraintSource(animator.GetBoneTransform(HumanBodyBones.RightHand), 0);
            EditorUtility.SetDirty(follow);
        }

        // ---- Controller pieces ----

        /// <summary>
        /// Where the prop is in one state: shown or not, which wrists carry <paramref name="body"/> (neither: the grab
        /// chain's grip), what is world-frozen, and whether the grab chain can pick it up.
        /// </summary>
        internal static AnimationClip PropClip(string body, string model, bool visible, bool baseFrozen, bool bodyFrozen, bool atHome,
            bool left, bool right, bool grabbable, bool resetGrip = false)
        {
            var clip = new AnimationClip();
            // A hold leaves the offsets measured where it was grabbed; back home, the prop sits on the grip again.
            if (resetGrip)
                foreach (string offset in new[] { "ParentPositionOffset", "ParentRotationOffset" })
                foreach (string axis in new[] { "x", "y", "z" })
                    Curve(clip, body, typeof(VRCParentConstraint), "Sources.source0." + offset + "." + axis, 0);
            Curve(clip, model, typeof(GameObject), "m_IsActive", visible ? 1 : 0);
            Curve(clip, GrabBone, typeof(VRCPhysBone), "m_Enabled", grabbable ? 1 : 0);
            Curve(clip, GrabBase, typeof(VRCParentConstraint), "FreezeToWorld", baseFrozen ? 1 : 0);
            Curve(clip, GrabBase, typeof(VRCParentConstraint), "Sources.source0.Weight", atHome ? 1 : 0);
            Curve(clip, GrabBase, typeof(VRCParentConstraint), "Sources.source1.Weight", atHome ? 0 : 1);
            Curve(clip, body, typeof(VRCParentConstraint), "FreezeToWorld", bodyFrozen ? 1 : 0);
            Curve(clip, body, typeof(VRCParentConstraint), "Sources.source0.Weight", left || right ? 0 : 1);
            Curve(clip, body, typeof(VRCParentConstraint), "Sources.source1.Weight", left ? 1 : 0);
            Curve(clip, body, typeof(VRCParentConstraint), "Sources.source2.Weight", right ? 1 : 0);
            return clip;
        }

        /// <summary>The owner's latch: entering <paramref name="state"/> sets which hands hold the prop, synced to everyone.</summary>
        internal static void Hold(AnimatorState state, string heldLeft, string heldRight, bool left, bool right) =>
            Drive(state, (heldLeft, left), (heldRight, right));

        /// <summary>Entering <paramref name="state"/> sets these bools, on the owner only.</summary>
        internal static void Drive(AnimatorState state, params (string name, bool value)[] values)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            foreach (var (name, value) in values) driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = name, value = value ? 1 : 0 });
        }

        /// <summary>
        /// The hand keeps hold: any gesture but Neutral (0) and Open (2), which is what a squeezed grip gives on every
        /// controller. Letting go is the first relaxed gesture, not a fully opened hand, so the release follows at once.
        /// </summary>
        internal static (string, AnimatorConditionMode, float)[] Gripping(string gesture) => new[]
            { (gesture, AnimatorConditionMode.NotEqual, 0f), (gesture, AnimatorConditionMode.NotEqual, 2f) };

        /// <summary>Leaves <paramref name="from"/> when <paramref name="gesture"/> relaxes or opens (and the other conditions hold).</summary>
        internal static void LetGo(AnimatorState from, AnimatorState to, string gesture, params (string name, AnimatorConditionMode mode, float value)[] also)
        {
            foreach (float relaxed in new[] { 0f, 2f })
                Transition(from, to, also.Append((gesture, AnimatorConditionMode.Equals, relaxed)).ToArray());
        }

        internal static AnimatorStateMachine Layer(AnimatorController controller, string name)
        {
            controller.AddLayer(name); var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1; controller.layers = layers;
            return layers[layers.Length - 1].stateMachine;
        }

        internal static AnimatorState State(AnimatorController controller, AnimatorStateMachine machine, string name, Motion motion)
        {
            if (string.IsNullOrEmpty(motion.name)) motion.name = name;
            Store(motion, controller);
            var state = machine.AddState(name); state.motion = motion; state.writeDefaultValues = false; return state;
        }

        // A blend tree and the clips it blends live in the controller asset.
        private static void Store(Motion motion, Object asset)
        {
            if (motion == null || AssetDatabase.Contains(motion)) return;
            AssetDatabase.AddObjectToAsset(motion, asset);
            if (motion is BlendTree tree) foreach (var child in tree.children) Store(child.motion, asset);
        }

        internal static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, params (string name, AnimatorConditionMode mode, float value)[] conditions)
        {
            var transition = from.AddTransition(to); transition.duration = 0; transition.hasExitTime = false;
            foreach (var condition in conditions) transition.AddCondition(condition.mode, condition.value, condition.name);
            return transition;
        }

        internal static void Curve(AnimationClip clip, string path, Type type, string property, float value) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, .2f, value));

        /// <summary>A clip setting the animator's own float <paramref name="parameter"/> (an animated animator parameter).</summary>
        internal static AnimationClip ParameterClip(string name, string parameter, float value)
        {
            var clip = new AnimationClip { name = name };
            Curve(clip, "", typeof(Animator), parameter, value);
            return clip;
        }

        internal static VRCExpressionParameters.Parameter Parameter(string name, bool synced = true) => new VRCExpressionParameters.Parameter
        { name = name, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = false, networkSynced = synced };

        /// <summary>
        /// Saves the prop's parameters and its menu (<paramref name="menuPath"/> opening <paramref name="controls"/>) next to
        /// the controller, and merges all three into the avatar through VRCFury.
        /// </summary>
        internal static void Install(GameObject root, string folder, AnimatorController controller, string menuPath, string subMenuName,
            List<VRCExpressionsMenu.Control> controls, VRCExpressionParameters.Parameter[] parameters)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            asset.parameters = parameters;
            AssetDatabase.CreateAsset(asset, folder + "/Parameters.asset");
            var sub = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            sub.controls = controls;
            AssetDatabase.CreateAsset(sub, folder + "/" + subMenuName + ".asset");
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control> { new VRCExpressionsMenu.Control { name = menuPath, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub } };
            AssetDatabase.CreateAsset(menu, folder + "/Menu.asset");
            VrcFury.Writer.FullController(root, controller, menu, asset);
        }

        internal static VRCExpressionsMenu.Control Control(string name, VRCExpressionsMenu.Control.ControlType type, string parameter) =>
            new VRCExpressionsMenu.Control { name = name, type = type, parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter }, value = 1 };

        // ---- Scene pieces ----

        internal static Transform Child(Transform parent, string name)
        { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }

        /// <summary>A receiver telling when the owner's left (or right) hand is within <paramref name="radius"/>.</summary>
        internal static void HandContact(Transform body, string name, string parameter, bool left, float radius, bool localOnly)
        {
            var receiver = Child(body, name).gameObject.AddComponent<VRCContactReceiver>();
            receiver.radius = radius; receiver.allowSelf = true; receiver.allowOthers = false;
            receiver.localOnly = localOnly; receiver.parameter = parameter;
            receiver.collisionTags.Add(left ? "HandL" : "HandR");
        }

        internal static Material Material(string folder, string name, Color color, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Missing shader: " + shaderName);
            var material = new Material(shader) { name = name };
            material.color = color; AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat"); return material;
        }

        internal static Transform Primitive(Transform parent, string name, PrimitiveType type, Vector3 scale, Vector3 position, Material material)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go.transform;
        }
    }
}
