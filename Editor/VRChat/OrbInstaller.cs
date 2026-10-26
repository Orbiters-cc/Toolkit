using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>What N4N0 is doing (<see cref="OrbInstaller.State"/>); any other value shows as <see cref="Idle"/>.</summary>
    public enum OrbState { Idle, Listening, Thinking, Talking, Sleeping, Happy, Annoyed, Surprised }

    /// <summary>
    /// N4N0, the Orbiters companion (Orbiters contracts, Docs/orb.md): a small glowing orb with eyes that floats beside its
    /// owner's head, lazily and with a spring (a PhysBone chain anyone can grab and let spring back), and the text panel it
    /// projects in front of its owner, which everyone around reads. Orbiters - N4N0 drives it over OSC through global
    /// parameters: what N4N0 is doing (<see cref="State"/>: its eyes, glow, a ring, the projector beam) and the panel's text,
    /// a block of cells at a time (KillFrenzy Avatar Text's protocol under N4N0's names). Its owner talks to it by holding the
    /// menu's Talk button or touching it. The panel sits low and to N4N0's side of its owner's view and never in front of
    /// their face; seen from further away it grows away from the face. Native avatar components only, its two shaders and
    /// glyph atlas copied into its folder and no Orbiters script: <see cref="Bind"/> ties it to the avatar's head and eyes.
    /// Quest and other phones show a mobile N4N0 instead (<see cref="Mobile"/>), which their builds keep.
    /// </summary>
    public static partial class OrbInstaller
    {
        public const string MenuPath = "N4N0";
        public const string PrefabName = "N4N0.prefab";
        // The contract's avatar parameters: global, so they keep these names in the avatar.
        public const string Visible = "N4N0/Visible", State = "N4N0/State", Talk = "N4N0/Talk", Touch = "N4N0/Touch";
        public const string TextVisible = "N4N0/Text/Visible", TextPointer = "N4N0/Text/Pointer", TextSyncPrefix = "N4N0/Text/Sync";
        public const int Columns = 32, Rows = 4, Cells = Columns * Rows, DefaultTextSync = 8;
        /// <summary><see cref="TextPointer"/>: write nothing; clear every cell.</summary>
        public const int PointerNone = 0, PointerClear = 255;
        /// <summary>The numbers of text sync values N4N0 may have.</summary>
        public static readonly IReadOnlyList<int> TextSyncCounts = new[] { 4, 8, 16 };

        internal const string Shown = "Shown", Head = Shown + "/Head", Tether = Head + "/Tether", Ball = Tether + "/1/2/Ball";
        internal const string Hop = Ball + "/Hop", Body = Hop + "/Body", Emitter = Hop + "/Emitter", Toucher = Ball + "/Touch", Panel = Head + "/Panel";
        internal static readonly Color Colour = new Color(0, 218 / 255f, 109 / 255f);

        // The layout is for an avatar whose eyes are 1.6 m up; Bind scales it with the avatar's eye height.
        internal const float ReferenceEyes = 1.6f;
        // N4N0's radius; each of the tether's two links (N4N0 rests on its tip, above its root: longer links let it lag further).
        internal const float Radius = .045f, Link = .07f, TouchRadius = .07f;
        // A cell of the panel (a glyph twice as tall as wide), the plate's margin around the text and the glow past it
        // (the text shader's Margin).
        internal const float CellWidth = .009f, CellHeight = .018f, PlatePad = .006f, PlateGlow = .004f;
        internal const float PageWidth = Columns * CellWidth + 2 * PlatePad, PageHeight = Rows * CellHeight + 2 * PlatePad;
        // From the eyes (right, up, forward, turned with the avatar): N4N0 floats beside the head, a little before the
        // ear, out of the way between the face and the people around; the panel's corner nearest the face is low and to
        // the right of its owner's view, out of its middle, and below the face as people in front see it.
        internal static readonly Vector3 OrbFromEyes = new Vector3(.26f, .02f, .12f), PanelFromEyes = new Vector3(.06f, -.18f, .5f);
        // Until Bind: the head bone 1.51 m up, the eyes 9 cm above it and 7 cm before it.
        private static readonly Vector3 DefaultHead = new Vector3(0, 1.51f, 0), DefaultEyes = new Vector3(0, .09f, .07f);

        private const string ShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/Orb.shader.txt";
        private const string TextShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbText.shader.txt";
        // Drawn by the contracts' Tools~/OrbTextAtlas from the glyph table: 2048 × 4096, 16 × 16 tiles of 128 × 256.
        private const string AtlasTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbText.png.bytes";

        /// <summary><see cref="TextSyncPrefix"/> and <paramref name="index"/>: "N4N0/Text/Sync3".</summary>
        public static string TextSync(int index) => TextSyncPrefix + index;

        /// <summary>The N4N0s below <paramref name="root"/>, known by their touch contact.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => r.parameter == Touch).Select(r => RootOf(r.transform)).Where(o => o != null).ToList();

        private static Transform RootOf(Transform touch)
        {
            for (var root = touch.parent; root != null; root = root.parent)
                if (root.Find(Toucher) == touch) return root;
            return null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var orb in FindAll(root)) Bind(orb, avatar);
        }, root => FindAll(root).Any());

        /// <summary>
        /// Creates the N4N0 prefab with its controller, menu, parameters, shaders, glyph atlas, meshes and materials in
        /// <paramref name="folder"/>; <paramref name="textSync"/> (4, 8 or 16) is its number of text sync values: more
        /// fill the panel sooner and cost more of the avatar's synced bits (1 + 8 + 1 + 8 + 8 per value);
        /// <paramref name="thumbnailBytes"/> (72 or 128, 128 with 8 text sync values or more) the size of each thumbnail
        /// its screen keeps.
        /// </summary>
        public static string CreatePrefab(string folder, int textSync = DefaultTextSync, int thumbnailBytes = DefaultThumbnailBytes)
        {
            if (!TextSyncCounts.Contains(textSync)) throw new ArgumentOutOfRangeException(nameof(textSync), "N4N0 has 4, 8 or 16 text sync values.");
            if (!OfferedThumbnailBytes.Contains(thumbnailBytes) || !ScreenFits(textSync, thumbnailBytes))
                throw new ArgumentOutOfRangeException(nameof(thumbnailBytes), "N4N0's thumbnails are 72 or 128 bytes, 128 with 8 text sync values or more.");
            return SavePrefab(folder, PrefabName, "N4N0", "N4N0", root => Build(root, folder, textSync, thumbnailBytes));
        }

        private static void Build(GameObject root, string folder, int textSync, int thumbnailBytes)
        {
            // Everything turns off with N4N0/Visible.
            var shown = Child(root.transform, Shown);
            // On the head bone, turned like the avatar (sources set by Bind), and as large as the avatar's eyes are high.
            var head = Child(shown, "Head");
            head.localPosition = DefaultHead;
            var follow = head.gameObject.AddComponent<VRCPositionConstraint>();
            follow.Sources.Add(new VRCConstraintSource(null, 1));
            follow.IsActive = true; follow.Locked = true;
            var level = head.gameObject.AddComponent<VRCRotationConstraint>();
            level.Sources.Add(new VRCConstraintSource(null, 1));
            level.IsActive = true; level.Locked = true;

            // The tether's root stays with the head; its two links and N4N0 on their tip lag behind and drift back, smoothly
            // (a weak pull and a soft spring: it barely overshoots), whatever moves the head.
            var tether = Child(head, "Tether");
            var link = Child(tether, "1"); link.localPosition = Vector3.up * Link;
            var tip = Child(link, "2"); tip.localPosition = Vector3.up * Link;
            var ball = Child(tip, "Ball");
            var hop = Child(ball, "Hop");
            var emitter = Child(hop, "Emitter");
            var panel = Child(head, "Panel");
            Layout(tether, panel, DefaultEyes);

            var phys = tether.gameObject.AddComponent<VRCPhysBone>();
            phys.rootTransform = tether; phys.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
            phys.pull = .09f; phys.spring = .4f; phys.gravity = 0; phys.gravityFalloff = 0;
            phys.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion; phys.immobile = .05f;
            // Only the tip, where N4N0 is, can be grabbed; pulled, the tether stretches and springs back.
            phys.radius = Radius; phys.radiusCurve = AnimationCurve.Constant(0, .49f, 0);
            phys.radiusCurve.AddKey(new Keyframe(.5f, 1)); phys.radiusCurve.AddKey(new Keyframe(1, 1));
            phys.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            phys.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True; phys.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            phys.grabMovement = .7f; phys.snapToHand = false; phys.maxStretch = 1.5f; phys.resetWhenDisabled = true;
            // What N4N0 holds is no part of the chain.
            phys.ignoreTransforms = new List<Transform> { ball };

            TouchReceiver(Child(ball, "Touch"));

            var shader = CopyShader(ShaderTemplate, folder + "/N4N0.shader");
            var body = Child(hop, "Body");
            body.localScale = Vector3.one * Radius;
            body.gameObject.AddComponent<MeshFilter>().sharedMesh = OrbMesh(folder);
            var bodyRenderer = body.gameObject.AddComponent<MeshRenderer>();
            bodyRenderer.sharedMaterials = new[] { OrbMaterial(folder, shader, "N4N0 core", false), OrbMaterial(folder, shader, "N4N0 glow", true) };
            Quiet(bodyRenderer);

            // Desktop mode's pointer (the screen's eye and gaze receivers find it): the owner's eyes and a point on their
            // line of sight, held to the head bone (sources set by Bind), two local contacts that cost nothing in VRChat's
            // performance rank, and the dot that shows where they look.
            var gaze = Child(head, "Gaze");
            gaze.localPosition = DefaultEyes;
            var sight = gaze.gameObject.AddComponent<VRCParentConstraint>();
            sight.Sources.Add(new VRCConstraintSource(null, 1));
            sight.IsActive = true; sight.Locked = true;
            var point = Child(gaze, "Point");
            point.localPosition = Vector3.forward * GazeDistance;
            foreach (var (at, tag) in new[] { (gaze, EyeTag), (point, GazeTag) })
            {
                var sender = at.gameObject.AddComponent<VRCContactSender>();
                sender.shapeType = ContactBase.ShapeType.Sphere; sender.radius = GazeRadius; sender.localOnly = true;
                sender.collisionTags.Add(tag);
            }
            var dot = Child(point, "Dot");
            dot.localScale = Vector3.one * GazeDotRadius;
            dot.gameObject.AddComponent<MeshFilter>().sharedMesh = body.GetComponent<MeshFilter>().sharedMesh;
            var dotRenderer = dot.gameObject.AddComponent<MeshRenderer>();
            // A plain dot, not N4N0's face.
            dotRenderer.sharedMaterial = PointerMaterial(folder, false);
            Quiet(dotRenderer);
            dot.gameObject.SetActive(false);

            // The panel and its beam are one skinned mesh: the plate and the beam's far end on the panel, its near end on
            // N4N0, wherever the tether takes it.
            var textShader = CopyShader(TextShaderTemplate, folder + "/N4N0 text.shader");
            var screen = panel.gameObject.AddComponent<SkinnedMeshRenderer>();
            screen.sharedMesh = PanelMesh(folder, panel, emitter);
            screen.bones = new[] { panel, emitter }; screen.rootBone = panel; screen.quality = SkinQuality.Bone1;
            screen.sharedMaterials = new[] { TextMaterial(folder, textShader, "N4N0 text", Atlas(folder), false), TextMaterial(folder, textShader, "N4N0 beam", null, true) };
            // The plate grown twice for far viewers, the beam and N4N0 on its tether, wherever they sway.
            screen.localBounds = new Bounds(new Vector3(.15f, 0, -.1f), Vector3.one * 1.4f);
            Quiet(screen);

            BuildController(root, folder, textSync, thumbnailBytes: thumbnailBytes);
            // Its files in N4N0's folder, which a gallery package carries.
            AddScreen(root.transform, folder, thumbnailBytes);
            panel.gameObject.SetActive(false);
            // Quest's N4N0, which mobile builds keep instead (OrbInstaller.Mobile).
            AddMobile(root.transform, folder);
            // The capture kit and camera rig Orbiters - N4N0 records with (OrbInstaller.Capture).
            AddCapture(root.transform, folder);
            AddParts(root.transform, folder);
        }

        /// <summary>
        /// Places the tether (so N4N0 rests on its tip) and the panel from where the eyes are in the head object's space.
        /// The panel faces ahead, upright: a plain rectangle for its owner looking ahead (its rows level, slightly
        /// foreshortened when looked at) and for the people in front.
        /// </summary>
        private static void Layout(Transform tether, Transform panel, Vector3 eyes)
        {
            tether.localPosition = eyes + OrbFromEyes - Vector3.up * 2 * Link;
            panel.localPosition = eyes + PanelFromEyes;
            panel.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// Fits a placed orb to <paramref name="avatar"/>: it follows the head bone, turned like the avatar, sized by how
        /// high the avatar's eyes are, N4N0 beside the eyes and the panel below them. Recorded with Undo; binding again
        /// (another avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform orb, Transform avatar)
        {
            if (orb == null) throw new ArgumentNullException(nameof(orb));
            var animator = Humanoid(avatar, "orb");
            var head = orb.Find(Head);
            var tether = orb.Find(Tether);
            var panel = orb.Find(Panel);
            var gaze = orb.Find(Gaze);
            var follow = head != null ? head.GetComponent<VRCPositionConstraint>() : null;
            var level = head != null ? head.GetComponent<VRCRotationConstraint>() : null;
            var sight = gaze != null ? gaze.GetComponent<VRCParentConstraint>() : null;
            if (follow == null || level == null || follow.Sources.Count < 1 || level.Sources.Count < 1 || tether == null || panel == null ||
                sight == null || sight.Sources.Count < 1)
                throw new InvalidOperationException("This N4N0 was modified: add it again.");
            var bone = animator.GetBoneTransform(HumanBodyBones.Head) ?? animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Hips);
            var eyes = Eyes(avatar, bone);
            float size = Mathf.Clamp(Vector3.Dot(eyes - avatar.position, avatar.up) / ReferenceEyes, .3f, 3);
            Undo.RecordObjects(new Object[] { head, follow, level, tether, panel, gaze, sight }, "Fit orb");
            head.SetPositionAndRotation(bone.position, avatar.rotation);
            float parent = head.parent != null ? Mathf.Abs(head.parent.lossyScale.x) : 1;
            head.localScale = Vector3.one * (size / Mathf.Max(parent, 1e-4f));
            follow.Sources[0] = new VRCConstraintSource(bone, 1, Vector3.zero, Vector3.zero);
            level.Sources[0] = new VRCConstraintSource(avatar, 1, Vector3.zero, Vector3.zero);
            Layout(tether, panel, head.InverseTransformPoint(eyes));
            // The eyes and the gaze point before them turn with the head bone (in desktop mode, with where its owner
            // looks), looking ahead at rest. Offsets are in the head bone's axes, unscaled.
            gaze.SetPositionAndRotation(eyes, avatar.rotation);
            var turn = Quaternion.Inverse(bone.rotation);
            sight.Sources[0] = new VRCConstraintSource(bone, 1, turn * (gaze.position - bone.position), (turn * gaze.rotation).eulerAngles);
            FitPlacement(orb, avatar, animator);
            BindPointers(orb, avatar, animator, size);
            BindCardHands(orb, animator, size);
            FitLight(orb);
            BindMobile(orb, avatar, bone, eyes, size);
            EditorUtility.SetDirty(follow); EditorUtility.SetDirty(level); EditorUtility.SetDirty(sight);
        }

        // Where the avatar sees from: its view position, else a little above and before the head bone.
        private static Vector3 Eyes(Transform avatar, Transform head)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null) return avatar.TransformPoint(descriptor.ViewPosition);
            return head.position + avatar.rotation * DefaultEyes * Mathf.Abs(avatar.lossyScale.y);
        }

        // ---- Assets ----

        private static Material OrbMaterial(string folder, Shader shader, string name, bool halo)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", Colour);
            material.SetFloat("_Halo", halo ? 1 : 0);
            if (halo)
            {
                material.SetFloat("_DstBlend", (float)BlendMode.One); material.SetFloat("_ZWrite", 0);
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetOverrideTag("RenderType", "Transparent");
                // Viewers who hide custom shaders would see a solid ball where the halo is.
                material.SetOverrideTag("VRCFallback", "Hidden");
            }
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static Material TextMaterial(string folder, Shader shader, string name, Texture atlas, bool beam)
        {
            var material = new Material(shader) { name = name };
            material.SetVector("_Cell", new Vector4(CellWidth, CellHeight, 0, 0));
            material.SetFloat("_Pad", PlatePad);
            material.SetFloat("_Part", beam ? 1 : 0);
            if (atlas != null) material.SetTexture("_MainTex", atlas);
            // The plate is drawn over the beam's end.
            material.SetFloat("_SrcBlend", (float)(beam ? BlendMode.One : BlendMode.SrcAlpha));
            material.SetFloat("_DstBlend", (float)(beam ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            material.renderQueue = (int)RenderQueue.Transparent + (beam ? 0 : 1);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        // The glyph atlas, at most 2048 on its longest side, alpha (the glyphs and their outline) as transparency, mipmaps.
        // Its colour is the glyphs' coverage, not a colour: read linearly.
        private static Texture2D Atlas(string folder)
        {
            string path = folder + "/N4N0 text.png";
            CopyFile(AtlasTemplate, path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true; importer.maxTextureSize = 2048; importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 4;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // The core (a unit sphere) and its halo (1.7 times as wide), one submesh each.
        private static Mesh OrbMesh(string folder)
        {
            var vertices = new List<Vector3>();
            var core = Sphere(vertices, 1, 28, 18);
            var halo = Sphere(vertices, 1.7f, 18, 12);
            var mesh = new Mesh { name = "N4N0" };
            mesh.SetVertices(vertices); mesh.SetNormals(vertices.Select(v => v.normalized).ToList());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(core, 0); mesh.SetTriangles(halo, 1);
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/N4N0 mesh.asset");
            return mesh;
        }

        // A latitude-longitude sphere added to vertices; its triangles face outwards.
        private static List<int> Sphere(List<Vector3> vertices, float radius, int segments, int rings)
        {
            int first = vertices.Count;
            for (int r = 0; r <= rings; r++)
            {
                float polar = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float around = 2 * Mathf.PI * s / segments;
                    vertices.Add(radius * new Vector3(Mathf.Sin(polar) * Mathf.Cos(around), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(around)));
                }
            }
            var triangles = new List<int>();
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = first + r * (segments + 1) + s, b = a + segments + 1;
                triangles.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
            }
            return triangles;
        }

        /// <summary>
        /// The panel's mesh, made for a full page (the text shader shrinks it to the text): the plate (with the margin its glow
        /// takes) facing the owner, and the beam, a pyramid from N4N0's middle to the plate's corners. A plate vertex's
        /// first UV is its corner; a beam vertex's second UV is where it is across its side and along the beam.
        /// </summary>
        private static Mesh PanelMesh(string folder, Transform panel, Transform emitter)
        {
            var vertices = new List<Vector3>(); var corners = new List<Vector2>(); var beam = new List<Vector2>(); var weights = new List<BoneWeight>();
            void Add(Vector3 position, Vector2 corner, Vector2 along, int bone)
            {
                vertices.Add(position); corners.Add(corner); beam.Add(along); weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            }
            Vector3 OnPage(Vector2 corner, float margin) =>
                new Vector3(corner.x * (PageWidth + 2 * margin) - margin, (corner.y - 1) * (PageHeight + 2 * margin) + margin, 0);
            // Bottom left, bottom right, top left, top right; seen by the owner (looking along +Z), top left, top right,
            // bottom right turns clockwise: the front.
            foreach (var corner in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) })
                Add(OnPage(corner, PlateGlow), corner, Vector2.zero, 0);
            var plate = new List<int> { 2, 3, 1, 2, 1, 0 };
            var orb = panel.InverseTransformPoint(emitter.position);
            var ring = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
            var sides = new List<int>();
            for (int k = 0; k < 4; k++)
            {
                sides.Add(vertices.Count); Add(orb, Vector2.zero, new Vector2(.5f, 0), 1);
                sides.Add(vertices.Count); Add(OnPage(ring[k], 0), ring[k], new Vector2(0, 1), 0);
                sides.Add(vertices.Count); Add(OnPage(ring[(k + 1) % 4], 0), ring[(k + 1) % 4], new Vector2(1, 1), 0);
            }
            var mesh = new Mesh { name = "N4N0 panel" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, corners); mesh.SetUVs(1, beam);
            mesh.SetNormals(vertices.Select(_ => Vector3.back).ToList());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(plate, 0); mesh.SetTriangles(sides, 1);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity, emitter.worldToLocalMatrix * panel.localToWorldMatrix };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/N4N0 panel mesh.asset");
            return mesh;
        }
    }
}
