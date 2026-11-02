using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Which bindings find nothing to animate: a missing object, component or blendshape, from the object a clip plays from.
public sealed class BindingAuditTests
{
    private GameObject root;
    private Mesh mesh;
    private AnimationClip clip;

    [SetUp] public void SetUp()
    {
        root = new GameObject("Avatar");
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform);
        mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.zero, Vector3.up, Vector3.zero }, null, null);
        body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        var hat = new GameObject("Hat");
        hat.transform.SetParent(root.transform);
        // Two children named alike: Unity binds the first.
        new GameObject("Twin").transform.SetParent(root.transform);
        var twin = new GameObject("Twin");
        twin.transform.SetParent(root.transform);
        twin.AddComponent<MeshRenderer>();
        clip = new AnimationClip();
        void Curve(string path, System.Type type, string property) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 0, 1));
        Curve("Hat", typeof(GameObject), "m_IsActive");
        Curve("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile");
        Curve("Body", typeof(SkinnedMeshRenderer), "blendShape.Frown");
        Curve("Hat", typeof(MeshRenderer), "m_Enabled");
        Curve("Gone", typeof(GameObject), "m_IsActive");
        Curve("Twin", typeof(MeshRenderer), "m_Enabled");
        Curve("", typeof(Transform), "m_LocalPosition.x");
    }

    [TearDown] public void TearDown()
    {
        Object.DestroyImmediate(root);
        Object.DestroyImmediate(mesh);
        Object.DestroyImmediate(clip);
    }

    [Test] public void MissingObjectsComponentsAndBlendShapesAreListed()
    {
        var result = BindingAudit.Of(new[] { (clip, root.transform) });
        Assert.AreEqual(7, result.Bindings);
        var missing = result.Missing.Select(m => (m.Binding.path, m.Binding.propertyName, m.Problem)).ToList();
        CollectionAssert.AreEquivalent(new[]
        {
            ("Body", "blendShape.Frown", BindingAudit.Problem.NoBlendShape),
            ("Hat", "m_Enabled", BindingAudit.Problem.NoComponent),
            ("Gone", "m_IsActive", BindingAudit.Problem.NoObject),
            ("Twin", "m_Enabled", BindingAudit.Problem.NoComponent),
        }, missing);
    }

    [Test] public void PathsStartWhereTheClipIsPlayed()
    {
        var below = new GameObject("Holder");
        below.transform.SetParent(root.transform);
        root.transform.Find("Hat").SetParent(below.transform);
        var result = BindingAudit.Of(new[] { (clip, below.transform) });
        Assert.IsFalse(result.Missing.Any(m => m.Binding.path == "Hat" && m.Binding.propertyName == "m_IsActive"), "Holder/Hat is found from the holder");
        Assert.IsTrue(result.Missing.Any(m => m.Binding.path == "Body" && m.Problem == BindingAudit.Problem.NoObject), "the body is not below the holder");
        Assert.AreEqual(BindingAudit.Problem.NoObject, BindingAudit.ProblemOf(root.transform, EditorCurveBinding.FloatCurve("Hat", typeof(GameObject), "m_IsActive")));
    }
}
