using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// Animations that animate objects that aren't there: the avatar's own clips, masks and state audio whose paths reach
    /// nothing, both on the avatar and on the copy built as an upload builds it (objects VRCFury or Modular Avatar make or
    /// move at upload are there then), so only the truly broken ones show. Where one object of that name exists, Fix points
    /// the path at it, in the avatar's assets (one Undo step; a clip other avatars use too is copied for this one).
    /// </summary>
    public static class BrokenBindings
    {
        private const int Listed = 3;

        public static List<AvatarIssue> Find(GameObject avatar, GameObject built)
        {
            var issues = new List<AvatarIssue>();
            if (avatar == null || built == null) return issues;
            var afterBuild = new HashSet<(string, string)>();
            foreach (var asset in AnimationPaths.Broken(built).Assets)
                foreach (var change in asset.Changes) afterBuild.Add((Full(built.transform, asset.Root, change.From), change.Property ?? ""));
            if (afterBuild.Count == 0) return issues;
            // A path broken on the avatar counts once it is broken as built too: from its own root, or from the avatar's (a
            // build tool's path not found where it merges).
            bool AlsoBuilt(RepathAsset asset, PathChange change) =>
                afterBuild.Contains((Full(avatar.transform, asset.Root, change.From), change.Property ?? "")) || afterBuild.Contains((change.From, change.Property ?? ""));
            var plan = AnimationPaths.Broken(avatar).Where(AlsoBuilt);
            var traced = new HashSet<(string, string)>();
            foreach (var asset in plan.Assets.OrderByDescending(a => a.FixableCount).ThenBy(a => a.Asset.name, StringComparer.Ordinal))
            {
                foreach (var change in asset.Changes)
                {
                    traced.Add((Full(avatar.transform, asset.Root, change.From), change.Property ?? ""));
                    traced.Add((change.From, change.Property ?? ""));
                }
                issues.Add(Issue(avatar, asset));
            }
            var rest = afterBuild.Where(k => !traced.Contains(k)).Select(k => k.Item1).Distinct().ToList();
            if (rest.Count > 0)
                issues.Add(new AvatarIssue(IssueLevel.Note, rest.Count + (rest.Count == 1 ? " path animates nothing" : " paths animate nothing") + " once built",
                    "Animations the build tools made or merged point at objects the built avatar doesn't have. In VRChat those parts do nothing.",
                    string.Join(", ", rest.Take(Listed)) + (rest.Count > Listed ? "…" : "")));
            return issues;
        }

        private static string Full(Transform avatar, Transform root, string path)
        {
            if (root == null || root == avatar) return path;
            string at = AnimationUtility.CalculateTransformPath(root, avatar);
            return at.Length == 0 ? path : at + "/" + path;
        }

        private static AvatarIssue Issue(GameObject avatar, RepathAsset asset)
        {
            var paths = asset.Changes.Select(c => c.From).Distinct().ToList();
            string kind = asset.Asset is AvatarMask ? "mask" : asset.Asset is AnimationClip ? "animation" : "state";
            string title = "“" + asset.Asset.name + "” (" + kind + ") " + (asset.Asset is AvatarMask ? "lists " : "animates ") +
                           paths.Count + (paths.Count == 1 ? " object that isn't there" : " objects that aren't there");
            var fixes = asset.Changes.Where(c => c.Fixable).ToList();
            string detail = "In VRChat these parts do nothing.";
            if (asset.Locked != null) detail += " It can't be fixed here: " + asset.Locked + ".";
            else if (fixes.Count > 0)
            {
                detail += " Fix points " + (fixes.Count == 1 ? "it" : fixes.Count + " of them") + " at " + string.Join(", ", fixes.Take(Listed).Select(c => "“" + c.To + "”")) +
                          (fixes.Count > Listed ? "…" : "") + (fixes.Count < asset.Changes.Count ? "; the others need you: " + asset.Changes.First(c => !c.Fixable).Problem + "." : ".");
                if (asset.Shared) detail += " Other objects use it too (" + string.Join(", ", asset.SharedWith.Take(Listed)) + "): Fix changes a copy for this avatar.";
            }
            else detail += " " + Capital(asset.Changes[0].Problem ?? "nothing matches") + ": rename or move the objects back, or remove those curves in the Animation window.";
            string where = string.Join(", ", paths.Take(Listed)) + (paths.Count > Listed ? "…" : "");
            if (asset.Locked != null || fixes.Count == 0) return new AvatarIssue(IssueLevel.Warning, title, detail, where);
            var target = asset.Asset;
            var wanted = new HashSet<(string, string)>(fixes.Select(c => (c.From, c.Property ?? "")));
            return new AvatarIssue(IssueLevel.Warning, title, detail, where, asset.Shared ? "Fix for this avatar" : "Fix", () =>
            {
                if (avatar == null) return "The avatar is gone.";
                // Planned again: the avatar may have changed since the check.
                var plan = AnimationPaths.Broken(avatar).Where((a, c) => a.Asset == target && wanted.Contains((c.From, c.Property ?? "")));
                var applied = AnimationPaths.Apply(plan, false, "Fix animation paths of " + target.name);
                return applied.Paths == 0 ? "Nothing left to fix" : "Fixed " + applied.Paths + (applied.Paths == 1 ? " path" : " paths") + (applied.Copies > 0 ? " in a copy" : "");
            });
        }

        private static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
