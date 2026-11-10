using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// One look at an avatar shared by the tools: the same answers as deriving each fact again, kept until a change they
// depend on, posing aside.
public sealed class AvatarSnapshotTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        EditorChangeBus.Flush();
    }

    [TearDown] public void TearDown()
    {
        AvatarSnapshot.ForgetAll();
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
    }

    private GameObject Child(string name, GameObject parent = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent((parent ?? root).transform, false);
        return go;
    }

    private SkinnedMeshRenderer Skin(string name, int vertices, int shapes, GameObject parent = null)
    {
        var mesh = new Mesh { name = name };
        owned.Add(mesh);
        var points = Enumerable.Range(0, vertices).Select(i => new Vector3(i, 0, 0)).ToArray();
        mesh.vertices = points;
        for (int s = 0; s < shapes; s++) mesh.AddBlendShapeFrame("Shape " + s, 100, new Vector3[vertices], null, null);
        var skin = Child(name, parent).AddComponent<SkinnedMeshRenderer>();
        skin.sharedMesh = mesh;
        return skin;
    }

    // The body as AttachmentPlanner.Body picked it before the snapshot (no humanoid: any skin outside an attachment).
    private static SkinnedMeshRenderer OldBody(Transform avatarRoot, Transform exclude = null)
    {
        var skeleton = AvatarSkeleton.Bones(avatarRoot);
        return avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh != null && (exclude == null || !r.transform.IsChildOf(exclude)) &&
                        (skeleton.Count > 0 ? r.bones.Any(b => b != null && skeleton.Contains(b)) : r.GetComponentInParent<OrbitersAttachment>(true) == null))
            .OrderByDescending(r => r.name.Equals("Body", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.sharedMesh.blendShapeCount)
            .ThenByDescending(r => r.sharedMesh.vertexCount)
            .FirstOrDefault();
    }

    [Test] public void TheBodyIsTheOneTheOldSearchPicked()
    {
        Skin("Hair", 40, 2);
        var face = Skin("Face", 30, 9);
        Skin("Tie", 30, 9);
        var accessory = Child("Hat");
        accessory.AddComponent<OrbitersAttachment>();
        Skin("Hat mesh", 900, 60, accessory);

        Assert.AreSame(OldBody(root.transform), AttachmentPlanner.Body(root.transform));
        Assert.AreSame(face, AttachmentPlanner.Body(AvatarSnapshot.Take(root)), "most blendshapes, the first of a tie, accessories left out");
        Assert.AreSame(OldBody(root.transform, face.transform), AttachmentPlanner.Body(AvatarSnapshot.Take(root), face.transform), "with the best left out");

        var body = Skin("body", 10, 0);
        Assert.AreSame(body, AttachmentPlanner.Body(AvatarSnapshot.Take(root)), "named Body first");
        Assert.AreSame(OldBody(root.transform), AttachmentPlanner.Body(root.transform));
    }

    [Test] public void TheHierarchyTableAnswersLikeTheTransforms()
    {
        var a = Child("A");
        var b = Child("B", a);
        var c = Child("C");
        var snapshot = AvatarSnapshot.Take(root);
        CollectionAssert.AreEqual(root.GetComponentsInChildren<Transform>(true), snapshot.Transforms);
        foreach (var t in snapshot.Transforms)
        {
            Assert.AreEqual(AnimationUtility.CalculateTransformPath(t, root.transform), snapshot.PathOf(t));
            int index = snapshot.IndexOf(t);
            int parent = snapshot.ParentOf(index);
            Assert.AreSame(t == root.transform ? null : t.parent, parent < 0 ? null : snapshot.Transforms[parent]);
            foreach (var other in snapshot.Transforms)
                Assert.AreEqual(t.IsChildOf(other), snapshot.IsAtOrBelow(t, other), t.name + " under " + other.name);
        }
        Assert.AreEqual(-1, snapshot.IndexOf(new GameObject("Elsewhere").transform.Also(owned)));
        Assert.True(snapshot.IsAtOrBelow(b.transform, a.transform));
        Assert.False(snapshot.IsAtOrBelow(c.transform, a.transform));
    }

    [Test] public void ALiveSnapshotKeepsFactsUntilAChangeTheyDependOn()
    {
        var skin = Skin("Body", 10, 1);
        var bone = Child("Bone").transform;
        skin.sharedMesh.bindposes = new[] { Matrix4x4.identity };
        skin.bones = new[] { bone };
        var snapshot = AvatarSnapshot.Of(root);
        var bones = snapshot.SkinnedBones;
        var colliders = snapshot.All<BoxCollider>();
        Assert.AreSame(bones, snapshot.SkinnedBones, "nothing changed");

        // Posing changes no fact.
        bone.localPosition = Vector3.one;
        EditorChangeBus.Report(c => c.AddProperties(bone.GetInstanceID()));
        Assert.AreSame(bones, snapshot.SkinnedBones);

        // A renderer's properties (its bones) do.
        EditorChangeBus.Report(c => c.AddProperties(skin.GetInstanceID()));
        Assert.AreNotSame(bones, snapshot.SkinnedBones);
        Assert.AreSame(colliders, snapshot.All<BoxCollider>(), "the component lists depend on structure only");

        var added = bone.gameObject.AddComponent<BoxCollider>();
        EditorChangeBus.Report(c => c.AddStructure(bone.gameObject.GetInstanceID()));
        CollectionAssert.AreEqual(new[] { added }, snapshot.All<BoxCollider>());
        Assert.AreSame(snapshot, AvatarSnapshot.Of(root), "one per root");
    }

    [Test] public void ObjectsAddedWithoutTellingTheEditorAreNoticedAnyway()
    {
        var live = AvatarSnapshot.Of(root);
        var taken = AvatarSnapshot.Take(root);
        Assert.AreEqual(1, live.Transforms.Count);
        Assert.AreEqual(1, taken.Transforms.Count);
        Child("Added");
        Assert.AreEqual(2, live.Transforms.Count, "the hierarchy's size changed");
        Assert.AreEqual(1, taken.Transforms.Count, "a snapshot taken for one operation is never updated");
    }

    [Test] public void ChangesOutsideTheAvatarKeepItsFacts()
    {
        var other = new GameObject("Other");
        SceneManager.MoveGameObjectToScene(other, scene);
        var snapshot = AvatarSnapshot.Of(root);
        var transforms = snapshot.Transforms;
        EditorChangeBus.Report(c => { c.AddStructure(other.GetInstanceID()); c.AddProperties(other.GetInstanceID()); });
        Assert.AreSame(transforms, snapshot.Transforms);
        EditorChangeBus.Report(c => c.SetUndoRedo());
        Assert.AreNotSame(transforms, snapshot.Transforms, "an Undo may have changed anything");
    }
}

internal static class AvatarSnapshotTestExtensions
{
    public static T Also<T>(this T value, List<Object> owned) where T : Component
    {
        owned.Add(value.gameObject);
        return value;
    }
}
