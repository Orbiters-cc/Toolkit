using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Which project assets use which: every scene, prefab, material, animation and asset under Assets/ is asked for its
    /// direct dependencies, a slice per editor tick so the editor stays responsive. Tools use it before replacing or
    /// removing files (an imported material another avatar's prefab still uses is kept).
    /// </summary>
    public sealed class ProjectReferences
    {
        private static readonly string[] Users = { "t:Scene", "t:Prefab", "t:Material", "t:AnimationClip", "t:AnimatorController", "t:ScriptableObject", "t:Model" };
        private readonly Dictionary<string, HashSet<string>> usedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public int Scanned { get; private set; }

        /// <summary>The assets directly using <paramref name="path"/>, leaving out <paramref name="ignore"/>.</summary>
        public IReadOnlyCollection<string> UsersOf(string path, ICollection<string> ignore = null) =>
            usedBy.TryGetValue(path, out var users) ? (IReadOnlyCollection<string>)users.Where(u => ignore == null || !ignore.Contains(u)).ToList() : Array.Empty<string>();

        /// <summary>Builds the map. <paramref name="progress"/> receives 0..1. Main thread; yields between slices.</summary>
        public static async Task<ProjectReferences> ScanAsync(Action<float, string> progress = null, CancellationToken cancellation = default)
        {
            var map = new ProjectReferences();
            var assets = AssetDatabase.FindAssets(string.Join(" ", Users), new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Scenes are not found by type filters combined in one query on every Unity version: add them explicitly.
            assets = assets.Concat(AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var started = DateTime.UtcNow;
            for (int i = 0; i < assets.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (string dependency in AssetDatabase.GetDependencies(assets[i], false))
                {
                    if (string.Equals(dependency, assets[i], StringComparison.OrdinalIgnoreCase)) continue;
                    if (!map.usedBy.TryGetValue(dependency, out var users)) map.usedBy[dependency] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    users.Add(assets[i]);
                }
                map.Scanned++;
                if ((DateTime.UtcNow - started).TotalMilliseconds > 12)
                {
                    progress?.Invoke((i + 1f) / assets.Count, $"Checking what uses what… {i + 1} of {assets.Count}");
                    await Task.Yield();
                    started = DateTime.UtcNow;
                }
            }
            progress?.Invoke(1f, "Checked " + assets.Count + " assets");
            return map;
        }
    }
}
