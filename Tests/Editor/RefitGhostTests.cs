using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// The original base's see-through preview: over the avatar's body, made of hidden copies of the original's objects (its
/// own bones, its meshes), and the open scene never marked changed. Fixtures in a preview scene.
/// </summary>
public sealed class RefitGhostTests
{
    [Test]
    public void TheGhostLiesOverTheBodyWithoutChangingTheOpenScene()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var made = new List<Object>();
        RefitGhost ghost = null;
        try
        {
            var original = new GameObject("Original");
            SceneManager.MoveGameObjectToScene(original, scene);
            var hips = new GameObject("Hips").transform;
            hips.SetParent(original.transform, false);
            hips.localPosition = Vector3.up;
            var bodyObject = new GameObject("Body");
            bodyObject.transform.SetParent(original.transform, false);
            var body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = RefitRecordsTests.MakeMesh("Ghost body");
            made.Add(mesh);
            body.sharedMesh = mesh;
            body.bones = new[] { hips };
            body.rootBone = hips;
            var avatar = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(avatar, scene);
            avatar.transform.position = new Vector3(2f, 0f, 0f);
            var avatarBodyObject = new GameObject("Body");
            avatarBodyObject.transform.SetParent(avatar.transform, false);
            var avatarBody = avatarBodyObject.AddComponent<SkinnedMeshRenderer>();

            var open = SceneManager.GetActiveScene();
            bool wasDirty = open.isDirty;
            ghost = RefitGhost.Show(original, body, avatarBody);
            if (!wasDirty) Assert.That(open.isDirty, Is.False, "The open scene is not marked changed.");
            Assert.That(ghost.Root.GetComponentsInChildren<Transform>(true).All(t => t.gameObject.hideFlags == HideFlags.HideAndDontSave), Is.True,
                "Every object of the preview is hidden and never saved.");
            Assert.That(ghost.Hips, Is.Not.Null);
            Assert.That(ghost.Hips.name, Is.EqualTo("Hips"));
            var ghostBody = ghost.Root.Find("Body");
            Assert.That((ghostBody.position - avatarBody.transform.position).magnitude, Is.LessThan(1e-5f), "Its body lies where the avatar's is.");
            var renderer = ghostBody.GetComponent<SkinnedMeshRenderer>();
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.bones.Single(), Is.SameAs(ghost.Root.Find("Hips")), "Skinned to its own copies of the bones.");
            Assert.That(original.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(3), "The original is left as it was.");
        }
        finally
        {
            ghost?.Dispose();
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
