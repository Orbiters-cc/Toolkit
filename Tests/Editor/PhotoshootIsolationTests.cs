using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class PhotoshootIsolationTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene fixtures;

    [SetUp] public void SetUp() => fixtures = EditorSceneManager.NewPreviewScene();

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(fixtures);
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
    }

    [Test]
    public void SessionsOnlySeeTheirOwnAvatar()
    {
        var small = Avatar("Unlit/Color", Color.red, 1f);
        var large = Avatar("Unlit/Color", Color.blue, 3f);
        int scenes = SceneManager.sceneCount;
        using (var first = new PhotoshootService.LivePreviewSession())
        using (var second = new PhotoshootService.LivePreviewSession())
        {
            second.UpdatePreview(Request(large));
            var pixels = Own(first.Capture(Request(small))).GetPixels32();
            Assert.That(SceneManager.sceneCount, Is.EqualTo(scenes), "Photoshoots add no scene to the open ones.");
            Assert.That(pixels.Count(p => p.r > 200 && p.b < 50), Is.GreaterThan(0));
            Assert.That(pixels.Count(p => p.b > 200 && p.r < 50), Is.Zero, "The other photoshoot's avatar stands on the same stage spot.");
        }
    }

    [Test]
    public void StageLightsAndOpenSceneLightsStayApart()
    {
        var avatar = Avatar("Standard", Color.white, 1f);
        // A lit cube in the open scene and a camera on it, none of it saved or shown.
        var cube = Own(EditorUtility.CreateGameObjectWithHideFlags("Open scene cube", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer)));
        cube.transform.position = new Vector3(-5000f, 0f, -5000f);
        cube.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        cube.GetComponent<MeshRenderer>().sharedMaterial = Own(new Material(Shader.Find("Standard")));
        var camera = Own(EditorUtility.CreateGameObjectWithHideFlags("Open scene camera", HideFlags.HideAndDontSave, typeof(Camera))).GetComponent<Camera>();
        camera.enabled = false;
        // Facing -Z like the photoshoot camera, so both see the faces a light travelling towards -Z reaches.
        camera.transform.SetPositionAndRotation(cube.transform.position + Vector3.forward * 3f, Quaternion.LookRotation(Vector3.back));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.targetTexture = Own(new RenderTexture(16, 16, 24));
        var openScene = Render(camera);

        using (var session = new PhotoshootService.LivePreviewSession())
        {
            var alone = Own(session.Capture(Request(avatar))).GetPixels32();
            Assert.That(Render(camera), Is.EqualTo(openScene), "Stage lights do not light the open scene.");

            var light = Own(EditorUtility.CreateGameObjectWithHideFlags("Open scene light", HideFlags.HideAndDontSave, typeof(Light))).GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.red;
            light.intensity = 8f;
            light.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            Assert.That(Render(camera), Is.Not.EqualTo(openScene), "The probe sees open scene lights.");
            Assert.That(Own(session.Capture(Request(avatar))).GetPixels32(), Is.EqualTo(alone), "Open scene lights do not light the stage.");
        }
    }

    private static PhotoshootService.RenderRequest Request(GameObject avatar) => new PhotoshootService.RenderRequest
    {
        avatarRoot = avatar, shotKind = PhotoshootService.ShotKind.Thumbnail, width = 64, height = 64
    };

    private GameObject Avatar(string shader, Color color, float size)
    {
        var root = new GameObject("Photoshoot test avatar");
        SceneManager.MoveGameObjectToScene(root, fixtures);
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localScale = Vector3.one * size;
        body.GetComponent<Renderer>().sharedMaterial = Own(new Material(Shader.Find(shader)) { color = color });
        return root;
    }

    private Color32[] Render(Camera camera)
    {
        camera.Render();
        var previous = RenderTexture.active;
        RenderTexture.active = camera.targetTexture;
        var texture = Own(new Texture2D(16, 16, TextureFormat.RGBA32, false));
        texture.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
        RenderTexture.active = previous;
        return texture.GetPixels32();
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
