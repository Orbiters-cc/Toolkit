using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEngine;

public sealed class AvatarObjectToolsTests
{
    // A tool that has a newer version for one object (as My Avatar for a gallery item).
    private sealed class Source : IAvatarObjectUpdates
    {
        public GameObject For;
        public string To;
        public AvatarObjectUpdate UpdateOf(GameObject go) => go == For ? new AvatarObjectUpdate { Name = go.name, From = "1.0.0", To = To } : null;
    }

    [Test]
    public void TheFirstToolThatKnowsAnUpdateAnswers()
    {
        var item = new GameObject("Item");
        var other = new GameObject("Other");
        var first = new Source { For = item, To = "1.0.1" };
        var second = new Source { For = item, To = "2.0.0" };
        int changes = 0;
        System.Action count = () => changes++;
        AvatarObjectTools.Changed += count;
        try
        {
            AvatarObjectTools.RegisterUpdates(first);
            AvatarObjectTools.RegisterUpdates(second);
            AvatarObjectTools.RegisterUpdates(first);
            Assert.AreEqual(2, changes, "Each tool registered once, and the views told each time");
            Assert.AreEqual("1.0.1", AvatarObjectTools.UpdateOf(item).To, "The first registered answer wins");
            Assert.IsNull(AvatarObjectTools.UpdateOf(other), "Nothing waits for an object no tool knows");
            Assert.IsNull(AvatarObjectTools.UpdateOf(null));
            AvatarObjectTools.UnregisterUpdates(first);
            Assert.AreEqual("2.0.0", AvatarObjectTools.UpdateOf(item).To, "The next tool answers once the first is gone");
        }
        finally
        {
            AvatarObjectTools.Changed -= count;
            AvatarObjectTools.UnregisterUpdates(first);
            AvatarObjectTools.UnregisterUpdates(second);
            Object.DestroyImmediate(item);
            Object.DestroyImmediate(other);
        }
    }
}
