using System.IO;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;

// VRChat's OSC config of an uploaded avatar, deleted for every account (a temporary folder stands for VRChat's OSC folder).
public sealed class VrchatOscConfigTests
{
    private const string Avatar = "avtr_2bd4c3b1-6a8e-4d1f-9e0a-1c2d3e4f5a6b";
    private const string Other = "avtr_00000000-0000-0000-0000-000000000001";
    private string osc;

    [SetUp] public void Create() => osc = Path.Combine(Path.GetTempPath(), "orbiters-osc-config-" + System.Guid.NewGuid().ToString("N"));

    [TearDown] public void Delete()
    {
        if (Directory.Exists(osc)) Directory.Delete(osc, true);
    }

    private string Config(string account, string avatar)
    {
        string folder = Path.Combine(osc, account, "Avatars");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, avatar + ".json");
        File.WriteAllText(file, "{\"id\":\"" + avatar + "\"}");
        return file;
    }

    [Test] public void EveryAccountsConfigOfTheAvatarGoesAndNothingElse()
    {
        string first = Config("usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", Avatar);
        string second = Config("usr_c27307e5-012e-4a1e-ba6f-77dd3835f813", Avatar);
        string other = Config("usr_c27307e5-012e-4a1e-ba6f-77dd3835f813", Other);
        Directory.CreateDirectory(Path.Combine(osc, "usr_1339e525-d75d-4832-9104-98dd89287681", "Avatars"));
        // VRChat's own folders beside the accounts' (VOR) are not accounts.
        string notAnAccount = Config("VOR", Avatar);

        var result = VrchatOscConfig.Reset(osc, Avatar);

        Assert.IsFalse(File.Exists(first));
        Assert.IsFalse(File.Exists(second));
        Assert.IsTrue(File.Exists(other), "another avatar's config stays");
        Assert.IsTrue(File.Exists(notAnAccount));
        CollectionAssert.AreEquivalent(new[] { "usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", "usr_c27307e5-012e-4a1e-ba6f-77dd3835f813" }, result.Accounts);
        CollectionAssert.IsEmpty(result.Failures);
        // Again: nothing left to reset.
        CollectionAssert.IsEmpty(VrchatOscConfig.Reset(osc, Avatar).Accounts);
    }

    [Test] public void NoFolderNoAvatarNoChange()
    {
        CollectionAssert.IsEmpty(VrchatOscConfig.Reset(osc, Avatar).Accounts, "VRChat never ran here");
        CollectionAssert.IsEmpty(VrchatOscConfig.Reset(null, Avatar).Accounts);
        string kept = Config("usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", Avatar);
        // A folder named like the file is not a config.
        string folder = Path.Combine(osc, "usr_c27307e5-012e-4a1e-ba6f-77dd3835f813", "Avatars", Avatar + ".json");
        Directory.CreateDirectory(folder);
        foreach (string id in new[] { null, "", "avtr_", "local:sdk_Rex", "wrld_" + Avatar.Substring(5), Avatar + "/../x", Avatar + ".json", "avtr_*" })
            CollectionAssert.IsEmpty(VrchatOscConfig.Reset(osc, id).Accounts, id ?? "null");
        Assert.IsTrue(File.Exists(kept));
        Assert.IsTrue(Directory.Exists(folder));
        Assert.AreEqual(new[] { "usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03" }, VrchatOscConfig.Reset(osc, Avatar).Accounts.ToArray());
        Assert.IsTrue(Directory.Exists(folder));
    }

    [Test] public void UploadedAvatarIdsOnly()
    {
        Assert.IsTrue(VrchatOscConfig.IsAvatarId(Avatar));
        Assert.IsFalse(VrchatOscConfig.IsAvatarId("avtr_a.b"));
        Assert.IsFalse(VrchatOscConfig.IsAvatarId("avtr_a\\b"));
        Assert.IsFalse(VrchatOscConfig.IsAvatarId("AVTR_x"));
        Assert.IsFalse(VrchatOscConfig.IsAvatarId("avtr_" + new string('a', 60)));
    }

    // Haï's Auto-reset OSC config clears only the signed-in account's file: the upload's reset no longer leaves it the job,
    // so with or without it every account's file goes.
    [Test] public void TheUploadResetsEveryAccountWhileTheSwitchIsOn()
    {
        using (Switch(true))
        {
            string first = Config("usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", Avatar);
            string second = Config("usr_1e0c2252-2c70-4b5b-b691-bcc25bb39cec", Avatar);
            var result = VrchatOscConfig.AfterUpload(osc, Avatar);
            Assert.IsNotNull(result);
            Assert.AreEqual(2, result.Accounts.Count);
            Assert.IsFalse(File.Exists(first) || File.Exists(second));
        }
    }

    // My Avatar's switch off: the upload deletes nothing, in any account.
    [Test] public void TheSwitchOffKeepsEveryConfig()
    {
        using (Switch(false))
        {
            string first = Config("usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", Avatar);
            string second = Config("usr_1e0c2252-2c70-4b5b-b691-bcc25bb39cec", Avatar);
            Assert.IsFalse(VrchatOscConfig.Enabled);
            Assert.IsNull(VrchatOscConfig.AfterUpload(osc, Avatar));
            Assert.IsTrue(File.Exists(first) && File.Exists(second));
        }
    }

    // Toolkit without My Avatar (nothing registers the switch): the reset stays on.
    [Test] public void WithoutTheSwitchTheResetStaysOn()
    {
        var registered = OrbitersFeatures.Find(VrchatOscConfig.Feature);
        if (registered != null) OrbitersFeatures.Unregister(VrchatOscConfig.Feature);
        try
        {
            Assert.IsTrue(VrchatOscConfig.Enabled);
            string file = Config("usr_11a17e99-a6fb-4b6a-b8d9-78b725f73b03", Avatar);
            Assert.AreEqual(1, VrchatOscConfig.AfterUpload(osc, Avatar).Accounts.Count);
            Assert.IsFalse(File.Exists(file));
        }
        finally { if (registered != null) OrbitersFeatures.Register(registered); }
    }

    // The switch as My Avatar registers it (or a stand-in where it isn't installed), set for the test, the user's choice back after.
    private static System.IDisposable Switch(bool on)
    {
        var feature = OrbitersFeatures.Find(VrchatOscConfig.Feature);
        bool standIn = feature == null;
        if (standIn) feature = OrbitersFeatures.Register(new OrbitersFeature { Key = VrchatOscConfig.Feature, Product = "Tests", Default = true });
        bool had = EditorPrefs.HasKey(feature.PrefKey), before = EditorPrefs.GetBool(feature.PrefKey, feature.Default);
        OrbitersFeatures.SetEnabled(VrchatOscConfig.Feature, on);
        return new Restore(() =>
        {
            if (had) EditorPrefs.SetBool(feature.PrefKey, before); else EditorPrefs.DeleteKey(feature.PrefKey);
            if (standIn) OrbitersFeatures.Unregister(VrchatOscConfig.Feature);
        });
    }

    private sealed class Restore : System.IDisposable
    {
        private System.Action action;
        public Restore(System.Action action) => this.action = action;
        public void Dispose() { action?.Invoke(); action = null; }
    }

    // VRChat's own folder, where the upload's reset looks.
    [Test] public void TheFolderIsVrchatsLocalLowOsc()
    {
        StringAssert.EndsWith(Path.Combine("AppData", "LocalLow", "VRChat", "VRChat", "OSC"), VrchatOscConfig.Folder);
    }
}
