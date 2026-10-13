using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

// The checks run on a built avatar: menus and parameters, animator flow, holes in the body, what Quest loses. Everything
// is made in a preview scene and in memory, and destroyed after.
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
        root = New("Avatar", null);
        descriptor = root.AddComponent<VRCAvatarDescriptor>();
        fx = Own(new AnimatorController());
        fx.AddLayer("Base");
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        descriptor.customExpressions = true;
    }

    [TearDown] public void TearDown()
    {
        // The controllers' layers, states, transitions and trees are objects of their own, in memory.
        var made = EditorUtility.CollectDependencies(owned.Where(o => o != null).ToArray()).Where(o => o != null && !EditorUtility.IsPersistent(o) && !(o is GameObject) && !(o is Component));
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in made.Concat(owned).ToList()) if (o != null) Object.DestroyImmediate(o);
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

    [Test] public void MenusCheckWhatEachControlTypeUses()
    {
        foreach (var name in new[] { "Hat", "Hue", "Outfit", "Size", "Look X" }) fx.AddParameter(name, AnimatorControllerParameterType.Float);
        descriptor.expressionParameters = Parameters(("Hat", VRCExpressionParameters.ValueType.Bool), ("Hue", VRCExpressionParameters.ValueType.Int),
            ("Outfit", VRCExpressionParameters.ValueType.Int), ("Size", VRCExpressionParameters.ValueType.Float), ("Look X", VRCExpressionParameters.ValueType.Float),
            ("Hat", VRCExpressionParameters.ValueType.Bool));
        var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        menu.controls = new List<VRCExpressionsMenu.Control>();
        var hat = Toggle("Hat", "Hat");
        // Left over from when the control was a radial: VRChat ignores it on a toggle.
        hat.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Gone" } };
        menu.controls.Add(hat);
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = "Hue", type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Hue" } },
        });
        var outfit = Toggle("Outfit", "Outfit"); outfit.value = 2.5f; menu.controls.Add(outfit);
        var size = Toggle("Size", "Size"); size.value = 3f; menu.controls.Add(size);
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = "Look", type = VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet,
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "Look X" }, new VRCExpressionsMenu.Control.Parameter { name = "Look X" } },
        });
        descriptor.expressionsMenu = menu;

        var issues = MenuAudit.Find(root);

        Assert.That(issues.Any(i => i.Title.Contains("“Gone”")), Is.False, "A toggle's stale sub-parameter is not used.");
        Assert.That(issues.Any(i => i.Title == "“Hue” moves “Hue”, which is Int"), Is.True, "A radial sets a Float.");
        Assert.That(issues.Any(i => i.Title.StartsWith("“Outfit” sets “Outfit” to 2.5", StringComparison.Ordinal)), Is.True, "An Int holds whole numbers.");
        Assert.That(issues.Any(i => i.Title.StartsWith("“Size” sets “Size” to 3", StringComparison.Ordinal)), Is.True, "A Float holds -1 to 1.");
        Assert.That(issues.Any(i => i.Title.Contains("“Look” moves one parameter with two of its axes")), Is.True);
        // VRCFury removes duplicates at upload in a project it is in.
        Assert.That(issues.Any(i => i.Title.Contains("“Hat” is listed 2 times")), Is.EqualTo(!VrcFury.Installed));
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

    [Test] public void AnimatorsCheckStateSettingsSyncedLayersAndDrivers()
    {
        fx.AddParameter("Mood", AnimatorControllerParameterType.Int);
        descriptor.expressionParameters = Parameters(("Shared", VRCExpressionParameters.ValueType.Bool));
        var machine = fx.layers[0].stateMachine;
        var idle = machine.AddState("Idle");
        machine.defaultState = idle;
        idle.speedParameterActive = true;
        idle.speedParameter = "Speed";
        var driver = idle.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
        driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>
        {
            new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = "Mood", value = 1 },
            new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = "Shared", value = 1 },
            new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = "Nowhere", value = 1 },
            new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, name = "Mood", source = "Lost" },
        };
        var layers = fx.layers;
        var synced = new AnimatorControllerLayer { name = "Glow", syncedLayerIndex = 0, defaultWeight = 1 };
        synced.SetOverrideMotion(idle, Own(new BlendTree { name = "Glow", blendType = BlendTreeType.Simple1D, blendParameter = "Brightness" }));
        fx.layers = layers.Append(synced).ToArray();

        var issues = AnimatorFlow.Find(root);

        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title == "“Base” reads “Speed”, which its controller doesn't have"), Is.True, "The state's speed waits on it.");
        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title == "“Glow” reads “Brightness”, which its controller doesn't have"), Is.True, "A synced layer's own blend tree.");
        Assert.That(issues.Any(i => i.Title == "“Base” drives “Nowhere”, which no layer and no expression parameter has"), Is.True);
        Assert.That(issues.Any(i => i.Title == "“Base” copies “Lost”, which no layer and no expression parameter has"), Is.True);
        Assert.That(issues.Any(i => i.Title.Contains("“Mood”") || i.Title.Contains("“Shared”")), Is.False, "A driver sets parameters across layers and expression parameters.");
    }

    [Test] public void AShrunkBodyShowsAHoleOnlyUnderClothingThatComesOff()
    {
        var body = New("Body", root.transform).AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(Sheet(-.2f, .2f, 0f, 1.6f, 0f, .05f));
        var shrink = mesh.vertices.Select(v => v.y >= 1.1f && v.y <= 1.4f ? new Vector3(0, 0, -.01f) : Vector3.zero).ToArray();
        mesh.AddBlendShapeFrame("Shrink_Chest", 100, shrink, null, null);
        mesh.AddBlendShapeFrame("Smile", 100, new Vector3[mesh.vertexCount], null, null);
        body.sharedMesh = mesh;
        body.SetBlendShapeWeight(0, 100);
        var hat = Wear("Hat", Sheet(-.1f, .1f, 1.7f, 1.9f, 0f, .02f));
        var states = fx.layers[0].stateMachine;
        states.AddState("Hat off").motion = Clip(("Hat", typeof(GameObject), "m_IsActive", 0f));

        Assert.That(BodyHoles.Find(root), Is.Empty, "The hat comes off, but it doesn't cover the chest.");

        var shirt = Wear("Shirt", Sheet(-.25f, .25f, 1f, 1.5f, .01f, .02f));
        var off = Clip(("Shirt", typeof(MeshRenderer), "m_Enabled", 0f));
        states.AddState("Shirt off").motion = off;
        var issues = BodyHoles.Find(root);
        Assert.That(issues.Single().Title, Does.Contain("“Shrink_Chest” stays at 100"), "The shirt's renderer turns off over the shrunk chest.");
        Assert.That(issues.Single().Detail, Does.Contain("“Shirt”").And.Not.Contain("“Hat”"));

        var vest = Wear("Vest", Sheet(-.25f, .25f, 1f, 1.5f, .02f, .02f));
        Assert.That(BodyHoles.Find(root), Is.Empty, "A vest always worn covers the chest when the shirt is off.");
        Object.DestroyImmediate(vest.gameObject);

        AnimationUtility.SetEditorCurve(off, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.Shrink_Chest"), AnimationCurve.Constant(0, 1, 0));
        Assert.That(BodyHoles.Find(root), Is.Empty, "The toggle sets it back.");
        Assert.That(BodyHoles.Shrinks("Iris Shrink"), Is.False, "An iris isn't skin.");
        Assert.That(hat, Is.Not.Null);
    }

    [Test] public void ADeepHideShapeUnderClothingThatComesOffShowsAHole()
    {
        var body = New("Body", root.transform).AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(Sheet(-.2f, .2f, 0f, 1.6f, 0f, .05f));
        // Pushed 30 cm in: as baked, the skin would be far from the shirt over it.
        mesh.AddBlendShapeFrame("Hide_Belly", 100, mesh.vertices.Select(v => v.y >= .7f && v.y <= 1f ? new Vector3(0, 0, -.3f) : Vector3.zero).ToArray(), null, null);
        body.sharedMesh = mesh;
        body.SetBlendShapeWeight(0, 100);
        Wear("Shirt", Sheet(-.25f, .25f, .6f, 1.1f, .01f, .02f));
        fx.layers[0].stateMachine.AddState("Shirt off").motion = Clip(("Shirt", typeof(GameObject), "m_IsActive", 0f));

        var issues = BodyHoles.Find(root);

        Assert.That(issues.Single().Title, Does.Contain("“Hide_Belly” stays at 100"), "The skin is measured where it is without the shape.");
        Assert.That(body.GetBlendShapeWeight(0), Is.EqualTo(100f), "The shape is set back after.");
    }

    [Test] public void QuestLosesDesktopShadersAndPcOnlyComponents()
    {
        var quad = New("Quad", root.transform);
        quad.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        quad.AddComponent<MeshRenderer>().sharedMaterial = Own(new Material(Shader.Find("Standard")));
        New("Lamp", root.transform).AddComponent<Light>();
        New("Follower", root.transform).AddComponent<UnityEngine.Animations.ParentConstraint>();

        var issues = QuestParity.Find(root);

        Assert.That(issues.Any(i => i.Level == IssueLevel.Problem && i.Title == "1 material uses a shader Quest can't show"), Is.True);
        Assert.That(issues.Any(i => i.Title == "Quest removes 1 light"), Is.True);
        Assert.That(issues.Any(i => i.Title == "Quest removes 1 Unity constraint"), Is.True);
        Assert.That(QuestParity.LooksQuest(root), Is.False);

        quad.GetComponent<MeshRenderer>().sharedMaterial.shader = Shader.Find("VRChat/Mobile/Toon Lit");
        Assert.That(QuestParity.Find(root).Any(i => i.Title.Contains("Quest can't show")), Is.False, "VRChat's mobile shaders show on Quest.");
        Assert.That(QuestParity.LooksQuest(root), Is.True, "Made for Quest: its own check covers what Quest loses.");
        Assert.That(QuestParity.ForQuest(root, null), Is.True);
    }

    [Test] public void ThePerPlatformOverrideIsTheOtherVersion()
    {
        var quest = New("Avatar (Quest)", null);
        var questDescriptor = quest.AddComponent<VRCAvatarDescriptor>();
        var store = Type.GetType("VRC.SDK3.Avatars.Components.VRCPerPlatformOverrides, VRC.SDK3A");
        if (store == null) Assert.Ignore("This VRChat SDK has no per-platform overrides.");
        var overrides = new SerializedObject(root.AddComponent(store));
        var list = overrides.FindProperty("platformOverrides");
        list.arraySize = 1;
        list.GetArrayElementAtIndex(0).FindPropertyRelative("avatar").objectReferenceValue = questDescriptor;
        list.GetArrayElementAtIndex(0).FindPropertyRelative("platform").intValue = (int)BuildTarget.Android;
        overrides.ApplyModifiedPropertiesWithoutUndo();

        var counterpart = QuestParity.CounterpartOf(root);

        Assert.That(counterpart?.Avatar, Is.SameAs(quest));
        Assert.That(counterpart.IsQuest, Is.True);
        Assert.That(QuestParity.ForQuest(root, counterpart), Is.False, "Quest players see the override: the PC version's Quest losses don't matter.");
    }

    [Test] public void TheTwoVersionsSyncTheSameParametersInTheSameOrder()
    {
        var other = New("Avatar (Quest)", null);
        var otherDescriptor = other.AddComponent<VRCAvatarDescriptor>();
        otherDescriptor.customExpressions = true;
        descriptor.expressionParameters = Parameters(("Hat", VRCExpressionParameters.ValueType.Bool), ("Size", VRCExpressionParameters.ValueType.Float), ("Glow", VRCExpressionParameters.ValueType.Bool));
        otherDescriptor.expressionParameters = Parameters(("Size", VRCExpressionParameters.ValueType.Float), ("Hat", VRCExpressionParameters.ValueType.Bool), ("Glow", VRCExpressionParameters.ValueType.Int));

        var issues = QuestParity.Compare(root, other, other.name);

        // VRCFury puts the Quest version's in the PC version's order at upload, once it knows that order.
        var expected = VrcFury.MobileAlignment(null).Aligns ? IssueLevel.Note : IssueLevel.Problem;
        Assert.That(issues.Any(i => i.Level == expected && i.Title.Contains("2 synced parameters")), Is.True, "Hat and Size swapped places.");
        Assert.That(issues.Any(i => i.Title.Contains("“Glow” is Bool here, Int on")), Is.True);
        otherDescriptor.expressionParameters = Parameters(("Hat", VRCExpressionParameters.ValueType.Bool), ("Size", VRCExpressionParameters.ValueType.Float), ("Glow", VRCExpressionParameters.ValueType.Bool));
        Assert.That(QuestParity.Compare(root, other, other.name), Is.Empty);
    }

    // Made hidden, moved into the preview scene, then shown: an object made in the open scene would mark it changed.
    private GameObject New(string name, Transform parent)
    {
        var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
        if (parent != null) go.transform.SetParent(parent, false);
        else SceneManager.MoveGameObjectToScene(go, scene);
        go.hideFlags = HideFlags.None;
        return go;
    }

    private MeshRenderer Wear(string name, Mesh mesh)
    {
        var go = New(name, root.transform);
        go.AddComponent<MeshFilter>().sharedMesh = Own(mesh);
        return go.AddComponent<MeshRenderer>();
    }

    // A flat grid of quads facing +z, from (x0, y0) to (x1, y1) at depth z.
    private static Mesh Sheet(float x0, float x1, float y0, float y1, float z, float step)
    {
        int columns = Mathf.RoundToInt((x1 - x0) / step), rows = Mathf.RoundToInt((y1 - y0) / step);
        var vertices = new List<Vector3>();
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++) vertices.Add(new Vector3(x0 + (x1 - x0) * x / columns, y0 + (y1 - y0) * y / rows, z));
        var triangles = new List<int>();
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = y * (columns + 1) + x, b = a + 1, c = a + columns + 1, d = c + 1;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        return mesh;
    }

    private VRCExpressionParameters Parameters(params (string name, VRCExpressionParameters.ValueType type)[] list)
    {
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = list.Select(p => new VRCExpressionParameters.Parameter { name = p.name, valueType = p.type, networkSynced = true }).ToArray();
        return parameters;
    }

    private static VRCExpressionsMenu.Control Toggle(string name, string parameter) => new VRCExpressionsMenu.Control
    {
        name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter }, value = 1,
    };

    private AnimationClip Clip(params (string path, Type type, string property, float value)[] curves)
    {
        var clip = Own(new AnimationClip());
        foreach (var (path, type, property, value) in curves)
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));
        return clip;
    }

    private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
}
