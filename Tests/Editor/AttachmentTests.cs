using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

public sealed class AttachmentTests
{
    private readonly List<Object> owned = new List<Object>();
    private GameObject avatar;
    private readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();

    private int undoGroup;

    [SetUp] public void SetUp()
    {
        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        avatar = Own(new GameObject("Attachment test avatar"));
        avatar.AddComponent<VRCAvatarDescriptor>();
        var armature = Child("Armature", avatar.transform, Vector3.zero);
        var hips = Bone("Hips", armature, new Vector3(0, 1, 0));
        var chest = Bone("Chest", Bone("Spine", hips, new Vector3(0, .1f, 0)), new Vector3(0, .15f, 0));
        Bone("Head", Bone("Neck", chest, new Vector3(0, .25f, 0)), new Vector3(0, .1f, 0));
        foreach (var (side, x) in new[] { ("Left", 1f), ("Right", -1f) })
            Bone(side + " wrist", Bone(side + " elbow", Bone(side + " arm", Bone(side + " shoulder", chest, new Vector3(.05f * x, .2f, 0)), new Vector3(.1f * x, 0, 0)), new Vector3(.25f * x, 0, 0)), new Vector3(.25f * x, 0, 0));
        Skin("Body", avatar.transform, bones.Values.ToArray());
    }

    [TearDown] public void TearDown()
    {
        AccessoryPoseSync.Disable();
        // The installer records Undo. Reverted here (no redo is kept), nothing is left for the Test Runner's final Undo
        // revert to bring back into the user's open scene.
        Undo.RevertAllDownToGroup(undoGroup);
        foreach (var build in owned.OfType<GameObject>()) AttachmentAnimationBuild.Release(build);
        foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        bones.Clear();
    }

    [Test] public void ClothingWithTheAvatarsBoneNamesUsesOneVrcFuryLink()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        var shirt = Clothing("Shirt", new[] { ("Hips", "", ""), ("Spine", "Hips", ""), ("Chest", "Spine", ""), ("Left shoulder", "Chest", ""), ("Left arm", "Left shoulder", "") });
        var plan = AttachmentPlanner.Analyze(shirt, avatar.transform);
        Assert.AreEqual(AttachmentKind.Clothing, plan.Kind);
        Assert.AreEqual(OrbitersAttachment.AttachMode.VrcFury, plan.Mode);
        Assert.AreEqual("Hips", plan.LinkFrom.name);
        Assert.AreSame(bones["Hips"], plan.LinkTo);
    }

    [Test] public void ClothingWithOtherBoneNamesIsLinkedByTheToolAndMergedOnTheBuildCopyOnly()
    {
        var shirt = Clothing("Shirt", new[] { ("hips", "", ""), ("spine", "hips", ""), ("chest", "spine", ""), ("shoulder.L", "chest", ""), ("upper_arm.L", "shoulder.L", ""), ("Hood string", "chest", "") });
        var plan = AttachmentPlanner.Analyze(shirt, avatar.transform);
        Assert.AreEqual(OrbitersAttachment.AttachMode.Merge, plan.Mode);
        var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        var link = attachment.links.Single(l => l.from.name == "upper_arm.L");
        Assert.AreSame(bones["Left arm"], link.to);
        Assert.False(attachment.links.Any(l => l.from.name == "Hood string"), "an extra bone follows its parent, it is not linked");

        // The scene keeps the accessory as it is; on the build copy its bones follow the avatar's. Without VRCFury to fix
        // animation paths they stay where they are and follow with a constraint.
        var arm = shirt.GetComponentsInChildren<Transform>().Single(t => t.name == "upper_arm.L");
        Assert.AreNotSame(bones["Left arm"], arm.parent);
        var clone = Own(Object.Instantiate(avatar));
        AttachmentBuild.Apply(clone);
        var clonedArm = clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "upper_arm.L");
        Assert.True(clonedArm.IsChildOf(clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Left arm")));
        Assert.Null(clonedArm.GetComponent<VRCParentConstraint>());
        Assert.Less(Vector3.Distance(arm.position, clonedArm.position), 1e-4f, "rest offset kept");
        AttachmentBuild.Finish(clone);
        Assert.IsEmpty(clone.GetComponentsInChildren<OrbitersAttachment>(true));
    }

    [Test] public void WhenVrcFuryBuildsTheAvatarLinkedBonesMoveUnderTheirBoneAndTheirMeshesKeepTheBodyShapes()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        Smile(avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.name == "Body"));
        var shirt = Clothing("Shirt", new[] { ("hips", "", ""), ("spine", "hips", ""), ("chest", "spine", ""), ("shoulder.L", "chest", ""), ("upper_arm.L", "shoulder.L", "") });
        // A mesh below a bone: it leaves the accessory when the bone moves.
        var chest = shirt.GetComponentsInChildren<Transform>().Single(t => t.name == "chest");
        var badge = Smile(Skin("Badge", chest, new[] { chest }));
        // With its menu toggle, a VRCFury component: VRCFury builds this avatar.
        var attachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(shirt, avatar.transform), new AttachmentOptions { Created = true });
        Assert.AreEqual(OrbitersAttachment.AttachMode.Merge, attachment.mode);
        Assert.True(attachment.syncBlendShapes);

        var clone = Own(Object.Instantiate(avatar));
        AttachmentBuild.Apply(clone);
        var clonedArm = clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "upper_arm.L");
        Assert.AreEqual("Left arm", clonedArm.parent.name);
        Assert.Null(clonedArm.GetComponent<VRCParentConstraint>());
        var clonedBadge = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Badge");
        Assert.False(clonedBadge.transform.IsChildOf(clone.GetComponentInChildren<OrbitersAttachment>(true).transform), "moved out with its bone");
        clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Body").SetBlendShapeWeight(0, 70);
        AttachmentBuild.Finish(clone);
        Assert.AreEqual(70, clonedBadge.GetBlendShapeWeight(0), 1e-3f, "still synced with the body");
    }

    [Test] public void PropNamedAfterItsPlaceFollowsThatBoneAndIsPlacedOnItWhenFar()
    {
        var hat = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hat.name = "Cool Hat";
        hat.transform.SetParent(avatar.transform, false);
        var plan = AttachmentPlanner.Analyze(hat, avatar.transform);
        Assert.AreEqual(AttachmentKind.Rigid, plan.Kind);
        Assert.AreSame(bones["Head"], plan.Parent);
        Assert.True(plan.Snap, "modelled at the feet, far from the head");
        Assert.False(plan.ParentGuessed);
        var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        Assert.Less(Vector3.Distance(AttachmentPlanner.Bounds(hat).center, bones["Head"].position), 1e-4f);
        Assert.AreSame(bones["Head"], attachment.parent);

        var clone = Own(Object.Instantiate(avatar));
        AttachmentBuild.Apply(clone);
        var clonedHat = clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Cool Hat");
        Assert.True(clonedHat.IsChildOf(clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Head")));
        Assert.Null(clonedHat.GetComponent<VRCParentConstraint>());
    }

    [Test] public void EmptyConstraintsNamedAfterAvatarBonesAreWiredAsVrcConstraints()
    {
        var sticks = Own(new GameObject("Glowsticks"));
        sticks.transform.SetParent(avatar.transform, false);
        var wrist = Child("Left wrist", sticks.transform, new Vector3(.5f, 1.4f, 0));
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(wrist, false);
        wrist.gameObject.AddComponent<ParentConstraint>();
        var odd = Child("Sparkle", sticks.transform, Vector3.zero);
        odd.gameObject.AddComponent<ParentConstraint>();

        var plan = AttachmentPlanner.Analyze(sticks, avatar.transform);
        Assert.AreEqual(2, plan.EmptyConstraints.Count);

        var before = wrist.position;
        AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        var converted = wrist.GetComponent<VRCParentConstraint>();
        Assert.NotNull(converted);
        Assert.Null(wrist.GetComponent<ParentConstraint>());
        Assert.AreSame(bones["Left wrist"], converted.Sources[0].SourceTransform);
        Assert.Less(Vector3.Distance(before, wrist.position), 1e-4f);
        Assert.AreSame(bones["Hips"], odd.GetComponent<VRCParentConstraint>().Sources[0].SourceTransform, "a constraint no bone name explains follows the hips");
    }

    [Test] public void EmptyVrcPositionAndRotationConstraintsAreWiredAndOtherKindsAreLeftWithANote()
    {
        var sticks = Own(new GameObject("Glowsticks"));
        sticks.transform.SetParent(avatar.transform, false);
        var head = Child("Head", sticks.transform, new Vector3(0, 1.7f, .1f));
        head.gameObject.AddComponent<VRCPositionConstraint>();
        var wrist = Child("Left wrist", sticks.transform, new Vector3(.5f, 1.4f, 0));
        wrist.rotation = Quaternion.Euler(0, 40, 0);
        wrist.gameObject.AddComponent<VRCRotationConstraint>();
        var aim = Child("Chest", sticks.transform, Vector3.zero);
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(aim, false);
        aim.gameObject.AddComponent<AimConstraint>();

        var plan = AttachmentPlanner.Analyze(sticks, avatar.transform);
        CollectionAssert.AreEquivalent(new Component[] { head.GetComponent<VRCPositionConstraint>(), wrist.GetComponent<VRCRotationConstraint>() }, plan.EmptyConstraints);
        Assert.True(plan.Notes.Any(n => n.Target == aim.GetComponent<AimConstraint>()), "an aim constraint does not attach anything: the user sets it");
        Assert.AreEqual(AttachmentKind.Rigid, plan.Kind, "the aimed cube is attached like any prop");

        AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        var position = head.GetComponent<VRCPositionConstraint>();
        Assert.AreSame(bones["Head"], position.Sources[0].SourceTransform);
        Assert.True(position.IsActive);
        Assert.Less(Vector3.Distance(head.position - bones["Head"].position, position.PositionOffset), 1e-4f, "keeps where it stands");
        var rotation = wrist.GetComponent<VRCRotationConstraint>();
        Assert.AreSame(bones["Left wrist"], rotation.Sources[0].SourceTransform);
        Assert.Less(Quaternion.Angle(bones["Left wrist"].rotation * Quaternion.Euler(rotation.RotationOffset), wrist.rotation), .01f);
        Assert.AreEqual(0, aim.GetComponent<AimConstraint>().sourceCount);
    }

    [Test] public void AnimatedUnityConstraintsAreWiredButNotConvertedSoTheirAnimationsKeepWorking()
    {
        var sticks = Own(new GameObject("Glowsticks"));
        sticks.transform.SetParent(avatar.transform, false);
        var left = Child("Left wrist", sticks.transform, new Vector3(.5f, 1.4f, 0));
        left.gameObject.AddComponent<ParentConstraint>();
        var right = Child("Right wrist", sticks.transform, new Vector3(-.5f, 1.4f, 0));
        right.gameObject.AddComponent<ParentConstraint>();
        // A world drop: the avatar's animation turns the left constraint off.
        var clip = Own(new AnimationClip { legacy = true });
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Glowsticks/Left wrist", typeof(ParentConstraint), "m_Active"), AnimationCurve.Constant(0, 1, 0));
        avatar.AddComponent<Animation>().clip = clip;

        var plan = AttachmentPlanner.Analyze(sticks, avatar.transform);
        AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        var kept = left.GetComponent<ParentConstraint>();
        Assert.NotNull(kept, "converting would destroy what the animation drives");
        Assert.AreSame(bones["Left wrist"], kept.GetSource(0).sourceTransform);
        Assert.Null(left.GetComponent<VRCParentConstraint>());
        Assert.True(plan.Notes.Any(n => n.Target == kept));
        Assert.Null(right.GetComponent<ParentConstraint>());
        Assert.AreSame(bones["Right wrist"], right.GetComponent<VRCParentConstraint>().Sources[0].SourceTransform);
    }

    [Test] public void OnlyActiveConstraintsFollowingTheAvatarMakeAnAccessoryConfigured()
    {
        Transform Hat(string name, out Transform mesh)
        {
            var hat = Own(new GameObject(name)).transform;
            hat.SetParent(avatar.transform, false);
            mesh = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
            mesh.SetParent(hat, false);
            return hat;
        }
        ParentConstraint Constrain(Transform t, Transform source)
        {
            var constraint = t.gameObject.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = source, weight = 1 });
            constraint.constraintActive = true;
            return constraint;
        }

        // Held by a locator of its own: nothing attaches it to the avatar yet.
        var inside = Hat("Party Hat", out var insideMesh);
        Constrain(insideMesh, Child("Locator", inside, Vector3.up));
        var plan = AttachmentPlanner.Analyze(inside.gameObject, avatar.transform);
        Assert.AreEqual(AttachmentKind.Rigid, plan.Kind);
        Assert.AreSame(bones["Head"], plan.Parent);

        var off = Hat("Sun Hat", out var offMesh);
        Constrain(offMesh, bones["Head"]).enabled = false;
        Assert.AreEqual(AttachmentKind.Rigid, AttachmentPlanner.Analyze(off.gameObject, avatar.transform).Kind, "a disabled constraint attaches nothing");

        var on = Hat("Top Hat", out var onMesh);
        Constrain(onMesh, bones["Head"]);
        Assert.AreEqual(AttachmentKind.Configured, AttachmentPlanner.Analyze(on.gameObject, avatar.transform).Kind);
    }

    [Test] public void AmbiguousBonesAreLeftForAiOrTheUserWithTheirCandidates()
    {
        var headPin = Child("Pin", bones["Head"], new Vector3(0, .05f, .1f));
        var chestPin = Child("Pin", bones["Chest"], new Vector3(0, .05f, .1f));
        Child("Tip", headPin, Vector3.up * .02f);
        Child("Tip", chestPin, Vector3.up * .02f);
        var clothing = Clothing("Brooch", new[] { ("hips", "", ""), ("Pin", "hips", ""), ("Tip", "Pin", "") });

        var plan = AttachmentPlanner.Analyze(clothing, avatar.transform);
        var pin = clothing.GetComponentsInChildren<Transform>().Single(t => t.name == "Pin");
        var tip = clothing.GetComponentsInChildren<Transform>().Single(t => t.name == "Tip");
        CollectionAssert.AreEquivalent(new[] { pin, tip }, plan.Ambiguous.Select(m => m.Source), "the tip only matched below the pin's guess");
        Assert.False(plan.Matches.Any(m => m.Matched && (m.Source == pin || m.Source == tip)));
        Assert.AreEqual(OrbitersAttachment.AttachMode.Merge, plan.Mode, "AI answers become the tool's own links");
        var note = plan.Notes.Single(n => n.Target == pin.gameObject).Reason;
        StringAssert.Contains("Head/Pin", note);
        StringAssert.Contains("Chest/Pin", note);

        var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, AddToggle = false });
        Assert.False(attachment.links.Any(l => l.from == pin || l.from == tip), "no guess is installed");
        Assert.True(attachment.links.Any(l => l.to == bones["Hips"]));
    }

    [Test] public void VrcFuryLinksPreviewWhereVrcFuryPutsThem()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        var socket = Child("Socket", bones["Head"], new Vector3(0, .1f, .05f));
        socket.localRotation = Quaternion.Euler(0, 90, 0);
        socket.localScale = Vector3.one * 2;
        var accessory = Own(new GameObject("Horns"));
        accessory.transform.SetParent(avatar.transform, false);
        var prop = Child("Horns bone", accessory.transform, new Vector3(.3f, 1.2f, 0));
        prop.localRotation = Quaternion.Euler(10, 0, 0);
        var model = VrcFury.Content(VrcFury.Writer.ArmatureLink(accessory, prop.gameObject, HumanBodyBones.Head, null));
        Set(model, "recursive", false);
        Set(model, "alignPosition", true);
        Set(model, "alignRotation", false);
        Set(model, "alignScale", true);
        var targets = (IList)VrcFury.Field(model, "linkTo");
        targets.Clear();
        // This avatar has no upper chest: VRCFury uses the next target, the head's Socket child.
        targets.Add(LinkTo(targets, HumanBodyBones.UpperChest, ""));
        targets.Add(LinkTo(targets, HumanBodyBones.Head, "Socket"));

        var link = AttachmentFollow.Links(avatar.transform).Single(l => l.Follower == prop);
        Assert.AreSame(socket, link.Target);
        var relative = Quaternion.Inverse(socket.rotation) * prop.rotation;
        bones["Neck"].localRotation = Quaternion.Euler(30, 0, 0);
        link.Apply();
        Assert.Less(Vector3.Distance(socket.position, prop.position), 1e-4f, "position aligned");
        Assert.Less(Quaternion.Angle(socket.rotation * relative, prop.rotation), .01f, "rotation not aligned: kept from the socket");
        Assert.Less(Vector3.Distance(socket.lossyScale, prop.lossyScale), 1e-4f, "scale aligned");
    }

    [Test] public void VrcFuryUnalignedScaleFollowsTargetScaleWithoutSnapping()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        var accessory = Own(new GameObject("Horns"));
        accessory.transform.SetParent(avatar.transform, false);
        var prop = Child("Horns bone", accessory.transform, new Vector3(.3f, 1.2f, 0));
        prop.localScale = new Vector3(2, 3, 4);
        var model = VrcFury.Content(VrcFury.Writer.ArmatureLink(accessory, prop.gameObject, HumanBodyBones.Head, null));
        Set(model, "recursive", false);
        Set(model, "alignScale", false);
        Set(model, "forceOneWorldScale", false);
        var before = prop.lossyScale;
        var link = AttachmentFollow.Links(avatar.transform).Single(l => l.Follower == prop);
        link.Apply();
        Assert.Less(Vector3.Distance(before, prop.lossyScale), 1e-4f, "not aligning scale preserves the authored size");
        link.Target.localScale *= 2;
        link.Apply();
        Assert.Less(Vector3.Distance(before * 2, prop.lossyScale), 1e-4f, "VRCFury reparents the prop, so subsequent target scale is inherited");
    }

    [Test] public void PosePreviewFollowsTheAvatarAndPutsEverythingBack()
    {
        var shirt = Clothing("Shirt", new[] { ("hips", "", ""), ("spine", "hips", ""), ("chest", "spine", ""), ("shoulder.L", "chest", ""), ("upper_arm.L", "shoulder.L", "") });
        AttachmentInstaller.Install(AttachmentPlanner.Analyze(shirt, avatar.transform), new AttachmentOptions { Created = true, AddToggle = false });
        var arm = shirt.GetComponentsInChildren<Transform>().Single(t => t.name == "upper_arm.L");
        var rest = arm.position;
        var offset = bones["Left arm"].InverseTransformPoint(arm.position);
        Assert.True(AccessoryPoseSync.Enable(avatar.transform), AccessoryPoseSync.LastStatus);
        bones["Left shoulder"].localRotation = Quaternion.Euler(0, 0, 50);
        AccessoryPoseSync.Sync();
        Assert.Less(Vector3.Distance(bones["Left arm"].TransformPoint(offset), arm.position), 1e-4f);
        AccessoryPoseSync.Disable();
        Assert.Less(Vector3.Distance(rest, arm.position), 1e-4f);
    }

    [Test] public void RemovingAnAccessoryTheToolPlacedDeletesIt()
    {
        var hat = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hat.name = "Hat";
        hat.transform.SetParent(avatar.transform, false);
        var attachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(hat, avatar.transform), new AttachmentOptions { Created = true, AddToggle = false });
        AttachmentInstaller.Remove(attachment);
        Assert.True(hat == null);
    }

    [Test] public void ConstraintsOfAnObjectTheUserPlacedStayTheirsInTheSceneAndGetTheirSourcesBackOnRemoval()
    {
        var sticks = Own(new GameObject("Glowsticks"));
        sticks.transform.SetParent(avatar.transform, false);
        var wrist = Child("Left wrist", sticks.transform, new Vector3(.5f, 1.4f, 0));
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(wrist, false);
        var constraint = wrist.gameObject.AddComponent<ParentConstraint>();
        constraint.weight = .5f;

        var attachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(sticks, avatar.transform), new AttachmentOptions { Created = false, AddToggle = false });
        Assert.AreSame(constraint, wrist.GetComponent<ParentConstraint>(), "not replaced in the scene");
        Assert.Null(wrist.GetComponent<VRCParentConstraint>());
        Assert.AreSame(bones["Left wrist"], constraint.GetSource(0).sourceTransform);
        CollectionAssert.Contains(attachment.convertAtBuild, constraint);

        // The build copy gets a VRChat constraint on the avatar's bone.
        var clone = Own(Object.Instantiate(avatar));
        AttachmentBuild.Apply(clone);
        Assert.IsEmpty(clone.GetComponentsInChildren<ParentConstraint>(true));
        var built = clone.GetComponentsInChildren<VRCParentConstraint>(true).Single();
        Assert.AreEqual("Left wrist", built.Sources[0].SourceTransform.name);
        Assert.True(built.Sources[0].SourceTransform.IsChildOf(clone.transform.Find("Armature")));
        AttachmentBuild.Finish(clone);

        AttachmentInstaller.Remove(attachment);
        Assert.AreEqual(0, constraint.sourceCount);
        Assert.AreEqual(.5f, constraint.weight, 1e-4f);
        Assert.False(constraint.constraintActive);
        Assert.False(constraint.locked);
        Assert.Null(sticks.GetComponent<OrbitersAttachment>());
        Assert.NotNull(wrist.GetComponent<ParentConstraint>());
    }

    [Test] public void RemovalPutsBackOnlyWhatTheUserDidNotChangeSince()
    {
        var sticks = Own(new GameObject("Glowsticks"));
        sticks.transform.SetParent(avatar.transform, false);
        var wrist = Child("Left wrist", sticks.transform, new Vector3(.5f, 1.4f, 0));
        GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(wrist, false);
        var constraint = wrist.gameObject.AddComponent<ParentConstraint>();
        var hat = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hat.name = "Cool Hat";
        hat.transform.SetParent(avatar.transform, false);
        hat.transform.localPosition = new Vector3(0, 0, .3f);

        var sticksAttachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(sticks, avatar.transform), new AttachmentOptions { Created = false, AddToggle = false });
        var hatAttachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(hat, avatar.transform), new AttachmentOptions { Created = false, AddToggle = false });
        Assert.Less(Vector3.Distance(AttachmentPlanner.Bounds(hat).center, bones["Head"].position), 1e-4f, "placed on the head");

        // The user changes the constraint's weight; the hat stays where the tool put it.
        constraint.weight = .3f;
        AttachmentInstaller.Remove(sticksAttachment);
        AttachmentInstaller.Remove(hatAttachment);
        Assert.AreSame(bones["Left wrist"], constraint.GetSource(0).sourceTransform, "changed since: the user's now");
        Assert.AreEqual(.3f, constraint.weight, 1e-4f);
        Assert.Less(Vector3.Distance(hat.transform.localPosition, new Vector3(0, 0, .3f)), 1e-4f, "back where the user had it");

        // Moved by the user after the tool placed it: it stays where the user put it.
        var again = AttachmentInstaller.Install(AttachmentPlanner.Analyze(hat, avatar.transform), new AttachmentOptions { Created = false, AddToggle = false });
        hat.transform.position += Vector3.right * .1f;
        var moved = hat.transform.localPosition;
        AttachmentInstaller.Remove(again);
        Assert.Less(Vector3.Distance(hat.transform.localPosition, moved), 1e-4f);
    }

    [Test] public void RemovalPutsBackTheBlendshapeWeightsCopiedFromTheBody()
    {
        var body = Smile(avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.name == "Body"));
        body.SetBlendShapeWeight(0, 70);
        var shirt = Clothing("Shirt", new[] { ("hips", "", ""), ("spine", "hips", ""), ("chest", "spine", "") });
        var mesh = Smile(shirt.GetComponentInChildren<SkinnedMeshRenderer>());
        mesh.SetBlendShapeWeight(0, 10);

        var attachment = AttachmentInstaller.Install(AttachmentPlanner.Analyze(shirt, avatar.transform), new AttachmentOptions { Created = false, AddToggle = false });
        Assert.AreEqual(70, mesh.GetBlendShapeWeight(0), 1e-3f, "shows the body's shapes right away");
        AttachmentInstaller.Remove(attachment);
        Assert.AreEqual(10, mesh.GetBlendShapeWeight(0), 1e-3f);
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    private static SkinnedMeshRenderer Smile(SkinnedMeshRenderer renderer)
    {
        var mesh = renderer.sharedMesh;
        mesh.AddBlendShapeFrame("Smile", 100, new Vector3[mesh.vertexCount], null, null);
        renderer.sharedMesh = null;
        renderer.sharedMesh = mesh;
        return renderer;
    }

    // VRCFury's models are internal: set through reflection, as a creator would in its inspector.
    private static void Set(object model, string field, object value) =>
        model.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(model, value);

    private static object LinkTo(IList targets, HumanBodyBones bone, string offset)
    {
        var target = Activator.CreateInstance(targets.GetType().GetGenericArguments()[0]);
        Set(target, "useBone", true);
        Set(target, "bone", bone);
        Set(target, "offset", offset);
        return target;
    }

    private Transform Child(string name, Transform parent, Vector3 local)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.localPosition = local;
        return child;
    }

    private Transform Bone(string name, Transform parent, Vector3 local) => bones[name] = Child(name, parent, local);

    // A clothing object with its own armature, placed like the avatar's bones of the same role, skinned to all of it.
    private GameObject Clothing(string name, (string bone, string parent, string _)[] chain)
    {
        var root = Own(new GameObject(name));
        root.transform.SetParent(avatar.transform, false);
        var armature = Child("Armature", root.transform, Vector3.zero);
        var made = new Dictionary<string, Transform>();
        foreach (var (bone, parent, _) in chain)
        {
            var match = bones.Values.FirstOrDefault(b => Orbiters.Toolkit.Armature.BoneNames.Normalize(b.name) == Orbiters.Toolkit.Armature.BoneNames.Normalize(bone));
            var t = Child(bone, parent == "" ? armature : made[parent], Vector3.zero);
            t.position = (match != null ? match.position : made[parent].position + Vector3.up * .05f) + new Vector3(0, 0, .02f);
            made[bone] = t;
        }
        Skin(name + " mesh", root.transform, made.Values.ToArray());
        return root;
    }

    private SkinnedMeshRenderer Skin(string name, Transform parent, Transform[] skinBones)
    {
        var renderer = Child(name, parent, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(new Mesh());
        var vertices = skinBones.Select(b => renderer.transform.InverseTransformPoint(b.position)).ToList();
        while (vertices.Count < 3) vertices.Add(vertices[0] + Vector3.right * .01f * vertices.Count);
        mesh.SetVertices(vertices);
        mesh.SetTriangles(Enumerable.Range(0, vertices.Count - vertices.Count % 3).ToArray(), 0);
        mesh.boneWeights = vertices.Select((_, i) => new BoneWeight { boneIndex0 = Mathf.Min(i, skinBones.Length - 1), weight0 = 1 }).ToArray();
        mesh.bindposes = skinBones.Select(b => b.worldToLocalMatrix * renderer.transform.localToWorldMatrix).ToArray();
        renderer.sharedMesh = mesh;
        renderer.bones = skinBones;
        renderer.rootBone = skinBones[0];
        return renderer;
    }
}
