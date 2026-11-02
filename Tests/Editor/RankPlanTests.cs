using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

// How far synthetic stats are from a rank, with the SDK's own PC and mobile limits: pure arithmetic, nothing measured.
public sealed class RankPlanTests
{
    [SetUp] public void SetUp()
    {
        if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
    }

    private static float Limit(AvatarPerformanceCategory category, PerformanceRating rating, bool mobile)
    {
        var level = AvatarPerformanceStats.GetStatLevelForRating(rating, mobile);
        return RankPlan.Counted.First(c => c.Category == category).Limit(level);
    }

    private static RankStats Stats(bool mobile, params (AvatarPerformanceCategory Category, float Value)[] values)
    {
        var stats = new RankStats { Mobile = mobile };
        foreach (var (category, value) in values) stats.Values[category] = value;
        foreach (var category in RankPlan.Categories()) stats.Ratings[category] = PerformanceRating.Excellent;
        return stats;
    }

    [Test] public void ACategoryOverTheTargetsLimitBlocksIt()
    {
        float good = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, false);
        var upload = Stats(false, (AvatarPerformanceCategory.PolyCount, good + 1000), (AvatarPerformanceCategory.MaterialCount, 1));
        var plan = RankPlan.For(null, upload, PerformanceRating.Good, false);
        var polygons = plan.For(AvatarPerformanceCategory.PolyCount);
        Assert.IsTrue(polygons.Blocks);
        Assert.AreEqual(PerformanceRating.Medium, polygons.Rating, "just over Good is Medium");
        Assert.AreEqual(1000f, polygons.Over, 1e-3f);
        Assert.AreEqual(1000f / good, polygons.OverShare, 1e-5f);
        Assert.AreEqual(4, polygons.Ticks.Count, "Excellent, Good, Medium and Poor");
        Assert.IsFalse(plan.Reached);
        Assert.IsNull(plan.For(AvatarPerformanceCategory.MaterialCount), "one material is Excellent: no gap");

        var medium = RankPlan.For(null, upload, PerformanceRating.Medium, false);
        Assert.IsTrue(medium.Reached, "Medium allows it");
        Assert.AreEqual(0f, medium.For(AvatarPerformanceCategory.PolyCount).Over);
    }

    [Test] public void QuestsLimitsBlockWhatPcAllows()
    {
        float pcGood = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, false);
        float questGood = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, true);
        Assume.That(questGood, Is.LessThan(pcGood));
        float value = (questGood + pcGood) / 2f;
        Assert.IsTrue(RankPlan.For(null, Stats(false, (AvatarPerformanceCategory.PolyCount, value)), PerformanceRating.Good, false).Reached);
        var quest = RankPlan.For(null, Stats(true, (AvatarPerformanceCategory.PolyCount, value)), PerformanceRating.Good, true);
        Assert.IsFalse(quest.Reached);
        Assert.AreEqual(questGood, quest.For(AvatarPerformanceCategory.PolyCount).Limit, 1e-3f);
    }

    [Test] public void TheFurthestOverComesFirst()
    {
        float polygons = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, false);
        float bones = Limit(AvatarPerformanceCategory.BoneCount, PerformanceRating.Good, false);
        var upload = Stats(false, (AvatarPerformanceCategory.PolyCount, polygons * 1.1f), (AvatarPerformanceCategory.BoneCount, bones * 1.5f));
        var plan = RankPlan.For(null, upload, PerformanceRating.Good, false);
        CollectionAssert.AreEqual(new[] { AvatarPerformanceCategory.BoneCount, AvatarPerformanceCategory.PolyCount },
            plan.Blocking.Select(g => g.Category).ToArray());
    }

    [Test] public void WhatHasNoValueBlocksByItsRating()
    {
        var upload = Stats(false);
        upload.Ratings[AvatarPerformanceCategory.ParticleTrailsEnabled] = PerformanceRating.Poor;
        var plan = RankPlan.For(null, upload, PerformanceRating.Good, false);
        var trails = plan.For(AvatarPerformanceCategory.ParticleTrailsEnabled);
        Assert.IsTrue(trails.Blocks);
        Assert.IsFalse(trails.Counted);
        Assert.AreEqual(0f, trails.Over);
        Assert.IsTrue(RankPlan.For(null, upload, PerformanceRating.Poor, false).Reached);
    }

    [Test] public void TheNowValuesComeAlong()
    {
        float good = Limit(AvatarPerformanceCategory.SkinnedMeshCount, PerformanceRating.Good, false);
        var now = Stats(false, (AvatarPerformanceCategory.SkinnedMeshCount, good + 4));
        var upload = Stats(false, (AvatarPerformanceCategory.SkinnedMeshCount, good + 1));
        var gap = RankPlan.For(now, upload, PerformanceRating.Good, false).For(AvatarPerformanceCategory.SkinnedMeshCount);
        Assert.AreEqual(good + 4, gap.Now);
        Assert.AreEqual(good + 1, gap.Upload);
    }

    [Test] public void TheDefaultAimIsOneRankBetterThanTheUpload()
    {
        Assert.AreEqual(PerformanceRating.Poor, RankPlan.DefaultTarget(PerformanceRating.VeryPoor, PerformanceRating.VeryPoor));
        Assert.AreEqual(PerformanceRating.Medium, RankPlan.DefaultTarget(PerformanceRating.VeryPoor, PerformanceRating.Poor));
        Assert.AreEqual(PerformanceRating.Excellent, RankPlan.DefaultTarget(PerformanceRating.Good, PerformanceRating.Excellent));
        Assert.AreEqual(PerformanceRating.Good, RankPlan.DefaultTarget(PerformanceRating.Good, PerformanceRating.Poor), "never worse than the rank now");
    }

    [Test] public void ContributorsNameTheirItemAndKeepTheBiggest()
    {
        var root = new GameObject("Avatar");
        try
        {
            Renderer(root.transform, "Body", 400);
            var jacket = new GameObject("Jacket").transform; jacket.SetParent(root.transform, false);
            jacket.gameObject.AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
            Renderer(jacket, "Sleeves", 100);
            Renderer(jacket, "Collar", 50);

            var contributors = RankContributors.Of(root);
            var stats = new RankStats();
            contributors.Fill(stats, _ => 0f);
            var polygons = stats.Costliest.Where(c => c.Category == AvatarPerformanceCategory.PolyCount).ToList();
            CollectionAssert.AreEqual(new[] { "Body", "Sleeves", "Collar" }, polygons.Select(c => c.Name).ToArray());
            Assert.AreEqual(400f, polygons[0].Amount);
            Assert.IsNull(polygons[0].Item);
            Assert.AreEqual("Jacket", polygons[1].Item);
            Assert.AreEqual("Jacket/Sleeves", polygons[1].Path);
            var item = stats.Items.Single();
            Assert.AreEqual("Jacket", item.Name);
            Assert.AreEqual(150f, item.Amount(AvatarPerformanceCategory.PolyCount));
            Assert.AreEqual(2f, item.Amount(AvatarPerformanceCategory.MeshCount));
        }
        finally
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh != null) Object.DestroyImmediate(filter.sharedMesh);
            Object.DestroyImmediate(root);
        }
    }

    // A mesh renderer with that many triangles.
    private static MeshFilter Renderer(Transform parent, string name, int triangles)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        var mesh = new Mesh();
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        for (int i = 0; i < triangles; i++)
        {
            vertices.Add(new Vector3(i, 0, 0)); vertices.Add(new Vector3(i, 1, 0)); vertices.Add(new Vector3(i + 1, 0, 0));
            indices.Add(3 * i); indices.Add(3 * i + 1); indices.Add(3 * i + 2);
        }
        mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0);
        var filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = new Material[1];
        return filter;
    }
}
