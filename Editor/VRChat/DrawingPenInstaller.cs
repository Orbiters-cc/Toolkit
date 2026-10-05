using System;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The drawing pen: a native avatar prop (nothing in the built avatar needs a custom runtime script). The prefab is
    /// avatar-independent and published like any accessory; <see cref="Bind"/> fits it to the avatar it is attached to.
    /// </summary>
    public static partial class DrawingPenInstaller
    {
        public const string MenuPath = "Drawing pen";
        public const string PrefabName = "Drawing pen.prefab";
        internal const string Enabled = "Pen/Enabled", Grab = "Pen/Grab", Left = "Pen/Left", Right = "Pen/Right", Clear = "Pen/Clear";
        internal const string HeldLeft = "Pen/HeldLeft", HeldRight = "Pen/HeldRight", Drop = "Pen/Drop";
        // Where the pen waits until it is bound to an avatar's chest.
        private static readonly Vector3 DefaultSpawn = new Vector3(.12f, 1.25f, .35f);
        public static OrbitersDrawingPen Find(GameObject avatar) => avatar != null ? avatar.GetComponentInChildren<OrbitersDrawingPen>(true) : null;

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var pen in root.GetComponentsInChildren<OrbitersDrawingPen>(true)) Bind(pen, avatar);
        });

        /// <summary>Creates the pen prefab with its controller, menu, parameters and materials in <paramref name="folder"/>.</summary>
        public static string CreatePrefab(string folder)
        {
            if (VrcFury.Writer == null) throw new InvalidOperationException("Install VRCFury in Creator Companion to build the drawing pen.");
            if (folder == null || !folder.StartsWith("Assets/", StringComparison.Ordinal)) throw new ArgumentException("Create the pen below Assets/.");
            string path = folder + "/" + PrefabName;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) throw new InvalidOperationException(folder + " already holds a drawing pen.");
            Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            var root = new GameObject("Drawing pen");
            try
            {
                Build(root, folder);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                return path;
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void Build(GameObject root, string folder)
        {
            var marker = root.AddComponent<OrbitersDrawingPen>(); marker.generatedFolder = folder;
            var home = Child(root.transform, "Spawn"); home.localPosition = DefaultSpawn;
            var basis = Child(root.transform, "Grab base"); basis.localPosition = home.localPosition - Vector3.up * .02f;
            var bone = Child(basis, "Bone");
            var end = Child(bone, "Grip"); end.localPosition = Vector3.up * .02f;
            var body = Child(root.transform, "Pen"); body.localPosition = home.localPosition;

            // Only the last part of this short chain is grabbable. A separate constraint carries the visible prop,
            // so stretching the grab chain never stretches its model or ink width.
            var phys = bone.gameObject.AddComponent<VRCPhysBone>();
            phys.rootTransform = bone; phys.endpointPosition = new Vector3(0, .001f, 0);
            phys.pull = 1; phys.spring = 0; phys.immobile = 1;
            phys.radius = .055f; phys.radiusCurve = AnimationCurve.Constant(0, .49f, 0);
            phys.radiusCurve.AddKey(new Keyframe(.5f, 1)); phys.radiusCurve.AddKey(new Keyframe(1, 1));
            phys.maxStretch = 1000; phys.maxSquish = 1; phys.grabMovement = 1;
            phys.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            phys.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True;
            phys.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            phys.snapToHand = true; phys.resetWhenDisabled = true; phys.parameter = Grab;

            var baseConstraint = basis.gameObject.AddComponent<VRCParentConstraint>();
            baseConstraint.Sources.Add(new VRCConstraintSource(home, 1, new Vector3(0, -.02f, 0), Vector3.zero));
            baseConstraint.Sources.Add(new VRCConstraintSource(body, 0, new Vector3(0, -.02f, 0), Vector3.zero));
            baseConstraint.IsActive = true; baseConstraint.Locked = true;
            baseConstraint.RebakeOffsetsWhenUnfrozen = false;
            // Sources 1 and 2 are the left and right wrists, set by Bind on the avatar.
            var follow = body.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(end, 1));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.IsActive = true; follow.Locked = true; follow.RebakeOffsetsWhenUnfrozen = false;

            AddHandContact(body, "Left hand", Left, "HandL");
            AddHandContact(body, "Right hand", Right, "HandR");
            var shell = Material(folder, "Pen shell", new Color(.055f, .07f, .09f), "VRChat/Mobile/Toon Standard");
            var inkMaterial = Material(folder, "Ink", Color.white, "Sprites/Default");
            var accent = Material(folder, "Pen accent", marker.color, "VRChat/Mobile/Toon Standard");
            var model = Child(body, "Model");
            // The constrained transform is the grip, not the model centre or the drawing tip.
            model.localPosition = Vector3.up * .035f;
            Primitive(model, "Barrel", PrimitiveType.Capsule, new Vector3(.022f, .075f, .022f), Vector3.zero, shell);
            Primitive(model, "Grip", PrimitiveType.Cylinder, new Vector3(.025f, .018f, .025f), new Vector3(0, -.035f, 0), accent);
            Primitive(model, "Nib", PrimitiveType.Sphere, Vector3.one * .013f, new Vector3(0, -.079f, 0), accent);
            var tip = Child(body, "Ink"); tip.localPosition = new Vector3(0, -.05f, 0);
            marker.ink = tip.gameObject.AddComponent<TrailRenderer>();
            marker.ink.sharedMaterial = inkMaterial; marker.ink.time = 0; marker.ink.emitting = false;
            marker.ink.minVertexDistance = .002f; marker.ink.widthMultiplier = .006f;
            marker.ink.numCornerVertices = 4; marker.ink.numCapVertices = 4;
            marker.ink.startColor = marker.ink.endColor = marker.color;
            marker.ink.shadowCastingMode = ShadowCastingMode.Off; marker.ink.receiveShadows = false;
            marker.ink.lightProbeUsage = LightProbeUsage.Off; marker.ink.reflectionProbeUsage = ReflectionProbeUsage.Off;
            marker.ink.autodestruct = false;
            BuildController(root, folder);
            model.gameObject.SetActive(false);
            phys.enabled = false;
        }

        /// <summary>
        /// Fits a placed pen to <paramref name="avatar"/>: it waits in front of the chest and is held at either wrist.
        /// Recorded with Undo; binding again (another avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(OrbitersDrawingPen pen, Transform avatar)
        {
            if (pen == null || avatar == null) throw new ArgumentNullException(pen == null ? nameof(pen) : nameof(avatar));
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) throw new InvalidOperationException("The drawing pen needs a humanoid avatar.");
            var root = pen.transform;
            var home = root.Find("Spawn"); var basis = root.Find("Grab base"); var body = root.Find("Pen");
            var follow = body != null ? body.GetComponent<VRCParentConstraint>() : null;
            if (home == null || basis == null || follow == null || follow.Sources.Count < 3) throw new InvalidOperationException("This drawing pen was modified: add it again.");
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Hips);
            Undo.RecordObjects(new Object[] { home, basis, body, follow }, "Fit drawing pen");
            home.position = chest.position + avatar.rotation * new Vector3(.12f, 0, .35f);
            basis.localPosition = home.localPosition - Vector3.up * .02f;
            body.localPosition = home.localPosition;
            follow.Sources[1] = HandSource(animator, true);
            follow.Sources[2] = HandSource(animator, false);
            EditorUtility.SetDirty(follow);
        }

        private static Transform Child(Transform parent, string name)
        { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }

        internal static VRCConstraintSource HandSource(Animator animator, bool left)
        {
            var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var index = animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            return HandSource(animator.transform, hand, middle, index, little, left);
        }

        internal static VRCConstraintSource HandSource(Transform avatar, Transform hand, Transform middle, Transform index, Transform little, bool left)
        {
            var forward = middle != null ? middle.position - hand.position : hand.rotation * (left ? Vector3.left : Vector3.right) * .08f;
            if (forward.sqrMagnitude < .000001f) forward = hand.forward * .08f;
            var across = index != null && little != null ? index.position - little.position : avatar.forward;
            var normal = Vector3.Cross(forward, across) * (left ? -1 : 1);
            if (normal.sqrMagnitude < .000001f) normal = Vector3.Cross(forward, avatar.up);
            if (normal.sqrMagnitude < .000001f) normal = Vector3.Cross(forward, avatar.right);
            if (across.sqrMagnitude < .000001f) across = Vector3.Cross(normal, forward);
            var rotation = Quaternion.LookRotation(normal.normalized, -across.normalized);
            // Use the wrist as the source: finger curl must not rotate or displace the grip.
            // Sit across the curled fingers, just clear of the palm, with the nib at the thumb side.
            var grip = hand.position + forward * .85f + normal.normalized * forward.magnitude * .3f;
            return new VRCConstraintSource(hand, 0, hand.InverseTransformPoint(grip),
                (Quaternion.Inverse(hand.rotation) * rotation).eulerAngles);
        }

        private static void AddHandContact(Transform body, string name, string parameter, string tag)
        {
            var receiver = Child(body, name).gameObject.AddComponent<VRCContactReceiver>();
            receiver.radius = .11f; receiver.allowSelf = true; receiver.allowOthers = false;
            receiver.localOnly = false; receiver.parameter = parameter;
            receiver.collisionTags.Add(tag);
        }

        private static Material Material(string folder, string name, Color color, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Missing shader: " + shaderName);
            var material = new Material(shader) { name = name };
            material.color = color; AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat"); return material;
        }

        private static void Primitive(Transform parent, string name, PrimitiveType type, Vector3 scale, Vector3 position, Material material)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }
}
