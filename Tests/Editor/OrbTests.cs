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
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

// Orb, the Orbiters companion (contracts Docs/orb.md), built whole into a temporary folder: its parameters as the desktop
// finds them, the text protocol on the panel's cells, its states, and its fit on an avatar.
public class OrbTests
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
        folder = "Assets/__OrbitersOrbTest-" + Guid.NewGuid().ToString("N");
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
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        Standalone(prefab);
        Assert.AreEqual(prefab.transform, OrbInstaller.FindAll(prefab).Single(), "Found by its touch contact");

        var expected = new List<(string name, VRCExpressionParameters.ValueType type, bool synced, bool saved, float value)>
        {
            ("Orb/Visible", VRCExpressionParameters.ValueType.Bool, true, true, 1),
            ("Orb/State", VRCExpressionParameters.ValueType.Int, true, false, 0),
            ("Orb/Talk", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Touch", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Photo3D", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Menu", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Call/Menu", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Call/Pick", VRCExpressionParameters.ValueType.Float, false, false, 0),
            ("Orb/Call/Go", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Call/End", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("Orb/Text/Visible", VRCExpressionParameters.ValueType.Bool, true, false, 0),
            ("Orb/Text/Pointer", VRCExpressionParameters.ValueType.Int, true, false, 0),
        };
        for (int i = 0; i < 8; i++) expected.Add(("Orb/Text/Sync" + i, VRCExpressionParameters.ValueType.Float, true, false, 0));
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        // The screen's parameters follow (TheScreensTouchPointsAndParametersAreLocal).
        CollectionAssert.AreEqual(expected, parameters.parameters.Take(expected.Count).Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToList());
        Assert.IsTrue(parameters.parameters.Skip(expected.Count).All(p => p.name.StartsWith("Orb/Screen/", StringComparison.Ordinal)));
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8, parameters.CalcTotalCost(), "Synced bits: 1 + 8 + 1 + 8 + 8·N");

        // Global in the Full Controller, so VRCFury keeps the names OSC looks for; its own parameters are renamed as usual.
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (var p in expected) Assert.IsTrue(VrcFury.IsGlobalParameter(full, p.name), p.name + " is global");
        Assert.IsFalse(VrcFury.IsGlobalParameter(full, OrbInstaller.Cell(0)));
        Assert.IsTrue(expected.All(p => VrcParameters.OscDriven(p.name)), "Optimizers leave OSC's parameters alone");

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller");
        var types = controller.parameters.ToDictionary(p => p.name, p => p.type);
        foreach (var p in expected)
            Assert.AreEqual(p.type == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int
                : p.type == VRCExpressionParameters.ValueType.Float ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool, types[p.name], p.name);
        Assert.IsTrue(controller.parameters.Single(p => p.name == OrbInstaller.Visible).defaultBool, "Shown until asked otherwise");

        var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single();
        Assert.AreEqual(("Orb", VRCExpressionsMenu.Control.ControlType.SubMenu), (menu.name, menu.type));
        CollectionAssert.AreEqual(new[]
            {
                ("Show Orb", VRCExpressionsMenu.Control.ControlType.Toggle, "Orb/Visible"), ("Menu", VRCExpressionsMenu.Control.ControlType.Toggle, "Orb/Menu"),
                ("Talk", VRCExpressionsMenu.Control.ControlType.Button, "Orb/Talk"),
                ("3D picture", VRCExpressionsMenu.Control.ControlType.Button, "Orb/Photo3D"), ("Call", VRCExpressionsMenu.Control.ControlType.SubMenu, ""),
            },
            menu.subMenu.controls.Select(c => (c.name, c.type, c.parameter.name)).ToArray());

        // The rig: the tether's tip carries the orb, the owner's hands touch it, the panel waits off.
        var root = prefab.transform;
        var tether = root.Find(OrbInstaller.Tether).GetComponent<VRCPhysBone>();
        Assert.AreEqual(root.Find(OrbInstaller.Ball), tether.ignoreTransforms.Single(), "What the orb holds is no part of the chain");
        Assert.AreEqual(0, tether.gravity);
        var touch = root.Find(OrbInstaller.Toucher).GetComponent<VRCContactReceiver>();
        Assert.IsTrue(touch.allowSelf && !touch.allowOthers && touch.localOnly);
        CollectionAssert.AreEquivalent(new[] { "Hand", "Finger" }, touch.collisionTags);
        Assert.IsTrue(root.Find(OrbInstaller.Shown).gameObject.activeSelf);
        var panel = root.Find(OrbInstaller.Panel);
        Assert.IsFalse(panel.gameObject.activeSelf, "No text yet");
        var screen = panel.GetComponent<SkinnedMeshRenderer>();
        CollectionAssert.AreEqual(new[] { panel, root.Find(OrbInstaller.Emitter) }, screen.bones, "The beam reaches from the orb to the panel");
        Assert.IsTrue(screen.sharedMaterials.All(m => m.shader.name == "Orbiters/Orb Text" && m.shader.isSupported));
        Assert.AreEqual("Orbiters/Orb", root.Find(OrbInstaller.Body).GetComponent<MeshRenderer>().sharedMaterial.shader.name);
        var atlas = (Texture2D)screen.sharedMaterials[0].GetTexture("_MainTex");
        Assert.AreEqual(folder + "/Orb text.png", AssetDatabase.GetAssetPath(atlas));
        Assert.AreEqual(2048, Mathf.Max(atlas.width, atlas.height), "Imported at most 2048 on its longest side");
        Assert.AreEqual(2, atlas.height / atlas.width, "16 × 16 tiles twice as tall as wide");
        Assert.Greater(atlas.mipmapCount, 1);
    }

    [TestCase(4)]
    [TestCase(16)]
    public void TheNumberOfSyncValuesSetsTheBlocksAndTheBits(int sync)
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        OrbInstaller.CreatePrefab(folder, sync);
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * sync, parameters.CalcTotalCost());
        Assert.AreEqual(sync, parameters.parameters.Count(p => p.name.StartsWith(OrbInstaller.TextSyncPrefix, StringComparison.Ordinal)));
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller");
        var blocks = controller.layers.Single(l => l.name == "Text block").stateMachine.states.Count(s => s.state.name.StartsWith("Block "));
        Assert.AreEqual(OrbInstaller.Cells / sync, blocks);
        Assert.Throws<ArgumentOutOfRangeException>(() => OrbInstaller.CreatePrefab(folder + "-5", 5));
    }

    [Test]
    public void SyncValuesWriteTheirBlocksCellsAndTheCellsStay()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var root = Play(prefab);
        var screen = root.transform.Find(OrbInstaller.Panel).GetComponent<SkinnedMeshRenderer>();
        playable.SetBool(OrbInstaller.TextVisible, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Orb/Text/Visible shows the panel");

        int[] Page() { var block = new MaterialPropertyBlock(); screen.GetPropertyBlock(block); return Enumerable.Range(0, 128).Select(c => Glyph(block.GetFloat("_Char" + c))).ToArray(); }
        void Send(int pointer, params int[] glyphs)
        {
            playable.SetInteger(OrbInstaller.TextPointer, pointer);
            for (int i = 0; i < glyphs.Length; i++) playable.SetFloat(OrbInstaller.TextSync(i), SyncValue(glyphs[i]));
            Tick(OrbInstaller.ValueLag + 4);
        }

        // The contract's encoding, both ends of each half and the glyphs past 128.
        var first = new[] { 0, 95, 127, 129, 223, 255, 65, 1 };
        Send(1, first);
        CollectionAssert.AreEqual(first, Page().Take(8), "Pointer 1 writes cells 0 to 7");
        Assert.IsTrue(Page().Skip(8).All(g => g == 0), "and nothing else");
        Assert.AreEqual(SyncValue(129), playable.GetFloat(OrbInstaller.Cell(3)), 1e-6f, "A cell keeps the float it was sent");

        // A new block and its values in the same update: the previous block keeps its cells.
        var second = new[] { 40, 69, 76, 76, 79, 158, 224, 33 };
        Send(2, second);
        CollectionAssert.AreEqual(first, Page().Take(8), "Block 1 kept");
        CollectionAssert.AreEqual(second, Page().Skip(8).Take(8), "Pointer 2 writes cells 8 to 15");

        // Values VRChat applied before their pointer (up to ValueLag updates): the block pointed at still keeps its cells.
        var third = new[] { 50, 51, 52, 53, 54, 55, 56, 57 };
        for (int i = 0; i < 8; i++) playable.SetFloat(OrbInstaller.TextSync(i), SyncValue(third[i]));
        Tick(OrbInstaller.ValueLag);
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
        var floats = Enumerable.Range(0, 128).Select(c => playable.GetFloat(OrbInstaller.Cell(c))).ToArray();
        Tick(3600);
        CollectionAssert.AreEqual(held, Page());
        for (int c = 0; c < 128; c++) Assert.AreEqual(floats[c], playable.GetFloat(OrbInstaller.Cell(c)), 1e-6f, "Cell " + c);

        // The panel turned off and on again shows what it held; pointer 255 clears every cell to a space.
        playable.SetBool(OrbInstaller.TextVisible, false); Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
        playable.SetBool(OrbInstaller.TextVisible, true); Tick(3);
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
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var root = Play(prefab);
        var body = root.transform.Find(OrbInstaller.Body).GetComponent<MeshRenderer>();
        var screen = root.transform.Find(OrbInstaller.Panel).GetComponent<SkinnedMeshRenderer>();
        var hop = root.transform.Find(OrbInstaller.Hop);
        float Look(string property) { var block = new MaterialPropertyBlock(); body.GetPropertyBlock(block); return block.GetFloat(property); }
        float Beam() { var block = new MaterialPropertyBlock(); screen.GetPropertyBlock(block); return block.GetFloat("_Beam"); }
        // Every property the state sets, as its look says (the ends of the transition).
        void Show(OrbState state)
        {
            playable.SetInteger(OrbInstaller.State, (int)state); Tick(20);
            var look = OrbInstaller.Looks[Mathf.Clamp((int)state, 0, 7) == (int)state ? (int)state : 0];
            foreach (var (property, value) in look.Values(look == OrbInstaller.Looks[(int)OrbState.Listening] ? playable.GetFloat("Voice") : 0))
                Assert.AreEqual(value, Look(property), 1e-4f, state + " " + property);
            Assert.AreEqual(look.Beam, Beam(), 1e-4f);
        }
        playable.SetBool(OrbInstaller.TextVisible, true);

        Tick(5);
        Assert.AreEqual((1f, 0f, 0f, 0f), (Look("_Open"), Look("_Ring"), Look("_Anger"), Beam()), "Idle");
        Show(OrbState.Listening);
        Assert.Greater(Look("_Glow"), 1.2f); Assert.Greater(Look("_Open"), 1.1f);
        playable.SetFloat("Voice", 1); Tick(3);
        Assert.AreEqual(1, Look("_Pulse"), 1e-4f, "Glowing up with its owner's voice");
        playable.SetFloat("Voice", 0);
        Show(OrbState.Thinking);
        Assert.AreEqual(1, Look("_Ring"), 1e-4f); Assert.Greater(Look("_Lid"), .2f); Assert.Less(Look("_Open"), .8f); Assert.Greater(Look("_LookY"), .05f);
        Show(OrbState.Talking);
        Assert.AreEqual((1f, 1f, 0f), (Beam(), Look("_Talk"), Look("_Ring")), "The beam projects the panel");
        Show(OrbState.Sleeping);
        Assert.Less(Look("_Glow"), .5f); Assert.AreEqual(0, Look("_Open")); Assert.Greater(Look("_Curve"), 0, "Closed eyes"); Assert.AreEqual(0, Beam());
        Show(OrbState.Happy);
        Assert.AreEqual(0, Look("_Open")); Assert.Less(Look("_Curve"), 0, "Arches");
        Show(OrbState.Annoyed);
        Assert.AreEqual(1, Look("_Anger"), 1e-4f); Assert.Greater(Look("_Tilt"), .2f); Assert.Less(Look("_Open"), .4f);
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Surprised);
        float highest = 0;
        for (int i = 0; i < 30; i++) { Tick(1); highest = Mathf.Max(highest, hop.localPosition.y); }
        Assert.Greater(highest, .02f, "A short jump");
        Assert.AreEqual(0, hop.localPosition.y, 1e-4f, "and back down");
        Assert.Greater(Look("_Open"), 1.05f); Assert.Greater(Look("_Wide"), 1.4f);
        Show((OrbState)42);
        Assert.AreEqual((1f, 0f, 0f, 1f), (Look("_Open"), Look("_Anger"), Beam(), Look("_Glow")), "Any other value shows as idle");

        playable.SetBool(OrbInstaller.Visible, false); Tick(3);
        Assert.IsFalse(root.transform.Find(OrbInstaller.Shown).gameObject.activeSelf, "Orb/Visible off hides the orb and its panel");
        playable.SetBool(OrbInstaller.Visible, true); Tick(3);
        Assert.IsTrue(root.transform.Find(OrbInstaller.Shown).gameObject.activeSelf);
    }

    [Test]
    public void ItFitsTheAvatarsHeadAndEyes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.2f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);

        var frame = orb.Find(OrbInstaller.Head);
        Assert.AreEqual(head, frame.GetComponent<VRCPositionConstraint>().Sources[0].SourceTransform, "On the head bone");
        Assert.AreEqual(avatar, frame.GetComponent<VRCRotationConstraint>().Sources[0].SourceTransform, "Turned like the avatar");
        Assert.Less(Vector3.Distance(head.position, frame.position), 1e-4f);
        Assert.AreEqual(1.2f / OrbInstaller.ReferenceEyes, frame.lossyScale.x, 1e-4f, "As large as the avatar's eyes are high");
        // The orb at the side of the eyes and in front of them, the panel lower and further, out of the face's way.
        var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
        var ball = orb.Find(OrbInstaller.Tether + "/1/2");
        var corner = orb.Find(OrbInstaller.Panel);
        Vector3 From(Transform t) => avatar.InverseTransformDirection(t.position - eyes);
        Assert.Less(Vector3.Distance(From(ball), OrbInstaller.OrbFromEyes * frame.lossyScale.x), 1e-3f);
        Assert.Less(Vector3.Distance(From(corner), OrbInstaller.PanelFromEyes * frame.lossyScale.x), 1e-3f);
        Assert.Less(Quaternion.Angle(avatar.rotation, corner.rotation), .01f, "The panel faces ahead, upright");
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(orb.gameObject, avatar).Kind, "Attached by its own constraints");

        // Bound again (a moved armature), the fit follows.
        head.position += Vector3.up * .1f;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        Assert.Less(Vector3.Distance(head.position, orb.Find(OrbInstaller.Head).position), 1e-4f);
    }

    [Test]
    public void ItFollowsTheHeadTurnedLikeTheAvatarNotLikeTheHead()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.6f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        var frame = orb.Find(OrbInstaller.Head);
        var panel = orb.Find(OrbInstaller.Panel);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(orb.gameObject);
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

    // ---- The projected screen (Docs/orb.md, Screen) ----

    // An orb (every orb has its screen), playing.
    private (GameObject root, MeshRenderer screen) PlayWithScreen(int textSync = 8, int thumbnailBytes = 72)
    {
        var root = Play(AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder, textSync, thumbnailBytes)));
        return (root, root.transform.Find(OrbInstaller.ScreenObject).GetComponent<MeshRenderer>());
    }

    // The screen's memory as its shader reads it (word k: b0 + 256 b1).
    private static byte[] Memory(MeshRenderer screen, int thumbnailBytes = 72)
    {
        var block = new MaterialPropertyBlock();
        screen.GetPropertyBlock(block);
        var bytes = new byte[OrbInstaller.ScreenBytes(thumbnailBytes)];
        for (int k = 0; k < OrbInstaller.ScreenWords(thumbnailBytes); k++)
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
        var bytes = new byte[OrbInstaller.ScreenBytes(thumbnailBytes)];
        for (int i = 0; i < bytes.Length; i++) { int b = (i * 37 + seed * 11 + 5) % 256; bytes[i] = (byte)(b == 128 ? 129 : b); }
        return bytes;
    }

    [Test]
    public void TheScreensTouchPointsAndParametersAreLocal()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, screen) = PlayWithScreen();
        Assert.IsTrue(OrbInstaller.HasScreen(root.transform));
        Assert.AreEqual(folder + "/Orb screen.mat", AssetDatabase.GetAssetPath(screen.sharedMaterial), "Its files in the orb's folder, which a package carries");
        Assert.IsFalse(screen.gameObject.activeSelf, "Hidden until it opens");
        var receivers = screen.GetComponentsInChildren<VRCContactReceiver>(true);
        Assert.AreEqual(8 + 4 + 4, receivers.Length, "Four touch points per index finger, four each for the eyes and the gaze point");
        foreach (var r in receivers)
        {
            Assert.IsTrue(r.localOnly && r.allowSelf && !r.allowOthers, "Local: the owner's own fingers only, nothing in the performance rank");
            Assert.AreEqual(VRC.Dynamics.ContactReceiver.ReceiverType.Proximity, r.receiverType);
            if (r.parameter.StartsWith(OrbInstaller.ScreenGazePrefix, StringComparison.Ordinal)) { Assert.AreEqual(OrbInstaller.GazeTag, r.collisionTags.Single()); continue; }
            if (r.parameter.StartsWith(OrbInstaller.ScreenEyePrefix, StringComparison.Ordinal)) { Assert.AreEqual(OrbInstaller.EyeTag, r.collisionTags.Single()); continue; }
            StringAssert.StartsWith(OrbInstaller.ScreenTouchPrefix, r.parameter);
            Assert.AreEqual(r.parameter.Contains("/R") ? "FingerIndexR" : "FingerIndexL", r.collisionTags.Single());
        }
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var screenParameters = parameters.parameters.Where(p => p.name.StartsWith("Orb/Screen/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1 + 1 + 1 + 1 + OrbInstaller.ScreenLocalValues + 8 + 8, screenParameters.Count, "Open, Replace, ThumbnailBytes, the pointer, 16 values, 8 touch points, 4 each for the eyes and the gaze");
        Assert.IsTrue(screenParameters.All(p => !p.networkSynced), "No synced bits");
        var replace = screenParameters.Single(p => p.name == OrbInstaller.ScreenReplace);
        Assert.AreEqual((0f, false), (replace.defaultValue, replace.saved), "Off unless the card's option is on, and never saved: each upload's option holds");
        var thumbnails = screenParameters.Single(p => p.name == OrbInstaller.ScreenThumbnailBytes);
        Assert.AreEqual((72f, false, VRCExpressionParameters.ValueType.Int), (thumbnails.defaultValue, thumbnails.saved, thumbnails.valueType), "72 bytes unless the card says 128, never saved");
        Assert.AreEqual(OrbInstaller.ScreenShaderName(72), screen.sharedMaterial.shader.name);
        var hold = screen.GetComponent<VRCParentConstraint>();
        Assert.IsFalse(hold.RebakeOffsetsWhenUnfrozen, "Opened again, it appears before the eyes, not where it was");
    }

    [Test]
    public void TheOwnersScreenKeepsItsLocalBlocksAndIgnoresTheSyncedOnes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (_, screen) = PlayWithScreen();
        playable.SetBool("IsLocal", true);
        playable.SetInteger(OrbInstaller.VRMode, 0);
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Orb/Screen/Open shows it in desktop mode");
        Assert.IsTrue(screen.GetComponent<VRCParentConstraint>().FreezeToWorld, "Frozen where it appeared");

        void Local(int pointer, byte[] bytes, int from)
        {
            playable.SetInteger(OrbInstaller.ScreenPointer, pointer);
            for (int i = 0; i < OrbInstaller.ScreenLocalValues; i++)
                playable.SetFloat(OrbInstaller.ScreenValue(i), SyncValue(from + i < bytes.Length ? bytes[from + i] : 0));
            // The desktop holds each block for two frames or more.
            Tick(2);
        }
        var data = Pattern(1);
        int blocks = (OrbInstaller.ScreenBytes(72) + 15) / 16;
        for (int b = 0; b < blocks; b++) Local(b + 1, data, b * 16);
        Local(0, data, 0);
        CollectionAssert.AreEqual(data, Memory(screen), "Every block written");

        // A minute later nothing drifted.
        Tick(3000);
        CollectionAssert.AreEqual(data, Memory(screen));

        // The text channel's screen blocks are for everyone else.
        playable.SetInteger(OrbInstaller.TextPointer, OrbInstaller.ScreenPointers(8, 72).first);
        for (int i = 0; i < 8; i++) playable.SetFloat(OrbInstaller.TextSync(i), SyncValue(7));
        Tick(4);
        CollectionAssert.AreEqual(data, Memory(screen), "The owner ignores the synced screen blocks");

        // Rewritten block by block, then cleared.
        var again = Pattern(2);
        for (int b = 0; b < blocks; b++) Local(b + 1, again, b * 16);
        Local(0, again, 0);
        CollectionAssert.AreEqual(again, Memory(screen));
        Local(OrbInstaller.ScreenLocalClear, again, 0);
        Local(0, again, 0);
        Assert.IsTrue(Memory(screen).All(v => v == 0), "Pointer 255 clears the owner's screen");

        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
        Assert.IsFalse(screen.GetComponent<VRCParentConstraint>().FreezeToWorld, "Closed, it follows the head again");

        // The menu's Menu toggle opens it at once, whatever the desktop does, and closes it when both are off.
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Orb/Menu shows the owner's screen");
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Still open while the desktop has it open");
        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Both off: closed");

        // In VR the overlay shows the menu, unless the screen replaces it.
        playable.SetInteger(OrbInstaller.VRMode, 1);
        playable.SetBool(OrbInstaller.Menu, true);
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "In VR without Replace the screen stays closed");
        playable.SetBool(OrbInstaller.ScreenReplace, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Replace: the screen in VR too");
        playable.SetBool(OrbInstaller.Menu, false);
        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
    }

    // Desktop mode: the owner points by looking. The eyes and the gaze point stay on the line of sight however the head
    // turns, and Orbiters desktop's reading (Docs/orb.md, Screen: the line through the two located points, on the
    // screen's plane) lands where the owner looks.
    [Test]
    public void TheGazePointFollowsTheLineOfSightOntoTheScreen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.4f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        var eye = orb.Find(OrbInstaller.Gaze);
        var gaze = orb.Find(OrbInstaller.GazePoint);
        foreach (var (at, tag) in new[] { (eye, OrbInstaller.EyeTag), (gaze, OrbInstaller.GazeTag) })
        {
            var sender = at.GetComponent<VRCContactSender>();
            Assert.IsTrue(sender.localOnly, "Local: nothing in the performance rank");
            Assert.AreEqual(tag, sender.collisionTags.Single());
        }
        var screen = orb.Find(OrbInstaller.ScreenObject);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(orb.gameObject);
            solver.Solve();
            var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
            float size = 1.4f / 1.6f;
            Assert.Less(Vector3.Distance(eyes + avatar.forward * OrbInstaller.GazeDistance * size, gaze.position), 1e-4f, "Straight ahead of the eyes at rest");
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
        var dot = root.transform.Find(OrbInstaller.GazeDot).gameObject;
        playable.SetBool("IsLocal", true);
        playable.SetInteger(OrbInstaller.VRMode, 0);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "Menu closed");
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsTrue(dot.activeSelf, "Desktop mode, menu open");
        playable.SetInteger(OrbInstaller.VRMode, 1);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "In VR fingers point");
        playable.SetInteger(OrbInstaller.VRMode, 0);
        playable.SetBool("IsLocal", false);
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsFalse(dot.activeSelf, "Nobody else sees it");
    }

    // VRCFury reverts every override of its components in a prefab instance when an upload starts (VRCFPrefabFixer): the
    // options of an orb placed from a prefab survive it.
    [Test]
    public void OptionsOfAPlacedOrbSurviveVrcFurysRevertBeforeAnUpload()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Assert.AreEqual((8, false, false, 72), OrbInstaller.OptionsOf(placed.transform));
        OrbInstaller.SetOptions(placed.transform, 4, true, true);
        Assert.AreEqual((4, true, true, 72), OrbInstaller.OptionsOf(placed.transform));
        Assert.Throws<ArgumentException>(() => OrbInstaller.SetOptions(placed.transform, 4, true, true, 128), "128-byte thumbnails need 8 text slots");

        // What VRCFury does to the avatar before the SDK copies it.
        foreach (var component in placed.GetComponents(VrcFury.Component))
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            var mods = PrefabUtility.GetPropertyModifications(component)?.Where(m => m.target == source).ToList();
            if (mods != null && mods.Count > 0) PrefabUtility.RevertObjectOverride(component, InteractionMode.AutomatedAction);
        }
        Assert.AreEqual((4, true, true, 72), OrbInstaller.OptionsOf(placed.transform), "Kept on the orb's own Full Controller");
        Assert.AreEqual(1, placed.GetComponents(VrcFury.Component).Length, "The prefab's Full Controller replaced, not doubled");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, OrbInstaller.ScreenReplace));

        // 128-byte thumbnails: the parameter's default, the screen's material and memory follow.
        OrbInstaller.SetOptions(placed.transform, 8, false, true, 128);
        Assert.AreEqual((8, false, true, 128), OrbInstaller.OptionsOf(placed.transform));
        var renderer = placed.transform.Find(OrbInstaller.ScreenObject).GetComponent<MeshRenderer>();
        Assert.AreEqual(OrbInstaller.ScreenShaderName(128), renderer.sharedMaterial.shader.name);
        Assert.IsTrue(renderer.sharedMaterial.HasProperty("_W511") && !renderer.sharedMaterial.HasProperty("_W512"), "1024 bytes: 512 words");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, OrbInstaller.ScreenThumbnailBytes));

        // And back: the screen stays, Replace off, 72-byte thumbnails.
        OrbInstaller.SetOptions(placed.transform, 8, false, false);
        Assert.AreEqual((8, false, false, 72), OrbInstaller.OptionsOf(placed.transform));
        Assert.IsTrue(OrbInstaller.HasScreen(placed.transform));
        Assert.AreEqual(OrbInstaller.ScreenShaderName(72), renderer.sharedMaterial.shader.name);
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
        var (first, blocks) = OrbInstaller.ScreenPointers(textSync, thumbnailBytes);
        Assert.AreEqual(OrbInstaller.Cells / textSync + 1, first);
        Assert.Less(first + blocks - 1, OrbInstaller.PointerScreenClose, "Every block has a pointer");

        void Synced(int pointer, byte[] bytes, int from)
        {
            playable.SetInteger(OrbInstaller.TextPointer, pointer);
            for (int i = 0; i < textSync; i++) playable.SetFloat(OrbInstaller.TextSync(i), SyncValue(from + i < bytes.Length ? bytes[from + i] : 0));
            Tick(OrbInstaller.ValueLag + 4);
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
        Synced(OrbInstaller.PointerScreenClose, data, 0);
        Assert.IsFalse(screen.gameObject.activeSelf, "Pointer 254 closes it");
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
        graph = PlayableGraph.Create("Orb test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Orb.controller"));
        AnimationPlayableOutput.Create(graph, "Orb", root.AddComponent<Animator>()).SetSourcePlayable(playable);
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
        var root = new GameObject("Orb avatar");
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
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
        }
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
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
