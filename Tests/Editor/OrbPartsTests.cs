using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// N4N0's customizable parts: its parts file and base variants written with the prefab, checked against it, and its menu
// theme drawn onto the avatar's screen by the orbiters-n4n0-menu renderer (Orbiters - N4N0's pictures, or the shipped
// ones), read back from the screen and undone.
public sealed class OrbPartsTests
{
    private TestUndoSandbox sandbox;
    private string folder;
    private Scene scene;

    [SetUp] public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        folder = "Assets/__OrbitersOrbPartsTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        scene = EditorSceneManager.NewPreviewScene();
    }

    [TearDown] public void TearDown()
    {
        foreach (var root in scene.GetRootGameObjects()) Object.DestroyImmediate(root);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        AssetDatabase.DeleteAsset(ItemParts.VariantsFolder + "/N4N0 parts test");
        if (AssetDatabase.IsValidFolder(ItemParts.VariantsFolder) && !Directory.EnumerateFileSystemEntries(ItemParts.VariantsFolder).Any()) AssetDatabase.DeleteAsset(ItemParts.VariantsFolder);
        sandbox.End();
    }

    private GameObject Place(GameObject prefab)
    {
        var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        placed.name = "N4N0 parts test";
        var attachment = placed.AddComponent<OrbitersAttachment>();
        attachment.source = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
        attachment.variant = AssetDatabase.GetAssetPath(prefab);
        return placed;
    }

    [Test] public void N4N0CarriesItsPartsAndTheirBaseVariants()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder));
        string partsPath = ItemParts.PathFor(AssetDatabase.GetAssetPath(prefab));
        Assert.AreEqual(folder + "/N4N0.parts.json", partsPath);
        var problems = new List<string>();
        var definition = ItemParts.Read(partsPath, prefab, problems);
        Assert.IsNotNull(definition);
        CollectionAssert.IsEmpty(problems, string.Join("\n", problems));
        CollectionAssert.IsSubsetOf(new[] { "theme", "model" }, definition.parts.Select(p => p.key));
        var theme = definition.Find("theme");
        Assert.AreEqual(OrbInstaller.MenuRenderer, theme.renderer);
        CollectionAssert.AreEqual(new[] { "Modern", "Retro" }, ItemParts.Bases(partsPath, theme).Select(b => b.name));
        Assert.AreEqual("retro", (string)ItemParts.Bases(partsPath, theme)[1].values["style"], "a theme file is read as Orbiters - N4N0 reads it");
        var model = definition.Find("model");
        CollectionAssert.AreEqual(OrbInstaller.ModelShapes, model.blendshapes);
        foreach (string path in model.slots.Single().paths) Assert.IsNotNull(prefab.transform.Find(path), path);
        var facts = ItemParts.Measure(model, prefab.transform.Find(model.slots[0].paths[0]).GetComponent<SkinnedMeshRenderer>().sharedMesh);
        Assert.LessOrEqual(facts.Triangles, model.maxTriangles, "the base model is within its own part's limits");
        Assert.LessOrEqual(facts.Radius, model.maxRadius);
        var bytes = File.ReadAllBytes(folder + "/" + OrbInstaller.PartsFolderName + "/model-n4n0.png");
        Assert.IsTrue(ItemParts.PictureSize(bytes, out int w, out int h) && w == 512 && h == 512, "the model's picture, 512 × 512");
        // What a package of N4N0 carries: the base files and their pictures.
        var assets = ItemParts.AssetsOf(partsPath, definition);
        CollectionAssert.IsSubsetOf(new[] { "theme-modern.json", "theme-retro.json", "model-n4n0.json", "model-n4n0.png" }.Select(f => folder + "/" + OrbInstaller.PartsFolderName + "/" + f), assets);
        var translation = definition.Find("translation");
        if (translation != null) Assert.Greater(ItemParts.Bases(partsPath, translation).Count, 1);
    }

    [Test] public void AThemesValuesAreTheThemeOrbitersN4N0Reads()
    {
        var definition = ItemParts.Parse(OrbInstaller.PartsFile());
        var theme = definition.Find("theme");
        var variant = PartVariant.Parse("{ \"schema\": 1, \"part\": \"theme\", \"name\": \"Gold\", \"values\": { \"colors.accent\": \"#FFC247\", \"screen.glow\": 1.5 } }", null);
        var json = OrbInstaller.ThemeJson(theme, variant);
        Assert.AreEqual("modern", (string)json["style"], "a field's default fills what the variant leaves out");
        Assert.AreEqual("#FFC247", (string)json["colors"]["accent"]);
        Assert.AreEqual("#101012", (string)json["colors"]["background"]);
        Assert.AreEqual(1.5, (double)json["screen"]["glow"], 1e-6);
    }

    [UnityTest] public IEnumerator TheMenuThemeGivesTheScreenItsOwnMaterials()
    {
        if (VrcFury.Writer == null) Assert.Ignore("Requires the VRCFury integration.");
        var placed = Place(AssetDatabase.LoadAssetAtPath<GameObject>(OrbInstaller.CreatePrefab(folder)));
        var theme = ItemParts.Of(placed).Single(p => p.Part.key == "theme");
        Assert.AreEqual("Modern", theme.Current.name);
        Assert.IsFalse(theme.Current.Recorded);
        var retro = ItemParts.Bases(theme.PartsPath, theme.Part).Single(b => b.name == "Retro");
        var applying = ItemParts.ApplyAsync(theme, retro);
        // Orbiters - N4N0 may draw the pages in the background.
        for (var until = DateTime.UtcNow.AddSeconds(150); !applying.IsCompleted && DateTime.UtcNow < until;) yield return null;
        Assert.IsTrue(applying.IsCompleted, "drawn within two and a half minutes");
        Assert.IsTrue(applying.Result.Applied, string.Join("\n", applying.Result.Problems));
        var screen = placed.transform.Find(OrbInstaller.ScreenObject).GetComponent<SkinnedMeshRenderer>();
        StringAssert.StartsWith(ItemParts.VariantsFolder + "/N4N0 parts test/N4N0 parts test/menu ", AssetDatabase.GetAssetPath(screen.sharedMaterial), "the screen's own materials");
        Assert.IsTrue(OrbInstaller.RetroOf(placed.transform));
        Assert.IsTrue(OrbInstaller.ScreenCurrent(screen.sharedMaterial, 72), "the screen's shader and pages of this version");
        Assert.AreEqual(new Color32(2, 10, 4, 255), (Color32)screen.sharedMaterial.GetColor("_Card"), "the theme's background");
        ItemParts.TryColor((string)retro.values["screen.edge"], out var edge);
        Assert.AreEqual(edge, (Color32)screen.sharedMaterials[1].GetColor("_EdgeColor"), "the beam in the theme's edge colour");
        Assert.AreEqual(1f, screen.sharedMaterial.GetFloat("_Glow"));
        var current = ItemParts.Of(placed).Single(p => p.Part.key == "theme").Current;
        Assert.AreEqual("Retro", current.name);
        Assert.IsTrue(current.Recorded);
        var thumbnail = ItemParts.ThumbnailAsync(placed.transform, theme.Definition, theme.PrefabPath, theme.PartsPath, theme.Part, retro);
        for (var until = DateTime.UtcNow.AddSeconds(150); !thumbnail.IsCompleted && DateTime.UtcNow < until;) yield return null;
        Assert.IsTrue(ItemParts.PictureSize(thumbnail.Result, out int w, out int h) && w == 512 && h == 512, "the menu in that theme, 512 × 512");

        sandbox.PerformUndo();
        Assert.AreEqual("Modern", ItemParts.Of(placed).Single(p => p.Part.key == "theme").Current.name, "Undo brings back the screen as it was");
        Assert.IsFalse(OrbInstaller.RetroOf(placed.transform));
    }
}
