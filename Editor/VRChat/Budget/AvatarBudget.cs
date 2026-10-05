using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.Toolkit.Editor.VRChat.Budget
{
    /// <summary>A counted stat, split between the avatar and the custom base it uses.</summary>
    public readonly struct BudgetCount
    {
        public readonly int Avatar, CustomBase;
        public BudgetCount(int avatar, int customBase) { Avatar = avatar; CustomBase = customBase; }
        public int Total => Avatar + CustomBase;
    }

    /// <summary>
    /// How an avatar uses VRChat's limits before it is built: synced parameter memory and the performance stats a custom
    /// base changes most (bones, PhysBones, contacts), each split between the avatar and its custom base. Changes the
    /// custom base's build makes (PhysBones it adds, bones it strips) are counted as built.
    /// </summary>
    public sealed class AvatarBudget
    {
        public ParameterBudget Parameters;
        public BudgetCount Bones, PhysBones, Contacts;
        /// <summary>What the build changes compared to the scene: PhysBones it adds, bones it removes.</summary>
        public int BuildPhysBones, BuildRemovedBones;
        /// <summary>The custom base as users know it, or null when the avatar has none.</summary>
        public string CustomBase;

        public static AvatarBudget Estimate(GameObject avatarRoot)
        {
            var budget = new AvatarBudget();
            if (avatarRoot == null) return budget;
            var info = CustomBases.Describe(avatarRoot.transform);
            CustomBaseFootprint footprint = null;
            try { footprint = info?.Footprint?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[Orbiters] Could not read what the custom base adds to " + avatarRoot.name + ": " + ex.Message); }
            budget.CustomBase = info?.Name;
            bool Owned(Transform t) => footprint != null && footprint.Owns(t);

            budget.Parameters = AvatarParameterBudget.Estimate(avatarRoot, new AvatarParameterBudget.Options { IsCustomBase = go => Owned(go.transform) });

            // VRChat counts the transforms skinned meshes use as bones.
            var bones = new HashSet<Transform>();
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var bone in renderer.bones)
                    if (bone != null) bones.Add(bone);
            budget.BuildRemovedBones = Mathf.Min(footprint?.BuildRemovedBones ?? 0, bones.Count);
            int customBones = footprint == null ? 0 : bones.Count(b => footprint.Bones.Contains(b) || Owned(b));
            int builtCustomBones = Mathf.Max(0, customBones - budget.BuildRemovedBones);
            budget.Bones = new BudgetCount(bones.Count - budget.BuildRemovedBones - builtCustomBones, builtCustomBones);

            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            budget.BuildPhysBones = footprint?.BuildPhysBones ?? 0;
            int customPhysBones = physBones.Count(p => Owned(p.transform));
            budget.PhysBones = new BudgetCount(physBones.Length - customPhysBones, customPhysBones + budget.BuildPhysBones);

            // Receivers only the wearer evaluates do not count.
            var contacts = avatarRoot.GetComponentsInChildren<ContactBase>(true).Where(c => !(c is ContactReceiver receiver && receiver.localOnly)).ToArray();
            int customContacts = contacts.Count(c => Owned(c.transform));
            budget.Contacts = new BudgetCount(contacts.Length - customContacts, customContacts);
            return budget;
        }

        private static readonly PerformanceRating[] Ratings =
            { PerformanceRating.Excellent, PerformanceRating.Good, PerformanceRating.Medium, PerformanceRating.Poor };

        /// <summary>
        /// False until the VRChat SDK has loaded its performance levels: it does on the first editor update after a
        /// script reload, and its lookups throw before that.
        /// </summary>
        public static bool Ready
        {
            get
            {
                try { return AvatarPerformanceStats.GetStatLevelForRating(PerformanceRating.Poor, false) != null; }
                catch (NullReferenceException) { return false; }
            }
        }

        /// <summary>VRChat's PC limit of a stat for a rating, from the SDK's own performance levels.</summary>
        /// <exception cref="InvalidOperationException">The SDK has not loaded its levels yet, as early in an editor reload.</exception>
        public static int Limit(Func<AvatarPerformanceStatsLevel, int> stat, PerformanceRating rating)
        {
            var level = AvatarPerformanceStats.GetStatLevelForRating(rating, false)
                ?? throw new InvalidOperationException("The VRChat SDK has not loaded its performance levels yet.");
            return stat(level);
        }

        /// <summary>The PC rating a value gets: the best rating whose limit it does not exceed.</summary>
        public static PerformanceRating Rate(Func<AvatarPerformanceStatsLevel, int> stat, int value)
        {
            foreach (var rating in Ratings)
                if (value <= Limit(stat, rating)) return rating;
            return PerformanceRating.VeryPoor;
        }

        public static IEnumerable<(PerformanceRating rating, int limit)> Limits(Func<AvatarPerformanceStatsLevel, int> stat) =>
            Ratings.Select(r => (r, Limit(stat, r)));
    }
}
