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
    public void OneHandCarriesItBothHandsStretchItBetweenThePalms()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var folder = "Assets/__OrbitersHandScreenTest-" + Guid.NewGuid().ToString("N");
        // The fixture lives in a preview scene, never in the user's open scene.
        var scene = EditorSceneManager.NewPreviewScene();
        var solver = new ConstraintSolver();
        Rig owner = null;
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandScreenInstaller.CreatePrefab(folder));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Hand screen.controller");
            owner = new Rig(prefab, controller, scene, "Hand screen", local: true);
            solver.Track(owner.Root);
            var frame = owner.Frame.gameObject;
            var grabs = owner.Root.GetComponentsInChildren<VRCPhysBone>(true);
            void Tick(int frames = 6)
            {
                for (int i = 0; i < frames; i++) { owner.Animate(); solver.Solve(); }
            }
            void Near(Vector3 expected, Vector3 actual, string message) => Assert.Less(Vector3.Distance(expected, actual), .005f, message);
            void Same(Vector3[] expected, string message)
            {
                for (int c = 0; c < expected.Length; c++) Near(expected[c], owner.Corners[c].position, message);
            }
            float Apart() => Vector3.Distance(owner.LeftWrist.position, owner.RightWrist.position);

            Tick(); Assert.IsFalse(frame.activeSelf);
            owner.Set(HandScreenInstaller.Enabled, true); Tick();
            Assert.IsTrue(frame.activeSelf); Assert.IsTrue(grabs.All(g => g.enabled));
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, owner.Width, .005f);
            Assert.AreEqual(owner.Width / HandScreenInstaller.Aspect, owner.Height, .005f, "16:9");
            Assert.Greater(Vector3.Dot(owner.Front, owner.Head.position - owner.Middle), 0, "It faces its owner");
            var home = owner.Middle;

            // The left hand takes the left handle: the screen is carried whole from where it was taken.
            owner.Grab(HandScreenInstaller.GrabLeftHandle, true); owner.Set(HandScreenInstaller.LeftAtLeft, true);
            owner.Gesture("GestureLeft", 1); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft)); Assert.IsFalse(owner.Get(HandScreenInstaller.HeldRight));
            Near(home, owner.Middle, "Taken where it is, without a jump");
            owner.Grab(HandScreenInstaller.GrabLeftHandle, false); owner.Set(HandScreenInstaller.LeftAtLeft, false); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft), "The native grab ending does not drop it");
            var carry = owner.Root.transform.Find(HandScreenInstaller.LeftGrip + "/" + HandScreenInstaller.Carry);
            var carried = owner.Corners.Select(c => carry.InverseTransformPoint(c.position)).ToArray();
            owner.LeftWrist.position += new Vector3(.1f, .2f, .3f); Tick();
            Same(carried.Select(carry.TransformPoint).ToArray(), "Carried whole in the hand");
            Assert.AreEqual(HandScreenInstaller.DefaultWidth, owner.Width, .005f);
            Assert.Greater(Vector3.Dot(owner.Front, owner.Head.position - owner.Middle), 0, "Still facing its owner");
            var before = owner.Corners.Select(c => c.position).ToArray();
            owner.LeftWrist.rotation = Quaternion.Euler(30, 40, -20); Tick();
            Same(before, "Turning the wrist leaves it as it is (a guest's copy of a wrist's turn is the least exact part of a pose)");
            owner.LeftWrist.rotation = Quaternion.identity; Tick();

            // The right hand takes the right handle: each handle goes onto its palm and the screen spans the hands, 16:9.
            owner.Grab(HandScreenInstaller.GrabRightHandle, true); owner.Set(HandScreenInstaller.RightAtRight, true);
            owner.Gesture("GestureRight", 1); owner.Visited.Clear(); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldRight)); Assert.IsFalse(owner.Get(HandScreenInstaller.Crossed));
            Assert.IsTrue(owner.Visited.Contains("Both hands catch"));
            Near(owner.LeftWrist.position, owner.LeftHandle.position, "The left handle is in the left palm");
            Near(owner.RightWrist.position, owner.RightHandle.position, "The right handle is in the right palm");
            Assert.AreEqual(Apart(), owner.Width, .005f);
            Assert.AreEqual(owner.Width / HandScreenInstaller.Aspect, owner.Height, .005f);
            owner.RightWrist.position += Vector3.right * .4f; Tick();
            Assert.AreEqual(Apart(), owner.Width, .005f, "Hands further apart make it wider");
            owner.RightWrist.position += Vector3.left * .7f; Tick();
            Assert.AreEqual(Apart(), owner.Width, .005f, "Closer hands make it smaller");
            Assert.AreEqual(owner.Width / HandScreenInstaller.Aspect, owner.Height, .01f);
            owner.RightWrist.rotation = Quaternion.Euler(0, 0, 50); before = owner.Corners.Select(c => c.position).ToArray(); Tick();
            Same(before, "Turning a wrist doesn't tilt it");
            owner.RightWrist.rotation = Quaternion.identity;
            float stretched = owner.Width;

            // Relaxing the right hand lets go of it at once; the left carries the stretched screen.
            owner.Gesture("GestureRight", 0); owner.Grab(HandScreenInstaller.GrabRightHandle, false); Tick();
            Assert.IsFalse(owner.Get(HandScreenInstaller.HeldRight)); Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft));
            owner.RightWrist.position += Vector3.right; owner.LeftWrist.position += Vector3.up * .1f; Tick();
            Assert.AreEqual(stretched, owner.Width, .005f, "One hand never resizes it");
            owner.Gesture("GestureLeft", 2); Tick();
            Assert.IsFalse(owner.Get(HandScreenInstaller.HeldLeft));
            before = owner.Corners.Select(c => c.position).ToArray();
            owner.LeftWrist.position += Vector3.forward; owner.Head.position += new Vector3(.5f, 0, -.4f); Tick();
            Same(before, "Let go, it stays where it is, turned as it was");

            // The left hand on the right handle, then the right hand on the left one: crossed, each handle in the palm
            // holding it, and the screen shows its back as one held from behind does.
            owner.Head.position -= new Vector3(.5f, 0, -.4f);
            owner.Grab(HandScreenInstaller.GrabRightHandle, true); owner.Set(HandScreenInstaller.LeftAtRight, true); owner.Gesture("GestureLeft", 1); Tick();
            owner.Grab(HandScreenInstaller.GrabRightHandle, false); owner.Set(HandScreenInstaller.LeftAtRight, false); Tick();
            owner.Grab(HandScreenInstaller.GrabLeftHandle, true); owner.Set(HandScreenInstaller.RightAtLeft, true); owner.Gesture("GestureRight", 1); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.Crossed));
            Near(owner.RightWrist.position, owner.LeftHandle.position, "The right palm holds the left handle");
            Near(owner.LeftWrist.position, owner.RightHandle.position, "The left palm holds the right handle");
            Assert.AreEqual(Apart(), owner.Width, .005f);
            // Drop in the menu lets go of both.
            owner.Set(HandScreenInstaller.Drop, true); Tick();
            Assert.IsFalse(owner.Get(HandScreenInstaller.HeldLeft) || owner.Get(HandScreenInstaller.HeldRight));
            owner.Set(HandScreenInstaller.Drop, false); owner.Grab(HandScreenInstaller.GrabLeftHandle, false);
            owner.Set(HandScreenInstaller.RightAtLeft, false); owner.Gesture("GestureLeft", 0); owner.Gesture("GestureRight", 0); Tick();

            // Grip alone on Touch (pointing, 3) keeps the first hand's native grab until it lets go: the other hand still
            // takes the other handle, and only that one.
            owner.Grab(HandScreenInstaller.GrabLeftHandle, true); owner.Set(HandScreenInstaller.LeftAtLeft, true); owner.Gesture("GestureLeft", 3); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft)); Assert.IsFalse(owner.Get(HandScreenInstaller.HeldRight));
            owner.Set(HandScreenInstaller.RightAtLeft, true); owner.Gesture("GestureRight", 3); Tick();
            Assert.IsFalse(owner.Get(HandScreenInstaller.HeldRight), "The first hand's grab is not the second hand's");
            owner.Set(HandScreenInstaller.RightAtLeft, false); owner.Grab(HandScreenInstaller.GrabRightHandle, true); owner.Set(HandScreenInstaller.RightAtRight, true); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft) && owner.Get(HandScreenInstaller.HeldRight), "Both hands while the first grab still holds");
            Assert.IsFalse(owner.Get(HandScreenInstaller.Crossed));
            owner.Gesture("GestureRight", 0); owner.Grab(HandScreenInstaller.GrabRightHandle, false); owner.Grab(HandScreenInstaller.GrabLeftHandle, false); Tick();
            Assert.IsTrue(owner.Get(HandScreenInstaller.HeldLeft)); Assert.IsFalse(owner.Get(HandScreenInstaller.HeldRight));
            owner.Gesture("GestureLeft", 0); Tick();
            Assert.IsFalse(owner.Get(HandScreenInstaller.HeldLeft));
            owner.Set(HandScreenInstaller.LeftAtLeft, false); owner.Set(HandScreenInstaller.RightAtRight, false);

            // A remote client follows the owner's synced hands.
            owner.Set("IsLocal", false); owner.Set(HandScreenInstaller.HeldLeft, true); owner.Set(HandScreenInstaller.HeldRight, true); Tick();
            Assert.AreEqual(Apart(), owner.Width, .005f);
            owner.LeftWrist.position += Vector3.left * .2f; Tick();
            Assert.AreEqual(Apart(), owner.Width, .005f);

            // Turned off and on, it is back in front of its owner at its first size.
            owner.Set(HandScreenInstaller.Enabled, false); Tick(); Assert.IsFalse(frame.activeSelf);
            owner.Set(HandScreenInstaller.HeldLeft, false); owner.Set(HandScreenInstaller.HeldRight, false);
            owner.Set(HandScreenInstaller.Enabled, true); Tick();
            Near(home, owner.Middle, "Back home"); Assert.AreEqual(HandScreenInstaller.DefaultWidth, owner.Width, .005f);

            var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
            Assert.AreEqual(4, parameters.CalcTotalCost(), "Shown, held left, held right, crossed");
        }
        finally
        {
            solver.Release();
            owner?.Release();
            EditorSceneManager.ClosePreviewScene(scene); AssetDatabase.DeleteAsset(folder);
        }
    }

    [Test]
    public void GuestsSeeItWhereItsOwnerPutsIt()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var folder = "Assets/__OrbitersHandScreenTest-" + Guid.NewGuid().ToString("N");
        var scene = EditorSceneManager.NewPreviewScene();
        var solver = new ConstraintSolver();
        var rigs = new List<Rig>();
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandScreenInstaller.CreatePrefab(folder));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Hand screen.controller");
            var owner = new Rig(prefab, controller, scene, "Owner", local: true);
            var guest = new Rig(prefab, controller, scene, "Guest", local: false);
            var late = new Rig(prefab, controller, scene, "Late guest", local: false);
            rigs.AddRange(new[] { owner, guest, late });
            foreach (var rig in rigs) solver.Track(rig.Root);

            // Guests get the owner's pose and synced bools a few updates late (the bools no sooner than the pose), and
            // their copy of each wrist is turned otherwise: the least exact part of a remote pose. The late guest only
            // joins halfway, when the owner already carries the screen somewhere else.
            const int Delay = 4;
            string[] synced = { HandScreenInstaller.Enabled, HandScreenInstaller.HeldLeft, HandScreenInstaller.HeldRight, HandScreenInstaller.Crossed };
            var history = new List<(Vector3 head, Vector3 left, Vector3 right, Quaternion leftTurn, Quaternion rightTurn, bool[] bools)>();
            var turnedOtherwise = Quaternion.Euler(20, -35, 15);
            bool joined = false;
            void Follow(Rig rig, int frame, bool turned)
            {
                var past = history[Math.Max(0, frame)];
                rig.Head.position = past.head;
                rig.LeftWrist.SetPositionAndRotation(past.left, turned ? past.leftTurn * turnedOtherwise : past.leftTurn);
                rig.RightWrist.SetPositionAndRotation(past.right, turned ? past.rightTurn * Quaternion.Inverse(turnedOtherwise) : past.rightTurn);
                for (int i = 0; i < synced.Length; i++) rig.Set(synced[i], past.bools[i]);
            }
            void Step(int frames = 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    owner.Animate();
                    history.Add((owner.Head.position, owner.LeftWrist.position, owner.RightWrist.position, owner.LeftWrist.rotation,
                        owner.RightWrist.rotation, synced.Select(owner.Get).ToArray()));
                    Follow(guest, history.Count - 1 - Delay, true);
                    guest.Animate();
                    if (joined)
                    {
                        Follow(late, history.Count - 1 - Delay, false);
                        late.Animate();
                    }
                    solver.Solve();
                }
            }
            void Agree(Rig rig, string message)
            {
                Step(Delay + 3);
                for (int c = 0; c < 4; c++)
                    Assert.Less(Vector3.Distance(owner.Corners[c].position, rig.Corners[c].position), .005f, rig.Root.name + ": " + message);
            }
            void Move(Transform wrist, Vector3 by, Vector3 turn, int frames)
            {
                for (int i = 0; i < frames; i++) { wrist.position += by / frames; wrist.rotation *= Quaternion.Euler(turn / frames); Step(); }
            }

            owner.Set(HandScreenInstaller.Enabled, true); Step(3);
            Agree(guest, "shown in front of its owner");

            // The left hand reaches the left handle and takes it, then carries the screen away, turning as it goes.
            owner.LeftWrist.position = owner.LeftHandle.position + new Vector3(-.03f, -.04f, -.02f); Step(2);
            owner.Grab(HandScreenInstaller.GrabLeftHandle, true); owner.Set(HandScreenInstaller.LeftAtLeft, true); owner.Gesture("GestureLeft", 1); Step(3);
            owner.Grab(HandScreenInstaller.GrabLeftHandle, false); owner.Set(HandScreenInstaller.LeftAtLeft, false);
            Move(owner.LeftWrist, new Vector3(.4f, .2f, .3f), new Vector3(60, 90, -30), 20); Step(3);
            Agree(guest, "carried in one hand");

            // A guest joins now: it shows the screen in front of the owner's face, as nobody told it where the screen is.
            joined = true; Step(3);
            // The right hand takes the right handle and stretches the screen.
            owner.RightWrist.position = owner.RightHandle.position + new Vector3(.02f, -.03f, .01f); Step(2);
            owner.Grab(HandScreenInstaller.GrabRightHandle, true); owner.Set(HandScreenInstaller.RightAtRight, true); owner.Gesture("GestureRight", 1); Step(3);
            owner.Grab(HandScreenInstaller.GrabRightHandle, false); owner.Set(HandScreenInstaller.RightAtRight, false);
            Move(owner.RightWrist, new Vector3(.35f, .05f, -.1f), new Vector3(-40, 20, 70), 15); Step(3);
            Agree(guest, "stretched between the hands");
            Agree(late, "held in both hands, a late guest sees it where its owner does");

            // The right hand lets go, the left carries it on and leaves it in the world, then the owner walks away.
            owner.Gesture("GestureRight", 0); Step(3);
            Move(owner.LeftWrist, new Vector3(-.2f, .1f, .25f), new Vector3(30, -120, 45), 20); Step(3);
            owner.Gesture("GestureLeft", 2); Step(3);
            for (int i = 0; i < 20; i++)
            {
                owner.Head.position += new Vector3(.05f, 0, -.04f); owner.LeftWrist.position += new Vector3(.05f, -.02f, -.04f);
                owner.RightWrist.position += new Vector3(.05f, -.02f, -.04f); owner.LeftWrist.rotation *= Quaternion.Euler(4, 6, 0); Step();
            }
            Agree(guest, "left in the world");
            Agree(late, "left in the world");
        }
        finally
        {
            solver.Release();
            foreach (var rig in rigs) rig.Release();
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

            // Every client works the screen out from the owner's head and palms, never a wrist's turn.
            var head = prefab.transform.Find(HandScreenInstaller.Head);
            var grips = new[] { HandScreenInstaller.LeftGrip, HandScreenInstaller.RightGrip }.Select(n => prefab.transform.Find(n)).ToArray();
            Assert.IsNotNull(head); Assert.IsTrue(grips.All(g => g != null));
            foreach (var grip in grips)
            {
                var face = grip.Find(HandScreenInstaller.Carry).GetComponent<VRCAimConstraint>();
                Assert.AreEqual(head, face.Sources[0].SourceTransform, "A carried screen faces the head");
                Assert.AreEqual(head, face.WorldUpTransform);
            }
            Assert.AreEqual(prefab.transform.Find(HandScreenInstaller.LeftHandle),
                prefab.transform.Find(HandScreenInstaller.Frame).GetComponent<VRCAimConstraint>().WorldUpTransform, "Upright as its left handle is");
            foreach (string name in new[] { HandScreenInstaller.LeftHandle, HandScreenInstaller.RightHandle })
            {
                var follow = prefab.transform.Find(name).GetComponent<VRCParentConstraint>();
                Assert.AreEqual(HandScreenInstaller.HandleSources, follow.Sources.Count);
                Assert.AreEqual(grips[0].Find(HandScreenInstaller.Carry), follow.Sources[HandScreenInstaller.InLeftHand].SourceTransform, "A handle in a hand follows the palm's Carry frame");
                Assert.AreEqual(grips[1].Find(HandScreenInstaller.Carry), follow.Sources[HandScreenInstaller.InRightHand].SourceTransform);
                Assert.AreEqual(grips[0].Find(HandScreenInstaller.Carry), follow.Sources[HandScreenInstaller.CarriedLeft].SourceTransform);
                Assert.AreEqual(grips[1].Find(HandScreenInstaller.Carry), follow.Sources[HandScreenInstaller.CarriedRight].SourceTransform);
            }
        }
        finally { AssetDatabase.DeleteAsset(folder); }
    }

    /// <summary>
    /// A hand screen in a preview scene with stand-ins for its owner's head and wrists, where <see cref="HandScreenInstaller.Bind"/>
    /// would tie it, run by its controller like an avatar in VRChat: on its owner, or on a guest who only gets the synced
    /// bools.
    /// </summary>
    private sealed class Rig
    {
        public readonly GameObject Root;
        public readonly Transform Head, LeftWrist, RightWrist, LeftHandle, RightHandle, Frame;
        public readonly Transform[] Corners;
        public readonly HashSet<string> Visited = new HashSet<string>();
        private readonly AnimatorController controller;
        private readonly AnimatorControllerPlayable playable;
        private PlayableGraph graph;
        private readonly int[] current;

        public Rig(GameObject prefab, AnimatorController controller, Scene scene, string name, bool local)
        {
            Root = Object.Instantiate(prefab); Root.name = name;
            SceneManager.MoveGameObjectToScene(Root, scene);
            Frame = Root.transform.Find(HandScreenInstaller.Frame);
            LeftHandle = Root.transform.Find(HandScreenInstaller.LeftHandle);
            RightHandle = Root.transform.Find(HandScreenInstaller.RightHandle);
            Corners = HandScreenInstaller.Corners.Select(p => Root.transform.Find(p)).ToArray();
            Head = StandIn("Head stand-in", new Vector3(0, 1.56f, 0));
            LeftWrist = StandIn("Left wrist", new Vector3(-.35f, 1.45f, .45f));
            RightWrist = StandIn("Right wrist", new Vector3(.35f, 1.45f, .45f));
            Tie(HandScreenInstaller.Head, Head);
            Tie(HandScreenInstaller.LeftGrip, LeftWrist);
            Tie(HandScreenInstaller.RightGrip, RightWrist);
            // In front of the face, as Bind ties it (a constraint without a source puts it at the world's origin).
            Tie(HeldPropRig.Spawn, Head, new Vector3(0, -.06f, .5f));
            this.controller = controller;
            graph = PlayableGraph.Create(name);
            var animator = Root.AddComponent<Animator>();
            playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, name, animator); output.SetSourcePlayable(playable);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); graph.Play();
            current = new int[controller.layers.Length];
            Set("IsLocal", local);
        }

        public float Width => Vector3.Distance(Corners[0].position, Corners[1].position);
        public float Height => Vector3.Distance(Corners[0].position, Corners[2].position);
        public Vector3 Middle => (Corners[0].position + Corners[3].position) / 2;
        // The picture faces -Z of its frame.
        public Vector3 Front => -Frame.forward;

        public bool Get(string parameter) => playable.GetBool(parameter);
        public void Set(string parameter, bool value) => playable.SetBool(parameter, value);
        public void Gesture(string parameter, int value) => playable.SetInteger(parameter, value);
        public void Grab(string handle, bool on) => Set(handle + "_IsGrabbed", on);

        /// <summary>One animator update and the SDK drivers it entered (applied on the owner only, as VRChat does).</summary>
        public void Animate()
        {
            graph.Evaluate(.02f);
            for (int layer = 0; layer < current.Length; layer++)
            {
                int state = playable.GetCurrentAnimatorStateInfo(layer).shortNameHash;
                if (state == current[layer]) continue;
                current[layer] = state;
                var active = controller.layers[layer].stateMachine.states.Single(s => Animator.StringToHash(s.state.name) == state).state;
                Visited.Add(active.name);
                foreach (var driver in active.behaviours.OfType<VRCAvatarParameterDriver>())
                {
                    Assert.IsTrue(driver.localOnly);
                    if (Get("IsLocal")) foreach (var set in driver.parameters) Set(set.name, set.value > .5f);
                }
            }
        }

        public void Release()
        {
            if (graph.IsValid()) graph.Destroy();
        }

        private Transform StandIn(string name, Vector3 at)
        {
            var stand = new GameObject(name).transform; stand.SetParent(Root.transform, false); stand.localPosition = at;
            return stand;
        }

        private void Tie(string path, Transform to, Vector3 offset = default)
        {
            var follow = Root.transform.Find(path).GetComponent<VRCParentConstraint>();
            follow.Sources[0] = new VRCConstraintSource(to, 1, offset, Vector3.zero);
        }
    }

    /// <summary>
    /// Runs the VRChat SDK's own constraint solver on objects of a preview scene (it only picks up the open scenes by
    /// itself), one update per <see cref="Solve"/>.
    /// </summary>
    private sealed class ConstraintSolver
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Manager = typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
        private readonly List<VRCConstraintBase> constraints = new List<VRCConstraintBase>();

        public void Track(GameObject root)
        {
            foreach (var constraint in root.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                constraints.Add(constraint);
                Manager.GetMethod("RegisterConstraint", Static).Invoke(null, new object[] { constraint });
            }
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
            constraints.Clear();
        }
    }
}
