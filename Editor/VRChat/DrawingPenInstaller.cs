using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The drawing pen: a native avatar prop (nothing in the built avatar needs a custom runtime script). The prefab is
    /// avatar-independent and published like any accessory; <see cref="Bind"/> fits it to the avatar it is attached to.
    /// It carries no Orbiters script: Toolkit releases ship without .meta files, so its scripts get other GUIDs in every
    /// project and a gallery copy referring to one would arrive with a missing script and never be fitted.
    /// </summary>
    public static partial class DrawingPenInstaller
    {
        public const string MenuPath = "Drawing pen";
        public const string PrefabName = "Drawing pen.prefab";
        internal const string Enabled = "Pen/Enabled", Grab = "Pen/Grab", Left = "Pen/Left", Right = "Pen/Right", Clear = "Pen/Clear";
        internal const string HeldLeft = "Pen/HeldLeft", HeldRight = "Pen/HeldRight", Drop = "Pen/Drop";
        // Where the pen waits until it is bound to an avatar's head.
        private static readonly Vector3 DefaultSpawn = new Vector3(.1f, 1.45f, .3f);
        internal static readonly Color InkColour = new Color(.1f, .85f, 1f);

        /// <summary>The pens below <paramref name="root"/>, known by their grab.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCPhysBone>(true).Where(p => p.parameter == Grab).Select(PenOf).Where(p => p != null).ToList();

        // The grab is "Grab base/Bone", two levels below the pen's root.
        private static Transform PenOf(VRCPhysBone grab)
        {
            var root = grab.transform.parent != null ? grab.transform.parent.parent : null;
            return root != null && root.Find(HeldPropRig.GrabBone) == grab.transform ? root : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var pen in FindAll(root)) Bind(pen, avatar);
        }, root => FindAll(root).Any());

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
            var rig = HeldPropRig.Build(root.transform, "Pen", DefaultSpawn, Grab, .055f, Left, Right, .18f, othersGrab: true, localContacts: false);
            var shell = HeldPropRig.Material(folder, "Pen shell", new Color(.055f, .07f, .09f), "VRChat/Mobile/Toon Standard");
            var inkMaterial = HeldPropRig.Material(folder, "Ink", Color.white, "Sprites/Default");
            var accent = HeldPropRig.Material(folder, "Pen accent", InkColour, "VRChat/Mobile/Toon Standard");
            var model = HeldPropRig.Child(rig.Body, "Model");
            // The constrained transform is the grip, not the model centre or the drawing tip.
            model.localPosition = Vector3.up * .035f;
            HeldPropRig.Primitive(model, "Barrel", PrimitiveType.Capsule, new Vector3(.022f, .075f, .022f), Vector3.zero, shell);
            HeldPropRig.Primitive(model, "Grip", PrimitiveType.Cylinder, new Vector3(.025f, .018f, .025f), new Vector3(0, -.035f, 0), accent);
            HeldPropRig.Primitive(model, "Nib", PrimitiveType.Sphere, Vector3.one * .013f, new Vector3(0, -.079f, 0), accent);
            var tip = HeldPropRig.Child(rig.Body, "Ink"); tip.localPosition = new Vector3(0, -.05f, 0);
            var ink = tip.gameObject.AddComponent<TrailRenderer>();
            ink.sharedMaterial = inkMaterial; ink.time = 0; ink.emitting = false;
            ink.minVertexDistance = .002f; ink.widthMultiplier = .006f;
            ink.numCornerVertices = 4; ink.numCapVertices = 4;
            ink.startColor = ink.endColor = InkColour;
            ink.shadowCastingMode = ShadowCastingMode.Off; ink.receiveShadows = false;
            ink.lightProbeUsage = LightProbeUsage.Off; ink.reflectionProbeUsage = ReflectionProbeUsage.Off;
            ink.autodestruct = false;
            BuildController(root, folder);
            model.gameObject.SetActive(false);
            rig.Grab.enabled = false;
        }

        /// <summary>
        /// Fits a placed pen to <paramref name="avatar"/>: it waits in front of the face (following the head while it is
        /// off) and follows either wrist, from wherever it was grabbed. Recorded with Undo; binding again (another avatar,
        /// a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform pen, Transform avatar)
        {
            if (pen == null || avatar == null) throw new ArgumentNullException(pen == null ? nameof(pen) : nameof(avatar));
            HeldPropRig.Bind(pen, "Pen", avatar, new Vector3(.1f, -.12f, .3f), "drawing pen");
        }
    }
}
