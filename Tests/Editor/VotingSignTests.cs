using System.IO;
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
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

public class VotingSignTests
{
    private TestUndoSandbox sandbox;

    [SetUp]
    public void SetUp() => sandbox = TestUndoSandbox.Begin();

    [TearDown]
    public void TearDown() => sandbox.End();

    [Test]
    public void TheMenuShowsEachScoreAndTheSignIsHeldLikeThePen()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var folder = "Assets/__OrbitersSignTest-" + System.Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder); AssetDatabase.ImportAsset(folder);
        var scene = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("Sign controller fixture");
        SceneManager.MoveGameObjectToScene(root, scene);
        var graph = default(PlayableGraph);
        try
        {
            graph = PlayableGraph.Create("Sign controller test");
            var sign = Child(root, "Sign"); var follow = sign.AddComponent<VRCParentConstraint>();
            for (int i = 0; i < 3; i++) follow.Sources.Add(new VRC.Dynamics.VRCConstraintSource(root.transform, i == 0 ? 1 : 0));
            var model = Child(sign, "Model");
            var plane = GameObject.CreatePrimitive(PrimitiveType.Quad); plane.name = "Plane"; plane.transform.SetParent(model.transform, false);
            var score = plane.GetComponent<MeshRenderer>();
            Child(Child(root, "Grab base"), "Bone").AddComponent<VRCPhysBone>();
            root.transform.Find("Grab base").gameObject.AddComponent<VRCParentConstraint>();
            VotingSignInstaller.BuildController(root, folder);
            var animator = root.AddComponent<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Voting sign.controller");
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            AnimationPlayableOutput.Create(graph, "Sign", animator).SetSourcePlayable(playable);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
            int ownerState = 0;
            void Tick()
            {
                for (int i = 0; i < 12; i++)
                {
                    graph.Evaluate(.02f);
                    int state = playable.GetCurrentAnimatorStateInfo(1).shortNameHash;
                    if (state == ownerState) continue;
                    ownerState = state;
                    var active = controller.layers[1].stateMachine.states.Single(s => Animator.StringToHash(s.state.name) == state).state;
                    foreach (var driver in active.behaviours.OfType<VRCAvatarParameterDriver>())
                        if (playable.GetBool("IsLocal")) foreach (var operation in driver.parameters) playable.SetBool(operation.name, operation.value > .5f);
                }
            }
            Vector4 Shown() { var block = new MaterialPropertyBlock(); score.GetPropertyBlock(block); return block.GetVector("_MainTex_ST"); }

            playable.SetBool("IsLocal", true);
            Tick(); Assert.IsFalse(model.activeSelf);
            playable.SetBool(VotingSignInstaller.Enabled, true); Tick(); Tick();
            Assert.IsTrue(model.activeSelf);
            Near(new Vector4(.33f, .33f, 0, .72f), Shown(), "No vote shows the empty cell");
            foreach (var choice in VotingSignInstaller.Choices)
            {
                playable.SetInteger(VotingSignInstaller.Vote, choice.Value); Tick();
                Near(new Vector4(.33f, .33f, choice.Cell.x, choice.Cell.y), Shown(), choice.Name);
            }
            playable.SetInteger(VotingSignInstaller.Vote, 0); Tick();
            Near(new Vector4(.33f, .33f, 0, .72f), Shown(), "Clearing the vote");

            // The pen's grab: a PhysBone pickup latched to the wrist that took it.
            playable.SetBool(VotingSignInstaller.Grab + "_IsGrabbed", true); playable.SetBool(VotingSignInstaller.Left, true);
            playable.SetInteger("GestureLeft", 1); Tick();
            Assert.IsTrue(playable.GetBool(VotingSignInstaller.HeldLeft)); Assert.AreEqual(1, follow.Sources[1].Weight);
            playable.SetBool(VotingSignInstaller.Grab + "_IsGrabbed", false); Tick();
            Assert.IsTrue(playable.GetBool(VotingSignInstaller.HeldLeft), "The native grab ending does not drop it");
            playable.SetInteger("GestureLeft", 0); Tick();
            Assert.IsFalse(playable.GetBool(VotingSignInstaller.HeldLeft)); Assert.IsTrue(follow.FreezeToWorld, "Let go, it stays in the world");

            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            Assert.AreEqual(12, parameters.CalcTotalCost(), "Shown, held left/right, drop and an 8-bit vote");
            var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Sign menu.asset");
            Assert.IsTrue(menu.controls.Count <= 8);
            var votes = menu.controls.Single(c => c.name == "Vote").subMenu;
            Assert.IsTrue(votes.controls.Count <= 8);
            var toggles = votes.controls.Concat(menu.controls).Where(c => c.parameter?.name == VotingSignInstaller.Vote).ToList();
            CollectionAssert.AreEquivalent(VotingSignInstaller.Choices.Select(c => (float)c.Value), toggles.Select(c => c.value), "Every score has its toggle");
            Assert.IsTrue(toggles.All(c => c.type == VRCExpressionsMenu.Control.ControlType.Toggle && c.name == VotingSignInstaller.Choices.Single(x => x.Value == (int)c.value).Name));
        }
        finally
        {
            if (graph.IsValid()) graph.Destroy();
            Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); AssetDatabase.DeleteAsset(folder);
        }
    }

    private static void Near(Vector4 expected, Vector4 actual, string what) =>
        Assert.Less(Vector4.Distance(expected, actual), 1e-4f, what + ": " + actual);

    private static GameObject Child(GameObject parent, string name)
    { var child = new GameObject(name); child.transform.SetParent(parent.transform, false); return child; }
}
