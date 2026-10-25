using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

// N4N0's capture kit and camera rig (contracts Docs/rig-frames.md, N4N0's capture kit): the rig and its cameras in the
// prefab, the controller and parameters Orbiters - N4N0 drives, the kit made for the avatar as it is built, the shaders
// as the contracts have them. What the kit draws into a camera's frames is checked in Mixer, which has the contracts'
// decoders (N4N0CaptureKitTests).
public class OrbCaptureTests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;
    private readonly List<Object> owned = new List<Object>();

    [SetUp]
    public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersOrbCaptureTest-" + Guid.NewGuid().ToString("N");
        scene = EditorSceneManager.NewPreviewScene();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        sandbox.End();
    }

    [Test]
    public void N4N0CarriesTheRigAndTheKitsRenderer()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var root = prefab.transform;
        Assert.IsTrue(OrbInstaller.HasCapture(root));
        Assert.AreEqual(root, root.Find(OrbInstaller.Capture).parent, "Beside Shown: hiding N4N0 leaves capture working");
        var rig = root.Find(OrbInstaller.CaptureRigPath);
        var constraint = rig.GetComponent<VRCParentConstraint>();
        Assert.AreEqual(2, constraint.Sources.Count, "the world origin (offsets from the animator), then the hips");
        Assert.IsNull(constraint.Sources[0].SourceTransform);
        Assert.AreEqual((0f, 1f), (constraint.Sources[0].Weight, constraint.Sources[1].Weight), "resting at the hips until asked to follow");
        var renderer = root.Find(OrbInstaller.RigRendererPath).GetComponent<MeshRenderer>();
        Assert.IsFalse(renderer.enabled);
        Assert.AreEqual(OrbInstaller.RigShaderName, renderer.sharedMaterial.shader.name);
        Assert.IsTrue(renderer.sharedMaterial.shader.isSupported, "the rig's shader compiles");
        for (int face = 1; face <= 6; face++)
        {
            var camera = root.Find(OrbInstaller.RigFacePath(face)).GetComponent<Camera>();
            Assert.IsFalse(camera.enabled, "off until the rig layer");
            Assert.AreEqual((90f, OrbInstaller.RigNear, OrbInstaller.RigFar, -100f), (camera.fieldOfView, camera.nearClipPlane, camera.farClipPlane, camera.depth));
            Assert.Less(Vector3.Distance(camera.transform.localRotation * Vector3.forward, OrbInstaller.RigBases[face - 1, 2]), 1e-5f, "face " + face + " forward");
            Assert.Less(Vector3.Distance(camera.transform.localRotation * Vector3.up, OrbInstaller.RigBases[face - 1, 1]), 1e-5f, "face " + face + " up");
            Assert.AreSame(camera.targetTexture, renderer.sharedMaterial.GetTexture("_OrbitersRigFace" + face));
            Assert.AreEqual((RenderTextureFormat.ARGBHalf, 512), (camera.targetTexture.format, camera.targetTexture.width), "HDR: the world's tonemapping treats the faces as the scene");
            StringAssert.StartsWith(folder + "/", AssetDatabase.GetAssetPath(camera.targetTexture));
        }
        var kit = root.Find(OrbInstaller.CaptureKitPath).GetComponent<SkinnedMeshRenderer>();
        Assert.IsFalse(kit.enabled);
        Assert.IsNull(kit.sharedMesh, "made for the avatar as it is built");
        Assert.AreEqual(OrbInstaller.KitShaderName, kit.sharedMaterial.shader.name);
        Assert.IsTrue(kit.sharedMaterial.shader.isSupported, "the kit's shader compiles");
        // Nothing from the Toolkit's package: a gallery package carries it all.
        Assert.IsFalse(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(prefab), true).Any(p => p.StartsWith("Packages/orbiters.", StringComparison.Ordinal)));
    }

    [Test]
    public void OrbitersN4N0DrivesItThroughLocalParameters()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        CollectionAssert.AreEqual(new[]
        {
            "N4N0/Capture/Record", "N4N0/Capture/Depth", "N4N0/Capture/Follow", "N4N0/Capture/Rig", "N4N0/Capture/RigX", "N4N0/Capture/RigY", "N4N0/Capture/RigZ",
            "N4N0/Capture/RigYaw",
        }, OrbInstaller.CaptureParameters, "the contracts' orb::params::CAPTURE");
        var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
        var full = VrcFury.Features(prefab).Single(f => f.kind == "FullController").feature;
        foreach (string name in OrbInstaller.CaptureParameters)
        {
            var p = parameters.parameters.Single(q => q.name == name);
            Assert.IsFalse(p.networkSynced || p.saved, name + ": local and never saved");
            Assert.AreEqual(name.StartsWith("N4N0/Capture/Rig", StringComparison.Ordinal) && name != OrbInstaller.CaptureRig ? VRCExpressionParameters.ValueType.Float : VRCExpressionParameters.ValueType.Bool, p.valueType, name);
            Assert.IsTrue(VrcFury.IsGlobalParameter(full, name), name + " keeps its name for OSC");
        }
        // No parameter is also a folder (VRChat's OSCQuery leaves such a parameter out).
        var names = parameters.parameters.Select(p => p.name).ToList();
        Assert.IsFalse(names.Any(a => names.Any(b => b.StartsWith(a + "/", StringComparison.Ordinal))), "a parameter that is also a folder");
        var record = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Menu.asset").controls.Single().subMenu.controls.Single(c => c.name == "Record");
        Assert.AreEqual((VRCExpressionsMenu.Control.ControlType.Toggle, OrbInstaller.CaptureRecord), (record.type, record.parameter.name));

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/N4N0.controller");
        var layers = controller.layers.ToDictionary(l => l.name, l => l.stateMachine);
        foreach (string layer in new[] { "Capture", "Capture local", "Capture rig", "Capture follow", "Capture rig pose" }) Assert.IsTrue(layers.ContainsKey(layer), layer);
        float Value(AnimatorState state, string path, Type type, string property)
        {
            var curve = AnimationUtility.GetEditorCurve((AnimationClip)state.motion, EditorCurveBinding.FloatCurve(path, type, property));
            Assert.IsNotNull(curve, state.name + ": " + path + " " + property);
            return curve.Evaluate(0);
        }
        AnimatorState In(string layer, string state) => layers[layer].states.Select(s => s.state).Single(s => s.name == state);
        // The kit: drawn while recording, depth frames one in four, the rig's renderer with the same cadence.
        Assert.AreEqual("Off", layers["Capture"].defaultState.name);
        Assert.AreEqual(0f, Value(In("Capture", "Off"), OrbInstaller.CaptureKitPath, typeof(SkinnedMeshRenderer), "m_Enabled"));
        Assert.AreEqual(1f, Value(In("Capture", "Recording"), OrbInstaller.CaptureKitPath, typeof(SkinnedMeshRenderer), "m_Enabled"));
        var depth = In("Capture", "Recording with depth frames");
        Assert.AreEqual(4f, Value(depth, OrbInstaller.CaptureKitPath, typeof(SkinnedMeshRenderer), "material._OrbitersDepthEvery"));
        Assert.AreEqual(4f, Value(depth, OrbInstaller.RigRendererPath, typeof(MeshRenderer), "material._OrbitersDepthEvery"), "rig frames are never depth frames");
        // The wearer's client only.
        Assert.AreEqual(1f, Value(In("Capture local", "Local"), OrbInstaller.CaptureKitPath, typeof(SkinnedMeshRenderer), "material._OrbitersLocal"));
        Assert.AreEqual("Remote", layers["Capture local"].defaultState.name);
        var rigOn = In("Capture rig", "Rig on");
        for (int face = 1; face <= 6; face++) Assert.AreEqual(1f, Value(rigOn, OrbInstaller.RigFacePath(face), typeof(Camera), "m_Enabled"));
        Assert.IsTrue(layers["Capture rig"].states.Single(s => s.state.name == "Rig off").state.transitions.Single().conditions.Any(c => c.parameter == "IsLocal"),
            "on only on the wearer's client");
        // Following: the world origin's source takes the weight from the hips'.
        var followingState = In("Capture follow", "Following");
        Assert.AreEqual((1f, 0f), (Value(followingState, OrbInstaller.CaptureRigPath, typeof(VRCParentConstraint), "Sources.source0.Weight"),
            Value(followingState, OrbInstaller.CaptureRigPath, typeof(VRCParentConstraint), "Sources.source1.Weight")));
        Assert.AreEqual("At the hips", layers["Capture follow"].defaultState.name);
        // Placed by the four values, linear from -1 to 1 over ±4096 m and ±180°, in a direct tree with Write Defaults on.
        var pose = layers["Capture rig pose"].defaultState;
        Assert.IsTrue(pose.writeDefaultValues, "a direct blend tree needs Write Defaults");
        var tree = (BlendTree)pose.motion;
        Assert.AreEqual(BlendTreeType.Direct, tree.blendType);
        Assert.IsTrue(tree.children.All(c => c.directBlendParameter == "N4N0/One"));
        var x = (BlendTree)tree.children.Single(c => ((BlendTree)c.motion).blendParameter == OrbInstaller.CaptureRigX).motion;
        CollectionAssert.AreEqual(new[] { -1f, 1f }, x.children.Select(c => c.threshold));
        var end = (AnimationClip)x.children[1].motion;
        var binding = AnimationUtility.GetCurveBindings(end).Single();
        Assert.AreEqual((OrbInstaller.CaptureRigPath, typeof(VRCParentConstraint), "Sources.source0.ParentPositionOffset.x"), (binding.path, binding.type, binding.propertyName));
        Assert.AreEqual(4096f, AnimationUtility.GetEditorCurve(end, binding).keys[0].value);
        var yaw = (BlendTree)tree.children.Single(c => ((BlendTree)c.motion).blendParameter == OrbInstaller.CaptureRigYaw).motion;
        Assert.AreEqual(180f, AnimationUtility.GetEditorCurve((AnimationClip)yaw.children[1].motion, AnimationUtility.GetCurveBindings((AnimationClip)yaw.children[1].motion).Single()).keys[0].value);
    }

    /// <summary>
    /// The kit as a PC build makes it: a triangle per humanoid bone skinned to it, the strip's, the band fill's and three
    /// header triangles on the hips, its material carrying the bones and the avatar's identity, its bounds around the
    /// rig, the rig resting at the hips.
    /// </summary>
    [Test]
    public void AnAvatarsBuildMakesTheKitForItsArmature()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, orb) = Placed();
        var copy = Object.Instantiate(avatar.gameObject);
        SceneManager.MoveGameObjectToScene(copy, scene);
        var built = copy.transform.Find(orb.name);
        var animator = copy.GetComponent<Animator>();
        var smr = OrbInstaller.BuildCaptureKit(built, animator, "Kit test avatar", folder + "/Kits");
        Assert.IsNotNull(smr);
        Assert.IsFalse(smr.enabled, "the animator turns it on");
        var present = Enumerable.Range(0, 55).Where(b => animator.GetBoneTransform((HumanBodyBones)b) != null).ToList();
        Assert.AreEqual(present.Count, smr.bones.Length);
        Assert.AreSame(animator.GetBoneTransform(HumanBodyBones.Hips), smr.bones[0]);
        var mesh = smr.sharedMesh;
        Assert.AreEqual(3 * (5 + present.Count), mesh.vertexCount);
        var uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);
        Assert.AreEqual(new Vector2(OrbInstaller.StripKind, 0), uvs[0]);
        Assert.AreEqual(new Vector2(OrbInstaller.FillKind, 0), uvs[3]);
        CollectionAssert.AreEqual(present.Select(b => (float)b), Enumerable.Range(5, present.Count).Select(t => uvs[3 * t].x));
        // Skinned through the bind poses, each bone's triangle sits at the bone with axes one metre long.
        for (int t = 5; t < mesh.vertexCount / 3; t++)
        {
            int index = mesh.boneWeights[3 * t].boneIndex0;
            var bone = smr.bones[index];
            var toWorld = bone.localToWorldMatrix * mesh.bindposes[index];
            Vector3 o = toWorld.MultiplyPoint3x4(mesh.vertices[3 * t]), xAxis = toWorld.MultiplyPoint3x4(mesh.vertices[3 * t + 1]);
            Assert.Less(Vector3.Distance(bone.position, o), 1e-4f);
            Assert.Less(Vector3.Distance(bone.right, xAxis - o), 1e-4f);
        }
        var material = smr.sharedMaterial;
        Assert.AreEqual(OrbInstaller.MaskWords(OrbInstaller.CaptureMask(animator)), material.GetVector("_OrbitersBoneMask"));
        Assert.AreEqual(OrbInstaller.CaptureIdentity("Kit test avatar"), Mathf.RoundToInt(material.GetFloat("_OrbitersAvatarId")));
        StringAssert.StartsWith(folder + "/Kits/Kit test avatar/", AssetDatabase.GetAssetPath(material));
        StringAssert.StartsWith(folder + "/Kits/Kit test avatar/", AssetDatabase.GetAssetPath(mesh));
        Assert.AreSame(built.Find(OrbInstaller.CaptureRigPath), smr.rootBone, "its bounds stand around the rig");
        Assert.AreEqual(4f, smr.localBounds.size.x * Mathf.Abs(smr.rootBone.lossyScale.x), 1e-3f, "4 m in the world");
        var constraint = built.Find(OrbInstaller.CaptureRigPath).GetComponent<VRCParentConstraint>();
        Assert.AreSame(smr.bones[0], constraint.Sources[1].SourceTransform, "resting at the hips");
        // Built again (another upload): the same files.
        var again = OrbInstaller.BuildCaptureKit(built, animator, "Kit test avatar", folder + "/Kits");
        Assert.AreSame(mesh, again.sharedMesh);
    }

    [Test]
    public void APhoneBuildHasNoCapturePart()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var (avatar, orb) = Placed();
        var copy = Object.Instantiate(avatar.gameObject);
        SceneManager.MoveGameObjectToScene(copy, scene);
        OrbInstaller.KeepPlatformPart(copy, mobile: true);
        Assert.IsNull(copy.transform.Find(orb.name).Find(OrbInstaller.Capture), "no avatar cameras nor custom shaders on phones");
        Assert.IsNotNull(orb.Find(OrbInstaller.Capture), "the avatar in the scene keeps it");
    }

    [Test]
    public void TheHookRunsAfterThePlatformPartAndBeforeVRCFury()
    {
        int order = new OrbCaptureHook().callbackOrder;
        Assert.Greater(order, -10200, "after the platform's N4N0 is kept");
        Assert.Less(order, -10110, "before the refit and attachment steps and VRCFury");
    }

    [Test]
    public void TheIdentityIsTheContractsCrc16()
    {
        // CRC-16/CCITT-FALSE's check value.
        Assert.AreEqual(0x29B1, OrbInstaller.CaptureIdentity("123456789"));
        Assert.AreEqual(0xFFFF, OrbInstaller.CaptureIdentity(""));
    }

    [Test]
    public void CopiedShadersHaveTheirIncludesAndTheirOwnNames()
    {
        string text = OrbInstaller.CaptureShaderText("// head\r\nShader \"Hidden/Orbiters/CaptureKit\"\r\n{\r\n    #include \"UnityCG.cginc\"\r\n    #include \"OrbitersCapture.cginc\"\r\n}\r\n",
            "Hidden/Orbiters/N4N0 Capture Kit", include => include == "OrbitersCapture.cginc" ? "// capture\nfloat x;" : throw new AssertionException(include));
        Assert.AreEqual("// head\nShader \"Hidden/Orbiters/N4N0 Capture Kit\"\n{\n    #include \"UnityCG.cginc\"\n// capture\nfloat x;\n}\n\n", text);
    }

    /// <summary>The Toolkit's copies of the contracts' shaders are the contracts' own (Mixer's Contracts, when in the project).</summary>
    [Test]
    public void TheShadersAreTheContracts()
    {
        var contracts = Directory.GetDirectories("Packages").Select(p => Path.Combine(p, "Contracts", "Shaders")).FirstOrDefault(p => File.Exists(Path.Combine(p, "OrbitersCaptureKit.shader")));
        if (contracts == null) Assert.Ignore("No Orbiters contracts in this project.");
        string Normal(string path) => File.ReadAllText(path).Replace("\r\n", "\n");
        const string toolkit = "Packages/orbiters.toolkit/Editor/VRChat/";
        foreach (var (mine, theirs) in new[]
                 {
                     ("OrbCaptureKit.shader.txt", "OrbitersCaptureKit.shader"), ("OrbCaptureRig.shader.txt", "OrbitersCaptureRig.shader"),
                     ("OrbitersCapture.cginc.txt", "OrbitersCapture.cginc"), ("OrbitersSkeletonBand.cginc.txt", "OrbitersSkeletonBand.cginc"),
                 })
            Assert.AreEqual(Normal(Path.Combine(contracts, theirs)), Normal(toolkit + mine), mine + " is " + theirs + " as the contracts have it: copy it again");
    }

    private (Transform avatar, Transform orb) Placed()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        var avatar = Humanoid();
        var orb = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar)).transform;
        AttachmentHooks.Prepare(orb.gameObject, avatar);
        return (avatar, orb);
    }

    // A small humanoid in the preview scene.
    private Transform Humanoid()
    {
        var root = new GameObject("Kit test avatar");
        SceneManager.MoveGameObjectToScene(root, scene);
        var bones = new Dictionary<string, Transform>();
        void Add(string name, string parent, Vector3 position)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent == null ? root.transform : bones[parent], false);
            bone.localPosition = position;
            bones[name] = bone;
        }
        Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
        Add("Neck", "Chest", new Vector3(0, .2f, 0)); Add("Head", "Neck", new Vector3(0, .07f, 0));
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
            ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL", ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR",
            ["RightHand"] = "HandR", ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL", ["RightUpperLeg"] = "UpperLegR",
            ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
        };
        var description = new HumanDescription
        {
            human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
            skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
            upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        owned.Add(avatar);
        root.AddComponent<Animator>().avatar = avatar;
        root.AddComponent<VRCAvatarDescriptor>().ViewPosition = new Vector3(0, 1.6f, .06f);
        return root.transform;
    }
}
