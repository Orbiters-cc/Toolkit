using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.Budget
{
    /// <summary>
    /// One platform's performance stats as plain data, kept with an estimate (across script reloads too): each category's
    /// value where VRChat counts one, each category's rating, and what costs most in each.
    /// </summary>
    public sealed class RankStats
    {
        public bool Mobile;
        public PerformanceRating Overall;
        public Dictionary<AvatarPerformanceCategory, float> Values = new Dictionary<AvatarPerformanceCategory, float>();
        public Dictionary<AvatarPerformanceCategory, PerformanceRating> Ratings = new Dictionary<AvatarPerformanceCategory, PerformanceRating>();
        /// <summary>The biggest contributors of each counted category, biggest first (<see cref="RankContributors"/>).</summary>
        public List<RankContributor> Costliest = new List<RankContributor>();
        /// <summary>What each Orbiters item on the avatar (an attachment) adds to each category.</summary>
        public List<RankItem> Items = new List<RankItem>();

        public float Value(AvatarPerformanceCategory category) => Values.TryGetValue(category, out float value) ? value : 0f;

        /// <summary>The SDK's stats of one platform (their texture memory already replaced by the build's).</summary>
        public static RankStats Of(AvatarPerformanceStats stats, bool mobile)
        {
            var result = new RankStats { Mobile = mobile, Overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall) };
            foreach (var category in RankPlan.Categories())
                result.Ratings[category] = stats.GetPerformanceRatingForCategory(category);
            foreach (var (category, value, _) in RankPlan.Counted)
                if (value(stats) is float v) result.Values[category] = v;
            return result;
        }
    }

    /// <summary>One thing that costs in a category: a renderer's polygons, a texture's memory, a PhysBone's transforms.</summary>
    public sealed class RankContributor
    {
        public AvatarPerformanceCategory Category;
        /// <summary>What it is (a renderer's object, a texture), and where it is on the avatar (the object holding it, from the root).</summary>
        public string Name, Path;
        /// <summary>The Orbiters item it belongs to (an attachment's name), or null.</summary>
        public string Item;
        public float Amount;
        /// <summary>The asset to show for it (a texture), else 0: the object at <see cref="Path"/>.</summary>
        public int Asset;
    }

    /// <summary>An Orbiters item (an attachment): what it adds to each category, removing it would save.</summary>
    public sealed class RankItem
    {
        public string Name, Path;
        public Dictionary<AvatarPerformanceCategory, float> Amounts = new Dictionary<AvatarPerformanceCategory, float>();
        public float Amount(AvatarPerformanceCategory category) => Amounts.TryGetValue(category, out float value) ? value : 0f;
    }

    /// <summary>
    /// How far an avatar is from a rank on one platform: for each category, its value at upload against the target's limit,
    /// how much over it is, and whether it holds the avatar below the target. Pure arithmetic over two estimates' stats
    /// (now and at upload): nothing is measured again.
    /// </summary>
    public sealed class RankPlan
    {
        public sealed class Gap
        {
            public AvatarPerformanceCategory Category;
            public string Name;
            /// <summary>The category has a value and limits; otherwise only its rating is known (a bounds or particle setting).</summary>
            public bool Counted;
            public float Now, Upload, Limit;
            public PerformanceRating Rating;
            /// <summary>It is rated worse than the target at upload: the avatar can't reach the target until it comes down.</summary>
            public bool Blocks;
            /// <summary>Each rank's limit, Excellent to Poor.</summary>
            public List<(PerformanceRating Rating, float Limit)> Ticks = new List<(PerformanceRating, float)>();
            /// <summary>How much it must come down to reach the target (0 when it doesn't block).</summary>
            public float Over => Counted && Blocks ? Math.Max(0f, Upload - Limit) : 0f;
            /// <summary><see cref="Over"/> as a share of the limit (1 when the limit is 0).</summary>
            public float OverShare => Over <= 0f ? 0f : Limit > 0f ? Over / Limit : 1f;
        }

        public PerformanceRating Target, Now, Upload;
        public bool Mobile;
        /// <summary>Every category rated below Excellent at upload, those that block the target first (the furthest over first).</summary>
        public List<Gap> Gaps = new List<Gap>();

        public IEnumerable<Gap> Blocking => Gaps.Where(g => g.Blocks);
        public bool Reached => Gaps.All(g => !g.Blocks);
        public Gap For(AvatarPerformanceCategory category) => Gaps.FirstOrDefault(g => g.Category == category);

        /// <summary>The ranks a plan can aim at, best first.</summary>
        public static readonly PerformanceRating[] Targets = { PerformanceRating.Excellent, PerformanceRating.Good, PerformanceRating.Medium, PerformanceRating.Poor };

        /// <summary>What the categories VRChat counts are, and the limit of each in a rank's level.</summary>
        internal static readonly (AvatarPerformanceCategory Category, Func<AvatarPerformanceStats, float?> Value, Func<AvatarPerformanceStatsLevel, float> Limit)[] Counted =
        {
            (AvatarPerformanceCategory.PolyCount, s => s.polyCount, l => l.polyCount),
            (AvatarPerformanceCategory.SkinnedMeshCount, s => s.skinnedMeshCount, l => l.skinnedMeshCount),
            (AvatarPerformanceCategory.MeshCount, s => s.meshCount, l => l.meshCount),
            (AvatarPerformanceCategory.MaterialCount, s => s.materialCount, l => l.materialCount),
            (AvatarPerformanceCategory.AnimatorCount, s => s.animatorCount, l => l.animatorCount),
            (AvatarPerformanceCategory.BoneCount, s => s.boneCount, l => l.boneCount),
            (AvatarPerformanceCategory.LightCount, s => s.lightCount, l => l.lightCount),
            (AvatarPerformanceCategory.ParticleSystemCount, s => s.particleSystemCount, l => l.particleSystemCount),
            (AvatarPerformanceCategory.ParticleTotalCount, s => s.particleTotalCount, l => l.particleTotalCount),
            (AvatarPerformanceCategory.ParticleMaxMeshPolyCount, s => s.particleMaxMeshPolyCount, l => l.particleMaxMeshPolyCount),
            (AvatarPerformanceCategory.TrailRendererCount, s => s.trailRendererCount, l => l.trailRendererCount),
            (AvatarPerformanceCategory.LineRendererCount, s => s.lineRendererCount, l => l.lineRendererCount),
            (AvatarPerformanceCategory.ClothCount, s => s.clothCount, l => l.clothCount),
            (AvatarPerformanceCategory.ClothMaxVertices, s => s.clothMaxVertices, l => l.clothMaxVertices),
            (AvatarPerformanceCategory.PhysicsColliderCount, s => s.physicsColliderCount, l => l.physicsColliderCount),
            (AvatarPerformanceCategory.PhysicsRigidbodyCount, s => s.physicsRigidbodyCount, l => l.physicsRigidbodyCount),
            (AvatarPerformanceCategory.AudioSourceCount, s => s.audioSourceCount, l => l.audioSourceCount),
            (AvatarPerformanceCategory.TextureMegabytes, s => s.textureMegabytes, l => l.textureMegabytes),
            (AvatarPerformanceCategory.PhysBoneComponentCount, s => s.physBone?.componentCount, l => l.physBone.componentCount),
            (AvatarPerformanceCategory.PhysBoneTransformCount, s => s.physBone?.transformCount, l => l.physBone.transformCount),
            (AvatarPerformanceCategory.PhysBoneColliderCount, s => s.physBone?.colliderCount, l => l.physBone.colliderCount),
            (AvatarPerformanceCategory.PhysBoneCollisionCheckCount, s => s.physBone?.collisionCheckCount, l => l.physBone.collisionCheckCount),
            (AvatarPerformanceCategory.ContactCount, s => s.contactCount, l => l.contactCount),
            (AvatarPerformanceCategory.ConstraintsCount, s => s.constraintsCount, l => l.constraintsCount),
            (AvatarPerformanceCategory.ConstraintDepth, s => s.constraintDepth, l => l.constraintDepth),
        };

        /// <summary>Every category the SDK rates (not the overall rating).</summary>
        public static IEnumerable<AvatarPerformanceCategory> Categories() =>
            Enum.GetValues(typeof(AvatarPerformanceCategory)).Cast<AvatarPerformanceCategory>().Where(c => c != AvatarPerformanceCategory.None &&
                c != AvatarPerformanceCategory.Overall && c != AvatarPerformanceCategory.AvatarPerformanceCategoryCount);

        /// <summary>The SDK's name for a category, as a sentence says it ("polygons", "texture memory").</summary>
        public static string NameOf(AvatarPerformanceCategory category) => AvatarPerformanceStats.GetPerformanceCategoryDisplayName(category);

        /// <summary>
        /// The default aim: one rank better than the upload's, never worse than the avatar's rank now, Excellent at best and
        /// Poor at worst.
        /// </summary>
        public static PerformanceRating DefaultTarget(PerformanceRating now, PerformanceRating upload)
        {
            var better = upload <= PerformanceRating.Excellent ? PerformanceRating.Excellent : upload - 1;
            if (now >= PerformanceRating.Excellent && now < better) better = now;
            return better < PerformanceRating.Excellent ? PerformanceRating.Excellent : better > PerformanceRating.Poor ? PerformanceRating.Poor : better;
        }

        /// <summary>The plan for <paramref name="target"/> on PC or mobile, with the SDK's own limits.</summary>
        /// <exception cref="InvalidOperationException">The SDK has not loaded its performance levels yet (<see cref="AvatarBudget.Ready"/>).</exception>
        public static RankPlan For(RankStats now, RankStats upload, PerformanceRating target, bool mobile) =>
            For(now, upload, target, mobile, rating => AvatarPerformanceStats.GetStatLevelForRating(rating, mobile)
                ?? throw new InvalidOperationException("The VRChat SDK has not loaded its performance levels yet."));

        /// <summary>The same with the levels given (tests): <paramref name="level"/> gives the limits of Excellent to Poor.</summary>
        public static RankPlan For(RankStats now, RankStats upload, PerformanceRating target, bool mobile, Func<PerformanceRating, AvatarPerformanceStatsLevel> level)
        {
            var plan = new RankPlan { Target = target, Mobile = mobile, Now = now?.Overall ?? upload.Overall, Upload = upload.Overall };
            var levels = Targets.ToDictionary(r => r, level);
            var counted = new HashSet<AvatarPerformanceCategory>();
            foreach (var (category, _, limit) in Counted)
            {
                counted.Add(category);
                if (!upload.Values.TryGetValue(category, out float value)) continue;
                var gap = new Gap
                {
                    Category = category, Name = NameOf(category), Counted = true, Upload = value, Now = now?.Value(category) ?? value,
                    Limit = limit(levels[target]),
                };
                foreach (var rating in Targets) gap.Ticks.Add((rating, limit(levels[rating])));
                gap.Rating = PerformanceRating.VeryPoor;
                foreach (var (rating, max) in gap.Ticks)
                    if (value <= max) { gap.Rating = rating; break; }
                gap.Blocks = gap.Rating > target;
                if (gap.Rating > PerformanceRating.Excellent || gap.Blocks) plan.Gaps.Add(gap);
            }
            // What has no value here (bounds, particle settings): its rating alone.
            foreach (var pair in upload.Ratings)
            {
                if (counted.Contains(pair.Key) || pair.Value <= PerformanceRating.Excellent) continue;
                plan.Gaps.Add(new Gap { Category = pair.Key, Name = NameOf(pair.Key), Rating = pair.Value, Blocks = pair.Value > target });
            }
            plan.Gaps = plan.Gaps.OrderByDescending(g => g.Blocks).ThenByDescending(g => g.Blocks ? g.OverShare : g.Limit > 0 ? g.Upload / g.Limit : 0f)
                .ThenBy(g => g.Category).ToList();
            return plan;
        }
    }
}
