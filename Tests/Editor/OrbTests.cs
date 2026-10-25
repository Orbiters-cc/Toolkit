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

// N4N0, the Orbiters companion (contracts Docs/orb.md), built whole into a temporary folder: its parameters as the desktop
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
            ("N4N0/Visible", VRCExpressionParameters.ValueType.Bool, true, true, 1),
            ("N4N0/State", VRCExpressionParameters.ValueType.Int, true, false, 0),
            ("N4N0/Talk", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Touch", VRCExpressionParameters.ValueType.Bool, false, false, 0),
            ("N4N0/Photo3D", VRCExpressionParameters.ValueType.Bool, false, false, 0),
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
        Assert.IsTrue(parameters.parameters.Skip(expected.Count).All(p => p.name.StartsWith("N4N0/Screen/", StringComparison.Ordinal) || p.name.StartsWith("N4N0/Card/", StringComparison.Ordinal)),
            "then the screen's and the link card's");
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8, parameters.CalcTotalCost(), "Synced bits: 1 + 8 + 1 + 8 + 8·N");

        // Global in the Full Controller, so VRCFury keeps the names OSC looks for; its own parameters are renamed as usual.
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (var p in expected) Assert.IsTrue(VrcFury.IsGlobalParameter(full, p.name), p.name + " is global");
        Assert.IsFalse(VrcFury.IsGlobalParameter(full, OrbInstaller.Cell(0)));
        Assert.IsTrue(expected.All(p => VrcParameters.OscDriven(p.name)), "Optimizers leave OSC's parameters alone");

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        var types = controller.parameters.ToDictionary(p => p.name, p => p.type);
        foreach (var p in expected)
            Assert.AreEqual(p.type == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int
                : p.type == VRCExpressionParameters.ValueType.Float ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool, types[p.name], p.name);
        Assert.IsTrue(controller.parameters.Single(p => p.name == OrbInstaller.Visible).defaultBool, "Shown until asked otherwise");

        var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single();
        Assert.AreEqual(("N4N0", VRCExpressionsMenu.Control.ControlType.SubMenu), (menu.name, menu.type));
        CollectionAssert.AreEqual(new[]
            {
                ("Show N4N0", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Visible"), ("Menu", VRCExpressionsMenu.Control.ControlType.Toggle, "N4N0/Menu"),
                ("Talk", VRCExpressionsMenu.Control.ControlType.Button, "N4N0/Talk"),
                ("3D picture", VRCExpressionsMenu.Control.ControlType.Button, "N4N0/Photo3D"), ("Call", VRCExpressionsMenu.Control.ControlType.SubMenu, ""),
            },
            menu.subMenu.controls.Select(c => (c.name, c.type, c.parameter.name)).ToArray());

        // The rig: the tether's tip carries N4N0, the owner's hands touch it, the panel waits off.
        var root = prefab.transform;
        var tether = root.Find(OrbInstaller.Tether).GetComponent<VRCPhysBone>();
        Assert.AreEqual(root.Find(OrbInstaller.Ball), tether.ignoreTransforms.Single(), "What N4N0 holds is no part of the chain");
        Assert.AreEqual(0, tether.gravity);
        var touch = root.Find(OrbInstaller.Toucher).GetComponent<VRCContactReceiver>();
        Assert.IsTrue(touch.allowSelf && !touch.allowOthers && touch.localOnly);
        CollectionAssert.AreEquivalent(new[] { "Hand", "Finger" }, touch.collisionTags);
        Assert.IsTrue(root.Find(OrbInstaller.Shown).gameObject.activeSelf);
        var panel = root.Find(OrbInstaller.Panel);
        Assert.IsFalse(panel.gameObject.activeSelf, "No text yet");
        var screen = panel.GetComponent<SkinnedMeshRenderer>();
        CollectionAssert.AreEqual(new[] { panel, root.Find(OrbInstaller.Emitter) }, screen.bones, "The beam reaches from N4N0 to the panel");
        Assert.IsTrue(screen.sharedMaterials.All(m => m.shader.name == "Orbiters/N4N0 Text" && m.shader.isSupported));
        Assert.AreEqual("Orbiters/N4N0", root.Find(OrbInstaller.Body).GetComponent<MeshRenderer>().sharedMaterial.shader.name);
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
        OrbInstaller.CreatePrefab(folder, sync);
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * sync, parameters.CalcTotalCost());
        Assert.AreEqual(sync, parameters.parameters.Count(p => p.name.StartsWith(OrbInstaller.TextSyncPrefix, StringComparison.Ordinal)));
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
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
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Text/Visible shows the panel");

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
        Assert.IsFalse(root.transform.Find(OrbInstaller.Shown).gameObject.activeSelf, "N4N0/Visible off hides N4N0 and its panel");
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
        // N4N0 at the side of the eyes and in front of them, the panel lower and further, out of the face's way.
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

    // N4N0 (every N4N0 has its screen), playing.
    private (GameObject root, SkinnedMeshRenderer screen) PlayWithScreen(int textSync = 8, int thumbnailBytes = 72)
    {
        var root = Play(AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder, textSync, thumbnailBytes)));
        return (root, root.transform.Find(OrbInstaller.ScreenObject).GetComponent<SkinnedMeshRenderer>());
    }

    // The screen's memory as its shader reads it (word k: b0 + 256 b1).
    private static byte[] Memory(Renderer screen, int thumbnailBytes = 72)
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
        Assert.AreEqual(folder + "/N4N0 screen.mat", AssetDatabase.GetAssetPath(screen.sharedMaterial), "Its files in N4N0's folder, which a package carries");
        Assert.IsFalse(screen.gameObject.activeSelf, "Hidden until it opens");
        var receivers = screen.GetComponentsInChildren<VRCContactReceiver>(true);
        Assert.AreEqual(6 + 4 + 4, receivers.Length, "Three touch points per hand's pointer, four each for the eyes and the gaze point");
        foreach (var r in receivers)
        {
            Assert.IsTrue(r.localOnly && r.allowSelf && !r.allowOthers, "Local: the owner's own fingers only, nothing in the performance rank");
            Assert.AreEqual(VRC.Dynamics.ContactReceiver.ReceiverType.Proximity, r.receiverType);
            if (r.parameter.StartsWith(OrbInstaller.ScreenGazePrefix, StringComparison.Ordinal)) { Assert.AreEqual(OrbInstaller.GazeTag, r.collisionTags.Single()); continue; }
            if (r.parameter.StartsWith(OrbInstaller.ScreenEyePrefix, StringComparison.Ordinal)) { Assert.AreEqual(OrbInstaller.EyeTag, r.collisionTags.Single()); continue; }
            StringAssert.StartsWith(OrbInstaller.ScreenTouchPrefix, r.parameter);
            Assert.AreEqual(OrbInstaller.PointerTag(r.parameter.Contains("/R")), r.collisionTags.Single());
        }
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var screenParameters = parameters.parameters.Where(p => p.name.StartsWith("N4N0/Screen/", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1 + 1 + 1 + 1 + 1 + OrbInstaller.ScreenLocalValues + 6 + 8 + 4 + 1, screenParameters.Count,
            "Open, Replace, ThumbnailBytes, Version, the pointer, 16 values, 6 touch points, 4 each for the eyes and the gaze, each ray's hit and ratio, Drop");
        Assert.IsTrue(screenParameters.All(p => !p.networkSynced), "No synced bits");
        var replace = screenParameters.Single(p => p.name == OrbInstaller.ScreenReplace);
        Assert.AreEqual((0f, false), (replace.defaultValue, replace.saved), "Off unless the card's option is on, and never saved: each upload's option holds");
        var thumbnails = screenParameters.Single(p => p.name == OrbInstaller.ScreenThumbnailBytes);
        Assert.AreEqual((72f, false, VRCExpressionParameters.ValueType.Int), (thumbnails.defaultValue, thumbnails.saved, thumbnails.valueType), "72 bytes unless the card says 128, never saved");
        var version = screenParameters.Single(p => p.name == OrbInstaller.ScreenVersionParameter);
        Assert.AreEqual(((float)OrbInstaller.ScreenVersion, false, VRCExpressionParameters.ValueType.Int), (version.defaultValue, version.saved, version.valueType),
            "The screen says its version: Orbiters - N4N0 shows a version 3 screen the six-tile Home, and lets a version 4 one point with rays");
        Assert.AreEqual(OrbInstaller.ScreenShaderName(72), screen.sharedMaterial.shader.name);
        Assert.AreEqual(folder + "/N4N0 screen beam.mat", AssetDatabase.GetAssetPath(screen.sharedMaterials[1]), "N4N0's beam, in N4N0's folder too");
        var hold = screen.GetComponent<VRCParentConstraint>();
        Assert.AreEqual(root.transform.Find(OrbInstaller.ScreenRig), hold.Sources[0].SourceTransform, "Held to its swing");
        Assert.IsFalse(hold.FreezeToWorld, "It follows its owner, never frozen in the world");
    }

    [Test]
    public void TheScreenSwingsAroundTheEyesAfterItsOwnerWithN4N0sBeam()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.4f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        var rig = orb.Find(OrbInstaller.ScreenRig);
        var screen = orb.Find(OrbInstaller.ScreenObject);
        var swing = rig.GetComponent<VRCPhysBone>();
        Assert.AreEqual(rig, swing.rootTransform);
        Assert.AreEqual((VRCPhysBoneBase.AdvancedBool.False, VRCPhysBoneBase.AdvancedBool.False, VRCPhysBoneBase.AdvancedBool.False),
            (swing.allowGrabbing, swing.allowCollision, swing.allowPosing), "Nobody grabs, poses or pushes it");
        Assert.AreEqual(0, swing.gravity, "It hangs straight ahead, not down");
        Assert.AreEqual(1, rig.childCount, "Its arm is the chain's only bone");
        Assert.AreNotEqual(Vector3.zero, swing.endpointPosition, "A lone bone needs its end");
        var renderer = screen.GetComponent<SkinnedMeshRenderer>();
        CollectionAssert.AreEqual(new[] { screen, orb.Find(OrbInstaller.Emitter) }, renderer.bones, "The beam's apex rides N4N0");
        Assert.AreEqual("Orbiters/N4N0 Beam", renderer.sharedMaterials[1].shader.name);
        Assert.AreEqual(2, renderer.sharedMesh.subMeshCount, "The page, then the beam");
        // Shown, as the animator shows it when it opens: a hidden screen's constraint does not run.
        screen.gameObject.SetActive(true);
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(orb.gameObject);
            solver.Solve();
            var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
            float size = 1.4f / OrbInstaller.ReferenceEyes;
            Assert.Less(Vector3.Distance(eyes, rig.position), 1e-3f, "The swing turns around the eyes");
            Assert.Less(Vector3.Distance(eyes + avatar.rotation * OrbInstaller.ScreenFromEyes * size, screen.position), 1e-3f, "Before the eyes at rest");
            Assert.Less(Quaternion.Angle(avatar.rotation, screen.rotation), .01f, "Facing its owner");
            // The PhysBone turns the root, lagging behind the head and the avatar's turns: the screen swings with it around
            // the eyes, still facing them.
            var lag = Quaternion.Euler(8, -20, 0);
            rig.localRotation = lag;
            solver.Solve();
            Assert.Less(Vector3.Distance(eyes + avatar.rotation * lag * OrbInstaller.ScreenFromEyes * size, screen.position), 1e-3f);
            Assert.Less(Quaternion.Angle(avatar.rotation * lag, screen.rotation), .01f);
        }
        finally { solver.Release(); }
    }

    // The link card (Docs/orb.md, Link card): the screen held where it was dropped, for its owner from N4N0/Screen/Drop and for
    // everyone else from pointer 253 until 254 closes it, its sender on meanwhile; every N4N0's hands feel cards, locally.
    [Test]
    public void ADroppedLinkCardStaysWhereItIsAndHandsFeelIt()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, screen) = PlayWithScreen();
        var sender = root.transform.Find(OrbInstaller.CardSender).GetComponent<VRCContactSender>();
        Assert.AreEqual((OrbInstaller.CardTag, false), (sender.collisionTags.Single(), sender.localOnly), "Everyone's hands feel the card");
        Assert.IsFalse(sender.gameObject.activeSelf, "Off until a card is dropped");
        foreach (bool right in new[] { false, true })
        {
            var receiver = root.transform.Find(OrbInstaller.CardHand(right)).GetComponent<VRCContactReceiver>();
            Assert.AreEqual((OrbInstaller.CardTag, OrbInstaller.CardTouch(right)), (receiver.collisionTags.Single(), receiver.parameter));
            Assert.IsTrue(receiver.allowSelf && receiver.allowOthers && receiver.localOnly, "Its own card and others', on its owner's client only");
        }
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset").parameters;
        foreach (var name in new[] { OrbInstaller.ScreenDrop, OrbInstaller.CardTouch(false), OrbInstaller.CardTouch(true) })
            Assert.IsFalse(parameters.Single(p => p.name == name).networkSynced, name + " is local");
        var hold = screen.GetComponent<VRCParentConstraint>();

        // Its owner's: N4N0/Screen/Drop.
        playable.SetBool("IsLocal", true);
        playable.SetInteger(OrbInstaller.VRMode, 0);
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        playable.SetBool(OrbInstaller.ScreenDrop, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf && hold.FreezeToWorld && sender.gameObject.activeSelf, "Dropped: held where it is, felt");
        playable.SetBool(OrbInstaller.ScreenDrop, false);
        Tick(3);
        Assert.IsFalse(hold.FreezeToWorld || sender.gameObject.activeSelf, "Picked up again: it follows its owner");
        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf);
        // In VR without Replace the overlay shows the menu, but a dropped card shows on the screen, and the Menu toggle
        // turned off meanwhile closes nothing.
        playable.SetInteger(OrbInstaller.VRMode, 1);
        playable.SetBool(OrbInstaller.Menu, true);
        playable.SetBool(OrbInstaller.ScreenDrop, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf && hold.FreezeToWorld, "The card shows in VR too");
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "Menu off doesn't take the card away");
        playable.SetBool(OrbInstaller.ScreenDrop, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Gone with the card");
        playable.SetInteger(OrbInstaller.VRMode, 0);

        // Everyone else's: pointer 253 holds it through the blocks that follow, until 254 closes it.
        playable.SetBool("IsLocal", false);
        int first = OrbInstaller.ScreenPointers(8, 72).first;
        playable.SetInteger(OrbInstaller.TextPointer, first);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf);
        Assert.IsFalse(hold.FreezeToWorld);
        playable.SetInteger(OrbInstaller.TextPointer, OrbInstaller.PointerCardDropped);
        Tick(3);
        Assert.IsTrue(hold.FreezeToWorld && sender.gameObject.activeSelf);
        playable.SetInteger(OrbInstaller.TextPointer, first + 1);
        Tick(3);
        Assert.IsTrue(hold.FreezeToWorld, "Still where it was while its blocks arrive");
        playable.SetInteger(OrbInstaller.TextPointer, OrbInstaller.PointerScreenClose);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf || hold.FreezeToWorld || sender.gameObject.activeSelf, "254 closes the card");
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
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Screen/Open shows it in desktop mode");
        Assert.IsFalse(screen.GetComponent<VRCParentConstraint>().FreezeToWorld, "It follows its owner");

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

        // The menu's Menu toggle opens it at once, whatever the desktop does, and turning it off closes it, even while
        // the desktop still has it open (the app quit or froze; in VRChat a driver then clears N4N0/Screen/Open).
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "N4N0/Menu shows the owner's screen");
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Menu off closes it, though the desktop left it open");
        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Both off: closed");

        // One the desktop opened with Menu off stays until the desktop closes it, or Menu goes on and off.
        playable.SetBool(OrbInstaller.ScreenOpen, true);
        Tick(3);
        Assert.IsTrue(screen.gameObject.activeSelf, "The desktop opens it");
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        Assert.IsFalse(screen.gameObject.activeSelf, "Menu on, then off, closes it");
        playable.SetBool(OrbInstaller.ScreenOpen, false);
        Tick(3);
        var closing = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller").layers.Single(l => l.name == "Screen")
            .stateMachine.states.Single(s => s.state.name == "Closed by menu").state;
        var driver = closing.behaviours.OfType<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>().Single();
        Assert.IsTrue(driver.localOnly);
        Assert.AreEqual((OrbInstaller.ScreenOpen, 0f), (driver.parameters.Single().name, driver.parameters.Single().value), "Menu off clears what the desktop set");

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
    // turns, and Orbiters - N4N0's reading (Docs/orb.md, Screen: the line through the two located points, on the
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

    // Pointing (Docs/orb.md, Pointing): a raycast from each index fingertip along the finger, as large as N4N0, at a
    // solid box on the screen's face.
    [Test]
    public void ItPointsFromTheIndexFingertipsAtASolidScreen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.2f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        var animator = avatar.GetComponent<Animator>();
        foreach (bool right in new[] { false, true })
        {
            var pointer = orb.Find(OrbInstaller.Pointer(right));
            var distal = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
            var middle = animator.GetBoneTransform(right ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
            Assert.AreEqual(distal, pointer.GetComponent<VRCParentConstraint>().Sources[0].SourceTransform, "Held to the last finger bone");
            var tip = distal.position + (distal.position - middle.position) * .8f;
            Assert.Less(Vector3.Distance(tip, pointer.position), 1e-4f, "At the fingertip");
            Assert.Less(Vector3.Angle((tip - middle.position).normalized, pointer.forward), .1f, "Along the finger");
            Assert.AreEqual(1.2f / OrbInstaller.ReferenceEyes, pointer.lossyScale.x, 1e-4f, "As large as N4N0");
            var ray = pointer.GetComponent<VRC.SDK3.Avatars.Components.VRCRaycast>();
            Assert.AreEqual((Vector3.forward, OrbInstaller.RayLength, true, OrbInstaller.Ray(right)), (ray.RaycastDirection, ray.Distance, ray.ApplyTransformScale, ray.Parameter));
            Assert.AreEqual(VRC.SDK3.Avatars.Components.VRCRaycast.CollisionMode.HitWorldsAndPlayers, ray.RaycastCollisionMode);
            Assert.AreEqual(pointer.Find("Bead"), ray.ResultTransform);
            var sender = ray.ResultTransform.GetComponent<VRCContactSender>();
            Assert.IsTrue(sender.localOnly);
            Assert.AreEqual(OrbInstaller.PointerTag(right), sender.collisionTags.Single());
            Assert.AreEqual(OrbInstaller.PointRadius / OrbInstaller.ReferenceEyes * 1.2f, sender.radius * sender.transform.lossyScale.x, 1e-6f, "A known, tiny contact");
        }
        var surface = orb.Find(OrbInstaller.Surface).GetComponent<BoxCollider>();
        Assert.IsFalse(surface.isTrigger, "VRChat's raycasts pass through triggers");
        Assert.AreEqual(new Vector3(OrbInstaller.ScreenWidth, OrbInstaller.ScreenHeight, OrbInstaller.SurfaceDepth), surface.size);
        Assert.IsFalse(surface.gameObject.activeSelf, "Only while the screen is open for its owner");
        Assert.IsFalse(orb.Find(OrbInstaller.Pointers).gameObject.activeSelf);
    }

    [Test]
    public void ThePointersShowForTheirOwnerInVrWhileTheScreenIsOpen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (root, _) = PlayWithScreen(textSync: 8);
        var pointers = root.transform.Find(OrbInstaller.Pointers).gameObject;
        var surface = root.transform.Find(OrbInstaller.Surface).gameObject;
        var bead = root.transform.Find(OrbInstaller.Pointer(true) + "/Bead").gameObject;
        var beam = root.transform.Find(OrbInstaller.Pointer(true) + "/Beam");
        playable.SetBool("IsLocal", true);
        playable.SetInteger(OrbInstaller.VRMode, 1);
        playable.SetBool(OrbInstaller.ScreenReplace, true);
        Tick(3);
        Assert.IsFalse(pointers.activeSelf || surface.activeSelf, "Menu closed");
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsTrue(pointers.activeSelf && surface.activeSelf, "In VR, the menu open on the screen");
        Assert.IsFalse(bead.activeSelf, "The ray meets nothing yet");
        playable.SetBool(OrbInstaller.RayHit(true), true);
        playable.SetFloat(OrbInstaller.RayRatio(true), .5f);
        Tick(3);
        Assert.IsTrue(bead.activeSelf && beam.gameObject.activeSelf);
        Assert.AreEqual(OrbInstaller.RayLength / 4, beam.localScale.z, 1e-4f, "The beam reaches the bead: half the ray, a needle centred on its half");
        Assert.AreEqual(OrbInstaller.RayLength / 4, beam.localPosition.z, 1e-4f);
        playable.SetBool(OrbInstaller.RayHit(true), false);
        Tick(3);
        Assert.IsFalse(bead.activeSelf || beam.gameObject.activeSelf);
        playable.SetInteger(OrbInstaller.VRMode, 0);
        Tick(3);
        Assert.IsFalse(pointers.activeSelf, "Desktop mode points by looking");
        playable.SetInteger(OrbInstaller.VRMode, 1);
        playable.SetBool("IsLocal", false);
        playable.SetBool(OrbInstaller.Menu, false);
        Tick(3);
        playable.SetBool(OrbInstaller.Menu, true);
        Tick(3);
        Assert.IsFalse(pointers.activeSelf || surface.activeSelf, "Nobody else has them");
    }

    // VRCFury reverts every override of its components in a prefab instance when an upload starts (VRCFPrefabFixer): the
    // options of N4N0 placed from a prefab survive it.
    [Test]
    public void OptionsOfAPlacedOrbSurviveVrcFurysRevertBeforeAnUpload()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Assert.AreEqual((8, false, false, 72, false), OrbInstaller.OptionsOf(placed.transform));
        OrbInstaller.SetOptions(placed.transform, 4, true, true);
        Assert.AreEqual((4, true, true, 72, false), OrbInstaller.OptionsOf(placed.transform));
        Assert.Throws<ArgumentException>(() => OrbInstaller.SetOptions(placed.transform, 4, true, true, 128), "128-byte thumbnails need 8 text slots");

        // What VRCFury does to the avatar before the SDK copies it.
        foreach (var component in placed.GetComponents(VrcFury.Component))
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(component);
            var mods = PrefabUtility.GetPropertyModifications(component)?.Where(m => m.target == source).ToList();
            if (mods != null && mods.Count > 0) PrefabUtility.RevertObjectOverride(component, InteractionMode.AutomatedAction);
        }
        Assert.AreEqual((4, true, true, 72, false), OrbInstaller.OptionsOf(placed.transform), "Kept on N4N0's own Full Controller");
        Assert.AreEqual(1, placed.GetComponents(VrcFury.Component).Length, "The prefab's Full Controller replaced, not doubled");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, OrbInstaller.ScreenReplace));

        // 128-byte thumbnails: the parameter's default, the screen's material and memory follow.
        OrbInstaller.SetOptions(placed.transform, 8, false, true, 128);
        Assert.AreEqual((8, false, true, 128, false), OrbInstaller.OptionsOf(placed.transform));
        var renderer = placed.transform.Find(OrbInstaller.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.AreEqual(OrbInstaller.ScreenShaderName(128), renderer.sharedMaterial.shader.name);
        Assert.IsTrue(renderer.sharedMaterial.HasProperty("_W511") && !renderer.sharedMaterial.HasProperty("_W512"), "1024 bytes: 512 words");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, OrbInstaller.ScreenThumbnailBytes));

        // And back: the screen stays, Replace off, 72-byte thumbnails.
        OrbInstaller.SetOptions(placed.transform, 8, false, false);
        Assert.AreEqual((8, false, false, 72, false), OrbInstaller.OptionsOf(placed.transform));
        Assert.IsTrue(OrbInstaller.HasScreen(placed.transform));
        Assert.AreEqual(OrbInstaller.ScreenShaderName(72), renderer.sharedMaterial.shader.name);
    }

    // The Light option (Docs/orb.md): a point light under N4N0 that everyone sees, in one of eight colours at a
    // brightness; N4N0 takes its colour. Switched off, it goes.
    [Test]
    public void TheLightOptionMakesTheOrbALightInItsColour()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        Assert.IsFalse(OrbInstaller.HasLight(placed.transform));
        OrbInstaller.SetOptions(placed.transform, 8, false, false, 72, light: true);
        Assert.AreEqual((8, false, false, 72, true), OrbInstaller.OptionsOf(placed.transform));
        var light = placed.transform.Find(OrbInstaller.LightObject).GetComponent<Light>();
        Assert.AreEqual((LightType.Point, LightShadows.None), (light.type, light.shadows));
        Assert.IsFalse(light.gameObject.activeSelf, "Off until asked");

        string shared = OrbInstaller.OptionsFolder + "Text slots 8 + light";
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(shared + "/Parameters.asset");
        var own = parameters.parameters.Where(p => p.name.StartsWith(OrbInstaller.Light, StringComparison.Ordinal))
            .Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToList();
        CollectionAssert.AreEqual(new[]
        {
            (OrbInstaller.Light, VRCExpressionParameters.ValueType.Bool, true, false, 0f),
            (OrbInstaller.LightColor, VRCExpressionParameters.ValueType.Int, true, true, 0f),
            (OrbInstaller.LightLevel, VRCExpressionParameters.ValueType.Float, true, true, OrbInstaller.LightLevelDefault),
        }, own);
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8 + 1 + 8 + 8, parameters.CalcTotalCost(), "17 synced bits more");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(placed).Single(f => f.kind == "FullController").feature, OrbInstaller.LightColor));
        var orbMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(shared + "/Menu.asset").controls.Single().subMenu;
        var lightMenu = orbMenu.controls.Single(c => c.name == "Light").subMenu;
        CollectionAssert.AreEqual(new[] { "Light", "Brightness", "Colour" }, lightMenu.controls.Select(c => c.name).ToArray());
        Assert.AreEqual(OrbInstaller.LightLevel, lightMenu.controls[1].subParameters.Single().name);
        var colours = lightMenu.controls[2].subMenu.controls;
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => (float)i).ToArray(), colours.Select(c => c.value).ToArray());
        Assert.IsTrue(colours.All(c => c.parameter.name == OrbInstaller.LightColor));

        // Played: pink at full brightness, then off; N4N0 is pink while lit, green again after.
        var root = Object.Instantiate(placed);
        SceneManager.MoveGameObjectToScene(root, scene);
        graph = PlayableGraph.Create("N4N0 light test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(shared + "/N4N0.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        var played = root.transform.Find(OrbInstaller.LightObject).GetComponent<Light>();
        var body = root.transform.Find(OrbInstaller.Body).GetComponent<MeshRenderer>();
        Color OrbColour() { var block = new MaterialPropertyBlock(); body.GetPropertyBlock(block); return block.GetColor("_Color"); }
        Tick(5);
        Assert.IsFalse(played.gameObject.activeSelf);
        playable.SetInteger(OrbInstaller.LightColor, 3);
        playable.SetFloat(OrbInstaller.LightLevel, 1);
        playable.SetBool(OrbInstaller.Light, true);
        Tick(20);
        Assert.IsTrue(played.gameObject.activeSelf);
        Assert.AreEqual(OrbInstaller.LightColors[3].color.r, played.color.r, .01f);
        Assert.AreEqual(OrbInstaller.LightColors[3].color.g, played.color.g, .01f);
        Assert.AreEqual(OrbInstaller.LightBright, played.intensity, .01f);
        Assert.IsTrue(OrbColour().r > .9f && OrbColour().g < .6f, "N4N0 is pink: " + OrbColour());
        playable.SetBool(OrbInstaller.Light, false);
        Tick(20);
        Assert.IsFalse(played.gameObject.activeSelf);
        Assert.IsTrue(OrbColour().r < .1f && OrbColour().g > .5f, "Green again: " + OrbColour());

        OrbInstaller.SetOptions(placed.transform, 8, false, false, 72, light: false);
        Assert.IsFalse(OrbInstaller.HasLight(placed.transform));
        Assert.AreEqual((8, false, false, 72, false), OrbInstaller.OptionsOf(placed.transform));
    }

    // A screen from before version 3 (fewer page slices) takes the current files when N4N0's options change, and the
    // controller then says its version: Orbiters - N4N0 shows it the six-tile Home.
    [Test]
    public void AnOlderScreenTakesTheCurrentPagesWithItsVersion()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var renderer = placed.transform.Find(OrbInstaller.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.IsTrue(OrbInstaller.ScreenCurrent(renderer.sharedMaterial, 72), "A new orb's screen is current");
        Assert.AreEqual(OrbInstaller.ScreenRevision, renderer.sharedMaterial.GetTag("OrbScreen", false), "its shader says its files' revision");
        var older = new Material(renderer.sharedMaterial);
        var pages = new Texture2DArray(4, 4, 10, TextureFormat.RGBA32, false);
        owned.Add(older); owned.Add(pages);
        older.SetTexture("_Pages", pages);
        renderer.sharedMaterial = older;
        Assert.IsFalse(OrbInstaller.ScreenCurrent(older, 72));
        OrbInstaller.SetOptions(placed.transform, 8, false, true);
        Assert.IsTrue(OrbInstaller.ScreenCurrent(renderer.sharedMaterial, 72));
        StringAssert.StartsWith(OrbInstaller.ScreenFolder + "/", AssetDatabase.GetAssetPath(renderer.sharedMaterial));
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(OrbInstaller.OptionsFolder + "Text slots 8 + screen in VR/Parameters.asset");
        var version = parameters.parameters.Single(p => p.name == OrbInstaller.ScreenVersionParameter);
        Assert.AreEqual((VRCExpressionParameters.ValueType.Int, false, false, (float)OrbInstaller.ScreenVersion), (version.valueType, version.networkSynced, version.saved, version.defaultValue));
        Assert.AreEqual(5, OrbInstaller.ScreenVersion);
    }

    // The retro look is the screen's material alone: it stays through N4N0's other options, and goes back.
    [Test]
    public void TheRetroLookIsTheScreensMaterialAndStaysThroughOptions()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var renderer = placed.transform.Find(OrbInstaller.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        Assert.IsFalse(OrbInstaller.RetroOf(placed.transform), "Orbiters' own look by default");
        OrbInstaller.SetLook(placed.transform, true);
        Assert.IsTrue(OrbInstaller.RetroOf(placed.transform));
        Assert.IsTrue(OrbInstaller.ScreenCurrent(renderer.sharedMaterial, 72));
        Assert.AreEqual(new Color32(43, 227, 106, 255), (Color32)renderer.sharedMaterials[1].GetColor("_EdgeColor"), "N4N0's beam in the phosphor's green");
        StringAssert.EndsWith("N4N0 screen pages retro.png", AssetDatabase.GetAssetPath(renderer.sharedMaterial.GetTexture("_Pages")), "The retro look's own pages");
        Assert.AreEqual(new Color32(2, 10, 4, 255), (Color32)renderer.sharedMaterial.GetColor("_Card"), "Its phosphor palette");
        Assert.AreEqual(new Color32(43, 227, 106, 255), (Color32)renderer.sharedMaterial.GetColor("_Accent"));
        StringAssert.StartsWith(OrbInstaller.ScreenFolder + "/", AssetDatabase.GetAssetPath(renderer.sharedMaterial));
        OrbInstaller.SetOptions(placed.transform, 8, false, true, 128);
        Assert.IsTrue(OrbInstaller.RetroOf(placed.transform), "another thumbnail size keeps the look");
        Assert.IsTrue(OrbInstaller.ScreenCurrent(renderer.sharedMaterial, 128));
        OrbInstaller.SetLook(placed.transform, false);
        Assert.IsFalse(OrbInstaller.RetroOf(placed.transform));
        Assert.IsTrue(OrbInstaller.ScreenCurrent(renderer.sharedMaterial, 128));
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

    // ---- N4N0 on Quest (OrbInstaller.Mobile) ----

    // A placed, fitted N4N0 on an avatar, with the Light option by default: the PC N4N0 then has all that phones remove.
    private (Transform avatar, Transform orb) Placed(bool light = true)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.4f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        if (light) OrbInstaller.SetOptions(orb, 8, false, false, 72, light: true);
        return (avatar, orb);
    }

    // What the SDK builds: a plain copy of the avatar, the platform's N4N0 kept.
    private GameObject Built(Transform avatar, bool mobile)
    {
        var holder = new GameObject("Build copies");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var copy = Object.Instantiate(avatar.gameObject, holder.transform);
        OrbInstaller.KeepPlatformPart(copy, mobile);
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
        var (avatar, orb) = Placed();
        Assert.IsTrue(OrbInstaller.HasMobile(orb) && OrbInstaller.HasLight(orb));
        Assert.IsFalse(orb.Find(OrbInstaller.Mobile).gameObject.activeSelf, "Off in the editor, where the PC N4N0 shows");
        var copy = Built(avatar, mobile: true);
        var built = copy.transform.Find(orb.name);
        Assert.IsNull(built.Find(OrbInstaller.Shown), "The PC N4N0 is gone");
        Assert.IsTrue(built.Find(OrbInstaller.Mobile).gameObject.activeSelf, "The mobile one is on");

        // What VRChat refuses or removes on phones: none of it.
        Assert.IsEmpty(VRC.SDK3.Validation.AvatarValidation.FindIllegalShaders(copy), "Only VRChat's mobile shaders");
        Assert.IsEmpty(copy.GetComponentsInChildren<Light>(true), "No light");
        Assert.IsEmpty(copy.GetComponentsInChildren<Collider>(true), "No collider");
        Assert.IsEmpty(copy.GetComponentsInChildren<IConstraint>(true), "No Unity constraint");
        Assert.IsEmpty(copy.GetComponentsInChildren<Camera>(true));
        Assert.IsEmpty(copy.GetComponentsInChildren<AudioSource>(true));
        // And what costs a mobile rank: one skinned mesh, one material slot, a few hundred triangles, one bone, no PhysBone,
        // no contact that counts.
        var renderer = (SkinnedMeshRenderer)copy.GetComponentsInChildren<Renderer>(true).Single();
        Assert.AreEqual(1, renderer.sharedMaterials.Length, "One material slot");
        StringAssert.StartsWith("VRChat/Mobile/", renderer.sharedMaterial.shader.name);
        Assert.AreEqual(OrbInstaller.MobileShader, renderer.sharedMaterial.shader.name);
        Assert.Less(renderer.sharedMesh.triangles.Length / 3, 400, "Under 400 triangles");
        Assert.AreEqual(1, renderer.bones.Length);
        Assert.IsEmpty(copy.GetComponentsInChildren<VRCPhysBone>(true), "Quest's Excellent rank allows no PhysBone");
        var receivers = copy.GetComponentsInChildren<VRCContactReceiver>(true);
        Assert.AreEqual(OrbInstaller.Touch, receivers.Single().parameter, "Its owner touches it");
        Assert.IsTrue(receivers.Single().localOnly && !receivers.Single().allowOthers, "Local: no contact counts in the rank");
        Assert.IsEmpty(copy.GetComponentsInChildren<VRCContactSender>(true));
        Assert.LessOrEqual(copy.GetComponentsInChildren<VRCConstraintBase>(true).Length, 2, "On the head, turned like the avatar");

        // Its Full Controller: the mobile controller alone; the PC N4N0's parameters, then the mobile version; the same menu.
        var full = built.GetComponents(VrcFury.Component).Single();
        var controller = Entries<AnimatorController>(full, "controllers", "controller").Single();
        Assert.AreEqual(folder + "/N4N0 mobile.controller", AssetDatabase.GetAssetPath(controller));
        var placedFull = orb.GetComponents(VrcFury.Component).Single();
        CollectionAssert.AreEqual(Entries<VRCExpressionParameters>(placedFull, "prms", "parameters"), Entries<VRCExpressionParameters>(full, "prms", "parameters"));
        CollectionAssert.AreEqual(Entries<VRCExpressionsMenu>(placedFull, "menus", "menu"), Entries<VRCExpressionsMenu>(full, "menus", "menu"));
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Content(full), OrbInstaller.MobileVersionParameter), "Orbiters - N4N0 finds it by its name");
        Assert.IsTrue(OrbInstaller.FindAll(copy).Count() == 0, "Nothing of the PC N4N0 left to find");
    }

    [Test]
    public void APcBuildKeepsOnlyThePcN4N0()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, orb) = Placed();
        var copy = Built(avatar, mobile: false);
        var built = copy.transform.Find(orb.name);
        Assert.IsNull(built.Find(OrbInstaller.Mobile), "The mobile N4N0 is gone");
        Assert.IsTrue(built.Find(OrbInstaller.Shown).gameObject.activeSelf);
        Assert.IsTrue(OrbInstaller.HasLight(built) && OrbInstaller.HasScreen(built) && OrbInstaller.HasPointing(built), "The PC N4N0 whole");
        Assert.IsFalse(copy.GetComponentsInChildren<Renderer>(true).Any(r => r.sharedMaterials.Any(m => m.shader.name.StartsWith("VRChat/Mobile/", StringComparison.Ordinal))));
        // As before the mobile N4N0: the PC controller, parameters and menu only.
        var full = built.GetComponents(VrcFury.Component).Single();
        var placedFull = orb.GetComponents(VrcFury.Component).Single();
        var controllers = Entries<AnimatorController>(full, "controllers", "controller");
        Assert.AreEqual(Entries<AnimatorController>(placedFull, "controllers", "controller").First(), controllers.Single(), "The PC controller");
        Assert.IsTrue(controllers.Single().parameters.Any(p => p.name == OrbInstaller.TextPointer));
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
        Assert.AreEqual(1 + 8 + 1 + 8 + 8 * 8 + 1 + 8 + 8, pc.Where(p => p.networkSynced).Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8), "N4N0 with 8 text slots and its light");
        CollectionAssert.AreEqual(Synced(pc), Synced(phone));
        // The PC N4N0's parameters come first and whole; the mobile build adds its version, local.
        CollectionAssert.AreEqual(pc.Select(p => p.name), phone.Take(pc.Count).Select(p => p.name));
        var version = phone.Skip(pc.Count).Single();
        Assert.AreEqual((OrbInstaller.MobileVersionParameter, VRCExpressionParameters.ValueType.Int, false, false, (float)OrbInstaller.MobileVersion),
            (version.name, version.valueType, version.networkSynced, version.saved, version.defaultValue));
    }

    // The mobile N4N0 played (its own controller): a face per state from the faces' picture, blinking, glowing, bobbing.
    [Test]
    public void TheMobileFacesFollowN4N0sState()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var root = PlayMobile(AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder)));
        var renderer = root.transform.Find(OrbInstaller.MobileFace).GetComponent<SkinnedMeshRenderer>();
        var floating = root.transform.Find(OrbInstaller.MobileFloat);
        MaterialPropertyBlock Block() { var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block); return block; }
        void Shows(int cell, string what)
        {
            foreach (var map in new[] { "_MainTex_ST", "_EmissionMap_ST" })
            {
                var st = Block().GetVector(map);
                Assert.Less(Vector2.Distance(Vector2.one / 3, new Vector2(st.x, st.y)), 1e-4f, what + " " + map + " tiling: " + st);
                Assert.Less(Vector2.Distance(OrbInstaller.CellOffset(cell), new Vector2(st.z, st.w)), 1e-4f, what + " " + map + ": " + st);
            }
        }
        var faces = (Texture2D)renderer.sharedMaterial.GetTexture("_MainTex");
        Assert.AreEqual(faces, renderer.sharedMaterial.GetTexture("_EmissionMap"), "The faces lit and glowing");
        Assert.AreEqual((768, 768), (faces.width, faces.height), "3 × 3 cells of 256²");
        Assert.Greater(faces.mipmapCount, 1);
        Assert.AreEqual(folder + "/N4N0 faces.png", AssetDatabase.GetAssetPath(faces), "In N4N0's folder, which a package carries");

        Tick(3);
        Shows((int)OrbState.Idle, "Idle");
        float idle = Block().GetFloat("_EmissionStrength");
        for (int s = 1; s < OrbInstaller.Looks.Length; s++)
        {
            playable.SetInteger(OrbInstaller.State, s);
            Tick(2);
            Shows(s, ((OrbState)s).ToString() + ", at once");
        }
        playable.SetInteger(OrbInstaller.State, 42);
        Tick(2);
        Shows((int)OrbState.Idle, "Any other value shows as Idle");

        // Idle blinks: 3.1 s in, its eyes shut for a moment.
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Happy);
        Tick(2);
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Idle);
        Tick(1);
        Tick(157);
        Shows(OrbInstaller.BlinkCell, "Blinking");
        Tick(10);
        Shows((int)OrbState.Idle, "Open again");

        // Asleep it glows dimmer; listening, brighter with its owner's voice; surprised, it jumps.
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Sleeping);
        Tick(2);
        Assert.Less(Block().GetFloat("_EmissionStrength"), idle * .5f);
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Listening);
        Tick(2);
        float quiet = Block().GetFloat("_EmissionStrength");
        playable.SetFloat("Voice", 1);
        Tick(2);
        Assert.Greater(Block().GetFloat("_EmissionStrength"), quiet * 1.4f, "Glowing up with its owner's voice");
        playable.SetFloat("Voice", 0);
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Surprised);
        float highest = 0;
        for (int i = 0; i < 30; i++) { Tick(1); highest = Mathf.Max(highest, floating.localPosition.y); }
        Assert.Greater(highest, .03f, "A short jump");
        Tick(100);
        Assert.Less(Mathf.Abs(floating.localPosition.y), .015f, "and back to bobbing");

        // N4N0/Visible hides it; its owner sees it look at them, everyone else sees it look ahead.
        var face = root.transform.Find(OrbInstaller.MobileFace);
        Assert.Less(Quaternion.Angle(Quaternion.identity, face.localRotation), .01f, "Ahead for everyone else");
        playable.SetBool("IsLocal", true);
        Tick(2);
        Assert.Less(Quaternion.Angle(OrbInstaller.OwnerFacing, face.localRotation), .01f, "At its owner on its owner's client");
        playable.SetBool(OrbInstaller.Visible, false);
        Tick(2);
        Assert.IsFalse(root.transform.Find(OrbInstaller.MobileHead).gameObject.activeSelf);
        playable.SetBool(OrbInstaller.Visible, true);
        Tick(2);
        Assert.IsTrue(root.transform.Find(OrbInstaller.MobileHead).gameObject.activeSelf);
    }

    // The mobile N4N0 takes the Light option's colour, brighter with its brightness, red while annoyed, green while off.
    [Test]
    public void TheMobileTintFollowsTheLightsColour()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var root = PlayMobile(AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder)));
        var renderer = root.transform.Find(OrbInstaller.MobileFace).GetComponent<SkinnedMeshRenderer>();
        Color Get(string property) { var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block); return block.GetColor(property); }
        Tick(5);
        foreach (var property in new[] { "_EmissionColor", "_Color", "_RimColor" })
            Assert.IsTrue(Get(property).g > Get(property).r && Get(property).g > Get(property).b, "N4N0's green: " + property + " " + Get(property));
        Assert.IsTrue(Get("_EmissionColor").g > Get("_EmissionColor").r + .3f, "Its glow fully green: " + Get("_EmissionColor"));

        playable.SetInteger(OrbInstaller.LightColor, 3);
        playable.SetFloat(OrbInstaller.LightLevel, 1);
        playable.SetBool(OrbInstaller.Light, true);
        Tick(20);
        var bright = Get("_EmissionColor");
        Assert.IsTrue(bright.r > bright.g + .2f && bright.b > bright.g, "Pink: " + bright);
        Assert.IsTrue(Get("_RimColor").r > Get("_RimColor").g, "Its rim too");
        playable.SetFloat(OrbInstaller.LightLevel, 0);
        Tick(3);
        Assert.Less(Get("_EmissionColor").b, bright.b * .8f, "Dimmer at a low brightness");
        playable.SetInteger(OrbInstaller.LightColor, 5);
        Tick(20);
        Assert.IsTrue(Get("_EmissionColor").b > Get("_EmissionColor").r, "Blue: " + Get("_EmissionColor"));

        playable.SetFloat(OrbInstaller.LightLevel, 1);
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Annoyed);
        Tick(20);
        Assert.IsTrue(Get("_EmissionColor").r > .5f && Get("_EmissionColor").r > Get("_EmissionColor").b * 1.5f, "Red while annoyed: " + Get("_EmissionColor"));
        playable.SetInteger(OrbInstaller.State, (int)OrbState.Idle);
        playable.SetBool(OrbInstaller.Light, false);
        Tick(20);
        Assert.IsTrue(Get("_EmissionColor").g > Get("_EmissionColor").r + .2f, "Green again: " + Get("_EmissionColor"));
    }

    [Test]
    public void TheMobileN4N0FitsTheAvatarBesideThePcOne()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var (avatar, head) = Humanoid(eyes: 1.2f);
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        var frame = orb.Find(OrbInstaller.MobileHead);
        Assert.AreEqual(head, frame.GetComponent<VRCPositionConstraint>().Sources[0].SourceTransform, "On the head bone");
        Assert.AreEqual(avatar, frame.GetComponent<VRCRotationConstraint>().Sources[0].SourceTransform, "Turned like the avatar");
        Assert.Less(Vector3.Distance(head.position, frame.position), 1e-4f);
        Assert.AreEqual(1.2f / OrbInstaller.ReferenceEyes, frame.lossyScale.x, 1e-4f, "As large as the avatar's eyes are high");
        var mobile = orb.Find(OrbInstaller.MobileOrb);
        Assert.Less(Vector3.Distance(orb.Find(OrbInstaller.Tether + "/1/2").position, mobile.position), 1e-4f, "Where the PC N4N0 rests");
        var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
        Assert.Less(Vector3.Angle(frame.rotation * OrbInstaller.OwnerFacing * Vector3.forward, eyes - mobile.position), .5f, "Turned to its owner, it looks at their eyes");
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(orb.gameObject, avatar).Kind, "Still attached by its own constraints");

        // The build step runs on every build before VRCFury and the Toolkit's attachments, hidden builds too, and never
        // changes a prefab instance (only build copies).
        var hook = new OrbPlatformHook();
        Assert.Less(hook.callbackOrder, -10110);
        var field = typeof(VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.IsTrue(((IEnumerable<VRC.SDKBase.Editor.BuildPipeline.IVRCSDKPreprocessAvatarCallback>)field.GetValue(null)).Any(c => c is OrbPlatformHook), "Registered with the SDK");
        Assert.IsFalse(HiddenAvatarBuild.LeftOut(typeof(OrbPlatformHook)), "My Avatar's checks see the platform's N4N0");
        OrbInstaller.KeepPlatformPart(avatar.gameObject, mobile: true);
        Assert.IsNotNull(orb.Find(OrbInstaller.Shown));
        Assert.IsFalse(orb.Find(OrbInstaller.Mobile).gameObject.activeSelf);
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
        root.transform.Find(OrbInstaller.Mobile).gameObject.SetActive(true);
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
