using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

public class ClothingCoverageTests
{
    [TestCase("Hoodie", true)]
    [TestCase("Rex_Pants", true)]
    [TestCase("Rex_Shorts", true)]
    [TestCase("Jacket_V1.1", true)]
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

    [Test] public void OutfitInspectsEachGarmentWithoutExpandingItsProps()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        var root = new GameObject("[P.0.E] - FishingOutfit - Rex");
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
        try
        {
            var item = root.AddComponent<OrbitersAttachment>();
            foreach (string name in new[] { "Jacket", "Shirt", "Shorts", "FishingRod", "ShirtButton" })
            {
                var go = new GameObject(name); go.transform.SetParent(root.transform, false);
                go.AddComponent<SkinnedMeshRenderer>();
            }
            Assert.IsFalse(ClothingCoverage.Eligible(item));
            Assert.IsTrue(ClothingCoverage.Eligible(item, true));
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                Assert.AreEqual(renderer.name == "Jacket" || renderer.name == "Shirt" || renderer.name == "Shorts",
                    ClothingCoverage.Eligible(item, renderer, true), renderer.name);
        }
        finally { Object.DestroyImmediate(root); UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
    }
}
