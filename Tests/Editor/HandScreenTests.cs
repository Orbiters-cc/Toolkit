using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using Unity.Jobs;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

public class HandScreenTests
{
    private TestUndoSandbox sandbox;

    [SetUp]
    public void SetUp() => sandbox = TestUndoSandbox.Begin();

    // After each test's own cleanup: reverts the Undo steps the controller and VRCFury APIs record.
    [TearDown]
    public void TearDown() => sandbox.End();

    [Test]
    public void OneHandCarriesItBothHandsStretchItBetweenTheHandles()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var folder = "Assets/__OrbitersHandScreenTest-" + Guid.NewGuid().ToString("N");
        // The fixture lives in a preview scene, never in the user's open scene.
        var scene = EditorSceneManager.NewPreviewScene();
        var graph = default(PlayableGraph);
        var solver = new ConstraintSolver();
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandScreenInstaller.CreatePrefab(folder));
            var root = Object.Instantiate(prefab);
            SceneManager.MoveGameObjectToScene(root, scene);
            var frame = root.transform.Find(HandScreenInstaller.Frame).gameObject;
            var corners = HandScreenInstaller.Corners.Select(p => root.transform.Find(p)).ToArray();
            var grabs = root.GetComponentsInChildren<VRCPhysBone>(true);
            // Stand-ins for the avatar's wrists, where Bind would put them.
            var leftWrist = new GameObject("Left wrist").transform; leftWrist.SetParent(root.transform, false);
            var rightWrist = new GameObject("Right wrist").transform; rightWrist.SetParent(root.transform, false);
            leftWrist.localPosition = new Vector3(-.35f, 1.45f, .45f); rightWrist.localPosition = new Vector3(.35f, 1.45f, .45f);
            foreach (string handle in new[] { HandScreenInstaller.LeftHandle, HandScreenInstaller.RightHandle })
            {
                var follow = root.transform.Find(handle).GetComponent<VRCParentConstraint>();
                follow.Sources[1] = new VRCConstraintSource(leftWrist, 0); follow.Sources[2] = new VRCConstraintSource(rightWrist, 0);
            }
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Hand screen.controller");

            graph = PlayableGraph.Create("Hand screen test");
            var animator = root.AddComponent<Animator>();
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, "Hand screen", animator); output.SetSourcePlayable(playable);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
            solver.Track(root);
            var current = new int[controller.layers.Length];
            var visited = new HashSet<string>();
            // Each update as in VRChat: the animator, the SDK drivers it entered (applied by the fixture, on the owner
            // only), then the constraints.
            void Tick(int frames = 6)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    graph.Evaluate(.02f);
                    for (int layer = 0; layer < current.Length; layer++)
                    {
                        int state = playable.GetCurrentAnimatorStateInfo(layer).shortNameHash;
                        if (state == current[layer]) continue;
                        current[layer] = state;
                        var active = controller.layers[layer].stateMachine.states.Single(s => Animator.StringToHash(s.state.name) == state).state;
                        visited.Add(active.name);
                        foreach (var driver in active.behaviours.OfType<VRCAvatarParameterDriver>())
                        {
                            Assert.IsTrue(driver.localOnly);
                            if (playable.GetBool("IsLocal")) foreach (var set in driver.parameters) playable.SetBool(set.name, set.value > .5f);
                        }
                    }
                    solver.Solve();
                }
            }
            float Width() => Vector3.Distance(corners[0].position, corners[1].position);
            float Height() => Vector3.Distance(corners[0].position, corners[2].position);
            Vector3 Middle() => (corners[0].position + corners[3].position) / 2;
            void Grab(string handle, bool on) => playable.SetBool(handle + "_IsGrabbed", on);
            void Near(Vector3 expected, Vector3 actual, string message) => Assert.Less(Vector3.Distance(expected, actual), .005f, message);

            playable.SetBool("IsLocal", true);
            Tick(); Assert.IsFalse(frame.activeSelf);
            playable.SetBool(HandScreenInstaller.Enabled, true); Tick();
            Assert.IsTrue(frame.activeSelf); Assert.IsTrue(grabs.All(g => g.enabled));
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, Width(), .005f);
            Assert.AreEqual(Width() / HandScreenInstaller.Aspect, Height(), .005f, "16:9");
            var home = Middle();

            // The left hand takes the left handle: both handles follow it, and the screen keeps its size.
            Grab(HandScreenInstaller.GrabLeftHandle, true); playable.SetBool(HandScreenInstaller.LeftAtLeft, true);
            playable.SetInteger("GestureLeft", 1); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft)); Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldRight));
            Grab(HandScreenInstaller.GrabLeftHandle, false); playable.SetBool(HandScreenInstaller.LeftAtLeft, false); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft), "The native grab ending does not drop it");
            leftWrist.position += new Vector3(.1f, .2f, .3f); Tick();
            Near(home + new Vector3(.1f, .2f, .3f), Middle(), "Carried where it was grabbed, without a jump");
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, Width(), .005f);
            leftWrist.rotation = Quaternion.Euler(0, 40, 0); Tick();
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, Width(), .005f, "Turning the hand turns the screen whole");
            leftWrist.rotation = Quaternion.identity; Tick();

            // The right hand takes the right handle, then the hands move 40 cm apart: 40 cm wider, still 16:9.
            Grab(HandScreenInstaller.GrabRightHandle, true); playable.SetBool(HandScreenInstaller.RightAtRight, true);
            playable.SetInteger("GestureRight", 1); visited.Clear(); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldRight)); Assert.IsFalse(playable.GetBool(HandScreenInstaller.Crossed));
            Assert.IsTrue(visited.Contains("Both hands catch"));
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, Width(), .005f, "Taking the second handle changes nothing");
            rightWrist.position += Vector3.right * .4f; Tick();
            Assert.AreEqual(HandScreenInstaller.DefaultWidth + .4f, Width(), .01f);
            Assert.AreEqual(Width() / HandScreenInstaller.Aspect, Height(), .01f);
            rightWrist.position += Vector3.left * .7f; Tick();
            Assert.AreEqual(HandScreenInstaller.DefaultWidth - .3f, Width(), .01f, "Closer hands make it smaller");
            float stretched = Width();

            // Relaxing the right hand lets go of it at once; the left carries the stretched screen.
            playable.SetInteger("GestureRight", 0); Grab(HandScreenInstaller.GrabRightHandle, false); Tick();
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldRight)); Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft));
            rightWrist.position += Vector3.right; leftWrist.position += Vector3.up * .1f; Tick();
            Assert.AreEqual(stretched, Width(), .005f, "One hand never resizes it");
            playable.SetInteger("GestureLeft", 2); Tick();
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldLeft));
            var dropped = Middle();
            leftWrist.position += Vector3.forward; Tick();
            Near(dropped, Middle(), "Let go, it stays where it is"); Assert.AreEqual(stretched, Width(), .005f);

            // The left hand on the right handle, then the right hand on the left one: crossed, still nothing jumps.
            Grab(HandScreenInstaller.GrabRightHandle, true); playable.SetBool(HandScreenInstaller.LeftAtRight, true); playable.SetInteger("GestureLeft", 1); Tick();
            Grab(HandScreenInstaller.GrabRightHandle, false); playable.SetBool(HandScreenInstaller.LeftAtRight, false); Tick();
            Grab(HandScreenInstaller.GrabLeftHandle, true); playable.SetBool(HandScreenInstaller.RightAtLeft, true); playable.SetInteger("GestureRight", 1); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.Crossed));
            Assert.AreEqual(stretched, Width(), .005f);
            // Drop in the menu lets go of both.
            playable.SetBool(HandScreenInstaller.Drop, true); Tick();
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldLeft) || playable.GetBool(HandScreenInstaller.HeldRight));
            playable.SetBool(HandScreenInstaller.Drop, false); Grab(HandScreenInstaller.GrabLeftHandle, false);
            playable.SetBool(HandScreenInstaller.RightAtLeft, false); playable.SetInteger("GestureLeft", 0); playable.SetInteger("GestureRight", 0); Tick();

            // Grip alone on Touch (pointing, 3) keeps the first hand's native grab until it lets go: the other hand still
            // takes the other handle, and only that one.
            Grab(HandScreenInstaller.GrabLeftHandle, true); playable.SetBool(HandScreenInstaller.LeftAtLeft, true); playable.SetInteger("GestureLeft", 3); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft)); Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldRight));
            playable.SetBool(HandScreenInstaller.RightAtLeft, true); playable.SetInteger("GestureRight", 3); Tick();
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldRight), "The first hand's grab is not the second hand's");
            playable.SetBool(HandScreenInstaller.RightAtLeft, false); Grab(HandScreenInstaller.GrabRightHandle, true); playable.SetBool(HandScreenInstaller.RightAtRight, true); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft) && playable.GetBool(HandScreenInstaller.HeldRight), "Both hands while the first grab still holds");
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.Crossed));
            playable.SetInteger("GestureRight", 0); Grab(HandScreenInstaller.GrabRightHandle, false); Grab(HandScreenInstaller.GrabLeftHandle, false); Tick();
            Assert.IsTrue(playable.GetBool(HandScreenInstaller.HeldLeft)); Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldRight));
            playable.SetInteger("GestureLeft", 0); Tick();
            Assert.IsFalse(playable.GetBool(HandScreenInstaller.HeldLeft));
            playable.SetBool(HandScreenInstaller.LeftAtLeft, false); playable.SetBool(HandScreenInstaller.RightAtRight, false);

            // A remote client follows the owner's synced hands.
            playable.SetBool("IsLocal", false); playable.SetBool(HandScreenInstaller.HeldLeft, true); playable.SetBool(HandScreenInstaller.HeldRight, true); Tick();
            float before = Width();
            leftWrist.position += Vector3.left * .2f; Tick();
            Assert.AreEqual(before + .2f, Width(), .02f);

            // Turned off and on, it is back in front of its owner at its first size.
            playable.SetBool(HandScreenInstaller.Enabled, false); Tick(); Assert.IsFalse(frame.activeSelf);
            playable.SetBool(HandScreenInstaller.HeldLeft, false); playable.SetBool(HandScreenInstaller.HeldRight, false);
            playable.SetBool(HandScreenInstaller.Enabled, true); Tick();
            Near(home, Middle(), "Back home"); Assert.AreEqual(HandScreenInstaller.DefaultWidth, Width(), .005f);

            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            Assert.AreEqual(4, parameters.CalcTotalCost(), "Shown, held left, held right, crossed");
        }
        finally
        {
            solver.Release();
            if (graph.IsValid()) graph.Destroy();
            EditorSceneManager.ClosePreviewScene(scene); AssetDatabase.DeleteAsset(folder);
        }
    }

    [Test]
    public void ThePrefabShowsTheWorldsVideoAndOnlyItsOwnerMovesIt()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var folder = "Assets/__OrbitersHandScreenTest-" + Guid.NewGuid().ToString("N");
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandScreenInstaller.CreatePrefab(folder));
            var picture = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterials[0];
            Assert.AreEqual("Orbiters/Hand Screen", picture.shader.name);
            Assert.IsFalse(picture.HasProperty("_Udon_VideoTex"), "A material value would hide the world's global video texture");
            // Toolkit releases ship without .meta files: a gallery copy must not refer to the Toolkit's assets or scripts.
            Assert.AreEqual(folder + "/Hand screen.shader", AssetDatabase.GetAssetPath(picture.shader));
            Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Any(p => p.StartsWith("Packages/orbiters.", StringComparison.Ordinal)));
            Assert.IsTrue(prefab.GetComponentsInChildren<MonoBehaviour>(true).All(c => !c.GetType().Assembly.GetName().Name.StartsWith("Orbiters")));
            Assert.AreEqual(prefab.transform, HandScreenInstaller.FindAll(prefab).Single(), "Found by its left handle's grab");
            var grabs = prefab.GetComponentsInChildren<VRCPhysBone>(true);
            Assert.AreEqual(2, grabs.Length, "A handle on each side");
            foreach (var phys in grabs)
            {
                Assert.AreEqual(VRCPhysBoneBase.AdvancedBool.Other, phys.allowGrabbing);
                Assert.IsTrue(phys.grabFilter.allowSelf); Assert.IsFalse(phys.grabFilter.allowOthers, "Only the owner moves it");
                Assert.IsFalse(phys.enabled);
            }
            Assert.IsTrue(prefab.GetComponentsInChildren<VRCContactReceiver>(true).All(r => r.localOnly && !r.allowOthers));
            Assert.IsFalse(prefab.transform.Find(HandScreenInstaller.Frame).gameObject.activeSelf, "Hidden until the menu shows it");
        }
        finally { AssetDatabase.DeleteAsset(folder); }
    }

    /// <summary>
    /// Runs the VRChat SDK's own constraint solver on objects of a preview scene (it only picks up the open scenes by
    /// itself), one update per <see cref="Solve"/>.
    /// </summary>
    private sealed class ConstraintSolver
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Manager = typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
        private VRCConstraintBase[] constraints = new VRCConstraintBase[0];

        public void Track(GameObject root)
        {
            constraints = root.GetComponentsInChildren<VRCConstraintBase>(true);
            foreach (var constraint in constraints) Manager.GetMethod("RegisterConstraint", Static).Invoke(null, new object[] { constraint });
        }

        public void Solve()
        {
            // The SDK unregisters a constraint whose object is turned off; turned on again, it is back (as in a scene).
            foreach (var constraint in constraints)
                if (constraint.isActiveAndEnabled && !(bool)Manager.GetMethod("IsConstraintRegistered", Static).Invoke(null, new object[] { constraint }))
                    Manager.GetMethod("RegisterConstraint", Static).Invoke(null, new object[] { constraint });
            Manager.GetMethod("Sdk_ManuallyRefreshGroups", Static).Invoke(null, new object[] { constraints.Where(c => c.isActiveAndEnabled).ToArray() });
            Manager.GetMethod("UpdateConstraints", Static).Invoke(null, null);
            var schedule = Manager.GetMethod("ScheduleExecutionJobs", Static);
            var stage = schedule.GetParameters()[0].ParameterType;
            foreach (string name in new[] { "PrePhysBone", "PostPhysBone", "PostLocalAvatarProcess" })
                ((JobHandle)schedule.Invoke(null, new[] { Enum.Parse(stage, name), (object)default(JobHandle) })).Complete();
            Manager.GetMethod("PostUpdateConstraints", Static).Invoke(null, null);
        }

        public void Release()
        {
            foreach (var constraint in constraints)
                if (constraint != null) Manager.GetMethod("UnregisterConstraint", Static).Invoke(null, new object[] { constraint });
            constraints = new VRCConstraintBase[0];
        }
    }
}
