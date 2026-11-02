using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// Other avatar tools on the avatar or in the project, and what they change in what Orbiters does (the compatibility
    /// matrix, MCB Test/reports/Compatibility matrix 2026-11.md). Orbiters keeps its behaviour; these notes say what the
    /// other tool does to it, or what Orbiters can't show. Read the avatar as it is in the scene, not a built copy: the
    /// tools' components leave the copy as they build.
    /// </summary>
    public static class ToolConflicts
    {
        /// <summary>A tool recognised by the namespace of its components.</summary>
        private sealed class Tool
        {
            public string Name, Namespace;
            /// <summary>It adds or changes menu items at upload, in its own build pass that My Avatar's menu preview doesn't model.</summary>
            public bool Menus;
        }

        private static readonly Tool[] Tools =
        {
            new Tool { Name = "lilycalInventory", Namespace = "jp.lilxyzw.lilycalinventory", Menus = true },
            new Tool { Name = "FaceEmo", Namespace = "Suzuryg.FaceEmo", Menus = true },
            new Tool { Name = "Avatar Menu Creator for MA", Namespace = "net.narazaka.avatarmenucreator", Menus = true },
            new Tool { Name = "AvatarPoseLibrary", Namespace = "com.hhotatea.avatar_pose_library", Menus = true },
            new Tool { Name = "EmotePrefab", Namespace = "com.github.pandrabox.emoteprefab", Menus = true },
            new Tool { Name = "Bavi Motion Patcher", Namespace = "Bavi.MotionPatcher", Menus = true },
        };

        /// <summary>
        /// The tools on the avatar whose menus are added at upload by their own build pass, which My Avatar's menu preview
        /// doesn't show (it models VRCFury's and Modular Avatar's rules): a real build shows them.
        /// </summary>
        public static IReadOnlyList<string> MenuTools(GameObject root) => On(root).Where(t => t.Menus).Select(t => t.Name).ToList();

        public static List<AvatarIssue> Find(GameObject root)
        {
            var issues = new List<AvatarIssue>();
            if (root == null) return issues;
            var menus = MenuTools(root);
            if (menus.Count > 0)
                issues.Add(new AvatarIssue(IssueLevel.Note, Join(menus) + (menus.Count == 1 ? " adds" : " add") + " menu items at upload",
                    "My Avatar's menu editor shows what VRCFury and Modular Avatar build, not these: its “Show the real build” button builds a hidden copy and shows the whole menu, read only.",
                    "Menu"));
            return issues;
        }

        private static IEnumerable<Tool> On(GameObject root)
        {
            if (root == null) yield break;
            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().Namespace is string ns) namespaces.Add(ns);
            foreach (var tool in Tools)
                if (namespaces.Any(ns => ns == tool.Namespace || ns.StartsWith(tool.Namespace + ".", StringComparison.Ordinal))) yield return tool;
        }

        private static string Join(IReadOnlyList<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }
}
