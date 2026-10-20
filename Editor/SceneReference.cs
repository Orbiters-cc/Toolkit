using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A scene object named by a tool's caller (an MCP tool, a command line): an instance ID, or a path from a scene root
    /// ("Root/Child"), optionally after its scene's name or path ("Scene:Root/Child"). Read the same whatever the editor's
    /// language; a number that is no object's ID is tried as a name, and a colon only names a scene that is open. Names
    /// may hold "/" themselves ("Shirt/Pants"): a path is matched against the names as they are. A path several objects
    /// answer to is refused, naming them, rather than one picked. In Prefab Mode the prefab's objects answer first, as the
    /// Hierarchy shows them; the scenes' objects answer when none of the prefab's does, or by their scene's name.
    /// </summary>
    public static class SceneReference
    {
        /// <summary>The object, or null when none answers. Throws when several do.</summary>
        /// <param name="scenes">Where objects are looked for: the open scenes (and the prefab being edited) unless given.</param>
        public static GameObject Resolve(string reference, IEnumerable<Scene> scenes = null)
        {
            if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Provide target (instance ID or hierarchy path).");
            reference = reference.Trim();
            var open = (scenes ?? Loaded()).ToList();
            if (int.TryParse(reference, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id))
            {
                var found = EditorUtility.InstanceIDToObject(id);
                var gameObject = found as GameObject ?? (found as Component)?.gameObject;
                // Scenes given filter IDs too; without, any scene object's ID answers (a tool's hidden copy included).
                if (gameObject && gameObject.scene.IsValid() && (scenes == null || open.Contains(gameObject.scene))) return gameObject;
            }
            var matches = new List<GameObject>();
            int colon = reference.IndexOf(':');
            if (colon > 0)
            {
                string name = reference.Substring(0, colon);
                var named = open.Where(s => s.name == name || s.path == name).ToList();
                if (named.Count > 0) matches.AddRange(named.SelectMany(s => Find(s, reference.Substring(colon + 1))));
            }
            // No open scene by that name: the colon belongs to the object's name. The first scene listed that answers (the
            // prefab being edited, before the scenes behind it) is the one meant.
            if (matches.Count == 0)
            {
                foreach (var group in Groups(open))
                {
                    matches.AddRange(group.SelectMany(s => Find(s, reference)));
                    if (matches.Count > 0) break;
                }
            }
            matches = matches.Distinct().ToList();
            if (matches.Count > 1)
                throw new ArgumentException(matches.Count + " objects answer to '" + reference + "': give an instance ID instead (" +
                                            string.Join(", ", matches.Take(5).Select(m => m.GetInstanceID().ToString(CultureInfo.InvariantCulture) + " in " + m.scene.name)) + ").");
            return matches.FirstOrDefault();
        }

        /// <summary>The prefab stage's scene when one is open (first), then the loaded scenes.</summary>
        public static IEnumerable<Scene> Loaded()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.IsValid()) yield return stage.scene;
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (scene.isLoaded) yield return scene;
            }
        }

        // The prefab stage's scene alone, then the other scenes together.
        private static IEnumerable<List<Scene>> Groups(List<Scene> scenes)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            var stageScene = stage != null && stage.scene.IsValid() ? stage.scene : default;
            if (stageScene.IsValid() && scenes.Contains(stageScene)) yield return new List<Scene> { stageScene };
            yield return scenes.Where(s => s != stageScene).ToList();
        }

        // Every object of the scene at that path (siblings may share a name, and names may hold "/").
        private static IEnumerable<GameObject> Find(Scene scene, string path)
        {
            var found = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects()) Match(root.transform, path, found);
            return found;
        }

        private static void Match(Transform transform, string path, List<GameObject> found)
        {
            string name = transform.name;
            if (!path.StartsWith(name, StringComparison.Ordinal)) return;
            if (path.Length == name.Length)
            {
                found.Add(transform.gameObject);
                return;
            }
            if (path[name.Length] != '/') return;
            string rest = path.Substring(name.Length + 1);
            for (int i = 0; i < transform.childCount; i++) Match(transform.GetChild(i), rest, found);
        }
    }
}
