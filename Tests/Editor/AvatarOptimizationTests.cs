using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

// Build-copy optimizations: what goes, what stays, and that skinned meshes look the same after.
public sealed class AvatarOptimizationTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private VRCAvatarDescriptor descriptor;
    private AnimatorController fx;
    private Transform[] bones;

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = new GameObject("Avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        descriptor = root.AddComponent<VRCAvatarDescriptor>();
        fx = Own(new AnimatorController()); fx.AddLayer("Base");
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        // A chain of four bones along Y.
        bones = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            bones[i] = new GameObject("Bone" + i).transform;
            bones[i].SetParent(i == 0 ? root.transform : bones[i - 1], false);
            bones[i].localPosition = i == 0 ? Vector3.zero : Vector3.up;
        }
    }

    [TearDown] public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    // ---- Objects and components ----

    [Test] public void HiddenObjectsGoUnlessAnAnimationShowsThemOrSomethingUsesThem()
    {
        var hidden = Child("Hidden"); hidden.SetActive(false); Child("Inside", hidden.transform);
        var shown = Child("Shown"); shown.SetActive(false);
        Animate("Shown", typeof(GameObject), "m_IsActive", 1);
        // A visible mesh skinned to a bone kept under a hidden object.
        var holder = Child("Holder"); holder.SetActive(false);
        bones[3].SetParent(holder.transform, true);
        var mesh = Skinned("Body", new[] { 0, 3 });
        // A collider under a hidden object, listed by a visible PhysBone.
        var colliders = Child("Colliders"); colliders.SetActive(false);
        var collider = colliders.AddComponent<VRCPhysBoneCollider>();
        var physBone = bones[1].gameObject.AddComponent<VRCPhysBone>();
        physBone.colliders = new List<VRCPhysBoneColliderBase> { collider };

        int removed = ComponentCleanup.RemoveHiddenObjects(new AvatarUsage(root));

        Assert.That(removed, Is.EqualTo(2), "The hidden object (with what is inside) and the colliders' object.");
        Assert.That(root.transform.Find("Hidden"), Is.Null);
        Assert.That(root.transform.Find("Shown"), Is.Not.Null, "An animation shows it.");
        Assert.That(root.transform.Find("Holder"), Is.Not.Null, "A visible mesh uses a bone inside it.");
        Assert.That(root.transform.Find("Colliders"), Is.Null, "A collider list is not a use.");
        Assert.That(physBone.colliders, Is.Empty, "The list loses the collider that went.");
        Assert.That(mesh != null);
    }

    [Test] public void DisabledComponentsGoUnlessAnAnimationTurnsThemOn()
    {
        var bone = bones[1].gameObject.AddComponent<VRCPhysBone>(); bone.enabled = false;
        var receiver = Child("Receiver").AddComponent<VRCContactReceiver>(); receiver.enabled = false;
        Animate("Receiver", typeof(VRCContactReceiver), "m_Enabled", 1);
        var light = Child("Light").AddComponent<Light>(); light.enabled = false;
        var prop = Child("Prop"); prop.AddComponent<MeshFilter>().sharedMesh = Own(Quad());
        var propRenderer = prop.AddComponent<MeshRenderer>(); propRenderer.enabled = false;
        var face = Skinned("Face", new[] { 0 }); face.enabled = false;
        descriptor.VisemeSkinnedMesh = face;
        var on = bones[2].gameObject.AddComponent<VRCPhysBone>();

        int removed = ComponentCleanup.RemoveDisabledComponents(new AvatarUsage(root));

        Assert.That(removed, Is.EqualTo(3), "The PhysBone, the light and the prop's renderer.");
        Assert.That(bone == null && light == null && propRenderer == null);
        Assert.That(prop.GetComponent<MeshFilter>(), Is.Null, "The renderer's mesh filter goes with it.");
        Assert.That(receiver != null, "An animation turns the receiver on.");
        Assert.That(face != null, "The descriptor uses the face for visemes.");
        Assert.That(on != null, "Enabled components stay.");
    }

    [Test] public void CollidersNoPhysBoneListsGo()
    {
        var used = Child("Used").AddComponent<VRCPhysBoneCollider>();
        var unused = Child("Unused").AddComponent<VRCPhysBoneCollider>();
        bones[1].gameObject.AddComponent<VRCPhysBone>().colliders = new List<VRCPhysBoneColliderBase> { used };

        Assert.That(ComponentCleanup.RemoveUnusedColliders(new AvatarUsage(root)), Is.EqualTo(1));
        Assert.That(used != null && unused == null);
    }

    [Test] public void PhysBonesNothingAnimatesStopFollowingAnimations()
    {
        var still = bones[1].gameObject.AddComponent<VRCPhysBone>(); still.isAnimated = true;
        var tail = Child("Tail"); var tip = Child("Tip", tail.transform);
        var moving = tail.AddComponent<VRCPhysBone>(); moving.isAnimated = true;
        Animate("Tail/Tip", typeof(Transform), "m_LocalRotation.x", .2f);

        Assert.That(ComponentCleanup.SettleStillPhysBones(new AvatarUsage(root)), Is.EqualTo(1));
        Assert.That(still.isAnimated, Is.False);
        Assert.That(moving.isAnimated, Is.True, "An animation turns a bone of its chain.");
        Assert.That(tip != null);
    }

    [Test] public void PhysBonesThatMoveNothingGo()
    {
        var idle = Chain("OldTail", 2).gameObject.AddComponent<VRCPhysBone>();
        var weighted = bones[1].gameObject.AddComponent<VRCPhysBone>();
        Skinned("Body", new[] { 0, 1, 2 });
        var charm = Chain("Charm", 2);
        new GameObject("Bead").AddComponent<MeshRenderer>().transform.SetParent(charm.GetChild(0), false);
        var carrying = charm.gameObject.AddComponent<VRCPhysBone>();
        var ear = Chain("Ear", 2).gameObject.AddComponent<VRCPhysBone>();
        ear.parameter = "Ear";
        fx.AddParameter("Ear_IsGrabbed", AnimatorControllerParameterType.Bool);

        Assert.That(ComponentCleanup.RemoveIdlePhysBones(new AvatarUsage(root)), Is.EqualTo(1));
        Assert.That(idle == null, "No mesh follows its bones and nothing sits on them.");
        Assert.That(weighted != null, "The body is weighted to its bones.");
        Assert.That(carrying != null, "A renderer hangs from its chain.");
        Assert.That(ear != null, "An animator reads its grab parameter.");
    }

    [Test] public void EndBonesBecomeTheEndpoint()
    {
        var tail = Chain("Tail", 3);
        var end = tail.GetChild(0).GetChild(0);
        end.localPosition = new Vector3(0, .2f, 0);
        Weighted("Fur", tail, tail.GetChild(0));
        var bone = tail.gameObject.AddComponent<VRCPhysBone>();
        // Two ends at different offsets have no single endpoint.
        var fork = Chain("Fork", 2);
        var second = new GameObject("Second").transform; second.SetParent(fork, false); second.localPosition = new Vector3(.3f, 0, 0);
        var forked = fork.gameObject.AddComponent<VRCPhysBone>();
        // An end a mesh is weighted to is part of the look.
        var ear = Chain("Ear", 2);
        Weighted("EarFur", ear, ear.GetChild(0));
        var weightedEnd = ear.gameObject.AddComponent<VRCPhysBone>();

        Assert.That(ComponentCleanup.TrimEndBones(new AvatarUsage(root)), Is.EqualTo(1));
        Assert.That(end == null);
        Assert.That(bone.endpointPosition, Is.EqualTo(new Vector3(0, .2f, 0)));
        Assert.That(forked.endpointPosition, Is.EqualTo(Vector3.zero));
        Assert.That(fork.childCount, Is.EqualTo(2));
        Assert.That(weightedEnd.endpointPosition, Is.EqualTo(Vector3.zero));
        Assert.That(ear.GetChild(0) != null);
    }

    // ---- Meshes ----

    [Test] public void UnweightedBonesLeaveTheBoneListAndTheMeshSkinsTheSame()
    {
        var body = Skinned("Body", new[] { 0, 2 });
        var source = body.sharedMesh;
        Pose();
        var before = World(body);

        var copy = Own(MeshOptimization.PruneBones(body));

        Assert.That(copy, Is.Not.Null);
        Assert.That(body.bones, Is.EqualTo(new[] { bones[0], bones[2] }));
        Assert.That(copy.bindposes.Length, Is.EqualTo(2));
        Assert.That(source.bindposes.Length, Is.EqualTo(4), "The source mesh is never changed.");
        AssertSame(before, World(body));
        Assert.That(MeshOptimization.PruneBones(body), Is.Null, "Nothing left to drop.");
    }

    [Test] public void SubmeshesSharingAnOpaqueMaterialBecomeOneSlot()
    {
        var opaque = Own(new Material(Shader.Find("Standard")));
        var other = Own(new Material(Shader.Find("Standard")));
        var transparent = Own(new Material(Shader.Find("Standard")) { renderQueue = 3000 });
        var body = Skinned("Body", new[] { 0 }, submeshes: 6);
        // Slots: opaque, other, opaque, transparent, transparent, and an empty submesh.
        body.sharedMesh.SetIndices(new int[0], MeshTopology.Triangles, 5);
        body.sharedMaterials = new[] { opaque, other, opaque, transparent, transparent, other };
        int triangles = body.sharedMesh.triangles.Length;

        var copy = Own(MeshOptimization.MergeSlots(body, new AvatarUsage(root)));

        Assert.That(copy, Is.Not.Null);
        Assert.That(body.sharedMaterials, Is.EqualTo(new[] { opaque, other, transparent, transparent }), "Transparent slots keep their order; the empty one goes.");
        Assert.That(copy.subMeshCount, Is.EqualTo(4));
        Assert.That(copy.triangles.Length, Is.EqualTo(triangles), "Every triangle is still drawn.");

        var swapped = Skinned("Swapped", new[] { 0 }, submeshes: 2);
        swapped.sharedMaterials = new[] { opaque, opaque };
        var clip = Own(new AnimationClip());
        AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Swapped", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[1]"),
            new[] { new ObjectReferenceKeyframe { time = 0, value = other } });
        fx.layers[0].stateMachine.AddState("Swap").motion = clip;
        Assert.That(MeshOptimization.MergeSlots(swapped, new AvatarUsage(root)), Is.Null, "An animation swaps a slot's material: the slots stay.");
    }

    [Test] public void TexturesTheShaderDoesNotHaveLeaveACopyOfTheMaterial()
    {
        var used = Own(new Texture2D(4, 4)); var stale = Own(new Texture2D(4, 4));
        var material = Own(new Material(Shader.Find("Standard")) { name = "Skin" });
        material.SetTexture("_MainTex", used);
        var serialized = new SerializedObject(material);
        var textures = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
        textures.InsertArrayElementAtIndex(textures.arraySize);
        var entry = textures.GetArrayElementAtIndex(textures.arraySize - 1);
        entry.FindPropertyRelative("first").stringValue = "_OldShaderTex";
        entry.FindPropertyRelative("second.m_Texture").objectReferenceValue = stale;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        var a = Skinned("A", new[] { 0 }); a.sharedMaterial = material;
        var b = Skinned("B", new[] { 0 }); b.sharedMaterial = material;

        Assert.That(MaterialCleanup.UnusedTextures(material), Is.EqualTo(new[] { "_OldShaderTex" }));
        Assert.That(MaterialCleanup.DropUnusedTextures(root), Is.EqualTo(1));
        var copy = Own(a.sharedMaterial);
        Assert.That(copy, Is.Not.SameAs(material));
        Assert.That(b.sharedMaterial, Is.SameAs(copy), "One copy per material.");
        Assert.That(copy.GetTexture("_OldShaderTex"), Is.Null);
        Assert.That(copy.GetTexture("_MainTex"), Is.SameAs(used));
        Assert.That(copy.name, Is.EqualTo("Skin"));
        Assert.That(material.GetTexture("_OldShaderTex"), Is.SameAs(stale), "The avatar's own material is never changed.");
    }

    [Test] public void MeshesAlwaysShownMergeIntoTheOneAnimationsUse()
    {
        var body = Skinned("Body", new[] { 0, 1 }, shape: "Smile");
        body.SetBlendShapeWeight(0, 40);
        Animate("Body", typeof(SkinnedMeshRenderer), "blendShape.Smile", 100);
        var shirt = Skinned("Shirt", new[] { 1, 2 }, shape: "Loose");
        shirt.SetBlendShapeWeight(0, 50);
        // Pants bound in another mesh space (moved up 0.5 at bind time): they skin the same after the merge.
        var pants = Skinned("Pants", new[] { 2, 3 }, offset: new Vector3(0, .5f, 0));
        var hat = Skinned("Hat", new[] { 3 });
        Animate("Hat", typeof(GameObject), "m_IsActive", 0);
        var bodyMaterial = body.sharedMaterial;
        Pose();
        var expected = World(body).Concat(World(shirt)).Concat(World(pants)).ToList();
        int triangles = new[] { body, shirt, pants }.Sum(r => r.sharedMesh.triangles.Length);

        var result = MeshMerge.MergeStill(new AvatarUsage(root));
        owned.AddRange(result.Meshes);

        Assert.That(result.Merged, Is.EqualTo(2), "The shirt and the pants join the body; the hat is toggled.");
        Assert.That(shirt == null && pants == null, "Their renderers go.");
        Assert.That(root.transform.Find("Shirt"), Is.Null, "An object left empty goes too.");
        Assert.That(hat != null && body != null);
        var merged = body.sharedMesh;
        Assert.That(merged.triangles.Length, Is.EqualTo(triangles));
        Assert.That(body.sharedMaterials.Length, Is.EqualTo(3));
        Assert.That(body.sharedMaterials[0], Is.SameAs(bodyMaterial), "The body's own slots come first.");
        Assert.That(merged.blendShapeCount, Is.EqualTo(1), "The body's animated shape stays; the shirt's was baked in.");
        Assert.That(merged.GetBlendShapeName(0), Is.EqualTo("Smile"));
        Assert.That(body.GetBlendShapeWeight(0), Is.EqualTo(40));
        Assert.That(body.bones.Distinct().Count(), Is.EqualTo(body.bones.Length), "Meshes bound in one rest pose share their bones.");
        AssertSame(expected, World(body));
    }

    [Test] public void PropsAlwaysShownJoinTheSkinnedMeshWhereTheyStand()
    {
        var body = Skinned("Body", new[] { 0, 1 });
        // A plain mesh held by the third bone, and one an animation hides.
        var prop = (MeshRenderer)Tube("Prop", .1f, 0f, .3f);
        prop.transform.SetParent(bones[2], false);
        prop.transform.localPosition = new Vector3(.2f, 0, 0);
        var toggled = (MeshRenderer)Tube("Toggled", .1f, 0f, .3f);
        Animate("Toggled", typeof(GameObject), "m_IsActive", 0);
        Pose();
        var mesh = prop.GetComponent<MeshFilter>().sharedMesh;
        var expected = World(body).Concat(mesh.vertices.Select(v => prop.transform.TransformPoint(v))).ToList();

        var result = MeshMerge.MergeStill(new AvatarUsage(root));
        owned.AddRange(result.Meshes);

        Assert.That(result.Merged, Is.EqualTo(1), "The held prop joins; the toggled one stays.");
        Assert.That(prop == null && toggled != null);
        var holder = bones[2].Find("Prop");
        Assert.That(holder, Is.Not.Null, "Its object stays: the merged mesh follows it.");
        Assert.That(holder.GetComponent<MeshFilter>(), Is.Null);
        Assert.That(body.bones, Does.Contain(holder));
        Assert.That(body.sharedMaterials.Length, Is.EqualTo(2));
        AssertSame(expected, World(body));
    }

    [Test] public void MeshesToggledTogetherBecomeOneAndTheirShapeAnimationsFollow()
    {
        Skinned("Body", new[] { 0, 1 });
        var shirt = Skinned("Shirt", new[] { 1, 2 }, shape: "Loose");
        var pants = Skinned("Pants", new[] { 2, 3 }, shape: "Loose");
        var hat = Skinned("Hat", new[] { 3 });
        pants.SetBlendShapeWeight(0, 30);
        // One clip takes the shirt and the pants off together and loosens the pants; another the hat.
        var outfit = Own(new AnimationClip());
        AnimationUtility.SetEditorCurve(outfit, EditorCurveBinding.FloatCurve("Shirt", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
        AnimationUtility.SetEditorCurve(outfit, EditorCurveBinding.FloatCurve("Pants", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
        AnimationUtility.SetEditorCurve(outfit, EditorCurveBinding.FloatCurve("Pants", typeof(SkinnedMeshRenderer), "blendShape.Loose"), AnimationCurve.Constant(0, 1, 100));
        fx.layers[0].stateMachine.AddState("Outfit off").motion = outfit;
        Animate("Hat", typeof(GameObject), "m_IsActive", 0);
        Pose();
        var expected = World(shirt).Concat(World(pants)).ToList();

        var build = AttachmentAnimationBuild.Prepare(root);
        try
        {
            var result = MeshMerge.MergeTogether(new AvatarUsage(root), build);
            owned.AddRange(result.Meshes);

            Assert.That(result.Merged, Is.EqualTo(1), "The pants join the shirt; the hat has its own toggle.");
            Assert.That(pants == null && hat != null);
            Assert.That(root.transform.Find("Pants"), Is.Not.Null, "Its object stays: its toggle animates it.");
            var merged = shirt.sharedMesh;
            Assert.That(Enumerable.Range(0, merged.blendShapeCount).Select(merged.GetBlendShapeName), Is.EqualTo(new[] { "Loose", "Loose Pants" }));
            Assert.That(shirt.GetBlendShapeWeight(1), Is.EqualTo(30));
            AssertSame(expected, World(shirt));
            var copy = build.Clips.Select(c => c.Clip).Single(c => AnimationUtility.GetCurveBindings(c).Any(b => b.path == "Shirt"));
            var bindings = AnimationUtility.GetCurveBindings(copy);
            Assert.That(bindings.Any(b => b.path == "Shirt" && b.propertyName == "blendShape.Loose Pants"), "The pants' shape animation now loosens them on the merged mesh.");
            Assert.That(bindings.Any(b => b.path == "Pants" && b.type == typeof(SkinnedMeshRenderer)), Is.False);
            Assert.That(AnimationUtility.GetCurveBindings(outfit).Any(b => b.path == "Pants" && b.type == typeof(SkinnedMeshRenderer)), "The avatar's own clip is untouched.");
        }
        finally { AttachmentAnimationBuild.Release(root); }
    }

    // ---- Names ----

    [Test] public void ShortNamesKeepWhatIsFoundByName()
    {
        var toggle = Child("Very long toggle object name");
        var inner = Child("Inner child name", toggle.transform);
        var body = Child("Body");
        fx.AddParameter("Only the animator uses this", AnimatorControllerParameterType.Float);
        fx.AddParameter("MenuToggle", AnimatorControllerParameterType.Bool);
        fx.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
        fx.AddParameter("TailWag_IsGrabbed", AnimatorControllerParameterType.Bool);
        var clip = Own(new AnimationClip());
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Very long toggle object name/Inner child name", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Only the animator uses this"), AnimationCurve.Constant(0, 1, 1));
        var state = fx.layers[0].stateMachine.AddState("Off");
        state.motion = clip;
        var transition = fx.layers[0].stateMachine.AddAnyStateTransition(state);
        transition.AddCondition(AnimatorConditionMode.If, 0, "TailWag_IsGrabbed");
        transition.AddCondition(AnimatorConditionMode.Greater, .5f, "Only the animator uses this");
        var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "MenuToggle", valueType = VRCExpressionParameters.ValueType.Bool } };
        descriptor.expressionParameters = parameters;
        var physBone = bones[1].gameObject.AddComponent<VRCPhysBone>();
        physBone.parameter = "TailWag";

        var build = AttachmentAnimationBuild.Prepare(root);
        try
        {
            var result = ShortNames.Rename(new AvatarUsage(root), build);

            Assert.That(toggle.name.Length, Is.EqualTo(1)); Assert.That(inner.name.Length, Is.EqualTo(1));
            Assert.That(body.name, Is.EqualTo("Body"), "MMD worlds animate Body by name.");
            Assert.That(result.Objects, Is.GreaterThanOrEqualTo(2));
            var controller = build.Owned.OfType<AnimatorController>().Single();
            var names = controller.parameters.Select(p => p.name).ToList();
            Assert.That(names, Does.Contain("MenuToggle"), "The menu, OSC and saved values use it.");
            Assert.That(names, Does.Contain("GestureLeft"), "VRChat sets it.");
            Assert.That(names, Does.Not.Contain("Only the animator uses this"));
            Assert.That(physBone.parameter.Length, Is.EqualTo(1));
            Assert.That(names, Does.Contain(physBone.parameter + "_IsGrabbed"), "A PhysBone's parameters change with it.");
            var copied = build.Owned.OfType<AnimatorStateTransition>().Single();
            Assert.That(copied.conditions.Select(c => c.parameter), Is.EqualTo(new[] { physBone.parameter + "_IsGrabbed", names.Single(n => n.Length == 1) }));
            var copy = build.Clips.Single().Clip;
            var bindings = AnimationUtility.GetCurveBindings(copy);
            Assert.That(bindings.Any(b => b.path == toggle.name + "/" + inner.name && b.propertyName == "m_IsActive"), "The animation follows the new names.");
            Assert.That(bindings.Any(b => b.type == typeof(Animator) && b.propertyName == names.Single(n => n.Length == 1)));
            Assert.That(fx.parameters.Select(p => p.name), Does.Contain("Only the animator uses this"), "The avatar's own controller is untouched.");
            Assert.That(ShortNames.Short(0), Is.EqualTo("a")); Assert.That(ShortNames.Short(62), Is.EqualTo("aa"));
        }
        finally { AttachmentAnimationBuild.Release(root); }
    }

    // ---- Menu icons ----

    [Test] public void MenuIconsShrinkOnCopiesOfTheMenus()
    {
        var icon = Own(new Texture2D(256, 128, TextureFormat.RGBA32, false));
        icon.SetPixels32(Enumerable.Repeat(new Color32(200, 40, 40, 255), 256 * 128).ToArray()); icon.Apply();
        var small = Own(new Texture2D(32, 32, TextureFormat.RGBA32, false));
        var sub = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        sub.controls.Add(new VRCExpressionsMenu.Control { name = "Hat", icon = icon, type = VRCExpressionsMenu.Control.ControlType.Toggle });
        sub.controls.Add(new VRCExpressionsMenu.Control { name = "Glasses", icon = small, type = VRCExpressionsMenu.Control.ControlType.Toggle });
        var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        menu.controls.Add(new VRCExpressionsMenu.Control { name = "Clothes", icon = icon, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = sub });
        descriptor.expressionsMenu = menu;
        Assert.That(MenuIcons.Of(root), Is.EquivalentTo(new[] { icon, small }));

        var result = MenuIcons.Shrink(root);
        owned.AddRange(result.Made);

        Assert.That(result.Icons, Is.EqualTo(1), "The same icon twice, shrunk once; the small one stays.");
        var copy = descriptor.expressionsMenu;
        Assert.That(copy, Is.Not.SameAs(menu));
        var shrunk = copy.controls[0].icon;
        Assert.That(new[] { shrunk.width, shrunk.height }, Is.EqualTo(new[] { 64, 32 }), "64 px at most, its shape kept.");
        Assert.That(copy.controls[0].subMenu, Is.Not.SameAs(sub));
        Assert.That(copy.controls[0].subMenu.controls[0].icon, Is.SameAs(shrunk));
        Assert.That(copy.controls[0].subMenu.controls[1].icon, Is.SameAs(small));
        Assert.That(menu.controls[0].icon, Is.SameAs(icon), "The menu assets stay as they are.");
        Assert.That(sub.controls[0].icon, Is.SameAs(icon));
    }

    // ---- Bones that never move ----

    [Test] public void BonesThatNeverMoveFollowTheBoneTheyHangFrom()
    {
        Animate("Bone0/Bone1", typeof(Transform), "m_LocalRotation.z", .2f);
        var charm = new GameObject("Charm").transform; charm.SetParent(bones[1], false); charm.localPosition = new Vector3(.2f, .1f, 0); charm.localRotation = Quaternion.Euler(0, 30, 0);
        var bead = new GameObject("Bead").transform; bead.SetParent(charm, false); bead.localPosition = new Vector3(0, .05f, 0);
        var swing = new GameObject("Swing").transform; swing.SetParent(bones[1], false); swing.localPosition = new Vector3(-.2f, 0, 0);
        swing.gameObject.AddComponent<VRCPhysBone>();
        var eye = new GameObject("Eye").transform; eye.SetParent(bones[2], false);
        var look = descriptor.customEyeLookSettings; look.leftEye = eye; descriptor.customEyeLookSettings = look;
        var mesh = Weighted("Trinkets", bones[0], bones[1], charm, bead, swing, eye);
        Pose();
        var before = World(mesh);

        var result = StaticBones.Fold(new AvatarUsage(root));
        owned.AddRange(result.Meshes);

        Assert.That(mesh.bones.Contains(charm) || mesh.bones.Contains(bead), Is.False, "The charm and its bead follow the animated bone.");
        Assert.That(mesh.bones.Contains(bones[1]) && mesh.bones.Contains(swing) && mesh.bones.Contains(eye), Is.True, "Animated, simulated and the eye stay.");
        Assert.That(mesh.bones.Contains(bones[0]), Is.False, "Nothing moves Bone0 against the avatar: it follows the root.");
        Assert.That(result.Bones, Is.EqualTo(3));
        Assert.That(charm != null && bead != null, "The transforms stay.");
        AssertSame(before, World(mesh));
    }

    // ---- The body under clothing ----

    [Test] public void BodyUnderClothingWornAllTheTimeGoes()
    {
        var body = (SkinnedMeshRenderer)Tube("Body", .1f, 0f, 1.6f, skinned: true);
        var shirt = Tube("Shirt", .11f, .6f, 1.2f, hole: true);
        var original = Triangles(body.sharedMesh);

        var result = BodyCover.HideCovered(new AvatarUsage(root), body);
        owned.Add(result?.Mesh);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Triangles, Is.GreaterThan(original.Count / 5), "Most of the skin under the shirt goes.");
        var left = Triangles(body.sharedMesh);
        Assert.That(left.Count, Is.EqualTo(original.Count - result.Triangles));
        // Everything outside the shirt, near its openings and under its hole stays.
        Assert.That(left.Count(t => t.y < .58f || t.y > 1.22f), Is.EqualTo(original.Count(t => t.y < .58f || t.y > 1.22f)));
        Assert.That(left.Any(t => t.y > .6f && t.y < .63f), "Skin just inside the shirt's lower opening stays.");
        Assert.That(left.Any(t => t.y > .86f && t.y < .94f && t.x > .05f && Mathf.Abs(t.z) < .03f), "Skin under the hole stays.");
        Assert.That(left.Any(t => t.y > .86f && t.y < .94f && t.x < -.05f), Is.False, "Skin on the other side of the hole goes.");
        Assert.That(shirt != null);
    }

    [Test] public void ClothingThatCanComeOffOrShowsThroughHidesNothing()
    {
        var body = (SkinnedMeshRenderer)Tube("Body", .1f, 0f, 1.6f, skinned: true);
        var shirt = Tube("Shirt", .11f, .6f, 1.2f);
        Animate("Shirt", typeof(GameObject), "m_IsActive", 0);
        Assert.That(BodyCover.HideCovered(new AvatarUsage(root), body), Is.Null, "A toggle can take the shirt off.");

        fx.layers[0].stateMachine.states = new ChildAnimatorState[0];
        shirt.sharedMaterial.renderQueue = 3000;
        Assert.That(BodyCover.HideCovered(new AvatarUsage(root), body), Is.Null, "A transparent shirt shows the body.");
    }

    // ---- Bounds ----

    [Test] public void MeshesThatFollowTheHipsShareOneBoxAroundTheAvatar()
    {
        var human = Humanoid();
        var hips = human["Hips"];
        var shirt = Weighted("Shirt", human["Chest"], human["HandL"]);
        var sock = Weighted("Sock", human["FootR"]);
        sock.rootBone = human["FootR"]; sock.localBounds = new Bounds(Vector3.zero, Vector3.one * .01f);
        // A prop held by a constraint can leave the avatar: its box stays its own.
        var held = Child("Held", human["HandR"]).transform;
        held.gameObject.AddComponent<UnityEngine.Animations.ParentConstraint>();
        var prop = Weighted("Prop", held);
        var propBox = prop.localBounds;

        Assert.That(AvatarBounds.Unify(root), Is.EqualTo(2));

        Assert.That(shirt.rootBone, Is.SameAs(hips));
        Assert.That(sock.rootBone, Is.SameAs(hips));
        Assert.That(sock.localBounds, Is.EqualTo(shirt.localBounds));
        var box = shirt.localBounds;
        foreach (var bone in new[] { human["HandL"], human["Chest"], human["FootR"] })
            Assert.That(box.Contains(hips.InverseTransformPoint(bone.position)), Is.True, bone.name + " is inside the box.");
        Assert.That(box.Contains(hips.InverseTransformPoint(human["HandL"].position + Vector3.up * .2f)), Is.True, "A raised hand stays inside.");
        Assert.That(prop.rootBone, Is.Not.SameAs(hips));
        Assert.That(prop.localBounds, Is.EqualTo(propBox));
    }

    // ---- Fixture ----

    private GameObject Child(string name, Transform parent = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent != null ? parent : root.transform, false);
        return go;
    }

    // A chain of transforms under the root, each 0.1 above the last; returns its first.
    private Transform Chain(string name, int length)
    {
        var first = Child(name).transform;
        var at = first;
        for (int i = 1; i < length; i++)
        {
            var next = new GameObject(name + i).transform;
            next.SetParent(at, false);
            next.localPosition = new Vector3(0, .1f, 0);
            at = next;
        }
        return first;
    }

    // A skinned triangle per transform, weighted to it.
    private SkinnedMeshRenderer Weighted(string name, params Transform[] to)
    {
        var renderer = Child(name).AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(new Mesh { name = name });
        var vertices = new List<Vector3>(); var weights = new List<BoneWeight>();
        for (int b = 0; b < to.Length; b++)
            foreach (var corner in new[] { Vector3.zero, Vector3.right, Vector3.up })
            {
                vertices.Add(to[b].position + corner * .05f);
                weights.Add(new BoneWeight { boneIndex0 = b, weight0 = 1 });
            }
        mesh.SetVertices(vertices);
        mesh.boneWeights = weights.ToArray();
        mesh.triangles = Enumerable.Range(0, vertices.Count).ToArray();
        mesh.bindposes = to.Select(t => t.worldToLocalMatrix).ToArray();
        renderer.sharedMesh = mesh;
        renderer.bones = to;
        return renderer;
    }

    // An open tube along Y with outward faces: skinned to the first bone, or a plain mesh. With a hole, the faces of the
    // tube's side towards +X between 0.85 and 0.95 are left out.
    private Renderer Tube(string name, float radius, float bottom, float top, bool skinned = false, bool hole = false)
    {
        const int around = 48, rings = 64;
        var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var indices = new List<int>();
        for (int r = 0; r <= rings; r++)
            for (int a = 0; a < around; a++)
            {
                float angle = a * Mathf.PI * 2f / around;
                var n = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                vertices.Add(n * radius + Vector3.up * Mathf.Lerp(bottom, top, r / (float)rings));
                normals.Add(n);
            }
        for (int r = 0; r < rings; r++)
            for (int a = 0; a < around; a++)
            {
                int i0 = r * around + a, i1 = r * around + (a + 1) % around, i2 = i0 + around, i3 = i1 + around;
                var mid = (vertices[i0] + vertices[i3]) * .5f;
                if (hole && mid.y > .85f && mid.y < .95f && mid.x > 0f && Mathf.Abs(mid.z) < .05f) continue;
                indices.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
        var mesh = Own(new Mesh { name = name });
        mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetTriangles(indices, 0);
        var material = Own(new Material(Shader.Find("Standard")) { name = name });
        var go = Child(name);
        if (!skinned)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var plain = go.AddComponent<MeshRenderer>(); plain.sharedMaterial = material;
            return plain;
        }
        mesh.boneWeights = vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
        mesh.bindposes = new[] { bones[0].worldToLocalMatrix };
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = new[] { bones[0] }; renderer.rootBone = bones[0]; renderer.sharedMaterial = material;
        return renderer;
    }

    private static List<Vector3> Triangles(Mesh mesh)
    {
        var v = mesh.vertices; var t = mesh.triangles;
        var centers = new List<Vector3>();
        for (int i = 0; i + 2 < t.Length; i += 3) centers.Add((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f);
        return centers;
    }

    private void Animate(string path, System.Type type, string property, float value)
    {
        var clip = Own(new AnimationClip());
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));
        fx.layers[0].stateMachine.AddState(path + " " + property).motion = clip;
    }

    // A strip of quads up the chain, one quad per listed bone (weighted to it), each submesh one quad.
    private SkinnedMeshRenderer Skinned(string name, int[] weighted, int submeshes = 1, string shape = null, Vector3 offset = default)
    {
        var renderer = Child(name).AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(new Mesh { name = name });
        var vertices = new List<Vector3>(); var weights = new List<BoneWeight>();
        int quads = Mathf.Max(weighted.Length, submeshes);
        for (int q = 0; q < quads; q++)
        {
            int bone = weighted[q % weighted.Length];
            float y = bone + .1f * (q / weighted.Length);
            foreach (var corner in new[] { new Vector3(0, y, 0), new Vector3(1, y, 0), new Vector3(0, y + .5f, 0), new Vector3(1, y + .5f, 0) })
            {
                vertices.Add(corner + offset);
                weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
            }
        }
        mesh.SetVertices(vertices);
        mesh.boneWeights = weights.ToArray();
        mesh.subMeshCount = submeshes;
        for (int s = 0; s < submeshes; s++)
        {
            var quadsOf = Enumerable.Range(0, quads).Where(q => q % submeshes == s).ToList();
            mesh.SetIndices(quadsOf.SelectMany(q => new[] { 4 * q, 4 * q + 2, 4 * q + 1, 4 * q + 1, 4 * q + 2, 4 * q + 3 }).ToArray(), MeshTopology.Triangles, s);
        }
        mesh.RecalculateNormals();
        // Bound with the mesh moved by offset: each bind pose maps the mesh space into the bone's.
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * Matrix4x4.Translate(-offset)).ToArray();
        if (shape != null) mesh.AddBlendShapeFrame(shape, 100, vertices.Select(_ => Vector3.forward).ToArray(), null, null);
        renderer.sharedMesh = mesh;
        renderer.bones = bones;
        renderer.rootBone = bones[0];
        renderer.sharedMaterial = Own(new Material(Shader.Find("Standard")) { name = name });
        return renderer;
    }

    private static Mesh Quad() => new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };

    // A pose away from the rest pose, so wrong bind poses would show.
    private void Pose()
    {
        bones[1].localRotation = Quaternion.Euler(0, 0, 30);
        bones[2].localRotation = Quaternion.Euler(20, 0, 0);
        bones[3].localScale = new Vector3(1.2f, 1, 1);
    }

    private List<Vector3> World(SkinnedMeshRenderer renderer)
    {
        var baked = Own(new Mesh());
        renderer.BakeMesh(baked, true);
        return baked.vertices.Select(v => renderer.transform.TransformPoint(v)).ToList();
    }

    private static void AssertSame(List<Vector3> expected, List<Vector3> actual)
    {
        Assert.That(actual.Count, Is.EqualTo(expected.Count));
        for (int i = 0; i < expected.Count; i++)
            Assert.That(Vector3.Distance(expected[i], actual[i]), Is.LessThan(1e-4f), "Vertex " + i + " moved: " + expected[i] + " -> " + actual[i]);
    }

    // A humanoid skeleton under the avatar (hips 1 m up) and its Animator.
    private Dictionary<string, Transform> Humanoid()
    {
        var human = new Dictionary<string, Transform>();
        void Add(string name, string parent, Vector3 position)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent == null ? root.transform : human[parent], false);
            bone.localPosition = position;
            human[name] = bone;
        }
        Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
        Add("Neck", "Chest", new Vector3(0, .24f, 0)); Add("Head", "Neck", new Vector3(0, .08f, 0));
        foreach (var side in new[] { "L", "R" })
        {
            float x = side == "L" ? -1 : 1;
            Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
            Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
            Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
            Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
        }
        var map = new Dictionary<string, string>
        {
            ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
            ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
            ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
            ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
        };
        var description = new HumanDescription
        {
            human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
        };
        var animator = root.GetComponent<Animator>();
        if (animator == null) animator = root.AddComponent<Animator>();
        animator.avatar = Own(AvatarBuilder.BuildHumanAvatar(root, description));
        Assert.That(animator.isHuman, Is.True);
        return human;
    }

    private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
}
