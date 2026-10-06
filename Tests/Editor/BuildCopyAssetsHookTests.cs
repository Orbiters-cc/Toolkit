using System;
using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// The SDK saves the build copy as a prefab: meshes a build step made in memory would upload as nothing.
public sealed class BuildCopyAssetsHookTests
{
    private Scene scene;
    private GameObject avatar;
    private string prefab;
    private readonly List<Object> owned = new List<Object>();

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        avatar = new GameObject("Build copy");
        SceneManager.MoveGameObjectToScene(avatar, scene);
    }

    [TearDown] public void TearDown()
    {
        AttachmentAnimationBuild.Release(avatar);
        if (prefab != null) AssetDatabase.DeleteAsset(prefab);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        EditorSceneManager.ClosePreviewScene(scene);
    }

    [Test] public void InMemoryMeshesAndMaterialsSurviveThePrefabSaveAndAreReleasedAfter()
    {
        var skinned = Child("Body").AddComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = Triangle("Body (twist)");
        skinned.sharedMaterial = Own(new Material(Shader.Find("Standard")) { name = "Locked body" });
        var prop = Child("Prop");
        prop.AddComponent<MeshFilter>().sharedMesh = Triangle("Prop (follows body)");
        prop.AddComponent<MeshRenderer>();
        var gizmoMesh = Triangle("Gizmo");
        gizmoMesh.hideFlags = HideFlags.HideAndDontSave;
        Child("Gizmo").AddComponent<SkinnedMeshRenderer>().sharedMesh = gizmoMesh;

        Assert.That(new BuildCopyAssetsHook().OnPreprocessAvatar(avatar), Is.True);

        Assert.That(EditorUtility.IsPersistent(skinned.sharedMesh), Is.True);
        Assert.That(EditorUtility.IsPersistent(skinned.sharedMaterial), Is.True);
        Assert.That(EditorUtility.IsPersistent(prop.GetComponent<MeshFilter>().sharedMesh), Is.True);
        Assert.That(EditorUtility.IsPersistent(gizmoMesh), Is.False, "Editor-only objects stay out of the build.");
        prefab = "Assets/BuildCopyAssetsTest_" + Guid.NewGuid().ToString("N") + ".prefab";
        var saved = PrefabUtility.SaveAsPrefabAsset(avatar, prefab);
        var savedBody = saved.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        Assert.That(savedBody.sharedMesh, Is.Not.Null);
        Assert.That(savedBody.sharedMesh.vertexCount, Is.EqualTo(3));
        Assert.That(savedBody.sharedMaterial, Is.Not.Null);
        Assert.That(saved.transform.Find("Prop").GetComponent<MeshFilter>().sharedMesh, Is.Not.Null);

        string data = AssetDatabase.GetAssetPath(skinned.sharedMesh);
        AttachmentAnimationBuild.Release(avatar);
        Assert.That(AssetDatabase.LoadMainAssetAtPath(data), Is.Null, "Build data is deleted once the build copy is released.");
    }

    private GameObject Child(string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(avatar.transform, false);
        return child;
    }

    private Mesh Triangle(string name)
    {
        var mesh = Own(new Mesh { name = name, vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } });
        mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.boneWeights = new[] { new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 } };
        return mesh;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
