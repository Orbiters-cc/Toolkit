using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's projected screen (Orbiters contracts, Docs/orb.md, Screen), on every N4N0: N4N0's menu shown in front of its
    /// owner, where everyone around sees it. Its owner's opens only in VRChat's desktop mode or with the N4N0 card's
    /// "Replace Orbiters overlay" option (<see cref="ScreenReplace"/>); in VR without it, Orbiters - N4N0's SteamVR overlay
    /// shows the menu instead. It looks projected, as N4N0's speech panel, N4N0's beam reaching its corners. It stays where
    /// it is in the world while its owner looks and points around it, and glides back before their eyes once it has been out
    /// of their sight a moment; its handle moves it (<see cref="AddPlacement"/>).
    /// <para>
    /// The desktop writes the screen's memory, 744 bytes (1024 with 128-byte thumbnails, <see cref="ScreenThumbnailBytes"/>)
    /// kept two to an animated animator parameter (each word b0 + 256 × b1, so a cleared word is two zero bytes): for its owner through local parameters (<see cref="ScreenPointer"/>
    /// and 16 <see cref="ScreenValue"/>s a block, as fast as VRChat takes them), for everyone else through the text
    /// panel's synced channel (<see cref="TextPointer"/> past the panel's blocks; 254 closes the screen). In VR each index
    /// finger points at it along its ray, which the screen draws (<see cref="AddPointers"/>), local contact receivers
    /// saying where the fingertip and a point further along the finger are. In desktop mode, where fingers can't reach it, its
    /// owner points by looking: two local contact senders held on the head bone, at the eyes and on the line of sight
    /// before them (<see cref="Gaze"/>, a small dot while the menu is open), and eight more receivers say where they look
    /// from, and a left click presses. No synced bits; the contacts are local and cost nothing in VRChat's performance rank.
    /// </para>
    /// </summary>
    public static partial class OrbInstaller
    {
        public const string ScreenOpen = "N4N0/Screen/Open", ScreenPointer = "N4N0/Screen/Pointer";
        public const string ScreenValuePrefix = "N4N0/Screen/Value", ScreenTouchPrefix = "N4N0/Screen/Touch/";
        /// <summary>
        /// Local, never changed by a menu: on when the N4N0 card's "Replace Orbiters overlay" option is (its default value),
        /// so the screen shows N4N0's menu in VR too. Orbiters - N4N0 reads it to choose between its overlay and the screen.
        /// </summary>
        public const string ScreenReplace = "N4N0/Screen/Replace";
        /// <summary>VRChat's: 1 in VR, 0 in desktop mode.</summary>
        internal const string VRMode = "VRMode";
        /// <summary>
        /// How near the owner's eyes and a point on their line of sight (the gaze point) are to each touch point, as
        /// <see cref="ScreenTouchPrefix"/> for fingers: the hovered point is where the line through both meets the screen.
        /// </summary>
        public const string ScreenEyePrefix = "N4N0/Screen/Eye", ScreenGazePrefix = "N4N0/Screen/Gaze";
        /// <summary>The two senders' collision tags: only the screen's eye and gaze receivers take them.</summary>
        public const string EyeTag = "N4N0Eye", GazeTag = "N4N0Gaze";
        // At the eyes, turned with the head bone; the gaze point on the line of sight, this far before them (nearer than
        // any point of the screen, so its dot shows in front of it); the senders' radius and the dot's.
        internal const string Gaze = Head + "/Gaze", GazePoint = Gaze + "/Point", GazeDot = GazePoint + "/Dot";
        internal const float GazeDistance = .38f, GazeRadius = .01f, GazeDotRadius = .004f;
        /// <summary>
        /// The screen's version (the avatar manifest's <c>orb.screen</c>, and <see cref="ScreenVersionParameter"/>'s default):
        /// 2 colour thumbnails, 3 the six-tile Home with the Music and Light pages, 4 pointing with rays from the fingertips
        /// (<see cref="AddPointing"/>), 5 the seven-tile Home with the States page and the link card (<see cref="AddCard"/>),
        /// 6 the ten-tile Home (Tracking, Suggest features, the versions), New state, the keyboard for names and ideas,
        /// Tracking, Suggest features and the world's video on the Music page; the screen held by a handle its owner grabs.
        /// 7 points along each index finger with contacts (the screen draws the rays), presses with the trigger or a touch,
        /// everyone else's fingertips press too; the screen stays where it is until it is out of its owner's sight
        /// (<see cref="AddPlacement"/>), its handle carries it in a palm, its size is <see cref="ScreenSize"/>.
        /// 8 shows the pictures at full size once they arrived (<see cref="AddPictures"/>). 9 the Music page's timeline (tap or
        /// drag along it to move in the track) and Take back card on the Results and Music pages. 10 the simple Home (YouTube,
        /// 3D capture and More large, fast walk, calls and states under them, what plays at the bottom), More, Previous
        /// instances and Add Orbiters as a friend (pages 23 to 27). 11 More with five cards and the QR code scanner (pages 28
        /// and 29: Orbiters - N4N0 reads VRChat's stream camera while it shows and opens a QR code's web link). 12 shares 3D
        /// captures (pages 30 to 33): the 3D picture page with the last capture, Share publicly, Share to … and History; History
        /// with both shares on each capture; the friends to share with; the warning before an explicit capture goes public.
        /// </summary>
        public const int ScreenVersion = 12;
        /// <summary>
        /// Local, not saved, set by no menu: the screen's version (<see cref="ScreenVersion"/>), which Orbiters - N4N0 reads
        /// to know the pages the screen has. Screens before version 3 lack it.
        /// </summary>
        public const string ScreenVersionParameter = "N4N0/Screen/Version";
        public const int ScreenLocalValues = 16;
        /// <summary><see cref="TextPointer"/>: everyone else's screen closes (and clears).</summary>
        public const int PointerScreenClose = 254;
        /// <summary><see cref="ScreenPointer"/>: the owner's screen memory clears.</summary>
        public const int ScreenLocalClear = 255;
        internal const string ScreenObject = Head + "/Screen";
        // The screen, 16:9.
        internal const float ScreenWidth = .48f, ScreenHeight = .27f;
        // The mesh's margin past the screen's border, where its glow fades out (the shader's).
        internal const float ScreenGlow = .008f;
        // The touch points (orbiters-orb's menu::touch): past the top corners, under the bottom's middle, behind the middle.
        // The eyes', the gaze point's and everyone else's fingertips' receivers reach the far corner of the screen (the
        // owner's fingers' reach further, PointingReach).
        internal const float ScreenTouchRadius = .8f;
        internal static readonly Vector3[] ScreenTouchPoints =
        {
            new Vector3(-ScreenWidth / 2 - .05f, ScreenHeight / 2 + .05f, 0), new Vector3(ScreenWidth / 2 + .05f, ScreenHeight / 2 + .05f, 0),
            new Vector3(0, -ScreenHeight / 2 - .05f, 0), new Vector3(0, 0, .15f),
        };
        // The screen's shared files: made once for every N4N0 that has a screen, and for every N4N0 switched to a thumbnail
        // size its own files lack.
        internal const string ScreenFolder = "Assets/Orbiters/N4N0/Screen " + ScreenRevision;
        // The screen files' revision, the shader's OrbScreen tag: 5 draws the back as a card ("N4N0 menu, look at the other
        // side"), 6 the retro look's own pages and live parts, 7 the projected look and N4N0's beam on a skinned screen,
        // 8 the version 6 pages (22 of them), 9 the rays from the fingertips (a third part, bound to the pointers), 10 the
        // projection's glow as a property (_Glow, a menu theme's screen glow), 11 the full pictures (_Pictures, CRT B), 12
        // their diagnostics (_PictureData, CRT A) and B's heartbeat no longer compared with the screen's clock, 13 the
        // version 9 pages (the Music timeline, Take back card), 14 the version 10 pages (27 of them: the simple Home, More,
        // Previous instances, Add Orbiters as a friend), 15 the version 11 pages (29 of them: More with the QR code
        // scanner, the scanner's page), 16 every one of those pages drawn (the shader's page count follows its layout rows;
        // it had stayed at 22, so pages 23 on, the Home among them, showed empty), 17 the version 12 pages (33 of them: the
        // 3D picture with the last capture, History, Share to …, the explicit warning), 18 the same pages redrawn (a friend
        // chosen in Share to … carries a green check, History's and Share to …'s counts are quiet subtitles).
        internal const string ScreenRevision = "18";
        private const string ScreenShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreen.shader.txt";
        private const string BeamShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbBeam.shader.txt";
        private const string RayShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbRay.shader.txt";
        // Drawn by Orbiters - N4N0 (`orbiters-desktop --orb-menu`) from the menu's layout: the page pictures (a slice per
        // page, the first empty) and the layout table.
        private const string ScreenPagesTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreenPages.png.bytes";
        // The same pages in the retro look (`orbiters-desktop --orb-menu`'s "N4N0 screen pages retro.png").
        private const string ScreenPagesRetroTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreenPagesRetro.png.bytes";
        private const string ScreenLayoutTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbScreenLayout.png.bytes";
        internal const int ScreenPageSlices = 34;

        public static string ScreenValue(int index) => ScreenValuePrefix + index;
        public static string ScreenTouch(bool right, int index) => ScreenTouchPrefix + (right ? "R" : "L") + index;
        public static string ScreenGaze(int index) => ScreenGazePrefix + index;
        public static string ScreenEye(int index) => ScreenEyePrefix + index;

        // The screen's own animator parameters: its words, the bytes the values carry (synced and local), the blocks the two
        // pointers write (one update late, as the local bytes; the synced bytes come from the text's late values,
        // ValueLag updates later still).
        internal const string SyncBlock = "N4N0/Screen/SyncBlock", LocalBlock = "N4N0/Screen/LocalBlock";
        internal static string Word(int k) => "N4N0/Screen/Word" + k;
        internal static string SyncDigit(int i) => "N4N0/Screen/SyncDigit" + i;
        internal static string LocalDigit(int i) => "N4N0/Screen/LocalDigit" + i;
        internal static string WordProperty(int k) => "material._W" + k;

        /// <summary>Whether a placed orb has the screen object.</summary>
        public static bool HasScreen(Transform orb) => orb != null && orb.Find(ScreenObject) != null;

        // ---- The shared files ----

        /// <summary>
        /// The screen's shader (word properties and thumbnail decoder written out), pages, layout, glyphs, mesh and
        /// material for thumbnails of <paramref name="thumbnailBytes"/>, made once in <paramref name="folder"/> (a new orb's
        /// own; the shared <see cref="ScreenFolder"/> for N4N0 made without a screen or switched to another thumbnail size;
        /// tests use their own). Each size has its own shader and material; the mesh is shared. The retro look
        /// (<paramref name="retro"/>, Docs/orb.md, Retro look) is a second material of the same shader beside it.
        /// </summary>
        internal static (Mesh mesh, Material material) ScreenAssets(string folder = ScreenFolder, int thumbnailBytes = DefaultThumbnailBytes, bool retro = false)
        {
            var (mesh, material) = ModernScreenAssets(folder, thumbnailBytes);
            if (!retro) return (mesh, material);
            string path = folder + "/" + ScreenFileName(thumbnailBytes) + " retro.mat";
            var look = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (look != null && look.shader == material.shader) return (mesh, look);
            look = new Material(material) { name = ScreenFileName(thumbnailBytes) + " retro" };
            look.SetFloat(RetroProperty, 1);
            string pagesPath = folder + "/N4N0 screen pages retro.png";
            look.SetTexture("_Pages", AssetDatabase.LoadAssetAtPath<Texture2DArray>(pagesPath) ?? ScreenPages(pagesPath, ScreenPagesRetroTemplate));
            // The phosphor palette, as Orbiters - N4N0's retro overlay paints (Docs/orb.md, Retro look).
            foreach (var (name, colour) in RetroColours) look.SetColor(name, colour);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(look, path);
            AssetDatabase.SaveAssets();
            return (mesh, look);
        }

        private const string RetroProperty = "_Retro";
        // Pictures of N4N0's menu in each look (`orbiters-desktop --orb-menu`'s "N4N0 preview.png" and "N4N0 preview
        // retro.png"), for the Look option on N4N0's card; read once.
        private static readonly string[] LookPictureFiles =
            { "Packages/orbiters.toolkit/Editor/VRChat/OrbPreview.png.bytes", "Packages/orbiters.toolkit/Editor/VRChat/OrbPreviewRetro.png.bytes" };
        private static Texture2D[] lookPictures;

        internal static IReadOnlyList<Texture> LookPictures()
        {
            if (lookPictures != null && lookPictures.All(t => t != null)) return lookPictures;
            lookPictures = LookPictureFiles.Select(path =>
            {
                var file = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                var picture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = System.IO.Path.GetFileNameWithoutExtension(path), hideFlags = HideFlags.HideAndDontSave };
                if (file != null) picture.LoadImage(file.bytes);
                return picture;
            }).ToArray();
            return lookPictures;
        }
        private static readonly (string name, Color32 colour)[] RetroColours =
        {
            ("_Card", new Color32(2, 10, 4, 255)), ("_Text", new Color32(125, 255, 166, 255)), ("_Text2", new Color32(47, 184, 94, 255)),
            ("_Accent", new Color32(43, 227, 106, 255)), ("_Placeholder", new Color32(5, 24, 11, 255)), ("_Dim", new Color32(24, 98, 50, 255)),
            ("_Hot", new Color32(200, 255, 215, 255)),
        };

        /// <summary>Whether a placed orb's screen has the retro look (a green phosphor terminal).</summary>
        public static bool RetroOf(Transform orb)
        {
            var renderer = ScreenRenderer(orb);
            var material = renderer != null ? renderer.sharedMaterial : null;
            return material != null && material.HasProperty(RetroProperty) && material.GetFloat(RetroProperty) > .5f;
        }

        /// <summary>A placed orb's screen renderer (its materials draw the pages, N4N0's beam and the rays), or null.</summary>
        internal static SkinnedMeshRenderer ScreenRenderer(Transform orb)
        {
            var screen = orb != null ? orb.Find(ScreenObject) : null;
            // TryGetComponent: a real null when missing (an older N4N0's screen has a MeshRenderer), not Unity's fake one.
            return screen != null && screen.TryGetComponent(out SkinnedMeshRenderer renderer) ? renderer : null;
        }

        /// <summary>
        /// N4N0's beam towards the screen in <paramref name="folder"/> (made once), in Orbiters' green or the retro
        /// look's phosphor.
        /// </summary>
        internal static Material ScreenBeam(string folder = ScreenFolder, bool retro = false)
        {
            string path = folder + "/N4N0 screen beam" + (retro ? " retro" : "") + ".mat";
            var beam = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (beam != null && beam.shader != null && beam.shader.isSupported) return beam;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(folder + "/N4N0 beam.shader") ?? CopyShader(BeamShaderTemplate, folder + "/N4N0 beam.shader");
            beam = new Material(shader) { name = "N4N0 screen beam" + (retro ? " retro" : "") };
            if (retro) beam.SetColor("_EdgeColor", RetroColours.First(c => c.name == "_Accent").colour);
            // Softer than the speech panel's: the menu is larger and nearer its owner's eyes, and stays open.
            beam.SetFloat("_Beam", .6f);
            // Under the screen, which is drawn over its end.
            beam.renderQueue = (int)RenderQueue.Transparent - 1;
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(beam, path);
            AssetDatabase.SaveAssets();
            return beam;
        }

        /// <summary>The fingertips' rays on the screen in <paramref name="folder"/> (made once), in the look's green.</summary>
        internal static Material ScreenRay(string folder = ScreenFolder, bool retro = false)
        {
            string path = folder + "/N4N0 ray" + (retro ? " retro" : "") + ".mat";
            var ray = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (ray != null && ray.shader != null && ray.shader.isSupported) return ray;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(folder + "/N4N0 ray.shader") ?? CopyShader(RayShaderTemplate, folder + "/N4N0 ray.shader");
            ray = new Material(shader) { name = "N4N0 ray" + (retro ? " retro" : "") };
            if (retro) ray.SetColor("_Color", RetroColours.First(c => c.name == "_Accent").colour);
            ray.SetVector("_Half", new Vector4(ScreenWidth / 2, ScreenHeight / 2, 0, 0));
            // Over the screen and its beam.
            ray.renderQueue = (int)RenderQueue.Transparent + 2;
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(ray, path);
            AssetDatabase.SaveAssets();
            return ray;
        }

        // The materials of a screen renderer: the pages, then the beam and the rays of the same look.
        private static void SetScreenMaterials(SkinnedMeshRenderer renderer, Material pages, bool retro)
        {
            string folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(pages)).Replace('\\', '/');
            renderer.sharedMaterials = new[] { pages, ScreenBeam(folder, retro), ScreenRay(folder, retro) };
        }

        /// <summary>
        /// Gives a placed orb's screen the retro look or Orbiters' own (with Undo): the shared material of its thumbnail size
        /// and look. Nothing else changes: no parameter, no synced bit.
        /// </summary>
        public static void SetLook(Transform orb, bool retro)
        {
            var renderer = ScreenRenderer(orb);
            if (renderer == null) throw new InvalidOperationException(HasScreen(orb) ? OlderScreen : "This N4N0 has no screen: choose its options again.");
            int thumbnailBytes = OptionsOf(orb).thumbnailBytes;
            if (RetroOf(orb) == retro && ScreenCurrent(renderer.sharedMaterial, thumbnailBytes)) return;
            var (_, material) = ScreenAssets(ScreenFolder, thumbnailBytes, retro);
            Undo.RecordObject(renderer, "N4N0 look");
            SetScreenMaterials(renderer, material, retro);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            SetCardMaterials(orb, material, retro, "N4N0 look");
        }

        // The link card draws with the screen's materials: it follows its look and thumbnail size.
        private static void SetCardMaterials(Transform orb, Material pages, bool retro, string undo)
        {
            var card = orb.Find(CardModel);
            if (card == null || !card.TryGetComponent(out SkinnedMeshRenderer renderer)) return;
            Undo.RecordObject(renderer, undo);
            SetScreenMaterials(renderer, pages, retro);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        /// <summary>A screen from before the skinned one (revision 7): the N4N0 it belongs to must be updated as a whole.</summary>
        internal const string OlderScreen = "This N4N0 is older than Orbiters Toolkit: update it in My Avatar first.";

        private static (Mesh mesh, Material material) ModernScreenAssets(string folder, int thumbnailBytes)
        {
            string name = ScreenFileName(thumbnailBytes);
            string materialPath = folder + "/" + name + ".mat", meshPath = folder + "/N4N0 screen mesh.asset";
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
                var pages = AssetDatabase.LoadAssetAtPath<Texture2DArray>(folder + "/N4N0 screen pages.png") ?? ScreenPages(folder + "/N4N0 screen pages.png", ScreenPagesTemplate);
                var layout = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/N4N0 screen layout.png") ?? ScreenLayout(folder + "/N4N0 screen layout.png");
                var glyphs = Atlas(folder);
                material = new Material(shader) { name = name };
                material.SetTexture("_Pages", pages);
                material.SetTexture("_Layout", layout);
                material.SetTexture("_MainTex", glyphs);
                SetPictureTextures(material, folder);
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

        // The screen's files for a thumbnail size: "N4N0 screen" for 72 bytes, "N4N0 screen 128 B" for 128.
        private static string ScreenFileName(int thumbnailBytes) => thumbnailBytes == DefaultThumbnailBytes ? "N4N0 screen" : "N4N0 screen " + thumbnailBytes + " B";

        /// <summary>The name of the screen's shader for thumbnails of <paramref name="thumbnailBytes"/>.</summary>
        internal static string ScreenShaderName(int thumbnailBytes) => "Orbiters/N4N0 Screen (" + thumbnailBytes + " B thumbnails)";

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
            string text = template.text.Replace("Shader \"Orbiters/N4N0 Screen\"", "Shader \"" + ScreenShaderName(thumbnailBytes) + "\"")
                .Replace("//@PROPERTIES", properties.ToString().TrimEnd()).Replace("//@BUFFER", buffer.ToString().TrimEnd()).Replace("//@SUM", sum.ToString())
                .Replace("//@DEFINES", defines).Replace("//@THUMBNAIL", ThumbnailDecoder(thumbnailBytes)).Replace("//@PICTURES", PictureScreenCode());
            System.IO.File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Unity did not import " + path + ".");
            return shader;
        }

        // The pages as a texture array (a slice per page id), sRGB, mipmapped.
        private static Texture2DArray ScreenPages(string path, string template)
        {
            CopyFile(template, path);
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

        /// <summary>
        /// The screen's skinned mesh. Its first part is a quad facing its owner (who looks along +Z), bound to the screen,
        /// with a margin for the glow: its first UV is the page (0, 0 bottom left), past 0 and 1 in the margin. Its second
        /// is N4N0's beam, a pyramid from N4N0 to the screen's corners: the apex at the origin of the second bone (N4N0's
        /// emitter), the corners bound to the screen; its second UV is where a vertex is across its side and along the beam.
        /// Its third is each index finger's ray (bones 3 and 4, the pointers): a strip and a dot whose vertices all sit at
        /// the pointer's origin with its forward as their normal (OrbRay.shader.txt builds them on the screen); their first
        /// UV is where a vertex is on its strip or dot, their second whether it is the dot. Every bind pose is the identity,
        /// so one mesh serves every screen wherever its N4N0 floats and its owner's hands are.
        /// </summary>
        private static Mesh ScreenMesh()
        {
            float x = ScreenWidth / 2, y = ScreenHeight / 2, u = ScreenGlow / ScreenWidth, v = ScreenGlow / ScreenHeight;
            var vertices = new List<Vector3>(); var page = new List<Vector2>(); var beam = new List<Vector2>(); var weights = new List<BoneWeight>();
            void Add(Vector3 position, Vector2 onPage, Vector2 along, int bone)
            {
                vertices.Add(position); page.Add(onPage); beam.Add(along); weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            }
            float gx = x + ScreenGlow, gy = y + ScreenGlow;
            Add(new Vector3(-gx, -gy, 0), new Vector2(-u, -v), Vector2.zero, 0); Add(new Vector3(gx, -gy, 0), new Vector2(1 + u, -v), Vector2.zero, 0);
            Add(new Vector3(-gx, gy, 0), new Vector2(-u, 1 + v), Vector2.zero, 0); Add(new Vector3(gx, gy, 0), new Vector2(1 + u, 1 + v), Vector2.zero, 0);
            var quad = new List<int> { 2, 3, 1, 2, 1, 0 };
            var ring = new[] { new Vector3(-x, y, 0), new Vector3(x, y, 0), new Vector3(x, -y, 0), new Vector3(-x, -y, 0) };
            var sides = new List<int>();
            for (int k = 0; k < 4; k++)
            {
                sides.Add(vertices.Count); Add(Vector3.zero, Vector2.zero, new Vector2(.5f, 0), 1);
                sides.Add(vertices.Count); Add(ring[k], Vector2.zero, new Vector2(0, 1), 0);
                sides.Add(vertices.Count); Add(ring[(k + 1) % 4], Vector2.zero, new Vector2(1, 1), 0);
            }
            var normals = Enumerable.Repeat(Vector3.back, vertices.Count).ToList();
            var rays = new List<int>();
            for (int hand = 0; hand < 2; hand++)
            {
                int first = vertices.Count;
                // The strip (along, across), then the dot (its corners).
                foreach (var at in new[] { new Vector2(0, -1), new Vector2(0, 1), new Vector2(1, -1), new Vector2(1, 1) })
                {
                    Add(Vector3.zero, at, Vector2.zero, 2 + hand); normals.Add(Vector3.forward);
                }
                foreach (var at in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
                {
                    Add(Vector3.zero, at, Vector2.right, 2 + hand); normals.Add(Vector3.forward);
                }
                rays.AddRange(new[] { first, first + 1, first + 2, first + 2, first + 1, first + 3 });
                rays.AddRange(new[] { first + 4, first + 5, first + 6, first + 6, first + 5, first + 7 });
            }
            var mesh = new Mesh { name = "N4N0 screen" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, page); mesh.SetUVs(1, beam);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 3;
            mesh.SetTriangles(quad, 0); mesh.SetTriangles(sides, 1); mesh.SetTriangles(rays, 2);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- On a placed orb ----

        /// <summary>
        /// Adds the screen to an orb as its prefab is built: before its owner's eyes, hidden until the animator shows it, held
        /// to its glide (<see cref="AddPlacement"/>), with N4N0's beam, the fingertips' rays and pointers, its receivers and
        /// handle, for thumbnails of <paramref name="thumbnailBytes"/>.
        /// </summary>
        internal static Transform AddScreen(Transform orb, string folder = ScreenFolder, int thumbnailBytes = DefaultThumbnailBytes)
        {
            var existing = orb.Find(ScreenObject);
            if (existing != null) return existing;
            var head = orb.Find(Head);
            var emitter = orb.Find(Emitter);
            if (head == null || emitter == null || orb.Find(Gaze) == null) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            var (mesh, material) = ScreenAssets(folder, thumbnailBytes);
            var pointers = AddPointers(orb);
            var glide = AddPlacement(orb);

            var screen = new GameObject("Screen");
            screen.transform.SetParent(head, false);
            screen.SetActive(false);
            var renderer = screen.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { screen.transform, emitter, pointers[0], pointers[1] }; renderer.rootBone = screen.transform; renderer.quality = SkinQuality.Bone1;
            SetScreenMaterials(renderer, material, false);
            // The screen with its glow, N4N0 on its tether beside the head wherever it sways, the hands pointing at it.
            renderer.localBounds = new Bounds(new Vector3(0, -.1f, -.3f), Vector3.one * 1.8f);
            Quiet(renderer);
            // Held to the glide (Bind puts it before the eyes).
            var hold = screen.AddComponent<VRCParentConstraint>();
            hold.Sources.Add(new VRCConstraintSource(glide, 1));
            hold.Locked = true; hold.IsActive = true;
            AddPointingReceivers(screen.transform);
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
            AddHandle(orb, screen.transform, folder);
            AddCard(orb, mesh, material);
            AddPictures(orb, folder);
            return screen.transform;
        }

        /// <summary>
        /// The screen of a placed orb draws thumbnails of <paramref name="thumbnailBytes"/> (with Undo): a screen whose
        /// shader is for another size (or from before colour thumbnails) takes the shared material for this one.
        /// </summary>
        internal static void SetScreenThumbnails(Transform orb, int thumbnailBytes)
        {
            var renderer = ScreenRenderer(orb);
            if (renderer == null) return;
            if (ScreenCurrent(renderer.sharedMaterial, thumbnailBytes)) return;
            bool retro = RetroOf(orb);
            // A menu theme or language drawn for this screen: drawn again for the new files.
            var drawn = ItemParts.Of(orb.gameObject).Where(p => p.Part.renderer == MenuRenderer && p.Current != null && p.Current.Recorded).Select(p => p.Current).ToList();
            var (_, material) = ScreenAssets(ScreenFolder, thumbnailBytes, retro);
            Undo.RecordObject(renderer, "N4N0 thumbnail size");
            SetScreenMaterials(renderer, material, retro);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            SetCardMaterials(orb, material, retro, "N4N0 thumbnail size");
            DrawPartsAgain(orb, drawn);
        }

        /// <summary>
        /// Whether a screen material draws this version's pages (a slice each: older screens have fewer) with thumbnails of
        /// <paramref name="thumbnailBytes"/>, from this revision's shader. An older one is swapped for the current files when
        /// N4N0's options change, with the controller saying its version.
        /// </summary>
        internal static bool ScreenCurrent(Material material, int thumbnailBytes) =>
            material != null && material.shader != null && material.shader.name == ScreenShaderName(thumbnailBytes) &&
            material.GetTag("OrbScreen", false) == ScreenRevision &&
            material.HasProperty("_Pages") && material.GetTexture("_Pages") is Texture2DArray pages && pages.depth == ScreenPageSlices;

        // ---- The animator ----

        /// <summary>
        /// The screen's parameters, layers and expression parameters (all local), added to N4N0's controller;
        /// <paramref name="replace"/> is <see cref="ScreenReplace"/>'s default, <paramref name="thumbnailBytes"/>
        /// <see cref="ScreenThumbnailBytes"/>'s (the memory's size follows).
        /// </summary>
        private static void ScreenController(AnimatorController controller, int textSync, List<VRCExpressionParameters.Parameter> parameters, bool replace, int thumbnailBytes)
        {
            controller.AddParameter(ScreenOpen, AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenReplace, type = AnimatorControllerParameterType.Bool, defaultBool = replace });
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenThumbnailBytes, type = AnimatorControllerParameterType.Int, defaultInt = thumbnailBytes });
            controller.AddParameter(new AnimatorControllerParameter { name = ScreenVersionParameter, type = AnimatorControllerParameterType.Int, defaultInt = ScreenVersion });
            controller.AddParameter(VRMode, AnimatorControllerParameterType.Int);
            controller.AddParameter(ScreenPointer, AnimatorControllerParameterType.Int);
            for (int i = 0; i < ScreenLocalValues; i++) controller.AddParameter(ScreenValue(i), AnimatorControllerParameterType.Float);
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
            PlacementController(controller, parameters);
            PointingController(controller, parameters);
            CardController(controller, parameters, textSync, thumbnailBytes);
            PictureController(controller, parameters, textSync, thumbnailBytes);

            parameters.Add(Parameter(ScreenOpen, synced: false));
            // Not saved: a new upload's option always holds.
            parameters.Add(Parameter(ScreenReplace, synced: false, defaultValue: replace ? 1 : 0));
            parameters.Add(Parameter(ScreenThumbnailBytes, synced: false, type: VRCExpressionParameters.ValueType.Int, defaultValue: thumbnailBytes));
            parameters.Add(Parameter(ScreenVersionParameter, synced: false, type: VRCExpressionParameters.ValueType.Int, defaultValue: ScreenVersion));
            parameters.Add(Parameter(ScreenPointer, synced: false, type: VRCExpressionParameters.ValueType.Int));
            for (int i = 0; i < ScreenLocalValues; i++) parameters.Add(Parameter(ScreenValue(i), synced: false, type: VRCExpressionParameters.ValueType.Float));
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

        /// <summary>
        /// Whether everyone else's screen can be written through the text channel: its blocks stay below pointer 253 (the
        /// dropped card's; 254 closes it).
        /// </summary>
        public static bool ScreenFits(int textSync, int thumbnailBytes)
        {
            var (first, blocks) = ScreenPointers(textSync, thumbnailBytes);
            return first + blocks - 1 < PointerCardDropped;
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
        /// The owner's local blocks need no lag: Orbiters - N4N0 never changes their pointer and values in one step.
        /// The owner's Menu toggle has the last word: turning it off closes their screen and clears
        /// <see cref="ScreenOpen"/> (a parameter driver), whatever Orbiters - N4N0 last set it to. (From version 7 the link
        /// card is an object of its own: the screen is never the card.)
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

            // Shown.
            var shown = new AnimationClip { name = "Screen shown" };
            Curve(shown, ScreenObject, typeof(GameObject), "m_IsActive", 1);
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
            var closed = HeldPropRig.State(controller, machine, "Closed", hidden);
            var open = HeldPropRig.State(controller, machine, "Open", tree);
            // The owner's screen while the menu's Menu toggle is on, and after it is turned off: turning Menu off always
            // closes it and clears N4N0/Screen/Open, so a screen Orbiters - N4N0 opened closes too when the app no longer
            // answers (it quit or froze with the screen open).
            var openByMenu = HeldPropRig.State(controller, machine, "Open by menu", tree);
            var closedByMenu = HeldPropRig.State(controller, machine, "Closed by menu", hidden);
            Drive(closedByMenu, (ScreenOpen, false));
            foreach (var state in new[] { closed, open, openByMenu, closedByMenu }) state.writeDefaultValues = true;
            machine.defaultState = closed;
            var (first, blocks) = ScreenPointers(textSync, thumbnailBytes);
            // The owner's opens at once when the Menu toggle is on (a blank screen then says the desktop does not answer)
            // or when the desktop opens it, in desktop mode or with Replace (in VR without it the overlay shows the menu).
            var modes = new[] { (VRMode, AnimatorConditionMode.Less, 1f), (ScreenReplace, AnimatorConditionMode.If, 0f) };
            foreach (var mode in modes)
            {
                Transition(closed, openByMenu, ("IsLocal", AnimatorConditionMode.If, 0), (Menu, AnimatorConditionMode.If, 0), mode);
                Transition(closedByMenu, openByMenu, ("IsLocal", AnimatorConditionMode.If, 0), (Menu, AnimatorConditionMode.If, 0), mode);
                Transition(closed, open, ("IsLocal", AnimatorConditionMode.If, 0), (ScreenOpen, AnimatorConditionMode.If, 0), (Menu, AnimatorConditionMode.IfNot, 0), mode);
            }
            // One the desktop opened closes when it closes it, or with the Menu toggle (on, then off).
            Transition(open, openByMenu, ("IsLocal", AnimatorConditionMode.If, 0), (Menu, AnimatorConditionMode.If, 0));
            Transition(open, closed, ("IsLocal", AnimatorConditionMode.If, 0), (ScreenOpen, AnimatorConditionMode.IfNot, 0), (Menu, AnimatorConditionMode.IfNot, 0));
            Transition(openByMenu, closedByMenu, ("IsLocal", AnimatorConditionMode.If, 0), (Menu, AnimatorConditionMode.IfNot, 0));
            Transition(closedByMenu, closed, (ScreenOpen, AnimatorConditionMode.IfNot, 0));
            Transition(closed, open, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Greater, first - 1), (TextPointer, AnimatorConditionMode.Less, first + blocks));
            Transition(open, closed, ("IsLocal", AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Equals, PointerScreenClose));
        }

        // A clip adding `value` to a word and to its material property.
        private static AnimationClip WordClip(int k, float value)
        {
            var clip = Hidden(new AnimationClip { name = "Word " + k + " " + value });
            Curve(clip, "", typeof(Animator), Word(k), value);
            Curve(clip, ScreenObject, typeof(SkinnedMeshRenderer), WordProperty(k), value);
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
