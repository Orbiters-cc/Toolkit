using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEngine;

// ToolConflicts' VRCFury Stub entry, decided from the packages installed (pretended here).
public sealed class ToolConflictsStubTests
{
    // The Checks' "Other tools": VRCFury components that VRCFury Stub keeps but nothing builds.
    [Test] public void ChecksSayVrcFuryStubComponentsWontBuild()
    {
        var type = VrcFury.Component;
        if (type == null) Assert.Ignore("VRCFury's types aren't loaded: no component to put on the avatar.");
        var avatar = new GameObject("Stub avatar");
        try
        {
            avatar.AddComponent(type);
            using (InstalledTools.Pretend((InstalledTools.VrcFuryStubId, "1.1436.0")))
            {
                var issues = ToolConflicts.Find(avatar);
                Assert.AreEqual(1, issues.Count);
                StringAssert.Contains("VRCFury Stub", issues[0].Title);
                Assert.AreEqual(IssueLevel.Warning, issues[0].Level);
            }
            using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
                Assert.IsEmpty(ToolConflicts.Find(avatar), "VRCFury builds them");
        }
        finally { Object.DestroyImmediate(avatar); }
    }
}
