using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.Tests;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// Customizable parts (Gadget.parts.json beside Gadget.prefab): the declaration checked against the prefab, variants
// checked as Orbiters checks posts (zip shape, files, pictures, texts, models), applied in place with Undo, read back from
// the item's files, carried over to an update, and parts drawn by a registered renderer.
public sealed class ItemPartsTests
{
    private const string Renderer = "test-parts-renderer";
    private TestUndoSandbox sandbox;
    private string folder, itemName;
    private Scene scene;
    private GameObject prefab, placed;
    private Material shell;
    private readonly List<string> rendered = new List<string>();

    private const string Parts = @"{
    ""schema"": 1,
    ""parts"": [
        {
            ""key"": ""colour"", ""label"": ""Colour"", ""kind"": ""values"",
            ""fields"": [
                { ""key"": ""main"", ""label"": ""Main"", ""type"": ""color"", ""default"": ""#FFFFFF"" },
                { ""key"": ""shine"", ""label"": ""Shine"", ""type"": ""number"", ""min"": 0, ""max"": 1, ""default"": 0.5 },
                { ""key"": ""decal"", ""label"": ""Decal"", ""type"": ""picture"", ""width"": 8, ""height"": 4, ""optional"": true }
            ],
            ""targets"": [
                { ""field"": ""main"", ""path"": ""Body"", ""slot"": 0, ""property"": ""_Color"" },
                { ""field"": ""shine"", ""path"": ""Body"", ""slot"": 0, ""property"": ""_Glossiness"" },
                { ""field"": ""decal"", ""path"": ""Body"", ""slot"": 0, ""property"": ""_MainTex"" }
            ],
            ""base"": [ { ""name"": ""White"", ""file"": ""Gadget parts/colour-white.json"" } ]
        },
        {
            ""key"": ""model"", ""label"": ""Model"", ""kind"": ""model"",
            ""slots"": [ { ""key"": ""body"", ""label"": ""Body"", ""paths"": [""Body""] } ],
            ""blendshapes"": [""Smile"", ""Blink""], ""maxTriangles"": 600, ""maxTextureSize"": 64, ""maxRadius"": 1.2,
            ""shaders"": [""VRChat/Mobile/Standard Lite"", ""VRChat/Mobile/Toon Lit""],
            ""base"": [ { ""name"": ""Own"", ""file"": ""Gadget parts/model-own.json"" } ]
        },
        {
            ""key"": ""words"", ""label"": ""Language"", ""kind"": ""strings"", ""strings"": ""Gadget parts/words-en.json"", ""renderer"": """ + Renderer + @""",
            ""base"": [ { ""name"": ""English"", ""language"": ""en"", ""file"": ""Gadget parts/words-en.json"" } ]
        }
    ]
}";

    [SetUp] public void SetUp()
    {
        sandbox = TestUndoSandbox.Begin();
        string id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
        itemName = "PartsTest " + id;
        folder = "Assets/__Item parts test " + id;
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        AssetDatabase.CreateFolder(folder, "Gadget parts");
        File.WriteAllText(folder + "/Gadget parts/colour-white.json", "{ \"schema\": 1, \"part\": \"colour\", \"name\": \"White\", \"values\": { \"main\": \"#FFFFFF\", \"shine\": 0.5 } }");
        File.WriteAllText(folder + "/Gadget parts/model-own.json", "{ \"schema\": 1, \"part\": \"model\", \"name\": \"Own\", \"model\": {} }");
        File.WriteAllText(folder + "/Gadget parts/words-en.json", "{ \"schema\": 1, \"language\": \"en\", \"strings\": { \"title\": { \"text\": \"Menu\", \"max\": 10 }, \"hello\": \"Hello\" } }");
        AssetDatabase.Refresh();

        shell = new Material(Shader.Find("VRChat/Mobile/Standard Lite")) { name = "Shell" };
        AssetDatabase.CreateAsset(shell, folder + "/Shell.mat");
        var mesh = Sphere("Gadget", 1f, true);
        AssetDatabase.CreateAsset(mesh, folder + "/Gadget mesh.asset");
        var root = new GameObject("Gadget");
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform);
        var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.sharedMaterials = new[] { shell, shell };
        prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Gadget.prefab");
        Object.DestroyImmediate(root);
        File.WriteAllText(ItemParts.PathFor(folder + "/Gadget.prefab"), Parts);
        AssetDatabase.ImportAsset(ItemParts.PathFor(folder + "/Gadget.prefab"));

        ItemParts.RegisterRenderer(Renderer, new ItemParts.PartRenderer
        {
            Anchors = item => new[] { item.Find("Body").GetComponent<Renderer>() },
            Render = job =>
            {
                var words = job.Parts["words"].variant;
                rendered.Add(words.language + ":" + words.strings["title"]);
                if (job.ThumbnailFor != null) { job.Thumbnail = new byte[] { 1 }; return Task.CompletedTask; }
                // The renderer's own material on the body's second slot.
                string made = job.Folder + "/words " + words.language;
                ItemParts.EnsureFolder(made);
                var material = new Material(shell) { name = "Words" };
                AssetDatabase.CreateAsset(material, made + "/Words.mat");
                job.Made.Add(made + "/Words.mat");
                var target = job.Item.Find("Body").GetComponent<Renderer>();
                job.Assign = () =>
                {
                    Undo.RecordObject(target, "Words");
                    var materials = target.sharedMaterials;
                    materials[1] = AssetDatabase.LoadAssetAtPath<Material>(made + "/Words.mat");
                    target.sharedMaterials = materials;
                };
                return Task.CompletedTask;
            },
        });

        // Placed on an avatar as My Avatar places it: a copy that remembers its prefab.
        scene = EditorSceneManager.NewPreviewScene();
        placed = Place(itemName);
    }

    private GameObject Place(string name)
    {
        var copy = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        copy.name = name;
        var attachment = copy.AddComponent<OrbitersAttachment>();
        attachment.source = AssetDatabase.AssetPathToGUID(folder + "/Gadget.prefab");
        attachment.variant = folder + "/Gadget.prefab";
        return copy;
    }

    [TearDown] public void TearDown()
    {
        foreach (var root in scene.GetRootGameObjects()) Object.DestroyImmediate(root);
        EditorSceneManager.ClosePreviewScene(scene);
        AssetDatabase.DeleteAsset(folder);
        AssetDatabase.DeleteAsset(ItemParts.VariantsFolder + "/" + itemName);
        if (AssetDatabase.IsValidFolder(ItemParts.VariantsFolder) && !Directory.EnumerateFileSystemEntries(ItemParts.VariantsFolder).Any()) AssetDatabase.DeleteAsset(ItemParts.VariantsFolder);
        sandbox.End();
    }

    // A sphere of a radius with two submeshes (the shell, a band), and blendshapes Smile and Blink.
    private static Mesh Sphere(string name, float radius, bool shapes, int segments = 12, int rings = 8)
    {
        var vertices = new List<Vector3>();
        for (int r = 0; r <= rings; r++)
        for (int s = 0; s <= segments; s++)
        {
            float polar = Mathf.PI * r / rings, around = 2 * Mathf.PI * s / segments;
            vertices.Add(radius * new Vector3(Mathf.Sin(polar) * Mathf.Cos(around), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(around)));
        }
        var shellTriangles = new List<int>(); var band = new List<int>();
        for (int r = 0; r < rings; r++)
        for (int s = 0; s < segments; s++)
        {
            int a = r * (segments + 1) + s, b = a + segments + 1;
            (r == rings / 2 ? band : shellTriangles).AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetNormals(vertices.Select(v => v.normalized).ToList());
        mesh.SetUVs(0, vertices.Select(v => new Vector2(v.x, v.y)).ToList());
        mesh.subMeshCount = 2;
        mesh.SetTriangles(shellTriangles, 0);
        mesh.SetTriangles(band, 1);
        if (shapes)
        {
            var deltas = vertices.Select(v => v * .05f).ToArray();
            mesh.AddBlendShapeFrame("Smile", 100, deltas, null, null);
            mesh.AddBlendShapeFrame("Blink", 100, deltas.Select(d => -d).ToArray(), null, null);
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    private static byte[] Png(int width, int height, Color colour)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.SetPixels(Enumerable.Repeat(colour, width * height).ToArray());
        texture.Apply();
        try { return texture.EncodeToPNG(); }
        finally { Object.DestroyImmediate(texture); }
    }

    private ItemParts.Placed Part(string key) => ItemParts.Of(placed).Single(p => p.Part.key == key);

    private static PartVariant Variant(string json, params (string name, byte[] bytes)[] files)
    {
        var variant = PartVariant.Parse(json, null);
        foreach (var (name, bytes) in files) variant.Files[name] = bytes;
        variant.Thumbnail = Png(512, 512, Color.gray);
        return variant;
    }

    private static List<string> Problems(ItemParts.Placed placed, PartVariant variant)
    {
        var problems = new List<string>();
        ItemParts.Check(placed.Part, variant, AssetDatabase.LoadAssetAtPath<GameObject>(placed.PrefabPath), problems, partsPath: placed.PartsPath);
        return problems;
    }

    // An FBX of a sphere made by Toolkit's own writer.
    private byte[] Fbx(float radius, bool shapes, int segments = 12)
    {
        var holder = new GameObject("Body");
        var mesh = Sphere("Body", radius, shapes, segments);
        // A tenth ahead (+Z): the file must come back facing the way it was written.
        mesh.vertices = mesh.vertices.Select(v => v + new Vector3(0, 0, .1f)).ToArray();
        mesh.RecalculateBounds();
        // Two materials: the file keeps a slot per material, as Blender writes it.
        var band = new Material(shell) { name = "Band" };
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "parts-test-" + System.Guid.NewGuid().ToString("N") + ".fbx");
            var result = FbxExport.Write(path, new[] { new FbxExportMesh { Transform = holder.transform, Mesh = mesh, Name = "Body", Materials = new[] { shell, band } } });
            Assert.IsTrue(result.Success, result.Error);
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            return bytes;
        }
        finally { Object.DestroyImmediate(holder); Object.DestroyImmediate(mesh); Object.DestroyImmediate(band); }
    }

    [Test] public void TheDeclarationIsCheckedAgainstThePrefab()
    {
        var problems = new List<string>();
        var definition = ItemParts.Read(ItemParts.PathFor(folder + "/Gadget.prefab"), prefab, problems);
        Assert.IsNotNull(definition);
        CollectionAssert.IsEmpty(problems, string.Join("\n", problems));
        CollectionAssert.AreEqual(new[] { "colour", "model", "words" }, definition.parts.Select(p => p.key));

        var bad = JObject.Parse(Parts);
        bad["parts"][0]["key"] = "Bad Key";
        bad["parts"][0]["targets"][0]["path"] = "Missing";
        bad["parts"][1]["kind"] = "script";
        bad["parts"][2]["base"][0]["file"] = "Gadget parts/none.json";
        File.WriteAllText(folder + "/Bad.parts.json", bad.ToString());
        problems.Clear();
        ItemParts.Read(folder + "/Bad.parts.json", prefab, problems);
        Assert.IsTrue(problems.Any(p => p.Contains("1 to 32 small letters")), string.Join("\n", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("no renderer \"Missing\"")), string.Join("\n", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("kind is \"script\"")), string.Join("\n", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("no file at Gadget parts/none.json")), string.Join("\n", problems));
    }

    [Test] public void AVariantsZipIsCheckedBeforeAnythingIsRead()
    {
        var colour = Part("colour");
        var variant = Variant("{ \"schema\": 1, \"part\": \"colour\", \"name\": \"Red\", \"values\": { \"main\": \"#FF0000\", \"shine\": 0.2, \"decal\": \"decal.png\" } }", ("decal.png", Png(8, 4, Color.red)));
        string zip = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "parts-test.zip");
        variant.WriteZip(zip, colour.Part);
        var problems = new List<string>();
        var read = PartVariant.ReadZip(File.ReadAllBytes(zip), problems);
        Assert.IsNotNull(read, string.Join("\n", problems));
        CollectionAssert.IsEmpty(Problems(colour, read));
        Assert.AreEqual(64, read.Sha256.Length);

        // A folder in the zip, then a file nothing names.
        byte[] Zip(params (string name, byte[] bytes)[] entries)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var (name, bytes) in entries) using (var stream = archive.CreateEntry(name).Open()) stream.Write(bytes, 0, bytes.Length);
                return memory.ToArray();
            }
        }
        var json = Encoding.UTF8.GetBytes(variant.ToJson());
        problems.Clear();
        Assert.IsNull(PartVariant.ReadZip(Zip(("variant.json", json), ("pictures/decal.png", Png(8, 4, Color.red))), problems));
        Assert.IsTrue(problems.Single().Contains("no folders"));
        problems.Clear();
        var extra = PartVariant.ReadZip(Zip(("variant.json", json), ("thumbnail.png", Png(512, 512, Color.gray)), ("decal.png", Png(8, 4, Color.red)), ("run.cs", new byte[] { 1 })), problems);
        Assert.IsTrue(Problems(colour, extra).Any(p => p.Contains("run.cs is in the variant")));
        // A picture of the wrong size, a thumbnail of the wrong size, a value out of its range.
        var wrong = Variant("{ \"schema\": 1, \"part\": \"colour\", \"name\": \"Red\", \"values\": { \"main\": \"#FF0000\", \"shine\": 3, \"decal\": \"decal.png\" } }", ("decal.png", Png(16, 4, Color.red)));
        wrong.Thumbnail = Png(256, 256, Color.gray);
        var found = Problems(colour, wrong);
        Assert.IsTrue(found.Any(p => p.Contains("16 × 4")), string.Join("\n", found));
        Assert.IsTrue(found.Any(p => p.Contains("thumbnail.png must be a 512 × 512 PNG")), string.Join("\n", found));
        Assert.IsTrue(found.Any(p => p.Contains("Shine is above 1")), string.Join("\n", found));
        File.Delete(zip);
    }

    [Test] public void StringsGiveEveryKeyATextWithinItsLength()
    {
        var words = Part("words");
        CollectionAssert.IsEmpty(Problems(words, Variant("{ \"schema\": 1, \"part\": \"words\", \"name\": \"Français\", \"language\": \"fr\", \"strings\": { \"title\": \"Menu FR\", \"hello\": \"Salut\" } }")));
        var found = Problems(words, Variant("{ \"schema\": 1, \"part\": \"words\", \"name\": \"Bad\", \"language\": \"french!\", \"strings\": { \"title\": \"A title far too long\", \"extra\": \"x\" } }"));
        Assert.IsTrue(found.Any(p => p.Contains("give its language")), string.Join("\n", found));
        Assert.IsTrue(found.Any(p => p.Contains("\"title\" is longer than 10")), string.Join("\n", found));
        Assert.IsTrue(found.Any(p => p.Contains("\"hello\" has no text")), string.Join("\n", found));
        Assert.IsTrue(found.Any(p => p.Contains("\"extra\" is not a text")), string.Join("\n", found));
    }

    [Test] public void ValuesGoOnCopiesOfTheItemsMaterialsAndAreReadBackFromThem()
    {
        var colour = Part("colour");
        Assert.AreEqual("White", colour.Current.name);
        Assert.IsFalse(colour.Current.Recorded, "as its prefab made it: its first base variant");
        var red = Variant("{ \"schema\": 1, \"part\": \"colour\", \"name\": \"Red\", \"values\": { \"main\": \"#FF0000\", \"shine\": 0.9, \"decal\": \"decal.png\" } }", ("decal.png", Png(8, 4, Color.red)));
        var result = ItemParts.ApplyAsync(colour, red).Result;
        Assert.IsTrue(result.Applied, string.Join("\n", result.Problems));
        var body = placed.transform.Find("Body").GetComponent<Renderer>();
        var material = body.sharedMaterials[0];
        Assert.AreNotEqual(shell, material, "a copy, never the prefab's own material");
        StringAssert.StartsWith(ItemParts.VariantsFolder + "/" + itemName + "/" + itemName + "/colour/", AssetDatabase.GetAssetPath(material));
        Assert.AreEqual(Color.red, material.GetColor("_Color"));
        Assert.AreEqual(.9f, material.GetFloat("_Glossiness"), 1e-4f);
        Assert.IsNotNull(material.GetTexture("_MainTex"));
        Assert.AreEqual(shell, body.sharedMaterials[1], "only the target's slot");
        var current = Part("colour").Current;
        Assert.AreEqual("Red", current.name);
        Assert.IsTrue(current.Recorded);

        sandbox.PerformUndo();
        Assert.AreEqual(shell, body.sharedMaterials[0]);
        Assert.AreEqual("White", Part("colour").Current.name, "Undo brings back the earlier variant");
    }

    [Test] public void AModelVariantIsImportedCheckedAndPutInTheSlots()
    {
        var model = Part("model");
        const string Material = "\"materials\": [ { \"shader\": \"VRChat/Mobile/Toon Lit\", \"textures\": { \"_MainTex\": \"skin.png\" } }, { \"shader\": \"VRChat/Mobile/Standard Lite\", \"colors\": { \"_EmissionColor\": \"#FFFFFF\" } } ]";
        var variant = Variant("{ \"schema\": 1, \"part\": \"model\", \"name\": \"Blue ball\", \"model\": { \"mesh\": \"ball.fbx\", \"slots\": { \"body\": { \"object\": \"Body\", " + Material + " } } } }",
            ("ball.fbx", Fbx(1f, true)), ("skin.png", Png(32, 32, Color.blue)));
        CollectionAssert.IsEmpty(Problems(model, variant));
        var result = ItemParts.ApplyAsync(model, variant).Result;
        Assert.IsTrue(result.Applied, string.Join("\n", result.Problems));
        var body = placed.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
        StringAssert.EndsWith("ball.fbx", AssetDatabase.GetAssetPath(body.sharedMesh));
        Assert.GreaterOrEqual(body.sharedMesh.GetBlendShapeIndex("Smile"), 0);
        Assert.AreEqual(.1f, body.sharedMesh.bounds.center.z, .02f, "it faces the way the file was written");
        Assert.AreEqual("VRChat/Mobile/Toon Lit", body.sharedMaterials[0].shader.name);
        Assert.IsTrue(body.sharedMaterials[1].IsKeywordEnabled("_EMISSION"), "an emission colour turns emission on");
        Assert.AreEqual("Blue ball", Part("model").Current.name);
        sandbox.PerformUndo();
        Assert.AreEqual(prefab.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh, body.sharedMesh);

        // A model without the item's blendshapes, too many triangles, too large, a shader the part doesn't allow.
        var bad = Variant("{ \"schema\": 1, \"part\": \"model\", \"name\": \"Bad\", \"model\": { \"mesh\": \"ball.fbx\", \"slots\": { \"body\": { \"object\": \"Body\", " + Material.Replace("Toon Lit", "Toon Standard") + " } } } }",
            ("ball.fbx", Fbx(2f, false, 60)), ("skin.png", Png(32, 32, Color.blue)));
        Assert.IsTrue(Problems(model, bad).Any(p => p.Contains("Toon Standard\" is not one of")));
        var badMesh = Variant("{ \"schema\": 1, \"part\": \"model\", \"name\": \"Bad\", \"model\": { \"mesh\": \"ball.fbx\", \"slots\": { \"body\": { \"object\": \"Body\", " + Material + " } } } }",
            ("ball.fbx", Fbx(2f, false, 60)), ("skin.png", Png(32, 32, Color.blue)));
        var refused = ItemParts.ApplyAsync(model, badMesh).Result;
        Assert.IsFalse(refused.Applied);
        Assert.IsTrue(refused.Problems.Any(p => p.Contains("no blendshape Smile, Blink")), string.Join("\n", refused.Problems));
        Assert.IsTrue(refused.Problems.Any(p => p.Contains("triangles (600 at most)")), string.Join("\n", refused.Problems));
        Assert.IsTrue(refused.Problems.Any(p => p.Contains("from its middle (1.2 at most)")), string.Join("\n", refused.Problems));
        Assert.AreEqual(prefab.transform.Find("Body").GetComponent<SkinnedMeshRenderer>().sharedMesh, body.sharedMesh, "nothing changed");
    }

    [Test] public void ARendererDrawsThePartsNamingItAndCarriesTheirRecord()
    {
        var words = Part("words");
        Assert.AreEqual("English", words.Current.name);
        var french = Variant("{ \"schema\": 1, \"part\": \"words\", \"name\": \"Français\", \"language\": \"fr\", \"strings\": { \"title\": \"Menu FR\", \"hello\": \"Salut\" } }");
        var result = ItemParts.ApplyAsync(words, french).Result;
        Assert.IsTrue(result.Applied, string.Join("\n", result.Problems));
        CollectionAssert.AreEqual(new[] { "fr:Menu FR" }, rendered);
        Assert.AreEqual("Français", Part("words").Current.name);
        Assert.AreEqual("fr", Part("words").Current.language);
        var thumbnail = ItemParts.ThumbnailAsync(placed.transform, words.Definition, words.PrefabPath, words.PartsPath, words.Part, french).Result;
        CollectionAssert.AreEqual(new byte[] { 1 }, thumbnail, "the renderer's picture");
    }

    [Test] public void AnUpdateOfTheItemKeepsItsVariants()
    {
        var colour = Part("colour");
        var red = Variant("{ \"schema\": 1, \"part\": \"colour\", \"name\": \"Red\", \"values\": { \"main\": \"#FF0000\", \"shine\": 0.5 } }");
        Assert.IsTrue(ItemParts.ApplyAsync(colour, red).Result.Applied);
        var update = Place(itemName);
        AttachmentHooks.CarryOptions(placed, update);
        var carried = ItemParts.Of(update).Single(p => p.Part.key == "colour");
        Assert.AreEqual("Red", carried.Current.name);
        Assert.AreEqual(Color.red, update.transform.Find("Body").GetComponent<Renderer>().sharedMaterials[0].GetColor("_Color"));
    }

    // ---- Audit 2026-10-28: variant files are named by their content and never change under an earlier user ----

    private const string RedJson = "{ \"schema\": 1, \"part\": \"colour\", \"name\": \"Red\", \"values\": { \"main\": \"#FF0000\", \"shine\": 0.9, \"decal\": \"decal.png\" } }";

    // Two variants with the same JSON and files of the same size but other bytes (the audit's probe) have other folders.
    [Test] public void AVariantsFolderIsNamedByTheBytesOfItsFiles()
    {
        var part = Part("colour").Part;
        var one = Variant(RedJson, ("decal.png", new byte[] { 1, 2, 3, 4 }));
        var other = Variant(RedJson, ("decal.png", new byte[] { 1, 2, 3, 5 }));
        other.Thumbnail = one.Thumbnail;
        Assert.AreNotEqual(ItemParts.VariantIdentity(part, one), ItemParts.VariantIdentity(part, other));
        var again = Variant(RedJson, ("decal.png", new byte[] { 1, 2, 3, 4 }));
        again.Thumbnail = one.Thumbnail;
        Assert.AreEqual(ItemParts.VariantIdentity(part, one), ItemParts.VariantIdentity(part, again), "the same variant again: the same folder");
        var posted = Variant(RedJson, ("decal.png", new byte[] { 1, 2, 3, 4 }));
        posted.Thumbnail = one.Thumbnail;
        posted.Id = 7;
        Assert.AreNotEqual(ItemParts.VariantIdentity(part, again), ItemParts.VariantIdentity(part, posted), "its record (here Orbiters' id) counts too");
    }

    // VAR-01/VAR-02: a second variant with the same JSON and a same-sized picture never changes the first one's files, so
    // Undo brings back exactly what the item showed; applying the first again takes its files as they are.
    [Test] public void AnotherVariantNeverChangesTheFilesOfAnEarlierOne()
    {
        var colour = Part("colour");
        byte[] redPicture = Png(8, 4, Color.red), bluePicture = Png(8, 4, Color.blue);
        var red = Variant(RedJson, ("decal.png", redPicture));
        var blue = Variant(RedJson, ("decal.png", bluePicture));
        blue.Thumbnail = red.Thumbnail;
        Assert.IsTrue(ItemParts.ApplyAsync(colour, red).Result.Applied);
        var body = placed.transform.Find("Body").GetComponent<Renderer>();
        var first = body.sharedMaterials[0];
        string firstPath = AssetDatabase.GetAssetPath(first), firstPicture = AssetDatabase.GetAssetPath(first.GetTexture("_MainTex"));
        var firstMaterialText = File.ReadAllText(firstPath);

        Assert.IsTrue(ItemParts.ApplyAsync(Part("colour"), blue).Result.Applied);
        var second = body.sharedMaterials[0];
        Assert.AreNotEqual(firstPath, AssetDatabase.GetAssetPath(second));
        Assert.AreNotEqual(firstPicture, AssetDatabase.GetAssetPath(second.GetTexture("_MainTex")), "its picture has its own file");
        CollectionAssert.AreEqual(redPicture, File.ReadAllBytes(firstPicture), "the first picture is as it was");
        Assert.AreEqual(firstMaterialText, File.ReadAllText(firstPath), "the first material is as it was");

        sandbox.PerformUndo();
        Assert.AreEqual(firstPath, AssetDatabase.GetAssetPath(body.sharedMaterials[0]));
        Assert.AreEqual(firstPicture, AssetDatabase.GetAssetPath(body.sharedMaterials[0].GetTexture("_MainTex")));

        // The same variant again on the item as it was (as a card applies it again): its files are taken as they are.
        sandbox.PerformUndo();
        Assert.AreEqual(shell, body.sharedMaterials[0]);
        var written = File.GetLastWriteTimeUtc(firstPath);
        var redAgain = Variant(RedJson, ("decal.png", redPicture));
        redAgain.Thumbnail = red.Thumbnail;
        Assert.IsTrue(ItemParts.ApplyAsync(Part("colour"), redAgain).Result.Applied);
        Assert.AreEqual(firstPath, AssetDatabase.GetAssetPath(body.sharedMaterials[0]), "the first material again, not a copy of it");
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(firstPath));
    }

    // VAR-02: a material made from an item's current material is never written over when that one changed since: a
    // material other copies and Undo steps use keeps what it had, and the new values get their own file.
    [Test] public void AMaterialMadeEarlierIsNotWrittenOverWhenItsSourceChanged()
    {
        var red = Variant(RedJson, ("decal.png", Png(8, 4, Color.red)));
        Assert.IsTrue(ItemParts.ApplyAsync(Part("colour"), red).Result.Applied);
        var body = placed.transform.Find("Body").GetComponent<Renderer>();
        string firstPath = AssetDatabase.GetAssetPath(body.sharedMaterials[0]);
        sandbox.PerformUndo();
        // The item's own material changes (its creator's update, the user's edit), then the same variant is applied.
        shell.SetFloat("_Glossiness", .1f);
        shell.renderQueue = 2100;
        EditorUtility.SetDirty(shell);
        AssetDatabase.SaveAssetIfDirty(shell);
        var text = File.ReadAllText(firstPath);
        Assert.IsTrue(ItemParts.ApplyAsync(Part("colour"), red).Result.Applied);
        Assert.AreNotEqual(firstPath, AssetDatabase.GetAssetPath(body.sharedMaterials[0]));
        Assert.AreEqual(2100, body.sharedMaterials[0].renderQueue);
        Assert.AreEqual(text, File.ReadAllText(firstPath), "the earlier copy is as it was");
    }

    // SEC-05: a cached or downloaded variant file larger than a variant's zip may be is refused before it is read.
    [Test] public void AVariantFileTooLargeIsNotRead()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "parts-test-" + System.Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var stream = File.Create(path)) stream.SetLength(ItemParts.MaxZipBytes + 1);
            var problems = new List<string>();
            Assert.IsNull(PartVariant.ReadZipFile(path, problems));
            Assert.IsTrue(problems.Any(p => p.Contains("larger than 16 MB")), string.Join("\n", problems));
            Variant(RedJson, ("decal.png", Png(8, 4, Color.red))).WriteZip(path);
            problems.Clear();
            var read = PartVariant.ReadZipFile(path, problems);
            Assert.IsNotNull(read, string.Join("\n", problems));
            Assert.AreEqual("Red", read.name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // VAR-03: applies on one item run one after another: a second part's choice waits until a renderer drawing the
    // item's parts from the choices it read has finished, so neither overwrites the other.
    [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator AppliesOnOneItemRunOneAfterAnother()
    {
        var drawing = new TaskCompletionSource<bool>();
        ItemParts.RegisterRenderer(Renderer, new ItemParts.PartRenderer
        {
            Anchors = item => new[] { item.Find("Body").GetComponent<Renderer>() },
            Render = async job => { await drawing.Task; job.Assign = () => { }; },
        });
        var french = Variant("{ \"schema\": 1, \"part\": \"words\", \"name\": \"Français\", \"language\": \"fr\", \"strings\": { \"title\": \"Menu FR\", \"hello\": \"Salut\" } }");
        var red = Variant(RedJson, ("decal.png", Png(8, 4, Color.red)));
        var first = ItemParts.ApplyAsync(Part("words"), french);
        var second = ItemParts.ApplyAsync(Part("colour"), red);
        for (int i = 0; i < 5; i++) yield return null;
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted, "the colour waits for the words");
        Assert.AreEqual(shell, placed.transform.Find("Body").GetComponent<Renderer>().sharedMaterials[0]);
        drawing.SetResult(true);
        double deadline = EditorApplication.timeSinceStartup + 30;
        while (!(first.IsCompleted && second.IsCompleted) && EditorApplication.timeSinceStartup < deadline) yield return null;
        Assert.IsTrue(first.Result.Applied, string.Join("\n", first.Result.Problems));
        Assert.IsTrue(second.Result.Applied, string.Join("\n", second.Result.Problems));
        Assert.AreEqual(Color.red, placed.transform.Find("Body").GetComponent<Renderer>().sharedMaterials[0].GetColor("_Color"));
    }

    // An update takes over its card's options first, then its parts: an option may redraw the parts.
    [Test] public void AnUpdateTakesItsOptionsBeforeItsParts()
    {
        var order = new List<string>();
        string tag = itemName;
        int chosen = 0;
        AttachmentHooks.RegisterOptions(root => root != null && root.name == tag ? new[] { new AttachmentOption { Label = "Order test " + tag, Choices = new[] { "A", "B" }, Current = root == placed ? 1 : chosen, Choose = i => { chosen = i; order.Add("option"); } } } : null);
        AttachmentHooks.RegisterCarry((from, to) => { if (to != null && to.name == tag) order.Add("parts"); return Task.CompletedTask; });
        var update = Place(itemName);
        AttachmentHooks.CarryOptions(placed, update);
        Assert.AreEqual(1, chosen);
        CollectionAssert.AreEqual(new[] { "option", "parts" }, order.Where(o => o == "option" || o == "parts").Distinct().ToArray());
        Assert.AreEqual("option", order.First());
        Assert.AreEqual("parts", order.Last());
    }

    // VAR-04: an apply cancelled before it starts changes nothing and writes nothing.
    [Test] public void ACancelledApplyChangesNothing()
    {
        var red = Variant(RedJson, ("decal.png", Png(8, 4, Color.red)));
        var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        var result = ItemParts.ApplyAsync(Part("colour"), red, null, cancelled.Token).Result;
        Assert.IsFalse(result.Applied);
        CollectionAssert.Contains(result.Problems, "Cancelled.");
        Assert.AreEqual(shell, placed.transform.Find("Body").GetComponent<Renderer>().sharedMaterials[0]);
        Assert.IsFalse(AssetDatabase.IsValidFolder(ItemParts.ItemFolder(placed.transform) + "/colour"), "no file was made");
    }

    // VAR-05: a change that fails while the item changes takes back the changes made before it.
    [Test] public void AFailingChangeTakesBackTheChangesBeforeIt()
    {
        ItemParts.RegisterRenderer(Renderer, new ItemParts.PartRenderer
        {
            Anchors = item => new[] { item.Find("Body").GetComponent<Renderer>() },
            Render = job => { job.Assign = () => throw new System.InvalidOperationException("the renderer broke"); return Task.CompletedTask; },
        });
        var red = Variant(RedJson, ("decal.png", Png(8, 4, Color.red)));
        var french = Variant("{ \"schema\": 1, \"part\": \"words\", \"name\": \"Français\", \"language\": \"fr\", \"strings\": { \"title\": \"Menu FR\", \"hello\": \"Salut\" } }");
        var colour = Part("colour");
        UnityEngine.TestTools.LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("the renderer broke"));
        var result = ItemParts.ApplyAsync(placed.transform, colour.Definition, colour.PrefabPath, colour.PartsPath,
            new List<(ItemParts.Part, PartVariant)> { (colour.Part, red), (Part("words").Part, french) }).Result;
        Assert.IsFalse(result.Applied);
        Assert.IsTrue(result.Problems.Any(p => p.Contains("the renderer broke")), string.Join("\n", result.Problems));
        Assert.AreEqual(shell, placed.transform.Find("Body").GetComponent<Renderer>().sharedMaterials[0], "the colour put on before the failure went back");
        Assert.AreEqual("White", Part("colour").Current.name);
    }

    // VAR-06/VAR-07: a model object's transform is baked in with normals by the inverse transpose (non-uniform scale) and,
    // mirrored, with its triangles turned around and its tangents' handedness flipped, so faces still face out.
    [Test] public void BakedModelsKeepTheirNormalsUnderScaleAndMirroring()
    {
        var mesh = new Mesh { name = "Slope" };
        mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
        var slanted = new Vector3(1, 1, 1).normalized;
        mesh.normals = Enumerable.Repeat(slanted, 4).ToArray();
        mesh.tangents = Enumerable.Repeat(new Vector4(1, -1, 0, 1).normalized, 4).Select(t => new Vector4(t.x, t.y, t.z, 1)).ToArray();
        mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };
        var shapeNormal = new Vector3(0, 0, 1) - slanted;
        mesh.AddBlendShapeFrame("Flat", 100, new Vector3[4], Enumerable.Repeat(shapeNormal, 4).ToArray(), null);
        try
        {
            var scale = Matrix4x4.Scale(new Vector3(3, 1, 1));
            var baked = ItemParts.BakeTransform(mesh, scale);
            try
            {
                var expected = new Vector3(1 / 3f, 1, 1).normalized;
                Assert.Less(Vector3.Angle(expected, baked.normals[0]), .01f, "normal by the inverse transpose, not the scale");
                Assert.AreEqual(0f, Vector3.Dot(baked.normals[0], new Vector3(baked.tangents[0].x, baked.tangents[0].y, baked.tangents[0].z)), 1e-4f, "the tangent stays in the surface");
                var shapeNormals = new Vector3[4];
                baked.GetBlendShapeFrameVertices(0, 0, new Vector3[4], shapeNormals, new Vector3[4]);
                Assert.Less(Vector3.Angle(new Vector3(0, 0, 1), baked.normals[0] + shapeNormals[0]), .05f, "a shape's normal is the transformed one");
                CollectionAssert.AreEqual(mesh.triangles, baked.triangles);
            }
            finally { Object.DestroyImmediate(baked); }

            var mirrored = ItemParts.BakeTransform(mesh, Matrix4x4.Scale(new Vector3(-1, 1, 1)));
            try
            {
                var v = mirrored.vertices; var t = mirrored.triangles;
                var face = Vector3.Cross(v[t[1]] - v[t[0]], v[t[2]] - v[t[0]]);
                Assert.Greater(Vector3.Dot(Vector3.Cross(mesh.vertices[1] - mesh.vertices[0], mesh.vertices[2] - mesh.vertices[0]), mesh.normals[0]), 0f);
                Assert.Greater(Vector3.Dot(face, mirrored.normals[0]), 0f, "its winding still agrees with its normal: a front face where it was");
                Assert.AreEqual(-1f, mirrored.tangents[0].w, "the tangents' handedness flips");
                Assert.Less(Vector3.Angle(new Vector3(-1, 1, 1).normalized, mirrored.normals[0]), .01f);
            }
            finally { Object.DestroyImmediate(mirrored); }
        }
        finally { Object.DestroyImmediate(mesh); }
    }
}
