using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEngine;

// What the project has, decided from the packages installed (pretended here), not from the types loaded.
public sealed class InstalledToolsTests
{
    [Test] public void VrcFuryAloneIsVrcFury()
    {
        using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
        {
            Assert.IsTrue(InstalledTools.VrcFury);
            Assert.IsFalse(InstalledTools.VrcFuryStub);
            Assert.AreEqual("1.1434.0", InstalledTools.Find(InstalledTools.VrcFuryId).Version);
        }
    }

    // The stub keeps VRCFury's component types: a type lookup finds them, but nothing builds them.
    [Test] public void VrcFuryStubIsNotVrcFury()
    {
        using (InstalledTools.Pretend((InstalledTools.VrcFuryStubId, "1.1436.0")))
        {
            Assert.IsFalse(InstalledTools.VrcFury);
            Assert.IsTrue(InstalledTools.VrcFuryStub);
            Assert.IsFalse(VrcFury.Installed, "VRCFury's types may be loaded, but the stub builds nothing");
        }
        using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0"), (InstalledTools.VrcFuryStubId, "1.1436.0")))
            Assert.IsFalse(InstalledTools.VrcFury, "with the stub installed too, VRCFury's build doesn't run");
    }

    [Test] public void AnAvatarWithVrcFuryComponentsIsNotBuiltWithoutVrcFury()
    {
        var avatar = new GameObject("Stub avatar");
        try
        {
            var type = VrcFury.Component;
            if (type != null) avatar.AddComponent(type);
            using (InstalledTools.Pretend((InstalledTools.VrcFuryStubId, "1.1436.0")))
                Assert.IsFalse(VrcFury.Builds(avatar));
            if (type == null) return;
            using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsTrue(VrcFury.Builds(avatar));
        }
        finally { Object.DestroyImmediate(avatar); }
    }

    [Test] public void ModularAvatarNdmfAndOptimizersByPackage()
    {
        using (InstalledTools.Pretend((InstalledTools.NdmfId, "1.8.0"), (InstalledTools.ModularAvatarId, "1.13.0")))
        {
            Assert.IsTrue(InstalledTools.Ndmf);
            Assert.IsTrue(InstalledTools.ModularAvatar);
            Assert.IsFalse(InstalledTools.Aao);
            Assert.IsFalse(InstalledTools.D4rk);
            Assert.IsFalse(InstalledTools.TexTransTool);
            Assert.IsNull(InstalledTools.Meshia);
        }
        using (InstalledTools.Pretend((InstalledTools.AaoId, "1.9.0"), (InstalledTools.D4rkId, "3.0.0"), (InstalledTools.TexTransToolId, "0.9.0")))
        {
            Assert.IsTrue(InstalledTools.Aao);
            Assert.IsTrue(InstalledTools.D4rk);
            Assert.IsTrue(InstalledTools.TexTransTool);
            Assert.IsFalse(InstalledTools.ModularAvatar);
        }
    }

    [Test] public void AnyMeshiaForkIsMeshia()
    {
        using (InstalledTools.Pretend(("net.kaikoga.meshia.mesh-simplification.ablet", "0.3.0")))
            Assert.AreEqual("net.kaikoga.meshia.mesh-simplification.ablet", InstalledTools.Meshia?.Id);
        using (InstalledTools.Pretend((InstalledTools.MeshiaIds[0], "0.4.0"), ("com.synthos.meshia", "1.0.0")))
            Assert.AreEqual(InstalledTools.MeshiaIds[0], InstalledTools.Meshia?.Id, "the original first");
    }

    [Test] public void PretendingEndsWithItsScope()
    {
        using (InstalledTools.Pretend((InstalledTools.AaoId, "1.0.0")))
        {
            using (InstalledTools.Pretend()) Assert.IsFalse(InstalledTools.Aao);
            Assert.IsTrue(InstalledTools.Aao, "the outer pretence again");
        }
    }
}
