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
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using Object = UnityEngine.Object;

// The avatar gun (contracts Docs/avatar-gun.md), built whole into a temporary folder: its parameters as Orbiters - N4N0 finds
// them, its ray and contacts, its fit on an avatar, the trigger's timings in the real controller, and N4N0's receiver.
public class AvatarGunTests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;
    private PlayableGraph graph;
    private AnimatorControllerPlayable playable;
    private AnimatorController controller;
    private readonly List<Object> owned = new List<Object>();
    private bool optionsFolderExisted;

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersAvatarGunTest-" + Guid.NewGuid().ToString("N");
        scene = EditorSceneManager.NewPreviewScene();
        optionsFolderExisted = AssetDatabase.IsValidFolder(AvatarGunInstaller.OptionsFolder.TrimEnd('/'));
    }

    [TearDown]
    public void TearDown()
    {
        if (graph.IsValid()) graph.Destroy();
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        if (!optionsFolderExisted) AssetDatabase.DeleteAsset("Assets/Orbiters/Avatar gun");
        sandbox.End();
    }

    [Test]
    public void ItCarriesTheContractsParametersUnderTheirOwnNames()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder));
        Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Any(p => p.StartsWith("Packages/orbiters.", StringComparison.Ordinal)),
            "Nothing of the Toolkit's: a gallery package carries everything it needs");
        Assert.IsTrue(prefab.GetComponentsInChildren<MonoBehaviour>(true).All(c => !c.GetType().Assembly.GetName().Name.StartsWith("Orbiters")));
        Assert.IsTrue(AttachmentHooks.Fits(prefab));
        Assert.AreEqual(prefab.transform, AvatarGunInstaller.FindAll(prefab).Single(), "Found by its ray's sender");

        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var expected = new[]
        {
            ("AvatarGun/Out", true), ("AvatarGun/Loaded", true), ("AvatarGun/Fire", true),
            ("AvatarGun/Pull", false), ("AvatarGun/Choose", false), ("AvatarGun/Hit", false),
        };
        CollectionAssert.AreEqual(expected, parameters.parameters.Select(p => (p.name, p.networkSynced)).ToList());
        Assert.IsTrue(parameters.parameters.All(p => p.valueType == VRCExpressionParameters.ValueType.Bool && !p.saved && p.defaultValue == 0), "Bools, never saved, off at first");
        Assert.AreEqual(3, parameters.CalcTotalCost(), "Three synced bits");
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (var (name, _) in expected)
        {
            Assert.IsTrue(VrcFury.IsGlobalParameter(full, name), name + " keeps its name in the avatar");
            Assert.IsTrue(VrcParameters.OscDriven(name), name + ": optimizers leave it alone");
        }
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Avatar gun.controller");
        foreach (var (name, _) in expected) Assert.AreEqual(AnimatorControllerParameterType.Bool, controller.parameters.Single(p => p.name == name).type, name);

        // The menu: a submenu with the gun's toggle and the picker's button, each with its icon.
        var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single();
        Assert.AreEqual((AvatarGunInstaller.MenuPath, VRCExpressionsMenu.Control.ControlType.SubMenu), (menu.name, menu.type));
        Assert.IsNotNull(menu.icon);
        var controls = menu.subMenu.controls;
        Assert.AreEqual(new[] { ("Avatar gun", VRCExpressionsMenu.Control.ControlType.Toggle, "AvatarGun/Out"), ("Choose avatar", VRCExpressionsMenu.Control.ControlType.Button, "AvatarGun/Choose") },
            controls.Select(c => (c.name, c.type, c.parameter.name)).ToArray());
        Assert.IsTrue(controls.All(c => c.icon != null && AssetDatabase.GetAssetPath(c.icon).StartsWith(folder + "/", StringComparison.Ordinal)), "Its own icons, in its folder");

        // The model: one mesh, the body and the emitter; hidden until it comes out.
        var gun = prefab.transform.Find(AvatarGunInstaller.Gun);
        Assert.IsFalse(gun.gameObject.activeSelf);
        var model = prefab.transform.Find(AvatarGunInstaller.Model);
        var mesh = model.GetComponent<MeshFilter>().sharedMesh;
        Assert.AreEqual(2, mesh.subMeshCount, "The body and the emitter");
        Assert.Less(mesh.triangles.Length / 3, 5000, "Low poly");
        Assert.AreEqual(new[] { "Avatar gun", "Avatar gun emitter unloaded" }, model.GetComponent<MeshRenderer>().sharedMaterials.Select(m => m.name).ToArray(), "Dim until loaded");
        Assert.AreEqual(0, prefab.GetComponentsInChildren<Light>(true).Length + prefab.GetComponentsInChildren<AudioSource>(true).Length, "No light, no sound");
        Assert.AreEqual(folder + "/Avatar gun beam.shader", AssetDatabase.GetAssetPath(prefab.transform.Find(AvatarGunInstaller.Beam).GetComponent<SkinnedMeshRenderer>().sharedMaterial.shader));
    }

    [Test]
    public void ItsRayCarriesTheShotsSenderAndItsReceiverFeelsOtherPlayersOnly()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder));
        var muzzle = prefab.transform.Find(AvatarGunInstaller.Muzzle);
        var ray = muzzle.GetComponent<VRCRaycast>();
        Assert.AreEqual(VRCRaycast.RaycastComponentVersion.Version_1_1, ray.Version);
        Assert.AreEqual((Vector3.forward, AvatarGunInstaller.Range, false, VRCRaycast.CollisionMode.HitWorldsAndPlayers),
            (ray.RaycastDirection, ray.Distance, ray.ApplyTransformScale, ray.RaycastCollisionMode), "20 m from the muzzle, worlds and players");
        Assert.AreEqual(20f, AvatarGunInstaller.Range);
        Assert.AreEqual(prefab.transform.Find(AvatarGunInstaller.RayEnd), ray.ResultTransform);
        Assert.AreEqual(VRCRaycast.MissBehavior.SnapToEnd, ray.BehaviorOnMiss);
        Assert.IsTrue(string.IsNullOrEmpty(ray.Parameter), "No animator parameter of its own");

        var end = ray.ResultTransform.GetComponent<VRCContactSender>();
        Assert.AreEqual((ContactBase.ShapeType.Sphere, .05f), (end.shapeType, end.radius), "A sphere of 5 cm where the ray ends");
        var close = prefab.transform.Find(AvatarGunInstaller.Close).GetComponent<VRCContactSender>();
        Assert.AreEqual((ContactBase.ShapeType.Capsule, .05f, 1.5f), (close.shapeType, close.radius, close.height));
        // Its capsule's axis (Y) along the muzzle, from the muzzle out to 1.5 m.
        Assert.Less(Vector3.Angle(close.transform.up, muzzle.forward), .01f);
        var middle = close.transform.TransformPoint(close.position);
        Assert.Less(Vector3.Distance(middle, muzzle.position + muzzle.forward * .75f), 1e-4f);
        foreach (var sender in new[] { end, close })
        {
            Assert.AreEqual(AvatarGunInstaller.ContactTag, sender.collisionTags.Single());
            Assert.AreEqual("OrbitersAvatarGun", AvatarGunInstaller.ContactTag);
            Assert.IsFalse(sender.localOnly, "Other players' receivers see it");
            Assert.IsFalse(sender.enabled, "Off between shots");
            Assert.Less(Vector3.Distance(Vector3.one, sender.transform.lossyScale), 1e-4f, "The contract's sizes whatever the hand's");
        }

        var receiver = prefab.transform.Find(AvatarGunInstaller.ReceiverName).GetComponent<VRCContactReceiver>();
        Assert.AreEqual(prefab.transform, receiver.transform.parent, "Around the body, not on the gun");
        Assert.IsTrue(receiver.localOnly && !receiver.allowSelf && receiver.allowOthers, "Local, other players only: its own gun never hits it");
        Assert.AreEqual((ContactReceiver.ReceiverType.Constant, ContactBase.ShapeType.Capsule, "AvatarGun/Hit"), (receiver.receiverType, receiver.shapeType, receiver.parameter));
        Assert.AreEqual(AvatarGunInstaller.ContactTag, receiver.collisionTags.Single());
        Assert.Greater(receiver.radius, .3f, "Wider than VRChat's player capsule");
    }

    [Test]
    public void ItFitsEitherPalmAlongTheHandAndBeforeTheEyes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder));
        var (avatar, animator) = Humanoid(eyes: 1.4f);
        var gun = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(gun.gameObject, avatar);
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(gun.gameObject, avatar).Kind, "Attached by its own constraints");

        var hold = gun.Find(AvatarGunInstaller.Gun).GetComponent<VRCParentConstraint>();
        Assert.AreEqual(3, hold.Sources.Count);
        var hands = new[] { (left: false, index: AvatarGunInstaller.InRightHand), (left: true, index: AvatarGunInstaller.InLeftHand) };
        foreach (var (left, index) in hands)
        {
            var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var source = hold.Sources[index];
            Assert.AreEqual(hand, source.SourceTransform, (left ? "left" : "right") + " hand");
            // Where the source puts the gun: along the hand (wrist to middle knuckle), its top on the index's side.
            var rotation = hand.rotation * Quaternion.Euler(source.ParentRotationOffset);
            var position = hand.position + hand.rotation * source.ParentPositionOffset;
            var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            Assert.Less(Vector3.Angle(rotation * Vector3.forward, middle.position - hand.position), .1f, "The barrel along the hand");
            Assert.Greater(Vector3.Dot(rotation * Vector3.up, avatar.forward), .9f, "Its top on the thumb's side (ahead, arms out palms down)");
            Assert.Less(Vector3.Distance(position, Vector3.Lerp(hand.position, middle.position, .5f)), .05f, "In the palm");
        }
        Assert.AreEqual(gun.Find(AvatarGunInstaller.Desktop), hold.Sources[AvatarGunInstaller.AtDesktop].SourceTransform);
        float scale = gun.Find(AvatarGunInstaller.Model).localScale.x;
        Assert.AreEqual(Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.RightHand).position, animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position) /
                        AvatarGunInstaller.ReferenceHand, scale, 1e-3f, "As large as the hand");
        Assert.Less(Vector3.Distance(gun.Find(AvatarGunInstaller.Muzzle).localPosition, AvatarGunInstaller.MuzzlePoint * scale), 1e-5f, "The muzzle at the model's");

        // Desktop mode: before the eyes, low and to the right, looking ahead, on the head bone.
        var desktop = gun.Find(AvatarGunInstaller.Desktop);
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        Assert.AreEqual(head, desktop.GetComponent<VRCParentConstraint>().Sources[0].SourceTransform);
        var eyes = avatar.TransformPoint(avatar.GetComponent<VRCAvatarDescriptor>().ViewPosition);
        Assert.Less(Vector3.Distance(avatar.InverseTransformDirection(desktop.position - eyes), AvatarGunInstaller.DesktopFromEyes * (1.4f / 1.6f)), 1e-3f);
        Assert.Less(Quaternion.Angle(avatar.rotation, desktop.rotation), .01f);

        // The receiver around the body from the feet up, as large as the avatar.
        var receiver = gun.Find(AvatarGunInstaller.ReceiverName).GetComponent<VRCContactReceiver>();
        float size = 1.4f / 1.6f;
        Assert.AreEqual(AvatarGunInstaller.ReceiverRadius * size, receiver.radius, 1e-4f);
        Assert.AreEqual(AvatarGunInstaller.ReceiverHeight * size, receiver.height, 1e-4f);
        Assert.Less(Vector3.Distance(receiver.transform.position, avatar.position + Vector3.up * AvatarGunInstaller.ReceiverMiddle * size), 1e-4f);
        Assert.Less(receiver.transform.position.y - receiver.height / 2, .01f, "Down to the feet");
        Assert.Greater(receiver.transform.position.y + receiver.height / 2, 1.4f, "Above the eyes");
    }

    [Test]
    public void ALoadedGunFiresForFireSecondsThenRearms()
    {
        var (root, fire) = Play();
        playable.SetBool("IsLocal", true);
        playable.SetInteger("VRMode", 1);
        playable.SetBool(AvatarGunInstaller.Loaded, true);
        playable.SetBool(AvatarGunInstaller.Out, true);
        Tick(.1f);
        Assert.IsTrue(root.transform.Find(AvatarGunInstaller.Gun).gameObject.activeSelf, "Out: in the hand");
        Squeeze(.9f);
        Until(() => playable.GetBool(AvatarGunInstaller.Fire), 1);
        float on = Until(() => !playable.GetBool(AvatarGunInstaller.Fire), 2);
        Assert.AreEqual(AvatarGunInstaller.FireSeconds, on, .05f, "Fire stays on for FIRE_MS");
        Assert.IsTrue(fire.Count == 1, "One shot");
        // Still squeezed: no second shot until the trigger is let go, and none in the re-arm time either.
        Tick(1.5f);
        Assert.AreEqual(1, fire.Count);
        Squeeze(.1f);
        Tick(.05f);
        Squeeze(.9f);
        Tick(.05f);
        Assert.AreEqual(2, fire.Count, "Let go, then pulled again: the next shot");
        // A pull during the re-arm time is lost.
        Tick(.4f);
        Squeeze(0); Tick(.05f); Squeeze(1); Tick(.05f);
        Assert.AreEqual(2, fire.Count, "Within REARM_MS of the last shot nothing fires");
        Until(() => playable.GetBool(AvatarGunInstaller.Fire), 3);
        Assert.AreEqual(2, fire.Count, "Still held: no shot after the re-arm either");
        Squeeze(0); Tick(.05f); Squeeze(1); Tick(.05f);
        Assert.AreEqual(3, fire.Count, "After REARM_MS, let go and pulled: the next shot");
        Assert.GreaterOrEqual(fire[2] - fire[1] - AvatarGunInstaller.FireSeconds, AvatarGunInstaller.RearmSeconds - .03f, "Off at least REARM_MS between shots");
    }

    [Test]
    public void TheTriggerWaitsForALetGoAfterTheGunComesOut()
    {
        var (_, fire) = Play();
        playable.SetBool("IsLocal", true);
        playable.SetInteger("VRMode", 1);
        playable.SetBool(AvatarGunInstaller.Loaded, true);
        Squeeze(1);
        playable.SetBool(AvatarGunInstaller.Out, true);
        Tick(.5f);
        Assert.AreEqual(0, fire.Count, "A fist held while taking it out fires nothing");
        // The hysteresis: easing to .45 is not a release, .35 is.
        Squeeze(.45f); Tick(.1f); Squeeze(1); Tick(.1f);
        Assert.AreEqual(0, fire.Count);
        Squeeze(.35f); Tick(.1f);
        Squeeze(.55f); Tick(.1f);
        Assert.AreEqual(1, fire.Count, "Past .5 after a release under .4");
    }

    [Test]
    public void AnUnloadedGunClicksAndFiresNothing()
    {
        var (root, fire) = Play();
        playable.SetBool("IsLocal", true);
        playable.SetInteger("VRMode", 1);
        playable.SetBool(AvatarGunInstaller.Out, true);
        Tick(.1f);
        var renderer = root.transform.Find(AvatarGunInstaller.Model).GetComponent<MeshRenderer>();
        Assert.AreEqual("Avatar gun emitter unloaded", renderer.sharedMaterials[1].name, "Dim");
        Squeeze(1);
        bool clicked = false;
        for (float t = 0; t < .3f; t += .02f)
        {
            Tick(.02f);
            clicked |= Mathf.Abs(Mathf.DeltaAngle(0, root.transform.Find(AvatarGunInstaller.Model).localEulerAngles.x)) > .5f;
        }
        Assert.IsTrue(clicked, "An empty click");
        Assert.AreEqual(0, fire.Count, "Nothing fires");
        playable.SetBool(AvatarGunInstaller.Loaded, true);
        Tick(.1f);
        Assert.AreEqual("Avatar gun emitter loaded", renderer.sharedMaterials[1].name, "Lit once loaded");
        Squeeze(0); Tick(.1f); Squeeze(1); Tick(.1f);
        Assert.AreEqual(1, fire.Count);
        // Put away, nothing fires either.
        playable.SetBool(AvatarGunInstaller.Out, false);
        Tick(1.2f);
        Squeeze(0); Tick(.1f); Squeeze(1); Tick(.1f);
        Assert.AreEqual(1, fire.Count, "Only while out");
        Assert.IsFalse(root.transform.Find(AvatarGunInstaller.Gun).gameObject.activeSelf);
    }

    [Test]
    public void InDesktopModePullFiresAndTheGunIsBeforeTheEyes()
    {
        var (root, fire) = Play();
        playable.SetBool("IsLocal", true);
        playable.SetInteger("VRMode", 0);
        playable.SetBool(AvatarGunInstaller.Loaded, true);
        playable.SetBool(AvatarGunInstaller.Out, true);
        Tick(.1f);
        var hold = root.transform.Find(AvatarGunInstaller.Gun).GetComponent<VRCParentConstraint>();
        Assert.AreEqual((0f, 0f, 1f), (hold.Sources[0].Weight, hold.Sources[1].Weight, hold.Sources[2].Weight), "Held by the desktop anchor");
        // The fist does nothing outside VR (desktop gestures are keys); Orbiters - N4N0's Pull fires.
        Squeeze(1); Tick(.2f);
        Assert.AreEqual(0, fire.Count);
        playable.SetBool(AvatarGunInstaller.Pull, true); Tick(.1f);
        Assert.AreEqual(1, fire.Count);
        playable.SetBool(AvatarGunInstaller.Pull, false);
        playable.SetInteger("VRMode", 1); Tick(.1f);
        Assert.AreEqual((1f, 0f, 0f), (hold.Sources[0].Weight, hold.Sources[1].Weight, hold.Sources[2].Weight), "In VR: the right palm");
    }

    [Test]
    public void EveryCopyFollowsTheSyncedFireAndNothingOnTheHitPathReadsIsLocal()
    {
        var (root, fire) = Play();
        // Someone else's copy: IsLocal off, Fire and Out reach it synced.
        playable.SetBool("IsLocal", false);
        playable.SetInteger("VRMode", 1);
        playable.SetBool(AvatarGunInstaller.Out, true);
        playable.SetBool(AvatarGunInstaller.Loaded, true);
        Squeeze(1); Tick(.2f);
        Assert.AreEqual(0, fire.Count, "Only its owner pulls the trigger");
        var end = root.transform.Find(AvatarGunInstaller.RayEnd).GetComponent<VRCContactSender>();
        var close = root.transform.Find(AvatarGunInstaller.Close).GetComponent<VRCContactSender>();
        var beam = root.transform.Find(AvatarGunInstaller.Beam);
        Assert.IsFalse(end.enabled || close.enabled || beam.gameObject.activeSelf);
        playable.SetBool(AvatarGunInstaller.Fire, true);
        Tick(.06f);
        Assert.IsTrue(end.enabled && close.enabled && beam.gameObject.activeSelf, "The shot on every copy: both senders, the beam");
        Assert.Greater(beam.GetComponent<SkinnedMeshRenderer>().GetPropertyBlockFloat("_Fire"), .5f, "Lit as it leaves");
        Tick(.3f);
        Assert.AreEqual(0, beam.GetComponent<SkinnedMeshRenderer>().GetPropertyBlockFloat("_Fire"), .01f, "Faded out by FIRE_MS");
        playable.SetBool(AvatarGunInstaller.Fire, false);
        Tick(.06f);
        Assert.IsFalse(end.enabled || close.enabled || beam.gameObject.activeSelf);

        // Structurally: only the trigger layer reads IsLocal; its drivers are the owner's.
        foreach (var layer in controller.layers)
        {
            var transitions = layer.stateMachine.states.SelectMany(s => s.state.transitions).Concat(layer.stateMachine.anyStateTransitions).ToList();
            bool readsLocal = transitions.Any(t => t.conditions.Any(c => c.parameter == "IsLocal"));
            Assert.AreEqual(layer.name == "Trigger", readsLocal, layer.name);
            foreach (var driver in layer.stateMachine.states.SelectMany(s => s.state.behaviours).OfType<VRCAvatarParameterDriver>())
                Assert.IsTrue(driver.localOnly && layer.name == "Trigger" && driver.parameters.All(p => p.name == AvatarGunInstaller.Fire));
        }
        var muzzle = root.transform.Find(AvatarGunInstaller.Muzzle);
        Assert.IsFalse(controller.animationClips.SelectMany(c => AnimationUtility.GetCurveBindings(c)).Any(b => b.path == AvatarGunInstaller.Muzzle && b.type == typeof(VRCRaycast)),
            "The ray is never switched by the animator");
        Assert.IsTrue(muzzle.GetComponent<VRCRaycast>().enabled);
    }

    [Test]
    public void TheHandOptionHoldsItInTheLeftHand()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder));
        var (avatar, _) = Humanoid(eyes: 1.6f);
        var gun = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(gun.gameObject, avatar);
        var option = AttachmentHooks.Options(gun.gameObject).Single(o => o.Label == "Hand");
        Assert.AreEqual(0, option.Current, "Right by default");
        option.Choose(1);
        Assert.AreEqual(true, AvatarGunInstaller.HandOf(gun));
        Assert.AreEqual(1, AttachmentHooks.Options(gun.gameObject).Single(o => o.Label == "Hand").Current);
        Assert.AreEqual(1, gun.GetComponents(VrcFury.Component).Length, "The prefab's Full Controller replaced, not doubled");
        var full = VrcFury.Features(gun.gameObject).Single(f => f.kind == "FullController").feature;
        Assert.IsTrue(VrcFury.IsGlobalParameter(full, AvatarGunInstaller.Fire), "Its globals kept");
        var left = AssetDatabase.LoadAssetAtPath<AnimatorController>(AvatarGunInstaller.OptionsFolder + "Left hand/Avatar gun.controller");
        Assert.IsTrue(left.parameters.Any(p => p.name == "GestureLeftWeight") && !left.parameters.Any(p => p.name == "GestureRightWeight"));
        Assert.IsTrue(AnimationUtility.GetCurveBindings(left.layers[0].stateMachine.states.Single(s => s.state.name == "Left hand").state.motion as AnimationClip)
            .Any(b => b.propertyName == "Sources.source1.Weight"));
        Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(left), true).Any(p => p.StartsWith(folder + "/", StringComparison.Ordinal)), "Its own emitter materials");
        option.Choose(0);
        Assert.AreEqual(false, AvatarGunInstaller.HandOf(gun));
    }

    [Test]
    public void N4N0CarriesTheReceiverAndABuildKeepsOne()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder + "/Gun"));
        var n4n0Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(N4N0Installer.CreatePrefab(folder + "/N4N0"));
        var n4n0Receiver = n4n0Prefab.transform.Find(AvatarGunInstaller.ReceiverName).GetComponent<VRCContactReceiver>();
        Assert.IsTrue(n4n0Receiver.localOnly && !n4n0Receiver.allowSelf && n4n0Receiver.allowOthers && n4n0Receiver.parameter == AvatarGunInstaller.Hit);
        Assert.IsFalse(n4n0Receiver.transform.IsChildOf(n4n0Prefab.transform.Find(N4N0Installer.Shown)), "The body's, not the bot's: kept with the bot hidden and on phones");
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/N4N0/Parameters.asset");
        var hit = parameters.parameters.Single(p => p.name == AvatarGunInstaller.Hit);
        Assert.IsFalse(hit.networkSynced || hit.saved, "No synced bit");
        Assert.IsTrue(VrcFury.IsGlobalParameter(VrcFury.Features(n4n0Prefab).Single(f => f.kind == "FullController").feature, AvatarGunInstaller.Hit));
        foreach (var path in new[] { "/N4N0/N4N0.controller", "/N4N0/N4N0 mobile.controller" })
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + path).parameters.Any(p => p.name == AvatarGunInstaller.Hit), path + " declares Hit for OSC");

        var (avatar, _) = Humanoid(eyes: 1.6f);
        var n4n0 = ((GameObject)PrefabUtility.InstantiatePrefab(n4n0Prefab, avatar)).transform;
        var gun = ((GameObject)PrefabUtility.InstantiatePrefab(gunPrefab, avatar)).transform;
        AttachmentHooks.Prepare(n4n0.gameObject, avatar);
        AttachmentHooks.Prepare(gun.gameObject, avatar);
        var bound = n4n0.Find(AvatarGunInstaller.ReceiverName).GetComponent<VRCContactReceiver>();
        Assert.AreEqual(AvatarGunInstaller.ReceiverRadius, bound.radius, 1e-4f, "Fitted to the body as the gun's");
        var copy = Object.Instantiate(avatar.gameObject);
        SceneManager.MoveGameObjectToScene(copy, scene);
        AvatarGunInstaller.KeepOneReceiver(copy);
        Assert.AreEqual(1, copy.GetComponentsInChildren<VRCContactReceiver>(true).Count(r => r.parameter == AvatarGunInstaller.Hit), "One receiver for both");
        Assert.AreEqual(2, avatar.GetComponentsInChildren<VRCContactReceiver>(true).Count(r => r.parameter == AvatarGunInstaller.Hit), "The avatar itself keeps both");
    }

    // ---- The controller in a PlayableGraph ----

    // The prefab's controller on a placed gun, every update applying the Trigger layer's drivers as VRChat does on its
    // owner's client; the times Fire rose at.
    private (GameObject root, List<float> fire) Play()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarGunInstaller.CreatePrefab(folder));
        var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/Avatar gun.controller");
        var animator = root.AddComponent<Animator>();
        graph = PlayableGraph.Create("Avatar gun test");
        playable = AnimatorControllerPlayable.Create(graph, controller);
        AnimationPlayableOutput.Create(graph, "Avatar gun", animator).SetSourcePlayable(playable);
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        graph.Play();
        fired = new List<float>();
        time = 0; triggerState = 0;
        return (root, fired);
    }

    private List<float> fired;
    private float time;
    private int triggerState;

    private void Squeeze(float weight)
    {
        playable.SetInteger("GestureRight", weight > 0 ? 1 : 0);
        playable.SetFloat("GestureRightWeight", weight);
    }

    private void Tick(float seconds)
    {
        int layer = controller.layers.ToList().FindIndex(l => l.name == "Trigger");
        for (float t = 0; t < seconds - 1e-4f; t += .01f)
        {
            graph.Evaluate(.01f);
            time += .01f;
            int state = playable.GetCurrentAnimatorStateInfo(layer).shortNameHash;
            if (state == triggerState) continue;
            triggerState = state;
            var entered = controller.layers[layer].stateMachine.states.Single(s => Animator.StringToHash(s.state.name) == state).state;
            foreach (var driver in entered.behaviours.OfType<VRCAvatarParameterDriver>())
            {
                if (driver.localOnly && !playable.GetBool("IsLocal")) continue;
                foreach (var operation in driver.parameters)
                {
                    if (operation.name == AvatarGunInstaller.Fire && operation.value > .5f && !playable.GetBool(AvatarGunInstaller.Fire)) fired.Add(time);
                    playable.SetBool(operation.name, operation.value > .5f);
                }
            }
        }
    }

    // Ticks until `done` (at most `limit` seconds); how long it took.
    private float Until(Func<bool> done, float limit)
    {
        float start = time;
        while (!done() && time - start < limit) Tick(.01f);
        return time - start;
    }

    // ---- The avatar ----

    // A humanoid avatar in the preview scene whose eyes are this high, arms out palms down, with index, middle and little fingers.
    private (Transform avatar, Animator animator) Humanoid(float eyes)
    {
        var root = new GameObject("Avatar gun avatar");
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
            Add("Index1" + side, "Hand" + side, new Vector3(.085f * x, 0, .025f));
            Add("Middle1" + side, "Hand" + side, new Vector3(.09f * x, 0, .005f));
            Add("Little1" + side, "Hand" + side, new Vector3(.075f * x, 0, -.03f));
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
        }
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
            ["Left Index Proximal"] = "Index1L", ["Left Middle Proximal"] = "Middle1L", ["Left Little Proximal"] = "Little1L",
            ["Right Index Proximal"] = "Index1R", ["Right Middle Proximal"] = "Middle1R", ["Right Little Proximal"] = "Little1R",
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
        var animator = root.AddComponent<Animator>();
        animator.avatar = avatar;
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        descriptor.ViewPosition = new Vector3(0, eyes, .06f * k);
        return (root.transform, animator);
    }
}

internal static class RendererBlocks
{
    // The value the animator wrote into a renderer's property block.
    public static float GetPropertyBlockFloat(this Renderer renderer, string name)
    {
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        return block.GetFloat(name);
    }
}
