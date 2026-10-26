using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Pictures of models: every part where and as large as it shows.</summary>
public sealed class ModelThumbnailsTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;

    [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

    [TearDown]
    public void TearDown()
    {
        foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
        owned.Clear();
        EditorSceneManager.ClosePreviewScene(scene);
    }

    // An avatar's root pictured whole: a ball moved by its blendshapes only (N4N0's: a mesh 1 m across on an object scaled to
    // 5 cm) stays 5 cm beside the body, instead of a 1 m ball hiding it.
    [Test]
    public void ASkinnedMeshWithoutBonesShowsAtItsScale()
    {
        var root = Own(new GameObject("Avatar"));
        SceneManager.MoveGameObjectToScene(root, scene);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        body.AddComponent<MeshFilter>().sharedMesh = Own(Cube("Body"));
        body.AddComponent<MeshRenderer>().sharedMaterial = Own(Colour(Color.red));
        var ball = new GameObject("Ball");
        ball.transform.SetParent(root.transform, false);
        ball.transform.localPosition = new Vector3(0, 0.6f, 0);
        ball.transform.localScale = Vector3.one * 0.05f;
        var skinned = ball.AddComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = Own(Cube("Ball"));
        skinned.sharedMaterial = Own(Colour(Color.blue));

        var picture = Own(ModelThumbnails.Picture(root, false));
        Assert.That(picture, Is.Not.Null);
        int red = 0, blue = 0;
        foreach (var pixel in picture.GetPixels())
        {
            if (pixel.r > 0.6f && pixel.g < 0.3f && pixel.b < 0.3f) red++;
            else if (pixel.b > 0.6f && pixel.r < 0.3f && pixel.g < 0.3f) blue++;
        }
        Assert.That(red, Is.GreaterThan(picture.width * picture.height / 5), "The body fills the picture.");
        Assert.That(blue, Is.LessThan(red / 50), "The ball is a speck beside it (" + blue + " pixels against the body's " + red + ").");
    }

    private static Material Colour(Color colour) => new Material(Shader.Find("Unlit/Color")) { color = colour, hideFlags = HideFlags.HideAndDontSave };

    // A cube 1 m across, centred on its object.
    private static Mesh Cube(string name)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int axis = 0; axis < 3; axis++)
        for (int sign = -1; sign <= 1; sign += 2)
        {
            var normal = Vector3.zero; normal[axis] = sign;
            var u = Vector3.zero; u[(axis + 1) % 3] = 1;
            var v = Vector3.Cross(normal, u);
            int start = vertices.Count;
            vertices.Add((normal - u - v) * 0.5f); vertices.Add((normal + u - v) * 0.5f);
            vertices.Add((normal + u + v) * 0.5f); vertices.Add((normal - u + v) * 0.5f);
            triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
        }
        var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private T Own<T>(T item) where T : Object
    {
        owned.Add(item);
        return item;
    }
}
