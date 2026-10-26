using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

// N4N0's full pictures (Orbiters contracts, Docs/orb-picture.md) in Unity: the shaders written from their templates, the
// animator's parameters and layers, and the bridge and both Custom Render Textures' shaders turning a vector picture's
// chunks (contracts TestVectors~/orb_picture.json, copied here) into its picture in CRT B, as the contracts' WARP test
// does outside Unity.
public class OrbPictureTests
{
    private const string VectorsPath = "Packages/orbiters.toolkit/Tests/Editor/OrbPictureVectors.json";
    private string folder;
    private Scene scene;
    private bool asyncCompilation;
    private readonly List<Object> owned = new List<Object>();

    [SetUp]
    public void SetUp()
    {
        folder = "Assets/__OrbitersPictureTest-" + Guid.NewGuid().ToString("N");
        System.IO.Directory.CreateDirectory(folder);
        AssetDatabase.ImportAsset(folder);
        scene = EditorSceneManager.NewPreviewScene();
        asyncCompilation = ShaderUtil.allowAsyncCompilation;
    }

    [TearDown]
    public void TearDown()
    {
        ShaderUtil.allowAsyncCompilation = asyncCompilation;
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
    }

    [Test]
    public void TheBridgeShaderNamesEveryValueInARegisterOfItsOwn()
    {
        var template = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/orbiters.toolkit/Editor/VRChat/OrbPictureWrite.shader.txt").text;
        var include = AssetDatabase.LoadAssetAtPath<TextAsset>(OrbInstaller.PictureIncludeTemplate).text;
        string text = OrbInstaller.PictureShaderText(template, include, "Test/Bridge");
        StringAssert.StartsWith("// N4N0's full pictures", text);
        StringAssert.Contains("Shader \"Test/Bridge\"", text);
        StringAssert.DoesNotContain("#include \"OrbitersPicture.cginc\"", text, "the include written in");
        StringAssert.DoesNotContain("//@", text, "every placeholder filled");
        StringAssert.Contains("float _PictureGate : packoffset(c0);", text);
        StringAssert.Contains("float _PictureS15 : packoffset(c16);", text);
        StringAssert.Contains("float _PictureL0 : packoffset(c17);", text);
        StringAssert.Contains("float _PictureL63 : packoffset(c80);", text, "the gate, 16 synced and 64 local values: the 81 bytes of a slot");
        StringAssert.Contains("[HideInInspector] _PictureL63 (\"Local 63\", Float) = 0", text);
        StringAssert.Contains("+ _PictureGate + _PictureS0", text, "every value used, so Unity sets it");
        StringAssert.Contains("+ _PictureL63", text);
        StringAssert.Contains("GrabPass { \"_N4N0PictureOriginal\" }", text);
    }

    [Test]
    public void PicturesReachEveryoneWithEightOrSixteenTextSlots()
    {
        foreach (int sync in new[] { 8, 16 })
        foreach (int bytes in new[] { 72, 128 })
            Assert.IsTrue(OrbInstaller.PicturesReachEveryone(sync, bytes), sync + " slots, " + bytes + " B");
        Assert.IsFalse(OrbInstaller.PicturesReachEveryone(4, 72), "with 4 the card's blocks take the picture pointers");
        Assert.AreEqual(252, OrbInstaller.PictureFirstPointer + OrbInstaller.PicturePointers, "up to the card's clear");
    }

    [Test]
    public void TheControllerCarriesTheOwnersValuesAndTheGate()
    {
        foreach (int sync in new[] { 4, 8, 16 })
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Pictures " + sync + ".controller");
            var parameters = new List<VRCExpressionParameters.Parameter>();
            OrbInstaller.PictureController(controller, parameters, sync, 72);
            for (int i = 0; i < OrbInstaller.PictureLocalValues; i++)
                Assert.AreEqual(AnimatorControllerParameterType.Float, controller.parameters.Single(p => p.name == OrbInstaller.PictureValue(i)).type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, controller.parameters.Single(p => p.name == OrbInstaller.PictureSend).type);
            Assert.AreEqual(65, parameters.Count);
            Assert.IsTrue(parameters.All(p => !p.networkSynced && !p.saved), "local and unsaved: no synced bit");
            var layers = controller.layers.Select(l => l.name).ToList();
            Assert.Contains("Pictures", layers);
            var pictures = controller.layers.Single(l => l.name == "Pictures").stateMachine;
            CollectionAssert.AreEquivalent(new[] { "Off", "On" }, pictures.states.Select(s => s.state.name));
            var on = pictures.states.Single(s => s.state.name == "On").state;
            var tree = (BlendTree)on.motion;
            // Shown, the synced values (with 8 or 16 slots), the 64 local values.
            Assert.AreEqual(1 + (sync == 4 ? 0 : sync) + OrbInstaller.PictureLocalValues, tree.children.Length);
            if (sync == 4)
            {
                Assert.IsFalse(layers.Contains("Pictures gate"), "4 slots: the pictures reach their owner only");
                continue;
            }
            var gate = controller.layers.Single(l => l.name == "Pictures gate").stateMachine;
            Assert.AreEqual(1 + OrbInstaller.PicturePointers, gate.states.Length);
            var last = gate.states.Single(s => s.state.name == "Chunk 63").state;
            var curve = AnimationUtility.GetEditorCurve((AnimationClip)last.motion, AnimationUtility.GetCurveBindings((AnimationClip)last.motion).Single());
            Assert.AreEqual(64 + (sync == 16 ? 64 : 0), curve.keys[0].value, "the gate: 1 + the pointer's offset, + 64 with 16 values");
        }
    }

    [Test]
    public void AScreenMaterialShowsItsFoldersPicturesAndReadsItsDataForTheDiagnostics()
    {
        ShaderUtil.allowAsyncCompilation = false;
        var (_, material) = OrbInstaller.ScreenAssets(folder, 128);
        var (data, pictures, _, _, _) = OrbInstaller.PictureAssets(folder);
        Assert.AreEqual(pictures, material.GetTexture(OrbInstaller.PicturesProperty), "B, which it shows");
        Assert.AreEqual(data, material.GetTexture(OrbInstaller.PictureDataProperty), "A, which its diagnostics read");
        Assert.IsFalse(ShaderUtil.ShaderHasError(material.shader), "the screen with its diagnostics compiles");
        Assert.AreEqual(OrbInstaller.ScreenRevision, material.GetTag("OrbScreen", false));
    }

    [Test]
    public void AVectorPicturesLocalChunksBecomeItsPictureInCrtB()
    {
        ShaderUtil.allowAsyncCompilation = false;
        var vectors = (Dictionary<string, object>)MiniJson.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>(VectorsPath).text);
        var vector = ((List<object>)vectors["pictures"]).Cast<Dictionary<string, object>>().First(p => Convert.ToInt32(p["slot"]) == 2);
        var chunks = ((List<object>)vector["local_chunks"]).Select(c => ((List<object>)c).Select(Convert.ToInt32).ToArray()).ToList();
        var signature = ((List<object>)vector["signature"]).Select(Convert.ToInt32).ToArray();
        var whole = ((List<object>)vector["layers"]).Cast<Dictionary<string, object>>().Single(l => Convert.ToInt32(l["layers"]) == 3);

        var (data, pictures, mesh, write, restore) = OrbInstaller.PictureAssets(folder);
        // Unity runs Custom Render Texture updates only in the editor's player loop, which an editor in the background
        // never runs, so each CRT's pass runs here, once a frame, as a CRT runs it.
        var a = new CrtPass(data, owned);
        var b = new CrtPass(pictures, owned);
        var camera = NewObject("Camera").AddComponent<Camera>();
        camera.enabled = false;
        camera.scene = scene;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.3f, .45f, .65f, 1);
        camera.transform.position = new Vector3(0, 0, -1);
        var frame = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
        owned.Add(frame);
        camera.targetTexture = frame;
        var bridge = NewObject("Pictures");
        bridge.AddComponent<MeshFilter>().sharedMesh = mesh;
        // Copies of the bridge's materials: the values set here never reach the folder's, and both read A's latest.
        var values = new Material(write);
        var restoring = new Material(restore);
        owned.Add(values);
        owned.Add(restoring);
        bridge.AddComponent<MeshRenderer>().sharedMaterials = new[] { values, restoring };

        // A frame: the bridge drawn and grabbed (its slot placed by A's key), then A (that grab), then B (A's latest).
        void Frame()
        {
            values.SetTexture("_PictureData", a.Latest);
            restoring.SetTexture("_PictureData", a.Latest);
            camera.Render();
            a.Run();
            b.Material.SetTexture("_PictureData", a.Latest);
            b.Run();
        }
        // The key first (A picks it at its first update; the bridge reads it a frame later).
        for (int i = 0; i < 3; i++) Frame();
        // Each local chunk for two frames, as the owner's animator holds them.
        foreach (var chunk in chunks)
        {
            for (int i = 0; i < chunk.Length; i++) values.SetFloat("_PictureL" + i, chunk[i] <= 127 ? chunk[i] / 127f : (chunk[i] - 256) / 127f);
            Frame();
            Frame();
        }
        // Jobs decode (18 frames) and hold while B takes them (12): the first may have started with fewer layers whole, so
        // up to three in turn.
        for (int i = 0; i < 120; i++) Frame();

        var shown = Read(b.Latest);
        // B's rows as Unity reads them back: its heartbeat's texel says which way up.
        Func<int, int, Color32> at = (x, y) => shown[(shown[289 * 512 + 511].a == 255 ? y : 289 - y) * 512 + x];
        var state = at(2 * 8, 289);
        Assert.AreEqual((1, 3, 0), ((int)state.r, (int)state.g, (int)state.b), "slot 2 shows its three layers");
        var sig = new[] { at(2 * 8 + 1, 289), at(2 * 8 + 2, 289) }.SelectMany(c => new int[] { c.r, c.g, c.b, c.a }).ToArray();
        CollectionAssert.AreEqual(signature, sig, "with its thumbnail's signature");
        foreach (Dictionary<string, object> sample in (List<object>)whole["samples"])
        {
            int x = Convert.ToInt32(sample["x"]), y = Convert.ToInt32(sample["y"]);
            var ycc = ((List<object>)sample["ycc"]).Select(Convert.ToInt32).ToArray();
            var want = Rgb(ycc);
            var got = at(256 + x, 144 + y);
            for (int c = 0; c < 3; c++)
                Assert.That(Mathf.Abs(want[c] - new[] { got.r, got.g, got.b }[c]), Is.LessThanOrEqualTo(1), "slot 2's pixel (" + x + ", " + y + ") channel " + c);
        }
    }

    private GameObject NewObject(string name)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, scene);
        owned.Add(go);
        return go;
    }

    // The contracts' YCbCr to sRGB bytes (JPEG's matrix), as CRT B writes them.
    private static int[] Rgb(int[] ycc)
    {
        double y = ycc[0] / 255.0, cb = ycc[1] / 255.0, cr = ycc[2] / 255.0;
        double r = y + 1.402 * cr, b = y + 1.772 * cb;
        double g = (y - .299 * r - .114 * b) / .587;
        return new[] { r, g, b }.Select(v => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255)).ToArray();
    }

    private Color32[] Read(RenderTexture texture)
    {
        var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
        owned.Add(read);
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
        read.Apply();
        RenderTexture.active = previous;
        return read.GetPixels32();
    }

    // A Custom Render Texture's pass run as Unity runs it (UnityCustomRenderTexture.cginc's vertex shader): one update
    // zone over the whole texture, double buffered, its last update as _SelfTexture2D.
    private sealed class CrtPass
    {
        private readonly RenderTexture[] buffers = new RenderTexture[2];
        private int latest;

        public CrtPass(CustomRenderTexture texture, List<Object> owned)
        {
            Material = new Material(texture.material);
            owned.Add(Material);
            for (int i = 0; i < 2; i++)
            {
                buffers[i] = new RenderTexture(texture.width, texture.height, 0, texture.graphicsFormat)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                buffers[i].Create();
                owned.Add(buffers[i]);
                var previous = RenderTexture.active;
                RenderTexture.active = buffers[i];
                GL.Clear(false, true, Color.clear);
                RenderTexture.active = previous;
            }
            // The batch of 16 zones the include declares, the first (the only one drawn) the whole texture.
            Material.SetVectorArray("CustomRenderTextureCenters", Enumerable.Repeat(new Vector4(.5f, .5f, .5f, 0), 16).ToArray());
            Material.SetVectorArray("CustomRenderTextureSizesAndRotations", Enumerable.Repeat(new Vector4(1, 1, 1, 0), 16).ToArray());
            Material.SetFloatArray("CustomRenderTexturePrimitiveIDs", new float[16]);
            Material.SetVector("CustomRenderTextureParameters", Vector4.zero);
            Material.SetVector("_CustomRenderTextureInfo", new Vector4(texture.width, texture.height, 1, 0));
        }

        public Material Material { get; }

        public RenderTexture Latest => buffers[latest];

        public void Run()
        {
            Material.SetTexture("_SelfTexture2D", buffers[latest]);
            var commands = new CommandBuffer();
            commands.SetRenderTarget(buffers[1 - latest]);
            commands.DrawProcedural(Matrix4x4.identity, Material, 0, MeshTopology.Triangles, 6);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
            latest = 1 - latest;
        }
    }

    // Just enough JSON for the vectors: objects, arrays, numbers, strings, true, false, null.
    private static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            return Value(text, ref i);
        }

        private static void Space(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Space(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++;
                Space(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    var key = (string)Value(s, ref i);
                    Space(s, ref i);
                    i++; // ':'
                    o[key] = Value(s, ref i);
                    Space(s, ref i);
                    if (s[i++] == '}') return o;
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++;
                Space(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Space(s, ref i);
                    if (s[i++] == ']') return a;
                }
            }
            if (c == '"')
            {
                int start = ++i;
                while (s[i] != '"') i++;
                return s.Substring(start, i++ - start);
            }
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int from = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(from, i - from), CultureInfo.InvariantCulture);
        }
    }
}
