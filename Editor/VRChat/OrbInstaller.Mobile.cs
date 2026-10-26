using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0 on Quest and other phones. VRChat shows only its mobile shaders there and removes lights and colliders, so every
    /// N4N0 carries a second N4N0 beside the PC one (<see cref="Mobile"/>, off in the editor): the same model (OrbInstaller.Model,
    /// in VRChat's mobile Standard Lite), its expressions set by <see cref="State"/> as the PC one's, its patterns tinted
    /// in the Light option's colour, beside its owner's head as the PC N4N0 is (without its floaty tether: a PhysBone costs a
    /// mobile performance rank) and touched as it is. Its owner sees it look at them; everyone else sees it look ahead. A build step (<see cref="OrbPlatformHook"/>) keeps
    /// the mobile N4N0 in Android and iOS builds and the PC one in PC builds, so one avatar uploads to both: the PC N4N0's
    /// parameters stay in both, in the same order (VRChat matches synced parameters between platforms by their place),
    /// and a mobile build adds one local parameter, <see cref="MobileVersionParameter"/>, which tells Orbiters - N4N0 which
    /// N4N0 it drives. No screen and no text on phones: no mobile shader can draw them.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>
        /// Local, not saved, set by no menu, in mobile builds only: the mobile N4N0's version (<see cref="MobileVersion"/>),
        /// which Orbiters - N4N0 reads over OSCQuery to know that it drives the mobile N4N0.
        /// </summary>
        public const string MobileVersionParameter = "N4N0/Mobile/Version";
        public const int MobileVersion = 1;
        internal const string Mobile = "Mobile", MobileHead = Mobile + "/Head", MobileOrb = MobileHead + "/Orb", MobileFloat = MobileOrb + "/Float";
        internal const string MobileFace = MobileFloat + "/Face";
        // The model's object, below Face.
        internal const string MobileBody = MobileFace + "/Body";
        // The anger the mobile State layer hands its Colour layer: an animated animator parameter, never an avatar one.
        internal const string MobileAnger = "N4N0/Mobile/Anger";

        /// <summary>Whether a placed orb has the mobile N4N0.</summary>
        public static bool HasMobile(Transform orb) => orb != null && orb.Find(MobileFace) != null;

        /// <summary>Whether the build target is a phone's (Android, iOS): builds then keep the mobile N4N0.</summary>
        internal static bool MobileTarget => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

        // Its owner's mobile N4N0 looks at their eyes, which Bind puts at -OrbFromEyes from it (in the avatar's turn).
        internal static Quaternion OwnerFacing => Quaternion.LookRotation(-OrbFromEyes, Vector3.up);

        // ---- The part ----

        /// <summary>
        /// The mobile N4N0 below <paramref name="root"/>, off until a mobile build turns it on, its files in
        /// <paramref name="folder"/>, and its controller and parameters in N4N0's Full Controller after the PC N4N0's.
        /// </summary>
        private static void AddMobile(Transform root, string folder, Mesh model, Material material)
        {
            var part = Child(root, Mobile);
            // On the head bone, turned like the avatar (sources set by Bind), as the PC N4N0's head.
            var head = Child(part, "Head");
            head.localPosition = DefaultHead;
            var follow = head.gameObject.AddComponent<VRCPositionConstraint>();
            follow.Sources.Add(new VRCConstraintSource(null, 1));
            follow.IsActive = true; follow.Locked = true;
            var level = head.gameObject.AddComponent<VRCRotationConstraint>();
            level.Sources.Add(new VRCConstraintSource(null, 1));
            level.IsActive = true; level.Locked = true;
            // Where the PC N4N0 rests: the animator bobs and hops Float, turns Face to its owner and sets the model's
            // expressions (Body, the PC N4N0's model and material).
            var orb = Child(head, "Orb");
            orb.localPosition = DefaultEyes + OrbFromEyes;
            TouchReceiver(Child(orb, "Touch"));
            var face = Child(Child(orb, "Float"), "Face");
            AddModel(Child(face, "Body"), model, material);
            part.gameObject.SetActive(false);
            var (controller, parameters) = MobileController(folder);
            AddMobileEntries(root, controller, parameters);
        }

        // The owner's hands touching N4N0 (N4N0/Touch, the owner's own and only on the owner's client).
        private static void TouchReceiver(Transform at)
        {
            var touch = at.gameObject.AddComponent<VRCContactReceiver>();
            touch.shapeType = ContactBase.ShapeType.Sphere; touch.radius = TouchRadius;
            touch.allowSelf = true; touch.allowOthers = false; touch.localOnly = true;
            touch.receiverType = ContactReceiver.ReceiverType.Constant; touch.parameter = Touch;
            touch.collisionTags.Add("Hand"); touch.collisionTags.Add("Finger");
        }

        // The mobile controller and parameters after the PC N4N0's in its Full Controller (a mobile build drops the PC
        // controller, a PC build both mobile entries), the version parameter keeping its name.
        private static void AddMobileEntries(Transform root, AnimatorController controller, VRCExpressionParameters parameters)
        {
            if (FullController(root, out var component) == null) throw new InvalidOperationException("N4N0 has no VRCFury Full Controller.");
            var serialized = new SerializedObject(component);
            void Append(string list, string field, Object asset)
            {
                var entries = serialized.FindProperty(list);
                // The new entry copies the last one (an FX controller), then takes its own asset.
                entries.arraySize++;
                Reference(entries.GetArrayElementAtIndex(entries.arraySize - 1).FindPropertyRelative(field), asset);
            }
            Append("content.controllers", "controller", controller);
            Append("content.prms", "parameters", parameters);
            var globals = serialized.FindProperty("content.globalParams");
            globals.arraySize++;
            globals.GetArrayElementAtIndex(globals.arraySize - 1).stringValue = MobileVersionParameter;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Fits a placed orb's mobile N4N0 as <see cref="Bind"/> fits the PC one: on the head bone, turned like the avatar,
        /// as large as the avatar's eyes are high, N4N0 beside the eyes. Nothing to fit on an N4N0 without one.
        /// </summary>
        private static void BindMobile(Transform orb, Transform avatar, Transform bone, Vector3 eyes, float size)
        {
            var head = orb.Find(MobileHead);
            var at = orb.Find(MobileOrb);
            if (head == null || at == null) return;
            var follow = head.GetComponent<VRCPositionConstraint>();
            var level = head.GetComponent<VRCRotationConstraint>();
            if (follow == null || level == null || follow.Sources.Count < 1 || level.Sources.Count < 1) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            Undo.RecordObjects(new Object[] { head, follow, level, at }, "Fit orb");
            head.SetPositionAndRotation(bone.position, avatar.rotation);
            float parent = head.parent != null ? Mathf.Abs(head.parent.lossyScale.x) : 1;
            head.localScale = Vector3.one * (size / Mathf.Max(parent, 1e-4f));
            follow.Sources[0] = new VRCConstraintSource(bone, 1, Vector3.zero, Vector3.zero);
            level.Sources[0] = new VRCConstraintSource(avatar, 1, Vector3.zero, Vector3.zero);
            at.localPosition = head.InverseTransformPoint(eyes) + OrbFromEyes;
            at.localRotation = Quaternion.identity;
            EditorUtility.SetDirty(follow); EditorUtility.SetDirty(level);
        }

        // ---- The builds ----

        /// <summary>
        /// Keeps the part of every N4N0 under <paramref name="avatarRoot"/> (a build copy) that the platform shows: on a
        /// phone (<paramref name="mobile"/>) the mobile N4N0, turned on, and the mobile controller, the PC N4N0 and its
        /// controller gone; on PC the PC N4N0 alone, as before the mobile one. Both keep the PC N4N0's parameters and menu,
        /// in the same order; the mobile build's own parameter keeps its name. An N4N0 without the mobile one is left out
        /// of mobile builds. Only build copies change: an N4N0 in a prefab or a prefab instance is left as it is.
        /// </summary>
        internal static void KeepPlatformPart(GameObject avatarRoot, bool mobile)
        {
            foreach (var orb in FindAll(avatarRoot).ToList())
            {
                if (PrefabUtility.IsPartOfAnyPrefab(orb)) continue;
                var drop = orb.Find(mobile ? Shown : Mobile);
                if (drop != null) Object.DestroyImmediate(drop.gameObject);
                // Phones have neither avatar cameras nor custom shaders: no capture kit (OrbInstaller.Capture).
                if (mobile) DropCapture(orb);
                var keep = mobile ? orb.Find(Mobile) : null;
                if (keep != null) keep.gameObject.SetActive(true);
                KeepPlatformEntries(orb, mobile);
            }
        }

        // The Full Controller's entries for the platform: a mobile build loses the PC controller (it animates nothing there),
        // a PC build the mobile controller and parameters.
        private static void KeepPlatformEntries(Transform orb, bool mobile)
        {
            if (VrcFury.Component == null) return;
            foreach (var component in orb.GetComponents(VrcFury.Component))
            {
                var serialized = new SerializedObject(component);
                var controllers = serialized.FindProperty("content.controllers");
                var prms = serialized.FindProperty("content.prms");
                if (controllers == null || !controllers.isArray || prms == null || !prms.isArray) continue;
                for (int i = controllers.arraySize - 1; i >= 0; i--)
                {
                    if (!(controllers.GetArrayElementAtIndex(i).FindPropertyRelative("controller.objRef")?.objectReferenceValue is AnimatorController controller)) continue;
                    bool pc = controller.parameters.Any(p => p.name == TextPointer), phone = controller.parameters.Any(p => p.name == MobileAnger);
                    if (mobile ? pc : phone) controllers.DeleteArrayElementAtIndex(i);
                }
                bool version = false;
                for (int i = prms.arraySize - 1; i >= 0; i--)
                {
                    var asset = prms.GetArrayElementAtIndex(i).FindPropertyRelative("parameters.objRef")?.objectReferenceValue as VRCExpressionParameters;
                    if (asset == null || asset.parameters == null || !asset.parameters.Any(p => p.name == MobileVersionParameter)) continue;
                    if (mobile) version = true;
                    else prms.DeleteArrayElementAtIndex(i);
                }
                // N4N0's options give the Full Controller the PC parameters' names as its global ones.
                var globals = serialized.FindProperty("content.globalParams");
                if (version && globals != null && globals.isArray &&
                    !Enumerable.Range(0, globals.arraySize).Any(i => globals.GetArrayElementAtIndex(i).stringValue == MobileVersionParameter))
                {
                    globals.arraySize++;
                    globals.GetArrayElementAtIndex(globals.arraySize - 1).stringValue = MobileVersionParameter;
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ---- The animator ----

        /// <summary>
        /// The mobile controller (paths below N4N0, as the PC one's) and its parameters: shown with N4N0/Visible; the model's
        /// expression per N4N0/State, as the PC N4N0's (<see cref="ModelStates"/>); its patterns' tint
        /// (<see cref="TintLayer"/>); the face turned to its owner on its owner's client (<see cref="FacingLayer"/>).
        /// </summary>
        private static (AnimatorController controller, VRCExpressionParameters parameters) MobileController(string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/N4N0 mobile.controller");
            controller.AddParameter(new AnimatorControllerParameter { name = Visible, type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter(State, AnimatorControllerParameterType.Int);
            // Written by the touch receiver: no PC controller declares it in a mobile build.
            controller.AddParameter(Touch, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Light, AnimatorControllerParameterType.Bool);
            controller.AddParameter(LightColor, AnimatorControllerParameterType.Int);
            controller.AddParameter(new AnimatorControllerParameter { name = LightLevel, type = AnimatorControllerParameterType.Float, defaultFloat = LightLevelDefault });
            // VRChat's.
            controller.AddParameter("Voice", AnimatorControllerParameterType.Float);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            controller.AddParameter(MobileAnger, AnimatorControllerParameterType.Float);

            Toggled(controller, controller.layers[0].stateMachine, Visible, MobileHead);
            ModelStates(controller, "Mobile state", MobileBody, MobileFloat, MobileAnger);
            TintLayer(controller, "Mobile colour", MobileBody, MobileAnger);
            FacingLayer(controller, "Mobile face", MobileFace);

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[] { Parameter(MobileVersionParameter, synced: false, type: VRCExpressionParameters.ValueType.Int, defaultValue: MobileVersion) };
            AssetDatabase.CreateAsset(parameters, folder + "/N4N0 mobile parameters.asset");
            return (controller, parameters);
        }
    }

    /// <summary>
    /// Keeps each N4N0's part for the platform being built (<see cref="OrbInstaller.KeepPlatformPart"/>): the mobile N4N0
    /// for Android and iOS, the PC one for PC.
    /// </summary>
    internal sealed class OrbPlatformHook : IVRCSDKPreprocessAvatarCallback
    {
        // Before the Toolkit's refit and attachment steps (-10110 to -10100) and VRCFury (-10000): they see one N4N0.
        public int callbackOrder => -10200;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            // N4N0's version for the avatar manifest, while its gallery folder is still known.
            OrbInstaller.NoteBuiltVersion(avatarRoot);
            OrbInstaller.KeepPlatformPart(avatarRoot, OrbInstaller.MobileTarget);
            return true;
        }
    }
}
