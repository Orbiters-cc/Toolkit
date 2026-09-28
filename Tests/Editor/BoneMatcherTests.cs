using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Armature;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class BoneMatcherTests
{
    private readonly List<Object> owned = new List<Object>();

    [TearDown] public void TearDown()
    {
        foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
    }

    // Unity / VRChat
    [TestCase("Hips", HumanBodyBones.Hips)]
    [TestCase("UpperChest", HumanBodyBones.UpperChest)]
    [TestCase("LeftUpperArm", HumanBodyBones.LeftUpperArm)]
    [TestCase("RightLowerLeg", HumanBodyBones.RightLowerLeg)]
    [TestCase("LeftIndexProximal", HumanBodyBones.LeftIndexProximal)]
    [TestCase("Left Thumb Intermediate", HumanBodyBones.LeftThumbIntermediate)]
    [TestCase("RightLittleDistal", HumanBodyBones.RightLittleDistal)]
    [TestCase("LeftEye", HumanBodyBones.LeftEye)]
    [TestCase("Jaw", HumanBodyBones.Jaw)]
    // Mixamo
    [TestCase("mixamorig:Hips", HumanBodyBones.Hips)]
    [TestCase("mixamorig:LeftUpLeg", HumanBodyBones.LeftUpperLeg)]
    [TestCase("mixamorig:LeftLeg", HumanBodyBones.LeftLowerLeg)]
    [TestCase("mixamorig_RightLeg", HumanBodyBones.RightLowerLeg)]
    [TestCase("LeftLeg", HumanBodyBones.LeftLowerLeg)]
    [TestCase("mixamorig:LeftArm", HumanBodyBones.LeftUpperArm)]
    [TestCase("mixamorig:LeftForeArm", HumanBodyBones.LeftLowerArm)]
    [TestCase("mixamorig:RightToeBase", HumanBodyBones.RightToes)]
    [TestCase("mixamorig:LeftHandIndex1", HumanBodyBones.LeftIndexProximal)]
    [TestCase("mixamorig:RightHandThumb3", HumanBodyBones.RightThumbDistal)]
    [TestCase("mixamorig:Spine1", HumanBodyBones.Chest)]
    [TestCase("mixamorig:Spine2", HumanBodyBones.UpperChest)]
    // Blender / Rigify
    [TestCase("upper_arm.L", HumanBodyBones.LeftUpperArm)]
    [TestCase("forearm.L", HumanBodyBones.LeftLowerArm)]
    [TestCase("hand.R", HumanBodyBones.RightHand)]
    [TestCase("shoulder.L", HumanBodyBones.LeftShoulder)]
    [TestCase("thigh.R", HumanBodyBones.RightUpperLeg)]
    [TestCase("shin.R", HumanBodyBones.RightLowerLeg)]
    [TestCase("foot.L", HumanBodyBones.LeftFoot)]
    [TestCase("toe.L", HumanBodyBones.LeftToes)]
    [TestCase("f_index.01.L", HumanBodyBones.LeftIndexProximal)]
    [TestCase("f_middle.02.R", HumanBodyBones.RightMiddleIntermediate)]
    [TestCase("f_pinky.03.L", HumanBodyBones.LeftLittleDistal)]
    [TestCase("thumb.01.L", HumanBodyBones.LeftThumbProximal)]
    [TestCase("Eye.L", HumanBodyBones.LeftEye)]
    [TestCase("DEF-thigh.L", HumanBodyBones.LeftUpperLeg)]
    // Blender copies along finger chains (Alphazear): Thumb.L, Thumb.L.001, Thumb.L.002
    [TestCase("Thumb.L", HumanBodyBones.LeftThumbProximal)]
    [TestCase("Thumb.L.001", HumanBodyBones.LeftThumbIntermediate)]
    [TestCase("IndexFinger.R.002", HumanBodyBones.RightIndexDistal)]
    [TestCase("Leg_L", HumanBodyBones.LeftUpperLeg)]
    [TestCase("LowerLeg.R", HumanBodyBones.RightLowerLeg)]
    [TestCase("Toe_Base_L", HumanBodyBones.LeftToes)]
    // VRoid
    [TestCase("J_Bip_C_Hips", HumanBodyBones.Hips)]
    [TestCase("J_Bip_C_UpperChest", HumanBodyBones.UpperChest)]
    [TestCase("J_Bip_L_UpperArm", HumanBodyBones.LeftUpperArm)]
    [TestCase("J_Bip_R_Index1", HumanBodyBones.RightIndexProximal)]
    [TestCase("J_Bip_L_Thumb3", HumanBodyBones.LeftThumbDistal)]
    [TestCase("J_Bip_R_ToeBase", HumanBodyBones.RightToes)]
    [TestCase("J_Adj_L_FaceEye", HumanBodyBones.LeftEye)]
    // Rexouium and bases derived from it
    [TestCase("Spine", HumanBodyBones.Spine)]
    [TestCase("Chest", HumanBodyBones.Chest)]
    [TestCase("ChestUp", HumanBodyBones.UpperChest)]
    [TestCase("Chest Up", HumanBodyBones.UpperChest)]
    [TestCase("Neck", HumanBodyBones.Neck)]
    [TestCase("Left shoulder", HumanBodyBones.LeftShoulder)]
    [TestCase("Left arm", HumanBodyBones.LeftUpperArm)]
    [TestCase("Left elbow", HumanBodyBones.LeftLowerArm)]
    [TestCase("Right wrist", HumanBodyBones.RightHand)]
    [TestCase("Left leg", HumanBodyBones.LeftUpperLeg)]
    [TestCase("Right knee", HumanBodyBones.RightLowerLeg)]
    [TestCase("Left ankle", HumanBodyBones.LeftFoot)]
    [TestCase("Left toe", HumanBodyBones.LeftToes)]
    [TestCase("IndexFinger1_L", HumanBodyBones.LeftIndexProximal)]
    [TestCase("ThumbFinger3_R", HumanBodyBones.RightThumbDistal)]
    [TestCase("PinkyFinger2_L", HumanBodyBones.LeftLittleIntermediate)]
    [TestCase("Eye_R", HumanBodyBones.RightEye)]
    // Other common names
    [TestCase("Hip", HumanBodyBones.Hips)]
    [TestCase("Pelvis", HumanBodyBones.Hips)]
    [TestCase("UpperLeg_L", HumanBodyBones.LeftUpperLeg)]
    [TestCase("Thigh_L", HumanBodyBones.LeftUpperLeg)]
    [TestCase("Calf_R", HumanBodyBones.RightLowerLeg)]
    [TestCase("Clavicle_L", HumanBodyBones.LeftShoulder)]
    [TestCase("Shoulder.L", HumanBodyBones.LeftShoulder)]
    [TestCase("Toe_R", HumanBodyBones.RightToes)]
    [TestCase("LeftToeBase", HumanBodyBones.LeftToes)]
    [TestCase("Bip01 L Thigh", HumanBodyBones.LeftUpperLeg)]
    [TestCase("Bip01 Pelvis", HumanBodyBones.Hips)]
    [TestCase("spine_01", HumanBodyBones.Spine)]
    [TestCase("spine_03", HumanBodyBones.UpperChest)]
    [TestCase("neck_01", HumanBodyBones.Neck)]
    [TestCase("ball_l", HumanBodyBones.LeftToes)]
    [TestCase("CC_Base_L_Thigh", HumanBodyBones.LeftUpperLeg)]
    public void InfersHumanoidBone(string name, HumanBodyBones expected)
    {
        Assert.True(BoneNames.TryInferHumanoid(name, out var bone), name);
        Assert.AreEqual(expected, bone, name);
    }

    [TestCase("Armature")]
    [TestCase("Armature.006")]
    [TestCase("Hips.001")]
    [TestCase("Head.012")]
    [TestCase("thigh.L.004")]
    [TestCase("Foot.L.001")]
    [TestCase("Toe.L.002")]
    [TestCase("Head_end")]
    [TestCase("Left leg_end")]
    [TestCase("HeadTop_End")]
    [TestCase("mixamorig:LeftHandIndex4")]
    [TestCase("RingFinger.L.003")]
    [TestCase("ThumbFinger3_R.merge")]
    [TestCase("Breast.L")]
    [TestCase("ArmBand.L")]
    [TestCase("HeadAccessory")]
    [TestCase("HairRoot")]
    [TestCase("HairJoint_7d4b5491_5cf1_4a9d_b642_6db73c3a6380")]
    [TestCase("J_Sec_Hair1_01")]
    [TestCase("Hand_Target")]
    [TestCase("Chest Pin (Put me in armature)")]
    [TestCase("Scalf Root, Place on chest")]
    [TestCase("EarRT_L")]
    [TestCase("FoxEar_Bottom.L")]
    [TestCase("Ear.L")]
    [TestCase("EyeManual.L")]
    [TestCase("palm.01.L")]
    [TestCase("toe1.L")]
    [TestCase("ToeIndex1_L")]
    [TestCase("EarRing_L")]
    [TestCase("Fthr_UpArm1_L")]
    [TestCase("FthrElbow1_R")]
    [TestCase("Planti_Left_Knee")]
    [TestCase("L_HoodString1")]
    [TestCase("Hood root")]
    [TestCase("Bone.012")]
    [TestCase("Tail_Rt")]
    [TestCase("NeckGRP")]
    [TestCase("Jaw2")]
    [TestCase("upper arm")]
    [TestCase("Toe")]
    [TestCase("Eye")]
    [TestCase("Hips_L")]
    [TestCase("")]
    public void LeavesNonHumanoidNames(string name)
    {
        Assert.False(BoneNames.TryInferHumanoid(name, out var bone), $"{name} inferred as {bone}");
    }

    [TestCase("mixamorig:Left_Arm", "leftarm")]
    [TestCase("Upper_Leg.L", "upperlegl")]
    [TestCase("Chest Up", "chestup")]
    [TestCase("f-index", "findex")]
    public void Normalizes(string name, string expected) => Assert.AreEqual(expected, BoneNames.Normalize(name));

    [TestCase("Left arm", BodySide.Left, "Right arm")]
    [TestCase("upper_arm.L", BodySide.Left, "upper_arm.R")]
    [TestCase("Thumb.R.001", BodySide.Right, "Thumb.L.001")]
    [TestCase("mixamorig:LeftHand", BodySide.Left, "mixamorig:RightHand")]
    [TestCase("J_Bip_R_UpperArm", BodySide.Right, "J_Bip_L_UpperArm")]
    [TestCase("EarRT_L", BodySide.Left, "EarRT_R")]
    [TestCase("FoxEar_Bottom.L", BodySide.Left, "FoxEar_Bottom.R")]
    [TestCase("L_HoodString1", BodySide.Left, "R_HoodString1")]
    [TestCase("Planti_Left_Knee", BodySide.Left, "Planti_Right_Knee")]
    [TestCase("LEFT_HAND", BodySide.Left, "RIGHT_HAND")]
    [TestCase("HandR", BodySide.Right, "HandL")]
    [TestCase("Hips", BodySide.None, null)]
    [TestCase("NoseRT", BodySide.None, null)]
    [TestCase("Tail_Rt", BodySide.None, null)]
    [TestCase("BR Tentacle Base", BodySide.None, null)]
    [TestCase("Leftover", BodySide.None, null)]
    [TestCase("L_R_Strap", BodySide.None, null)]
    public void ReadsAndMirrorsSides(string name, BodySide side, string mirrored)
    {
        Assert.AreEqual(side, BoneNames.Side(name), name);
        Assert.AreEqual(mirrored, BoneNames.Mirror(name), name);
    }

    [TestCase("Armature", true)]
    [TestCase("Armature.006", true)]
    [TestCase("Scalf Armature", true)]
    [TestCase("Armature_NSFW", true)]
    [TestCase("Skeleton", true)]
    [TestCase("Rig", true)]
    [TestCase("Root", true)]
    [TestCase("Hips", false)]
    [TestCase("HairRoot", false)]
    [TestCase("Root_Ears", false)]
    [TestCase("Chest Pin (Put me in armature)", false)]
    public void RecognizesArmatureContainers(string name, bool expected) => Assert.AreEqual(expected, BoneNames.IsArmatureContainer(name));

    [TestCase("Head_end", true)]
    [TestCase("FoxEar_Top.L_end", true)]
    [TestCase("HeadTop_End", true)]
    [TestCase("2_end", true)]
    [TestCase("Bend", false)]
    [TestCase("Head", false)]
    public void RecognizesEndLeaves(string name, bool expected) => Assert.AreEqual(expected, BoneNames.IsEnd(name));

    [Test]
    public void MatchesRexouiumClothingOntoBlenderNamedAvatar()
    {
        var avatar = BlenderAvatar(out var bones);
        var index = AvatarBoneIndex.Build(avatar, bones.Values);
        Assert.AreSame(bones["Head"], index.Humanoid(HumanBodyBones.Head));
        Assert.AreSame(bones["upper_arm.L"], index.Humanoid(HumanBodyBones.LeftUpperArm));
        Assert.IsNull(index.Humanoid(HumanBodyBones.UpperChest));

        // Rexouium hoodie armature.
        var hoodie = Node("Hoodie", null);
        var armature = Node("Armature", hoodie);
        var hips = Node("Hips", armature);
        var chest = Node("Chest", Node("Spine", hips));
        var chestUp = Node("ChestUp", chest);
        var head = Node("Head", Node("Neck", chestUp));
        var headEnd = Node("Head_end", head);
        var leftArm = Node("Left arm", Node("Left shoulder", chestUp));
        var leftElbow = Node("Left elbow", leftArm);
        var string1 = Node("L_HoodString1", chestUp);
        var leftLeg = Node("Left leg", hips);
        var sources = hoodie.GetComponentsInChildren<Transform>(true).Where(t => t != hoodie).ToList();

        var matches = BoneMatcher.MatchHierarchy(sources, index).ToDictionary(m => m.Source);
        Assert.AreEqual(sources.Count, matches.Count);
        Expect(matches[hips], bones["Hips"], BoneMatchKind.ExactName);
        Expect(matches[head], bones["Head"], BoneMatchKind.ExactName);
        Expect(matches[leftArm], bones["upper_arm.L"], BoneMatchKind.Humanoid);
        Expect(matches[leftElbow], bones["forearm.L"], BoneMatchKind.Humanoid);
        Expect(matches[leftLeg], bones["Left leg"], BoneMatchKind.ExactName);
        // The avatar has no upper chest: ChestUp would land on its parent's Chest, so it stays an extra bone.
        Assert.False(matches[chestUp].Matched);
        Assert.False(matches[headEnd].Matched);
        Assert.False(matches[string1].Matched);
        Expect(matches[armature], bones["Armature"], BoneMatchKind.ExactName);
        Assert.True(matches.Values.Where(m => m.Matched).All(m => !m.Ambiguous && m.Confidence >= 0.8f));
    }

    [Test]
    public void MatchesOtherNamingSchemesByHumanoidRole()
    {
        var avatar = BlenderAvatar(out var bones);
        var index = AvatarBoneIndex.Build(avatar, bones.Values);
        var clothing = Node("Clothing", null);
        Expect(BoneMatcher.Match(Node("mixamorig:LeftForeArm", clothing), index), bones["forearm.L"], BoneMatchKind.Humanoid);
        Expect(BoneMatcher.Match(Node("J_Bip_L_UpperArm", clothing), index), bones["upper_arm.L"], BoneMatchKind.Humanoid);
        Expect(BoneMatcher.Match(Node("IndexFinger1_L", clothing), index), bones["f_index.01.L"], BoneMatchKind.Humanoid);
        Expect(BoneMatcher.Match(Node("Thigh_R", clothing), index), bones["Right leg"], BoneMatchKind.Humanoid);
        Expect(BoneMatcher.Match(Node("mixamorig:Head", clothing), index), bones["Head"], BoneMatchKind.ExactName);
        Assert.False(BoneMatcher.Match(Node("Brim", clothing), index).Matched);
    }

    [Test]
    public void StripsTheClothingSuffix()
    {
        var avatar = BlenderAvatar(out var bones);
        var index = AvatarBoneIndex.Build(avatar, bones.Values);
        var shirt = Node("Shirt", null);
        var hips = Node("Hips_Shirt", shirt);
        var chest = Node("Chest_Shirt", Node("Spine_Shirt", hips));
        var arm = Node("Left arm_Shirt", Node("shoulder.L_Shirt", chest));
        var sources = shirt.GetComponentsInChildren<Transform>(true).Where(t => t != shirt).ToList();

        var options = BoneMatcher.DetectAffixes(sources, index);
        Assert.NotNull(options);
        Assert.AreEqual("_Shirt", options.Suffix);
        Assert.IsNull(options.Prefix);
        var matches = BoneMatcher.MatchHierarchy(sources, index, options).ToDictionary(m => m.Source);
        Expect(matches[hips], bones["Hips"], BoneMatchKind.AffixName);
        Expect(matches[chest], bones["Chest"], BoneMatchKind.AffixName);
        Expect(matches[arm], bones["upper_arm.L"], BoneMatchKind.Humanoid);
        // Without the suffix the contained name still finds the bone, with less confidence.
        var contained = BoneMatcher.Match(hips, index);
        Assert.AreEqual(BoneMatchKind.ContainedName, contained.Kind);
        Assert.AreSame(bones["Hips"], contained.Target);
        Assert.Less(contained.Confidence, 0.8f);
    }

    [Test]
    public void DetectsNoAffixForBlenderCopiesAndEndLeaves()
    {
        var avatar = BlenderAvatar(out var bones);
        var index = AvatarBoneIndex.Build(avatar, bones.Values);
        var hair = Node("Hair", null);
        var head = Node("Head", hair);
        Node("Head.001", head); Node("Head.002", head); Node("Head_end", head); Node("Neck_end", head);
        Assert.IsNull(BoneMatcher.DetectAffixes(hair.GetComponentsInChildren<Transform>(true), index));
        var copy = BoneMatcher.MatchHierarchy(hair.GetComponentsInChildren<Transform>(true), index).Single(m => m.Source.name == "Head.001");
        Assert.False(copy.Matched);
    }

    [Test]
    public void DuplicateNamesPreferTheParentsBranch()
    {
        var avatar = BlenderAvatar(out var bones);
        // A prop on the avatar with bones named like the body's.
        var propHead = Node("Head", Node("Glowsticks", bones["Hips"]));
        var pinChest = Node("Pin", bones["Chest"]);
        var pinHead = Node("Pin", bones["Head"]);
        var index = AvatarBoneIndex.Build(avatar, avatar.GetComponentsInChildren<Transform>(true).Where(t => t != avatar));
        Assert.AreEqual(2, index.WithName("head").Count);

        var clothing = Node("Clothing", null);
        var neck = Node("Neck", clothing);
        var head = Node("Head", neck);
        var pin = Node("Pin", head);
        var matches = BoneMatcher.MatchHierarchy(new[] { pin, head, neck }, index);
        Assert.AreEqual(new[] { pin, head, neck }, matches.Select(m => m.Source).ToArray());
        Expect(matches[1], bones["Head"], BoneMatchKind.ExactName);
        Assert.False(matches[1].Ambiguous);
        Expect(matches[0], pinHead, BoneMatchKind.ExactName);
        Assert.False(matches[0].Ambiguous);

        // Alone, a pin could be either: ambiguous, the other one kept as an alternative.
        var lone = BoneMatcher.Match(Node("Pin", null), index);
        Assert.True(lone.Matched && lone.Ambiguous);
        Assert.AreEqual(1, lone.Alternatives.Count);
        CollectionAssert.AreEquivalent(new[] { pinChest, pinHead }, new[] { lone.Target, lone.Alternatives[0] });
        Assert.Less(lone.Confidence, 0.8f);
        Assert.AreNotSame(propHead, matches[1].Target);
    }

    [Test]
    public void RestFramesComeFromBindPoses()
    {
        var root = Node("Accessory", null);
        var bone = Node("Bone", root);
        bone.localPosition = new Vector3(0, 1, 0);
        var renderer = Node("Mesh", root).gameObject.AddComponent<SkinnedMeshRenderer>();
        var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } });
        mesh.bindposes = new[] { bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix };
        renderer.sharedMesh = mesh;
        renderer.bones = new[] { bone };
        renderer.rootBone = bone;
        bone.localPosition = new Vector3(0, 2, 0);
        bone.localRotation = Quaternion.Euler(0, 90, 0);

        var frames = ArmatureRest.Frames(new[] { renderer });
        Assert.Less(Vector3.Distance(new Vector3(0, 1, 0), frames[bone].GetColumn(3)), 1e-5f);
        Assert.AreSame(root, ArmatureRest.CommonRoot(renderer, null));
        Assert.IsNull(ArmatureRest.CommonRoot(renderer, root));
    }

    private static void Expect(BoneMatch match, Transform target, BoneMatchKind kind)
    {
        Assert.AreSame(target, match.Target, match.ToString());
        Assert.AreEqual(kind, match.Kind, match.ToString());
    }

    // An avatar named like MCB's MasculineCanine base (Blender/Rigify limbs, Rexouium legs), without a humanoid Animator.
    private Transform BlenderAvatar(out Dictionary<string, Transform> bones)
    {
        var map = new Dictionary<string, Transform>();
        Transform Add(string name, Transform parent) => map[name] = Node(name, parent);
        var avatar = Node("Avatar", null);
        var hips = Add("Hips", Add("Armature", avatar));
        var chest = Add("Chest", Add("Spine", hips));
        var head = Add("Head", Add("Neck", chest));
        Add("HairRoot", head);
        Add("LeftEye", head);
        foreach (var side in new[] { "L", "R" })
        {
            var hand = Add($"hand.{side}", Add($"forearm.{side}", Add($"upper_arm.{side}", Add($"shoulder.{side}", chest))));
            Add($"f_index.02.{side}", Add($"f_index.01.{side}", hand));
            Add($"toe.{side}", Add($"foot.{side}", Add($"shin.{side}", Add(side == "L" ? "Left leg" : "Right leg", hips))));
        }
        bones = map;
        return avatar;
    }

    private Transform Node(string name, Transform parent)
    {
        var node = new GameObject(name).transform;
        if (parent == null) owned.Add(node.gameObject);
        else node.SetParent(parent, false);
        return node;
    }

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
