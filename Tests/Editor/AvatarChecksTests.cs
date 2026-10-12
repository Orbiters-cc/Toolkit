using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

// The checks run on a built avatar: menus and parameters, animator flow, holes in the body, what Quest loses.
public sealed class AvatarChecksTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private VRCAvatarDescriptor descriptor;
    private AnimatorController fx;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        descriptor = root.AddComponent<VRCAvatarDescriptor>();
        fx = Own(new AnimatorController());
        fx.AddLayer("Base");
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        descriptor.customExpressions = true;
    }

    [TearDown] public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void MenusFlagCutPagesDeadControlsAndUnreadSyncedParameters()
    {
        fx.AddParameter("Hat", AnimatorControllerParameterType.Bool);
        descriptor.expressionParameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        descriptor.expressionParameters.parameters = new[]
        {
            new VRCExpressionParameters.Parameter { name = "Hat", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true },
            new VRCExpressionParameters.Parameter { name = "Ghost", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true },
            new VRCExpressionParameters.Parameter { name = "Quiet", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false },
        };
        var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        menu.controls = Enumerable.Range(0, 6).Select(i => Toggle("Hat " + i, "Hat")).ToList();
        menu.controls.Add(Toggle("Ghost", "Ghost"));
        menu.controls.Add(Toggle("Gone", "Missing"));
        menu.controls.Add(new VRCExpressionsMenu.Control { name = "Empty", type = VRCExpressionsMenu.Control.ControlType.SubMenu });
        descriptor.expressionsMenu = menu;

        var issues = MenuAudit.Find(root);

        Assert.That(issues.Count(i => i.Level == IssueLevel.Problem && i.Title.Contains("9 controls")), Is.EqualTo(1), "Nine controls: the ninth can't be reached.");
        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title.Contains("“Missing”")), Is.True, "A control on a parameter that doesn't exist does nothing.");
        Assert.That(issues.Any(i => i.Title.Contains("sets “Ghost”")), Is.True, "Nothing reads Ghost: its control moves nothing.");
        Assert.That(issues.Any(i => i.Title.Contains("“Ghost” is synced")), Is.True, "And its bits are synced for nothing.");
        Assert.That(issues.Any(i => i.Title.Contains("Quiet")), Is.False, "A local parameter costs nothing.");
        Assert.That(issues.Any(i => i.Title.Contains("“Empty” opens no menu")), Is.True);
        Assert.That(issues.Any(i => i.Title.Contains("“Hat")), Is.False, "Hat is read by the FX layer.");
    }

    [Test] public void AnimatorsFlagMissingParametersAndStatesNothingReaches()
    {
        var machine = fx.layers[0].stateMachine;
        var idle = machine.AddState("Idle");
        var open = machine.AddState("Open");
        machine.AddState("Forgotten");
        var nested = machine.AddStateMachine("Hats");
        var hat = nested.AddState("Hat");
        idle.AddTransition(open).AddCondition(AnimatorConditionMode.If, 0, "OpenMouth");
        idle.AddTransition(nested);
        machine.defaultState = idle;

        var issues = AnimatorFlow.Find(root);

        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title.Contains("“OpenMouth”")), Is.True, "The transition waits on a parameter the controller doesn't have.");
        var unreachable = AnimatorFlow.Unreachable(machine);
        Assert.That(unreachable.Select(s => s.name), Is.EquivalentTo(new[] { "Forgotten" }), "A state inside a sub-state machine is reached through its entry.");
        Assert.That(issues.Any(i => i.Title.Contains("1 state can never play")), Is.True);
        Assert.That(hat, Is.Not.Null);
    }

    [Test] public void AShrunkBodyWithClothingThatComesOffShowsAHole()
    {
        var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
        body.transform.SetParent(root.transform, false);
        var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } });
        foreach (var shape in new[] { "Shrink_Chest", "Smile" }) mesh.AddBlendShapeFrame(shape, 100, new Vector3[3], null, null);
        body.sharedMesh = mesh;
        body.SetBlendShapeWeight(0, 100);
        var shirt = new GameObject("Shirt"); shirt.transform.SetParent(root.transform, false);
        shirt.AddComponent<MeshFilter>().sharedMesh = mesh; shirt.AddComponent<MeshRenderer>();
        var off = Clip(("Shirt", typeof(GameObject), "m_IsActive", 0f));
        fx.layers[0].stateMachine.AddState("Shirt off").motion = off;

        var issues = BodyHoles.Find(root);
        Assert.That(issues.Single().Title, Does.Contain("“Shrink_Chest” stays at 100"));

        AnimationUtility.SetEditorCurve(off, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Shrink_Chest"), AnimationCurve.Constant(0, 1, 0));
        Assert.That(BodyHoles.Find(root), Is.Empty, "The toggle sets it back.");
        Assert.That(BodyHoles.Shrinks("Iris Shrink"), Is.False, "An iris isn't skin.");
    }

    [Test] public void QuestLosesDesktopShadersAndPcOnlyComponents()
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.GetComponent<MeshRenderer>().sharedMaterial = Own(new Material(Shader.Find("Standard")));
        new GameObject("Lamp", typeof(Light)).transform.SetParent(root.transform, false);
        var follower = new GameObject("Follower", typeof(UnityEngine.Animations.ParentConstraint));
        follower.transform.SetParent(root.transform, false);

        var issues = QuestParity.Find(root);

        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title == "1 material uses a shader Quest can't show"), Is.True);
        Assert.That(issues.Any(i => i.Title == "Quest removes 1 light"), Is.True);
        Assert.That(issues.Any(i => i.Title == "Quest removes 1 Unity constraint"), Is.True);

        quad.GetComponent<MeshRenderer>().sharedMaterial.shader = Shader.Find("VRChat/Mobile/Toon Lit");
        Assert.That(QuestParity.Find(root).Any(i => i.Title.Contains("Quest can't show")), Is.False, "VRChat's mobile shaders show on Quest.");
    }

    [Test] public void TheTwoVersionsSyncTheSameParametersInTheSameOrder()
    {
        var other = new GameObject("Avatar (Quest)");
        SceneManager.MoveGameObjectToScene(other, scene);
        var otherDescriptor = other.AddComponent<VRCAvatarDescriptor>();
        otherDescriptor.customExpressions = true;
        descriptor.expressionParameters = Parameters(("Hat", VRCExpressionParameters.ValueType.Bool), ("Size", VRCExpressionParameters.ValueType.Float), ("Glow", VRCExpressionParameters.ValueType.Bool));
        otherDescriptor.expressionParameters = Parameters(("Size", VRCExpressionParameters.ValueType.Float), ("Hat", VRCExpressionParameters.ValueType.Bool), ("Glow", VRCExpressionParameters.ValueType.Int));

        var issues = QuestParity.Compare(root, other, other.name);

        Assert.That(issues.Any(i => i.Title.Contains("2 synced parameters sit at another place")), Is.True, "Hat and Size swapped places.");
        Assert.That(issues.Any(i => i.Title.Contains("“Glow” is Bool here, Int on")), Is.True);
        otherDescriptor.expressionParameters = Parameters(("Hat", VRCExpressionParameters.ValueType.Bool), ("Size", VRCExpressionParameters.ValueType.Float), ("Glow", VRCExpressionParameters.ValueType.Bool));
        Assert.That(QuestParity.Compare(root, other, other.name), Is.Empty);
    }

    private VRCExpressionParameters Parameters(params (string name, VRCExpressionParameters.ValueType type)[] list)
    {
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = list.Select(p => new VRCExpressionParameters.Parameter { name = p.name, valueType = p.type, networkSynced = true }).ToArray();
        return parameters;
    }

    private static VRCExpressionsMenu.Control Toggle(string name, string parameter) => new VRCExpressionsMenu.Control
    {
        name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
    };

    private AnimationClip Clip(params (string path, System.Type type, string property, float value)[] curves)
    {
        var clip = Own(new AnimationClip());
        foreach (var (path, type, property, value) in curves)
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));
        return clip;
    }

    private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
}
