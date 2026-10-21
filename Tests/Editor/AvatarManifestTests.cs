using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Manifest;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

// The avatar manifest Orbiters desktop reads (contracts Docs/avatar-manifest.md and its schema): file names, tags, and the
// menu and parameters as VRChat shows them.
public class AvatarManifestTests
{
    private readonly List<Object> owned = new List<Object>();
    private string folder;

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "OrbitersManifestTest-" + Guid.NewGuid().ToString("N"));
        AvatarManifest.Folder = folder;
    }

    [TearDown]
    public void TearDown()
    {
        AvatarManifest.Folder = null;
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        if (Directory.Exists(folder) && Path.GetFileName(folder).StartsWith("OrbitersManifestTest-", StringComparison.Ordinal)) Directory.Delete(folder, true);
    }

    [Test]
    public void FileNamesKeepOnlyLettersDigitsUnderscoresAndHyphens()
    {
        Assert.AreEqual("avtr_2bd4-a.json", AvatarManifest.FileName("avtr_2bd4-a"));
        Assert.AreEqual("local_12____x.json", AvatarManifest.FileName("local:12/..\\x"));
        Assert.AreEqual("local_sdk_Rexouium_1_6__.json", AvatarManifest.FileName("local:sdk_Rexouium 1.6 é"));
    }

    [Test]
    public void TagsComeFromLabelsFoldersAndParameterNamesInSeveralLanguages()
    {
        AvatarTags Of(string label, params string[] parameters) => AvatarManifestTags.Of(label, parameters);
        Assert.AreEqual(AvatarTags.Clothing, Of("Hoodie"));
        Assert.AreEqual(AvatarTags.Clothing, Of("<color=#ff8800>Sweat à capuche</color>"));
        Assert.AreEqual(AvatarTags.Clothing, Of("Chaqueta"));
        Assert.AreEqual(AvatarTags.Clothing, Of("Jacke"));
        Assert.AreEqual(AvatarTags.Clothing, Of("パーカー"));
        Assert.AreEqual(AvatarTags.Clothing, Of("ﾌﾞｰﾂ"), "Half width katakana");
        Assert.AreEqual(AvatarTags.Clothing, Of("Toggle 2", "VF12_Clothes/Shirt2"), "From the parameter's words");
        Assert.AreEqual(AvatarTags.Clothing, Of("Boots"), "Plurals");
        Assert.AreEqual(AvatarTags.Accessory, Of("Lunettes"));
        Assert.AreEqual(AvatarTags.Hair, Of("髪型"));
        Assert.AreEqual(AvatarTags.Hair, Of("Ponytail"));
        Assert.AreEqual(AvatarTags.Body, Of("Breast size"), "A body slider is no NSFW toggle");
        Assert.AreEqual(AvatarTags.Face, Of("Face tracking"));
        Assert.AreEqual(AvatarTags.Nsfw, Of("Nude"));
        Assert.AreEqual(AvatarTags.Nsfw, Of("SPS"));
        Assert.AreEqual(AvatarTags.Nsfw, Of("Nackt"));
        Assert.AreEqual(AvatarTags.Emote, Of("Dance"));
        Assert.AreEqual(AvatarTags.Locomotion, Of("GoGo Loco"));
        Assert.AreEqual(AvatarTags.Prop, Of("Drawing pen"));
        Assert.AreEqual(AvatarTags.Effect, Of("Particles"));
        Assert.AreEqual(AvatarTags.System, Of("Réglages"));
        Assert.AreEqual(AvatarTags.None, Of("Toggle 1"), "Nothing clear, no tag");
        CollectionAssert.AreEqual(new[] { "clothing", "nsfw", "orb" }, AvatarManifestTags.Names(AvatarTags.Orb | AvatarTags.Clothing | AvatarTags.Nsfw));
    }

    [Test]
    public void TheManifestListsTheMenuAsVRChatShowsIt()
    {
        var clothes = Menu(Item("<b>Hoodie</b>", Control.ControlType.Toggle, "VF12_Clothes/Hoodie"), Item("Nude", Control.ControlType.Toggle, "VF13_Nude"));
        var orb = Menu(Item("Show Orb", Control.ControlType.Toggle, OrbInstaller.Visible), Item("Talk", Control.ControlType.Button, OrbInstaller.Talk));
        var main = Menu(Folder("Clothes", clothes), Item("Hair colour", Control.ControlType.RadialPuppet, "", "Hair/Hue"),
            Item("Move", Control.ControlType.TwoAxisPuppet, "Move/Open", "Move/X", "Move/Y"), Folder("Orb", orb), Folder("Clothes again", clothes));
        // A loop back to the main menu.
        clothes.controls.Add(Folder("Back", main));
        var parameters = new List<VRCExpressionParameters.Parameter>
        {
            new VRCExpressionParameters.Parameter { name = "VF12_Clothes/Hoodie", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true, saved = true, defaultValue = 1 },
            new VRCExpressionParameters.Parameter { name = "VF13_Nude", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true },
            new VRCExpressionParameters.Parameter { name = "Hair/Hue", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true, defaultValue = .25f },
            new VRCExpressionParameters.Parameter { name = OrbInstaller.Visible, valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true, saved = true, defaultValue = 1 },
            new VRCExpressionParameters.Parameter { name = OrbInstaller.TextPointer, valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true },
        };
        for (int i = 0; i < 8; i++) parameters.Add(new VRCExpressionParameters.Parameter { name = OrbInstaller.TextSync(i), valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true });
        var avatar = new GameObject("Rexouium");
        owned.Add(avatar);
        var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
        descriptor.customExpressions = true;
        descriptor.expressionsMenu = main;
        descriptor.expressionParameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
        descriptor.expressionParameters.parameters = parameters.ToArray();

        var manifest = AvatarManifest.Read(avatar, "avtr_test");
        manifest.WrittenUtc = "2026-10-21T19:04:11Z"; manifest.Generator = "Orbiters Toolkit test";
        var json = JObject.Parse(manifest.ToJson());
        Valid(json);
        Assert.AreEqual(("avtr_test", "Rexouium", 8), ((string)json["avatar_id"], (string)json["name"], (int)json["orb"]["text_sync"]));
        var hoodie = json["parameters"].First(p => (string)p["name"] == "VF12_Clothes/Hoodie");
        Assert.AreEqual(("bool", true, true, 1.0), ((string)hoodie["type"], (bool)hoodie["synced"], (bool)hoodie["saved"], (double)hoodie["default"]));
        Assert.AreEqual(.25, (double)json["parameters"].First(p => (string)p["name"] == "Hair/Hue")["default"], 1e-6);

        var controls = json["controls"].ToDictionary(c => string.Join("/", c["path"].Select(l => (string)l)));
        CollectionAssert.AreEqual(new[] { "Clothes", "Clothes/Hoodie", "Clothes/Nude", "Hair colour", "Move", "Orb", "Orb/Show Orb", "Orb/Talk" }, controls.Keys,
            "Parents before their contents, each submenu once, rich text gone");
        var toggle = controls["Clothes/Hoodie"];
        Assert.AreEqual(("toggle", "VF12_Clothes/Hoodie", 1.0), ((string)toggle["type"], (string)toggle["parameter"], (double)toggle["value"]));
        CollectionAssert.AreEqual(new[] { "clothing" }, toggle["tags"].Select(t => (string)t), "Its folder's tag and its own");
        CollectionAssert.AreEqual(new[] { "clothing", "nsfw" }, controls["Clothes/Nude"]["tags"].Select(t => (string)t));
        var radial = controls["Hair colour"];
        Assert.AreEqual(("radial", "Hair/Hue"), ((string)radial["type"], (string)radial["parameter"]));
        CollectionAssert.AreEqual(new[] { "Hair/Hue" }, radial["sub_parameters"].Select(t => (string)t));
        CollectionAssert.Contains(radial["tags"].Select(t => (string)t).ToList(), "hair");
        var puppet = controls["Move"];
        Assert.AreEqual(("two_axis", "Move/Open"), ((string)puppet["type"], (string)puppet["parameter"]));
        CollectionAssert.AreEqual(new[] { "Move/X", "Move/Y" }, puppet["sub_parameters"].Select(t => (string)t));
        CollectionAssert.AreEqual(new[] { "Up", "Right", "Down", "Left" }, puppet["labels"].Select(t => (string)t));
        CollectionAssert.AreEqual(new[] { "orb" }, controls["Orb"]["tags"].Select(t => (string)t), "A folder of Orb's controls");
        Assert.AreEqual("button", (string)controls["Orb/Talk"]["type"]);
        CollectionAssert.Contains(controls["Orb/Talk"]["tags"].Select(t => (string)t).ToList(), "orb");

        // Written in one step under its id's file name; a newer build replaces it.
        string path = AvatarManifestExporter.Write(manifest, "local:sdk_Rexouium");
        Assert.AreEqual(Path.Combine(folder, "local_sdk_Rexouium.json"), path);
        Assert.AreEqual("local:sdk_Rexouium", (string)JObject.Parse(File.ReadAllText(path))["avatar_id"]);
        manifest.Name = "Rexouium 2";
        AvatarManifestExporter.Write(manifest, "local:sdk_Rexouium");
        Assert.AreEqual("Rexouium 2", (string)JObject.Parse(File.ReadAllText(path))["name"]);
        CollectionAssert.AreEqual(new[] { "local_sdk_Rexouium.json" }, Directory.GetFiles(folder).Select(Path.GetFileName), "No temporary file left");

        // An avatar without the prop has no orb; one with VRChat's default menu has no controls.
        descriptor.expressionParameters.parameters = parameters.Take(3).ToArray();
        Assert.IsNull(JObject.Parse(AvatarManifest.Read(avatar, "avtr_test").ToJson())["orb"]);
        descriptor.customExpressions = false;
        Valid(JObject.Parse(AvatarManifest.Read(avatar, "avtr_test").ToJson()));
        Assert.AreEqual(0, JObject.Parse(AvatarManifest.Read(avatar, "avtr_test").ToJson())["controls"].Count());
    }

    [Test]
    public void TheExporterRunsAfterEveryOtherBuildStep()
    {
        var exporter = new AvatarManifestExporter();
        var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var callbacks = (field?.GetValue(null) as IEnumerable<IVRCSDKPreprocessAvatarCallback>)?.ToList();
        Assert.IsNotNull(callbacks, "The SDK lists its build steps where Orbiters looks for them");
        Assert.IsTrue(callbacks.Any(c => c is AvatarManifestExporter), "Registered with the SDK");
        foreach (var callback in callbacks) Assert.LessOrEqual(callback.callbackOrder, exporter.callbackOrder, callback.GetType().FullName);
        Assert.IsTrue(HiddenAvatarBuild.LeftOut(typeof(AvatarManifestExporter)), "Orbiters' hidden builds upload nothing");
        // Outside the SDK's own builds (here, a test), nothing is written.
        var avatar = new GameObject("Not built");
        owned.Add(avatar);
        avatar.AddComponent<VRCAvatarDescriptor>();
        Assert.IsTrue(exporter.OnPreprocessAvatar(avatar));
        Assert.IsFalse(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    // The schema's required fields, types and enums (contracts Schemas/avatar-manifest.schema.json).
    private static void Valid(JObject json)
    {
        foreach (string field in new[] { "format", "version", "avatar_id", "parameters", "controls" }) Assert.IsNotNull(json[field], field);
        Assert.AreEqual("orbiters.avatar", (string)json["format"]);
        Assert.AreEqual(1, (int)json["version"]);
        Assert.IsNotEmpty((string)json["avatar_id"]);
        foreach (var p in json["parameters"])
        {
            Assert.IsNotEmpty((string)p["name"]);
            CollectionAssert.Contains(new[] { "bool", "int", "float" }, (string)p["type"]);
            Assert.AreEqual(JTokenType.Boolean, p["synced"].Type);
            Assert.AreEqual(JTokenType.Boolean, p["saved"].Type);
            Assert.That(p["default"].Type, Is.EqualTo(JTokenType.Integer).Or.EqualTo(JTokenType.Float));
        }
        var tags = new[] { "clothing", "accessory", "hair", "body", "face", "nsfw", "pose", "emote", "locomotion", "prop", "effect", "system", "orb" };
        foreach (var c in json["controls"])
        {
            Assert.IsNotEmpty(c["path"]);
            CollectionAssert.Contains(new[] { "toggle", "button", "submenu", "radial", "two_axis", "four_axis" }, (string)c["type"]);
            foreach (var tag in c["tags"] ?? new JArray()) CollectionAssert.Contains(tags, (string)tag);
        }
        if (json["orb"] != null && json["orb"].Type != JTokenType.Null) CollectionAssert.Contains(new[] { 4, 8, 16 }, (int)json["orb"]["text_sync"]);
    }

    private VRCExpressionsMenu Menu(params Control[] controls)
    {
        var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
        menu.controls = controls.ToList();
        return menu;
    }

    private static Control Item(string name, Control.ControlType type, string parameter, params string[] subs) => new Control
    {
        name = name, type = type, value = 1, parameter = new Control.Parameter { name = parameter },
        subParameters = subs.Select(s => new Control.Parameter { name = s }).ToArray(),
        labels = type == Control.ControlType.TwoAxisPuppet ? new[] { "Up", "Right", "Down", "Left" }.Select(l => new Control.Label { name = l }).ToArray() : null,
    };

    private static Control Folder(string name, VRCExpressionsMenu menu) => new Control { name = name, type = Control.ControlType.SubMenu, subMenu = menu };

    private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
}
