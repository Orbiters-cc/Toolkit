using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class AttachmentVolumeFitTests
{
    private Scene scene;
    private readonly List<Object> owned = new List<Object>();
    [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();
    [TearDown] public void TearDown()
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var component in root.GetComponentsInChildren<Component>(true)) if (component) Undo.ClearUndo(component);
        foreach (var value in owned) if (value) Object.DestroyImmediate(value);
        owned.Clear();
        EditorSceneManager.ClosePreviewScene(scene);
    }
    private GameObject Root(string name)
    {
        var root = new GameObject(name);
        SceneManager.MoveGameObjectToScene(root, scene);
        return root;
    }
    private MeshCollider Body()
    {
        var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
        SceneManager.MoveGameObjectToScene(primitive, scene);
        var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
        var collider = Root("Body surface").AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        Object.DestroyImmediate(primitive);
        return collider;
    }

    [Test] public void ClearancePreservesSplitSeamsAndDoesNotMoveAlreadyClearFabric()
    {
        var body = Body();
        var vertices = new[] { new Vector3(.45f, 0, 0), new Vector3(.45f, .1f, 0), new Vector3(.45f, 0, .1f),
            new Vector3(.45f, 0, 0), new Vector3(.8f, 0, 0) };
        var original = (Vector3[])vertices.Clone();
        var origins = vertices.Select(v => new Vector3(0, v.y, v.z)).ToArray();
        var result = AttachmentVolumeFit.Expand(vertices, origins, new[] { 0, 1, 2, 3, 2, 1 }, body, 2, .02f);
        Assert.That(result[0].x, Is.EqualTo(.52f).Within(.001f));
        Assert.That(result[3], Is.EqualTo(result[0]), "UV seam copies remain coincident");
        Assert.That(result[4], Is.EqualTo(original[4]), "already clear disconnected fabric stays as authored");
        CollectionAssert.AreEqual(original, vertices, "the imported mesh data is read-only");
    }

    [Test] public void SleeveCannotExpandAgainstAnUnrelatedTorsoSurface()
    {
        var body = Body();
        var vertices = new[] { new Vector3(.45f, 0, 0), new Vector3(.45f, .1f, 0), new Vector3(.45f, 0, .1f) };
        var origins = vertices.Select(v => new Vector3(0, v.y, v.z)).ToArray();
        var result = AttachmentVolumeFit.Expand(vertices, origins, new[] { 0, 1, 2 }, body, 2, .02f,
            new[] { 2, 2, 2 }, Enumerable.Repeat(1, body.sharedMesh.triangles.Length / 3).ToArray());
        CollectionAssert.AreEqual(vertices, result, "foreign surfaces must not make sleeve spikes");
    }

    [Test] public void CancelRestoresOriginalMeshAndLeavesLaterManualReplacementAlone()
    {
        var root = Root("Jacket");
        var attachment = root.AddComponent<OrbitersAttachment>();
        attachment.fitted.Add(new OrbitersAttachment.FittedPose { transform = root.transform,
            before = new OrbitersAttachment.LocalPose { scale = Vector3.one, rotation = Quaternion.identity },
            after = new OrbitersAttachment.LocalPose { scale = Vector3.one, rotation = Quaternion.identity } });
        var before = new Mesh(); var after = new Mesh(); var manual = new Mesh();
        owned.AddRange(new Object[] { before, after, manual });
        var renderer = root.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = after;
        var other = Root("Manually changed").AddComponent<SkinnedMeshRenderer>(); other.sharedMesh = manual;
        attachment.fittedMeshes.Add(new OrbitersAttachment.FittedMesh { renderer = renderer, before = before, after = after });
        attachment.fittedMeshes.Add(new OrbitersAttachment.FittedMesh { renderer = other, before = before, after = after });
        var refitted = new Mesh(); owned.Add(refitted);
        var record = renderer.gameObject.AddComponent<OrbitersRefit>();
        record.original = RefitRecords.Capture(root.transform, renderer);
        record.mesh = refitted; renderer.sharedMesh = refitted;
        AttachmentFit.Cancel(attachment);
        Assert.AreSame(before, renderer.sharedMesh);
        Assert.True(record == null, "cancelling the initial fit also unwinds its dependent ReFit");
        Assert.AreSame(manual, other.sharedMesh);
        Assert.IsEmpty(attachment.fittedMeshes);
    }
}
