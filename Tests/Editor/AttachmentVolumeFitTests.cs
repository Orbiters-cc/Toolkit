using System.Collections.Generic;
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
