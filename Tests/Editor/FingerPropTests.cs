using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.PhysBone.Components;

// The props worn on the right index finger: Rainbowo and the orbit orb, built whole into a temporary folder.
public class FingerPropTests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;
    private PlayableGraph graph;

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersFingerPropTest-" + System.Guid.NewGuid().ToString("N");
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
    public void RainbowoDrawsWhilePointingAndLeavesRainbowsInTheWorldOnRequest()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RainbowoInstaller.CreatePrefab(folder));
        Standalone(prefab);
        Assert.AreEqual(prefab.transform, RainbowoInstaller.FindAll(prefab).Single(), "Found by its trail");
        var (root, playable) = Play(prefab, folder + "/Rainbowo.controller");
        var trail = root.transform.Find(RainbowoInstaller.Rainbow).GetComponent<TrailRenderer>();
        var sparkles = root.transform.Find(RainbowoInstaller.Sparkles).GetComponent<ParticleSystem>();
        int wipes = 0;
        void Tick() { for (int i = 0; i < 6; i++) { graph.Evaluate(.02f); if (trail.time == 0 && playable.GetBool(RainbowoInstaller.Enabled)) wipes++; } }

        Tick(); Assert.IsFalse(trail.emitting); Assert.AreEqual(0, trail.time);
        playable.SetBool(RainbowoInstaller.Enabled, true); Tick();
        Assert.IsFalse(trail.emitting); Assert.AreEqual(RainbowoInstaller.Lasting, trail.time);
        playable.SetInteger("GestureRight", 1); Tick(); Assert.IsFalse(trail.emitting, "Only pointing draws");
        wipes = 0;
        playable.SetInteger("GestureRight", RainbowoInstaller.Point); Tick();
        Assert.IsTrue(trail.emitting); Assert.IsTrue(sparkles.emission.enabled); Assert.AreEqual(RainbowoInstaller.Lasting, trail.time);
        Assert.Greater(wipes, 0, "A new rainbow replaces the last one");
        playable.SetInteger("GestureRight", 0); Tick();
        Assert.IsFalse(trail.emitting); Assert.IsFalse(sparkles.emission.enabled); Assert.AreEqual(RainbowoInstaller.Lasting, trail.time, "The rainbow stays");

        playable.SetBool(RainbowoInstaller.Keep, true); wipes = 0;
        playable.SetInteger("GestureRight", RainbowoInstaller.Point); Tick();
        Assert.IsTrue(trail.emitting); Assert.AreEqual(0, wipes, "Leave in world keeps every rainbow");
        playable.SetBool(RainbowoInstaller.Enabled, false); Tick();
        Assert.IsFalse(trail.emitting); Assert.AreEqual(0, trail.time, "Turning it off clears them");

        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.AreEqual(2, parameters.CalcTotalCost());
    }

    [Test]
    public void TheOrbitOrbCirclesOnItsTetherAndLightsOnlyOnRequest()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbitOrbInstaller.CreatePrefab(folder));
        Standalone(prefab);
        Assert.AreEqual(prefab.transform, OrbitOrbInstaller.FindAll(prefab).Single(), "Found by its tether");
        var tether = prefab.transform.Find(OrbitOrbInstaller.Tether).GetComponent<VRCPhysBone>();
        Assert.IsTrue(prefab.transform.Find(OrbitOrbInstaller.Ball).Cast<Transform>().All(tether.ignoreTransforms.Contains), "What the orb shows is no part of the chain");
        Assert.AreEqual(LightShadows.None, prefab.GetComponentInChildren<Light>(true).shadows);
        var (root, playable) = Play(prefab, folder + "/Orbit orb.controller");
        var orbit = root.transform.Find(OrbitOrbInstaller.Orbit);
        var light = root.transform.Find(OrbitOrbInstaller.Lamp).gameObject;
        void Tick(float seconds) { for (float t = 0; t < seconds; t += .02f) graph.Evaluate(.02f); }

        Tick(.2f); Assert.IsFalse(orbit.gameObject.activeSelf); Assert.IsFalse(light.activeSelf);
        playable.SetBool(OrbitOrbInstaller.Enabled, true); Tick(.1f);
        Assert.IsTrue(orbit.gameObject.activeSelf); Assert.IsFalse(light.activeSelf, "No light until asked");
        float before = orbit.localEulerAngles.y; Tick(OrbitOrbInstaller.OrbitSeconds / 4);
        Assert.AreEqual(90, Mathf.DeltaAngle(before, orbit.localEulerAngles.y), 6, "A quarter turn in a quarter of the orbit");
        playable.SetBool(OrbitOrbInstaller.Lit, true); Tick(.1f); Assert.IsTrue(light.activeSelf);
        playable.SetBool(OrbitOrbInstaller.Enabled, false); Tick(.1f); Assert.IsFalse(orbit.gameObject.activeSelf, "Hidden with its light");

        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        Assert.AreEqual(2, parameters.CalcTotalCost());
    }

    // Toolkit releases ship without .meta files: a gallery copy refers to nothing of this project's Toolkit, and its package
    // declares the Toolkit that fits it.
    private static void Standalone(GameObject prefab)
    {
        Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Any(p => p.StartsWith("Packages/orbiters.", System.StringComparison.Ordinal)));
        Assert.IsTrue(prefab.GetComponentsInChildren<MonoBehaviour>(true).All(c => !c.GetType().Assembly.GetName().Name.StartsWith("Orbiters")));
        Assert.IsTrue(AttachmentHooks.Fits(prefab));
    }

    private (GameObject root, AnimatorControllerPlayable playable) Play(GameObject prefab, string controllerPath)
    {
        var root = Object.Instantiate(prefab);
        SceneManager.MoveGameObjectToScene(root, scene);
        graph = PlayableGraph.Create("Finger prop test");
        var playable = AnimatorControllerPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath));
        AnimationPlayableOutput.Create(graph, "Prop", root.AddComponent<Animator>()).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
        return (root, playable);
    }
}
