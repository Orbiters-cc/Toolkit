using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// The objects below a root by the path an animation binds them with, as Unity resolves it: of two siblings with the
    /// same name, the first. Taken once; later changes to the hierarchy don't show in it. Also each object's path at the
    /// time, so a later hierarchy can be compared with it.
    /// </summary>
    public sealed class PathSnapshot
    {
        private readonly Dictionary<string, Transform> byPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<Transform, string> byTransform = new Dictionary<Transform, string>();

        public Transform Root { get; }

        private PathSnapshot(Transform root) => Root = root;

        /// <summary>Every object below <paramref name="root"/> (itself included, at "") as it is now.</summary>
        public static PathSnapshot Of(Transform root)
        {
            var snapshot = new PathSnapshot(root);
            if (root == null) return snapshot;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                snapshot.Add(t == root ? "" : AnimationUtility.CalculateTransformPath(t, root), t);
            return snapshot;
        }

        /// <summary>
        /// The hierarchy as it was before <paramref name="moves"/>: each moved object (renamed, or put under another parent)
        /// at its former path from <paramref name="root"/>, the objects below it with it, every other object where it is.
        /// </summary>
        public static PathSnapshot Before(Transform root, IReadOnlyList<(Transform Target, string FormerPath)> moves)
        {
            if (moves == null || moves.Count == 0) return Of(root);
            var snapshot = new PathSnapshot(root);
            if (root == null) return snapshot;
            var former = new Dictionary<Transform, string>();
            foreach (var (target, path) in moves)
                if (target != null && path != null && target != root && !former.ContainsKey(target)) former.Add(target, path);
            var old = new Dictionary<Transform, string> { [root] = "" };
            // Parents come before their children in this order.
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) { snapshot.Add("", t); continue; }
                if (!former.TryGetValue(t, out var path))
                {
                    string parent = t.parent != null && old.TryGetValue(t.parent, out var p) ? p : null;
                    if (parent == null) continue;
                    path = parent.Length == 0 ? t.name : parent + "/" + t.name;
                }
                old[t] = path;
                snapshot.Add(path, t);
            }
            return snapshot;
        }

        private void Add(string path, Transform t)
        {
            // Unity itself resolves duplicate sibling paths to the first transform.
            if (!byPath.ContainsKey(path)) byPath.Add(path, t);
            if (!byTransform.ContainsKey(t)) byTransform.Add(t, path);
        }

        public bool TryGet(string path, out Transform target) => byPath.TryGetValue(path ?? "", out target);

        public bool Contains(string path) => byPath.ContainsKey(path ?? "");

        /// <summary>The object's path when the snapshot was taken, or null when it wasn't there.</summary>
        public string PathOf(Transform t) => t != null && byTransform.TryGetValue(t, out var path) ? path : null;

        public IEnumerable<KeyValuePair<Transform, string>> Paths => byTransform;

        public int Count => byTransform.Count;

        /// <summary>
        /// A former path resolves to <paramref name="target"/> (and the paths below it to its children), unless something
        /// else lives there now. <paramref name="formerPath"/> starts at this snapshot's root.
        /// </summary>
        public void Alias(Transform target, string formerPath)
        {
            if (target == null || formerPath == null) return;
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                string path = t == target ? formerPath : formerPath + "/" + AnimationUtility.CalculateTransformPath(t, target);
                if (!byPath.ContainsKey(path)) byPath.Add(path, t);
            }
        }

        /// <summary>
        /// The objects still below the root whose path differs from the snapshot's (renamed, or put elsewhere, or below one
        /// that was), each with its path then. Objects added or deleted since are not moves.
        /// </summary>
        public List<(Transform Target, string FormerPath)> Moves()
        {
            var moves = new List<(Transform, string)>();
            if (Root == null) return moves;
            foreach (var t in Root.GetComponentsInChildren<Transform>(true))
                if (t != Root && byTransform.TryGetValue(t, out var path) && path != AnimationUtility.CalculateTransformPath(t, Root)) moves.Add((t, path));
            return moves;
        }
    }
}
