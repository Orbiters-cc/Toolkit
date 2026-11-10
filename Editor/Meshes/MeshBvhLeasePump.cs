using Orbiters.Toolkit.Meshes;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// Releases <see cref="MeshBvh"/> leases dropped without Dispose even when no tree is pinned or refitted again: a low-frequency
    /// task on the editor host, and once more before a domain reload. Both run on the main thread, so the jobs registered on a
    /// dropped lease are completed before its pins go (<see cref="MeshBvh.ReleaseDroppedLeases"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class MeshBvhLeasePump
    {
        private const double Interval = 30;

        static MeshBvhLeasePump()
        {
            OrbitersEditorHost.Every("BVH dropped leases", Interval, Release, HostWhen.Always);
            OrbitersEditorHost.BeforeReload += Release;
        }

        private static void Release() => MeshBvh.ReleaseDroppedLeases(true);
    }
}
