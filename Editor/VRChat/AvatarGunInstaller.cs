using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Build;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar gun (Orbiters contracts, Docs/avatar-gun.md): a toy ray gun its wearer points at another player; when the
    /// trigger is pulled, that player's own Orbiters - N4N0 changes their avatar to the public avatar the gun is loaded with.
    /// Native avatar components only. <see cref="Out"/> puts it in the right hand (or the left, the card's Hand option),
    /// fitted to the palm with the barrel along the hand, or, in desktop mode, before the eyes where its owner looks. The
    /// owner's fist (or Orbiters - N4N0's <see cref="Pull"/>) fires a loaded gun: the animator raises <see cref="Fire"/>
    /// (synced) for <see cref="FireSeconds"/>, then keeps it off for <see cref="RearmSeconds"/> at least; an unloaded gun
    /// only clicks. Every copy of the avatar, the hit player's above all, follows the synced Fire: its VRChat raycast from
    /// the muzzle carries a contact sender (<see cref="ContactTag"/>) where it ends and a second one covers the muzzle's
    /// first <see cref="CloseRange"/>, both on during a shot, and the beam reaches the ray's end. Nothing on that path
    /// waits for IsLocal. The prefab carries a receiver too (<see cref="AddReceiver"/>, N4N0's prefab one as well), which
    /// raises <see cref="Hit"/> on its owner's own client. Its shader and pictures are copied into its folder and it
    /// carries no Orbiters script: Toolkit releases ship without .meta files, so a gallery copy referring to one would
    /// arrive with a missing script.
    /// </summary>
    public static partial class AvatarGunInstaller
    {
        public const string MenuPath = "Avatar gun";
        public const string PrefabName = "Avatar gun.prefab";

        // The contract's names and times (Orbiters contracts, Docs/avatar-gun.md; the Toolkit does not reference the
        // contracts' C# twin, CSharp/AvatarGun.cs). Global in the gun's Full Controller, so they keep these names in the
        // avatar for Orbiters - N4N0 over OSC. None is saved.
        /// <summary>Synced: the gun is in the hand (the menu, Orbiters - N4N0).</summary>
        public const string Out = "AvatarGun/Out";
        /// <summary>Synced, set by Orbiters - N4N0: an avatar is chosen and shots can be reported.</summary>
        public const string Loaded = "AvatarGun/Loaded";
        /// <summary>Synced, set by the gun's animator: a shot leaves.</summary>
        public const string Fire = "AvatarGun/Fire";
        /// <summary>Local, set by Orbiters - N4N0: the trigger for players without a hand gesture (desktop mode).</summary>
        public const string Pull = "AvatarGun/Pull";
        /// <summary>Local, the menu's button: Orbiters - N4N0 opens its avatar picker.</summary>
        public const string Choose = "AvatarGun/Choose";
        /// <summary>Local, set by the receiver: another player's gun sender touches it.</summary>
        public const string Hit = "AvatarGun/Hit";
        /// <summary>The gun's senders' and the receiver's contact tag.</summary>
        public const string ContactTag = "OrbitersAvatarGun";
        /// <summary>FIRE_MS and REARM_MS, in seconds.</summary>
        public const float FireSeconds = .25f, RearmSeconds = .75f;
        /// <summary>RANGE_M, SENDER_RADIUS_M and CLOSE_RANGE_M, in metres.</summary>
        public const float Range = 20, SenderRadius = .05f, CloseRange = 1.5f;

        // The prop's objects. Gun follows the right palm, the left one or the desktop anchor (its constraint's sources, in
        // this order); its model is scaled to the hand, the muzzle never (its contacts keep the contract's sizes).
        internal const string Gun = "Gun", Recoil = Gun + "/Recoil", Model = Recoil + "/Model", Muzzle = Gun + "/Muzzle", RayEnd = Muzzle + "/Hit", Close = Muzzle + "/Close range";
        internal const string Beam = Gun + "/Beam", Desktop = "Desktop";
        internal const int InRightHand = 0, InLeftHand = 1, AtDesktop = 2, HoldSources = 3;
        /// <summary>The receiver's object, below the gun's root and N4N0's (the same name: a build keeps one).</summary>
        public const string ReceiverName = "Avatar gun receiver";
        // The trigger's pressure (the fist's gesture weight) that fires, and the release it waits for before the next shot.
        internal const float TriggerOn = .5f, TriggerOff = .4f;
        // A hand as long as this (wrist to middle knuckle) holds the gun at its modelled size.
        internal const float ReferenceHand = .09f;
        // In desktop mode the gun hangs low and to the right before the eyes (right, up, forward at 1.6 m eye height).
        internal static readonly Vector3 DesktopFromEyes = new Vector3(.13f, -.15f, .3f);
        // Until Bind: the right palm at the height of a hand at rest beside a 1.6 m avatar.
        private static readonly Vector3 DefaultHold = new Vector3(.3f, 1, .25f);

        /// <summary>The guns below <paramref name="root"/>, known by the sender at their ray's end.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCContactSender>(true).Where(s => s.collisionTags.Contains(ContactTag)).Select(s => RootOf(s.transform))
                .Where(g => g != null).Distinct().ToList();

        // The sender is RayEnd, three levels below the gun's root.
        private static Transform RootOf(Transform sender)
        {
            var root = sender.parent != null && sender.parent.parent != null ? sender.parent.parent.parent : null;
            return root != null && root.Find(RayEnd) == sender ? root : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterHooks()
        {
            AttachmentHooks.Register((root, avatar) =>
            {
                foreach (var gun in FindAll(root)) Bind(gun, avatar);
            }, root => FindAll(root).Any());
            AttachmentHooks.RegisterOptions(root => FindAll(root).Select(gun =>
            {
                var target = gun;
                return new AttachmentOption
                {
                    Label = "Hand",
                    Tooltip = "The hand that holds the avatar gun in VR; its fist pulls the trigger. In desktop mode it is always before your eyes. No synced bits.",
                    Choices = new[] { "Right", "Left" },
                    ChoiceTooltips = new[] { "In the right hand, fired with the right fist", "In the left hand, fired with the left fist" },
                    Current = HandOf(gun) switch { true => 1, false => 0, _ => -1 },
                    Choose = index => SetHand(target, index == 1),
                };
            }).ToList());
        }

        /// <summary>Creates the avatar gun's prefab with its controller, menu, parameters, shader, meshes and materials in <paramref name="folder"/>.</summary>
        public static string CreatePrefab(string folder) =>
            SavePrefab(folder, PrefabName, "Avatar gun", "avatar gun", root => Build(root, folder));

        private static void Build(GameObject root, string folder)
        {
            // Where the gun is held: a palm, or the desktop anchor before the eyes (Bind sets all three).
            var gun = Child(root.transform, Gun);
            gun.localPosition = DefaultHold;
            var hold = gun.gameObject.AddComponent<VRCParentConstraint>();
            for (int i = 0; i < HoldSources; i++) hold.Sources.Add(new VRCConstraintSource(null, i == InRightHand ? 1 : 0));
            hold.IsActive = true; hold.Locked = true;
            var desktop = Home(root.transform, DefaultHold);
            desktop.name = Desktop;

            // The model, kicked back by a shot (Recoil) and twitched by an empty click (the model itself).
            var model = Child(Child(gun, "Recoil"), "Model");
            var renderer = model.gameObject.AddComponent<MeshRenderer>();
            model.gameObject.AddComponent<MeshFilter>().sharedMesh = ModelMesh(folder);
            renderer.sharedMaterials = new[] { BodyMaterial(folder), EmitterMaterials(folder).dim };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            // The ray, its end's sender and the close one: unit scale whatever the hand's, so the contract's sizes hold.
            var muzzle = Child(gun, "Muzzle");
            muzzle.localPosition = MuzzlePoint;
            var end = Child(muzzle, "Hit");
            end.localPosition = Vector3.forward * Range;
            var ray = muzzle.gameObject.AddComponent<VRCRaycast>();
            // Version 1.1 also meets remote players' colliders (VRChat makes them triggers) and the local player's.
            ray.Version = VRCRaycast.RaycastComponentVersion.Version_1_1;
            ray.RaycastDirection = Vector3.forward; ray.Distance = Range; ray.ApplyTransformScale = false;
            ray.RaycastCollisionMode = VRCRaycast.CollisionMode.HitWorldsAndPlayers;
            ray.ResultTransform = end; ray.ApplyRotation = false;
            // A miss leaves the end at full range: the beam shows the whole ray, and its sender meets nobody there.
            ray.BehaviorOnMiss = VRCRaycast.MissBehavior.SnapToEnd;
            var sender = end.gameObject.AddComponent<VRCContactSender>();
            sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = SenderRadius;
            Shot(sender);
            var close = Child(muzzle, "Close range");
            // A capsule along the muzzle's first CloseRange (contacts' capsules lie along their Y axis).
            close.localRotation = Quaternion.Euler(90, 0, 0);
            var near = close.gameObject.AddComponent<VRCContactSender>();
            near.shapeType = ContactBase.ShapeType.Capsule; near.radius = SenderRadius; near.height = CloseRange;
            near.position = Vector3.up * (CloseRange / 2);
            Shot(near);

            AddBeam(gun, muzzle, end, folder);
            AddReceiver(root.transform);
            BuildController(root, folder);
            gun.gameObject.SetActive(false);
        }

        // A shot's sender: seen by everyone (other players' receivers), off until the animator fires.
        private static void Shot(VRCContactSender sender)
        {
            sender.collisionTags.Add(ContactTag);
            LocalOnlySender(sender, false);
            sender.enabled = false;
        }

        /// <summary>
        /// Fits a placed gun to <paramref name="avatar"/>: held in either palm with the barrel along the hand (its model as
        /// large as the hand), before the eyes in desktop mode, and its receiver around the body. Recorded with Undo;
        /// binding again (another avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform gun, Transform avatar)
        {
            if (gun == null) throw new ArgumentNullException(nameof(gun));
            var animator = Humanoid(avatar, "avatar gun");
            var held = gun.Find(Gun);
            var model = gun.Find(Model);
            var muzzle = gun.Find(Muzzle);
            var desktop = gun.Find(Desktop);
            var hold = held != null && held.TryGetComponent(out VRCParentConstraint h) ? h : null;
            if (hold == null || hold.Sources.Count < HoldSources || model == null || muzzle == null || desktop == null)
                throw new InvalidOperationException("This avatar gun was modified: add it again.");
            var right = HandFrame(animator, avatar, false);
            var left = HandFrame(animator, avatar, true);
            Undo.RecordObjects(new Object[] { held, hold, model, muzzle }, "Fit avatar gun");
            held.SetPositionAndRotation(right.position, right.rotation);
            hold.Sources[InRightHand] = Source(right.bone, right.position, right.rotation, 1);
            hold.Sources[InLeftHand] = Source(left.bone, left.position, left.rotation, 0);
            hold.Sources[AtDesktop] = new VRCConstraintSource(desktop, 0, Vector3.zero, Vector3.zero);
            // Sized by the right hand (both are, near enough).
            model.localScale = Vector3.one * right.scale;
            muzzle.localPosition = MuzzlePoint * right.scale;
            muzzle.localRotation = Quaternion.identity;
            EditorUtility.SetDirty(hold);

            var head = animator.GetBoneTransform(HumanBodyBones.Head) ?? animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Hips);
            var eyes = N4N0Installer.Eyes(avatar, head);
            float size = BodySize(avatar, eyes);
            Undo.RecordObject(desktop, "Fit avatar gun");
            FollowHead(desktop, animator, avatar, Quaternion.Inverse(avatar.rotation) * (eyes - head.position) + DesktopFromEyes * size, "avatar gun");
            BindReceiver(gun, avatar);
        }

        // A constraint source holding the gun at (position, rotation) from `bone`: offsets in the bone's axes, unscaled.
        private static VRCConstraintSource Source(Transform bone, Vector3 position, Quaternion rotation, float weight)
        {
            var turn = Quaternion.Inverse(bone.rotation);
            return new VRCConstraintSource(bone, weight, turn * (position - bone.position), (turn * rotation).eulerAngles);
        }

        /// <summary>
        /// Where the gun sits in a hand as the avatar stands now: its grip in the fist (a little past the middle of the palm,
        /// towards its inside), its barrel along the hand (wrist to middle knuckle), its top on the thumb's side (little
        /// finger to index knuckles), so that the hand aims it as it points; and how large the hand is.
        /// </summary>
        internal static (Transform bone, Vector3 position, Quaternion rotation, float scale) HandFrame(Animator animator, Transform avatar, bool left)
        {
            var (hand, palm) = Palm(animator, left, "avatar gun");
            Transform Bone(HumanBodyBones b) => animator.GetBoneTransform(b);
            var middle = Bone(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal) ??
                         Bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal) ??
                         Bone(left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal);
            var forearm = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            Vector3 along = middle != null && (middle.position - hand.position).sqrMagnitude > 1e-6f ? middle.position - hand.position
                : forearm != null ? (hand.position - forearm.position) * .35f : avatar.forward * ReferenceHand;
            float length = along.magnitude;
            var forward = along / Mathf.Max(length, 1e-6f);
            var index = Bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = Bone(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal) ??
                         Bone(left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal);
            // Without those fingers, a hand at rest in a T or A pose: palm down, the thumb ahead.
            var top = index != null && little != null && (index.position - little.position).sqrMagnitude > 1e-6f ? index.position - little.position : avatar.forward;
            top = Vector3.ProjectOnPlane(top, forward);
            if (top.sqrMagnitude < 1e-8f) top = Vector3.ProjectOnPlane(avatar.up, forward);
            var rotation = Quaternion.LookRotation(forward, top.normalized);
            float scale = Mathf.Clamp(length / ReferenceHand, .4f, 3);
            // The inside of the palm is the gun's left for the right hand, its right for the left.
            var grip = palm + rotation * new Vector3(left ? GripInside : -GripInside, -GripDown, GripAhead) * scale;
            return (hand, grip, rotation, scale);
        }

        /// <summary>How large the avatar is: its eye height over 1.6 m, as N4N0's.</summary>
        internal static float BodySize(Transform avatar, Vector3 eyes) =>
            Mathf.Clamp(Vector3.Dot(eyes - avatar.position, avatar.up) / N4N0Installer.ReferenceEyes, .3f, 3);

        // ---- The receiver (the gun's, N4N0's) ----

        // Around the whole body, for an avatar whose eyes are 1.6 m up: VRChat's player capsule (about 0.2 m wide and as tall
        // as the avatar) inside it with room to spare, so the ray's end on that capsule lies in it.
        internal const float ReceiverRadius = .45f, ReceiverHeight = 2.3f, ReceiverMiddle = .95f;

        /// <summary>
        /// The receiver below <paramref name="root"/> (Docs/avatar-gun.md): a capsule around the whole body, local only,
        /// touched by other players' gun senders and never by its own avatar's, raising <see cref="Hit"/> while one touches
        /// it. It costs no synced bit; its owner's controller declares <see cref="Hit"/> (an unsynced expression parameter)
        /// for Orbiters - N4N0 to read over OSC. <see cref="BindReceiver"/> fits it to the avatar; an avatar with the gun
        /// and N4N0 keeps one (<see cref="KeepOneReceiver"/>).
        /// </summary>
        public static VRCContactReceiver AddReceiver(Transform root)
        {
            var at = Child(root, ReceiverName);
            at.localPosition = Vector3.up * ReceiverMiddle;
            var receiver = at.gameObject.AddComponent<VRCContactReceiver>();
            receiver.shapeType = ContactBase.ShapeType.Capsule;
            receiver.radius = ReceiverRadius; receiver.height = ReceiverHeight;
            receiver.localOnly = true; receiver.allowSelf = false; receiver.allowOthers = true;
            receiver.receiverType = ContactReceiver.ReceiverType.Constant;
            receiver.parameter = Hit;
            receiver.collisionTags.Add(ContactTag);
            return receiver;
        }

        /// <summary>
        /// Fits the receiver below <paramref name="root"/> (if it has one) to <paramref name="avatar"/>: upright around its
        /// body from the feet up, as large as its eye height says. Recorded with Undo.
        /// </summary>
        public static void BindReceiver(Transform root, Transform avatar)
        {
            var at = root != null ? root.Find(ReceiverName) : null;
            var receiver = at != null && at.TryGetComponent(out VRCContactReceiver r) ? r : null;
            if (receiver == null || avatar == null) return;
            var animator = avatar.GetComponent<Animator>();
            var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            var eyes = head != null ? N4N0Installer.Eyes(avatar, head)
                : avatar.TryGetComponent(out VRCAvatarDescriptor descriptor) ? avatar.TransformPoint(descriptor.ViewPosition) : avatar.position + avatar.up * N4N0Installer.ReferenceEyes;
            float size = BodySize(avatar, eyes);
            Undo.RecordObjects(new Object[] { at, receiver }, "Fit avatar gun receiver");
            at.SetPositionAndRotation(avatar.position + avatar.up * (ReceiverMiddle * size), avatar.rotation);
            // Its sizes in its own space (contacts scale with their object).
            float scale = Mathf.Max(Mathf.Abs(at.lossyScale.x), 1e-4f);
            receiver.radius = ReceiverRadius * size / scale;
            receiver.height = ReceiverHeight * size / scale;
            receiver.position = Vector3.zero; receiver.rotation = Quaternion.identity;
            EditorUtility.SetDirty(receiver);
        }

        /// <summary>
        /// Leaves one receiver on <paramref name="avatar"/> (a build copy): the gun's and N4N0's would only both report the
        /// same touches. The first shown one stays.
        /// </summary>
        internal static void KeepOneReceiver(GameObject avatar)
        {
            if (avatar == null) return;
            var all = avatar.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => r.parameter == Hit && r.collisionTags.Contains(ContactTag)).ToList();
            if (all.Count < 2) return;
            var keep = all.FirstOrDefault(r => r.gameObject.activeInHierarchy) ?? all[0];
            foreach (var receiver in all.Where(r => r != keep)) Object.DestroyImmediate(receiver);
        }

        // ---- The hand option ----

        /// <summary>Whether a placed gun is held in the left hand (its Full Controller's), null when it can't be read.</summary>
        internal static bool? HandOf(Transform gun)
        {
            var controller = FullController(gun, out _, out _);
            return controller == null ? (bool?)null : controller.parameters.Any(p => p.name == "GestureLeftWeight");
        }

        /// <summary>
        /// Holds a placed gun in the left hand (<paramref name="left"/>) or the right one: its Full Controller takes that
        /// hand's controller, made once in <see cref="OptionsFolder"/> (the gun's own files, a gallery's among them, are left
        /// as they are). On a gun placed from a prefab, the Full Controller is replaced by a copy of its own first, as
        /// N4N0's options do: VRCFury reverts overrides of its components at upload. Recorded with Undo.
        /// </summary>
        public static void SetHand(Transform gun, bool left)
        {
            var current = FullController(gun, out var component, out int index) ?? throw new InvalidOperationException("This avatar gun has no VRCFury Full Controller: add it again.");
            if (current.parameters.Any(p => p.name == "GestureLeftWeight") == left) return;
            var controller = SharedController(left);
            component = VrcFury.OwnComponent(component);
            var serialized = new SerializedObject(component);
            VrcFury.Reference(serialized.FindProperty("content.controllers").GetArrayElementAtIndex(index).FindPropertyRelative("controller"), controller);
            serialized.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Avatar gun hand");
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        // The gun's controller in its Full Controller (the one that raises Fire), the component and its entry.
        private static UnityEditor.Animations.AnimatorController FullController(Transform gun, out Component component, out int index)
        {
            component = null; index = -1;
            if (gun == null || VrcFury.Component == null) return null;
            foreach (var candidate in gun.GetComponents(VrcFury.Component))
            {
                var controllers = new SerializedObject(candidate).FindProperty("content.controllers");
                if (controllers == null || !controllers.isArray) continue;
                for (int c = 0; c < controllers.arraySize; c++)
                    if (controllers.GetArrayElementAtIndex(c).FindPropertyRelative("controller.objRef")?.objectReferenceValue is UnityEditor.Animations.AnimatorController controller &&
                        controller.parameters.Any(p => p.name == Fire))
                    {
                        component = candidate; index = c;
                        return controller;
                    }
            }
            return null;
        }

        // Each hand's controller, shared by every gun switched to it (it names no object of its own: its curves follow
        // paths below the gun). A new controller gets a new folder: guns switched earlier keep the files they use.
        internal const string OptionsFolder = "Assets/Orbiters/Avatar gun/Hands 1/";

        private static UnityEditor.Animations.AnimatorController SharedController(bool left)
        {
            string folder = OptionsFolder + (left ? "Left hand" : "Right hand");
            string path = folder + "/Avatar gun.controller";
            var controller = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path);
            if (controller != null) return controller;
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            System.IO.Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            controller = Controller(folder, left);
            AssetDatabase.SaveAssets();
            return controller;
        }
    }

    /// <summary>Keeps one avatar gun receiver on an avatar wearing the gun and N4N0 (<see cref="AvatarGunInstaller.KeepOneReceiver"/>).</summary>
    internal sealed class AvatarGunReceiverStep : BuildStep
    {
        public override BuildPhase Phase => BuildPhase.BeforeVrcFury;

        public override bool Run(BuildContext context)
        {
            AvatarGunInstaller.KeepOneReceiver(context.Avatar);
            return true;
        }
    }
}
