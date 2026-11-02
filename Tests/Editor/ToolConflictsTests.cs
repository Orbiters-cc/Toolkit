using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

// ToolConflicts' entries decided from the packages installed (pretended here), found by the compatibility project.
public sealed class ToolConflictsTests
{
    // params profile, 2026-11-02: Lazy Optimiser deleted N4N0's capture rig and kit (switched on through their renderers).
    [Test] public void LazyOptimiserIsAWarningForAnAvatarWithOrbitersItems()
    {
        var avatar = new GameObject("Lazy avatar");
        var item = new GameObject("N4N0");
        item.transform.SetParent(avatar.transform);
        try
        {
            int was = PlayerPrefs.GetInt("lazyoptimiser.shouldOptimise", 1);
            PlayerPrefs.SetInt("lazyoptimiser.shouldOptimise", 1);
            try
            {
                using (InstalledTools.Pretend(("net.euan.lazyoptimiser", "0.2.6"), (InstalledTools.VrcFuryId, "1.1434.0")))
                {
                    Assert.AreEqual(IssueLevel.Note, ToolConflicts.Find(avatar).Find(i => i.Title.StartsWith("Lazy Optimiser")).Level, "no Orbiters item: a note");
                    item.AddComponent<OrbitersAttachment>();
                    var issue = ToolConflicts.Find(avatar).Find(i => i.Title.StartsWith("Lazy Optimiser"));
                    Assert.IsNotNull(issue);
                    Assert.AreEqual(IssueLevel.Warning, issue.Level);
                    StringAssert.Contains("N4N0", issue.Where);
                }
                using (InstalledTools.Pretend((InstalledTools.VrcFuryId, "1.1434.0")))
                    Assert.IsFalse(ToolConflicts.Find(avatar).Exists(i => i.Title.StartsWith("Lazy Optimiser")), "not installed");
            }
            finally { PlayerPrefs.SetInt("lazyoptimiser.shouldOptimise", was); }
        }
        finally { Object.DestroyImmediate(avatar); }
    }
}
