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
    /// reports a change to the avatar's objects or to one of those assets (an edit, an undo, an import, a move), and then
    /// only what the change touched: the objects whose properties or components changed, or the hierarchy below an object
    /// added, removed or moved (an Undo, a scene change or an unknown kind of change measures every object again). The
    /// value is a sum of each object's share, so an object measured again replaces its share. Changes made without telling
    /// the editor (no Undo, no change event, no import) show at the next change it is told of. Inspectors that mark an
    /// object changed without changing it don't count: contents are compared.
    /// </summary>
    public static class AvatarSignature
    {
        private static readonly Regex Reference = new Regex("\"instanceID\":(-?\\d+)", RegexOptions.Compiled);

        // One GameObject of the avatar: its share of the sum (its transform's place, its own contents and its components'),
        // the ids it answers for and the assets its components point at.
        private sealed class Part
        {
            public int Parent;
            public long Sum;
            public int[] Ids = Array.Empty<int>();
            public int[] References = Array.Empty<int>();
        }

        private sealed class Entry
        {
            public GameObject Root;
            // Walk: objects may have been added, removed or moved (the transforms are listed again); Dirty: GameObjects
            // whose own share is measured again.
            public bool Walk = true, AssetsTouched = true, Everything = true;
            public readonly HashSet<int> Dirty = new HashSet<int>();
            // Subtrees anything below may have changed in (a prefab update, a hierarchy rewritten): all measured again.
            public readonly List<Transform> Rewritten = new List<Transform>();
            public readonly Dictionary<int, Part> Parts = new Dictionary<int, Part>();
            // Every id a part answers for (its GameObject, transform and components), to its GameObject's id.
            public readonly Dictionary<int, int> Owners = new Dictionary<int, int>();
            public long Sum;
            public string Value;
            public long Assets;
            // The assets the avatar's own objects point at (with how many parts point at each), and every asset file under them.
            public readonly Dictionary<int, int> References = new Dictionary<int, int>();
            public bool ReferencesChanged = true;
            public HashSet<string> Guids = new HashSet<string>(StringComparer.Ordinal), Paths = new HashSet<string>(StringComparer.Ordinal);
            // Objects' serialized contents and the assets they point at, measured again only when their dirty count moved.
            public readonly Dictionary<int, Known> Contents = new Dictionary<int, Known>();
            public Dictionary<int, Known> AssetContents = new Dictionary<int, Known>();

            public bool Stale => Walk || Everything || Dirty.Count > 0 || Rewritten.Count > 0 || AssetsTouched || ReferencesChanged;
        }

        private readonly struct Known
        {
            public readonly int Dirty;
            public readonly long Hash;
            public readonly int[] References;
            public Known(int dirty, long hash, int[] references) { Dirty = dirty; Hash = hash; References = references; }
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly List<Transform> transforms = new List<Transform>();
        private static readonly List<Component> components = new List<Component>();
        private static readonly List<int> ids = new List<int>();
        private static readonly HashSet<int> found = new HashSet<int>();

        static AvatarSignature() => EditorChangeBus.Changed += Changed;

        public static string Of(GameObject root)
        {
            if (root == null) return "";
            EditorChangeBus.Flush();
            int id = root.GetInstanceID();
            if (!Entries.TryGetValue(id, out var entry)) Entries[id] = entry = new Entry { Root = root };
            if (!entry.Stale) return entry.Value;
            entry.Value = Measure(entry);
            return entry.Value;
        }

        private static string Measure(Entry entry)
        {
            unchecked
            {
                // Every GameObject of a rewritten subtree is measured again, as if it had changed itself.
                if (entry.Rewritten.Count > 0)
                {
                    foreach (var subtree in entry.Rewritten)
                        if (subtree != null && subtree.IsChildOf(entry.Root.transform))
                            foreach (var t in subtree.GetComponentsInChildren<Transform>(true)) entry.Dirty.Add(t.gameObject.GetInstanceID());
                    entry.Rewritten.Clear();
                    entry.Walk = true;
                }
                if (entry.Walk || entry.Everything) WalkAll(entry);
                else
                {
                    foreach (int go in entry.Dirty)
                    {
                        if (!entry.Parts.ContainsKey(go)) continue;
                        // Gone without the editor saying so: list the transforms again.
                        if (!(EditorUtility.InstanceIDToObject(go) is GameObject value) || value == null || !value.transform.IsChildOf(entry.Root.transform))
                        {
                            WalkAll(entry);
                            break;
                        }
                        MeasurePart(entry, value.transform, go);
                    }
                }
                entry.Dirty.Clear();
                if (entry.AssetsTouched || entry.ReferencesChanged)
                {
                    entry.AssetsTouched = entry.ReferencesChanged = false;
                    entry.Assets = MeasureAssets(entry);
                }
                return (entry.Sum + entry.Assets).ToString("x");
            }
        }

        // Lists the transforms: parts gone are dropped, new ones and moved ones measured, the others measured again only
        // when marked (all of them after an Undo or an unknown change).
        private static void WalkAll(Entry entry)
        {
            bool all = entry.Everything;
            entry.Walk = entry.Everything = false;
            found.Clear();
            entry.Root.GetComponentsInChildren(true, transforms);
            foreach (var transform in transforms)
            {
                int go = transform.gameObject.GetInstanceID();
                found.Add(go);
                int parent = transform.parent != null ? transform.parent.GetInstanceID() : 0;
                if (all || !entry.Parts.TryGetValue(go, out var part) || part.Parent != parent || entry.Dirty.Contains(go)) MeasurePart(entry, transform, go);
            }
            transforms.Clear();
            if (found.Count != entry.Parts.Count)
                foreach (int go in entry.Parts.Keys.Where(k => !found.Contains(k)).ToList()) RemovePart(entry, go);
            found.Clear();
        }

        private static void MeasurePart(Entry entry, Transform transform, int go)
        {
            unchecked
            {
                var gameObject = transform.gameObject;
                int parent = transform.parent != null ? transform.parent.GetInstanceID() : 0;
                long sum = Mix(transform.GetInstanceID(), parent) + Mix(go, Content(entry, gameObject, null));
                var references = new HashSet<int>();
                ids.Clear();
                ids.Add(go);
                ids.Add(transform.GetInstanceID());
                transform.GetComponents(components);
                foreach (var component in components)
                {
                    if (component == null || component is Transform) continue;
                    int id = component.GetInstanceID();
                    ids.Add(id);
                    sum += Mix(id, Content(entry, component, references));
                }
                components.Clear();
                var part = new Part { Parent = parent, Sum = sum, Ids = ids.ToArray(), References = references.Count > 0 ? references.ToArray() : Array.Empty<int>() };
                MeasuredParts++;
                // The new share first, then the old one out: a reference kept by the part is never counted as gone.
                entry.Parts.TryGetValue(go, out var old);
                entry.Parts[go] = part;
                entry.Sum += part.Sum;
                foreach (int id in part.Ids) entry.Owners[id] = go;
                foreach (int reference in part.References)
                {
                    entry.References.TryGetValue(reference, out int count);
                    if (count == 0) entry.ReferencesChanged = true;
                    entry.References[reference] = count + 1;
                }
                if (old != null) Forget(entry, go, old, part.Ids);
            }
        }

        private static void RemovePart(Entry entry, int go)
        {
            if (!entry.Parts.TryGetValue(go, out var part)) return;
            entry.Parts.Remove(go);
            Forget(entry, go, part, Array.Empty<int>());
        }

        // Takes a part's earlier share out of the sum with its references, and forgets its ids no longer there (a component
        // removed) with their contents.
        private static void Forget(Entry entry, int go, Part part, int[] kept)
        {
            unchecked { entry.Sum -= part.Sum; }
            foreach (int id in part.Ids)
                if (Array.IndexOf(kept, id) < 0 && entry.Owners.TryGetValue(id, out int owner) && owner == go)
                {
                    entry.Owners.Remove(id);
                    entry.Contents.Remove(id);
                }
            foreach (int reference in part.References)
            {
                if (!entry.References.TryGetValue(reference, out int count)) continue;
                if (count <= 1) { entry.References.Remove(reference); entry.ReferencesChanged = true; }
                else entry.References[reference] = count - 1;
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
                var earlier = entry.AssetContents;
                entry.AssetContents = new Dictionary<int, Known>();
                var assets = entry.References.Keys.Select(EditorUtility.InstanceIDToObject).Where(o => o != null).ToArray();
                if (assets.Length == 0) return 0;
                var imported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var asset in EditorUtility.CollectDependencies(assets))
                {
                    if (asset == null || !EditorUtility.IsPersistent(asset)) continue;
                    int id = asset.GetInstanceID();
                    // Only the assets still used are remembered.
                    if (earlier.TryGetValue(id, out var known)) entry.AssetContents[id] = known;
                    sum += Mix(id, Content(entry.AssetContents, asset, null));
                    string path = AssetDatabase.GetAssetPath(asset);
                    if (string.IsNullOrEmpty(path) || !entry.Paths.Add(path)) continue;
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid)) entry.Guids.Add(guid);
                    // Files Unity imports (models, images, sounds) change on disk; its own assets change in the editor.
                    if (AssetDatabase.IsForeignAsset(asset)) imported.Add(path);
                }
                foreach (var path in imported) sum += Mix(Hash64(path), Hash64(AssetDatabase.GetAssetDependencyHash(path).ToString()));
                return sum;
            }
        }

        private static long Content(Entry entry, Object value, HashSet<int> references) => Content(entry.Contents, value, references);

        // An object's contents, by its serialized text; the persistent assets it points at go to `references`. Large data
        // (meshes, textures, clips, sounds) by its dirty count and its import: its text is many megabytes.
        private static long Content(Dictionary<int, Known> contents, Object value, HashSet<int> references)
        {
            int id = value.GetInstanceID(), dirty = EditorUtility.GetDirtyCount(value);
            if (!contents.TryGetValue(id, out var known) || known.Dirty != dirty)
            {
                long hash = dirty;
                int[] pointed = Array.Empty<int>();
                if (!(value is Mesh || value is Texture || value is AnimationClip || value is AudioClip || value is Shader))
                {
                    try
                    {
                        string text = EditorJsonUtility.ToJson(value);
                        hash = Hash64(text);
                        var list = new List<int>();
                        foreach (Match match in Reference.Matches(text))
                            if (int.TryParse(match.Groups[1].Value, out int reference) && reference != 0 && !list.Contains(reference) &&
                                EditorUtility.InstanceIDToObject(reference) is Object target && target != null && EditorUtility.IsPersistent(target))
                                list.Add(reference);
                        pointed = list.ToArray();
                    }
                    // Some objects cannot be serialized to text (a broken script's): their dirty count stands for them.
                    catch (Exception) { hash = dirty; }
                }
                contents[id] = known = new Known(dirty, hash, pointed);
            }
            if (references != null) references.UnionWith(known.References);
            return known.Hash;
        }

        // ---- What the editor reports ----

        private static void Changed(EditorChanges changes)
        {
            if (Entries.Count == 0) return;
            // Destroyed avatars are forgotten.
            List<int> forget = null;
            foreach (var pair in Entries)
                if (pair.Value.Root == null) (forget ??= new List<int>()).Add(pair.Key);
            if (forget != null) foreach (int key in forget) Entries.Remove(key);

            if (changes.Everything || changes.UndoRedo)
            {
                foreach (var entry in Entries.Values) entry.Everything = entry.AssetsTouched = true;
                return;
            }
            foreach (int id in changes.Properties) Touch(changes, id, false);
            foreach (int id in changes.Structure) Touch(changes, id, false);
            foreach (int id in changes.Subtrees) Touch(changes, id, true);
            foreach (int id in changes.Rewritten) Rewrite(changes, id);
            foreach (string guid in changes.Assets) TouchAsset(guid, null);
            foreach (string path in changes.ImportPaths())
                if (!string.IsNullOrEmpty(path)) TouchAsset(AssetDatabase.AssetPathToGUID(path), path);
        }

        // An object changed: its part is measured again in each avatar holding it; the transforms are listed again when the
        // hierarchy changed there, or when an object not known yet (just added) changed under the avatar.
        private static void Touch(EditorChanges changes, int id, bool structure)
        {
            if (id == 0) return;
            Transform transform = null;
            bool resolved = false;
            foreach (var entry in Entries.Values)
            {
                if (entry.Owners.TryGetValue(id, out int go))
                {
                    if (structure) entry.Walk = true;
                    entry.Dirty.Add(go);
                    continue;
                }
                if (!resolved) { transform = changes.TransformOf(id); resolved = true; }
                if (transform != null && transform.IsChildOf(entry.Root.transform)) entry.Walk = true;
            }
        }

        // A subtree anything below may have changed in: the avatars holding it measure all of it again (all of the avatar
        // when the subtree holds it, or when its object is gone).
        private static void Rewrite(EditorChanges changes, int id)
        {
            if (id == 0) return;
            var transform = changes.TransformOf(id);
            foreach (var entry in Entries.Values)
            {
                if (transform == null) { if (entry.Owners.ContainsKey(id)) entry.Everything = entry.AssetsTouched = true; continue; }
                if (entry.Root.transform.IsChildOf(transform)) entry.Everything = true;
                else if (transform.IsChildOf(entry.Root.transform)) entry.Rewritten.Add(transform);
            }
        }

        /// <summary>For tests: forgets what was measured of this avatar, so the next value is measured from nothing.</summary>
        internal static void Forget(GameObject root)
        {
            if (root != null) Entries.Remove(root.GetInstanceID());
        }

        private static void TouchAsset(string guid, string path)
        {
            foreach (var entry in Entries.Values)
                if (guid != null && entry.Guids.Contains(guid) || path != null && entry.Paths.Contains(path))
                    entry.AssetsTouched = true;
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

        /// <summary>A 64-bit FNV-1a hash of the text's characters, the same in every session.</summary>
        internal static long Hash64(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char c in text)
                {
                    hash = (hash ^ (byte)c) * 1099511628211UL;
                    hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
                }
                return (long)hash;
            }
        }

        /// <summary>For tests and profiling: how many GameObjects were measured since the scripts loaded.</summary>
        internal static int MeasuredParts { get; private set; }
    }
}
