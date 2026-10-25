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
    /// N4N0 carries a second N4N0 beside the PC one (<see cref="Mobile"/>, off in the editor): one skinned orb of 384
    /// triangles with one VRChat/Mobile/Toon Standard material, its face taken from a picture of N4N0's faces (the animator
    /// moves the texture's offset with <see cref="State"/>), tinted in the Light option's colour, beside its owner's head
    /// as the PC N4N0 is (without its floaty tether: a PhysBone costs a mobile performance rank) and touched as it is. Its
    /// owner sees it look at them; everyone else sees it look ahead. A build step (<see cref="OrbPlatformHook"/>) keeps
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
        internal const string MobileShader = "VRChat/Mobile/Toon Standard";
        // The anger the mobile State layer hands its Colour layer: an animated animator parameter, never an avatar one.
        internal const string MobileAnger = "N4N0/Mobile/Anger";
        // The orb: rings from the face's middle round to the back, segments around them.
        internal const int MobileRings = 9, MobileSegments = 24;
        // The faces: 3 × 3 cells of 256² (a cell per state, Idle's blink in the ninth). Each cell is the whole sphere seen
        // around its face: the front half as a viewer in front sees it, out to FrontRadius of the cell from its middle, the
        // back squeezed into the rim and corners, which every cell paints alike so that neighbours bleed nothing.
        internal const int FaceCells = 3, FaceCellPixels = 256, BlinkCell = 8;
        internal const float FrontRadius = .48f;
        // The glow at a state's Glow of 1 (_EmissionStrength), the rim's (_RimIntensity), the eyes' grey, how dark and how
        // bright the Light option's brightness makes the tint.
        internal const float MobileGlow = .9f, MobileRim = 1, EyeShade = .04f, MobileDim = .8f, MobileBright = 1.2f;
        // When Idle, Listening and Talking blink in their 9.6 s loop, and for how long.
        private static readonly float[] Blinks = { 3.1f, 7.6f };
        private const float BlinkLength = .1f;
        private static readonly Color AngerRed = new Color(1, .1f, .07f);

        /// <summary>Whether a placed orb has the mobile N4N0.</summary>
        public static bool HasMobile(Transform orb) => orb != null && orb.Find(MobileFace) != null;

        /// <summary>Where a face's cell starts in the faces' picture (the texture's offset for it).</summary>
        internal static Vector2 CellOffset(int cell) => new Vector2(cell % FaceCells, cell / FaceCells) / FaceCells;

        /// <summary>Whether the build target is a phone's (Android, iOS): builds then keep the mobile N4N0.</summary>
        internal static bool MobileTarget => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

        // Its owner's mobile N4N0 looks at their eyes, which Bind puts at -OrbFromEyes from it (in the avatar's turn).
        internal static Quaternion OwnerFacing => Quaternion.LookRotation(-OrbFromEyes, Vector3.up);

        // ---- The part ----

        /// <summary>
        /// The mobile N4N0 below <paramref name="root"/>, off until a mobile build turns it on, its files in
        /// <paramref name="folder"/>, and its controller and parameters in N4N0's Full Controller after the PC N4N0's.
        /// </summary>
        private static void AddMobile(Transform root, string folder)
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
            // Where the PC N4N0 rests: the animator bobs and hops Float, and turns Face (the orb's one bone) to its owner.
            var orb = Child(head, "Orb");
            orb.localPosition = DefaultEyes + OrbFromEyes;
            TouchReceiver(Child(orb, "Touch"));
            var face = Child(Child(orb, "Float"), "Face");
            var renderer = face.gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = MobileMesh(folder);
            renderer.bones = new[] { face }; renderer.rootBone = face; renderer.quality = SkinQuality.Bone1;
            renderer.sharedMaterials = new[] { MobileMaterial(folder, FaceAtlas(folder)) };
            renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * (2 * Radius + .02f));
            // Lit by the world (light probes), but casting and taking no shadow.
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
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

        // ---- The files ----

        /// <summary>
        /// The orb: a sphere of <see cref="Radius"/> around its bone, its face along +Z, 384 triangles. Its rings run from the
        /// face's middle to the back, and the first UV of each vertex is its place in a face's cell (<see cref="FaceUv"/>);
        /// the back's pole has a vertex per segment, on the cell's rim between the segment's two.
        /// </summary>
        private static Mesh MobileMesh(string folder)
        {
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>(); var triangles = new List<int>();
            int Add(Vector3 direction, Vector2 uv) { vertices.Add(direction * Radius); uvs.Add(uv); return vertices.Count - 1; }
            // Facing outwards: Unity's front faces turn clockwise as seen, their cross product towards the viewer.
            void Triangle(int a, int b, int c)
            {
                if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), vertices[a] + vertices[b] + vertices[c]) < 0) (b, c) = (c, b);
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
            int front = Add(Vector3.forward, FaceUv(Vector3.forward));
            var rings = new int[MobileRings - 1][];
            for (int r = 1; r < MobileRings; r++)
            {
                float polar = Mathf.PI * r / MobileRings;
                rings[r - 1] = new int[MobileSegments];
                for (int s = 0; s < MobileSegments; s++)
                {
                    float around = 2 * Mathf.PI * s / MobileSegments;
                    var direction = new Vector3(Mathf.Sin(polar) * Mathf.Cos(around), Mathf.Sin(polar) * Mathf.Sin(around), Mathf.Cos(polar));
                    rings[r - 1][s] = Add(direction, FaceUv(direction));
                }
            }
            for (int s = 0; s < MobileSegments; s++)
            {
                int t = (s + 1) % MobileSegments;
                Triangle(front, rings[0][s], rings[0][t]);
                for (int r = 0; r + 1 < rings.Length; r++)
                {
                    Triangle(rings[r][s], rings[r + 1][s], rings[r + 1][t]);
                    Triangle(rings[r][s], rings[r + 1][t], rings[r][t]);
                }
                float middle = 2 * Mathf.PI * (s + .5f) / MobileSegments;
                int back = Add(Vector3.back, CellPoint(Mathf.PI, new Vector2(-Mathf.Cos(middle), Mathf.Sin(middle))));
                Triangle(rings[rings.Length - 1][s], back, rings[rings.Length - 1][t]);
            }
            var mesh = new Mesh { name = "N4N0 mobile" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
            mesh.SetNormals(vertices.Select(v => v.normalized).ToList());
            mesh.SetTriangles(triangles, 0);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, vertices.Count).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, folder + "/N4N0 mobile mesh.asset");
            return mesh;
        }

        /// <summary>
        /// Where a direction from N4N0's middle is in a face's cell: the front half as a viewer in front sees it, out to
        /// <see cref="FrontRadius"/> (as many texels everywhere on their screen, and a straight map inside each triangle),
        /// the back from there to the cell's rim by its angle from the face's middle (+Z). Their right is −X.
        /// </summary>
        internal static Vector2 FaceUv(Vector3 direction)
        {
            var n = direction.normalized;
            var across = new Vector2(-n.x, n.y);
            return CellPoint(Mathf.Acos(Mathf.Clamp(n.z, -1, 1)), across.sqrMagnitude > 1e-12f ? across.normalized : Vector2.right);
        }

        private static Vector2 CellPoint(float polar, Vector2 around) => new Vector2(.5f, .5f) + around * CellRadius(polar);

        private static float CellRadius(float polar) => polar <= Mathf.PI / 2
            ? FrontRadius * Mathf.Sin(polar)
            : FrontRadius + (.5f - FrontRadius) * Mathf.Min(1, (polar - Mathf.PI / 2) / (Mathf.PI / 2));

        private static float PolarAt(float radius) => radius <= FrontRadius
            ? Mathf.Asin(radius / FrontRadius)
            : Mathf.PI / 2 + Mathf.Min(1, (radius - FrontRadius) / (.5f - FrontRadius)) * (Mathf.PI / 2);

        private static Material MobileMaterial(string folder, Texture2D faces)
        {
            var shader = Shader.Find(MobileShader);
            if (shader == null) throw new InvalidOperationException("VRChat's " + MobileShader + " shader is missing: update the VRChat SDK in Creator Companion.");
            var material = new Material(shader) { name = "N4N0 mobile" };
            // The faces lit by the world and glowing on their own: the same cell of both maps.
            foreach (string map in new[] { "_MainTex", "_EmissionMap" })
            {
                material.SetTexture(map, faces);
                material.SetTextureScale(map, Vector2.one / FaceCells);
                material.SetTextureOffset(map, CellOffset((int)OrbState.Idle));
            }
            material.SetFloat("_EmissionUV", 0);
            SetTint(material, Colour, 1);
            material.SetFloat("_EmissionStrength", MobileGlow);
            // A glow at the edge, whatever lights the world: the PC N4N0's halo and coloured rim.
            material.EnableKeyword("USE_RIMLIGHT");
            material.SetFloat("_RimIntensity", MobileRim);
            material.SetFloat("_RimRange", .4f); material.SetFloat("_RimSharpness", .25f);
            material.SetFloat("_RimAlbedoTint", 0); material.SetFloat("_RimEnvironmental", 0);
            AssetDatabase.CreateAsset(material, folder + "/N4N0 mobile.mat");
            return material;
        }

        // The tint's three colours, as the Colour layer animates them: the lit faces and their glow at a brightness, both
        // paler than the colour (the PC N4N0's core is whiter), and the rim in the colour itself.
        private static IEnumerable<(string property, Color colour)> Tint(Color colour, float brightness)
        {
            yield return ("_Color", Opaque(Color.Lerp(colour, Color.white, .5f) * .35f));
            yield return ("_EmissionColor", Opaque(Color.Lerp(colour, Color.white, .35f) * brightness));
            yield return ("_RimColor", Opaque(colour));
        }

        private static void SetTint(Material material, Color colour, float brightness)
        {
            foreach (var (property, value) in Tint(colour, brightness)) material.SetColor(property, value);
        }

        private static Color Opaque(Color colour) { colour.a = 1; return colour; }

        // The faces' picture, imported for phones: mipmapped, clamped, as colour.
        private static Texture2D FaceAtlas(string folder)
        {
            string path = folder + "/N4N0 faces.png";
            System.IO.File.WriteAllBytes(path, FacesPng());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default; importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.None; importer.mipmapEnabled = true;
            importer.npotScale = TextureImporterNPOTScale.None; importer.maxTextureSize = 1024;
            importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 2;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            // On phones ASTC 4 × 4 keeps the eyes' edges crisp: 0.75 MB with the mipmaps.
            foreach (string platform in new[] { "Android", "iPhone" })
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = platform, overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.ASTC_4x4,
                    textureCompression = TextureImporterCompression.CompressedHQ,
                });
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---- The faces ----

        private static byte[] facesPng;

        /// <summary>
        /// N4N0's faces as the orb's shader draws them (Orb.shader.txt: the eyes' shape, lids, arcs and tilt, their glints,
        /// the core whiter towards the face's middle), in grey for the material's colours to tint: a 768² PNG of a cell per
        /// state, in <see cref="OrbState"/>'s order, and Idle's eyes shut (the blink) in the ninth. Drawn once.
        /// </summary>
        internal static byte[] FacesPng()
        {
            if (facesPng != null) return facesPng;
            int size = FaceCells * FaceCellPixels;
            var pixels = new Color32[size * size];
            for (int cell = 0; cell < FaceCells * FaceCells; cell++)
            {
                var look = cell < Looks.Length ? Looks[cell] : Looks[(int)OrbState.Idle];
                float open = cell == BlinkCell ? 0 : look.Open;
                int x0 = cell % FaceCells * FaceCellPixels, y0 = cell / FaceCells * FaceCellPixels;
                for (int y = 0; y < FaceCellPixels; y++)
                for (int x = 0; x < FaceCellPixels; x++)
                {
                    byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(FacePixel(x, y, look, open)) * 255);
                    pixels[(y0 + y) * size + x0 + x] = new Color32(value, value, value, 255);
                }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                return facesPng = texture.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(texture); }
        }

        // A texel of a cell: the shade at its middle or, near the edge of an eye or a glint, the share of 4 × 4 samples across
        // it that fall inside (a texel is about 0.008 of the face's plane).
        private static float FacePixel(int x, int y, Look look, float open)
        {
            var middle = new Vector2((x + .5f) / FaceCellPixels, (y + .5f) / FaceCellPixels);
            if (!FaceAt(middle, look, open, out float body, out float eyes, out float spark, out float front)) return body;
            if (Mathf.Min(Mathf.Abs(eyes), Mathf.Abs(spark)) > .04f) return Shade(body, eyes < 0 ? 1 : 0, eyes < 0 && spark < 0 ? 1 : 0, front, open);
            const int samples = 4;
            float ink = 0, shine = 0;
            for (int j = 0; j < samples; j++)
            for (int i = 0; i < samples; i++)
            {
                var at = middle + new Vector2((i + .5f) / samples - .5f, (j + .5f) / samples - .5f) / FaceCellPixels;
                FaceAt(at, look, open, out _, out float e, out float s, out _);
                if (e >= 0) continue;
                ink++;
                if (s < 0) shine++;
            }
            return Shade(body, ink / (samples * samples), shine / (samples * samples), front, open);
        }

        // At a point of a cell: the core's shade, the eyes' and the glints' signed distances in the face's plane, how much
        // the point faces ahead. False on the back, where no eye is.
        private static bool FaceAt(Vector2 uv, Look look, float open, out float body, out float eyes, out float spark, out float front)
        {
            var d = uv - new Vector2(.5f, .5f);
            float r = d.magnitude, polar = PolarAt(r);
            front = Mathf.Cos(polar);
            body = .8f + .2f * Mathf.Pow(Mathf.Max(front, 0), 2);
            eyes = spark = 1;
            if (front < .2f) return false;
            var p = (r > 1e-6f ? d / r : Vector2.zero) * Mathf.Sin(polar);
            eyes = Mathf.Min(Eye(p, -1, open, look, out var a), Eye(p, 1, open, look, out var b));
            var glint = new Vector2(-.036f, .062f * Mathf.Min(open, 1));
            spark = Mathf.Min((a - glint).magnitude, (b - glint).magnitude) - .03f;
            return true;
        }

        private static float Shade(float body, float ink, float shine, float front, float open)
        {
            float facing = Smoothstep(.2f, .35f, front);
            float value = Mathf.Lerp(body, EyeShade, ink * facing);
            return Mathf.Lerp(value, 1, shine * facing * Smoothstep(.55f, .8f, open) * .9f);
        }

        // One eye's signed distance (negative inside) in the face's plane, side −1 left and +1 right as the viewer sees it:
        // the orb shader's Eye.
        private static float Eye(Vector2 p, float side, float open, Look look, out Vector2 local)
        {
            float w = .115f * look.Wide;
            var q = Rotate(p - new Vector2(side * .33f * Mathf.Lerp(1, look.Wide, .5f) + look.LookX, .1f + look.LookY), -side * look.Tilt);
            local = q;
            float h = .19f * open;
            float pill = RoundBox(q, new Vector2(w, Mathf.Max(h, .001f)), Mathf.Min(w, Mathf.Max(h, .001f)));
            pill = Mathf.Max(pill, q.y - Mathf.Lerp(h, -h * .1f, look.Lid));
            float x = Mathf.Clamp(q.x, -w, w);
            float arc = look.Curve * (.85f * x * x / w - .3f * w);
            float slope = look.Curve * 1.7f * x / w;
            float stroke = new Vector2(Mathf.Max(Mathf.Abs(q.x) - w, 0), (q.y - arc) / Mathf.Sqrt(1 + slope * slope)).magnitude - .028f;
            return Mathf.Lerp(stroke, pill, Smoothstep(.06f, .35f, open));
        }

        private static Vector2 Rotate(Vector2 p, float angle)
        {
            float s = Mathf.Sin(angle), c = Mathf.Cos(angle);
            return new Vector2(c * p.x - s * p.y, s * p.x + c * p.y);
        }

        private static float RoundBox(Vector2 p, Vector2 half, float radius)
        {
            var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - half + new Vector2(radius, radius);
            return Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - radius;
        }

        private static float Smoothstep(float from, float to, float x)
        {
            float t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3 - 2 * t);
        }

        // ---- The animator ----

        /// <summary>
        /// The mobile controller (paths below N4N0, as the PC one's) and its parameters: shown with N4N0/Visible; a state per
        /// N4N0/State, each its face (Idle, Listening and Talking blinking now and then), glow, bob or hop; the tint of the
        /// Light option's colour at its brightness (N4N0's green while off), red while annoyed; the face turned to its
        /// owner on its owner's client. Faces change at once: the texture's offset can't blend between cells.
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
            MobileStates(controller);
            MobileTint(controller);
            MobileFacing(controller);

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[] { Parameter(MobileVersionParameter, synced: false, type: VRCExpressionParameters.ValueType.Int, defaultValue: MobileVersion) };
            AssetDatabase.CreateAsset(parameters, folder + "/N4N0 mobile parameters.asset");
            return (controller, parameters);
        }

        // One state for each value of N4N0/State, reached at once from any other; each sets everything the layer animates.
        private static void MobileStates(AnimatorController controller)
        {
            var machine = Layer(controller, "Mobile state");
            var states = new AnimatorState[Looks.Length];
            for (int s = 0; s < Looks.Length; s++)
            {
                string name = ((OrbState)s).ToString();
                Motion motion = MobileClip(name, s, 0);
                // Listening glows up with its owner's voice.
                if (s == (int)OrbState.Listening)
                {
                    var voice = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = "Voice", useAutomaticThresholds = false };
                    voice.AddChild(motion, 0);
                    voice.AddChild(MobileClip(name + " loud", s, 1), 1);
                    motion = voice;
                }
                states[s] = HeldPropRig.State(controller, machine, name, motion);
                if (s > 0) Jump(machine, states[s], (State, AnimatorConditionMode.Equals, s));
            }
            machine.defaultState = states[0];
            Jump(machine, states[0], (State, AnimatorConditionMode.Less, 1));
            Jump(machine, states[0], (State, AnimatorConditionMode.Greater, Looks.Length - 1));
        }

        /// <summary>
        /// A state of the mobile N4N0, looping whole bobs (3.2 s each, 5.5 asleep, as the orb shader's): its face's cell on
        /// both maps (and the blink's for a moment in the states that blink), its glow (Talking shimmering, Listening up with
        /// the voice at <paramref name="pulse"/> 1), its anger, the bob, and Surprised's jump, which plays once.
        /// </summary>
        private static AnimationClip MobileClip(string name, int state, float pulse)
        {
            var look = Looks[state];
            float period = Mathf.Lerp(3.2f, 5.5f, look.Sleep), length = period * (look.Sleep > .5f ? 2 : 3);
            var clip = new AnimationClip { name = name };
            var open = CellOffset(state);
            var shut = CellOffset(BlinkCell);
            var blinks = look.Blink > .5f ? Blinks : Array.Empty<float>();
            foreach (string map in new[] { "_MainTex_ST", "_EmissionMap_ST" })
            {
                string property = "material." + map;
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), property + ".x", 1f / FaceCells);
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), property + ".y", 1f / FaceCells);
                SetCurve(clip, MobileFace, typeof(SkinnedMeshRenderer), property + ".z", Steps(length, open.x, shut.x, blinks));
                SetCurve(clip, MobileFace, typeof(SkinnedMeshRenderer), property + ".w", Steps(length, open.y, shut.y, blinks));
            }
            float glow = MobileGlow * look.Glow * (1 + .55f * pulse);
            if (look.Talk > 0) SetCurve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material._EmissionStrength",
                Sampled(length, t => glow * (1 + .14f * look.Talk * Mathf.Sin(t * 21) * Mathf.Sin(t * 6.3f))));
            else Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material._EmissionStrength", glow);
            Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material._RimIntensity", Mathf.Min(1, MobileRim * look.Glow * (1 + .3f * pulse)));
            Curve(clip, "", typeof(Animator), MobileAnger, look.Anger);
            // Up 3.5 cm and back down in 0.36 s (the PC N4N0's hop), over the bob.
            var jump = new AnimationCurve(new Keyframe(0, 0), new Keyframe(.14f, .035f), new Keyframe(.36f, 0), new Keyframe(.6f, 0));
            float bob = .22f * look.Bob * Radius;
            SetCurve(clip, MobileFloat, typeof(Transform), "m_LocalPosition.y",
                Sampled(length, t => bob * Mathf.Sin(t * 2 * Mathf.PI / period) + (look.Hops && t <= .6f ? jump.Evaluate(t) : 0)));
            foreach (char axis in "xz") Curve(clip, MobileFloat, typeof(Transform), "m_LocalPosition." + axis, 0);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = !look.Hops;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        private static void SetCurve(AnimationClip clip, string path, Type type, string property, AnimationCurve curve) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);

        // A value that holds, then jumps to `shut` for BlinkLength at each blink, and back.
        private static AnimationCurve Steps(float length, float open, float shut, IEnumerable<float> blinks)
        {
            var keys = new List<Keyframe> { new Keyframe(0, open) };
            foreach (float t in blinks) { keys.Add(new Keyframe(t, shut)); keys.Add(new Keyframe(t + BlinkLength, open)); }
            keys.Add(new Keyframe(length, open));
            var curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
            return curve;
        }

        // A value over `length`, sampled 30 times a second, straight between samples.
        private static AnimationCurve Sampled(float length, Func<float, float> value)
        {
            int count = Mathf.CeilToInt(length * 30);
            var curve = new AnimationCurve(Enumerable.Range(0, count + 1).Select(i => { float t = Mathf.Min(length, i / 30f); return new Keyframe(t, value(t)); }).ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            return curve;
        }

        /// <summary>
        /// The tint: N4N0's green while the light is off, else the light's colour, darker or brighter with its brightness;
        /// red while annoyed (<see cref="MobileAnger"/>, the PC N4N0's red). One state per colour, as the PC Light layer's,
        /// each setting every colour the layer animates. It reads the Light option's parameters, which an N4N0 without the
        /// option lacks: it then stays green.
        /// </summary>
        private static void MobileTint(AnimatorController controller)
        {
            var machine = Layer(controller, "Mobile colour");
            var off = HeldPropRig.State(controller, machine, "Off", TintTree("N4N0", Colour, 1));
            machine.defaultState = off;
            Enter(machine, off, (Light, AnimatorConditionMode.IfNot, 0));
            for (int i = 0; i < LightColors.Count; i++)
            {
                var (name, color) = LightColors[i];
                var level = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = LightLevel, useAutomaticThresholds = false };
                level.AddChild(TintTree(name + " dim", color, MobileDim), 0);
                level.AddChild(TintTree(name + " bright", color, MobileBright), 1);
                var state = HeldPropRig.State(controller, machine, name, level);
                Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Equals, i));
                // A colour past the last is the first.
                if (i == 0) Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Greater, LightColors.Count - 1));
            }
        }

        // A tint and its annoyed red, by MobileAnger.
        private static BlendTree TintTree(string name, Color colour, float brightness)
        {
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = MobileAnger, useAutomaticThresholds = false };
            tree.AddChild(TintClip(name, colour, brightness), 0);
            tree.AddChild(TintClip(name + " annoyed", Color.Lerp(colour, AngerRed, .9f), brightness), 1);
            return tree;
        }

        private static AnimationClip TintClip(string name, Color colour, float brightness)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (property, value) in Tint(colour, brightness))
            {
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material." + property + ".r", value.r);
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material." + property + ".g", value.g);
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material." + property + ".b", value.b);
                Curve(clip, MobileFace, typeof(SkinnedMeshRenderer), "material." + property + ".a", value.a);
            }
            return clip;
        }

        // The face looks ahead, at whoever is in front; on its owner's client, at its owner's eyes (as the PC N4N0 looks at
        // whoever looks at it).
        private static void MobileFacing(AnimatorController controller)
        {
            var machine = Layer(controller, "Mobile face");
            AnimationClip Turned(string name, Quaternion turn)
            {
                var clip = new AnimationClip { name = name };
                Curve(clip, MobileFace, typeof(Transform), "m_LocalRotation.x", turn.x);
                Curve(clip, MobileFace, typeof(Transform), "m_LocalRotation.y", turn.y);
                Curve(clip, MobileFace, typeof(Transform), "m_LocalRotation.z", turn.z);
                Curve(clip, MobileFace, typeof(Transform), "m_LocalRotation.w", turn.w);
                return clip;
            }
            var ahead = HeldPropRig.State(controller, machine, "Ahead", Turned("Face ahead", Quaternion.identity));
            var owner = HeldPropRig.State(controller, machine, "At its owner", Turned("Face its owner", OwnerFacing));
            machine.defaultState = ahead;
            Transition(ahead, owner, ("IsLocal", AnimatorConditionMode.If, 0));
            Transition(owner, ahead, ("IsLocal", AnimatorConditionMode.IfNot, 0));
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
