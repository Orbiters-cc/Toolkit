using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// One value that changes when anything a check of an avatar reads changes: its objects and components (every tool's,
    /// not where its bones stand) and the assets they use, down to menus, parameters, controllers, clips, materials, meshes
    /// and textures (edited in the editor, or imported files changed on disk). It is measured again only when the editor
    /// reports a change to the avatar's objects or to one of those assets (an edit, an undo, an import, a move); otherwise
    /// the last value is kept, and nothing is walked while nothing changes. Changes made without telling the editor (no
    /// Undo, no change event, no import) show at the next change it is told of. Inspectors that mark an object changed
    /// without changing it don't count: contents are compared.
    /// </summary>
    public static class AvatarSignature
    {
        private static readonly Regex Reference = new Regex("\"instanceID\":(-?\\d+)", RegexOptions.Compiled);

        private sealed class Entry
        {
            public GameObject Root;
            public bool Touched = true, AssetsTouched = true;
            public string Value;
            public long Assets;
            // The assets the avatar's own objects point at, and every asset file under them.
            public HashSet<int> References = new HashSet<int>();
            public HashSet<string> Guids = new HashSet<string>(StringComparer.Ordinal), Paths = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        // Objects' serialized contents and the assets they point at, measured again only when their dirty count moved.
        private static readonly Dictionary<int, (int Dirty, int Hash, int[] References)> Contents = new Dictionary<int, (int, int, int[])>();
        private static readonly List<Transform> transforms = new List<Transform>();
        private static readonly List<Component> components = new List<Component>();

        static AvatarSignature()
        {
            ObjectChangeEvents.changesPublished += Changed;
            Undo.undoRedoPerformed += TouchAll;
        }

        public static string Of(GameObject root)
        {
            if (root == null) return "";
            int id = root.GetInstanceID();
            if (!Entries.TryGetValue(id, out var entry)) Entries[id] = entry = new Entry { Root = root };
            if (!entry.Touched) return entry.Value;
            entry.Touched = false;
            entry.Value = Measure(entry);
            return entry.Value;
        }

        private static string Measure(Entry entry)
        {
            unchecked
            {
                // Summed, so the order things are met in doesn't matter.
                long sum = 0;
                var references = new HashSet<int>();
                entry.Root.GetComponentsInChildren(true, transforms);
                foreach (var transform in transforms)
                {
                    sum += Mix(transform.GetInstanceID(), transform.parent != null ? transform.parent.GetInstanceID() : 0);
                    sum += Mix(transform.gameObject.GetInstanceID(), Content(transform.gameObject, null));
                    transform.GetComponents(components);
                    foreach (var component in components)
                        if (component != null && !(component is Transform)) sum += Mix(component.GetInstanceID(), Content(component, references));
                }
                transforms.Clear();
                components.Clear();
                if (entry.AssetsTouched || !references.SetEquals(entry.References))
                {
                    entry.References = references;
                    entry.AssetsTouched = false;
                    entry.Assets = MeasureAssets(entry);
                }
                return (sum + entry.Assets).ToString("x");
            }
        }

        // The assets under the avatar's references; assets point at assets only, so this never wanders into the scene.
        private static long MeasureAssets(Entry entry)
        {
            unchecked
            {
                long sum = 0;
                entry.Guids.Clear();
                entry.Paths.Clear();
                var assets = entry.References.Select(EditorUtility.InstanceIDToObject).Where(o => o != null).ToArray();
                if (assets.Length == 0) return 0;
                var imported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var asset in EditorUtility.CollectDependencies(assets))
                {
                    if (asset == null || !EditorUtility.IsPersistent(asset)) continue;
                    sum += Mix(asset.GetInstanceID(), Content(asset, null));
                    string path = AssetDatabase.GetAssetPath(asset);
                    if (string.IsNullOrEmpty(path) || !entry.Paths.Add(path)) continue;
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid)) entry.Guids.Add(guid);
                    // Files Unity imports (models, images, sounds) change on disk; its own assets change in the editor.
                    if (AssetDatabase.IsForeignAsset(asset)) imported.Add(path);
                }
                foreach (var path in imported) sum += Mix(path.GetHashCode(), AssetDatabase.GetAssetDependencyHash(path).GetHashCode());
                return sum;
            }
        }

        // An object's contents, by its serialized text; the persistent assets it points at go to `references`. Large data
        // (meshes, textures, clips, sounds) by its dirty count and its import: its text is many megabytes.
        private static int Content(Object value, HashSet<int> references)
        {
            int id = value.GetInstanceID(), dirty = EditorUtility.GetDirtyCount(value);
            if (!Contents.TryGetValue(id, out var known) || known.Dirty != dirty)
            {
                int hash = dirty;
                int[] found = Array.Empty<int>();
                if (!(value is Mesh || value is Texture || value is AnimationClip || value is AudioClip || value is Shader))
                {
                    try
                    {
                        string text = EditorJsonUtility.ToJson(value);
                        hash = text.GetHashCode();
                        var list = new List<int>();
                        foreach (Match match in Reference.Matches(text))
                            if (int.TryParse(match.Groups[1].Value, out int reference) && reference != 0 && !list.Contains(reference) &&
                                EditorUtility.InstanceIDToObject(reference) is Object target && target != null && EditorUtility.IsPersistent(target))
                                list.Add(reference);
                        found = list.ToArray();
                    }
                    catch (Exception) { hash = dirty; }
                }
                Contents[id] = known = (dirty, hash, found);
            }
            if (references != null) references.UnionWith(known.References);
            return known.Hash;
        }

        // ---- What the editor reports ----

        private static void Changed(ref ObjectChangeEventStream stream)
        {
            if (Entries.Count == 0) return;
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties); Touch(properties.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure); Touch(structure.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy); Touch(hierarchy.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        Touch(parent.instanceId); Touch(parent.previousParentInstanceId); Touch(parent.newParentInstanceId); break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created); Touch(created.instanceId); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed); Touch(destroyed.instanceId); Touch(destroyed.parentInstanceId); break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var order); Touch(order.instanceId); break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset); TouchAsset(asset.guid.ToString(), null); break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var made); TouchAsset(made.guid.ToString(), null); break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var gone); TouchAsset(gone.guid.ToString(), null); break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (int id in prefabs.instanceIds) Touch(id);
                        break;
                    // A scene opened or closed, and changes added in later Unity versions.
                    default: TouchAll(); break;
                }
            }
        }

        // An object changed: the avatars it belongs to (destroyed avatars are forgotten).
        private static void Touch(int id)
        {
            if (id == 0) return;
            var value = EditorUtility.InstanceIDToObject(id);
            var transform = value is GameObject go ? go.transform : value is Component component ? component.transform : null;
            List<int> forget = null;
            foreach (var pair in Entries)
            {
                var root = pair.Value.Root;
                if (root == null) (forget ??= new List<int>()).Add(pair.Key);
                else if (pair.Key == id || transform != null && transform.IsChildOf(root.transform)) pair.Value.Touched = true;
            }
            if (forget != null) foreach (int key in forget) Entries.Remove(key);
        }

        private static void TouchAsset(string guid, string path)
        {
            foreach (var entry in Entries.Values)
                if (guid != null && entry.Guids.Contains(guid) || path != null && entry.Paths.Contains(path))
                    entry.Touched = entry.AssetsTouched = true;
        }

        private static void TouchAll()
        {
            foreach (var entry in Entries.Values) entry.Touched = entry.AssetsTouched = true;
        }

        /// <summary>Asset files imported, deleted or moved (from <see cref="AvatarSignatureImports"/>).</summary>
        internal static void Imported(IEnumerable<string> paths)
        {
            if (Entries.Count == 0) return;
            foreach (string path in paths)
                if (!string.IsNullOrEmpty(path)) TouchAsset(AssetDatabase.AssetPathToGUID(path), path);
        }

        private static long Mix(long a, long b)
        {
            unchecked
            {
                ulong x = (ulong)a * 0x9E3779B97F4A7C15UL ^ (ulong)b;
                x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
                return (long)x;
            }
        }
    }

    /// <summary>Tells <see cref="AvatarSignature"/> about asset files imported, deleted or moved.</summary>
    internal sealed class AvatarSignatureImports : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) =>
            AvatarSignature.Imported(imported.Concat(deleted).Concat(moved).Concat(movedFrom));
    }
}
