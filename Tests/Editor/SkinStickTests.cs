using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

public sealed class SkinStickTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject avatar;
    private Transform hips, spine, chest;
    private SkinnedMeshRenderer body;

    // A strip of skin in front of the torso: the row at 1.2 m blends Spine and Chest half and half.
    [SetUp] public void SetUp()
    {
        // The fixture lives in a preview scene, never in the user's open scene.
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Skin stick test avatar");
        SceneManager.MoveGameObjectToScene(avatar, scene);
        avatar.AddComponent<VRCAvatarDescriptor>();
        var armature = Child("Armature", avatar.transform, Vector3.zero);
        hips = Child("Hips", armature, new Vector3(0, 1f, 0));
        spine = Child("Spine", hips, new Vector3(0, .1f, 0));
        chest = Child("Chest", spine, new Vector3(0, .2f, 0));
        body = Child("Body", avatar.transform, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
        var bones = new[] { hips, spine, chest };
        var rows = new[] { (y: 1.1f, spine: 1f), (y: 1.2f, spine: .5f), (y: 1.3f, spine: 0f) };
        var vertices = new List<Vector3>();
        var weights = new List<BoneWeight>();
        foreach (var row in rows)
            foreach (float x in new[] { -.1f, .1f })
            {
                vertices.Add(new Vector3(x, row.y, .1f));
                weights.Add(new BoneWeight { boneIndex0 = 1, weight0 = row.spine, boneIndex1 = 2, weight1 = 1f - row.spine });
            }
        var mesh = Own(new Mesh());
        mesh.SetVertices(vertices);
        mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5 }, 0);
        mesh.boneWeights = weights.ToArray();
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * body.transform.localToWorldMatrix).ToArray();
        body.sharedMesh = mesh;
        body.bones = bones;
        body.rootBone = hips;
    }

    [TearDown] public void TearDown()
    {
        foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        if (avatar != null) Object.DestroyImmediate(avatar);
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test] public void ASmallAccessoryOnTheSkinOffersToStickBlendedBetweenTheBonesThere()
    {
        var piercing = Piercing(new Vector3(0, 1.2f, .1f), .01f);
        var option = AttachmentHooks.Options(piercing.gameObject).Single(o => o.Label == SkinStick.Label);
        Assert.True(option.IsToggle);
        Assert.AreEqual(0, option.Current);

        var spot = SkinStick.Find(piercing, out string reason);
        Assert.NotNull(spot, reason);
        Assert.AreSame(body, spot.Skin);
        CollectionAssert.AreEquivalent(new[] { spine, chest }, spot.Bones);
        Assert.AreEqual(.5f, spot.Weights[0], .02f);
        Assert.AreEqual(.5f, spot.Weights[1], .02f);
        Assert.AreEqual(.005f, spot.Gap, .001f, "the cube's corners are half its size from the skin it goes through");

        option.Choose(1);
        Assert.True(piercing.stickToSkin);
    }

    [Test] public void ALargeAccessoryOrOneAwayFromTheSkinIsNotOffered()
    {
        var away = Piercing(new Vector3(0, 1.2f, .3f), .01f);
        Assert.IsNull(SkinStick.Find(away, out string reason));
        StringAssert.Contains("from the skin", reason);
        Assert.False(AttachmentHooks.Options(away.gameObject).Any(o => o.Label == SkinStick.Label));

        var big = Piercing(new Vector3(0, 1.2f, .1f), .4f);
        Assert.IsNull(SkinStick.Find(big, out reason));
        StringAssert.Contains("larger", reason);

        // Once on, the switch stays to turn it off.
        away.stickToSkin = true;
        Assert.True(AttachmentHooks.Options(away.gameObject).Any(o => o.Label == SkinStick.Label));
    }

    [Test] public void ClothingSkinnedToTheAvatarsBonesIsNotOffered()
    {
        var piercing = Piercing(new Vector3(0, 1.2f, .1f), .01f);
        var skinned = Child("Chain", piercing.transform, Vector3.zero).gameObject.AddComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = piercing.GetComponent<MeshFilter>().sharedMesh;
        skinned.bones = new[] { chest };
        Assert.IsNull(SkinStick.Find(piercing, out string reason));
        StringAssert.Contains("skinned", reason);
    }

    // Built, the accessory stays put, then goes where skinning takes the skin under it when the torso bends, also when
    // a source bone is scaled (the constraint's offsets leave the scale out).
    [TestCase(1f), TestCase(2f)]
    public void TheBuildConstraintKeepsItInPlaceThenFollowsTheSkinLikeSkinning(float spineScale)
    {
        spine.localScale = Vector3.one * spineScale;
        chest.localScale = Vector3.one / spineScale;
        // The skin rests where it was: bound to the scaled bones.
        body.sharedMesh.bindposes = body.bones.Select(b => b.worldToLocalMatrix * body.transform.localToWorldMatrix).ToArray();
        var point = new Vector3(0, 1.2f, .1f);
        var piercing = Piercing(point, .01f);
        piercing.stickToSkin = true;
        var rest = new[] { spine, chest }.ToDictionary(b => b, b => b.localToWorldMatrix);
        Assert.NotNull(SkinStick.Apply(avatar));
        var constraint = piercing.GetComponent<VRCParentConstraint>();
        Assert.NotNull(constraint);
        Assert.AreEqual(2, constraint.Sources.Count);
        Assert.NotNull(piercing.GetComponent<OrbitersSurfaceFollow>(), "it follows the body's blendshapes too");

        var solver = new ConstraintSolver();
        try
        {
            solver.Track(avatar);
            solver.Solve();
            Assert.Less(Vector3.Distance(point, piercing.transform.position), .0005f, "the constraint keeps it where it stands");

            chest.localRotation = Quaternion.Euler(40, 0, 0);
            spine.localRotation = Quaternion.Euler(10, 0, 0);
            solver.Solve();
            // Linear blend skinning of the skin point under it.
            var skinned = Vector3.zero;
            foreach (var bone in rest.Keys) skinned += (bone.localToWorldMatrix * rest[bone].inverse).MultiplyPoint3x4(point) * .5f;
            Assert.Less(Vector3.Distance(skinned, piercing.transform.position), .002f, "it follows the skin, " + skinned + " vs " + piercing.transform.position);
            Assert.Greater(Vector3.Distance(point, piercing.transform.position), .01f, "the bend moved it");
        }
        finally { solver.Release(); }
    }

    // The posing preview blends the same bones the build's constraint does.
    [Test] public void ThePreviewBlendsTheSameBonesAsTheBuild()
    {
        var piercing = Piercing(new Vector3(0, 1.2f, .1f), .01f);
        piercing.stickToSkin = true;
        var link = AttachmentFollow.Links(avatar.transform, false).Single(l => l.Follower == piercing.transform);
        Assert.NotNull(link.Blend);
        Assert.AreEqual(2, link.Blend.Length);

        var copy = Object.Instantiate(avatar);
        SceneManager.MoveGameObjectToScene(copy, scene);
        owned.Add(copy);
        Assert.NotNull(SkinStick.Apply(copy));
        var solver = new ConstraintSolver();
        try
        {
            solver.Track(copy);
            foreach (var root in new[] { avatar.transform, copy.transform })
            {
                root.Find("Armature/Hips/Spine").localRotation = Quaternion.Euler(0, 0, 25);
                root.Find("Armature/Hips/Spine/Chest").localRotation = Quaternion.Euler(30, 10, 0);
            }
            link.Apply();
            solver.Solve();
            var built = copy.transform.Find(piercing.name);
            Assert.Less(Vector3.Distance(built.position, piercing.transform.position), .001f);
            Assert.Less(Quaternion.Angle(built.rotation, piercing.transform.rotation), .5f);
        }
        finally { solver.Release(); }
    }

    private OrbitersAttachment Piercing(Vector3 position, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = "Piercing " + owned.Count;
        go.transform.SetParent(avatar.transform, false);
        go.transform.position = position;
        go.transform.localScale = Vector3.one * size;
        var attachment = go.AddComponent<OrbitersAttachment>();
        attachment.mode = OrbitersAttachment.AttachMode.Parent;
        attachment.parent = chest;
        return attachment;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

    private static Transform Child(string name, Transform parent, Vector3 local)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = local;
        return t;
    }
}
