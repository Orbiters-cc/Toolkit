using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

/// <summary>
/// The menu toggle Toolkit makes for an accessory without one of its creator's gets the accessory's picture as its icon.
/// Everything happens in a preview scene of its own: the user's open scene is never touched.
/// </summary>
public sealed class AttachmentIconTests
{
    private const string AvatarName = "Icon test avatar";
    private static string AvatarFolder => AttachmentIcons.Folder + "/" + AvatarName;

    private Scene scene;
    private int undoGroup;
    private bool folderExisted, orbitersExisted, iconsExisted;
    private readonly List<Object> owned = new List<Object>();

    [SetUp] public void SetUp()
    {
        if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
        orbitersExisted = AssetDatabase.IsValidFolder("Assets/Orbiters");
        iconsExisted = AssetDatabase.IsValidFolder(AttachmentIcons.Folder);
        folderExisted = AssetDatabase.IsValidFolder(AvatarFolder);
        Assert.False(folderExisted, AvatarFolder + " is left from an earlier run");
        scene = EditorSceneManager.NewPreviewScene();
        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
    }

    [TearDown] public void TearDown()
    {
        AttachmentIcons.Flush();
        Undo.RevertAllDownToGroup(undoGroup);
        if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        foreach (var value in owned) if (value != null) Object.DestroyImmediate(value);
        owned.Clear();
        if (!folderExisted && AssetDatabase.IsValidFolder(AvatarFolder)) AssetDatabase.DeleteAsset(AvatarFolder);
        if (!iconsExisted && AssetDatabase.IsValidFolder(AttachmentIcons.Folder) && AssetDatabase.FindAssets("", new[] { AttachmentIcons.Folder }).Length == 0)
            AssetDatabase.DeleteAsset(AttachmentIcons.Folder);
        if (!orbitersExisted && AssetDatabase.IsValidFolder("Assets/Orbiters") && AssetDatabase.FindAssets("", new[] { "Assets/Orbiters" }).Length == 0)
            AssetDatabase.DeleteAsset("Assets/Orbiters");
    }

    [Test] public void AnAccessoryWithoutAToggleGetsOneShowingItsPictureOnceItIsPlaced()
    {
        var avatar = Avatar();
        var hat = Hat(avatar);
        var plan = AttachmentPlanner.Analyze(hat, avatar);
        Assert.False(plan.HasToggle);
        var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true });
        var toggle = Toggles(hat).Single();
        Assert.True(OrbitersMenuIcons.Is(IconOf(toggle)), "Orbiters' default icon until the accessory is placed");

        AttachmentIcons.Flush();
        var icon = IconOf(toggle);
        Assert.NotNull(icon);
        Assert.False(OrbitersMenuIcons.Is(icon));
        string path = AssetDatabase.GetAssetPath(icon);
        Assert.AreEqual(AvatarFolder + "/Cool Hat.png", path, "beside the avatar's other generated files, named after the accessory");
        Assert.True(GeneratedAssets.IsTracked(path), "goes once the toggle does");
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        Assert.False(importer.mipmapEnabled);
        Assert.True(importer.alphaIsTransparency);
        Assert.LessOrEqual(importer.maxTextureSize, AttachmentIcons.MaxSize);
        Assert.AreNotEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
        Assert.AreEqual(icon.width, icon.height, "square");
        Assert.LessOrEqual(icon.width, AttachmentIcons.MaxSize);

        // The model alone: clear around it, the hat's own colour in the middle.
        var file = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        owned.Add(file);
        Assert.True(file.LoadImage(File.ReadAllBytes(FileUtil.GetPhysicalPath(path))));
        Assert.Less(file.GetPixel(1, 1).a, .01f, "no background in the corners");
        var middle = file.GetPixel(file.width / 2, file.height / 2);
        Assert.Greater(middle.a, .9f);
        Assert.Greater(middle.r, middle.g + .3f, "the hat is red");
        Assert.NotNull(attachment);
    }

    [Test] public void ACreatorsToggleIsKeptAndNothingIsPictured()
    {
        var avatar = Avatar();
        var hat = Hat(avatar);
        VrcFury.Writer.Toggle(hat, "Hats/Cool Hat", hat, true);
        var plan = AttachmentPlanner.Analyze(hat, avatar);
        Assert.True(plan.HasToggle);
        AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true });
        AttachmentIcons.Flush();
        Assert.AreEqual(1, Toggles(hat).Count(), "no second toggle");
        Assert.True(OrbitersMenuIcons.Is(IconOf(Toggles(hat).Single())));
        Assert.False(AssetDatabase.IsValidFolder(AvatarFolder), "no picture made");
    }

    [Test] public void AToggleGoneBeforeTheAccessoryIsPicturedGetsNoPicture()
    {
        var avatar = Avatar();
        var hat = Hat(avatar);
        AttachmentInstaller.Install(AttachmentPlanner.Analyze(hat, avatar), new AttachmentOptions { Created = true });
        Object.DestroyImmediate(Toggles(hat).Single());
        AttachmentIcons.Flush();
        Assert.False(AssetDatabase.IsValidFolder(AvatarFolder), "no picture made");
    }

    [Test] public void EachInstallPicturesItsOwnCopy()
    {
        var avatar = Avatar();
        var first = Hat(avatar);
        AttachmentInstaller.Install(AttachmentPlanner.Analyze(first, avatar), new AttachmentOptions { Created = true });
        var second = Hat(avatar);
        AttachmentInstaller.Install(AttachmentPlanner.Analyze(second, avatar), new AttachmentOptions { Created = true });
        AttachmentIcons.Flush();
        string a = AssetDatabase.GetAssetPath(IconOf(Toggles(first).Single())), b = AssetDatabase.GetAssetPath(IconOf(Toggles(second).Single()));
        Assert.AreEqual(AvatarFolder + "/Cool Hat.png", a);
        Assert.AreNotEqual(a, b, "a second copy never takes the first one's picture");
        StringAssert.StartsWith(AvatarFolder + "/Cool Hat", b);
    }

    private Transform Avatar()
    {
        var root = new GameObject(AvatarName);
        SceneManager.MoveGameObjectToScene(root, scene);
        root.AddComponent<VRCAvatarDescriptor>();
        var hips = Child("Hips", Child("Armature", root.transform, Vector3.zero), new Vector3(0, 1, 0));
        var chest = Child("Chest", Child("Spine", hips, new Vector3(0, .1f, 0)), new Vector3(0, .15f, 0));
        Child("Head", Child("Neck", chest, new Vector3(0, .25f, 0)), new Vector3(0, .1f, 0));
        return root.transform;
    }

    // A red cube named like a hat: it follows the head.
    private GameObject Hat(Transform avatar)
    {
        var hat = new GameObject("Cool Hat");
        SceneManager.MoveGameObjectToScene(hat, scene);
        hat.transform.SetParent(avatar, false);
        hat.transform.localScale = Vector3.one * .2f;
        var material = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
        owned.Add(material);
        hat.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        hat.AddComponent<MeshRenderer>().sharedMaterial = material;
        return hat;
    }

    private static Transform Child(string name, Transform parent, Vector3 local)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.localPosition = local;
        return child;
    }

    private static IEnumerable<Component> Toggles(GameObject root) => VrcFury.Features(root).Where(f => f.kind == "Toggle").Select(f => f.component);

    private static Texture2D IconOf(Component toggle)
    {
        using var serialized = new SerializedObject(toggle);
        return serialized.FindProperty("content.icon.objRef").objectReferenceValue as Texture2D;
    }
}
