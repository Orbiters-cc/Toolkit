using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// Item settings (Jacket.settings.json beside Jacket.prefab): offered on a placed item's card, read back from the item,
// applied in place, and checked against the prefab for the publishing window.
public sealed class ItemSettingsTests
{
    private string folder;
    private Scene scene;
    private GameObject prefab, placed;
    private Material red, blue;

    private const string Settings = @"{
    ""schema"": 1,
    ""settings"": [
        { ""label"": ""Hood"", ""type"": ""toggle"", ""on"": { ""show"": [""Hood""] } },
        { ""label"": ""Style"", ""type"": ""choice"", ""choices"": [
            { ""label"": ""Long"", ""show"": [""Long""] },
            { ""label"": ""Short"", ""show"": [""Short""], ""picture"": ""Pictures/short.png"" }
        ] },
        { ""label"": ""Colour"", ""type"": ""choice"", ""choices"": [
            { ""label"": ""Red"", ""materials"": [{ ""renderer"": ""Body"", ""slot"": 0, ""material"": ""Materials/Red.mat"" }] },
            { ""label"": ""Blue"", ""materials"": [{ ""renderer"": ""Body"", ""slot"": 0, ""material"": ""Materials/Blue.mat"" }] }
        ] },
        { ""label"": ""Fit"", ""type"": ""choice"", ""choices"": [
            { ""label"": ""Regular"", ""blendshapes"": [{ ""renderer"": ""Skin"", ""name"": ""Slim"", ""weight"": 0 }] },
            { ""label"": ""Slim"", ""blendshapes"": [{ ""renderer"": ""Skin"", ""name"": ""Slim"", ""weight"": 100 }] }
        ] }
    ]
}";

    [SetUp] public void SetUp()
    {
        folder = "Assets/__Item settings test " + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        AssetDatabase.CreateFolder(folder, "Materials");
        AssetDatabase.CreateFolder(folder, "Pictures");
        var shader = Shader.Find("Standard");
        red = new Material(shader) { name = "Red" };
        blue = new Material(shader) { name = "Blue" };
        AssetDatabase.CreateAsset(red, folder + "/Materials/Red.mat");
        AssetDatabase.CreateAsset(blue, folder + "/Materials/Blue.mat");
        var picture = new Texture2D(4, 4);
        File.WriteAllBytes(folder + "/Pictures/short.png", picture.EncodeToPNG());
        Object.DestroyImmediate(picture);
        AssetDatabase.ImportAsset(folder + "/Pictures/short.png");

        var mesh = new Mesh { name = "Skin", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
        mesh.AddBlendShapeFrame("Slim", 100f, new[] { Vector3.zero, Vector3.left * .1f, Vector3.zero }, null, null);
        AssetDatabase.CreateAsset(mesh, folder + "/Skin.asset");

        var root = new GameObject("Jacket");
        new GameObject("Hood").transform.SetParent(root.transform);
        new GameObject("Long").transform.SetParent(root.transform);
        new GameObject("Short").transform.SetParent(root.transform);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform);
        body.AddComponent<MeshFilter>();
        body.AddComponent<MeshRenderer>().sharedMaterial = red;
        var skin = new GameObject("Skin");
        skin.transform.SetParent(root.transform);
        skin.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Jacket.prefab");
        Object.DestroyImmediate(root);
        File.WriteAllText(ItemSettings.PathFor(folder + "/Jacket.prefab"), Settings);
        AssetDatabase.ImportAsset(ItemSettings.PathFor(folder + "/Jacket.prefab"));

        // Placed on an avatar as My Avatar places it: a copy that remembers its prefab.
        scene = EditorSceneManager.NewPreviewScene();
        placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var attachment = placed.AddComponent<OrbitersAttachment>();
        attachment.source = AssetDatabase.AssetPathToGUID(folder + "/Jacket.prefab");
        attachment.variant = folder + "/Jacket.prefab";
    }

    [TearDown] public void TearDown()
    {
        if (placed != null) Object.DestroyImmediate(placed);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
    }

    private AttachmentOption Setting(string label) => AttachmentHooks.Options(placed).Single(o => o.Label == label);

    [Test] public void TheCardOffersTheSettingsAndReadsThemBackFromTheItem()
    {
        var labels = AttachmentHooks.Options(placed).Select(o => o.Label).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Hood", "Style", "Colour", "Fit" }, labels);
        var hood = Setting("Hood");
        Assert.IsTrue(hood.IsToggle);
        CollectionAssert.AreEqual(new[] { "Off", "On" }, hood.Choices);
        Assert.AreEqual(1, hood.Current, "The hood is shown: on");
        hood.Choose(0);
        Assert.IsFalse(placed.transform.Find("Hood").gameObject.activeSelf);
        Assert.AreEqual(0, Setting("Hood").Current);

        var colour = Setting("Colour");
        Assert.IsFalse(colour.IsToggle);
        Assert.AreEqual(0, colour.Current, "Red is on the body");
        colour.Choose(1);
        Assert.AreEqual(blue, placed.transform.Find("Body").GetComponent<Renderer>().sharedMaterial);
        Assert.AreEqual(1, Setting("Colour").Current);

        var skin = placed.transform.Find("Skin").GetComponent<SkinnedMeshRenderer>();
        Setting("Fit").Choose(1);
        Assert.AreEqual(100f, skin.GetBlendShapeWeight(0));
        Assert.AreEqual(1, Setting("Fit").Current);
    }

    [Test] public void AChoicesOptionsTakeTurnsShowingTheirObjects()
    {
        var style = Setting("Style");
        Assert.AreEqual(-1, style.Current, "Both shown: neither option holds");
        style.Choose(1);
        Assert.IsTrue(placed.transform.Find("Short").gameObject.activeSelf);
        Assert.IsFalse(placed.transform.Find("Long").gameObject.activeSelf, "What another option shows is hidden");
        Assert.AreEqual(1, Setting("Style").Current);
        Assert.IsNotNull(Setting("Style").ChoicePictures[1], "The option's picture, beside the file");
        Assert.IsNull(Setting("Style").ChoicePictures[0]);
    }

    [Test] public void TheChecksSayWhatIsWrongAndAPackageGetsTheAssetsTheSettingsUse()
    {
        string path = ItemSettings.PathFor(folder + "/Jacket.prefab");
        var problems = new List<string>();
        var definition = ItemSettings.Read(path, prefab, problems);
        CollectionAssert.IsEmpty(problems);
        CollectionAssert.AreEquivalent(new[] { folder + "/Pictures/short.png", folder + "/Materials/Red.mat", folder + "/Materials/Blue.mat" },
            ItemSettings.AssetsOf(path, definition));

        File.WriteAllText(path, @"{ ""settings"": [
            { ""label"": ""Hood"", ""type"": ""toggle"", ""on"": { ""show"": [""Hod""] } },
            { ""label"": ""Hood"", ""type"": ""choice"", ""choices"": [ { ""label"": ""A"", ""materials"": [{ ""renderer"": ""Body"", ""slot"": 3, ""material"": ""Materials/Green.mat"" }] } ] },
            { ""label"": ""Fit"", ""type"": ""choice"", ""choices"": [ { ""label"": ""A"" }, { ""label"": ""B"", ""blendshapes"": [{ ""renderer"": ""Skin"", ""name"": ""Wide"", ""weight"": 50 }] } ] }
        ] }");
        problems.Clear();
        ItemSettings.Read(path, prefab, problems);
        string all = string.Join("\n", problems);
        StringAssert.Contains("no object \"Hod\"", all);
        StringAssert.Contains("two settings have this label", all);
        StringAssert.Contains("needs at least two options", all);
        StringAssert.Contains("no material slot 3", all);
        StringAssert.Contains("no material at Materials/Green.mat", all);
        StringAssert.Contains("no blendshape \"Wide\"", all);

        File.WriteAllText(path, "{ not json");
        problems.Clear();
        Assert.IsNull(ItemSettings.Read(path, prefab, problems));
        StringAssert.Contains("not valid JSON", problems.Single());
    }

    [Test] public void AssetPathsAreBesideTheFileUnderAssetsOrByGuid()
    {
        string path = folder + "/Jacket.settings.json";
        Assert.AreEqual(folder + "/Materials/Red.mat", ItemSettings.AssetPath(path, "Materials/Red.mat"));
        Assert.AreEqual("Assets/Other/Red.mat", ItemSettings.AssetPath(path, "Assets/Other/Red.mat"));
        Assert.AreEqual(folder + "/Materials/Red.mat", ItemSettings.AssetPath(path, "guid:" + AssetDatabase.AssetPathToGUID(folder + "/Materials/Red.mat")));
        Assert.AreEqual("Assets/Shared/Red.mat", ItemSettings.AssetPath(path, "../Shared/Red.mat"));
    }
}
