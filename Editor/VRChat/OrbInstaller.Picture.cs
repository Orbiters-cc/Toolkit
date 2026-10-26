using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's full pictures (Orbiters contracts, Docs/orb-picture.md; screen version 8): the pictures the screen shows (the
    /// results' thumbnails, the music's art, the link card's picture) at the size of their place, kept and decoded by two
    /// Custom Render Textures on the avatar. Orbiters - N4N0 sends them in chunks: through the text channel's pointers 188 to
    /// 251 for everyone (<see cref="PictureFirstPointer"/>), through 64 local values for the owner (<see cref="PictureValue"/>,
    /// much faster). N4N0's animator copies the current chunks onto a small renderer of its own, the bridge
    /// (<see cref="PicturesObject"/>, on only while chunks flow), which writes them as pixels into a slot of the frame and
    /// grabs it; CRT A (<see cref="PictureDataFile"/>) finds them there, keeps each picture's stream and decodes one picture at
    /// a time; CRT B (<see cref="PicturesFile"/>) keeps the decoded pictures, which the screen's shader draws over the
    /// thumbnails they belong to while B is alive (the thumbnails show otherwise: phones, hidden custom shaders, a frozen
    /// texture). No synced bit: the local values cost nothing, the synced chunks ride the text channel.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>The owner's picture chunks (local floats, set by Orbiters - N4N0): <c>N4N0/Picture/Value0</c> to <c>Value63</c>.</summary>
        public const string PictureValuePrefix = "N4N0/Picture/Value";
        /// <summary>Local, set by Orbiters - N4N0: picture chunks flow through the local values (the owner's bridge stays on).</summary>
        public const string PictureSend = "N4N0/Picture/Send";
        public const int PictureLocalValues = 64;
        /// <summary><see cref="TextPointer"/> of the first picture chunk; 64 pointers (slot × 8 + the chunk's low bits).</summary>
        public const int PictureFirstPointer = 188, PicturePointers = 64;
        /// <summary>The synced values the bridge carries at most (text slots of 8 or 16).</summary>
        internal const int PictureSyncedValues = 16;
        /// <summary>The bridge: a quad below N4N0's head, the write and restore materials on its two parts.</summary>
        internal const string PicturesObject = Head + "/Pictures";
        // Around N4N0's head: the owner's eyes always inside, so the bridge is never culled for them.
        internal const float PictureBounds = 2f;
        // The shaders' templates and the contracts' include (a copy of Orbiters contracts' Shaders/OrbitersPicture.cginc).
        private const string PictureWriteTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbPictureWrite.shader.txt";
        private const string PictureRestoreTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbPictureRestore.shader.txt";
        private const string PictureDataTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbPictureData.shader.txt";
        private const string PicturesTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbPictures.shader.txt";
        internal const string PictureIncludeTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbitersPicture.cginc.txt";
        // The files in a screen folder.
        internal const string PictureDataFile = "N4N0 picture data", PicturesFile = "N4N0 pictures", PictureBridgeFile = "N4N0 picture bridge";
        /// <summary>The screen material's texture property holding CRT B.</summary>
        internal const string PicturesProperty = "_Pictures";
        private const string PictureDataProperty = "_PictureData";
        // The textures' sizes (contracts Docs/orb-picture.md, CRT A and CRT B).
        internal const int PictureDataWidth = 512, PictureDataHeight = 256, PicturesWidth = 512, PicturesHeight = 290;

        public static string PictureValue(int index) => PictureValuePrefix + index;
        // The bridge's material properties: the synced gate, the synced values, the local values.
        internal static string PictureSyncedProperty(int i) => "_PictureS" + i;
        internal static string PictureLocalProperty(int i) => "_PictureL" + i;
        internal const string PictureGateProperty = "_PictureGate";

        /// <summary>Whether a placed orb has the bridge.</summary>
        internal static bool HasPictures(Transform orb) => orb != null && orb.Find(PicturesObject) != null;

        /// <summary>
        /// Whether everyone else gets full pictures from N4N0 with <paramref name="textSync"/> text values and thumbnails of
        /// <paramref name="thumbnailBytes"/>: 8 or 16 values, and the screen's and the card's blocks below the picture pointers
        /// (contracts orb::picture::chunk::reaches_everyone). With 4 the pictures reach their owner only.
        /// </summary>
        internal static bool PicturesReachEveryone(int textSync, int thumbnailBytes)
        {
            if (textSync != 8 && textSync != 16) return false;
            var (first, blocks) = CardPointers(textSync, thumbnailBytes, true);
            return first + blocks <= PictureFirstPointer;
        }

        // ---- The shared files ----

        /// <summary>
        /// The pictures' files in <paramref name="folder"/>, made once: the two textures with their shaders and materials, and
        /// the bridge's mesh and materials (which hold both textures: CRT A gives the bridge its key).
        /// </summary>
        internal static (CustomRenderTexture data, CustomRenderTexture pictures, Mesh bridge, Material write, Material restore) PictureAssets(string folder = ScreenFolder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.ImportAsset(folder);
            }
            var data = PictureTexture(folder, PictureDataFile, PictureDataTemplate, "Orbiters/N4N0 Picture Data", PictureDataWidth, PictureDataHeight, RenderTextureFormat.ARGBFloat, null);
            var pictures = PictureTexture(folder, PicturesFile, PicturesTemplate, "Orbiters/N4N0 Pictures", PicturesWidth, PicturesHeight, RenderTextureFormat.ARGB32, data);
            string meshPath = folder + "/" + PictureBridgeFile + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = PictureBridgeMesh();
                AssetDatabase.CreateAsset(mesh, meshPath);
            }
            var write = PictureBridgeMaterial(folder, " write", PictureWriteTemplate, "Orbiters/N4N0 Picture Bridge Write", data, pictures);
            var restore = PictureBridgeMaterial(folder, " restore", PictureRestoreTemplate, "Orbiters/N4N0 Picture Bridge Restore", data, null);
            AssetDatabase.SaveAssets();
            return (data, pictures, mesh, write, restore);
        }

        /// <summary>CRT B of a screen folder (the screen's and the card's materials sample it).</summary>
        internal static Texture PicturesTexture(string folder) => PictureAssets(folder).pictures;

        // A double-buffered texture updated every frame, point filtered, black at load, with its shader written in the folder.
        private static CustomRenderTexture PictureTexture(string folder, string name, string template, string shaderName, int width, int height, RenderTextureFormat format,
            CustomRenderTexture source)
        {
            string path = folder + "/" + name + ".asset", materialPath = folder + "/" + name + ".mat", shaderPath = folder + "/" + name + ".shader";
            var texture = AssetDatabase.LoadAssetAtPath<CustomRenderTexture>(path);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (texture != null && material != null && material.shader != null && material.shader.isSupported && texture.material == material) return texture;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || !shader.isSupported) shader = PictureShader(template, shaderPath, shaderName);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.shader = shader;
            // CRT A's key seed: another per folder, so avatars made apart rarely share a slot of the frame.
            if (source == null) material.SetFloat("_PictureSeed", UnityEngine.Random.Range(1f, 65535f));
            else material.SetTexture(PictureDataProperty, source);
            EditorUtility.SetDirty(material);
            if (texture == null)
            {
                texture = new CustomRenderTexture(width, height, format, RenderTextureReadWrite.Linear) { name = name };
                AssetDatabase.CreateAsset(texture, path);
            }
            texture.material = material;
            texture.updateMode = CustomRenderTextureUpdateMode.Realtime;
            texture.updatePeriod = 0;
            texture.initializationMode = CustomRenderTextureUpdateMode.OnLoad;
            texture.initializationSource = CustomRenderTextureInitializationSource.TextureAndColor;
            texture.initializationColor = Color.clear;
            texture.doubleBuffered = true;
            texture.wrapUpdateZones = false;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.useMipMap = false;
            texture.anisoLevel = 0;
            EditorUtility.SetDirty(texture);
            return texture;
        }

        private static Material PictureBridgeMaterial(string folder, string suffix, string template, string shaderName, CustomRenderTexture data, CustomRenderTexture pictures)
        {
            string name = PictureBridgeFile + suffix, path = folder + "/" + name + ".mat", shaderPath = folder + "/" + name + ".shader";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null && material.shader != null && material.shader.isSupported && material.GetTexture(PictureDataProperty) == data) return material;
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || !shader.isSupported) shader = PictureShader(template, shaderPath, shaderName);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture(PictureDataProperty, data);
            if (pictures != null) material.SetTexture(PicturesProperty, pictures);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// A picture shader's text: the template with the contracts' include written in, and the bridge's value properties
        /// (each its own register, which the include reads as one array).
        /// </summary>
        internal static string PictureShaderText(string template, string include, string name)
        {
            var properties = new StringBuilder();
            var registers = new StringBuilder();
            var sum = new StringBuilder();
            properties.AppendLine($"        [HideInInspector] {PictureGateProperty} (\"Gate\", Float) = 0");
            registers.AppendLine($"float {PictureGateProperty} : packoffset(c0);");
            sum.Append($"    + {PictureGateProperty}");
            for (int i = 0; i < PictureSyncedValues; i++)
            {
                properties.AppendLine($"        [HideInInspector] {PictureSyncedProperty(i)} (\"Synced {i}\", Float) = 0");
                registers.AppendLine($"float {PictureSyncedProperty(i)} : packoffset(c{1 + i});");
                sum.Append($" + {PictureSyncedProperty(i)}");
            }
            for (int i = 0; i < PictureLocalValues; i++)
            {
                properties.AppendLine($"        [HideInInspector] {PictureLocalProperty(i)} (\"Local {i}\", Float) = 0");
                registers.AppendLine($"float {PictureLocalProperty(i)} : packoffset(c{1 + PictureSyncedValues + i});");
                sum.Append($" + {PictureLocalProperty(i)}");
                if (i % 16 == 15) sum.AppendLine().Append("   ");
            }
            string text = CaptureShaderText(template, name, file =>
            {
                if (file != "OrbitersPicture.cginc") throw new InvalidOperationException("A picture shader includes " + file + ".");
                // Each value also summed where nothing reads it: an unused property is stripped, and Unity never sets it.
                return include.Replace("//@PICTURE_VALUES", registers.ToString().TrimEnd()).Replace("//@PICTURE_SUM", sum.ToString().TrimEnd());
            });
            // One kind of line ending (the generated lines' differ from the template's), or Unity warns about the file.
            return text.Replace("//@PROPERTIES", properties.ToString().TrimEnd()).Replace("\r\n", "\n");
        }

        private static Shader PictureShader(string template, string path, string name)
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(template);
            var include = AssetDatabase.LoadAssetAtPath<TextAsset>(PictureIncludeTemplate);
            if (text == null || include == null) throw new InvalidOperationException("Missing " + (text == null ? template : PictureIncludeTemplate) + ": reinstall Orbiters Toolkit.");
            System.IO.File.WriteAllText(path, PictureShaderText(text.text, include.text, name));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Unity did not import " + path + ".");
            return shader;
        }

        /// <summary>The screen's code that reads CRT B (the include's screen part), for the screen shader's <c>//@PICTURES</c>.</summary>
        internal static string PictureScreenCode()
        {
            var include = AssetDatabase.LoadAssetAtPath<TextAsset>(PictureIncludeTemplate);
            if (include == null) throw new InvalidOperationException("Missing " + PictureIncludeTemplate + ": reinstall Orbiters Toolkit.");
            return "            #define PICTURE_SCREEN\n" + include.text.Replace("\r\n", "\n");
        }

        // A quad (its first UV the slot's corners) in two parts for the two materials, with bounds around N4N0's head.
        private static Mesh PictureBridgeMesh()
        {
            var mesh = new Mesh { name = PictureBridgeFile };
            mesh.vertices = new[] { new Vector3(-.005f, -.005f, 0), new Vector3(.005f, -.005f, 0), new Vector3(-.005f, .005f, 0), new Vector3(.005f, .005f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0, false);
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 1, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * PictureBounds);
            return mesh;
        }

        // ---- On a placed orb ----

        /// <summary>
        /// Adds the bridge under N4N0's head (off: the animator shows it while chunks flow) and gives the screen's and the
        /// card's materials CRT B. Does nothing on N4N0 that has it.
        /// </summary>
        internal static Transform AddPictures(Transform orb, string folder = ScreenFolder)
        {
            var existing = orb.Find(PicturesObject);
            var (_, pictures, mesh, write, restore) = PictureAssets(folder);
            foreach (var path in new[] { ScreenObject, CardModel })
            {
                var part = orb.Find(path);
                if (part == null || !part.TryGetComponent(out SkinnedMeshRenderer renderer)) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty(PicturesProperty) || material.GetTexture(PicturesProperty) != null) continue;
                    material.SetTexture(PicturesProperty, pictures);
                    EditorUtility.SetDirty(material);
                }
            }
            if (existing != null)
            {
                SyncPictures(orb);
                return existing;
            }
            var head = orb.Find(Head);
            if (head == null) throw new InvalidOperationException("This N4N0 was modified: add it again.");
            var bridge = new GameObject("Pictures");
            Undo.RegisterCreatedObjectUndo(bridge, "N4N0 pictures");
            bridge.transform.SetParent(head, false);
            bridge.SetActive(false);
            bridge.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer2 = bridge.AddComponent<MeshRenderer>();
            renderer2.sharedMaterials = new[] { write, restore };
            Quiet(renderer2);
            renderer2.allowOcclusionWhenDynamic = false;
            renderer2.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            SyncPictures(orb);
            return bridge.transform;
        }

        /// <summary>
        /// The bridge feeds the textures the screen shows: its materials (which give it CRT A's key) are those of the folder
        /// holding the CRT B the screen's material samples (a theme's or another thumbnail size's material may come from
        /// another screen folder than the bridge's). Run before each upload too (<see cref="OrbPicturesHook"/>).
        /// </summary>
        internal static void SyncPictures(Transform orb)
        {
            var bridge = orb != null ? orb.Find(PicturesObject) : null;
            var screen = ScreenRenderer(orb);
            if (bridge == null || screen == null || !bridge.TryGetComponent(out MeshRenderer renderer) || !bridge.TryGetComponent(out MeshFilter filter)) return;
            var material = screen.sharedMaterial;
            if (material == null || !material.HasProperty(PicturesProperty) || !(material.GetTexture(PicturesProperty) is CustomRenderTexture pictures)) return;
            string path = AssetDatabase.GetAssetPath(pictures);
            if (string.IsNullOrEmpty(path)) return;
            var (_, _, mesh, write, restore) = PictureAssets(System.IO.Path.GetDirectoryName(path).Replace(System.IO.Path.DirectorySeparatorChar, '/'));
            var materials = renderer.sharedMaterials;
            if (materials.Length != 2 || materials[0] != write || materials[1] != restore) renderer.sharedMaterials = new[] { write, restore };
            if (filter.sharedMesh != mesh) filter.sharedMesh = mesh;
        }

        // ---- The animator ----

        /// <summary>
        /// The pictures' parameters, layers and expression parameters, added to N4N0's controller: the owner's 64 local values
        /// and <see cref="PictureSend"/>; "Pictures gate" (with 8 or 16 text values: the text pointer, one update late, as the
        /// bridge's gate: 1 + its offset past <see cref="PictureFirstPointer"/>, + 64 with 16 values); "Pictures" (the bridge
        /// shown while a picture pointer is up or the owner's chunks flow, the values copied onto it: the synced ones as the
        /// text's late values, as the screen reads them, the local ones as they are).
        /// </summary>
        internal static void PictureController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters, int textSync, int thumbnailBytes)
        {
            for (int i = 0; i < PictureLocalValues; i++) controller.AddParameter(PictureValue(i), AnimatorControllerParameterType.Float);
            controller.AddParameter(PictureSend, AnimatorControllerParameterType.Bool);
            bool everyone = PicturesReachEveryone(textSync, thumbnailBytes);

            if (everyone)
            {
                var gate = Layer(controller, "Pictures gate");
                AnimatorState Set(string name, float value)
                {
                    var clip = new AnimationClip { name = "Pictures gate " + name };
                    Curve(clip, PicturesObject, typeof(MeshRenderer), "material." + PictureGateProperty, value);
                    return HeldPropRig.State(controller, gate, name, Hidden(clip));
                }
                var none = Set("None", 0);
                gate.defaultState = none;
                for (int a = 0; a < PicturePointers; a++)
                    Jump(gate, Set("Chunk " + a, 1 + a + (textSync == 16 ? 64 : 0)), (TextPointer, AnimatorConditionMode.Equals, PictureFirstPointer + a));
                Jump(gate, none, (TextPointer, AnimatorConditionMode.Less, PictureFirstPointer));
                Jump(gate, none, (TextPointer, AnimatorConditionMode.Greater, PictureFirstPointer + PicturePointers - 1));
            }

            var machine = Layer(controller, "Pictures");
            AnimationClip Property(string property, float value)
            {
                var clip = Hidden(new AnimationClip { name = "Pictures " + property + " " + value });
                Curve(clip, PicturesObject, typeof(MeshRenderer), "material." + property, value);
                return clip;
            }
            var tree = Hidden(new BlendTree { name = "Pictures", blendType = BlendTreeType.Direct });
            var children = new List<ChildMotion>();
            void Add(Motion motion) => children.Add(new ChildMotion { motion = motion, directBlendParameter = One, timeScale = 1 });
            var shown = Hidden(new AnimationClip { name = "Pictures shown" });
            Curve(shown, PicturesObject, typeof(GameObject), "m_IsActive", 1);
            Add(shown);
            if (everyone)
                for (int i = 0; i < textSync; i++)
                    Add(Copy("Pictures synced " + i, TextLate(i, ValueLag), Property(PictureSyncedProperty(i), -1), Property(PictureSyncedProperty(i), 1)));
            for (int i = 0; i < PictureLocalValues; i++)
                Add(Copy("Pictures local " + i, PictureValue(i), Property(PictureLocalProperty(i), -1), Property(PictureLocalProperty(i), 1)));
            tree.children = children.ToArray();
            var hidden = new AnimationClip { name = "Pictures hidden" };
            Curve(hidden, PicturesObject, typeof(GameObject), "m_IsActive", 0);
            var off = HeldPropRig.State(controller, machine, "Off", hidden);
            var on = HeldPropRig.State(controller, machine, "On", tree);
            off.writeDefaultValues = true; on.writeDefaultValues = true;
            machine.defaultState = off;
            Transition(off, on, (PictureSend, AnimatorConditionMode.If, 0));
            if (everyone)
                Transition(off, on, (TextPointer, AnimatorConditionMode.Greater, PictureFirstPointer - 1), (TextPointer, AnimatorConditionMode.Less, PictureFirstPointer + PicturePointers));
            Transition(on, off, (PictureSend, AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Less, PictureFirstPointer));
            Transition(on, off, (PictureSend, AnimatorConditionMode.IfNot, 0), (TextPointer, AnimatorConditionMode.Greater, PictureFirstPointer + PicturePointers - 1));

            for (int i = 0; i < PictureLocalValues; i++) parameters.Add(Parameter(PictureValue(i), synced: false, type: VRCExpressionParameters.ValueType.Float));
            parameters.Add(Parameter(PictureSend, synced: false));
        }
    }

    /// <summary>Before each upload, N4N0's bridge feeds the textures its screen shows (<see cref="OrbInstaller.SyncPictures"/>).</summary>
    internal sealed class OrbPicturesHook : IVRCSDKPreprocessAvatarCallback
    {
        // After the platform's N4N0 is kept (-10200) and its capture kit is built (-10150), before VRCFury (-10000).
        public int callbackOrder => -10140;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            foreach (var orb in OrbInstaller.FindAll(avatarRoot)) OrbInstaller.SyncPictures(orb);
            return true;
        }
    }
}
