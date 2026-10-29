using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

// N4N0's screen mirror (contracts Docs/orb.md, Screen mirror): the camera on the owner's line of sight, its render texture,
// the layers that render it every other frame on its owner's client and freeze it in the world, and its parameters.
public class OrbMirrorTests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;
    private PlayableGraph graph;
    private AnimatorControllerPlayable playable;

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersOrbMirrorTest-" + Guid.NewGuid().ToString("N");
        scene = EditorSceneManager.NewPreviewScene();
    }

    [TearDown]
    public void TearDown()
    {
        if (graph.IsValid()) graph.Destroy();
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        sandbox.End();
    }

    [Test]
    public void N4N0CarriesTheMirrorsCameraOnItsOwnersLineOfSight()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var root = prefab.transform;
        Assert.IsTrue(OrbInstaller.HasMirror(root));
        var mirror = root.Find(OrbInstaller.Mirror);
        Assert.AreEqual(root.Find(OrbInstaller.Shown), mirror.parent, "hidden with N4N0, and gone with the PC N4N0 on phones");
        var hold = mirror.GetComponent<VRCParentConstraint>();
        Assert.AreEqual(1, hold.Sources.Count);
        Assert.AreEqual(root.Find(OrbInstaller.Gaze), hold.Sources[0].SourceTransform, "the eyes and their line of sight, N4N0's own frame: nothing for Bind to set");
        Assert.IsTrue(hold.IsActive && !hold.FreezeToWorld && !hold.RebakeOffsetsWhenUnfrozen);
        var camera = root.Find(OrbInstaller.MirrorCamera).GetComponent<Camera>();
        Assert.IsFalse(camera.enabled, "off until the mirror layer turns it on");
        Assert.AreEqual(OrbInstaller.MirrorFov(0), camera.fieldOfView, 1e-4f);
        Assert.AreEqual(40f, OrbInstaller.MirrorFov(0), 1e-3f);
        Assert.AreEqual(4f, OrbInstaller.MirrorFov(1), 1e-3f);
        Assert.Greater(OrbInstaller.MirrorFov(.5f), 4f);
        Assert.Less(OrbInstaller.MirrorFov(.5f), 22f, "even in the picture's scale, not in degrees");
        foreach (int hidden in new[] { 5, 9, 10, 12, 18, 19 }) Assert.AreEqual(0, camera.cullingMask & (1 << hidden), "layer " + hidden + " left out");
        foreach (int shown in new[] { 0, 4, 11, 17 }) Assert.AreNotEqual(0, camera.cullingMask & (1 << shown), "layer " + shown + " drawn");
        Assert.IsFalse(camera.allowHDR || camera.allowMSAA);
        var texture = camera.targetTexture;
        Assert.AreEqual((384, 216, 1), (texture.width, texture.height, texture.antiAliasing));
        Assert.IsTrue(texture.sRGB);
        Assert.AreEqual(folder + "/N4N0 mirror.renderTexture", AssetDatabase.GetAssetPath(texture), "in N4N0's folder, which its gallery package carries");

        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (var (name, type) in new[]
                 {
                     (OrbInstaller.MirrorOn, VRCExpressionParameters.ValueType.Bool), (OrbInstaller.MirrorPlaced, VRCExpressionParameters.ValueType.Bool),
                     (OrbInstaller.MirrorZoom, VRCExpressionParameters.ValueType.Float),
                 })
        {
            var p = parameters.parameters.Single(q => q.name == name);
            Assert.IsFalse(p.networkSynced || p.saved, name + ": local and never saved");
            Assert.AreEqual(type, p.valueType, name);
            Assert.IsTrue(VrcFury.IsGlobalParameter(full, name), name + " keeps its name for OSC");
        }
    }

    [Test]
    public void ItRendersEveryOtherFrameOnItsOwnersClientAndFreezesInTheWorld()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var root = Play(prefab).transform;
        var camera = root.Find(OrbInstaller.MirrorCamera).GetComponent<Camera>();
        var hold = root.Find(OrbInstaller.Mirror).GetComponent<VRCParentConstraint>();
        var rendered = new List<bool>();
        void Run(int updates)
        {
            rendered.Clear();
            for (int i = 0; i < updates; i++)
            {
                graph.Evaluate(.011f);
                rendered.Add(camera.enabled);
            }
        }

        // Someone else's copy: never, whatever the parameters say.
        playable.SetBool("IsLocal", false);
        playable.SetBool(OrbInstaller.MirrorOn, true);
        Run(8);
        Assert.IsFalse(rendered.Any(r => r), "other players' clients never render it");

        // The owner's: every other frame while on, at the zoom asked.
        playable.SetBool("IsLocal", true);
        playable.SetFloat(OrbInstaller.MirrorZoom, .5f);
        Run(12);
        var tail = rendered.Skip(4).ToList();
        Assert.AreEqual(4, tail.Count(r => r), string.Join(",", rendered));
        for (int i = 1; i < tail.Count; i++) Assert.AreNotEqual(tail[i - 1], tail[i], "one frame on, one off: " + string.Join(",", rendered));
        Assert.AreEqual(OrbInstaller.MirrorFov(.5f), camera.fieldOfView, .05f);
        playable.SetFloat(OrbInstaller.MirrorZoom, 1);
        Run(4);
        Assert.AreEqual(4f, camera.fieldOfView, .05f);

        Assert.IsFalse(hold.FreezeToWorld, "following the eyes until placed");
        playable.SetBool(OrbInstaller.MirrorPlaced, true);
        Run(3);
        Assert.IsTrue(hold.FreezeToWorld, "left where it was in the world");
        playable.SetBool(OrbInstaller.MirrorOn, false);
        Run(4);
        Assert.IsFalse(rendered.Skip(1).Any(r => r), "off with the page");
        Assert.IsTrue(hold.FreezeToWorld, "still where it was: back on the page, the picture comes back at once");
        playable.SetBool(OrbInstaller.MirrorPlaced, false);
        Run(3);
        Assert.IsFalse(hold.FreezeToWorld);

        // Every state sets what its layer animates: the camera, its field of view and the screen's switch.
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        var cameraLayer = controller.layers.Single(l => l.name == "Mirror camera").stateMachine;
        foreach (var state in cameraLayer.states.Select(s => s.state))
        {
            var clips = state.motion is BlendTree tree ? tree.children.Select(c => (AnimationClip)c.motion) : new[] { (AnimationClip)state.motion };
            foreach (var clip in clips)
                CollectionAssert.AreEquivalent(new[] { "m_Enabled", "field of view", "material._MirrorLive" }, AnimationUtility.GetCurveBindings(clip).Select(b => b.propertyName), state.name);
        }
    }

    [Test]
    public void APhoneBuildHasNoMirrorAndTheHookRunsBeforeVRCFury()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var copy = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(copy, scene);
        OrbInstaller.KeepPlatformPart(copy, mobile: true);
        Assert.IsNull(copy.transform.Find(OrbInstaller.Mirror), "phones have no avatar cameras");
        int order = new OrbMirrorHook().callbackOrder;
        Assert.Greater(order, -10200, "after the platform's N4N0 is kept");
        Assert.Less(order, -10110, "before the refit and attachment steps and VRCFury");
    }

    [Test]
    public void TheBuildPointsTheCameraAtWhatTheScreenShows()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var copy = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(copy, scene);
        var renderer = OrbInstaller.ScreenRenderer(copy.transform);
        var material = renderer.sharedMaterial;
        OrbInstaller.BindMirrors(copy);
        if (!material.HasProperty("_Mirror"))
        {
            Assert.IsNull(copy.transform.Find(OrbInstaller.Mirror), "a screen without the mirror leaves the camera out");
            return;
        }
        var camera = copy.transform.Find(OrbInstaller.MirrorCamera).GetComponent<Camera>();
        Assert.AreSame(material.GetTexture("_Mirror"), camera.targetTexture);
        // A screen given another folder's material (N4N0's options): the camera follows it.
        var other = new RenderTexture(16, 9, 0);
        var swapped = new Material(material);
        swapped.SetTexture("_Mirror", other);
        renderer.sharedMaterial = swapped;
        OrbInstaller.BindMirrors(copy);
        Assert.AreSame(other, camera.targetTexture);
        Object.DestroyImmediate(swapped);
        Object.DestroyImmediate(other);
    }

    private GameObject Play(GameObject prefab)
    {
        var root = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(root, scene);
        graph = PlayableGraph.Create("N4N0 mirror test");
        playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller"));
        AnimationPlayableOutput.Create(graph, "N4N0", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        return root;
    }
}
