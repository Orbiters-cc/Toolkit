using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The hand screen: the video the world is playing, on a screen held by a handle on each side. One hand carries it
    /// (both handles follow that wrist), both hands stretch it (each handle follows its own wrist and the screen spans
    /// them, 16:9, like pulling a picture bigger on a phone), and let go it stays in the world. Native avatar components
    /// only: the size needs no animator maths, constraints work the corners out from the two handles. Grabbing is the
    /// drawing pen's method (<see cref="HeldPropRig"/>): a PhysBone pickup latched to the wrist where it was grabbed.
    /// The prefab is avatar-independent and <see cref="Bind"/> fits it to the avatar it is attached to. It carries its own
    /// shader and no Orbiters script: Toolkit releases ship without .meta files, so its files get other GUIDs in every
    /// project and a gallery copy referring to them would find nothing there.
    /// </summary>
    public static partial class HandScreenInstaller
    {
        public const string MenuPath = "Hand screen";
        public const string PrefabName = "Hand screen.prefab";
        internal const string Enabled = "HandScreen/Enabled", HeldLeft = "HandScreen/HeldLeft", HeldRight = "HandScreen/HeldRight";
        // Both hands hold it, the left hand on the right handle and the right hand on the left one.
        internal const string Crossed = "HandScreen/Crossed", Drop = "HandScreen/Drop";
        internal const string GrabLeftHandle = "HandScreen/GrabLeftHandle", GrabRightHandle = "HandScreen/GrabRightHandle";
        // Which hand is at which handle.
        internal const string LeftAtLeft = "HandScreen/LeftHandAtLeft", RightAtLeft = "HandScreen/RightHandAtLeft";
        internal const string LeftAtRight = "HandScreen/LeftHandAtRight", RightAtRight = "HandScreen/RightHandAtRight";
        internal const string LeftHandle = "Left handle", RightHandle = "Right handle", Frame = "Left handle/Frame", Far = "Left handle/Frame/Far";
        internal const float DefaultWidth = .6f, Aspect = 16f / 9;
        private const float Margin = .012f;
        internal static readonly Color Accent = new Color(.1f, .85f, 1f);
        private const string ShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/HandScreen.shader.txt";
        // Where the screen's middle waits: until it is bound to an avatar, then in front of its face wherever it looks.
        private static readonly Vector3 DefaultSpawn = new Vector3(0, 1.5f, .5f);
        private static readonly Vector3 FromHead = new Vector3(0, -.06f, .5f);
        // The screen's corners, in the order of the mesh's bones.
        internal static readonly string[] Corners = { Frame + "/Top left turn/Top left", Far + "/Top right turn/Top right", Frame + "/Bottom left turn/Bottom left", Far + "/Bottom right turn/Bottom right" };

        /// <summary>The hand screens below <paramref name="root"/>, known by their left handle's grab.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCPhysBone>(true).Where(p => p.parameter == GrabLeftHandle).Select(ScreenOf).Where(s => s != null).ToList();

        // The grab is "Left handle/Grab", two levels below the hand screen's root.
        private static Transform ScreenOf(VRCPhysBone grab)
        {
            var root = grab.transform.parent != null ? grab.transform.parent.parent : null;
            return root != null && root.Find(LeftHandle + "/Grab") == grab.transform ? root : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var screen in FindAll(root)) Bind(screen, avatar);
        }, root => FindAll(root).Any());

        /// <summary>Creates the hand screen prefab with its controller, menu, parameters, mesh and materials in <paramref name="folder"/>.</summary>
        public static string CreatePrefab(string folder)
        {
            if (VrcFury.Writer == null) throw new InvalidOperationException("Install VRCFury in Creator Companion to build the hand screen.");
            if (folder == null || !folder.StartsWith("Assets/", StringComparison.Ordinal)) throw new ArgumentException("Create the hand screen below Assets/.");
            string path = folder + "/" + PrefabName;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) throw new InvalidOperationException(folder + " already holds a hand screen.");
            Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
            var root = new GameObject("Hand screen");
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
            var home = HeldPropRig.Home(root.transform, DefaultSpawn);
            var leftHome = HeldPropRig.Child(home, "Left home"); leftHome.localPosition = Vector3.left * DefaultWidth / 2;
            var rightHome = HeldPropRig.Child(home, "Right home"); rightHome.localPosition = Vector3.right * DefaultWidth / 2;
            var left = Handle(root.transform, LeftHandle, leftHome, GrabLeftHandle, LeftAtLeft, RightAtLeft);
            var right = Handle(root.transform, RightHandle, rightHome, GrabRightHandle, LeftAtRight, RightAtRight);

            // The frame runs from the left handle towards the right one, upright as the left handle is; Far sits on the
            // right handle, so its position in the frame is the screen's width.
            var frame = HeldPropRig.Child(left, "Frame");
            var aim = frame.gameObject.AddComponent<VRCAimConstraint>();
            aim.Sources.Add(new VRCConstraintSource(right, 1));
            aim.AimAxis = Vector3.right; aim.UpAxis = Vector3.up;
            aim.WorldUp = VRCConstraintBase.WorldUpType.ObjectRotationUp; aim.WorldUpTransform = left; aim.WorldUpVector = Vector3.up;
            aim.IsActive = true; aim.Locked = true;
            var far = HeldPropRig.Child(frame, "Far"); far.localPosition = Vector3.right * DefaultWidth;
            var reach = far.gameObject.AddComponent<VRCPositionConstraint>();
            reach.Sources.Add(new VRCConstraintSource(right, 1));
            reach.IsActive = true; reach.Locked = true;
            // Each corner copies the width, turned a quarter up or down and scaled to half the 16:9 height.
            var corners = new[] { Corner(frame, far, "Top left", true), Corner(far, far, "Top right", true), Corner(frame, far, "Bottom left", false), Corner(far, far, "Bottom right", false) };

            var shell = HeldPropRig.Material(folder, "Screen shell", new Color(.055f, .07f, .09f), "VRChat/Mobile/Toon Standard");
            var accent = HeldPropRig.Material(folder, "Screen accent", Accent, "VRChat/Mobile/Toon Standard");
            var shader = CopyShader(folder);
            var picture = PictureMaterial(folder, shader, "Screen picture", video: true);
            var back = PictureMaterial(folder, shader, "Screen back", video: false);
            // The handles keep their size; the bars show where to grab.
            HeldPropRig.Primitive(frame, "Left bar", PrimitiveType.Capsule, new Vector3(.016f, .06f, .016f), Vector3.left * .024f, accent);
            HeldPropRig.Primitive(far, "Right bar", PrimitiveType.Capsule, new Vector3(.016f, .06f, .016f), Vector3.right * .024f, accent);
            var screen = HeldPropRig.Child(frame, "Screen").gameObject.AddComponent<SkinnedMeshRenderer>();
            screen.sharedMesh = ScreenMesh(folder, screen.transform, corners);
            screen.bones = corners; screen.rootBone = frame; screen.quality = SkinQuality.Bone1;
            screen.sharedMaterials = new[] { picture, shell, back };
            screen.localBounds = new Bounds(Vector3.zero, Vector3.one * 8);
            screen.shadowCastingMode = ShadowCastingMode.Off; screen.receiveShadows = false;

            BuildController(root, folder);
            frame.gameObject.SetActive(false);
            foreach (var grab in root.GetComponentsInChildren<VRCPhysBone>(true)) grab.enabled = false;
        }

        // A handle follows its home, then a wrist. Its short PhysBone chain only reports the grab; nothing follows it.
        private static Transform Handle(Transform root, string name, Transform home, string grab, string leftAt, string rightAt)
        {
            var handle = HeldPropRig.Child(root, name); handle.position = home.position;
            var follow = handle.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(home, 1));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.Sources.Add(new VRCConstraintSource(null, 0));
            follow.IsActive = true; follow.Locked = true; follow.RebakeOffsetsWhenUnfrozen = true;
            var bone = HeldPropRig.Child(handle, "Grab");
            HeldPropRig.Child(bone, "Grip").localPosition = Vector3.up * .02f;
            HeldPropRig.GrabChain(bone, .1f, grab, othersGrab: false);
            // Generous, so a grab at the edge of the PhysBone's reach still knows its hand.
            HeldPropRig.HandContact(handle, "Left hand", leftAt, true, .22f, localOnly: true);
            HeldPropRig.HandContact(handle, "Right hand", rightAt, false, .22f, localOnly: true);
            return handle;
        }

        private static Transform Corner(Transform parent, Transform far, string name, bool top)
        {
            var turn = HeldPropRig.Child(parent, name + " turn");
            turn.localRotation = Quaternion.Euler(0, 0, top ? 90 : -90); turn.localScale = Vector3.one * (.5f / Aspect);
            var corner = HeldPropRig.Child(turn, name); corner.localPosition = far.localPosition;
            var copy = corner.gameObject.AddComponent<VRCPositionConstraint>();
            copy.Sources.Add(new VRCConstraintSource(far, 1));
            copy.SolveInLocalSpace = true; copy.IsActive = true; copy.Locked = true;
            return corner;
        }

        /// <summary>
        /// The picture, the dark bezel behind it and the idle back, each vertex held by one corner: the bezel and the gaps
        /// between the layers keep their size in metres whatever the screen's.
        /// </summary>
        private static Mesh ScreenMesh(string folder, Transform renderer, Transform[] corners)
        {
            float w = DefaultWidth, h = DefaultWidth / Aspect / 2, m = Margin;
            var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var normals = new List<Vector3>(); var weights = new List<BoneWeight>();
            void Layer(float grow, float z, bool backFacing)
            {
                for (int i = 0; i < 4; i++)
                {
                    vertices.Add(new Vector3(i % 2 == 0 ? -grow : w + grow, i < 2 ? h + grow : -h - grow, z));
                    // Seen from behind, the back's left is the screen's right: its picture is not mirrored.
                    uv.Add(new Vector2((i % 2 == 0) != backFacing ? 0 : 1, i < 2 ? 1 : 0));
                    normals.Add(backFacing ? Vector3.forward : Vector3.back);
                    weights.Add(new BoneWeight { boneIndex0 = i, weight0 = 1 });
                }
            }
            Layer(0, 0, false); Layer(m, .002f, false); Layer(m, .004f, true);
            var mesh = new Mesh { name = "Hand screen" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetNormals(normals);
            mesh.subMeshCount = 3;
            // Facing the owner (-Z): top left, top right, bottom right, then the other half; the back the other way round.
            mesh.SetTriangles(new[] { 0, 1, 3, 0, 3, 2 }, 0);
            mesh.SetTriangles(new[] { 4, 5, 7, 4, 7, 6 }, 1);
            mesh.SetTriangles(new[] { 8, 11, 9, 8, 10, 11 }, 2);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = corners.Select(c => c.worldToLocalMatrix * renderer.localToWorldMatrix).ToArray();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/Screen mesh.asset");
            return mesh;
        }

        // The shader goes into the prop's folder, so the published package holds it.
        private static Shader CopyShader(string folder)
        {
            var template = AssetDatabase.LoadAssetAtPath<TextAsset>(ShaderTemplate);
            if (template == null) throw new InvalidOperationException("Missing " + ShaderTemplate + ": reinstall Orbiters Toolkit.");
            string path = folder + "/Hand screen.shader";
            File.WriteAllText(path, template.text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Unity did not import " + path + ".");
            return shader;
        }

        private static Material PictureMaterial(string folder, Shader shader, string name, bool video)
        {
            var material = new Material(shader) { name = name };
            material.SetFloat("_Aspect", Aspect); material.SetColor("_Accent", Accent); material.SetFloat("_Video", video ? 1 : 0);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        /// <summary>
        /// Fits a placed hand screen to <paramref name="avatar"/>: it waits in front of the face (following the head while
        /// it is off) and each handle follows
        /// either wrist from wherever it was grabbed. Recorded with Undo; binding again (another avatar, a moved armature)
        /// replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform screen, Transform avatar)
        {
            if (screen == null) throw new ArgumentNullException(nameof(screen));
            var animator = HeldPropRig.Humanoid(avatar, "hand screen");
            var home = screen.Find(HeldPropRig.Spawn);
            var handles = new[] { screen.Find(LeftHandle), screen.Find(RightHandle) };
            var follows = handles.Select(h => h != null ? h.GetComponent<VRCParentConstraint>() : null).ToArray();
            if (home == null || follows.Any(f => f == null || f.Sources.Count < 3)) throw new InvalidOperationException("This hand screen was modified: add it again.");
            Undo.RecordObjects(new Object[] { home, handles[0], handles[1], follows[0], follows[1] }, "Fit hand screen");
            HeldPropRig.FollowHead(home, animator, avatar, FromHead, "hand screen");
            for (int i = 0; i < 2; i++)
            {
                var waits = follows[i].Sources[0].SourceTransform;
                handles[i].SetPositionAndRotation(waits.position, waits.rotation);
                HeldPropRig.Wrists(follows[i], animator);
            }
        }
    }
}
