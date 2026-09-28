using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using Orbiters.Toolkit.Editor.VRChat.PhysBones;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

public sealed class AvatarSafetyRegressionTests
{
    private readonly List<Object> owned = new List<Object>();
    private GameObject root;
    [SetUp] public void SetUp() { root = Own(new GameObject("Toolkit regression avatar")); }
    [TearDown] public void TearDown()
    {
        AccessoryPoseSync.Disable();
        foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
    }
    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
    private Transform Child(string name, Transform parent) { var child = new GameObject(name).transform; child.SetParent(parent, false); return child; }

    [TestCase(false, 16)]
    [TestCase(true, 8)]
    public void IndependentControllersOnlyShareExplicitGlobals(bool global, int expected)
    {
        root.AddComponent<VRCAvatarDescriptor>();
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Shared", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true } };
        AddFullController(parameters, global ? new[] { "Shared" } : Array.Empty<string>());
        AddFullController(parameters, global ? new[] { "Shared" } : Array.Empty<string>());
        var budget = AvatarParameterBudget.Estimate(root);
        Assert.AreEqual(expected, budget.FullControllerBits);
        Assert.AreEqual(expected, budget.TotalBeforeCompression);
    }

    [Test] public void LocalControllerDoesNotMergeWithDescriptorParameter()
    {
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Shared", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true } };
        descriptor.customExpressions = true; descriptor.expressionParameters = parameters;
        AddFullController(parameters, Array.Empty<string>());
        Assert.AreEqual(16, AvatarParameterBudget.Estimate(root).TotalBeforeCompression);
    }

    [Test] public void NegativeGlobalRuleKeepsControllersIndependent()
    {
        root.AddComponent<VRCAvatarDescriptor>();
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Private", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true } };
        AddFullController(parameters, new[] { "*", "!Private" });
        AddFullController(parameters, new[] { "*", "!Private" });
        Assert.AreEqual(2, AvatarParameterBudget.Estimate(root).FullControllerBits);
    }

    [Test] public void CompressionStatusReadsActualGlobalModeWithoutChangingComponents()
    {
        root.AddComponent<VRCAvatarDescriptor>();
        const string key = "com.vrcfury.parameterCompressor";
        bool existed = EditorPrefs.HasKey(key); int old = EditorPrefs.GetInt(key);
        try
        {
            int count = root.GetComponentsInChildren<Component>(true).Length;
            EditorPrefs.SetInt(key, 0); StringAssert.Contains("automatically", AvatarParameterBudget.Estimate(root).CompressionStatus);
            EditorPrefs.SetInt(key, 1); StringAssert.Contains("asks", AvatarParameterBudget.Estimate(root).CompressionStatus);
            EditorPrefs.SetInt(key, 2); StringAssert.Contains("disabled", AvatarParameterBudget.Estimate(root).CompressionStatus);
            Assert.AreEqual(count, root.GetComponentsInChildren<Component>(true).Length);
        }
        finally { if (existed) EditorPrefs.SetInt(key, old); else EditorPrefs.DeleteKey(key); }
    }

    [Test] public void IgnoredPhysBoneBranchAndDescendantsRemainUnsimulated()
    {
        var hair = Child("Hair", root.transform);
        var ignored = Child("HairIgnored", hair);
        var tip = Child("HairIgnoredTip", ignored);
        var driven = Child("HairDriven", hair);
        var physics = root.AddComponent<VRCPhysBone>(); physics.rootTransform = hair; physics.ignoreTransforms = new List<Transform> { ignored };
        var method = typeof(PhysBoneParts).GetMethod("SimulatedTransforms", BindingFlags.Static | BindingFlags.NonPublic);
        var covered = (HashSet<Transform>)method.Invoke(null, new object[] { new[] { physics } });
        Assert.True(covered.Contains(hair)); Assert.True(covered.Contains(driven));
        Assert.False(covered.Contains(ignored)); Assert.False(covered.Contains(tip));
        var second = ignored.gameObject.AddComponent<VRCPhysBone>(); second.rootTransform = ignored;
        covered = (HashSet<Transform>)method.Invoke(null, new object[] { new[] { physics, second } });
        Assert.True(covered.Contains(ignored)); Assert.True(covered.Contains(tip));
    }

    [TestCase(2f, 2f, 2f)]
    [TestCase(2f, 3f, 0.5f)]
    public void AccessoryOffsetFollowsRootScaleAndNextPose(float x, float y, float z)
    {
        var armature = Child("Armature", root.transform);
        var avatarBone = Child("Hips", armature);
        var body = Child("Body", root.transform).gameObject.AddComponent<SkinnedMeshRenderer>();
        body.rootBone = avatarBone; body.bones = new[] { avatarBone };
        body.sharedMesh = Skin(body, avatarBone);
        var clothing = Child("Clothing", root.transform);
        var clothingBone = Child("Hips", clothing); clothingBone.position = new Vector3(0.2f, 0.4f, 0.1f);
        var clothRenderer = Child("ClothMesh", clothing).gameObject.AddComponent<SkinnedMeshRenderer>();
        clothRenderer.rootBone = clothingBone; clothRenderer.bones = new[] { clothingBone };
        clothRenderer.sharedMesh = Skin(clothRenderer, clothingBone);
        Assert.True(AccessoryPoseSync.Enable(root.transform, body), AccessoryPoseSync.LastStatus);
        var localOffset = avatarBone.InverseTransformPoint(clothingBone.position);
        root.transform.localScale = new Vector3(x, y, z);
        avatarBone.localRotation = Quaternion.Euler(0, 0, 35);
        AccessoryPoseSync.Sync(false);
        Assert.Less(Vector3.Distance(avatarBone.TransformPoint(localOffset), clothingBone.position), 0.0001f);
        avatarBone.localScale = new Vector3(1.4f, 0.8f, 1.1f);
        AccessoryPoseSync.Sync(false);
        Assert.Less(Vector3.Distance(avatarBone.TransformPoint(localOffset), clothingBone.position), 0.0001f);
    }

    [Test] public void PhotoshootRefreshesSourceAppearanceAndReusesUnchangedClone()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(root.transform, false);
        var originalMaterial = Own(new Material(cube.GetComponent<Renderer>().sharedMaterial));
        var replacementMaterial = Own(new Material(originalMaterial));
        cube.GetComponent<Renderer>().sharedMaterial = originalMaterial;
        var replacementMesh = Own(Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh));
        using (var session = new PhotoshootService.LivePreviewSession())
        {
            // Exercise the production cache without rendering or altering the user's scene lighting.
            var previewScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var sceneField = typeof(PhotoshootService.LivePreviewSession).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic);
            sceneField.SetValue(session, previewScene);
            try
            {
            var ensure = typeof(PhotoshootService.LivePreviewSession).GetMethod("EnsureAvatarCopy", BindingFlags.Instance | BindingFlags.NonPublic);
            var copyField = typeof(PhotoshootService.LivePreviewSession).GetField("avatarCopy", BindingFlags.Instance | BindingFlags.NonPublic);
            bool Refresh() => (bool)ensure.Invoke(session, new object[] { root, null, false });
            Assert.True(Refresh());
            var first = (GameObject)copyField.GetValue(session);
            Assert.False(Refresh()); Assert.AreSame(first, copyField.GetValue(session));
            cube.GetComponent<Renderer>().sharedMaterial = replacementMaterial;
            Assert.True(Refresh());
            Assert.AreSame(replacementMaterial, ((GameObject)copyField.GetValue(session)).GetComponentInChildren<Renderer>(true).sharedMaterial);
            cube.GetComponent<MeshFilter>().sharedMesh = replacementMesh;
            Assert.True(Refresh());
            Assert.AreSame(replacementMesh, ((GameObject)copyField.GetValue(session)).GetComponentInChildren<MeshFilter>(true).sharedMesh);
            cube.SetActive(false); Assert.True(Refresh());
            Assert.False(((GameObject)copyField.GetValue(session)).transform.GetChild(0).gameObject.activeSelf);
            Child("Added accessory", root.transform); Assert.True(Refresh());
            Assert.AreEqual(2, ((GameObject)copyField.GetValue(session)).transform.childCount);
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(previewScene);
                sceneField.SetValue(session, default(UnityEngine.SceneManagement.Scene));
            }
        }
    }

    private Mesh Skin(SkinnedMeshRenderer renderer, Transform bone)
    {
        var mesh = Own(new Mesh());
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }; mesh.triangles = new[] { 0, 1, 2 };
        mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
        mesh.bindposes = new[] { bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix };
        return mesh;
    }

    private void AddFullController(VRCExpressionParameters parameters, IEnumerable<string> globals)
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        var componentType = Find("VF.Model.VRCFury"); var controllerType = Find("VF.Model.Feature.FullController");
        Assert.NotNull(componentType, "VRCFury is required for these integration tests.");
        var component = root.AddComponent(componentType);
        var controller = Activator.CreateInstance(controllerType);
        var entries = (IList)controllerType.GetField("prms").GetValue(controller);
        var entryType = entries.GetType().GetGenericArguments()[0]; var entry = Activator.CreateInstance(entryType);
        var field = entryType.GetField("parameters"); var wrapper = Activator.CreateInstance(field.FieldType);
        field.FieldType.GetField("objRef").SetValue(wrapper, parameters); field.SetValue(entry, wrapper); entries.Add(entry);
        controllerType.GetField("globalParams").SetValue(controller, globals.ToList());
        componentType.GetField("content").SetValue(component, controller);
    }
}
