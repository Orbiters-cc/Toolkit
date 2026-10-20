using System.Collections.Generic;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Hidden objects (materials, textures, meshes) a static cache makes once and keeps while the scripts run. A script reload
    /// clears the static but not the object (hidden objects are never unloaded), so each reload would leave one more behind:
    /// those kept here are destroyed just before the reload, and the cache makes a new one when next asked.
    /// </summary>
    public static class ReloadScopedObjects
    {
        private static readonly List<Object> Kept = new List<Object>();
        private static bool listening;

        /// <summary>Keeps <paramref name="value"/> until the next script reload and returns it.</summary>
        public static T Keep<T>(T value) where T : Object
        {
            if (value == null) return value;
            if (!listening)
            {
                AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
                listening = true;
            }
            Kept.Add(value);
            return value;
        }

        /// <summary>Destroys what was kept (the caches it belongs to must make it again).</summary>
        internal static void ReleaseAll()
        {
            foreach (var value in Kept)
                if (value != null && !EditorUtility.IsPersistent(value)) Object.DestroyImmediate(value);
            Kept.Clear();
        }
    }
}
