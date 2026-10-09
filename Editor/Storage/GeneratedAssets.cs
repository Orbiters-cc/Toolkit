using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Storage
{
    /// <summary>
    /// Files a tool generated for objects of a scene (meshes fitted to an avatar, repaired materials, copies of dropped
    /// models): once nothing uses them any more (the accessory removed or undone, a fit cancelled, a choice dismissed), they
    /// go. Tracked files are checked at a quiet moment against every object loaded in the editor and every asset of the
    /// project (<see cref="ProjectReferences"/>). What goes is kept aside in Library for the editor session and comes back
    /// as soon as an Undo or Redo brings back an object using it; a new session starts without it.
    /// </summary>
    [InitializeOnLoad]
    public static class GeneratedAssets
    {
        private const string StartedKey = "Orbiters.GeneratedAssets.Started", TrashKey = "Orbiters.GeneratedAssets.Trash";
        // Seconds without a new change before a sweep: an Undo burst or a save sweeps once.
        private const double Quiet = 2;
        private static string Root => Path.GetDirectoryName(Application.dataPath);
        private static string ListFile => Path.Combine(Root, "Library", "Orbiters", "GeneratedAssets.json");
        private static string TrashFolder => Path.Combine(Root, "Library", "Orbiters", "GeneratedTrash");

        [Serializable] private sealed class Tracked { public List<string> guids = new List<string>(); }
        [Serializable] private sealed class Item { public string guid, path; public List<int> ids = new List<int>(); }
        [Serializable] private sealed class Trash { public List<Item> items = new List<Item>(); }

        private static bool scheduled, sweeping, again;
        private static double due;

        static GeneratedAssets()
        {
            Undo.undoRedoPerformed += UndoRedo;
            EditorSceneManager.sceneSaved += _ => SweepSoon();
            if (SessionState.GetBool(StartedKey, false)) return;
            // A new editor session: no Undo can bring back what an earlier one put aside.
            SessionState.SetBool(StartedKey, true);
            Write(new Trash());
            try { if (Directory.Exists(TrashFolder)) Directory.Delete(TrashFolder, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            SweepSoon();
        }

        /// <summary>
        /// Remembers generated assets (paths under Assets/) so they go once nothing uses them. A tracked folder goes once it
        /// holds no file any more.
        /// </summary>
        public static void Track(IEnumerable<string> paths)
        {
            var list = ReadList();
            int count = list.guids.Count;
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid) && !list.guids.Contains(guid)) list.guids.Add(guid);
            }
            if (list.guids.Count != count) WriteList(list);
        }

        public static void Track(params string[] paths) => Track((IEnumerable<string>)paths);

        public static bool IsTracked(string path) => ReadList().guids.Contains(AssetDatabase.AssetPathToGUID(path ?? ""));

        /// <summary>Sweeps once the editor has been quiet for a moment (after a removal, a cancel, an Undo, a save).</summary>
        public static void SweepSoon()
        {
            due = EditorApplication.timeSinceStartup + Quiet;
            if (scheduled) return;
            scheduled = true;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < due || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorApplication.update -= Tick;
            scheduled = false;
            _ = SweepAsync();
        }

        /// <summary>Puts aside the tracked assets nothing uses; returns their paths.</summary>
        internal static async Task<List<string>> SweepAsync()
        {
            var removed = new List<string>();
            if (sweeping) { again = true; return removed; }
            sweeping = true;
            try
            {
                var list = ReadList();
                // Files gone by other means are forgotten.
                if (list.guids.RemoveAll(g => !Exists(AssetDatabase.GUIDToAssetPath(g))) > 0) WriteList(list);
                var candidates = list.guids.Select(AssetDatabase.GUIDToAssetPath).Where(p => File.Exists(Path.Combine(Root, p))).ToList();
                candidates.RemoveAll(Rendered().Contains);
                if (candidates.Count == 0) { PutAside(removed); return removed; }
                var references = await ProjectReferences.ScanAsync();
                // The scan spans editor frames: what is loaded now decides.
                var loaded = Loaded();
                var removable = new HashSet<string>(candidates.Where(p => !loaded.Contains(p) && File.Exists(Path.Combine(Root, p))), StringComparer.OrdinalIgnoreCase);
                // Files only used by other files going go together; anything used from outside that set stays.
                for (bool changed = true; changed;)
                {
                    changed = false;
                    foreach (string path in removable.ToList())
                        if (references.UsersOf(path, removable).Count > 0) { removable.Remove(path); changed = true; }
                }
                removed.AddRange(removable);
                PutAside(removed);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { Debug.LogWarning("[Orbiters] Unused generated files were kept: " + ex.Message); }
            finally
            {
                sweeping = false;
                if (again) { again = false; SweepSoon(); }
            }
            return removed;
        }

        private static bool Exists(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            (File.Exists(Path.Combine(Root, path)) || Directory.Exists(Path.Combine(Root, path)));

        // Quick first look: meshes and materials of the loaded renderers, with what those generated files use.
        private static HashSet<string> Rendered()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(Object asset) { string path = asset ? AssetDatabase.GetAssetPath(asset) : null; if (!string.IsNullOrEmpty(path)) used.Add(path); }
            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>().Where(r => !EditorUtility.IsPersistent(r)))
            {
                foreach (var material in renderer.sharedMaterials) Add(material);
                if (renderer is SkinnedMeshRenderer skin) Add(skin.sharedMesh);
            }
            foreach (var filter in Resources.FindObjectsOfTypeAll<MeshFilter>().Where(f => !EditorUtility.IsPersistent(f))) Add(filter.sharedMesh);
            var tracked = new HashSet<string>(ReadList().guids.Select(AssetDatabase.GUIDToAssetPath), StringComparer.OrdinalIgnoreCase);
            foreach (string path in used.Where(tracked.Contains).ToList()) used.UnionWith(AssetDatabase.GetDependencies(path, true));
            return used;
        }

        // Every asset the objects loaded in the editor use right now (open and preview scenes, saved or not).
        private static HashSet<string> Loaded()
        {
            var roots = Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go && go.transform.parent == null && !EditorUtility.IsPersistent(go)).Cast<Object>().ToArray();
            return new HashSet<string>(EditorUtility.CollectDependencies(roots).Where(o => o && EditorUtility.IsPersistent(o))
                .Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
        }

        // Each file and its .meta are copied into Library first: Undo may bring back an object using them.
        private static void PutAside(List<string> paths)
        {
            var trash = ReadTrash();
            var deleted = new List<string>();
            foreach (string path in paths)
            {
                string guid = AssetDatabase.AssetPathToGUID(path), full = Path.Combine(Root, path), folder = Path.Combine(TrashFolder, guid);
                try
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(full, Path.Combine(folder, "asset"), true);
                    if (File.Exists(full + ".meta")) File.Copy(full + ".meta", Path.Combine(folder, "meta"), true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                trash.items.RemoveAll(i => i.guid == guid);
                trash.items.Add(new Item { guid = guid, path = path, ids = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o).Select(o => o.GetInstanceID()).ToList() });
                deleted.Add(path);
            }
            var failed = new List<string>();
            if (deleted.Count > 0) AssetDatabase.DeleteAssets(deleted.ToArray(), failed);
            trash.items.RemoveAll(i => failed.Contains(i.path));
            Write(trash);
            var list = ReadList();
            list.guids.RemoveAll(g => trash.items.Any(i => i.guid == g));
            RemoveEmptyFolders(list);
            WriteList(list);
        }

        // Tracked folders (a drop's copies) go once only folders are left in them.
        private static void RemoveEmptyFolders(Tracked list)
        {
            foreach (string guid in list.guids.ToList())
            {
                string folder = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(folder) || !folder.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (Directory.EnumerateFiles(Path.Combine(Root, folder), "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))) continue;
                if (AssetDatabase.DeleteAsset(folder)) list.guids.Remove(guid);
            }
        }

        // An Undo or Redo brought back an object using a file put aside: the file comes back (with the same GUID, so the
        // object finds it again), with the files put aside it uses itself.
        private static void UndoRedo()
        {
            SweepSoon();
            var trash = ReadTrash();
            if (trash.items.Count == 0) return;
            var ids = new HashSet<int>(trash.items.SelectMany(i => i.ids));
            var wanted = new HashSet<int>(References(ids));
            if (wanted.Count == 0) return;
            var restore = trash.items.Where(i => i.ids.Any(wanted.Contains)).ToList();
            for (int i = 0; i < restore.Count; i++)
            {
                string text = ReadText(Path.Combine(TrashFolder, restore[i].guid, "asset"));
                if (text != null) restore.AddRange(trash.items.Where(other => !restore.Contains(other) && text.Contains(other.guid)));
            }
            Restore(restore);
        }

        internal static void Restore(List<string> paths)
        {
            var set = new HashSet<string>(paths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            Restore(ReadTrash().items.Where(i => set.Contains(i.path)).ToList());
        }

        private static void Restore(List<Item> items)
        {
            if (items.Count == 0) return;
            var trash = ReadTrash();
            var list = ReadList();
            foreach (var item in items)
            {
                string folder = Path.Combine(TrashFolder, item.guid), full = Path.Combine(Root, item.path);
                try
                {
                    if (File.Exists(full)) { Debug.LogWarning($"[Orbiters] {item.path} could not come back: another file is there now."); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    if (File.Exists(Path.Combine(folder, "meta"))) File.Copy(Path.Combine(folder, "meta"), full + ".meta", false);
                    File.Copy(Path.Combine(folder, "asset"), full, false);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Debug.LogWarning($"[Orbiters] {item.path} could not come back: {ex.Message}"); continue; }
                AssetDatabase.ImportAsset(item.path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                trash.items.RemoveAll(i => i.guid == item.guid);
                if (!list.guids.Contains(item.guid)) list.guids.Add(item.guid);
                try { Directory.Delete(folder, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            Write(trash);
            WriteList(list);
        }

        // Instance IDs among those of files put aside that a loaded renderer, mesh filter or Orbiters component still points
        // to (the reference is kept by Unity while the file is missing).
        private static IEnumerable<int> References(HashSet<int> ids)
        {
            var found = new HashSet<int>();
            var components = Resources.FindObjectsOfTypeAll<Renderer>().Cast<Component>().Concat(Resources.FindObjectsOfTypeAll<MeshFilter>())
                .Concat(Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(b => b && (b.GetType().Namespace ?? "").StartsWith("Orbiters", StringComparison.Ordinal)));
            foreach (var component in components.Where(c => c && !EditorUtility.IsPersistent(c)))
            {
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && ids.Contains(property.objectReferenceInstanceIDValue))
                            found.Add(property.objectReferenceInstanceIDValue);
                }
            }
            return found;
        }

        private static string ReadText(string file)
        {
            try { return new FileInfo(file).Length <= 16 * 1024 * 1024 ? File.ReadAllText(file) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private static Tracked ReadList()
        {
            try { return File.Exists(ListFile) ? JsonUtility.FromJson<Tracked>(File.ReadAllText(ListFile)) ?? new Tracked() : new Tracked(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return new Tracked(); }
        }

        private static void WriteList(Tracked list)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ListFile)); File.WriteAllText(ListFile, JsonUtility.ToJson(list)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Debug.LogWarning("[Orbiters] Could not remember generated files: " + ex.Message); }
        }

        private static Trash ReadTrash()
        {
            string json = SessionState.GetString(TrashKey, null);
            return string.IsNullOrEmpty(json) ? new Trash() : JsonUtility.FromJson<Trash>(json) ?? new Trash();
        }

        private static void Write(Trash trash)
        {
            if (trash.items.Count == 0) SessionState.EraseString(TrashKey);
            else SessionState.SetString(TrashKey, JsonUtility.ToJson(trash));
        }
    }
}
