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
}
