using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Orb's projected screen (Orbiters contracts, Docs/orb.md, Screen), on every orb: Orb's menu shown in front of its
    /// owner, where everyone around sees it. Its owner's opens only in VRChat's desktop mode or with the orb card's
    /// "Replace Orbiters overlay" option (<see cref="ScreenReplace"/>); in VR without it, Orbiters desktop's SteamVR overlay
    /// shows the menu instead. When it opens it stays where it appeared (frozen in the world) so a hand reaching for it
    /// never pushes it away.
    /// <para>
    /// The desktop writes the screen's memory, 744 bytes (1024 with 128-byte thumbnails, <see cref="ScreenThumbnailBytes"/>)
    /// kept two to an animated animator parameter (each word b0 + 256 × b1, so a cleared word is two zero bytes): for its owner through local parameters (<see cref="ScreenPointer"/>
    /// and 16 <see cref="ScreenValue"/>s a block, as fast as VRChat takes them), for everyone else through the text
    /// panel's synced channel (<see cref="TextPointer"/> past the panel's blocks; 254 closes the screen). Eight local
    /// contact receivers (four per index finger) say where a finger is. In desktop mode, where fingers can't reach it, its
    /// owner points by looking: two local contact senders held on the head bone, at the eyes and on the line of sight
    /// before them (<see cref="Gaze"/>, a small dot while the menu is open), and eight more receivers say where they look
    /// from, and a left click presses. No synced bits; the contacts are local and cost nothing in VRChat's performance rank.
    /// </para>
    /// </summary>
    public static partial class OrbInstaller
    {
        public const string ScreenOpen = "Orb/Screen/Open", ScreenPointer = "Orb/Screen/Pointer";
        public const string ScreenValuePrefix = "Orb/Screen/Value", ScreenTouchPrefix = "Orb/Screen/Touch/";
        /// <summary>
        /// Local, never changed by a menu: on when the orb card's "Replace Orbiters overlay" option is (its default value),
        /// so the screen shows Orb's menu in VR too. Orbiters desktop reads it to choose between its overlay and the screen.
        /// </summary>
        public const string ScreenReplace = "Orb/Screen/Replace";
        /// <summary>VRChat's: 1 in VR, 0 in desktop mode.</summary>
        internal const string VRMode = "VRMode";
        /// <summary>
        /// How near the owner's eyes and a point on their line of sight (the gaze point) are to each touch point, as
        /// <see cref="ScreenTouchPrefix"/> for fingers: the hovered point is where the line through both meets the screen.
        /// </summary>
        public const string ScreenEyePrefix = "Orb/Screen/Eye", ScreenGazePrefix = "Orb/Screen/Gaze";
        /// <summary>The two senders' collision tags: only the screen's eye and gaze receivers take them.</summary>
        public const string EyeTag = "OrbEye", GazeTag = "OrbGaze";
        // At the eyes, turned with the head bone; the gaze point on the line of sight, this far before them (nearer than
        // any point of the screen, so its dot shows in front of it); the senders' radius and the dot's.
        internal const string Gaze = Head + "/Gaze", GazePoint = Gaze + "/Point", GazeDot = GazePoint + "/Dot";
        internal const float GazeDistance = .38f, GazeRadius = .01f, GazeDotRadius = .004f;
        /// <summary>The screen protocol's version (the avatar manifest's <c>orb.screen</c>): 2, colour thumbnails.</summary>
        public const int ScreenVersion = 2;
        public const int ScreenLocalValues = 16;
        /// <summary><see cref="TextPointer"/>: everyone else's screen closes (and clears).</summary>
        public const int PointerScreenClose = 254;
        /// <summary><see cref="ScreenPointer"/>: the owner's screen memory clears.</summary>
        public const int ScreenLocalClear = 255;
        internal const string ScreenObject = Head + "/Screen";
        // The screen, 16:9, and where its middle sits from the eyes (right, up, forward): low and close enough to touch.
        internal const float ScreenWidth = .48f, ScreenHeight = .27f;
        internal static readonly Vector3 ScreenFromEyes = new Vector3(0, -.2f, .42f);
        // The touch points (orbiters-orb's menu::touch): past the top corners, under the bottom's middle, behind the middle;
        // their radius reaches the far corner of the screen.
        internal const float ScreenTouchRadius = .8f;
        internal static readonly Vector3[] ScreenTouchPoints =
        {
            new Vector3(-ScreenWidth / 2 - .05f, ScreenHeight / 2 + .05f, 0), new Vector3(ScreenWidth / 2 + .05f, ScreenHeight / 2 + .05f, 0),
            new Vector3(0, -ScreenHeight / 2 - .05f, 0), new Vector3(0, 0, .15f),
        };
        // The screen's shared files: made once for every orb that has a screen, and for every orb switched to a thumbnail
        // size its own files lack.
        internal const string ScreenFolder = "Assets/Orbiters/Orb/Screen 3";
        private const string ScreenShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreen.shader.txt";
        // Drawn by Orbiters desktop (`orbiters-desktop --orb-menu`) from the menu's layout: the page pictures (a slice per
        // page, the first empty) and the layout table.
        private const string ScreenPagesTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreenPages.png.bytes";
        private const string ScreenLayoutTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreenLayout.png.bytes";
        internal const int ScreenPageSlices = 10;

        public static string ScreenValue(int index) => ScreenValuePrefix + index;
        public static string ScreenTouch(bool right, int index) => ScreenTouchPrefix + (right ? "R" : "L") + index;
        public static string ScreenGaze(int index) => ScreenGazePrefix + index;
        public static string ScreenEye(int index) => ScreenEyePrefix + index;

        // The screen's own animator parameters: its words, the bytes the values carry (synced and local), the blocks the two
        // pointers write (one update late, as the local bytes; the synced bytes come from the text's late values,
        // ValueLag updates later still).
        internal const string SyncBlock = "Orb/Screen/SyncBlock", LocalBlock = "Orb/Screen/LocalBlock";
        internal static string Word(int k) => "Orb/Screen/Word" + k;
        internal static string SyncDigit(int i) => "Orb/Screen/SyncDigit" + i;
        internal static string LocalDigit(int i) => "Orb/Screen/LocalDigit" + i;
        internal static string WordProperty(int k) => "material._W" + k;

        /// <summary>Whether a placed orb has the screen object.</summary>
        public static bool HasScreen(Transform orb) => orb != null && orb.Find(ScreenObject) != null;

        // ---- The shared files ----

        /// <summary>
        /// The screen's shader (word properties and thumbnail decoder written out), pages, layout, glyphs, mesh and
        /// material for thumbnails of <paramref name="thumbnailBytes"/>, made once in <paramref name="folder"/> (a new orb's
        /// own; the shared <see cref="ScreenFolder"/> for an orb made without a screen or switched to another thumbnail size;
        /// tests use their own). Each size has its own shader and material; the mesh is shared.
        /// </summary>
        internal static (Mesh mesh, Material material) ScreenAssets(string folder = ScreenFolder, int thumbnailBytes = DefaultThumbnailBytes)
        {
            string name = ScreenFileName(thumbnailBytes);
            string materialPath = folder + "/" + name + ".mat", meshPath = folder + "/Orb screen mesh.asset";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (material != null && mesh != null && material.shader != null && material.shader.isSupported) return (mesh, material);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.ImportAsset(folder);
            }
            if (material == null || material.shader == null || !material.shader.isSupported)
            {
                var shader = ScreenShader(folder + "/" + name + ".shader", thumbnailBytes);
                var pages = AssetDatabase.LoadAssetAtPath<Texture2DArray>(folder + "/Orb screen pages.png") ?? ScreenPages(folder + "/Orb screen pages.png");
                var layout = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Orb screen layout.png") ?? ScreenLayout(folder + "/Orb screen layout.png");
                var glyphs = Atlas(folder);
                material = new Material(shader) { name = name };
                material.SetTexture("_Pages", pages);
                material.SetTexture("_Layout", layout);
                material.SetTexture("_MainTex", glyphs);
                material.renderQueue = (int)RenderQueue.Transparent;
                AssetDatabase.DeleteAsset(materialPath);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            // Made once: every screen of this folder, whatever its thumbnails, draws it.
            if (mesh == null)
            {
                mesh = ScreenMesh();
                AssetDatabase.CreateAsset(mesh, meshPath);
            }
            AssetDatabase.SaveAssets();
            return (mesh, material);
        }

        // The screen's files for a thumbnail size: "Orb screen" for 72 bytes, "Orb screen 128 B" for 128.
        private static string ScreenFileName(int thumbnailBytes) => thumbnailBytes == DefaultThumbnailBytes ? "Orb screen" : "Orb screen " + thumbnailBytes + " B";

        /// <summary>The name of the screen's shader for thumbnails of <paramref name="thumbnailBytes"/>.</summary>
        internal static string ScreenShaderName(int thumbnailBytes) => "Orbiters/Orb Screen (" + thumbnailBytes + " B thumbnails)";

        // The template with its word properties, their registers and their sum written out, and the thumbnail decoder.
        private static Shader ScreenShader(string path, int thumbnailBytes)
        {
            var template = AssetDatabase.LoadAssetAtPath<TextAsset>(ScreenShaderTemplate);
            if (template == null) throw new InvalidOperationException("Missing " + ScreenShaderTemplate + ": reinstall Orbiters Toolkit.");
            var properties = new StringBuilder();
            var buffer = new StringBuilder();
            var sum = new StringBuilder();
            for (int k = 0; k < ScreenWords(thumbnailBytes); k++)
            {
                properties.AppendLine($"        [HideInInspector] _W{k} (\"Word {k}\", Float) = 0");
                buffer.AppendLine($"            float _W{k} : packoffset(c{k});");
                sum.Append(k % 12 == 0 ? "\n                " : " ").Append($"+ _W{k}");
            }
            string defines = $"            #define SCREEN_WORDS {ScreenWords(thumbnailBytes)}\n            #define THUMBNAIL_BYTES {thumbnailBytes}";
            string text = template.text.Replace("Shader \"Orbiters/Orb Screen\"", "Shader \"" + ScreenShaderName(thumbnailBytes) + "\"")
                .Replace("//@PROPERTIES", properties.ToString().TrimEnd()).Replace("//@BUFFER", buffer.ToString().TrimEnd()).Replace("//@SUM", sum.ToString())
                .Replace("//@DEFINES", defines).Replace("//@THUMBNAIL", ThumbnailDecoder(thumbnailBytes));
            System.IO.File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Unity did not import " + path + ".");
            return shader;
        }

        // The pages as a texture array (a slice per page id), sRGB, mipmapped.
        private static Texture2DArray ScreenPages(string path)
        {
            CopyFile(ScreenPagesTemplate, path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.textureShape = TextureImporterShape.Texture2DArray;
            settings.flipbookRows = ScreenPageSlices;
            settings.flipbookColumns = 1;
            importer.SetTextureSettings(settings);
            importer.sRGBTexture = true; importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true; importer.maxTextureSize = 8192; importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 2;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        }

        // The layout table: exact bytes (no colour space, no compression, no filtering).
        private static Texture2D ScreenLayout(string path)
        {
            CopyFile(ScreenLayoutTemplate, path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None; importer.filterMode = FilterMode.Point; importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // A quad facing its owner (who looks along +Z), its first UV the page (0, 0 bottom left).
        private static Mesh ScreenMesh()
        {
            float x = ScreenWidth / 2, y = ScreenHeight / 2;
            var mesh = new Mesh { name = "Orb screen" };
            mesh.SetVertices(new List<Vector3> { new Vector3(-x, -y, 0), new Vector3(x, -y, 0), new Vector3(-x, y, 0), new Vector3(x, y, 0) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
            mesh.SetNormals(Enumerable.Repeat(Vector3.back, 4).ToList());
            mesh.SetTriangles(new[] { 2, 3, 1, 2, 1, 0 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- On a placed orb ----

        /// <summary>
        /// Adds the screen to a placed orb (with Undo): in front of its owner's eyes, hidden until the animator shows it,
        /// with its touch points, for thumbnails of <paramref name="thumbnailBytes"/>. Its place follows the panel's (both
        /// are fitted from the eyes).
        /// </summary>
        internal static Transform AddScreen(Transform orb, string folder = ScreenFolder, int thumbnailBytes = DefaultThumbnailBytes)
        {
            var existing = orb.Find(ScreenObject);
            if (existing != null) return existing;
            var head = orb.Find(Head);
            var panel = orb.Find(Panel);
            if (head == null || panel == null) throw new InvalidOperationException("This orb was modified: add it again.");
            var (mesh, material) = ScreenAssets(folder, thumbnailBytes);
            var screen = new GameObject("Screen");
            Undo.RegisterCreatedObjectUndo(screen, "Orb screen");
            screen.transform.SetParent(head, false);
            screen.SetActive(false);
            screen.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = screen.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            Quiet(renderer);
            // Follows the head (its parent) while closed; frozen in the world while open.
            var hold = screen.AddComponent<VRCParentConstraint>();
            hold.Sources.Add(new VRCConstraintSource(head, 1));
            hold.Locked = true; hold.IsActive = true; hold.FreezeToWorld = false; hold.RebakeOffsetsWhenUnfrozen = false;
            for (int hand = 0; hand < 2; hand++)
            for (int i = 0; i < ScreenTouchPoints.Length; i++)
            {
                bool right = hand == 1;
                var point = Child(screen.transform, "Touch " + (right ? "R" : "L") + i);
                point.localPosition = ScreenTouchPoints[i];
                var receiver = point.gameObject.AddComponent<VRCContactReceiver>();
                receiver.shapeType = ContactBase.ShapeType.Sphere; receiver.radius = ScreenTouchRadius;
                receiver.allowSelf = true; receiver.allowOthers = false; receiver.localOnly = true;
                receiver.receiverType = ContactReceiver.ReceiverType.Proximity; receiver.parameter = ScreenTouch(right, i);
                receiver.collisionTags.Add(right ? "FingerIndexR" : "FingerIndexL");
            }
            // The eyes' and the gaze point's, at the same places.
            foreach (var (name, tag, parameter) in new (string, string, Func<int, string>)[] { ("Eye ", EyeTag, ScreenEye), ("Gaze ", GazeTag, ScreenGaze) })
            for (int i = 0; i < ScreenTouchPoints.Length; i++)
            {
                var point = Child(screen.transform, name + i);
                point.localPosition = ScreenTouchPoints[i];
                var receiver = point.gameObject.AddComponent<VRCContactReceiver>();
                receiver.shapeType = ContactBase.ShapeType.Sphere; receiver.radius = ScreenTouchRadius;
                receiver.allowSelf = true; receiver.allowOthers = false; receiver.localOnly = true;
                receiver.receiverType = ContactReceiver.ReceiverType.Proximity; receiver.parameter = parameter(i);
                receiver.collisionTags.Add(tag);
            }
            PlaceScreen(orb);
            return screen.transform;
        }

        /// <summary>
        /// The screen of a placed orb draws thumbnails of <paramref name="thumbnailBytes"/> (with Undo): a screen whose
        /// shader is for another size (or from before colour thumbnails) takes the shared material for this one.
        /// </summary>
        internal static void SetScreenThumbnails(Transform orb, int thumbnailBytes)
        {
            var screen = orb.Find(ScreenObject);
            var renderer = screen != null ? screen.GetComponent<MeshRenderer>() : null;
            if (renderer == null) return;
            var current = renderer.sharedMaterial;
            if (current != null && current.shader != null && current.shader.name == ScreenShaderName(thumbnailBytes)) return;
            var (_, material) = ScreenAssets(ScreenFolder, thumbnailBytes);
            Undo.RecordObject(renderer, "Orb thumbnail size");
            renderer.sharedMaterial = material;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        // The screen before the eyes, which the panel's place tells (the panel sits at eyes + PanelFromEyes).
        private static void PlaceScreen(Transform orb)
        {
            var screen = orb.Find(ScreenObject);
            var panel = orb.Find(Panel);
            if (screen == null || panel == null) return;
            var eyes = panel.localPosition - PanelFromEyes;
            var hold = screen.GetComponent<VRCParentConstraint>();
            Undo.RecordObjects(new Object[] { screen, hold }, "Fit orb screen");
            screen.localPosition = eyes + ScreenFromEyes;
            screen.localRotation = Quaternion.identity;
            if (hold != null && hold.Sources.Count > 0)
            {
                var source = hold.Sources[0];
                source.ParentPositionOffset = screen.localPosition;
                source.ParentRotationOffset = Vector3.zero;
                hold.Sources[0] = source;
                EditorUtility.SetDirty(hold);
            }
        }

        // ---- The animator ----

        /// <summary>
        /// The screen's parameters, layers and expression parameters (all local), added to an orb's controller;
        /// <paramref name="replace"/> is <see cref="ScreenReplace"/>'s default, <paramref name="thumbnailBytes"/>
        /// <see cref="ScreenThumbnailBytes"/>'s (the memory's size follows).
        /// </summary>
        private static void ScreenController(AnimatorController controller, int textSync, List<VRCExpressionParameters.Parameter> parameters, bool replace, int thumbnailBytes)
        {
            controller.AddParameter(ScreenOpen, AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenReplace, type = AnimatorControllerParameterType.Bool, defaultBool = replace });
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenThumbnailBytes, type = AnimatorControllerParameterType.Int, defaultInt = thumbnailBytes });
            controller.AddParameter(VRMode, AnimatorControllerParameterType.Int);
            controller.AddParameter(ScreenPointer, AnimatorControllerParameterType.Int);
            for (int i = 0; i < ScreenLocalValues; i++) controller.AddParameter(ScreenValue(i), AnimatorControllerParameterType.Float);
            for (int hand = 0; hand < 2; hand++)
            for (int i = 0; i < ScreenTouchPoints.Length; i++) controller.AddParameter(ScreenTouch(hand == 1, i), AnimatorControllerParameterType.Float);
            for (int i = 0; i < ScreenTouchPoints.Length; i++) controller.AddParameter(ScreenEye(i), AnimatorControllerParameterType.Float);
            for (int i = 0; i < ScreenTouchPoints.Length; i++) controller.AddParameter(ScreenGaze(i), AnimatorControllerParameterType.Float);
            // VRChat's: whether this client is the avatar's owner.
            if (!controller.parameters.Any(p => p.name == "IsLocal")) controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            controller.AddParameter(SyncBlock, AnimatorControllerParameterType.Float);
            controller.AddParameter(LocalBlock, AnimatorControllerParameterType.Float);
            for (int i = 0; i < textSync; i++) controller.AddParameter(SyncDigit(i), AnimatorControllerParameterType.Float);
            for (int i = 0; i < ScreenLocalValues; i++) controller.AddParameter(LocalDigit(i), AnimatorControllerParameterType.Float);
            for (int k = 0; k < ScreenWords(thumbnailBytes); k++) controller.AddParameter(Word(k), AnimatorControllerParameterType.Float);

            ScreenBlocks(controller, textSync, thumbnailBytes);
            ScreenLocalBlocks(controller, thumbnailBytes);
            ScreenLayer(controller, textSync, thumbnailBytes);
            GazeDotLayer(controller);

            parameters.Add(Parameter(ScreenOpen, synced: false));
            // Not saved: a new upload's option always holds.
            parameters.Add(Parameter(ScreenReplace, synced: false, defaultValue: replace ? 1 : 0));
            parameters.Add(Parameter(ScreenThumbnailBytes, synced: false, type: VRCExpressionParameters.ValueType.Int, defaultValue: thumbnailBytes));
            parameters.Add(Parameter(ScreenPointer, synced: false, type: VRCExpressionParameters.ValueType.Int));
            for (int i = 0; i < ScreenLocalValues; i++) parameters.Add(Parameter(ScreenValue(i), synced: false, type: VRCExpressionParameters.ValueType.Float));
            for (int hand = 0; hand < 2; hand++)
            for (int i = 0; i < ScreenTouchPoints.Length; i++) parameters.Add(Parameter(ScreenTouch(hand == 1, i), synced: false, type: VRCExpressionParameters.ValueType.Float));
            for (int i = 0; i < ScreenTouchPoints.Length; i++) parameters.Add(Parameter(ScreenEye(i), synced: false, type: VRCExpressionParameters.ValueType.Float));
            for (int i = 0; i < ScreenTouchPoints.Length; i++) parameters.Add(Parameter(ScreenGaze(i), synced: false, type: VRCExpressionParameters.ValueType.Float));
        }

        /// <summary>
        /// The gaze point's dot: shown for its owner in desktop mode while their screen is open (as the screen opens), so
        /// they see what they point at; nobody else sees it.
        /// </summary>
        private static void GazeDotLayer(AnimatorController controller)
        {
            var machine = Layer(controller, "Gaze dot");
            var hidden = new AnimationClip { name = "Gaze dot hidden" };
            Curve(hidden, GazeDot, typeof(GameObject), "m_IsActive", 0);
            var shown = new AnimationClip { name = "Gaze dot shown" };
            Curve(shown, GazeDot, typeof(GameObject), "m_IsActive", 1);
            var off = HeldPropRig.State(controller, machine, "Off", hidden);
            var on = HeldPropRig.State(controller, machine, "On", shown);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            foreach (var trigger in new[] { ScreenOpen, Menu })
                Transition(off, on, ("IsLocal", AnimatorConditionMode.If, 0), (trigger, AnimatorConditionMode.If, 0), (VRMode, AnimatorConditionMode.Less, 1));
            Transition(on, off, (ScreenOpen, AnimatorConditionMode.IfNot, 0), (Menu, AnimatorConditionMode.IfNot, 0));
            Transition(on, off, (VRMode, AnimatorConditionMode.Greater, 0));
        }

        /// <summary>
        /// The first <see cref="TextPointer"/> of the screen and how many blocks it has, for <paramref name="textSync"/>
        /// values and thumbnails of <paramref name="thumbnailBytes"/>.
        /// </summary>
        internal static (int first, int blocks) ScreenPointers(int textSync, int thumbnailBytes) => (Cells / textSync + 1, (ScreenBytes(thumbnailBytes) + textSync - 1) / textSync);

        /// <summary>Whether everyone else's screen can be written through the text channel: its blocks stay below pointer 254.</summary>
        public static bool ScreenFits(int textSync, int thumbnailBytes)
        {
            var (first, blocks) = ScreenPointers(textSync, thumbnailBytes);
            return first + blocks - 1 < PointerScreenClose;
        }

        // The synced pointer as a float (SyncBlock), only on the clients of everyone else: one state per screen block.
        private static void ScreenBlocks(AnimatorController controller, int textSync, int thumbnailBytes)
        {
            var machine = Layer(controller, "Screen block");
            var (first, blocks) = ScreenPointers(textSync, thumbnailBytes);
            AnimatorState Set(string name, float block) => HeldPropRig.State(controller, machine, name, ParameterClip(name, SyncBlock, block));
            var none = Set("None", 0);
            machine.defaultState = none;
            for (int b = 1; b <= blocks; b++)
                Jump(machine, Set("Block " + b, b), (TextPointer, AnimatorConditionMode.Equals, first + b - 1), ("IsLocal", AnimatorConditionMode.IfNot, 0));
            // The owner's screen has its own pointer; every other pointer writes nothing here.
            Jump(machine, none, ("IsLocal", AnimatorConditionMode.If, 0));
            Jump(machine, none, (TextPointer, AnimatorConditionMode.Less, first));
            Jump(machine, none, (TextPointer, AnimatorConditionMode.Greater, first + blocks - 1));
        }

        // The owner's pointer as a float (LocalBlock): one state per block of 16 values, and clearing.
        private static void ScreenLocalBlocks(AnimatorController controller, int thumbnailBytes)
        {
            var machine = Layer(controller, "Screen local block");
            int blocks = (ScreenBytes(thumbnailBytes) + ScreenLocalValues - 1) / ScreenLocalValues;
            AnimatorState Set(string name, float block) => HeldPropRig.State(controller, machine, name, ParameterClip(name, LocalBlock, block));
            var none = Set("None", 0);
            machine.defaultState = none;
            for (int b = 1; b <= blocks; b++) Jump(machine, Set("Block " + b, b), (ScreenPointer, AnimatorConditionMode.Equals, b));
            Jump(machine, Set("Clear", ScreenLocalClear), (ScreenPointer, AnimatorConditionMode.Equals, ScreenLocalClear));
            Jump(machine, none, (ScreenPointer, AnimatorConditionMode.Less, 1));
            Jump(machine, none, (ScreenPointer, AnimatorConditionMode.Greater, blocks), (ScreenPointer, AnimatorConditionMode.Less, ScreenLocalClear));
        }

        /// <summary>
        /// The screen, closed or open. Open (its owner set <see cref="ScreenOpen"/>; for everyone else, a screen block came
        /// through the text channel, until pointer 254) it shows, frozen where it appeared, and one direct blend tree keeps
        /// its words every update: each word keeps itself; while its block is pointed at it takes the block's two bytes
        /// instead; local pointer 255 clears every word. The bytes (from the values through 1D trees: direct weights can't be
        /// negative) and the blocks are one update late together, the synced bytes <see cref="ValueLag"/> updates later
        /// still (read from the text's late values: values VRChat applied before their pointer never reach the block before).
        /// The owner's local blocks need no lag: Orbiters desktop never changes their pointer and values in one step.
        /// </summary>
        private static void ScreenLayer(AnimatorController controller, int textSync, int thumbnailBytes)
        {
            var machine = Layer(controller, "Screen");
            int words = ScreenWords(thumbnailBytes);
            var plus = new AnimationClip[words];
            var minus = new AnimationClip[words];
            var high = new AnimationClip[words];
            for (int k = 0; k < words; k++)
            {
                plus[k] = WordClip(k, 1); minus[k] = WordClip(k, -1); high[k] = WordClip(k, 256);
            }
            var tree = Hidden(new BlendTree { name = "Screen", blendType = BlendTreeType.Direct });
            var children = new List<ChildMotion>();
            void Add(List<ChildMotion> list, Motion motion, string weight) => list.Add(new ChildMotion { motion = motion, directBlendParameter = weight, timeScale = 1 });

            // Shown, frozen in the world.
            var shown = new AnimationClip { name = "Screen shown" };
            Curve(shown, ScreenObject, typeof(GameObject), "m_IsActive", 1);
            Curve(shown, ScreenObject, typeof(VRCParentConstraint), "FreezeToWorld", 1);
            Add(children, shown, One);
            // The digits.
            for (int i = 0; i < textSync; i++) Add(children, Digit(SyncDigit(i), TextLate(i, ValueLag)), One);
            for (int i = 0; i < ScreenLocalValues; i++) Add(children, Digit(LocalDigit(i), ScreenValue(i)), One);
            // Every word keeps itself.
            var keep = Hidden(new BlendTree { name = "Screen keeps", blendType = BlendTreeType.Direct });
            var keeps = new List<ChildMotion>();
            for (int k = 0; k < words; k++) Add(keeps, plus[k], Word(k));
            keep.children = keeps.ToArray();
            Add(children, keep, One);

            // A block's words take its bytes: two each (low + 256 × high), the word's own value cancelled.
            BlendTree Writes(string name, string blockParameter, int bytesPerBlock, Func<int, string> digit, int blocks, bool clear)
            {
                var writes = Hidden(new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = blockParameter, useAutomaticThresholds = false });
                writes.AddChild(Hidden(new AnimationClip { name = name + " none" }), 0);
                for (int b = 1; b <= blocks; b++)
                {
                    var block = Hidden(new BlendTree { name = name + " " + b, blendType = BlendTreeType.Direct });
                    var parts = new List<ChildMotion>();
                    for (int j = 0; j < bytesPerBlock / 2; j++)
                    {
                        int k = ((b - 1) * bytesPerBlock) / 2 + j;
                        if (k >= words) break;
                        Add(parts, minus[k], Word(k));
                        Add(parts, plus[k], digit(2 * j));
                        Add(parts, high[k], digit(2 * j + 1));
                    }
                    block.children = parts.ToArray();
                    writes.AddChild(block, b);
                }
                if (clear)
                {
                    var all = Hidden(new BlendTree { name = name + " clear", blendType = BlendTreeType.Direct });
                    var parts = new List<ChildMotion>();
                    for (int k = 0; k < words; k++) Add(parts, minus[k], Word(k));
                    all.children = parts.ToArray();
                    writes.AddChild(all, ScreenLocalClear);
                }
                return writes;
            }
            var (_, syncBlocks) = ScreenPointers(textSync, thumbnailBytes);
            Add(children, Writes("Screen writes", SyncBlock, textSync, SyncDigit, syncBlocks, false), One);
            Add(children, Writes("Screen local writes", LocalBlock, ScreenLocalValues, LocalDigit, (ScreenBytes(thumbnailBytes) + ScreenLocalValues - 1) / ScreenLocalValues, true), One);
            tree.children = children.ToArray();

            var hidden = new AnimationClip { name = "Screen hidden" };
            Curve(hidden, ScreenObject, typeof(GameObject), "m_IsActive", 0);
            Curve(hidden, ScreenObject, typeof(VRCParentConstraint), "FreezeToWorld", 0);
            var closed = HeldPropRig.State(controller, machine, "Closed", hidden);
            var open = HeldPropRig.State(controller, machine, "Open", tree);
            closed.writeDefaultValues = true; open.writeDefaultValues = true;
            machine.defaultState = closed;
            var (first, blocks) = ScreenPointers(textSync, thumbnailBytes);
            // The owner's opens when the desktop opens it or at once when the menu's Menu toggle is on (a blank screen
            // then says the desktop does not answer), in desktop mode or with Replace (in VR without it the overlay shows
            // the menu); it closes when both are off.
            foreach (var trigger in new[] { ScreenOpen, Menu })
            {
                Transition(closed, open, ("IsLocal", AnimatorConditionMode.If, 0), (trigger, AnimatorConditionMode.If, 0), (VRMode, AnimatorConditionMode.Less, 1));
                Transition(closed, open, ("IsLocal", AnimatorConditionMode.If, 0), (trigger, AnimatorConditionMode.If, 0), (ScreenReplace, AnimatorConditionMode.If, 0));
            }
            Transition(open, closed, ("IsLocal", AnimatorConditionMode.If, 0), (ScreenOpen, AnimatorConditionMode.IfNot, 0), (Menu, AnimatorConditionMode.IfNot, 0));
            Transition(closed, open, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Greater, first - 1), (TextPointer, AnimatorConditionMode.Less, first + blocks));
            Transition(open, closed, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Equals, PointerScreenClose));
        }

        // A clip adding `value` to a word and to its material property.
        private static AnimationClip WordClip(int k, float value)
        {
            var clip = Hidden(new AnimationClip { name = "Word " + k + " " + value });
            Curve(clip, "", typeof(Animator), Word(k), value);
            Curve(clip, ScreenObject, typeof(MeshRenderer), WordProperty(k), value);
            return clip;
        }

        // The byte (0 to 255) a value carries (Docs/orb.md: b / 127 up to 127, (b − 256) / 127 from 129), through two straight
        // pieces: values −1 to −1/127 are bytes 129 to 255, values 0 to 1 bytes 0 to 127 (no value lies between the two).
        private static BlendTree Digit(string digit, string from)
        {
            var tree = Hidden(new BlendTree { name = digit, blendType = BlendTreeType.Simple1D, blendParameter = from, useAutomaticThresholds = false });
            tree.AddChild(Hidden(ParameterClip(digit + " = 129", digit, 129)), -1);
            tree.AddChild(Hidden(ParameterClip(digit + " = 255", digit, 255)), -1f / 127);
            tree.AddChild(Hidden(ParameterClip(digit + " = 0", digit, 0)), 0);
            tree.AddChild(Hidden(ParameterClip(digit + " = 127", digit, 127)), 1);
            return tree;
        }
    }
}
