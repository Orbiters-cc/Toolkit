using System;
using System.Collections.Generic;
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
    /// The voting sign: a wooden sign showing the score chosen in the menu (3 to 10, LOL or a cross), held like the drawing
    /// pen (<see cref="HeldPropRig"/>) in either hand, left in the world or passed to a friend. Native avatar components only
    /// and no Orbiters script, like the pen. Its model and textures are art of their own: copied into the folder first
    /// (<see cref="ModelFile"/>, <see cref="ScoreFile"/> and the wood and metal textures), <see cref="CreatePrefab"/> builds
    /// the materials, controller, menu and prefab around them.
    /// </summary>
    public static partial class VotingSignInstaller
    {
        public const string MenuPath = "Voting sign";
        public const string PrefabName = "Voting sign.prefab";
        internal const string Enabled = "VotingSign/Enabled", Vote = "VotingSign/Vote", Grab = "VotingSign/Grab";
        internal const string Left = "VotingSign/Left", Right = "VotingSign/Right", HeldLeft = "VotingSign/HeldLeft", HeldRight = "VotingSign/HeldRight", Drop = "VotingSign/Drop";
        internal const string Body = "Sign", Model = "Sign/Model", Score = "Sign/Model/Plane";
        // The art the folder holds before the sign is built: the model, its score sheet (a 3 × 4 grid of white marks on
        // black) and the wood and metal textures (colour, normal map, occlusion; metallic for the metal).
        internal const string ModelFile = "Voting sign.fbx", ScoreFile = "Scores.png";
        // The model is in decimetres or so: this makes the sign about 75 cm tall with a 46 cm board.
        private const float ModelScale = .1333f;
        // Where the hand holds the handle, in the model's units: low on the stick behind the board.
        private static readonly Vector3 GripInModel = new Vector3(-.275f, 1.3f, 0);
        // Where the grip waits: until it is bound to an avatar, then in front of its face, the board about at eye level.
        private static readonly Vector3 DefaultSpawn = new Vector3(.12f, 1.1f, .3f);
        private static readonly Vector3 FromHead = new Vector3(.12f, -.36f, .3f);

        /// <summary>A score the menu can show: its value, its name (and icon, Icons/name.png) and its cell of the score sheet.</summary>
        internal sealed class Choice
        {
            public int Value; public string Name; public Vector2 Cell;
            public Choice(int value, string name, float x, float y) { Value = value; Name = name; Cell = new Vector2(x, y); }
        }

        // Each cell is a third of the sheet; the offsets are the ones the sign's art was made with. Nothing (0) is the
        // empty top-left cell.
        internal static readonly Vector2 CellSize = new Vector2(.33f, .33f), Blank = new Vector2(0, .72f);
        internal static readonly Choice[] Choices =
        {
            new Choice(3, "3", .67f, .72f), new Choice(4, "4", 0, .465f), new Choice(5, "5", .33f, .46f), new Choice(6, "6", .67f, .46f),
            new Choice(7, "7", 0, .21f), new Choice(8, "8", .33f, .21f), new Choice(9, "9", .67f, .21f), new Choice(10, "10", 0, -.04f),
            new Choice(11, "LOL", .33f, -.04f), new Choice(12, "No", .66f, -.03f),
        };

        /// <summary>The voting signs below <paramref name="root"/>, known by their grab.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCPhysBone>(true).Where(p => p.parameter == Grab).Select(SignOf).Where(s => s != null).ToList();

        // The grab is "Grab base/Bone", two levels below the sign's root.
        private static Transform SignOf(VRCPhysBone grab)
        {
            var root = grab.transform.parent != null ? grab.transform.parent.parent : null;
            return root != null && root.Find(HeldPropRig.GrabBone) == grab.transform ? root : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var sign in FindAll(root)) Bind(sign, avatar);
        }, root => FindAll(root).Any());

        /// <summary>
        /// Creates the sign prefab with its controller, menu, parameters and materials in <paramref name="folder"/>, around
        /// the model and textures already there.
        /// </summary>
        public static string CreatePrefab(string folder) =>
            HeldPropRig.SavePrefab(folder, PrefabName, "Voting sign", "voting sign", root => Build(root, folder));

        private static void Build(GameObject root, string folder)
        {
            var source = PrepareArt(folder);
            var rig = HeldPropRig.Build(root.transform, Body, DefaultSpawn, Grab, .07f, Left, Right, .18f, othersGrab: true, localContacts: false);
            var wood = Surface(folder, "Wood", false);
            var metal = Surface(folder, "Metal", true);
            var score = HeldPropRig.Additive(folder, "Score", AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + ScoreFile));
            score.mainTextureScale = CellSize; score.mainTextureOffset = Blank; EditorUtility.SetDirty(score);

            var model = Object.Instantiate(source, rig.Body, false);
            model.name = "Model";
            foreach (var animator in model.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
            // The score faces forward, away from whoever holds the sign; the grip sits on the body's origin.
            model.transform.localRotation = Quaternion.Euler(0, -90, 0);
            model.transform.localScale = Vector3.one * ModelScale;
            model.transform.localPosition = -(model.transform.localRotation * GripInModel * ModelScale);
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                // The board and its stick are wood, the score lies on the board's front, the rest (bracket, bolts) is metal.
                bool isScore = renderer.name == "Plane";
                renderer.sharedMaterial = isScore ? score : renderer.name == "Cube" || renderer.name == "Cylinder" ? wood : metal;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (isScore) { renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false; }
            }
            if (model.transform.Find("Plane") == null) throw new InvalidOperationException(ModelFile + " has no score plane: copy the voting sign's model again.");
            BuildController(root, folder);
            model.SetActive(false);
            rig.Grab.enabled = false;
        }

        /// <summary>
        /// Checks the art is in <paramref name="folder"/> and sets how Unity imports it: the model without materials,
        /// animation or rig, normal maps as normal maps, data maps linear, the score sheet clamped.
        /// </summary>
        private static GameObject PrepareArt(string folder)
        {
            string modelPath = folder + "/" + ModelFile;
            if (!(AssetImporter.GetAtPath(modelPath) is ModelImporter model)) throw new InvalidOperationException("Copy the voting sign's model to " + modelPath + " first.");
            model.materialImportMode = ModelImporterMaterialImportMode.None;
            model.animationType = ModelImporterAnimationType.None; model.importAnimation = false;
            model.importBlendShapes = false; model.importCameras = false; model.importLights = false; model.isReadable = false;
            model.SaveAndReimport();
            foreach (string name in new[] { "Wood", "Wood normal", "Wood occlusion", "Metal", "Metal normal", "Metal metallic", "Metal occlusion", ScoreFile.Replace(".png", "") })
            {
                string path = folder + "/" + name + ".png";
                if (!(AssetImporter.GetAtPath(path) is TextureImporter texture)) throw new InvalidOperationException("Copy the voting sign's texture " + path + " first.");
                texture.textureType = name.EndsWith(" normal", StringComparison.Ordinal) ? TextureImporterType.NormalMap : TextureImporterType.Default;
                texture.sRGBTexture = !name.EndsWith(" occlusion", StringComparison.Ordinal) && !name.EndsWith(" metallic", StringComparison.Ordinal) && !name.EndsWith(" normal", StringComparison.Ordinal);
                texture.maxTextureSize = 2048;
                if (path.EndsWith(ScoreFile, StringComparison.Ordinal)) texture.wrapMode = TextureWrapMode.Clamp;
                texture.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        }

        // VRChat's toon shader, which every platform shows, with the art's colour, normal and occlusion maps.
        private static Material Surface(string folder, string name, bool metallic)
        {
            var material = HeldPropRig.Material(folder, name, Color.white, "VRChat/Mobile/Toon Standard");
            Texture2D Map(string suffix) => AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + name + suffix + ".png");
            material.mainTexture = Map("");
            material.SetTexture("_BumpMap", Map(" normal")); material.EnableKeyword("USE_NORMAL_MAPS");
            material.SetTexture("_OcclusionMap", Map(" occlusion")); material.SetFloat("_OcclusionMapChannel", 0); material.EnableKeyword("USE_OCCLUSION_MAP");
            material.EnableKeyword("USE_SPECULAR");
            if (metallic)
            {
                material.SetTexture("_MetallicMap", Map(" metallic")); material.SetFloat("_MetallicMapChannel", 0);
                material.SetFloat("_MetallicStrength", 1); material.SetFloat("_GlossStrength", .55f);
            }
            else material.SetFloat("_GlossStrength", .2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Fits a placed sign to <paramref name="avatar"/>: it waits in front of the face (following the head while it is
        /// off) and follows either wrist, from wherever it was grabbed. Recorded with Undo; binding again (another avatar,
        /// a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform sign, Transform avatar)
        {
            if (sign == null || avatar == null) throw new ArgumentNullException(sign == null ? nameof(sign) : nameof(avatar));
            HeldPropRig.Bind(sign, Body, avatar, FromHead, "voting sign");
        }
    }
}
