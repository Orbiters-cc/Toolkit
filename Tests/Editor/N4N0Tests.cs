using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

// N4N0, the Orbiters companion (contracts Docs/n4n0.md), built whole into a temporary folder: its parameters as the desktop
// finds them, the text protocol on the panel's cells, its states, and its fit on an avatar.
public class N4N0Tests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;
    private PlayableGraph graph;
    private AnimatorControllerPlayable playable;
    private readonly List<Object> owned = new List<Object>();

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersN4N0Test-" + Guid.NewGuid().ToString("N");
        scene = EditorSceneManager.NewPreviewScene();
    }

    [TearDown]
    public void TearDown()
    {
        if (graph.IsValid()) graph.Destroy();
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        sandbox.End();
    }

    [Test]
    public void ItCarriesTheContractsParametersUnderTheirOwnNames()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        Standalone(prefab);
        Assert.AreEqual(prefab.transform, N4N0Installer.FindAll(prefab).Single(), "Found by its touch contact");

        var expected = new List<(string name, VRCExpressionParameters.ValueType type, bool synced, bool saved, float value)>
        {
            ("N4N0/Visible", VRCExpressionParameters.ValueType.Bool, true, true, 1),
            ("N4N0/State", VRCExpressionParameters.ValueType.Int, true, false, 0),
            ("N4N0/Talk", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Touch", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Photo3DPeople", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Photo3DWorld", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Menu", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Call/Menu", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Call/Pick", VRCExpressionParameters.ValueType.Float, false, false, 0),
            ("N4N0/Call/Go", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Call/End", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Text/Visible", VRCExpressionParameters.ValueType.Bool, true, false, 0),
            ("N4N0/Text/Pointer", VRCExpressionParameters.ValueType.Int, true, false, 0),
        };
        for (int i = 0; i < 8; i++) expected.Add(("N4N0/Text/Sync" + i, VRCExpressionParameters.ValueType.Float, true, false, 0));
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        // The screen's parameters follow (TheScreensTouchPointsAndParametersAreLocal).
        CollectionAssert.AreEqual(expected, parameters.parameters.Take(expected.Count).Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToList());
        Assert.IsTrue(parameters.parameters.Skip(expected.Count).All(p => p.name.StartsWith("N4N0/Screen/", StringComparison.Ordinal) || p.name.StartsWith("N4N0/Card/", StringComparison.Ordinal) ||
                                                                      p.name.StartsWith("N4N0/Capture/", StringComparison.Ordinal) || p.name.StartsWith("N4N0/Picture/", StringComparison.Ordinal) ||
                                                                      p.name.StartsWith("N4N0/Touchpad/", StringComparison.Ordinal) || p.name.StartsWith("N4N0/Stick/", StringComparison.Ordinal) ||
                                                                      p.name.StartsWith("N4N0/Mirror/", StringComparison.Ordinal)),
            "then the screen's, the link card's, the capture kit's (N4N0CaptureTests), the full pictures' (N4N0PictureTests), the controller input's and the screen mirror's (N4N0MirrorTests)");
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8 + 3 + 8 + 3 + 25, parameters.CalcTotalCost(), "Synced bits: 1 + 8 + 1 + 8 + 8·N, the screen's 3 + 8, the card's 3, the screen's place 24 + 1");

        // Global in the Full Controller, so VRCFury keeps the names OSC looks for; its own parameters are renamed as usual.
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (var p in expected) Assert.IsTrue(VrcFury.IsGlobalParameter(full, p.name), p.name + " is global");
        Assert.IsFalse(VrcFury.IsGlobalParameter(full, N4N0Installer.Cell(0)));
        Assert.IsTrue(expected.All(p => VrcParameters.OscDriven(p.name)), "Optimizers leave OSC's parameters alone");
        // The controller input (contracts' Docs/n4n0-input.md): local, never saved, at rest, global, left alone by optimizers.
        var inputs = parameters.parameters.Where(p => p.name.StartsWith("N4N0/Touchpad/", StringComparison.Ordinal) || p.name.StartsWith("N4N0/Stick/", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(N4N0Installer.InputParameters.ToList(), inputs.Select(p => p.name).ToList());
        Assert.IsTrue(inputs.All(p => !p.networkSynced && !p.saved && p.defaultValue == 0 && VrcFury.IsGlobalParameter(full, p.name) && VrcParameters.OscDriven(p.name)));
        Assert.AreEqual(8, inputs.Count(p => p.valueType == VRCExpressionParameters.ValueType.Bool));
        Assert.AreEqual(10, inputs.Count(p => p.valueType == VRCExpressionParameters.ValueType.Float));

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        var types = controller.parameters.ToDictionary(p => p.name, p => p.type);
        foreach (var p in expected)
            Assert.AreEqual(p.type == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int
                : p.type == VRCExpressionParameters.ValueType.Float ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool, types[p.name], p.name);
        Assert.IsTrue(controller.parameters.Single(p => p.name == N4N0Installer.Visible).defaultBool, "Shown until asked otherwise");

        var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single();
        Assert.AreEqual(("N4N0", VRCExpressionsMenu.Control.ControlType.SubMenu), (menu.name, menu.type));
        CollectionAssert.AreEqual(new[]
            {
                ("Show N4N0", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Visible"), ("Menu", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Menu"),
                ("Stay here", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Screen/Pinned"),
                ("Talk", VRCExpressionsMenu.Control.ControlType.Button, "N4N0/Talk"),
                ("3D picture of people", VRCExpressionsMenu.Control.ControlType.Button, "N4N0/Photo3DPeople"),
                ("3D picture of the world", VRCExpressionsMenu.Control.ControlType.Button, "N4N0/Photo3DWorld"),
                ("Call", VRCExpressionsMenu.Control.ControlType.SubMenu, ""),
                ("Record", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Capture/Record"),
            },
            menu.subMenu.controls.Select(c => (c.name, c.type, c.parameter.name)).ToArray());

        // The rig: the tether's tip carries N4N0, the owner's hands touch it, the panel waits off.
        var root = prefab.transform;
        var tether = root.Find(N4N0Installer.Tether).GetComponent<VRCPhysBone>();
        Assert.AreEqual(root.Find(N4N0Installer.Ball), tether.ignoreTransforms.Single(), "What N4N0 holds is no part of the chain");
        Assert.AreEqual(0, tether.gravity);
        var touch = root.Find(N4N0Installer.Toucher).GetComponent<VRCContactReceiver>();
        Assert.IsTrue(touch.allowSelf && !touch.allowOthers && touch.localOnly);
        CollectionAssert.AreEquivalent(new[] { "Hand", "Finger" }, touch.collisionTags);
        Assert.IsTrue(root.Find(N4N0Installer.Shown).gameObject.activeSelf);
        var panel = root.Find(N4N0Installer.Panel);
        Assert.IsFalse(panel.gameObject.activeSelf, "No text yet");
        var screen = panel.GetComponent<SkinnedMeshRenderer>();
        CollectionAssert.AreEqual(new[] { panel, root.Find(N4N0Installer.Emitter) }, screen.bones, "The beam reaches from N4N0 to the panel");
        Assert.IsTrue(screen.sharedMaterials.All(m => m.shader.name == "Orbiters/N4N0 Text" && m.shader.isSupported));
        var model = root.Find(N4N0Installer.Body).GetComponent<SkinnedMeshRenderer>();
        Assert.AreEqual(N4N0Installer.ModelShader, model.sharedMaterials.Single().shader.name, "N4N0's model in VRChat's mobile Standard Lite, one material");
        CollectionAssert.AreEqual(N4N0Installer.ModelShapes, Enumerable.Range(0, model.sharedMesh.blendShapeCount).Select(model.sharedMesh.GetBlendShapeName).ToArray());
        Assert.AreEqual(folder + "/N4N0 model.asset", AssetDatabase.GetAssetPath(model.sharedMesh), "In N4N0's folder, which a package carries");
        var upright = root.Find(N4N0Installer.Hop).GetComponent<VRCRotationConstraint>();
        Assert.AreEqual(root.Find(N4N0Installer.StayFrame), upright.Sources.Single().SourceTransform, "Upright and turned like the avatar, however the tether swings");
        var folderIcon = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single().icon;
        Assert.AreEqual(folder + "/N4N0 icon.png", AssetDatabase.GetAssetPath(folderIcon), "N4N0's folder shows the N4N0 mark");
        var atlas = (Texture2D)screen.sharedMaterials[0].GetTexture("_MainTex");
        Assert.AreEqual(folder + "/N4N0 text.png", AssetDatabase.GetAssetPath(atlas));
        Assert.AreEqual(2048, Mathf.Max(atlas.width, atlas.height), "Imported at most 2048 on its longest side");
        Assert.AreEqual(2, atlas.height / atlas.width, "16 × 16 tiles twice as tall as wide");
        Assert.Greater(atlas.mipmapCount, 1);
    }

    [TestCase(4)]
    [TestCase(16)]
    public void TheNumberOfSyncValuesSetsTheBlocksAndTheBits(int sync)
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        N4N0Installer.CreatePrefab(folder, sync);
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * sync + 3 + 8 + 3 + 25, parameters.CalcTotalCost());
        Assert.AreEqual(sync, parameters.parameters.Count(p => p.name.StartsWith(N4N0Installer.TextSyncPrefix, StringComparison.Ordinal)));
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        var blocks = controller.layers.Single(l => l.name == "Text block").stateMachine.states.Count(s => s.state.name.StartsWith("Block "));
        Assert.AreEqual(N4N0Installer.Cells / sync, blocks);
        Assert.Throws<ArgumentOutOfRangeException>(() => N4N0Installer.CreatePrefab(folder + "-5", 5));
    }

    [Test]
    public void SyncValuesWriteTheirBlocksCellsAndTheCellsStay()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var root = Play(prefab);
        var screen = root.transform.Find(N4N0Installer.Panel).GetComponent<SkinnedMeshRenderer>();
        playable.SetBool(N4N0Installer.TextVisible, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Text/Visible shows the panel");

        int[] Page() { var block = new MaterialPropertyBlock(); screen.GetPropertyBlock(block); return Enumerable.Range(0, 128).Select(c => Glyph(block.GetFloat("_Char" + c))).ToArray(); }
        void Send(int pointer, params int[] glyphs)
        {
            playable.SetInteger(N4N0Installer.TextPointer, pointer);
            for (int i = 0; i < glyphs.Length; i++) playable.SetFloat(N4N0Installer.TextSync(i), SyncValue(glyphs[i]));
            Tick(N4N0Installer.ValueLag + 4);
        }

        // The contract's encoding, both ends of each half and the glyphs past 128.
        var first = new[] { 0, 95, 127, 129, 223, 255, 65, 1 };
        Send(1, first);
        CollectionAssert.AreEqual(first, Page().Take(8), "Pointer 1 writes cells 0 to 7");
        Assert.IsTrue(Page().Skip(8).All(g => g == 0), "and nothing else");
        Assert.AreEqual(SyncValue(129), playable.GetFloat(N4N0Installer.Cell(3)), 1e-6f, "A cell keeps the float it was sent");

        // A new block and its values in the same update: the previous block keeps its cells.
        var second = new[] { 40, 69, 76, 76, 79, 158, 224, 33 };
        Send(2, second);
        CollectionAssert.AreEqual(first, Page().Take(8), "Block 1 kept");
        CollectionAssert.AreEqual(second, Page().Skip(8).Take(8), "Pointer 2 writes cells 8 to 15");

        // Values VRChat applied before their pointer (up to ValueLag updates): the block pointed at still keeps its cells.
        var third = new[] { 50, 51, 52, 53, 54, 55, 56, 57 };
        for (int i = 0; i < 8; i++) playable.SetFloat(N4N0Installer.TextSync(i), SyncValue(third[i]));
        Tick(N4N0Installer.ValueLag);
        Send(3, third);
        CollectionAssert.AreEqual(second, Page().Skip(8).Take(8), "Block 2 kept");
        CollectionAssert.AreEqual(third, Page().Skip(16).Take(8), "Pointer 3 writes cells 16 to 23");

        // While the pointer stays, the values keep writing; pointer 0 writes nothing; any other pointer neither.
        Send(2, 70, 71, 72, 73, 74, 75, 76, 77);
        CollectionAssert.AreEqual(new[] { 70, 71, 72, 73, 74, 75, 76, 77 }, Page().Skip(8).Take(8));
        var before = Page();
        Send(0, 9, 9, 9, 9, 9, 9, 9, 9);
        CollectionAssert.AreEqual(before, Page(), "Pointer 0 writes nothing");
        Send(17, 9, 9, 9, 9, 9, 9, 9, 9);
        Send(200, 9, 9, 9, 9, 9, 9, 9, 9);
        CollectionAssert.AreEqual(before, Page(), "Pointers past the last block write nothing");
        Send(16, 1, 2, 3, 4, 5, 6, 7, 8);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Page().Skip(120), "The last block holds cells 120 to 127");

        // Held for a long while (a minute at 60 updates a second), no cell drifts.
        Send(0);
        var held = Page();
        var floats = Enumerable.Range(0, 128).Select(c => playable.GetFloat(N4N0Installer.Cell(c))).ToArray();
        Tick(3600);
        CollectionAssert.AreEqual(held, Page());
        for (int c = 0; c < 128; c++) Assert.AreEqual(floats[c], playable.GetFloat(N4N0Installer.Cell(c)), 1e-6f, "Cell " + c);

        // The panel turned off and on again shows what it held; pointer 255 clears every cell to a space.
        playable.SetBool(N4N0Installer.TextVisible, false); Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
        playable.SetBool(N4N0Installer.TextVisible, true); Tick(3);
        CollectionAssert.AreEqual(held, Page());
        Send(255);
        Assert.IsTrue(Page().All(g => g == 0), "Pointer 255 clears the panel");
        Send(0);
        Assert.IsTrue(Page().All(g => g == 0));
    }

    [Test]
    public void EachStateShowsItsLook()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var root = Play(prefab);
        var model = root.transform.Find(N4N0Installer.Body).GetComponent<SkinnedMeshRenderer>();
        var screen = root.transform.Find(N4N0Installer.Panel).GetComponent<SkinnedMeshRenderer>();
        var hop = root.transform.Find(N4N0Installer.Hop);
        float Shape(string name) => model.GetBlendShapeWeight(model.sharedMesh.GetBlendShapeIndex(name));
        float Beam() { var block = new MaterialPropertyBlock(); screen.GetPropertyBlock(block); return block.GetFloat("_Beam"); }
        float Anger() => playable.GetFloat(N4N0Installer.Anger);
        // Every blendshape the state holds, as its look says (the ends of the transition), its beam and anger.
        void Show(N4N0State state)
        {
            playable.SetInteger(N4N0Installer.State, (int)state); Tick(20);
            var look = N4N0Installer.Looks[Mathf.Clamp((int)state, 0, 7) == (int)state ? (int)state : 0];
            foreach (var shape in N4N0Installer.ModelShapes)
                if (!(shape == "Blink" && look.Blinks) && !(shape == "Talk" && look.Talks))
                    Assert.AreEqual(look.Weight(shape, playable.GetFloat("Voice")), Shape(shape), .01f, state + " " + shape);
            Assert.AreEqual((look.Beam, look.Anger), (Beam(), Anger()));
        }
        playable.SetBool(N4N0Installer.TextVisible, true);

        Tick(5);
        Assert.IsTrue(N4N0Installer.ModelShapes.All(s => Shape(s) == 0) && Anger() == 0 && Beam() == 0, "Idle: N4N0's Normal face");
        Show(N4N0State.Listening);
        Assert.AreEqual(20, Shape("Surprised"), .01f);
        playable.SetFloat("Voice", 1); Tick(3);
        Assert.AreEqual(45, Shape("Surprised"), .5f, "Its eyes widen with its owner's voice");
        playable.SetFloat("Voice", 0);
        Show(N4N0State.Thinking);
        Assert.AreEqual(100, Shape("Thinking"), .01f);
        Show(N4N0State.Talking);
        Assert.AreEqual(1, Beam(), 1e-4f, "The beam projects the panel");
        float least = 100, most = 0;
        for (int i = 0; i < 40; i++) { Tick(1); least = Mathf.Min(least, Shape("Talk")); most = Mathf.Max(most, Shape("Talk")); }
        Assert.Greater(most - least, 40, "Its mouth moves while it talks");
        Show(N4N0State.Sleeping);
        Assert.AreEqual(100, Shape("Sleeping"), .01f);
        Show(N4N0State.Happy);
        Assert.AreEqual(100, Shape("Happy"), .01f);
        Show(N4N0State.Annoyed);
        Assert.AreEqual((100f, 1f), (Mathf.Round(Shape("Annoyed")), Anger()), "Brows down, and red");
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Surprised);
        float highest = 0;
        for (int i = 0; i < 30; i++) { Tick(1); highest = Mathf.Max(highest, hop.localPosition.y); }
        Assert.Greater(highest, .02f, "A short jump");
        Assert.Less(Mathf.Abs(hop.localPosition.y), .011f, "and back down to its bob");
        Assert.AreEqual(100, Shape("Surprised"), .01f);
        Show((N4N0State)42);
        Assert.IsTrue(N4N0Installer.ModelShapes.All(s => s == "Blink" || Shape(s) == 0) && Anger() == 0 && Beam() == 0, "Any other value shows as idle");

        // Idle blinks: 3.1 s in, its eyes shut for a moment.
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Happy); Tick(20);
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Idle);
        Tick(1); Tick(167);
        Assert.AreEqual(100, Shape("Blink"), .01f, "Blinking");
        Tick(10);
        Assert.AreEqual(0, Shape("Blink"), .01f, "Open again");

        // Its owner sees it look at them, everyone else sees it look ahead.
        var body = root.transform.Find(N4N0Installer.Body);
        Assert.Less(Quaternion.Angle(Quaternion.identity, body.localRotation), .01f, "Ahead for everyone else");
        playable.SetBool("IsLocal", true); Tick(2);
        Assert.Less(Quaternion.Angle(N4N0Installer.OwnerFacing, body.localRotation), .01f, "At its owner on its owner's client");

        playable.SetBool(N4N0Installer.Visible, false); Tick(3);
        Assert.IsFalse(root.transform.Find(N4N0Installer.Shown).gameObject.activeSelf, "N4N0/Visible off hides N4N0 and its panel");
        playable.SetBool(N4N0Installer.Visible, true); Tick(3);
        Assert.IsTrue(root.transform.Find(N4N0Installer.Shown).gameObject.activeSelf);
    }

    // A recording or a 3D picture (N4N0/Capture/Record, local) hides N4N0's screen, handle, link card, text panel and desktop
    // dot on its owner's client, and shows them again after, the menu left open.
    [Test]
    public void RecordingHidesTheScreenAndTheCardOutOfThePicture()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var root = Play(AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder)));
        var renderers = N4N0Installer.CaptureHidden.Select(h => root.transform.Find(h.path).GetComponent(h.type) as Renderer).ToList();
        Assert.IsTrue(renderers.All(r => r != null), "Every one is there");
        playable.SetBool("IsLocal", true);
        playable.SetBool(N4N0Installer.Menu, true);
        Tick(5);
        Assert.IsTrue(renderers.All(r => r.enabled), "Shown");
        playable.SetBool(N4N0Installer.CaptureRecord, true);
        Tick(3);
        Assert.IsTrue(renderers.All(r => !r.enabled), "Hidden while recording");
        Assert.IsTrue(playable.GetBool(N4N0Installer.Menu), "The menu stays open");
        playable.SetBool(N4N0Installer.CaptureRecord, false);
        Tick(3);
        Assert.IsTrue(renderers.All(r => r.enabled), "Shown again after");
    }

    // The model (N4N0Installer.Model): a sphere with the face's strokes and the back's mark floating above it, every
    // expression's triangles facing out, the back still while the face changes, light enough for phones.
    [Test]
    public void TheModelsFaceBendsIntoEveryExpression()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/N4N0 model.asset");
        if (mesh == null)
        {
            N4N0Installer.CreatePrefab(folder);
            mesh = AssetDatabase.LoadAssetAtPath<Mesh>(folder + "/N4N0 model.asset");
        }
        Assert.Less(mesh.triangles.Length / 3, 2500, "Light enough for phones");
        Assert.AreEqual(1, mesh.subMeshCount, "One material slot");
        var vertices = mesh.vertices;
        var uvs = mesh.uv;
        var triangles = mesh.triangles;
        // The shell's points take their colour on the pictures' left half, the patterns' on their right.
        var shell = triangles.Where(i => uvs[i].x == N4N0Installer.ShellU).ToArray();
        var patterns = triangles.Where(i => uvs[i].x == N4N0Installer.PatternU).ToArray();
        Assert.AreEqual(triangles.Length, shell.Length + patterns.Length);
        Assert.IsTrue(shell.All(i => Mathf.Abs(vertices[i].magnitude - 1) < 1e-4f), "The shell is the unit sphere");
        var moved = new HashSet<int>();
        foreach (var shape in N4N0Installer.ModelShapes)
        {
            int index = mesh.GetBlendShapeIndex(shape), frames = mesh.GetBlendShapeFrameCount(index);
            Assert.AreEqual(4, frames, shape + ": its in-betweens at 25, 50 and 75 %, then the expression");
            var deltas = new Vector3[vertices.Length];
            // The in-betweens follow the sphere, a little above it: the face never cuts through the ball on its way.
            for (int f = 0; f + 1 < frames; f++)
            {
                mesh.GetBlendShapeFrameVertices(index, f, deltas, null, null);
                Assert.IsTrue(patterns.All(i => (vertices[i] + deltas[i]).magnitude > 1.01f && (vertices[i] + deltas[i]).magnitude < 1.03f), shape + " frame " + f + ": above the shell");
            }
            mesh.GetBlendShapeFrameVertices(index, frames - 1, deltas, null, null);
            for (int i = 0; i < vertices.Length; i++) if (deltas[i].sqrMagnitude > 1e-10f) moved.Add(i);
            var bent = vertices.Select((v, i) => v + deltas[i]).ToArray();
            Assert.IsTrue(patterns.All(i => bent[i].magnitude > 1.01f && bent[i].magnitude < 1.02f), shape + ": the patterns float above the shell");
            int inward = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                if (uvs[triangles[t]].x != N4N0Installer.PatternU) continue;
                Vector3 a = bent[triangles[t]], b = bent[triangles[t + 1]], c = bent[triangles[t + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.sqrMagnitude > 1e-12f && Vector3.Dot(cross, a + b + c) < 0) inward++;
            }
            Assert.AreEqual(0, inward, shape + ": every triangle faces out");
        }
        Assert.IsTrue(moved.All(i => vertices[i].z > -.2f), "Only the face moves: the back's mark stays");
        Assert.IsTrue(patterns.Any(i => vertices[i].z < -.8f), "The back carries the mark");
    }

    [Test]
    public void ItFitsTheAvatarsHeadAndEyes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.2f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);

        var frame = n4n0.Find(N4N0Installer.Head);
        Assert.AreEqual(head, frame.GetComponent<VRCPositionConstraint>().Sources[0].SourceTransform, "On the head bone");
        Assert.AreEqual(avatar, frame.GetComponent<VRCRotationConstraint>().Sources[0].SourceTransform, "Turned like the avatar");
        Assert.Less(Vector3.Distance(head.position, frame.position), 1e-4f);
        Assert.AreEqual(1.2f / N4N0Installer.ReferenceEyes, frame.lossyScale.x, 1e-4f, "As large as the avatar's eyes are high");
        // N4N0 at the side of the eyes and in front of them, the panel lower and further, out of the face's way.
        var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
        var ball = n4n0.Find(N4N0Installer.Tether + "/1/2");
        var corner = n4n0.Find(N4N0Installer.Panel);
        Vector3 From(Transform t) => avatar.InverseTransformDirection(t.position - eyes);
        Assert.Less(Vector3.Distance(From(ball), N4N0Installer.N4N0FromEyes * frame.lossyScale.x), 1e-3f);
        Assert.Less(Vector3.Distance(From(corner), N4N0Installer.PanelFromEyes * frame.lossyScale.x), 1e-3f);
        Assert.Less(Quaternion.Angle(avatar.rotation, corner.rotation), .01f, "The panel faces ahead, upright");
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(n4n0.gameObject, avatar).Kind, "Attached by its own constraints");

        // Bound again (a moved armature), the fit follows.
        head.position += Vector3.up * .1f;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        Assert.Less(Vector3.Distance(head.position, n4n0.Find(N4N0Installer.Head).position), 1e-4f);
    }

    [Test]
    public void ItFollowsTheHeadTurnedLikeTheAvatarNotLikeTheHead()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.6f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var frame = n4n0.Find(N4N0Installer.Head);
        var panel = n4n0.Find(N4N0Installer.Panel);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(n4n0.gameObject);
            solver.Solve();
            var rest = (offset: panel.position - head.position, turn: panel.rotation);
            // Looking down and aside, leaning: the panel stays where its owner can look down at it.
            head.position += new Vector3(.05f, -.08f, .1f);
            head.rotation = Quaternion.Euler(35, -50, 10);
            solver.Solve();
            Assert.Less(Vector3.Distance(head.position, frame.position), 1e-4f, "On the head bone");
            Assert.Less(Quaternion.Angle(avatar.rotation, frame.rotation), .01f, "Turned like the avatar, not like the head");
            Assert.Less(Vector3.Distance(rest.offset, panel.position - head.position), 1e-4f);
            // The avatar turns (in VRChat, with its player): everything turns with it.
            avatar.rotation = Quaternion.Euler(0, 90, 0);
            solver.Solve();
            Assert.Less(Quaternion.Angle(avatar.rotation, frame.rotation), .01f);
            Assert.Less(Quaternion.Angle(avatar.rotation * rest.turn, panel.rotation), .01f);
        }
        finally { solver.Release(); }
    }

    // ---- The projected screen (Docs/n4n0.md, Screen) ----

    // N4N0 (every N4N0 has its screen), playing.
    private (GameObject root, SkinnedMeshRenderer screen) PlayWithScreen(int textSync = 8, int thumbnailBytes = 72)
    {
        var root = Play(AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder, textSync, thumbnailBytes)));
        return (root, root.transform.Find(N4N0Installer.ScreenObject).GetComponent<SkinnedMeshRenderer>());
    }

    // The screen's memory as its shader reads it (word k: b0 + 256 b1).
    private static byte[] Memory(Renderer screen, int thumbnailBytes = 72)
    {
        var block = new MaterialPropertyBlock();
        screen.GetPropertyBlock(block);
        var bytes = new byte[N4N0Installer.ScreenBytes(thumbnailBytes)];
        for (int k = 0; k < N4N0Installer.ScreenWords(thumbnailBytes); k++)
        {
            float w = block.GetFloat("_W" + k);
            float high = Mathf.Floor((w + .5f) / 256);
            bytes[2 * k] = (byte)Mathf.RoundToInt(w - high * 256);
            bytes[2 * k + 1] = (byte)high;
        }
        return bytes;
    }

    // Bytes for every place of the memory, each value but 128 (it has no float).
    private static byte[] Pattern(int seed, int thumbnailBytes = 72)
    {
        var bytes = new byte[N4N0Installer.ScreenBytes(thumbnailBytes)];
        for (int i = 0; i < bytes.Length; i++) { int b = (i * 37 + seed * 11 + 5) % 256; bytes[i] = (byte)(b == 128 ? 129 : b); }
        return bytes;
    }

    [Test]
    public void TheScreensReceiversAndParametersAreLocal()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, screen) = PlayWithScreen();
        Assert.IsTrue(N4N0Installer.HasScreen(root.transform));
        Assert.AreEqual(folder + "/N4N0 screen.mat", AssetDatabase.GetAssetPath(screen.sharedMaterial), "Its files in N4N0's folder, which a package carries");
        Assert.IsFalse(screen.gameObject.activeSelf, "Hidden until it opens");
        // The owner's own receivers are in the contact room, everyone else's fingertips' at the real screen.
        var receivers = screen.GetComponentsInChildren<VRCContactReceiver>(true).Concat(root.transform.Find(N4N0Installer.RoomScreen).GetComponentsInChildren<VRCContactReceiver>(true)).ToArray();
        foreach (var r in receivers) Assert.IsTrue(r.localOnly, r.parameter + ": local, nothing in the performance rank");
        var proximity = receivers.Where(r => r.receiverType == VRC.Dynamics.ContactReceiver.ReceiverType.Proximity).ToList();
        Assert.AreEqual(8 + 8 + 4 + 4 + 4, proximity.Count, "Four per hand for the fingertip and for the aim, four for everyone else's fingertips, four each for the eyes and the gaze point");
        foreach (var r in proximity)
        {
            var (tag, reach, self) =
                r.parameter.StartsWith(N4N0Installer.ScreenTouchPrefix, StringComparison.Ordinal) ? (N4N0Installer.TipTag(r.parameter.Contains("/R")), N4N0Installer.PointingReach, true) :
                r.parameter.StartsWith(N4N0Installer.ScreenAimPrefix, StringComparison.Ordinal) ? (N4N0Installer.AimTag(r.parameter.Contains("/R")), N4N0Installer.PointingReach, true) :
                r.parameter.StartsWith(N4N0Installer.ScreenOthersPrefix, StringComparison.Ordinal) ? ("FingerIndex", N4N0Installer.ScreenTouchRadius, false) :
                r.parameter.StartsWith(N4N0Installer.ScreenGazePrefix, StringComparison.Ordinal) ? (N4N0Installer.GazeTag, N4N0Installer.ScreenTouchRadius, true) :
                (N4N0Installer.EyeTag, N4N0Installer.ScreenTouchRadius, true);
            Assert.AreEqual((tag, reach, self, !self), (r.collisionTags.Single(), r.radius, r.allowSelf, r.allowOthers), r.parameter);
            int point = r.parameter[r.parameter.Length - 1] - '0';
            Assert.AreEqual(N4N0Installer.ScreenTouchPoints[point], r.transform.localPosition, r.parameter + " at its touch point");
        }
        var view = receivers.Single(r => r.parameter == N4N0Installer.ScreenInView);
        Assert.AreEqual((N4N0Installer.ViewTag, true, false), (view.collisionTags.Single(), view.allowSelf, view.allowOthers), "The middle: in its owner's sight or not");
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var screenParameters = parameters.parameters.Where(p => p.name.StartsWith("N4N0/Screen/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1 + 1 + 1 + 1 + 1 + N4N0Installer.ScreenLocalValues + 4 + 4 + 4 + 8 + 8 + 4 + 2 + 4, screenParameters.Count,
            "Open, Replace, ThumbnailBytes, Version, the pointer, 16 values, 4 each for the eyes and the gaze, Placed, HeldLeft, HeldRight, Size, the fingertips' and aims' 8 each, everyone else's 4");
        CollectionAssert.AreEquivalent(new[] { N4N0Installer.ScreenPlaced, N4N0Installer.ScreenHeldLeft, N4N0Installer.ScreenHeldRight, N4N0Installer.ScreenSize, N4N0Installer.ScreenPlaceX, N4N0Installer.ScreenPlaceY, N4N0Installer.ScreenPlaceZ, N4N0Installer.ScreenSettle },
            screenParameters.Where(p => p.networkSynced).Select(p => p.name), "Only where it is and its size are synced");
        var pinned = screenParameters.Single(p => p.name == N4N0Installer.ScreenPinned);
        Assert.AreEqual((false, true, VRCExpressionParameters.ValueType.Bool), (pinned.networkSynced, pinned.saved, pinned.valueType), "Stay here: its owner's choice, kept");
        var size = screenParameters.Single(p => p.name == N4N0Installer.ScreenSize);
        Assert.AreEqual((VRCExpressionParameters.ValueType.Float, N4N0Installer.DefaultScreenSize), (size.valueType, size.defaultValue), "Its size 1 by default");
        var replace = screenParameters.Single(p => p.name == N4N0Installer.ScreenReplace);
        Assert.AreEqual((0f, false), (replace.defaultValue, replace.saved), "Off unless the card's option is on, and never saved: each upload's option holds");
        var thumbnails = screenParameters.Single(p => p.name == N4N0Installer.ScreenThumbnailBytes);
        Assert.AreEqual((72f, false, VRCExpressionParameters.ValueType.Int), (thumbnails.defaultValue, thumbnails.saved, thumbnails.valueType), "72 bytes unless the card says 128, never saved");
        var version = screenParameters.Single(p => p.name == N4N0Installer.ScreenVersionParameter);
        Assert.AreEqual(((float)N4N0Installer.ScreenVersion, false, VRCExpressionParameters.ValueType.Int), (version.defaultValue, version.saved, version.valueType),
            "The screen says its version: Orbiters - N4N0 knows its pages and how it points");
        Assert.AreEqual(N4N0Installer.ScreenShaderName(72), screen.sharedMaterial.shader.name);
        Assert.AreEqual(folder + "/N4N0 screen beam.mat", AssetDatabase.GetAssetPath(screen.sharedMaterials[1]), "N4N0's beam, in N4N0's folder too");
        Assert.AreEqual(folder + "/N4N0 ray.mat", AssetDatabase.GetAssetPath(screen.sharedMaterials[2]), "and the fingertips' rays");
        var hold = screen.GetComponent<VRCParentConstraint>();
        Assert.AreEqual(root.transform.Find(N4N0Installer.ScreenGlide), hold.Sources[0].SourceTransform, "Held to its glide");
        Assert.IsFalse(hold.FreezeToWorld, "Its place stays, not the screen itself");
    }

    // Where the screen is (screen version 7): before the eyes until it is placed, then where it is in the world however its
    // owner looks, gliding back before the eyes once it is no longer placed; a hand carries it from where it took it.
    [Test]
    public void TheScreenStaysWhereItIsUntilItGlidesBackOrAHandCarriesIt()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.4f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var screen = n4n0.Find(N4N0Installer.ScreenObject);
        var renderer = screen.GetComponent<SkinnedMeshRenderer>();
        CollectionAssert.AreEqual(new[] { screen, n4n0.Find(N4N0Installer.Emitter), n4n0.Find(N4N0Installer.Pointer(false)), n4n0.Find(N4N0Installer.Pointer(true)) }, renderer.bones,
            "The beam's apex rides N4N0, the rays the fingertips");
        Assert.AreEqual(("Orbiters/N4N0 Beam", "Orbiters/N4N0 Ray"), (renderer.sharedMaterials[1].shader.name, renderer.sharedMaterials[2].shader.name));
        Assert.AreEqual(3, renderer.sharedMesh.subMeshCount, "The page, the beam, the rays");
        var grab = n4n0.Find(N4N0Installer.ScreenHandle + "/Grab").GetComponent<VRCPhysBone>();
        Assert.AreEqual((VRCPhysBoneBase.AdvancedBool.Other, true, false, N4N0Installer.ScreenGrab), (grab.allowGrabbing, grab.grabFilter.allowSelf, grab.grabFilter.allowOthers, grab.parameter),
            "Only its owner grabs the handle");
        Assert.AreEqual("Orbiters/N4N0 Dot", n4n0.Find(N4N0Installer.ScreenHandle + "/Bar").GetComponent<MeshRenderer>().sharedMaterial.shader.name);
        var view = n4n0.Find(N4N0Installer.RoomGaze).GetComponentsInChildren<VRCContactSender>(true).Where(c => c.collisionTags.Contains(N4N0Installer.ViewTag)).ToList();
        Assert.AreEqual(N4N0Installer.ViewDistances.Length, view.Count, "The view cone's spheres");
        Assert.IsTrue(view.All(c => c.localOnly), "Local: nothing in the performance rank");

        graph = PlayableGraph.Create("N4N0 placement test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", n4n0.gameObject.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        playable.SetBool("IsLocal", true);
        playable.SetInteger(N4N0Installer.VRMode, 0);
        playable.SetBool(N4N0Installer.Menu, true);
        var solver = new ConstraintSolver();
        void Step(int updates) { for (int i = 0; i < updates; i++) { graph.Evaluate(.02f); solver.Solve(); } }
        float size = 1.4f / N4N0Installer.ReferenceEyes;
        var eyesFromHead = Quaternion.Inverse(head.rotation) * (avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition) - head.position);
        Vector3 Eyes() => head.position + head.rotation * eyesFromHead;
        Vector3 Home() => Eyes() + head.rotation * Quaternion.Inverse(avatar.rotation) * (avatar.rotation * N4N0Installer.ScreenHomeFromEyes * size);
        try
        {
            solver.Track(n4n0.gameObject);
            Step(3);
            Assert.IsTrue(screen.gameObject.activeSelf);
            Assert.Less(Vector3.Distance(Home(), screen.position), 1e-3f, "Not placed: before the eyes");
            Assert.Less(Vector3.Angle(Eyes() - screen.position, -screen.forward), .5f, "Facing them");
            Assert.Less(Vector3.Angle(Vector3.ProjectOnPlane(Vector3.up, screen.forward), screen.up), .5f, "Upright: no roll, tipped back towards the eyes only");

            // Placed: it stays where it is in the world while its owner looks around.
            playable.SetBool(N4N0Installer.ScreenPlaced, true);
            Step(3);
            var there = screen.position;
            head.rotation = avatar.rotation * Quaternion.Euler(10, 40, 0);
            Step(3);
            Assert.Less(Vector3.Distance(there, screen.position), 1e-3f, "Placed: where it was, the head turned");

            // No longer placed: it glides back to where the eyes looked as it set off (between the two halfway through).
            playable.SetBool(N4N0Installer.ScreenPlaced, false);
            Step(1);
            Step(Mathf.RoundToInt(N4N0Installer.GlideTime / .02f / 2) - 1);
            float fromThere = Vector3.Distance(there, screen.position), toHome = Vector3.Distance(Home(), screen.position);
            Assert.Greater(fromThere, .02f, "Gliding");
            Assert.Greater(toHome, .02f, "not there yet");
            Step(Mathf.RoundToInt(N4N0Installer.GlideTime / .02f) + 5);
            Assert.Less(Vector3.Distance(Home(), screen.position), 1e-3f, "Back before the eyes");
            // There it stays, never riding the head; out of sight a moment more, it glides again to where they look.
            var arrived = screen.position;
            head.rotation = avatar.rotation * Quaternion.Euler(-5, -20, 0);
            Step(3);
            Assert.Less(Vector3.Distance(arrived, screen.position), 1e-3f, "Staying where it arrived, the head turned");
            Step(Mathf.RoundToInt(N4N0Installer.LookAwayTime / .02f) + Mathf.RoundToInt(N4N0Installer.GlideTime / .02f) + 10);
            Assert.Less(Vector3.Distance(Home(), screen.position), 1e-3f, "Glided again before the eyes");
            playable.SetBool(N4N0Installer.ScreenPlaced, true);
            Step(3);

            // Held in the left hand: it rides the palm from where it was taken; let go, it stays.
            var hand = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftHand);
            var before = screen.position;
            playable.SetBool(N4N0Installer.ScreenHeldLeft, true);
            Step(3);
            Assert.Less(Vector3.Distance(before, screen.position), 1e-3f, "Taken where it was");
            var carry = n4n0.Find(N4N0Installer.ScreenGrip(false) + "/" + N4N0Installer.Carry);
            var offset = carry.InverseTransformPoint(screen.position);
            hand.position += new Vector3(.1f, .05f, -.05f);
            Step(3);
            Assert.Greater(Vector3.Distance(before, screen.position), .05f, "Carried");
            Assert.Less(Vector3.Distance(offset, carry.InverseTransformPoint(screen.position)), 1e-3f, "From where it was taken, in the palm's carry frame");
            var dropped = screen.position;
            playable.SetBool(N4N0Installer.ScreenHeldLeft, false);
            Step(3);
            hand.position -= new Vector3(.1f, .05f, -.05f);
            Step(3);
            Assert.Less(Vector3.Distance(dropped, screen.position), 1e-3f, "Let go: it stays");

            // Its size: 1 by default, the largest at 1.
            Assert.AreEqual(size, screen.lossyScale.x, 1e-3f);
            playable.SetFloat(N4N0Installer.ScreenSize, 1);
            Step(2);
            Assert.AreEqual(size * N4N0Installer.LargestScreen, screen.lossyScale.x, 1e-3f);
        }
        finally { solver.Release(); }

        // Its owner's animator decides whether it stays (the drivers run in VRChat only): placed once in sight, no longer
        // placed after a while out of sight, placed while held.
        var stays = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller").layers.Single(l => l.name == "Screen stays").stateMachine;
        bool Drives(string state, bool value) => stays.states.Single(s => s.state.name == state).state.behaviours.OfType<VRCAvatarParameterDriver>()
            .Any(d => d.localOnly && d.parameters.Any(x => x.name == N4N0Installer.ScreenPlaced && x.value == (value ? 1 : 0)));
        Assert.IsTrue(Drives("Following", false) && Drives("Staying", true) && Drives("Holding", true));
        var away = stays.states.Single(s => s.state.name == "Looking away").state;
        Assert.AreEqual(N4N0Installer.LookAwayTime, away.motion.averageDuration, 1e-3f, "Out of sight that long first");
    }

    [Test]
    public void GrabbingThenRecenteringLeavesTheScreenStillInFrontOfTheEyes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.4f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        graph = PlayableGraph.Create("N4N0 grab and recenter");
        playable = AnimatorControllerPlayable.Create(graph, controller);
        AnimationPlayableOutput.Create(graph, "N4N0", n4n0.gameObject.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        playable.SetBool("IsLocal", true);
        playable.SetBool(N4N0Installer.Menu, true);
        var screen = n4n0.Find(N4N0Installer.ScreenObject);
        var home = n4n0.Find(N4N0Installer.ScreenHome);
        var view = n4n0.Find(N4N0Installer.RoomGaze).GetComponentsInChildren<VRCContactSender>(true)
            .Where(c => c.collisionTags.Contains(N4N0Installer.ViewTag)).ToArray();
        var states = new int[controller.layers.Length];
        var solver = new ConstraintSolver();
        bool intermittentView = false;
        int frame = 0, recenters = 0;
        bool wasPlaced = false;
        var transitions = new List<string>();
        void Step(int count)
        {
            for (int n = 0; n < count; n++)
            {
                frame++;
                playable.SetBool(N4N0Installer.ScreenInView, intermittentView ? frame % 12 == 0 :
                    view.Any(c => Vector3.Distance(c.transform.position, screen.position) < c.radius * c.transform.lossyScale.x));
                graph.Evaluate(.05f);
                // The real SDK constraint solver below; parameter-driver Set behaviours emulated on entry (VRChat runs them).
                for (int l = 0; l < states.Length; l++)
                {
                    var state = playable.GetCurrentAnimatorStateInfo(l);
                    if (states[l] == state.fullPathHash) continue;
                    states[l] = state.fullPathHash;
                    var definition = controller.layers[l].stateMachine.states.Select(s => s.state).FirstOrDefault(s => Animator.StringToHash(s.name) == state.shortNameHash);
                    if (definition == null) continue;
                    if (controller.layers[l].name == "Screen stays" || controller.layers[l].name == "Screen grip")
                        transitions.Add(frame + " " + controller.layers[l].name + "=" + definition.name + " view=" + playable.GetBool(N4N0Installer.ScreenInView) + " held=" + playable.GetBool(N4N0Installer.ScreenHeldLeft));
                    foreach (var driver in definition.behaviours.OfType<VRCAvatarParameterDriver>())
                    foreach (var p in driver.parameters.Where(p => p.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set))
                        if (controller.parameters.Any(x => x.name == p.name && x.type == AnimatorControllerParameterType.Bool)) playable.SetBool(p.name, p.value != 0);
                }
                solver.Solve();
                bool placed = playable.GetBool(N4N0Installer.ScreenPlaced);
                if (wasPlaced && !placed) recenters++;
                wasPlaced = placed;
            }
        }
        try
        {
            solver.Track(n4n0.gameObject);
            Step(40);
            Assert.Less(Vector3.Distance(home.position, screen.position), .001f, "opened before the eyes");
            playable.SetBool(N4N0Installer.ScreenGrab + "_IsGrabbed", true);
            playable.SetBool(N4N0Installer.ScreenHandL, true);
            playable.SetInteger("GestureLeft", 1);
            Step(5);
            avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftHand).position += new Vector3(.15f, -.08f, -.1f);
            Step(5);
            playable.SetBool(N4N0Installer.ScreenGrab + "_IsGrabbed", false);
            playable.SetInteger("GestureLeft", 0);
            Step(5);
            head.rotation = avatar.rotation * Quaternion.Euler(0, 90, 0);
            Step(48);
            Assert.Less(Vector3.Distance(home.position, screen.position), .001f, "recentered after a grab\n" + string.Join("\n", transitions));
            var arrived = screen.position;
            Step(80);
            Assert.Less(Vector3.Distance(arrived, screen.position), .001f, "stays centered without another glide");
            Assert.IsTrue(playable.GetBool(N4N0Installer.ScreenPlaced));
            Assert.IsFalse(playable.GetBool(N4N0Installer.ScreenHeldLeft));
            // A contact that briefly sees the centered screen must not re-arm an endless glide loop.
            // One recenter is allowed when sight first goes; subsequent one-frame pulses cannot keep restarting it.
            intermittentView = true;
            recenters = 0;
            Step(120);
            Assert.LessOrEqual(recenters, 1, "intermittent view contact re-armed repeated recentering after a grab");
            // Closing during a grip must drop it, even when the gesture has not relaxed yet.
            intermittentView = false;
            playable.SetBool(N4N0Installer.ScreenGrab + "_IsGrabbed", true);
            playable.SetInteger("GestureLeft", 1);
            Step(5);
            Assert.IsTrue(playable.GetBool(N4N0Installer.ScreenHeldLeft));
            playable.SetBool(N4N0Installer.Menu, false);
            playable.SetBool(N4N0Installer.ScreenOpen, false);
            Step(5);
            Assert.IsFalse(playable.GetBool(N4N0Installer.ScreenHeldLeft), "a hidden handle cannot keep its grip latched");
        }
        finally { solver.Release(); }
    }

    // The link card (Docs/n4n0.md, Link card; screen version 7): an object of its own, out while N4N0/Card/Out is on (for
    // everyone: it is synced), frozen where it appeared, N4N0's beam projecting it a moment; anyone's pickup grab moves it;
    // its memory comes from the text channel's pointers past the screen's on every client; every N4N0's hands feel it.
    [Test]
    public void TheLinkCardIsOutWhereItAppearsAndEveryClientWritesItsMemory()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, screen) = PlayWithScreen();
        var n4n0 = root.transform;
        Assert.IsTrue(N4N0Installer.HasCard(n4n0));
        var model = n4n0.Find(N4N0Installer.CardModel);
        var card = model.GetComponent<SkinnedMeshRenderer>();
        Assert.AreEqual(screen.sharedMesh, card.sharedMesh, "Drawn by the screen's mesh");
        CollectionAssert.AreEqual(screen.sharedMaterials, card.sharedMaterials, "and its materials");
        Assert.AreEqual(n4n0.Find(N4N0Installer.Emitter), card.bones[1], "N4N0's beam reaches it");
        var sender = n4n0.Find(N4N0Installer.CardSender).GetComponent<VRCContactSender>();
        Assert.AreEqual((N4N0Installer.CardTag, false), (sender.collisionTags.Single(), sender.localOnly), "Everyone's hands feel the card");
        var grab = n4n0.Find(N4N0Installer.CardGrabBase + "/Bone").GetComponent<VRCPhysBone>();
        Assert.AreEqual((VRCPhysBoneBase.AdvancedBool.True, N4N0Installer.CardGrab), (grab.allowGrabbing, grab.parameter), "Anyone grabs it to move it");
        foreach (bool right in new[] { false, true })
        {
            var receiver = n4n0.Find(N4N0Installer.CardHand(right)).GetComponent<VRCContactReceiver>();
            Assert.AreEqual((N4N0Installer.CardTag, N4N0Installer.CardTouch(right)), (receiver.collisionTags.Single(), receiver.parameter));
            Assert.IsTrue(receiver.allowSelf && receiver.allowOthers && receiver.localOnly, "Its own card and others', on its owner's client only");
        }
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset").parameters;
        foreach (var name in new[] { N4N0Installer.CardOut, N4N0Installer.CardHeldLeft, N4N0Installer.CardHeldRight })
            Assert.IsTrue(parameters.Single(p => p.name == name).networkSynced, name + " is synced");
        foreach (bool right in new[] { false, true }) Assert.IsFalse(parameters.Single(p => p.name == N4N0Installer.CardTouch(right)).networkSynced);
        Assert.IsFalse(parameters.Any(p => p.name == "N4N0/Screen/Drop"), "the screen is never the card any more");

        // Out, for anyone's client: shown, frozen where it appeared, N4N0's beam fading out.
        var place = n4n0.Find(N4N0Installer.CardPlace).GetComponent<VRCParentConstraint>();
        float Beam() { var block = new MaterialPropertyBlock(); card.GetPropertyBlock(block); return block.GetFloat("_Beam"); }
        playable.SetBool("IsLocal", false);
        Tick(2);
        Assert.IsFalse(model.gameObject.activeSelf || grab.enabled, "Not out");
        playable.SetBool(N4N0Installer.CardOut, true);
        Tick(3);
        Assert.IsTrue(model.gameObject.activeSelf && grab.enabled && place.FreezeToWorld, "Out where it appeared, and grabbable");
        Assert.Greater(Beam(), .2f, "N4N0's beam projects it");
        Tick(Mathf.CeilToInt(N4N0Installer.CardBeamTime / .02f) + 4);
        Assert.AreEqual(0, Beam(), 1e-3f, "then it stands alone");
        Assert.IsFalse(screen.gameObject.activeSelf, "The screen stays closed beside it");
        // Someone's pickup: the place follows the grip (frozen a moment first), the grab base frozen; let go, it stays.
        playable.SetBool(N4N0Installer.CardGrab + "_IsGrabbed", true);
        Tick(3);
        Assert.AreEqual(1, place.Sources[N4N0Installer.CardOnGrip].Weight);
        Assert.IsFalse(place.FreezeToWorld);
        Assert.IsTrue(n4n0.Find(N4N0Installer.CardGrabBase).GetComponent<VRCParentConstraint>().FreezeToWorld);
        playable.SetBool(N4N0Installer.CardGrab + "_IsGrabbed", false);
        Tick(3);
        Assert.IsTrue(place.FreezeToWorld, "Let go: it stays");

        // Its memory, on every client (its owner's too): 252 clears it, a block's pointer writes its words.
        int textSync = 8, thumbnailBytes = 72;
        bool picture = N4N0Installer.CardPictureFits(textSync, thumbnailBytes);
        Assert.IsTrue(picture, "8 values keep the card's picture");
        var words = N4N0Installer.CardWords(thumbnailBytes, picture);
        var (first, blocks) = N4N0Installer.CardPointers(textSync, thumbnailBytes, picture);
        Assert.AreEqual(N4N0Installer.ScreenPointers(textSync, thumbnailBytes).first + N4N0Installer.ScreenPointers(textSync, thumbnailBytes).blocks, first, "Right past the screen's");
        Assert.LessOrEqual(first + blocks, N4N0Installer.PointerCardClear);
        var bytes = new byte[2 * words.Count];
        for (int i = 0; i < bytes.Length; i++) { int v = (i * 29 + 7) % 256; bytes[i] = (byte)(v == 128 ? 129 : v); }
        void Synced(int pointer, int from)
        {
            playable.SetInteger(N4N0Installer.TextPointer, pointer);
            for (int i = 0; i < textSync; i++) playable.SetFloat(N4N0Installer.TextSync(i), SyncValue(from + i < bytes.Length ? bytes[from + i] : 0));
            Tick(N4N0Installer.ValueLag + 4);
        }
        float Word(int k) { var block = new MaterialPropertyBlock(); card.GetPropertyBlock(block); return block.GetFloat("_W" + k); }
        foreach (bool local in new[] { false, true })
        {
            playable.SetBool("IsLocal", local);
            Synced(N4N0Installer.PointerCardClear, 0);
            Assert.IsTrue(words.All(k => Word(k) == 0), "252 clears the card");
            for (int b = 0; b < blocks; b++) Synced(first + b, b * textSync);
            Synced(0, 0);
            for (int j = 0; j < words.Count; j++)
                Assert.AreEqual(bytes[2 * j] + 256 * bytes[2 * j + 1], Word(words[j]), .01f, (local ? "Its owner's" : "Everyone else's") + " card word " + j + " (_W" + words[j] + ")");
        }
        // The screen's own blocks never write the card.
        Synced(N4N0Installer.ScreenPointers(textSync, thumbnailBytes).first, 0);
        Assert.AreEqual(bytes[0] + 256 * bytes[1], Word(words[0]), .01f);
        playable.SetBool(N4N0Installer.CardOut, false);
        Tick(3);
        Assert.IsFalse(model.gameObject.activeSelf, "Taken back");
    }

    [Test]
    public void TheOwnersScreenKeepsItsLocalBlocksAndIgnoresTheSyncedOnes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (_, screen) = PlayWithScreen();
        playable.SetBool("IsLocal", true);
        playable.SetInteger(N4N0Installer.VRMode, 0);
        playable.SetBool(N4N0Installer.ScreenOpen, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Screen/Open shows it in desktop mode");
        Assert.IsFalse(screen.GetComponent<VRCParentConstraint>().FreezeToWorld, "It follows its owner");

        void Local(int pointer, byte[] bytes, int from)
        {
            playable.SetInteger(N4N0Installer.ScreenPointer, pointer);
            for (int i = 0; i < N4N0Installer.ScreenLocalValues; i++)
                playable.SetFloat(N4N0Installer.ScreenValue(i), SyncValue(from + i < bytes.Length ? bytes[from + i] : 0));
            // The desktop holds each block for two frames or more.
            Tick(2);
        }
        var data = Pattern(1);
        int blocks = (N4N0Installer.ScreenBytes(72) + 15) / 16;
        for (int b = 0; b < blocks; b++) Local(b + 1, data, b * 16);
        Local(0, data, 0);
        CollectionAssert.AreEqual(data, Memory(screen), "Every block written");

        // A minute later nothing drifted.
        Tick(3000);
        CollectionAssert.AreEqual(data, Memory(screen));

        // The text channel's screen blocks are for everyone else.
        playable.SetInteger(N4N0Installer.TextPointer, N4N0Installer.ScreenPointers(8, 72).first);
        for (int i = 0; i < 8; i++) playable.SetFloat(N4N0Installer.TextSync(i), SyncValue(7));
        Tick(4);
        CollectionAssert.AreEqual(data, Memory(screen), "The owner ignores the synced screen blocks");

        // Rewritten block by block, then cleared.
        var again = Pattern(2);
        for (int b = 0; b < blocks; b++) Local(b + 1, again, b * 16);
        Local(0, again, 0);
        CollectionAssert.AreEqual(again, Memory(screen));
        Local(N4N0Installer.ScreenLocalClear, again, 0);
        Local(0, again, 0);
        Assert.IsTrue(Memory(screen).All(v => v == 0), "Pointer 255 clears the owner's screen");

        playable.SetBool(N4N0Installer.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);

        // The menu's Menu toggle opens it at once, whatever the desktop does, and turning it off closes it, even while
        // the desktop still has it open (the app quit or froze; in VRChat a driver then clears N4N0/Screen/Open).
        playable.SetBool(N4N0Installer.Menu, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Menu shows the owner's screen");
        playable.SetBool(N4N0Installer.ScreenOpen, true);
        playable.SetBool(N4N0Installer.Menu, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Menu off closes it, though the desktop left it open");
        playable.SetBool(N4N0Installer.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Both off: closed");

        // One the desktop opened with Menu off stays until the desktop closes it, or Menu goes on and off.
        playable.SetBool(N4N0Installer.ScreenOpen, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "The desktop opens it");
        playable.SetBool(N4N0Installer.Menu, true);
        Tick(3);
        playable.SetBool(N4N0Installer.Menu, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Menu on, then off, closes it");
        playable.SetBool(N4N0Installer.ScreenOpen, false);
        Tick(3);
        var closing = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller").layers.Single(l => l.name == "Screen")
            .stateMachine.states.Single(s => s.state.name == "Closed by menu").state;
        var driver = closing.behaviours.OfType<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>().Single();
        Assert.IsTrue(driver.localOnly);
        Assert.AreEqual((N4N0Installer.ScreenOpen, 0f), (driver.parameters.Single().name, driver.parameters.Single().value), "Menu off clears what the desktop set");

        // In VR the overlay shows the menu, unless the screen replaces it.
        playable.SetInteger(N4N0Installer.VRMode, 1);
        playable.SetBool(N4N0Installer.Menu, true);
        playable.SetBool(N4N0Installer.ScreenOpen, true);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "In VR without Replace the screen stays closed");
        playable.SetBool(N4N0Installer.ScreenReplace, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Replace: the screen in VR too");
        playable.SetBool(N4N0Installer.Menu, false);
        playable.SetBool(N4N0Installer.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
    }

    // Desktop mode: the owner points by looking. The eyes and the gaze point stay on the line of sight however the head
    // turns, and Orbiters - N4N0's reading (Docs/n4n0.md, Screen: the line through the two located points, on the
    // screen's plane) lands where the owner looks.
    /// <summary>
    /// The contact room (N4N0Installer.Room): what its copies measure from each other is what the real things measure, turned
    /// and walked anywhere, at any size of the screen; the room is far enough down to have a cell of VRChat's grid to itself.
    /// </summary>
    [Test]
    public void TheContactRoomMeasuresWhatTheRealThingsMeasureFromTheirCellOfTheirOwn()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.4f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        n4n0.Find(N4N0Installer.ContactRoom).gameObject.SetActive(true);
        n4n0.Find(N4N0Installer.RoomScreen).gameObject.SetActive(true);
        foreach (bool right in new[] { false, true }) n4n0.Find(N4N0Installer.RoomPointer(right)).gameObject.SetActive(true);
        var screen = n4n0.Find(N4N0Installer.ScreenObject);
        var copy = n4n0.Find(N4N0Installer.RoomScreen);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(n4n0.gameObject);
            foreach (var (place, yaw, scale) in new[] { (Vector3.zero, 0f, 1f), (new Vector3(30, 9, -12), 70f, 1f), (new Vector3(-300, -40, 800), 215f, 1.8f), (new Vector3(4, 0, 4), 320f, .6f) })
            {
                avatar.SetPositionAndRotation(place, Quaternion.Euler(0, yaw, 0));
                head.rotation = avatar.rotation * Quaternion.Euler(12, -30, 5);
                screen.localScale = copy.localScale = Vector3.one * scale;
                solver.Solve();
                solver.Solve();
                foreach (bool right in new[] { false, true })
                {
                    var pointer = n4n0.Find(N4N0Installer.Pointer(right));
                    var probe = n4n0.Find(N4N0Installer.RoomPointer(right));
                    Assert.Less((screen.InverseTransformPoint(pointer.position) - copy.InverseTransformPoint(probe.position)).magnitude, 2e-3f, "The fingertip from the screen, at " + place);
                    var aim = pointer.TransformPoint(Vector3.forward * N4N0Installer.AimDistance);
                    Assert.Less((screen.InverseTransformPoint(aim) - copy.InverseTransformPoint(probe.Find("Aim").position)).magnitude, 2e-3f, "The aim from the screen");
                }
                var eyes = n4n0.Find(N4N0Installer.Gaze);
                Assert.Less((screen.InverseTransformPoint(eyes.position) - copy.InverseTransformPoint(n4n0.Find(N4N0Installer.RoomGaze).position)).magnitude, 2e-3f, "The eyes from the screen");
                Assert.Less(Quaternion.Angle(screen.rotation, copy.rotation), .1f, "The same way up");
                Assert.Greater(Vector3.Distance(screen.position, copy.position), 60f, "Far from everyone's contacts: VRChat's grid has cells of 5 m");
            }
        }
        finally { solver.Release(); }
    }

    /// <summary>The Action Menu has Stay here on its first page with every option, within its eight controls.</summary>
    [Test]
    public void StayHereIsOnTheActionMenuWithinItsEightControls()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        foreach (bool options in new[] { false, true })
        {
            string at = folder + (options ? "/With" : "/Without");
            AssetDatabase.CreateFolder(folder, options ? "With" : "Without");
            N4N0Installer.CreatePrefab(at);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(at + "/N4N0.prefab");
            var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (options) N4N0Installer.SetOptions(placed.transform, 8, true, false, 72, light: true);
            var top = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(at + "/Menu.asset").controls.Single().subMenu;
            if (!options)
            {
                Assert.LessOrEqual(top.controls.Count, VRCExpressionsMenu.MAX_CONTROLS);
                Assert.AreEqual(N4N0Installer.ScreenPinned, top.controls.Single(c => c.name == "Stay here").parameter.name);
            }
            UnityEngine.Object.DestroyImmediate(placed);
        }
    }

    [Test]
    public void TheGazePointFollowsTheLineOfSightOntoTheScreen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.4f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var eye = n4n0.Find(N4N0Installer.Gaze);
        var gaze = n4n0.Find(N4N0Installer.GazePoint);
        foreach (var (at, tag) in new[] { (n4n0.Find(N4N0Installer.RoomGaze), N4N0Installer.EyeTag), (n4n0.Find(N4N0Installer.RoomPoint), N4N0Installer.GazeTag) })
        {
            var sender = at.GetComponent<VRCContactSender>();
            Assert.IsTrue(sender.localOnly, "Local: nothing in the performance rank");
            Assert.AreEqual(tag, sender.collisionTags.Single());
        }
        var screen = n4n0.Find(N4N0Installer.ScreenObject);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(n4n0.gameObject);
            solver.Solve();
            var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
            float size = 1.4f / 1.6f;
            Assert.Less(Vector3.Distance(eyes + avatar.forward * N4N0Installer.GazeDistance * size, gaze.position), 1e-4f, "Straight ahead of the eyes at rest");
            var eyesFromHead = Quaternion.Inverse(head.rotation) * (eyes - head.position);
            foreach (var look in new[] { Quaternion.Euler(25, 0, 0), Quaternion.Euler(38, -18, 0), Quaternion.Euler(14, 22, 0) })
            {
                head.rotation = avatar.rotation * look;
                solver.Solve();
                var eyesNow = head.position + head.rotation * eyesFromHead;
                var sight = head.rotation * Quaternion.Inverse(avatar.rotation) * avatar.forward;
                Assert.Less(Vector3.Distance(eyesNow, eye.position), 1e-4f, "At the eyes");
                Assert.Less(Vector3.Angle(sight, gaze.position - eyesNow), .01f, "On the line of sight");
                // Where the line of sight meets the screen's plane, and what the desktop finds, in the screen's own space.
                var from = screen.InverseTransformPoint(eyesNow);
                var dir = screen.InverseTransformDirection(sight);
                var truth = from + dir * (-from.z / dir.z);
                var g = screen.InverseTransformPoint(gaze.position);
                var e = screen.InverseTransformPoint(eye.position);
                var read = e + (g - e) * (-e.z / (g.z - e.z));
                Assert.Less(Vector2.Distance(truth, read), .001f, look.eulerAngles + ": where the owner looks");
                Assert.Less(g.z, -.01f, "In front of the screen, so its dot shows");
            }
        }
        finally { solver.Release(); }
    }

    [Test]
    public void TheGazeDotShowsForItsOwnerInDesktopModeWhileTheMenuIsOpen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, _) = PlayWithScreen();
        var dot = root.transform.Find(N4N0Installer.GazeDot).gameObject;
        playable.SetBool("IsLocal", true);
        playable.SetInteger(N4N0Installer.VRMode, 0);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "Menu closed");
        playable.SetBool(N4N0Installer.Menu, true);
        Tick(3);
        Assert.IsTrue(dot.activeSelf, "Desktop mode, menu open");
        playable.SetInteger(N4N0Installer.VRMode, 1);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "In VR fingers point");
        playable.SetInteger(N4N0Installer.VRMode, 0);
        playable.SetBool("IsLocal", false);
        playable.SetBool(N4N0Installer.Menu, false);
        Tick(3);
        playable.SetBool(N4N0Installer.Menu, true);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "Nobody else sees it");
    }

    // Pointing (Docs/n4n0.md, Pointing, screen version 7): each index finger's pointer at its tip along the finger, as large as
    // N4N0, with a sender at the fingertip and one further along the finger.
    [Test]
    public void ItPointsAlongTheIndexFingersWithTwoSendersEach()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.2f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var animator = avatar.GetComponent<Animator>();
        float size = 1.2f / N4N0Installer.ReferenceEyes;
        Assert.IsTrue(N4N0Installer.HasPointing(n4n0));
        foreach (bool right in new[] { false, true })
        {
            var pointer = n4n0.Find(N4N0Installer.Pointer(right));
            var distal = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
            var middle = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
            var hand = animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            var source = pointer.GetComponent<VRCParentConstraint>().Sources[0];
            Assert.AreEqual(hand, source.SourceTransform, "Held to the hand: curling the index to pull a trigger moves nothing");
            var tip = distal.position + (distal.position - middle.position) * .8f;
            Assert.Less(Vector3.Distance(tip, pointer.position), 1e-4f, "At the fingertip");
            Assert.Less(Vector3.Distance(hand.position + hand.rotation * source.ParentPositionOffset, pointer.position), 1e-4f, "where the straight finger's tip is, from the hand");
            Assert.Less(Vector3.Angle((tip - middle.position).normalized, pointer.forward), .1f, "Along the finger");
            Assert.AreEqual(size, pointer.lossyScale.x, 1e-4f, "As large as N4N0");
            var copy = n4n0.Find(N4N0Installer.RoomPointer(right));
            var sender = copy.GetComponent<VRCContactSender>();
            Assert.AreEqual((true, N4N0Installer.TipTag(right)), (sender.localOnly, sender.collisionTags.Single()), "The fingertip's");
            Assert.AreEqual(N4N0Installer.PointRadius * size, sender.radius * sender.transform.lossyScale.x, 1e-6f, "A known, tiny contact");
            var aim = copy.Find("Aim").GetComponent<VRCContactSender>();
            Assert.AreEqual((true, N4N0Installer.AimTag(right)), (aim.localOnly, aim.collisionTags.Single()), "The aim's");
            Assert.AreEqual(N4N0Installer.AimDistance * size, aim.transform.localPosition.magnitude * copy.lossyScale.x, 1e-4f, "Further along the finger");
        }
        Assert.IsFalse(n4n0.Find(N4N0Installer.Pointers).gameObject.activeSelf, "On in VR only");
    }

    [Test]
    public void ThePointersAndTheirRaysAreOnInVr()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, screen) = PlayWithScreen(textSync: 8);
        var pointers = root.transform.Find(N4N0Installer.Pointers).gameObject;
        float Rays() { var block = new MaterialPropertyBlock(); screen.GetPropertyBlock(block); return block.GetFloat("_Rays"); }
        playable.SetBool("IsLocal", true);
        playable.SetInteger(N4N0Installer.VRMode, 0);
        Tick(3);
        Assert.IsFalse(pointers.activeSelf, "Desktop mode points by looking");
        Assert.AreEqual(0, Rays());
        playable.SetInteger(N4N0Installer.VRMode, 1);
        Tick(3);
        Assert.IsTrue(pointers.activeSelf, "In VR the pointers follow the fingertips");
        Assert.AreEqual(1, Rays(), "and the screen draws their rays");
        playable.SetBool("IsLocal", false);
        Tick(3);
        Assert.IsTrue(pointers.activeSelf && Rays() > .5f, "Everyone sees the owner's rays on the screen (VRMode is synced)");
        playable.SetInteger(N4N0Installer.VRMode, 0);
        Tick(3);
        Assert.IsFalse(pointers.activeSelf);
    }

    // VRCFury reverts every override of its components in a prefab instance when an upload starts (VRCFPrefabFixer): the
    // options of N4N0 placed from a prefab survive it.
    [Test]
    public void OptionsOfAPlacedN4N0SurviveVrcFurysRevertBeforeAnUpload()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Assert.AreEqual((8, false, false, 72, false), N4N0Installer.OptionsOf(placed.transform));
        N4N0Installer.SetOptions(placed.transform, 4, true, true);
        Assert.AreEqual((4, true, true, 72, false), N4N0Installer.OptionsOf(placed.transform));
        Assert.Throws<ArgumentException>(() => N4N0Installer.SetOptions(placed.transform, 4, true, true, 128), "128-byte thumbnails need 8 text slots");

        // What VRCFury does to the avatar before the SDK copies it.
        foreach (var component in placed.GetComponents(VrcFury.Component))
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            var mods = PrefabUtility.GetPropertyModifications(component)?.Where(m => m.target == source).ToList();
            if (mods != null && mods.Count > 0) PrefabUtility.RevertObjectOverride(component, InteractionMode.AutomatedAction);
        }
        Assert.AreEqual((4, true, true, 72, false), N4N0Installer.OptionsOf(placed.transform), "Kept on N4N0's own Full Controller");
        Assert.AreEqual(1, placed.GetComponents(VrcFury.Component).Length, "The prefab's Full Controller replaced, not doubled");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, N4N0Installer.ScreenReplace));

        // 128-byte thumbnails: the parameter's default, the screen's material and memory follow.
        N4N0Installer.SetOptions(placed.transform, 8, false, true, 128);
        Assert.AreEqual((8, false, true, 128, false), N4N0Installer.OptionsOf(placed.transform));
        var renderer = placed.transform.Find(N4N0Installer.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.AreEqual(N4N0Installer.ScreenShaderName(128), renderer.sharedMaterial.shader.name);
        Assert.IsTrue(renderer.sharedMaterial.HasProperty("_W511") && !renderer.sharedMaterial.HasProperty("_W512"), "1024 bytes: 512 words");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, N4N0Installer.ScreenThumbnailBytes));

        // And back: the screen stays, Replace off, 72-byte thumbnails.
        N4N0Installer.SetOptions(placed.transform, 8, false, false);
        Assert.AreEqual((8, false, false, 72, false), N4N0Installer.OptionsOf(placed.transform));
        Assert.IsTrue(N4N0Installer.HasScreen(placed.transform));
        Assert.AreEqual(N4N0Installer.ScreenShaderName(72), renderer.sharedMaterial.shader.name);
    }

    // The Light option (Docs/n4n0.md): a point light under N4N0 that everyone sees, in one of eight colours at a
    // brightness; N4N0 takes its colour. Switched off, it goes.
    [Test]
    public void TheLightOptionMakesTheN4N0ALightInItsColour()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Assert.IsFalse(N4N0Installer.HasLight(placed.transform));
        N4N0Installer.SetOptions(placed.transform, 8, false, false, 72, light: true);
        Assert.AreEqual((8, false, false, 72, true), N4N0Installer.OptionsOf(placed.transform));
        var light = placed.transform.Find(N4N0Installer.LightObject).GetComponent<Light>();
        Assert.AreEqual((LightType.Point, LightShadows.None), (light.type, light.shadows));
        Assert.IsFalse(light.gameObject.activeSelf, "Off until asked");

        string shared = N4N0Installer.OptionsFolder + "Text slots 8 + light";
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(shared + "/Parameters.asset");
        var own = parameters.parameters.Where(p => p.name.StartsWith("N4N0/Light/", StringComparison.Ordinal))
            .Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToList();
        CollectionAssert.AreEqual(new[]
        {
            (N4N0Installer.Light, VRCExpressionParameters.ValueType.Bool, true, false, 0f),
            (N4N0Installer.LightColor, VRCExpressionParameters.ValueType.Int, true, true, 0f),
            (N4N0Installer.LightLevel, VRCExpressionParameters.ValueType.Float, true, true, N4N0Installer.LightLevelDefault),
            (N4N0Installer.LightStay, VRCExpressionParameters.ValueType.Bool, true, false, 0f),
        }, own);
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8 + 3 + 8 + 3 + 25 + 1 + 8 + 8 + 1, parameters.CalcTotalCost(), "18 synced bits more");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, N4N0Installer.LightColor));
        var n4n0Menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(shared + "/Menu.asset").controls.Single().subMenu;
        var lightMenu = n4n0Menu.controls.Single(c => c.name == "More").subMenu.controls.Single(c => c.name == "Light").subMenu;
        CollectionAssert.AreEqual(new[] { "Light", "Brightness", "Colour", "Stay there" }, lightMenu.controls.Select(c => c.name).ToArray());
        Assert.AreEqual(N4N0Installer.LightLevel, lightMenu.controls[1].subParameters.Single().name);
        var colours = lightMenu.controls[2].subMenu.controls;
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => (float)i).ToArray(), colours.Select(c => c.value).ToArray());
        Assert.IsTrue(colours.All(c => c.parameter.name == N4N0Installer.LightColor));

        // Played: pink at full brightness, then off; N4N0's patterns glow pink while lit, white again after.
        var root = Object.Instantiate(placed);
        SceneManager.MoveGameObjectToScene(root, scene);
        graph = PlayableGraph.Create("N4N0 light test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(shared + "/N4N0.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        var played = root.transform.Find(N4N0Installer.LightObject).GetComponent<Light>();
        var body = root.transform.Find(N4N0Installer.Body).GetComponent<SkinnedMeshRenderer>();
        Color N4N0Colour() { var block = new MaterialPropertyBlock(); body.GetPropertyBlock(block); return block.GetColor("_EmissionColor"); }
        Tick(5);
        Assert.IsFalse(played.gameObject.activeSelf);
        playable.SetInteger(N4N0Installer.LightColor, 3);
        playable.SetFloat(N4N0Installer.LightLevel, 1);
        playable.SetBool(N4N0Installer.Light, true);
        Tick(20);
        Assert.IsTrue(played.gameObject.activeSelf);
        Assert.AreEqual(N4N0Installer.LightColors[3].color.r, played.color.r, .01f);
        Assert.AreEqual(N4N0Installer.LightColors[3].color.g, played.color.g, .01f);
        Assert.AreEqual(N4N0Installer.LightBright, played.intensity, .01f);
        Assert.IsTrue(N4N0Colour().r > .9f && N4N0Colour().r > N4N0Colour().g + .15f, "N4N0 glows pink: " + N4N0Colour());
        // Stay there: N4N0 stays where it is in the world, then follows again.
        var anchor = root.transform.Find(N4N0Installer.StayFrame).GetComponent<VRCParentConstraint>();
        Assert.IsFalse(anchor.FreezeToWorld);
        playable.SetBool(N4N0Installer.LightStay, true);
        Tick(3);
        Assert.IsTrue(anchor.FreezeToWorld, "Staying there");
        playable.SetBool(N4N0Installer.LightStay, false);
        Tick(3);
        Assert.IsFalse(anchor.FreezeToWorld, "Following again");
        playable.SetBool(N4N0Installer.Light, false);
        Tick(20);
        Assert.IsFalse(played.gameObject.activeSelf);
        Assert.IsTrue(Mathf.Abs(N4N0Colour().r - 1) < .01f && Mathf.Abs(N4N0Colour().g - 1) < .01f && Mathf.Abs(N4N0Colour().b - 1) < .01f, "White again: " + N4N0Colour());

        N4N0Installer.SetOptions(placed.transform, 8, false, false, 72, light: false);
        Assert.IsFalse(N4N0Installer.HasLight(placed.transform));
        Assert.AreEqual((8, false, false, 72, false), N4N0Installer.OptionsOf(placed.transform));
    }

    // A screen from before version 3 (fewer page slices) takes the current files when N4N0's options change, and the
    // controller then says its version: Orbiters - N4N0 shows it the six-tile Home.
    [Test]
    public void AnOlderScreenTakesTheCurrentPagesWithItsVersion()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var renderer = placed.transform.Find(N4N0Installer.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.IsTrue(N4N0Installer.ScreenCurrent(renderer.sharedMaterial, 72), "A new N4N0's screen is current");
        Assert.AreEqual(N4N0Installer.ScreenRevision, renderer.sharedMaterial.GetTag(N4N0Installer.ScreenRevisionTag, false), "its shader says its files' revision");
        var older = new Material(renderer.sharedMaterial);
        var pages = new Texture2DArray(4, 4, 10, TextureFormat.RGBA32, false);
        owned.Add(older); owned.Add(pages);
        older.SetTexture("_Pages", pages);
        renderer.sharedMaterial = older;
        Assert.IsFalse(N4N0Installer.ScreenCurrent(older, 72));
        N4N0Installer.SetOptions(placed.transform, 8, false, true);
        Assert.IsTrue(N4N0Installer.ScreenCurrent(renderer.sharedMaterial, 72));
        StringAssert.StartsWith(N4N0Installer.ScreenFolder + "/", AssetDatabase.GetAssetPath(renderer.sharedMaterial));
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(N4N0Installer.OptionsFolder + "Text slots 8 + screen in VR/Parameters.asset");
        var version = parameters.parameters.Single(p => p.name == N4N0Installer.ScreenVersionParameter);
        Assert.AreEqual((VRCExpressionParameters.ValueType.Int, false, false, (float)N4N0Installer.ScreenVersion), (version.valueType, version.networkSynced, version.saved, version.defaultValue));
        Assert.AreEqual(16, N4N0Installer.ScreenVersion);
    }

    // The Action Menu has room for eight: the two 3D pictures cost one more place, so with both options Walk boost and Light
    // share More. With one of them, or none, everything stays where it was. Both pictures are local: no synced bit.
    [Test]
    public void TheActionMenuHasTwoPicturesAndNeverMoreThanEightControls()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        string[] Names(string options, out VRCExpressionsMenu menu)
        {
            menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(N4N0Installer.OptionsFolder + options + "/Menu.asset").controls.Single().subMenu;
            return menu.controls.Select(c => c.name).ToArray();
        }

        N4N0Installer.SetOptions(placed.transform, 8, true, false, 72, light: true);
        CollectionAssert.AreEqual(new[] { "Show N4N0", "Menu", "Stay here", "Talk", "3D picture of people", "3D picture of the world", "Call", "More" },
            Names("Text slots 8 + walk boost + light", out var both), "All on: 8 controls, the options and Record in More");
        Assert.LessOrEqual(both.controls.Count, VRCExpressionsMenu.MAX_CONTROLS);
        var more = both.controls.Single(c => c.name == "More").subMenu;
        CollectionAssert.AreEqual(new[] { "Walk boost", "Light", "Record" }, more.controls.Select(c => c.name).ToArray());
        Assert.AreEqual(N4N0Installer.WalkBoost, more.controls[0].subParameters.Single().name, "The radial still sets its parameter");
        Assert.IsNotNull(more.controls[1].subMenu, "Light keeps its own submenu");
        StringAssert.EndsWith("N4N0 more menu.asset", AssetDatabase.GetAssetPath(more), "A saved asset, as a menu that VRCFury merges must be");

        N4N0Installer.SetOptions(placed.transform, 8, true, false);
        CollectionAssert.AreEqual(new[] { "Show N4N0", "Menu", "Stay here", "Talk", "3D picture of people", "3D picture of the world", "Call", "More" },
            Names("Text slots 8 + walk boost", out _), "One option: in More with Record too, Stay here keeps the first page full");

        N4N0Installer.SetOptions(placed.transform, 8, false, false, 72, light: true);
        CollectionAssert.AreEqual(new[] { "Show N4N0", "Menu", "Stay here", "Talk", "3D picture of people", "3D picture of the world", "Call", "More" },
            Names("Text slots 8 + light", out var one));
        var buttons = one.controls.Where(c => c.name.StartsWith("3D picture", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(buttons.All(c => c.type == VRCExpressionsMenu.Control.ControlType.Button));
        CollectionAssert.AreEqual(new[] { N4N0Installer.Photo3DPeople, N4N0Installer.Photo3DWorld }, buttons.Select(c => c.parameter.name).ToArray());
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(N4N0Installer.OptionsFolder + "Text slots 8 + light/Parameters.asset");
        Assert.IsTrue(buttons.All(b => parameters.parameters.Single(p => p.name == b.parameter.name).networkSynced == false), "Local: no synced bit");
        Assert.IsFalse(parameters.parameters.Any(p => p.name == "N4N0/Photo3D"), "The old button is not built any more");
        var owner = VrcFury.Features(placed).Single(f => f.kind == "FullController").feature;
        Assert.IsTrue(buttons.All(b => VrcFury.IsGlobalParameter(owner, b.parameter.name)), "OSC finds them under these names");
    }

    // The retro look is the screen's material alone: it stays through N4N0's other options, and goes back.
    [Test]
    public void TheRetroLookIsTheScreensMaterialAndStaysThroughOptions()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var renderer = placed.transform.Find(N4N0Installer.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.IsFalse(N4N0Installer.RetroOf(placed.transform), "Orbiters' own look by default");
        N4N0Installer.SetLook(placed.transform, true);
        Assert.IsTrue(N4N0Installer.RetroOf(placed.transform));
        Assert.IsTrue(N4N0Installer.ScreenCurrent(renderer.sharedMaterial, 72));
        Assert.AreEqual(new Color32(43, 227, 106, 255), (Color32)renderer.sharedMaterials[1].GetColor("_EdgeColor"), "N4N0's beam in the phosphor's green");
        StringAssert.EndsWith("N4N0 screen pages retro.png", AssetDatabase.GetAssetPath(renderer.sharedMaterial.GetTexture("_Pages")), "The retro look's own pages");
        Assert.AreEqual(new Color32(2, 10, 4, 255), (Color32)renderer.sharedMaterial.GetColor("_Card"), "Its phosphor palette");
        Assert.AreEqual(new Color32(43, 227, 106, 255), (Color32)renderer.sharedMaterial.GetColor("_Accent"));
        StringAssert.StartsWith(N4N0Installer.ScreenFolder + "/", AssetDatabase.GetAssetPath(renderer.sharedMaterial));
        N4N0Installer.SetOptions(placed.transform, 8, false, true, 128);
        Assert.IsTrue(N4N0Installer.RetroOf(placed.transform), "another thumbnail size keeps the look");
        Assert.IsTrue(N4N0Installer.ScreenCurrent(renderer.sharedMaterial, 128));
        N4N0Installer.SetLook(placed.transform, false);
        Assert.IsFalse(N4N0Installer.RetroOf(placed.transform));
        Assert.IsTrue(N4N0Installer.ScreenCurrent(renderer.sharedMaterial, 128));
        Assert.AreEqual("N4N0 screen beam", renderer.sharedMaterials[1].name, "and its beam goes back to Orbiters' green");
    }

    [TestCase(8, 72)]
    [TestCase(4, 72)]
    [TestCase(8, 128)]
    public void EveryoneElsesScreenOpensWithItsBlocksAndClosesAt254(int textSync, int thumbnailBytes)
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (_, screen) = PlayWithScreen(textSync, thumbnailBytes);
        playable.SetBool("IsLocal", false);
        Tick(3);
        var (first, blocks) = N4N0Installer.ScreenPointers(textSync, thumbnailBytes);
        Assert.AreEqual(N4N0Installer.Cells / textSync + 1, first);
        Assert.Less(first + blocks - 1, N4N0Installer.PointerScreenClose, "Every block has a pointer");

        void Synced(int pointer, byte[] bytes, int from)
        {
            playable.SetInteger(N4N0Installer.TextPointer, pointer);
            for (int i = 0; i < textSync; i++) playable.SetFloat(N4N0Installer.TextSync(i), SyncValue(from + i < bytes.Length ? bytes[from + i] : 0));
            Tick(N4N0Installer.ValueLag + 4);
        }
        // The panel's own blocks don't open it.
        Synced(1, Pattern(3), 0);
        Assert.IsFalse(screen.gameObject.activeSelf, "Panel pointers leave the screen closed");
        var data = Pattern(4, thumbnailBytes);
        for (int b = 0; b < blocks; b++) Synced(first + b, data, b * textSync);
        Assert.IsTrue(screen.gameObject.activeSelf, "Its first block opens it");
        Synced(0, data, 0);
        CollectionAssert.AreEqual(data, Memory(screen, thumbnailBytes), "Every block written");
        // Panel text meanwhile changes nothing on the screen.
        Synced(2, Pattern(5), 0);
        CollectionAssert.AreEqual(data, Memory(screen, thumbnailBytes));
        Synced(N4N0Installer.PointerScreenClose, data, 0);
        Assert.IsFalse(screen.gameObject.activeSelf, "Pointer 254 closes it");
    }

    // ---- N4N0 on Quest (N4N0Installer.Mobile) ----

    // A placed, fitted N4N0 on an avatar, with the Light option by default: the PC N4N0 then has all that phones remove.
    private (Transform avatar, Transform n4n0) Placed(bool light = true)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.4f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        if (light) N4N0Installer.SetOptions(n4n0, 8, false, false, 72, light: true);
        return (avatar, n4n0);
    }

    // What the SDK builds: a plain copy of the avatar, the platform's N4N0 kept.
    private GameObject Built(Transform avatar, bool mobile)
    {
        var holder = new GameObject("Build copies");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var copy = Object.Instantiate(avatar.gameObject, holder.transform);
        N4N0Installer.KeepPlatformPart(copy, mobile);
        return copy;
    }

    // The assets of a Full Controller's list entries (controllers or parameters), in their order.
    private static List<T> Entries<T>(Component fullController, string list, string field) where T : Object
    {
        var entries = new SerializedObject(fullController).FindProperty("content." + list);
        return Enumerable.Range(0, entries.arraySize).Select(i => entries.GetArrayElementAtIndex(i).FindPropertyRelative(field + ".objRef").objectReferenceValue as T).ToList();
    }

    // The expression parameters VRCFury merges from a built avatar's Full Controllers, in their order.
    private static List<VRCExpressionParameters.Parameter> Merged(GameObject built) =>
        VrcFury.Features(built).Where(f => f.kind == "FullController")
            .SelectMany(f => Entries<VRCExpressionParameters>(f.component, "prms", "parameters")).SelectMany(p => p.parameters).ToList();

    [Test]
    public void AMobileBuildKeepsOnlyTheMobileN4N0()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, n4n0) = Placed();
        Assert.IsTrue(N4N0Installer.HasMobile(n4n0) && N4N0Installer.HasLight(n4n0));
        Assert.IsFalse(n4n0.Find(N4N0Installer.Mobile).gameObject.activeSelf, "Off in the editor, where the PC N4N0 shows");
        var copy = Built(avatar, mobile: true);
        var built = copy.transform.Find(n4n0.name);
        Assert.IsNull(built.Find(N4N0Installer.Shown), "The PC N4N0 is gone");
        Assert.IsTrue(built.Find(N4N0Installer.Mobile).gameObject.activeSelf, "The mobile one is on");

        // What VRChat refuses or removes on phones: none of it.
        Assert.IsEmpty(VRC.SDK3.Validation.AvatarValidation.FindIllegalShaders(copy), "Only VRChat's mobile shaders");
        Assert.IsEmpty(copy.GetComponentsInChildren<Light>(true), "No light");
        Assert.IsEmpty(copy.GetComponentsInChildren<Collider>(true), "No collider");
        Assert.IsEmpty(copy.GetComponentsInChildren<IConstraint>(true), "No Unity constraint");
        Assert.IsEmpty(copy.GetComponentsInChildren<Camera>(true));
        Assert.IsEmpty(copy.GetComponentsInChildren<AudioSource>(true));
        // And what costs a mobile rank: one skinned mesh, one material slot, under 2,500 triangles, no bone, no PhysBone,
        // no contact that counts.
        var renderer = (SkinnedMeshRenderer)copy.GetComponentsInChildren<Renderer>(true).Single();
        Assert.AreEqual(1, renderer.sharedMaterials.Length, "One material slot");
        StringAssert.StartsWith("VRChat/Mobile/", renderer.sharedMaterial.shader.name);
        Assert.AreEqual(N4N0Installer.ModelShader, renderer.sharedMaterial.shader.name);
        Assert.Less(renderer.sharedMesh.triangles.Length / 3, 2500, "Under 2,500 triangles");
        Assert.AreEqual(0, renderer.bones.Length, "No bone: its blendshapes move it");
        Assert.IsEmpty(copy.GetComponentsInChildren<VRCPhysBone>(true), "Quest's Excellent rank allows no PhysBone");
        var receivers = copy.GetComponentsInChildren<VRCContactReceiver>(true);
        Assert.AreEqual(N4N0Installer.Touch, receivers.Single().parameter, "Its owner touches it");
        Assert.IsTrue(receivers.Single().localOnly && !receivers.Single().allowOthers, "Local: no contact counts in the rank");
        Assert.IsEmpty(copy.GetComponentsInChildren<VRCContactSender>(true));
        Assert.LessOrEqual(copy.GetComponentsInChildren<VRCConstraintBase>(true).Length, 2, "On the head, turned like the avatar");

        // Its Full Controller: the mobile controller alone; the PC N4N0's parameters, then the mobile version; the same menu.
        var full = built.GetComponents(VrcFury.Component).Single();
        var controller = Entries<AnimatorController>(full, "controllers", "controller").Single();
        Assert.AreEqual(folder + "/N4N0 mobile.controller", AssetDatabase.GetAssetPath(controller));
        var placedFull = n4n0.GetComponents(VrcFury.Component).Single();
        CollectionAssert.AreEqual(Entries<VRCExpressionParameters>(placedFull, "prms", "parameters"), Entries<VRCExpressionParameters>(full, "prms", "parameters"));
        CollectionAssert.AreEqual(Entries<VRCExpressionsMenu>(placedFull, "menus", "menu"), Entries<VRCExpressionsMenu>(full, "menus", "menu"));
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Content(full), N4N0Installer.MobileVersionParameter), "Orbiters - N4N0 finds it by its name");
        Assert.IsTrue(N4N0Installer.FindAll(copy).Count() == 0, "Nothing of the PC N4N0 left to find");
    }

    [Test]
    public void APcBuildKeepsOnlyThePcN4N0()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, n4n0) = Placed();
        var copy = Built(avatar, mobile: false);
        var built = copy.transform.Find(n4n0.name);
        Assert.IsNull(built.Find(N4N0Installer.Mobile), "The mobile N4N0 is gone");
        Assert.IsTrue(built.Find(N4N0Installer.Shown).gameObject.activeSelf);
        Assert.IsTrue(N4N0Installer.HasLight(built) && N4N0Installer.HasScreen(built) && N4N0Installer.HasPointing(built), "The PC N4N0 whole");
        Assert.AreEqual(1, copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(r => r.sharedMesh != null && r.sharedMesh.name == "N4N0 model"), "One N4N0 model: the PC one");
        // As before the mobile N4N0: the PC controller, parameters and menu only.
        var full = built.GetComponents(VrcFury.Component).Single();
        var placedFull = n4n0.GetComponents(VrcFury.Component).Single();
        var controllers = Entries<AnimatorController>(full, "controllers", "controller");
        Assert.AreEqual(Entries<AnimatorController>(placedFull, "controllers", "controller").First(), controllers.Single(), "The PC controller");
        Assert.IsTrue(controllers.Single().parameters.Any(p => p.name == N4N0Installer.TextPointer));
        Assert.AreEqual(Entries<VRCExpressionParameters>(placedFull, "prms", "parameters").First(), Entries<VRCExpressionParameters>(full, "prms", "parameters").Single());
        Assert.IsFalse(Merged(copy).Any(p => p.name.StartsWith("N4N0/Mobile/", StringComparison.Ordinal)), "No mobile parameter on PC");
    }

    // VRChat matches synced parameters between an avatar's platforms by their place: N4N0's are the same, in the same order.
    [Test]
    public void BothBuildsSyncTheSameParametersInTheSameOrder()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, _) = Placed();
        var pc = Merged(Built(avatar, mobile: false));
        var phone = Merged(Built(avatar, mobile: true));
        List<(string, VRCExpressionParameters.ValueType)> Synced(List<VRCExpressionParameters.Parameter> list) =>
            list.Where(p => p.networkSynced).Select(p => (p.name, p.valueType)).ToList();
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8 + 3 + 8 + 3 + 25 + 1 + 8 + 8 + 1, pc.Where(p => p.networkSynced).Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8), "N4N0 with 8 text slots and its light");
        CollectionAssert.AreEqual(Synced(pc), Synced(phone));
        // The PC N4N0's parameters come first and whole; the mobile build adds its version, local.
        CollectionAssert.AreEqual(pc.Select(p => p.name), phone.Take(pc.Count).Select(p => p.name));
        var version = phone.Skip(pc.Count).Single();
        Assert.AreEqual((N4N0Installer.MobileVersionParameter, VRCExpressionParameters.ValueType.Int, false, false, (float)N4N0Installer.MobileVersion),
            (version.name, version.valueType, version.networkSynced, version.saved, version.defaultValue));
    }

    // The mobile N4N0 played (its own controller): the PC N4N0's model and expressions, blinking, bobbing, jumping.
    [Test]
    public void TheMobileN4N0ShowsThePcOnesExpressions()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var root = PlayMobile(AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder)));
        var model = root.transform.Find(N4N0Installer.MobileBody).GetComponent<SkinnedMeshRenderer>();
        var pc = root.transform.Find(N4N0Installer.Body).GetComponent<SkinnedMeshRenderer>();
        Assert.AreEqual(pc.sharedMesh, model.sharedMesh, "The PC N4N0's model");
        CollectionAssert.AreEqual(pc.sharedMaterials, model.sharedMaterials, "in its material, which phones show");
        Assert.AreEqual(N4N0Installer.Radius, model.transform.localScale.x, 1e-6f);
        var floating = root.transform.Find(N4N0Installer.MobileFloat);
        float Shape(string name) => model.GetBlendShapeWeight(model.sharedMesh.GetBlendShapeIndex(name));

        Tick(3);
        Assert.IsTrue(N4N0Installer.ModelShapes.All(s => Shape(s) == 0), "Idle: the Normal face");
        foreach (var (state, shape) in new[] { (N4N0State.Thinking, "Thinking"), (N4N0State.Sleeping, "Sleeping"), (N4N0State.Happy, "Happy"), (N4N0State.Annoyed, "Annoyed") })
        {
            playable.SetInteger(N4N0Installer.State, (int)state);
            Tick(20);
            Assert.AreEqual(100, Shape(shape), .01f, state.ToString());
        }
        Assert.AreEqual(1, playable.GetFloat(N4N0Installer.MobileAnger), 1e-4f, "Annoyed, it turns red");
        playable.SetInteger(N4N0Installer.State, 42);
        Tick(20);
        Assert.IsTrue(N4N0Installer.ModelShapes.All(s => s == "Blink" || Shape(s) == 0), "Any other value shows as Idle");

        // Idle blinks: 3.1 s in, its eyes shut for a moment.
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Happy);
        Tick(20);
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Idle);
        Tick(1); Tick(167);
        Assert.AreEqual(100, Shape("Blink"), .01f, "Blinking");
        Tick(10);
        Assert.AreEqual(0, Shape("Blink"), .01f, "Open again");

        // Listening, its eyes widen with its owner's voice; surprised, it jumps.
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Listening);
        Tick(20);
        float quiet = Shape("Surprised");
        playable.SetFloat("Voice", 1);
        Tick(3);
        Assert.Greater(Shape("Surprised"), quiet + 20, "Wider with its owner's voice");
        playable.SetFloat("Voice", 0);
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Surprised);
        float highest = 0;
        for (int i = 0; i < 30; i++) { Tick(1); highest = Mathf.Max(highest, floating.localPosition.y); }
        Assert.Greater(highest, .03f, "A short jump");
        Tick(100);
        Assert.Less(Mathf.Abs(floating.localPosition.y), .015f, "and back to bobbing");

        // N4N0/Visible hides it; its owner sees it look at them, everyone else sees it look ahead.
        var face = root.transform.Find(N4N0Installer.MobileFace);
        Assert.Less(Quaternion.Angle(Quaternion.identity, face.localRotation), .01f, "Ahead for everyone else");
        playable.SetBool("IsLocal", true);
        Tick(2);
        Assert.Less(Quaternion.Angle(N4N0Installer.OwnerFacing, face.localRotation), .01f, "At its owner on its owner's client");
        playable.SetBool(N4N0Installer.Visible, false);
        Tick(2);
        Assert.IsFalse(root.transform.Find(N4N0Installer.MobileHead).gameObject.activeSelf);
        playable.SetBool(N4N0Installer.Visible, true);
        Tick(2);
        Assert.IsTrue(root.transform.Find(N4N0Installer.MobileHead).gameObject.activeSelf);
    }

    // The mobile N4N0's patterns take the Light option's colour, brighter with its brightness, red while annoyed, white
    // while off.
    [Test]
    public void TheMobileTintFollowsTheLightsColour()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var root = PlayMobile(AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder)));
        var renderer = root.transform.Find(N4N0Installer.MobileBody).GetComponent<SkinnedMeshRenderer>();
        Color Glow() { var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block); return block.GetColor("_EmissionColor"); }
        Tick(5);
        Assert.IsTrue(Glow().r > .99f && Glow().g > .99f && Glow().b > .99f, "White: " + Glow());

        playable.SetInteger(N4N0Installer.LightColor, 3);
        playable.SetFloat(N4N0Installer.LightLevel, 1);
        playable.SetBool(N4N0Installer.Light, true);
        Tick(20);
        var bright = Glow();
        Assert.IsTrue(bright.r > bright.g + .15f && bright.b > bright.g, "Pink: " + bright);
        playable.SetFloat(N4N0Installer.LightLevel, 0);
        Tick(3);
        Assert.Less(Glow().b, bright.b * .8f, "Dimmer at a low brightness");
        playable.SetInteger(N4N0Installer.LightColor, 5);
        Tick(20);
        Assert.IsTrue(Glow().b > Glow().r, "Blue: " + Glow());

        playable.SetFloat(N4N0Installer.LightLevel, 1);
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Annoyed);
        Tick(20);
        Assert.IsTrue(Glow().r > .5f && Glow().r > Glow().b * 1.5f, "Red while annoyed: " + Glow());
        playable.SetInteger(N4N0Installer.State, (int)N4N0State.Idle);
        playable.SetBool(N4N0Installer.Light, false);
        Tick(20);
        Assert.IsTrue(Glow().r > .99f && Glow().g > .99f && Glow().b > .99f, "White again: " + Glow());
    }

    [Test]
    public void TheMobileN4N0FitsTheAvatarBesideThePcOne()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.2f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        var frame = n4n0.Find(N4N0Installer.MobileHead);
        Assert.AreEqual(head, frame.GetComponent<VRCPositionConstraint>().Sources[0].SourceTransform, "On the head bone");
        Assert.AreEqual(avatar, frame.GetComponent<VRCRotationConstraint>().Sources[0].SourceTransform, "Turned like the avatar");
        Assert.Less(Vector3.Distance(head.position, frame.position), 1e-4f);
        Assert.AreEqual(1.2f / N4N0Installer.ReferenceEyes, frame.lossyScale.x, 1e-4f, "As large as the avatar's eyes are high");
        var mobile = n4n0.Find(N4N0Installer.MobileN4N0);
        Assert.Less(Vector3.Distance(n4n0.Find(N4N0Installer.Tether + "/1/2").position, mobile.position), 1e-4f, "Where the PC N4N0 rests");
        var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
        Assert.Less(Vector3.Angle(frame.rotation * N4N0Installer.OwnerFacing * Vector3.forward, eyes - mobile.position), .5f, "Turned to its owner, it looks at their eyes");
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(n4n0.gameObject, avatar).Kind, "Still attached by its own constraints");

        // The build step runs on every build before VRCFury and the Toolkit's attachments, hidden builds too, and never
        // changes a prefab instance (only build copies).
        var hook = new N4N0PlatformHook();
        Assert.Less(hook.callbackOrder, -10110);
        var field = typeof(VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.IsTrue(((IEnumerable<VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback>)field.GetValue(null)).Any(c => c is N4N0PlatformHook), "Registered with the SDK");
        Assert.IsFalse(HiddenAvatarBuild.LeftOut(typeof(N4N0PlatformHook)), "My Avatar's checks see the platform's N4N0");
        N4N0Installer.KeepPlatformPart(avatar.gameObject, mobile: true);
        Assert.IsNotNull(n4n0.Find(N4N0Installer.Shown));
        Assert.IsFalse(n4n0.Find(N4N0Installer.Mobile).gameObject.activeSelf);
    }

    // ---- Helpers ----

    // The contract's float for glyph g, and the glyph the panel shows for a cell's value (the text shader's reading).
    private static float SyncValue(int glyph) => glyph <= 127 ? glyph / 127f : (glyph - 256) / 127f;
    private static int Glyph(float cell) { int g = Mathf.RoundToInt(cell); return g < 0 ? g + 256 : g; }

    private void Tick(int updates) { for (int i = 0; i < updates; i++) graph.Evaluate(.02f); }

    private GameObject Play(GameObject prefab)
    {
        var root = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(root, scene);
        graph = PlayableGraph.Create("N4N0 test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        return root;
    }

    // The prefab's mobile N4N0 turned on (as a mobile build does), playing its own controller.
    private GameObject PlayMobile(GameObject prefab)
    {
        var root = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(root, scene);
        root.transform.Find(N4N0Installer.Mobile).gameObject.SetActive(true);
        graph = PlayableGraph.Create("N4N0 mobile test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0 mobile.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        return root;
    }

    // Toolkit releases ship without .meta files: a gallery copy refers to nothing of this project's Toolkit, and its package
    // declares the Toolkit that fits it.
    private static void Standalone(GameObject prefab)
    {
        Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Any(p => p.StartsWith("Packages/orbiters.", StringComparison.Ordinal)));
        Assert.IsTrue(prefab.GetComponentsInChildren<MonoBehaviour>(true).All(c => !c.GetType().Assembly.GetName().Name.StartsWith("Orbiters")));
        Assert.IsTrue(AttachmentHooks.Fits(prefab));
    }

    // A humanoid avatar in the preview scene whose eyes are this high: its root and head bone.
    private (Transform avatar, Transform head) Humanoid(float eyes)
    {
        var root = new GameObject("N4N0 avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        float k = eyes / 1.6f;
        var bones = new Dictionary<string, Transform>();
        void Add(string name, string parent, Vector3 position)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent == null ? root.transform : bones[parent], false);
            bone.localPosition = position * k;
            bones[name] = bone;
        }
        Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
        Add("Neck", "Chest", new Vector3(0, .2f, 0)); Add("Head", "Neck", new Vector3(0, .07f, 0));
        foreach (var side in new[] { "L", "R" })
        {
            float x = side == "L" ? -1 : 1;
            Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
            Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
            Add("Index1" + side, "Hand" + side, new Vector3(.08f * x, 0, .02f)); Add("Index2" + side, "Index1" + side, new Vector3(.03f * x, 0, 0));
            Add("Index3" + side, "Index2" + side, new Vector3(.02f * x, 0, 0));
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
        }
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
            ["Left Index Proximal"] = "Index1L", ["Left Index Intermediate"] = "Index2L", ["Left Index Distal"] = "Index3L",
            ["Right Index Proximal"] = "Index1R", ["Right Index Intermediate"] = "Index2R", ["Right Index Distal"] = "Index3R",
            ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
            ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
        };
        var description = new HumanDescription
        {
            human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        owned.Add(avatar);
        root.AddComponent<Animator>().avatar = avatar;
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.ViewPosition = new Vector3(0, eyes, .06f * k);
        return (root.transform, bones["Head"]);
    }
}
