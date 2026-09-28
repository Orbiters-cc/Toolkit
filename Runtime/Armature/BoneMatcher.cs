using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Armature
{
    public enum BoneMatchKind { None, ExactName, AffixName, Humanoid, ContainedName }

    public readonly struct BoneMatch
    {
        public readonly Transform Source, Target;
        public readonly BoneMatchKind Kind;
        /// <summary>0..1; lowered when other targets were as good.</summary>
        public readonly float Confidence;
        /// <summary>Other equally good targets; non-empty when the match is ambiguous.</summary>
        public readonly IReadOnlyList<Transform> Alternatives;

        public BoneMatch(Transform source, Transform target, BoneMatchKind kind, float confidence, IReadOnlyList<Transform> alternatives = null)
        {
            Source = source;
            Target = target;
            Kind = target != null ? kind : BoneMatchKind.None;
            Confidence = target != null ? confidence : 0f;
            Alternatives = alternatives;
        }

        public bool Matched => Target != null;
        public bool Ambiguous => Alternatives != null && Alternatives.Count > 0;
        public override string ToString() => $"{(Source != null ? Source.name : "null")} -> {(Target != null ? Target.name : "none")} ({Kind}, {Confidence:0.##}{(Ambiguous ? ", ambiguous" : "")})";
    }

    public sealed class BoneMatchOptions
    {
        /// <summary>Text the clothing adds to the avatar's bone names, stripped before matching: "Shirt_" / "_Shirt".</summary>
        public string Prefix, Suffix;
        /// <summary>Match "Hips_Shirt"-like names by the longest avatar bone name they contain.</summary>
        public bool AllowContained = true;
    }

    /// <summary>
    /// Finds the avatar bone a clothing or accessory bone belongs to: same name (ignoring case, separators and
    /// namespaces), same name once the clothing's prefix/suffix is removed, the same humanoid role ("Left arm" =
    /// "upper_arm.L"), then the longest avatar bone name it contains.
    /// </summary>
    public static class BoneMatcher
    {
        private const float AmbiguityPenalty = 0.6f;

        public static BoneMatch Match(Transform source, AvatarBoneIndex avatar, BoneMatchOptions options = null) =>
            Match(source, avatar, options, null);

        /// <summary>
        /// Matches parents before children. When several avatar bones fit, the ones below the parent's match win, which
        /// resolves duplicate names ("Head" twice); those left equally good are the match's Alternatives. A bone that would
        /// land on its parent's target ("Head.001" under "Head") stays unmatched: it is an extra bone of that parent.
        /// Results are in the order of <paramref name="sources"/>.
        /// </summary>
        public static List<BoneMatch> MatchHierarchy(IEnumerable<Transform> sources, AvatarBoneIndex avatar, BoneMatchOptions options = null)
        {
            var list = sources.Where(s => s != null).Distinct().ToList();
            var targets = new Dictionary<Transform, Transform>();
            var results = new Dictionary<Transform, BoneMatch>();
            foreach (var source in list.OrderBy(AvatarBoneIndex.Depth))
            {
                Transform parentTarget = null;
                for (var parent = source.parent; parent != null && parentTarget == null; parent = parent.parent)
                    targets.TryGetValue(parent, out parentTarget);
                var match = Match(source, avatar, options, parentTarget);
                if (match.Matched && match.Target == parentTarget) match = new BoneMatch(source, null, BoneMatchKind.None, 0f);
                results[source] = match;
                if (match.Matched) targets[source] = match.Target;
            }
            return list.Select(s => results[s]).ToList();
        }

        /// <summary>The prefix or suffix most clothing bone names add to an avatar bone name ("Hips_Shirt" = "_Shirt"),
        /// null when there is none.</summary>
        public static BoneMatchOptions DetectAffixes(IEnumerable<Transform> sources, AvatarBoneIndex avatar)
        {
            var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var suffixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var avatarNames = avatar.Bones.Select(b => b.name).Where(n => n.Length >= 3).Distinct().ToList();
            int considered = 0;
            foreach (var source in sources.Where(s => s != null).Distinct())
            {
                var name = source.name;
                if (avatar.WithName(BoneNames.Normalize(name)).Count > 0 || BoneNames.IsArmatureContainer(name)) continue;
                considered++;
                string prefix = null, suffix = null;
                int prefixLength = 0, suffixLength = 0;
                foreach (var avatarName in avatarNames)
                {
                    if (avatarName.Length >= name.Length) continue;
                    if (avatarName.Length > suffixLength && name.StartsWith(avatarName, StringComparison.OrdinalIgnoreCase))
                    {
                        suffixLength = avatarName.Length;
                        suffix = name.Substring(avatarName.Length);
                    }
                    if (avatarName.Length > prefixLength && name.EndsWith(avatarName, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixLength = avatarName.Length;
                        prefix = name.Substring(0, name.Length - avatarName.Length);
                    }
                }
                if (IsAffix(prefix)) prefixes[prefix] = prefixes.TryGetValue(prefix, out var p) ? p + 1 : 1;
                if (IsAffix(suffix)) suffixes[suffix] = suffixes.TryGetValue(suffix, out var s) ? s + 1 : 1;
            }
            var options = new BoneMatchOptions { Prefix = Common(prefixes, considered), Suffix = Common(suffixes, considered) };
            return options.Prefix == null && options.Suffix == null ? null : options;
        }

        private static BoneMatch Match(Transform source, AvatarBoneIndex avatar, BoneMatchOptions options, Transform parentTarget)
        {
            if (source == null || avatar == null) return new BoneMatch(source, null, BoneMatchKind.None, 0f);
            var name = source.name;
            var key = BoneNames.Normalize(name);
            if (key.Length == 0) return new BoneMatch(source, null, BoneMatchKind.None, 0f);

            var exact = avatar.WithName(key);
            if (exact.Count > 0) return Pick(source, exact, BoneMatchKind.ExactName, 1f, avatar, parentTarget);
            // Containers ("Armature.001") hold bones; they only match a container of the same name.
            if (BoneNames.IsArmatureContainer(name)) return new BoneMatch(source, null, BoneMatchKind.None, 0f);

            var bare = StripAffixes(name, options);
            var bareKey = bare != name ? BoneNames.Normalize(bare) : null;
            if (!string.IsNullOrEmpty(bareKey))
            {
                var affixed = avatar.WithName(bareKey);
                if (affixed.Count > 0) return Pick(source, affixed, BoneMatchKind.AffixName, 0.95f, avatar, parentTarget);
            }

            if (BoneNames.TryInferHumanoid(bare, out var id))
            {
                var human = avatar.Humanoid(id);
                if (human != null) return new BoneMatch(source, human, BoneMatchKind.Humanoid, bareKey != null ? 0.85f : 0.9f);
            }

            if (options != null && !options.AllowContained) return new BoneMatch(source, null, BoneMatchKind.None, 0f);
            var searched = string.IsNullOrEmpty(bareKey) ? key : bareKey;
            var side = BoneNames.Side(bare);
            if (BoneNames.IsEnd(bare)) return new BoneMatch(source, null, BoneMatchKind.None, 0f);
            List<Transform> best = null;
            int bestLength = 4;
            foreach (var entry in avatar.Names)
            {
                if (entry.Key.Length < bestLength || entry.Key.Length >= searched.Length || !searched.Contains(entry.Key)) continue;
                // "Hips.001" is a Blender copy of Hips, not a Hips of its own.
                if (searched.Replace(entry.Key, "").All(char.IsDigit)) continue;
                var fitting = entry.Value.Where(b => SideOf(b, avatar) == side && !BoneNames.IsArmatureContainer(b.name)).ToList();
                if (fitting.Count == 0) continue;
                if (entry.Key.Length > bestLength || best == null) { best = fitting; bestLength = entry.Key.Length; }
                else best.AddRange(fitting);
            }
            if (best == null) return new BoneMatch(source, null, BoneMatchKind.None, 0f);
            return Pick(source, best, BoneMatchKind.ContainedName, 0.5f + 0.3f * bestLength / searched.Length, avatar, parentTarget);
        }

        private static BoneMatch Pick(Transform source, IReadOnlyList<Transform> candidates, BoneMatchKind kind, float confidence,
            AvatarBoneIndex avatar, Transform parentTarget)
        {
            IReadOnlyList<Transform> pool = candidates;
            if (pool.Count > 1 && parentTarget != null)
            {
                var below = pool.Where(t => t != parentTarget && t.IsChildOf(parentTarget)).ToList();
                if (below.Count > 0) pool = below;
            }
            if (pool.Count > 1)
            {
                var human = pool.Where(t => avatar.TryGetHumanoid(t, out _)).ToList();
                if (human.Count > 0) pool = human;
            }
            if (pool.Count == 1) return new BoneMatch(source, pool[0], kind, confidence);
            var ordered = pool.OrderBy(AvatarBoneIndex.Depth).ToList();
            return new BoneMatch(source, ordered[0], kind, confidence * AmbiguityPenalty, ordered.Skip(1).ToList());
        }

        private static string StripAffixes(string name, BoneMatchOptions options)
        {
            if (options == null) return name;
            var bare = name;
            if (!string.IsNullOrEmpty(options.Prefix) && bare.Length > options.Prefix.Length && bare.StartsWith(options.Prefix, StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(options.Prefix.Length);
            if (!string.IsNullOrEmpty(options.Suffix) && bare.Length > options.Suffix.Length && bare.EndsWith(options.Suffix, StringComparison.OrdinalIgnoreCase))
                bare = bare.Substring(0, bare.Length - options.Suffix.Length);
            return bare;
        }

        private static BodySide SideOf(Transform bone, AvatarBoneIndex avatar) =>
            avatar.Sides.TryGetValue(bone, out var side) ? side : BoneNames.Side(bone.name);

        // Not a Blender copy number (".001"), an end marker ("_end") or a side ("_L").
        private static bool IsAffix(string affix)
        {
            var key = BoneNames.Normalize(affix);
            return key.Length > 0 && !key.All(char.IsDigit) && key != "end" && key != "l" && key != "r" && key != "left" && key != "right";
        }

        private static string Common(Dictionary<string, int> counts, int considered)
        {
            if (counts.Count == 0) return null;
            var best = counts.OrderByDescending(c => c.Value).ThenByDescending(c => c.Key.Length).ThenBy(c => c.Key, StringComparer.Ordinal).First();
            return best.Value >= 2 && best.Value >= considered * 0.2f ? best.Key : null;
        }
    }
}
