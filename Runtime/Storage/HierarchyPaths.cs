using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Orbiters.Toolkit
{
    /// <summary>
    /// Hierarchy paths of transforms, one contract for every tool: names joined by "/", as Unity's animation bindings write
    /// them. <see cref="Under"/> leaves the root out ("" for the root itself) and answers null for a transform outside it;
    /// <see cref="WithRoot"/> starts with the root's name; <see cref="Full"/> starts at the scene root.
    /// </summary>
    public static class HierarchyPaths
    {
        /// <summary>The path below <paramref name="root"/> (as AnimationUtility.CalculateTransformPath), "" for the root, null outside it or for null.</summary>
        public static string Under(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            if (target == root) return "";
            var names = new List<string>();
            for (var t = target; t != null; t = t.parent)
            {
                if (t == root)
                {
                    names.Reverse();
                    return string.Join("/", names);
                }
                names.Add(t.name);
            }
            return null;
        }

        /// <summary>The path below <paramref name="root"/> starting with the root's own name, null outside it.</summary>
        public static string WithRoot(Transform root, Transform target)
        {
            string under = Under(root, target);
            return under == null ? null : under.Length == 0 ? root.name : root.name + "/" + under;
        }

        /// <summary>The path from the scene root, starting with that root's name; null for null.</summary>
        public static string Full(Transform target)
        {
            if (target == null) return null;
            var builder = new StringBuilder(target.name);
            for (var t = target.parent; t != null; t = t.parent) builder.Insert(0, t.name + "/");
            return builder.ToString();
        }

        /// <summary>
        /// The transform at <paramref name="path"/> below <paramref name="root"/> (<see cref="Under"/>'s form: "" is the root),
        /// the first child of each name, as Transform.Find; null when there is none.
        /// </summary>
        public static Transform Find(Transform root, string path)
        {
            if (root == null || path == null) return null;
            return path.Length == 0 ? root : root.Find(path);
        }

        /// <summary>How many transforms from the scene root down to <paramref name="target"/>, itself included (0 for null).</summary>
        public static int Depth(Transform target)
        {
            int depth = 0;
            for (var t = target; t != null; t = t.parent) depth++;
            return depth;
        }
    }
}
