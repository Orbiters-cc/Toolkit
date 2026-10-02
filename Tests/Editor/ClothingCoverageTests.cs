using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

public class ClothingCoverageTests
{
    [TestCase("Hoodie", true)]
    [TestCase("Rex_Pants", true)]
    [TestCase("T-Shirt", true)]
    [TestCase("Underwear", true)]
    [TestCase("Glowsticks", false)]
    [TestCase("ShirtButton", false)]
    public void OnlyBodyClothingWithDimensionEvidenceOptsIn(string name, bool eligible)
    {
        var go = new GameObject(name);
        try
        {
            var item = go.AddComponent<OrbitersAttachment>();
            Assert.IsFalse(ClothingCoverage.Eligible(item));
            var pose = new OrbitersAttachment.FittedPose { transform = go.transform,
                before = new OrbitersAttachment.LocalPose { scale = Vector3.one, rotation = Quaternion.identity },
                after = new OrbitersAttachment.LocalPose { scale = Vector3.one, rotation = Quaternion.Euler(30, 0, 0), position = Vector3.one } };
            item.fitted.Add(pose);
            Assert.IsFalse(ClothingCoverage.Eligible(item), "Pose and placement do not prove a different base.");
            pose.after.scale = Vector3.one * .9f; item.fitted[0] = pose;
            Assert.AreEqual(eligible, ClothingCoverage.Eligible(item));
        }
        finally { Object.DestroyImmediate(go); }
    }
}
