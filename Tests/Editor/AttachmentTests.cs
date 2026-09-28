using System.Collections.Generic;
using System.Linq;
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

        // The scene keeps the accessory as it is; the build copy gets its bones under the avatar's.
        var arm = shirt.GetComponentsInChildren<Transform>().Single(t => t.name == "upper_arm.L");
        Assert.AreNotSame(bones["Left arm"], arm.parent);
        var clone = Own(Object.Instantiate(avatar));
        AttachmentBuild.Apply(clone);
        var clonedArm = clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "upper_arm.L");
        Assert.AreEqual("Left arm", clonedArm.parent.name);
        Assert.Less(Vector3.Distance(arm.position, clonedArm.position), 1e-4f, "rest offset kept");
        AttachmentBuild.Finish(clone);
        Assert.IsEmpty(clone.GetComponentsInChildren<OrbitersAttachment>(true));
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
        Assert.AreEqual("Head", clone.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Cool Hat").parent.name);
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

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

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
