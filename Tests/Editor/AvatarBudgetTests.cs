using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using Object = UnityEngine.Object;

public sealed class AvatarBudgetTests
{
    private sealed class Provider : ICustomBaseProvider
    {
        public Transform Root;
        public CustomBaseFootprint Footprint;
        public CustomBaseInfo Describe(Transform avatarRoot) =>
            avatarRoot == Root ? new CustomBaseInfo { Name = "Test base", Footprint = () => Footprint } : null;
    }

    private readonly List<Object> owned = new List<Object>();
    private GameObject root;
    private Provider provider;

    [SetUp] public void SetUp()
    {
        root = Own(new GameObject("Budget avatar"));
        root.AddComponent<VRCAvatarDescriptor>();
        provider = new Provider { Root = root.transform };
        CustomBases.Register(provider);
    }

    [TearDown] public void TearDown()
    {
        CustomBases.Unregister(provider);
        foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
    }

    [Test] public void SplitsTheCustomBaseFromTheAvatarAndCountsItsBuildChanges()
    {
        var logic = Child("mcb logic", root.transform);
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Flex", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true } };
        AddFullController(logic.gameObject, parameters);
        logic.gameObject.AddComponent<VRCPhysBone>();
        logic.gameObject.AddComponent<VRCContactSender>();
        Child("Hair", root.transform).gameObject.AddComponent<VRCPhysBone>();
        root.AddComponent<VRCContactReceiver>();
        root.AddComponent<VRCContactReceiver>().localOnly = true;

        var armature = Child("Armature", root.transform);
        var bones = new[] { Child("Hips", armature), Child("Added", armature), Child("Stripped", armature) };
        var body = Child("Body", root.transform).gameObject.AddComponent<SkinnedMeshRenderer>();
        body.bones = bones;
        provider.Footprint = new CustomBaseFootprint { Objects = { logic.gameObject }, Bones = { bones[1], bones[2] }, BuildPhysBones = 2, BuildRemovedBones = 1 };

        var budget = AvatarBudget.Estimate(root);
        Assert.AreEqual("Test base", budget.CustomBase);
        Assert.AreEqual(8, budget.Parameters.CustomBaseBits);
        Assert.AreEqual(0, budget.Parameters.AvatarBits);
        Assert.AreEqual((1, 3), (budget.PhysBones.Avatar, budget.PhysBones.CustomBase), "its own PhysBone and the two its build adds");
        Assert.AreEqual((1, 1), (budget.Contacts.Avatar, budget.Contacts.CustomBase), "a local-only receiver does not count");
        Assert.AreEqual((1, 1), (budget.Bones.Avatar, budget.Bones.CustomBase), "the stripped bone leaves the custom base's count");
        Assert.AreEqual(2, budget.BuildPhysBones);
        Assert.AreEqual(1, budget.BuildRemovedBones);
    }

    [Test] public void AnAvatarWithoutCustomBaseIsAllAvatar()
    {
        CustomBases.Unregister(provider);
        Child("Hair", root.transform).gameObject.AddComponent<VRCPhysBone>();
        var budget = AvatarBudget.Estimate(root);
        Assert.IsNull(budget.CustomBase);
        Assert.AreEqual((1, 0), (budget.PhysBones.Avatar, budget.PhysBones.CustomBase));
    }

    [Test] public void RatesWithTheSdksPcLimits()
    {
        int excellent = AvatarBudget.Limit(l => l.boneCount, PerformanceRating.Excellent);
        int poor = AvatarBudget.Limit(l => l.boneCount, PerformanceRating.Poor);
        Assert.AreEqual(PerformanceRating.Excellent, AvatarBudget.Rate(l => l.boneCount, excellent));
        Assert.AreEqual(PerformanceRating.Good, AvatarBudget.Rate(l => l.boneCount, excellent + 1));
        Assert.AreEqual(PerformanceRating.VeryPoor, AvatarBudget.Rate(l => l.boneCount, poor + 1));
    }

    // Worked out from VRCFury's solver: 40 radial floats are 320 bits; 31 number slots in 2 batches with a 2-bit index fit.
    [Test] public void EstimatesWhatVrcFuryCompressionLeavesAfterBuild()
    {
        Descriptor(Enumerable.Range(0, 40).Select(i => "Radial" + i).ToArray(), "Hold");
        WithCompression(0, () =>
        {
            var parameters = AvatarBudget.Estimate(root).Parameters;
            Assert.AreEqual(321, parameters.TotalBeforeCompression);
            Assert.IsTrue(parameters.Compresses);
            Assert.AreEqual(40, parameters.CompressedParameters, "the held button cannot be compressed");
            Assert.AreEqual(251, parameters.BuiltBits);
            Assert.AreEqual(2 * (.1f + .5f / 30f), parameters.SyncSeconds, 1e-4f);
            Assert.IsFalse(parameters.OverBudget);
        });
    }

    [Test] public void NothingIsCompressedWhenVrcFuryCompressionIsOff()
    {
        Descriptor(Enumerable.Range(0, 40).Select(i => "Radial" + i).ToArray(), null);
        WithCompression(2, () =>
        {
            var parameters = AvatarBudget.Estimate(root).Parameters;
            Assert.IsFalse(parameters.Compresses);
            Assert.AreEqual(320, parameters.BuiltBits);
            Assert.IsTrue(parameters.OverBudget);
        });
    }

    [Test] public void ParametersNoMenuDrivesStayUncompressed()
    {
        Descriptor(Array.Empty<string>(), null, Enumerable.Range(0, 40).Select(i => "Osc" + i).ToArray());
        WithCompression(0, () => Assert.AreEqual(320, AvatarBudget.Estimate(root).Parameters.BuiltBits));
    }

    // Floats driven by radial puppets, a bool held by a button, and floats no menu uses.
    private void Descriptor(string[] radials, string button, string[] unused = null)
    {
        var descriptor = root.GetComponent<VRCAvatarDescriptor>();
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        var list = new List<VRCExpressionParameters.Parameter>();
        foreach (string name in radials.Concat(unused ?? Array.Empty<string>()))
            list.Add(new VRCExpressionParameters.Parameter { name = name, valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true });
        foreach (string name in radials)
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = name, type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = name } },
            });
        if (button != null)
        {
            list.Add(new VRCExpressionParameters.Parameter { name = button, valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true });
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = button, type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = button },
            });
        }
        parameters.parameters = list.ToArray();
        descriptor.customExpressions = true;
        descriptor.expressionParameters = parameters;
        descriptor.expressionsMenu = menu;
    }

    // VRCFury's global compression setting: 0 compresses, 1 asks, 2 fails the build.
    private static void WithCompression(int mode, Action check)
    {
        const string key = "com.vrcfury.parameterCompressor";
        bool existed = EditorPrefs.HasKey(key); int old = EditorPrefs.GetInt(key);
        try { EditorPrefs.SetInt(key, mode); check(); }
        finally { if (existed) EditorPrefs.SetInt(key, old); else EditorPrefs.DeleteKey(key); }
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
    private static Transform Child(string name, Transform parent) { var child = new GameObject(name).transform; child.SetParent(parent, false); return child; }

    private static void AddFullController(GameObject host, VRCExpressionParameters parameters)
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        var componentType = Find("VF.Model.VRCFury"); var controllerType = Find("VF.Model.Feature.FullController");
        Assert.NotNull(componentType, "VRCFury is required for these integration tests.");
        var component = host.AddComponent(componentType);
        var controller = Activator.CreateInstance(controllerType);
        var entries = (IList)controllerType.GetField("prms").GetValue(controller);
        var entryType = entries.GetType().GetGenericArguments()[0]; var entry = Activator.CreateInstance(entryType);
        var field = entryType.GetField("parameters"); var wrapper = Activator.CreateInstance(field.FieldType);
        field.FieldType.GetField("objRef").SetValue(wrapper, parameters); field.SetValue(entry, wrapper); entries.Add(entry);
        controllerType.GetField("globalParams").SetValue(controller, new List<string>());
        componentType.GetField("content").SetValue(component, controller);
    }
}
