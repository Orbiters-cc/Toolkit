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
using VRC.SDKBase.Editor.BuildPipeline;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's capture kit and camera rig (Orbiters contracts' Docs/rig-frames.md, N4N0's capture kit, with
    /// Docs/capture-strip.md, Docs/skeleton-band.md and Docs/depth-frames.md): what lets Orbiters - N4N0 record a VRChat
    /// world through VRChat's camera, for 3D pictures and recordings. In the frames VRChat's camera renders, never in
    /// anyone's view, mirrors or other players' pictures, the kit draws the capture strip (the camera's exact pose), the
    /// skeleton band (the avatar's pose) and depth frames; the rig, six square 90° cameras around one point, makes about
    /// every other frame a rig frame of six more views while it is on. Orbiters - N4N0 drives them over OSC through local,
    /// unsynced parameters (no synced bits): <see cref="CaptureRecord"/> (N4N0 › Record sets it too),
    /// <see cref="CaptureDepth"/>, <see cref="CaptureFollow"/>, <see cref="CaptureRig"/> and where the rig stands.
    /// <para>
    /// The rig and its six cameras come with N4N0 (their shader copied into its folder, the contracts' as they are); the
    /// kit's skinned helper mesh is made for the avatar's armature as the avatar is built for upload
    /// (<see cref="OrbCaptureHook"/>, PC only: phones have no avatar cameras and no custom shaders), so it always fits
    /// the armature uploaded. The kit's bounds stand around the rig: at the hips while the rig rests there (VRChat's
    /// "Good" bounds), at VRChat's camera while Orbiters - N4N0 has the rig follow it, so the kit draws wherever the
    /// camera goes.
    /// </para>
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>The capture kit draws the strip, the skeleton band and depth frames (local; N4N0 › Record sets it, and
        /// Orbiters - N4N0 starts or stops recording when it changes).</summary>
        public const string CaptureRecord = "N4N0/Capture/Record";
        /// <summary>While recording, about one colour frame in <see cref="DepthEvery"/> is a depth frame (local, set by Orbiters - N4N0).</summary>
        public const string CaptureDepth = "N4N0/Capture/Depth";
        /// <summary>The rig stands where <see cref="CaptureRigX"/>, Y, Z and <see cref="CaptureRigYaw"/> say, else at the hips (local).</summary>
        public const string CaptureFollow = "N4N0/Capture/Follow";
        /// <summary>The rig's six cameras render and its renderer draws (local, the wearer's client only).</summary>
        public const string CaptureRig = "N4N0/Capture/Rig";
        /// <summary>Where the rig stands while following: value × <see cref="RigReachMetres"/> (Unity world), heading value × <see cref="RigYawDegrees"/>.</summary>
        public const string CaptureRigX = "N4N0/Capture/RigX", CaptureRigY = "N4N0/Capture/RigY", CaptureRigZ = "N4N0/Capture/RigZ", CaptureRigYaw = "N4N0/Capture/RigYaw";
        /// <summary>Every capture parameter (contracts' <c>orb::params::CAPTURE</c>): Orbiters - N4N0's own.</summary>
        public static readonly IReadOnlyList<string> CaptureParameters = new[] { CaptureRecord, CaptureDepth, CaptureFollow, CaptureRig, CaptureRigX, CaptureRigY, CaptureRigZ, CaptureRigYaw };
        public const float RigReachMetres = 4096f, RigYawDegrees = 180f;

        /// <summary>The capture part below N4N0's root: the rig (with the renderer of its rig frames) and the kit's renderer.</summary>
        public const string Capture = "Capture", CaptureRigPath = Capture + "/Rig", CaptureKitPath = Capture + "/Kit", RigRendererPath = CaptureRigPath + "/Rig renderer";
        /// <summary>The faces' cameras below the rig, in the contracts' face order (1..6: +X, −X, +Y, −Y, +Z, −Z).</summary>
        public static readonly IReadOnlyList<string> RigFaceNames = new[] { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
        public static string RigFacePath(int face) => CaptureRigPath + "/Face " + RigFaceNames[face - 1];
        /// <summary>Columns of face 1..6's camera (right, up, forward) in the rig's axes (contracts' <c>rig::BASES</c>).</summary>
        internal static readonly Vector3[,] RigBases =
        {
            { new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0) },
            { new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(-1, 0, 0) },
            { new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0) },
            { new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0) },
            { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) },
            { new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1) },
        };
        /// <summary>The face cameras' near and far planes (the rig's shader tells their renders by the near plane).</summary>
        internal const float RigNear = 0.0479f, RigFar = 1000f;
        /// <summary>Each face's render texture, square: a little more than a face takes in a 1080p frame (420 px).</summary>
        internal const int RigFaceSize = 512;
        // Triangle kinds (uv0.x) the kit's shader reads; bones are 0..54.
        internal const float StripKind = 64f, FillKind = 65f, HeaderKind = 66f;
        internal const int CaptureBones = 55;
        // While depth frames are on, about one frame in DepthEvery is a depth frame.
        internal const float DepthEvery = 4f;
        // The kit's and the rig renderer's bounds, around the rig: VRChat's "Good" bounds while it rests at the hips.
        internal const float CaptureBounds = 4f;
        // VRChat's UI layers (UI, UiMenu, InternalUI): the rig's cameras leave them out.
        private const int RigHiddenLayers = (1 << 5) | (1 << 12) | (1 << 19);
        // The contracts' shaders, as they are (Orbiters contracts' Shaders/), and their includes.
        private const string KitShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbCaptureKit.shader.txt";
        private const string RigShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbCaptureRig.shader.txt";
        internal static readonly (string include, string template)[] CaptureIncludes =
        {
            ("OrbitersCapture.cginc", "Packages/orbiters.toolkit/Editor/VRChat/OrbitersCapture.cginc.txt"),
            ("OrbitersSkeletonBand.cginc", "Packages/orbiters.toolkit/Editor/VRChat/OrbitersSkeletonBand.cginc.txt"),
        };
        /// <summary>The copies' shader names (the contracts' are Hidden/Orbiters/CaptureKit and CaptureRig, which Mixer's
        /// capture simulator finds by name).</summary>
        internal const string KitShaderName = "Hidden/Orbiters/N4N0 Capture Kit", RigShaderName = "Hidden/Orbiters/N4N0 Capture Rig";
        /// <summary>Where builds keep each avatar's kit mesh and material (a folder per avatar name).</summary>
        internal const string CaptureKitFolder = "Assets/Orbiters/N4N0/Capture kit";

        private static readonly int BoneMaskId = Shader.PropertyToID("_OrbitersBoneMask");
        private static readonly int AvatarIdId = Shader.PropertyToID("_OrbitersAvatarId");
        private static readonly int DepthEveryId = Shader.PropertyToID("_OrbitersDepthEvery");

        /// <summary>Whether a placed orb has the capture kit and rig.</summary>
        public static bool HasCapture(Transform orb) => orb != null && orb.Find(RigRendererPath) != null && orb.Find(CaptureKitPath) != null;

        // ---- The part ----

        /// <summary>
        /// The capture part below <paramref name="orb"/> (an orb that has one keeps it): the rig, placed by a VRChat Parent
        /// Constraint whose first source is the world origin (offsets from the animator) and whose second the hips (set as
        /// the avatar is built), its six cameras into HDR render textures and the renderer drawing their pictures, all off;
        /// and the kit's renderer, whose mesh the build makes. Shaders, material and textures go to <paramref name="folder"/>.
        /// </summary>
        internal static Transform AddCapture(Transform orb, string folder)
        {
            var existing = orb.Find(Capture);
            if (existing != null && HasCapture(orb)) return existing;
            if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);
            var capture = new GameObject(Capture).transform;
            Undo.RegisterCreatedObjectUndo(capture.gameObject, "N4N0 capture");
            capture.SetParent(orb, false);
            var rig = Child(capture, "Rig");
            var constraint = rig.gameObject.AddComponent<VRCParentConstraint>();
            constraint.Sources.Add(new VRCConstraintSource(null, 0f));
            constraint.Sources.Add(new VRCConstraintSource(null, 1f));
            constraint.IsActive = true; constraint.Locked = true;

            var rigMaterial = new Material(CopyCaptureShader(RigShaderTemplate, folder + "/N4N0 capture rig.shader", RigShaderName)) { name = "N4N0 capture rig" };
            for (int face = 1; face <= RigFaceNames.Count; face++)
            {
                string path = folder + "/N4N0 capture face " + RigFaceNames[face - 1] + ".renderTexture";
                var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
                if (texture == null)
                {
                    texture = new RenderTexture(RigFaceSize, RigFaceSize, 24, RenderTextureFormat.ARGBHalf)
                    {
                        name = System.IO.Path.GetFileNameWithoutExtension(path), antiAliasing = 1, useMipMap = false, autoGenerateMips = false,
                        wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                    };
                    AssetDatabase.CreateAsset(texture, path);
                }
                var holder = Child(rig, "Face " + RigFaceNames[face - 1]);
                holder.localRotation = Quaternion.LookRotation(RigBases[face - 1, 2], RigBases[face - 1, 1]);
                var camera = holder.gameObject.AddComponent<Camera>();
                camera.fieldOfView = 90f;
                camera.nearClipPlane = RigNear;
                camera.farClipPlane = RigFar;
                // Before VRChat's camera, which draws their pictures in the same frame.
                camera.depth = -100f;
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.cullingMask = ~RigHiddenLayers;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.allowDynamicResolution = false;
                camera.targetTexture = texture;
                camera.enabled = false;
                rigMaterial.SetTexture("_OrbitersRigFace" + face, texture);
            }
            rigMaterial.SetFloat("_OrbitersLocal", 0f);
            rigMaterial.SetFloat(DepthEveryId, 0f);
            AssetDatabase.CreateAsset(rigMaterial, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 capture rig.mat"));
            var rigRenderer = Child(rig, "Rig renderer");
            rigRenderer.gameObject.AddComponent<MeshFilter>().sharedMesh = RigMesh(folder);
            var renderer = rigRenderer.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = rigMaterial;
            Silent(renderer);
            renderer.enabled = false;

            // The kit: its material here, its mesh (the avatar's bones) made by each build.
            var kit = Child(capture, "Kit");
            var kitMaterial = new Material(CopyCaptureShader(KitShaderTemplate, folder + "/N4N0 capture kit.shader", KitShaderName)) { name = "N4N0 capture kit" };
            kitMaterial.SetFloat(DepthEveryId, 0f);
            kitMaterial.SetFloat("_OrbitersOnlyInCamera", 1f);
            kitMaterial.SetFloat("_OrbitersLocal", 0f);
            AssetDatabase.CreateAsset(kitMaterial, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 capture kit.mat"));
            var smr = kit.gameObject.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMaterial = kitMaterial;
            Silent(smr);
            smr.enabled = false;
            return capture;
        }

        /// <summary>
        /// Writes a contracts shader into N4N0's folder under its own name, its includes written into it (a gallery
        /// package carries the shader, not what it includes), and imports it.
        /// </summary>
        private static Shader CopyCaptureShader(string template, string path, string name)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(template);
            if (text == null) throw new InvalidOperationException("Missing " + template + ": reinstall Orbiters Toolkit.");
            System.IO.File.WriteAllText(path, CaptureShaderText(text.text, name, include =>
            {
                var file = CaptureIncludes.FirstOrDefault(i => i.include == include);
                var asset = file.template != null ? AssetDatabase.LoadAssetAtPath<TextAsset>(file.template) : null;
                if (asset == null) throw new InvalidOperationException("Missing " + include + ": reinstall Orbiters Toolkit.");
                return asset.text;
            }));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Unity did not import " + path + ".");
            return shader;
        }

        /// <summary>A contracts shader's text renamed <paramref name="name"/>, each <c>#include "Orbiters….cginc"</c> replaced by the file.</summary>
        internal static string CaptureShaderText(string shader, string name, Func<string, string> include)
        {
            var lines = shader.Replace("\r\n", "\n").Split('\n');
            var output = new StringBuilder();
            bool renamed = false;
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (!renamed && trimmed.StartsWith("Shader \"", StringComparison.Ordinal))
                {
                    output.Append("Shader \"").Append(name).Append("\"\n");
                    renamed = true;
                    continue;
                }
                if (trimmed.StartsWith("#include \"Orbiters", StringComparison.Ordinal) && trimmed.EndsWith(".cginc\"", StringComparison.Ordinal))
                {
                    output.Append(include(trimmed.Substring("#include \"".Length).TrimEnd('"')).Replace("\r\n", "\n")).Append('\n');
                    continue;
                }
                output.Append(line).Append('\n');
            }
            if (!renamed) throw new InvalidOperationException("A capture shader without its Shader line.");
            return output.ToString();
        }

        // Neither shadows nor probes nor occlusion: what it draws is no part of the scene.
        private static void Silent(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>The rig renderer's mesh: three triangles at its origin (uv0.x 0 the faces, 1 the strip, 2 the stamp).</summary>
        private static Mesh RigMesh(string folder)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int part = 0; part < 3; part++)
                foreach (var v in new[] { Vector3.zero, Vector3.right * 0.01f, Vector3.up * 0.01f })
                {
                    vertices.Add(v);
                    uvs.Add(new Vector2(part, 0f));
                }
            var mesh = new Mesh { name = "N4N0 capture rig" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(Enumerable.Range(0, vertices.Count).ToArray(), 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * CaptureBounds);
            AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 capture rig mesh.asset"));
            return mesh;
        }

        // ---- The kit, made as the avatar is built ----

        /// <summary>The 16-bit identity the kit bakes for an avatar (skeleton band header): CRC-16 (CCITT, 0xFFFF) of its
        /// name's UTF-8 bytes, as Mixer reads it back (contracts' <c>SkeletonBand.Crc16</c>).</summary>
        public static ushort CaptureIdentity(string avatarName)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in Encoding.UTF8.GetBytes(avatarName ?? ""))
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        /// <summary>The humanoid bones an avatar has, as the band's 64-bit mask.</summary>
        internal static ulong CaptureMask(Animator animator)
        {
            ulong mask = 0;
            for (int b = 0; b < CaptureBones; b++)
                if (animator.GetBoneTransform((HumanBodyBones)b) != null)
                    mask |= 1UL << b;
            return mask;
        }

        /// <summary>The mask as the material keeps it: four 16-bit words, high first.</summary>
        internal static Vector4 MaskWords(ulong mask) => new Vector4((mask >> 48) & 0xFFFF, (mask >> 32) & 0xFFFF, (mask >> 16) & 0xFFFF, mask & 0xFFFF);

        /// <summary>
        /// The kit's mesh in <paramref name="space"/> (the renderer's transform), skinned to <paramref name="bones"/> (the
        /// hips first): the strip, band fill and three header triangles on the hips, then one triangle per humanoid bone with
        /// vertices at its origin and one metre along its local X and Y axes, each skinned to that bone only.
        /// </summary>
        internal static Mesh CaptureMesh(Animator animator, Transform space, out Transform[] bones, out ulong mask)
        {
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var list = new List<Transform> { hips };
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var toSpace = space.worldToLocalMatrix;
            void Triangle(Transform bone, int index, float kind, float copy)
            {
                var o = bone.position;
                foreach (var p in new[] { o, o + bone.right, o + bone.up })
                {
                    vertices.Add(toSpace.MultiplyPoint3x4(p));
                    uvs.Add(new Vector2(kind, copy));
                    weights.Add(new BoneWeight { boneIndex0 = index, weight0 = 1f });
                }
            }
            Triangle(hips, 0, StripKind, 0f);
            Triangle(hips, 0, FillKind, 0f);
            for (int copy = 0; copy < 3; copy++) Triangle(hips, 0, HeaderKind, copy);
            mask = 0;
            for (int b = 0; b < CaptureBones; b++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)b);
                if (bone == null) continue;
                mask |= 1UL << b;
                int index = b == (int)HumanBodyBones.Hips ? 0 : list.Count;
                if (index != 0) list.Add(bone);
                Triangle(bone, index, b, 0f);
            }
            bones = list.ToArray();
            var mesh = new Mesh { name = "N4N0 capture kit" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            // The shader needs the triangles in this order: the band's fill before the cells drawn over it.
            mesh.SetTriangles(Enumerable.Range(0, vertices.Count).ToArray(), 0, false);
            mesh.boneWeights = weights.ToArray();
            mesh.bindposes = list.Select(t => t.worldToLocalMatrix * space.localToWorldMatrix).ToArray();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * CaptureBounds);
            return mesh;
        }

        /// <summary>
        /// Makes the kit of <paramref name="orb"/> for the avatar it is on (<paramref name="animator"/>, named
        /// <paramref name="avatarName"/>): the mesh for its armature and a material with its bones and identity, saved in a
        /// folder of the avatar's below <paramref name="root"/>; the kit's bounds around the rig; the rig resting at the hips.
        /// Returns the kit's renderer, off (the animator turns it on), or null for an orb without the capture part.
        /// </summary>
        public static SkinnedMeshRenderer BuildCaptureKit(Transform orb, Animator animator, string avatarName, string root = CaptureKitFolder)
        {
            if (orb == null || !HasCapture(orb)) return null;
            if (animator == null || animator.avatar == null || !animator.isHuman) throw new InvalidOperationException(avatarName + " is not humanoid: N4N0's capture kit draws a humanoid's bones.");
            var kit = orb.Find(CaptureKitPath);
            var rig = orb.Find(CaptureRigPath);
            var smr = kit.GetComponent<SkinnedMeshRenderer>() ?? kit.gameObject.AddComponent<SkinnedMeshRenderer>();
            var template = smr.sharedMaterial;
            if (template == null || template.shader == null) throw new InvalidOperationException("This N4N0's capture kit has no material: add N4N0 again.");
            string folder = root + "/" + SafeFolderName(avatarName);
            EnsureFolder(folder);
            var mesh = SaveInPlace(CaptureMesh(animator, kit, out var bones, out ulong mask), folder + "/Capture kit mesh.asset");
            var material = new Material(template);
            material.SetVector(BoneMaskId, MaskWords(mask));
            material.SetFloat(AvatarIdId, CaptureIdentity(avatarName));
            material = SaveInPlace(material, folder + "/Capture kit.mat");
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.sharedMaterial = material;
            // Its bounds stand around the rig, as large in the world whatever the avatar's scale.
            smr.rootBone = rig;
            var s = rig.lossyScale;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(CaptureBounds / Mathf.Max(1e-6f, Mathf.Abs(s.x)), CaptureBounds / Mathf.Max(1e-6f, Mathf.Abs(s.y)), CaptureBounds / Mathf.Max(1e-6f, Mathf.Abs(s.z))));
            smr.quality = SkinQuality.Bone1;
            smr.updateWhenOffscreen = false;
            smr.skinnedMotionVectors = false;
            smr.enabled = false;
            var constraint = rig.GetComponent<VRCParentConstraint>();
            // Its resting place: the hips, as they are (the follow layer gives this source the weight while not following).
            if (constraint != null && constraint.Sources.Count >= 2) constraint.Sources[1] = new VRCConstraintSource(bones[0], 1f, Vector3.zero, Vector3.zero);
            return smr;
        }

        /// <summary>
        /// Each N4N0 below <paramref name="avatarRoot"/> (a build copy) for the platform built: on PC its capture kit made
        /// for the avatar (<see cref="BuildCaptureKit"/>), on a phone (<paramref name="mobile"/>) its capture part gone. An
        /// avatar that is not humanoid loses the capture part, with a warning. N4N0 in prefabs and prefab instances is left
        /// as it is.
        /// </summary>
        internal static void BuildCaptureKits(GameObject avatarRoot, bool mobile)
        {
            foreach (var orb in FindAll(avatarRoot).ToList())
            {
                if (PrefabUtility.IsPartOfAnyPrefab(orb) || orb.Find(Capture) == null) continue;
                var animator = avatarRoot.GetComponent<Animator>();
                if (mobile || animator == null || !animator.isHuman)
                {
                    if (!mobile) Debug.LogWarning("N4N0: " + avatarRoot.name + " is not humanoid, so it uploads without N4N0's capture kit (no 3D pictures or recordings with it).");
                    DropCapture(orb);
                    continue;
                }
                BuildCaptureKit(orb, animator, BuiltAvatarName(avatarRoot));
            }
        }

        /// <summary>Takes the capture part out of a build copy's N4N0 (a phone's build, an avatar that is not humanoid).</summary>
        internal static void DropCapture(Transform orb)
        {
            var capture = orb.Find(Capture);
            if (capture != null) Object.DestroyImmediate(capture.gameObject);
        }

        // The avatar's own name: VRChat builds a copy named "<name>(Clone)".
        private static string BuiltAvatarName(GameObject avatarRoot)
        {
            string name = avatarRoot.name;
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - "(Clone)".Length).TrimEnd() : name;
        }

        private static string SafeFolderName(string name)
        {
            var cleaned = new string((name ?? "").Select(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (cleaned.Length > 60) cleaned = cleaned.Substring(0, 60).TrimEnd();
            return cleaned.Length == 0 ? "Avatar" : cleaned;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, slash));
            AssetDatabase.CreateFolder(folder.Substring(0, slash), folder.Substring(slash + 1));
        }

        // A kit made again keeps its files: new contents, same file (a build's references stay good).
        private static T SaveInPlace<T>(T asset, string path) where T : Object
        {
            asset.name = System.IO.Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }
            EditorUtility.CopySerialized(asset, existing);
            existing.name = asset.name;
            Object.DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ---- The controller ----

        /// <summary>
        /// The capture parameters, layers and N4N0 › Record: the kit off, recording, or recording with depth frames
        /// (<see cref="CaptureRecord"/>, <see cref="CaptureDepth"/>), giving the rig's renderer the same depth cadence (rig
        /// frames are never depth frames); the kit drawing on the wearer's client only (IsLocal); the rig on while
        /// <see cref="CaptureRig"/> is on the wearer's client; following its four values or resting at the hips
        /// (<see cref="CaptureFollow"/>); placed by them (a direct blend tree of one linear 1D tree per value, Write Defaults
        /// on). Every state sets all its layer animates, so any Write Defaults leave nothing behind.
        /// </summary>
        private static void CaptureController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters, List<VRCExpressionsMenu.Control> controls)
        {
            foreach (string name in new[] { CaptureRecord, CaptureDepth, CaptureFollow, CaptureRig }) controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            foreach (string name in new[] { CaptureRigX, CaptureRigY, CaptureRigZ, CaptureRigYaw }) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            if (!controller.parameters.Any(p => p.name == "IsLocal")) controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);

            // The kit: drawn while recording, with the depth cadence for it and the rig's renderer.
            var kit = Layer(controller, "Capture");
            AnimatorState KitState(string name, bool shown, float depthEvery)
            {
                var clip = new AnimationClip { name = "Capture " + name };
                Curve(clip, CaptureKitPath, typeof(SkinnedMeshRenderer), "m_Enabled", shown ? 1 : 0);
                Curve(clip, CaptureKitPath, typeof(SkinnedMeshRenderer), "material._OrbitersDepthEvery", depthEvery);
                Curve(clip, RigRendererPath, typeof(MeshRenderer), "material._OrbitersDepthEvery", depthEvery);
                return HeldPropRig.State(controller, kit, name, clip);
            }
            var off = KitState("Off", false, 0);
            var recording = KitState("Recording", true, 0);
            var depth = KitState("Recording with depth frames", true, DepthEvery);
            kit.defaultState = off;
            Transition(off, recording, (CaptureRecord, AnimatorConditionMode.If, 0), (CaptureDepth, AnimatorConditionMode.IfNot, 0));
            Transition(off, depth, (CaptureRecord, AnimatorConditionMode.If, 0), (CaptureDepth, AnimatorConditionMode.If, 0));
            Transition(recording, off, (CaptureRecord, AnimatorConditionMode.IfNot, 0));
            Transition(recording, depth, (CaptureDepth, AnimatorConditionMode.If, 0));
            Transition(depth, off, (CaptureRecord, AnimatorConditionMode.IfNot, 0));
            Transition(depth, recording, (CaptureDepth, AnimatorConditionMode.IfNot, 0));

            // The wearer's own client only: other players' copies never put strips in their pictures and streams.
            var local = Layer(controller, "Capture local");
            AnimatorState LocalState(string name, float value)
            {
                var clip = new AnimationClip { name = "Capture " + name.ToLowerInvariant() };
                Curve(clip, CaptureKitPath, typeof(SkinnedMeshRenderer), "material._OrbitersLocal", value);
                return HeldPropRig.State(controller, local, name, clip);
            }
            var remote = LocalState("Remote", 0);
            var own = LocalState("Local", 1);
            local.defaultState = remote;
            Transition(remote, own, ("IsLocal", AnimatorConditionMode.If, 0));
            Transition(own, remote, ("IsLocal", AnimatorConditionMode.IfNot, 0));

            // The rig's cameras and renderer, on the wearer's client while asked for.
            var rigLayer = Layer(controller, "Capture rig");
            AnimatorState RigState(string name, bool on)
            {
                var clip = new AnimationClip { name = "Capture " + name.ToLowerInvariant() };
                for (int face = 1; face <= RigFaceNames.Count; face++) Curve(clip, RigFacePath(face), typeof(Camera), "m_Enabled", on ? 1 : 0);
                Curve(clip, RigRendererPath, typeof(MeshRenderer), "m_Enabled", on ? 1 : 0);
                Curve(clip, RigRendererPath, typeof(MeshRenderer), "material._OrbitersLocal", on ? 1 : 0);
                return HeldPropRig.State(controller, rigLayer, name, clip);
            }
            var rigOff = RigState("Rig off", false);
            var rigOn = RigState("Rig on", true);
            rigLayer.defaultState = rigOff;
            Transition(rigOff, rigOn, (CaptureRig, AnimatorConditionMode.If, 0), ("IsLocal", AnimatorConditionMode.If, 0));
            Transition(rigOn, rigOff, (CaptureRig, AnimatorConditionMode.IfNot, 0));
            Transition(rigOn, rigOff, ("IsLocal", AnimatorConditionMode.IfNot, 0));

            // Following the camera (the world origin's source) or resting at the hips (the second source).
            var follow = Layer(controller, "Capture follow");
            AnimatorState FollowState(string name, bool on)
            {
                var clip = new AnimationClip { name = "Capture " + name.ToLowerInvariant() };
                Curve(clip, CaptureRigPath, typeof(VRCParentConstraint), "Sources.source0.Weight", on ? 1 : 0);
                Curve(clip, CaptureRigPath, typeof(VRCParentConstraint), "Sources.source1.Weight", on ? 0 : 1);
                return HeldPropRig.State(controller, follow, name, clip);
            }
            var resting = FollowState("At the hips", false);
            var following = FollowState("Following", true);
            follow.defaultState = resting;
            Transition(resting, following, (CaptureFollow, AnimatorConditionMode.If, 0));
            Transition(following, resting, (CaptureFollow, AnimatorConditionMode.IfNot, 0));

            // Where it stands while following.
            var pose = Layer(controller, "Capture rig pose");
            var tree = Hidden(new BlendTree { name = "Capture rig pose", blendType = BlendTreeType.Direct });
            var children = new List<ChildMotion>();
            foreach (var (parameter, property, reach) in new[]
                     {
                         (CaptureRigX, "ParentPositionOffset.x", RigReachMetres), (CaptureRigY, "ParentPositionOffset.y", RigReachMetres),
                         (CaptureRigZ, "ParentPositionOffset.z", RigReachMetres), (CaptureRigYaw, "ParentRotationOffset.y", RigYawDegrees),
                     })
            {
                var axis = Hidden(new BlendTree { name = parameter, blendType = BlendTreeType.Simple1D, blendParameter = parameter, useAutomaticThresholds = false });
                foreach (float end in new[] { -1f, 1f })
                {
                    var clip = Hidden(new AnimationClip { name = parameter + (end < 0 ? " -" : " +") });
                    Curve(clip, CaptureRigPath, typeof(VRCParentConstraint), "Sources.source0." + property, end * reach);
                    axis.AddChild(clip, end);
                }
                children.Add(new ChildMotion { motion = axis, directBlendParameter = One, timeScale = 1 });
            }
            tree.children = children.ToArray();
            var placed = HeldPropRig.State(controller, pose, "Capture rig pose", tree);
            placed.writeDefaultValues = true;

            // Local, never saved, never synced: Orbiters - N4N0 sets them over OSC (expression parameters all the same).
            parameters.Add(Parameter(CaptureRecord, synced: false));
            parameters.Add(Parameter(CaptureDepth, synced: false));
            parameters.Add(Parameter(CaptureFollow, synced: false));
            parameters.Add(Parameter(CaptureRig, synced: false));
            foreach (string name in new[] { CaptureRigX, CaptureRigY, CaptureRigZ, CaptureRigYaw })
                parameters.Add(Parameter(name, synced: false, type: VRCExpressionParameters.ValueType.Float));
            // Records the world with VRChat's camera through Orbiters - N4N0 (its window or SteamVR tab do too).
            controls.Add(Control("Record", VRCExpressionsMenu.Control.ControlType.Toggle, CaptureRecord));
        }
    }

    /// <summary>
    /// Makes each N4N0's capture kit for the avatar being built, on PC, and takes the capture part out of phone builds
    /// (<see cref="OrbInstaller.BuildCaptureKits"/>).
    /// </summary>
    internal sealed class OrbCaptureHook : IVRCSDKPreprocessAvatarCallback
    {
        // After the platform's N4N0 is kept (-10200), before the Toolkit's refit and attachment steps (-10110 to -10100) and
        // VRCFury (-10000), which then finds the kit where N4N0's controller animates it.
        public int callbackOrder => -10150;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            OrbInstaller.BuildCaptureKits(avatarRoot, OrbInstaller.MobileTarget);
            return true;
        }
    }
}
