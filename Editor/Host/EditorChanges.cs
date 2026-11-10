using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What the editor reported changed since the last <see cref="EditorChangeBus.Changed"/>: Unity's change streams
    /// (object changes, hierarchy changes, Undo and Redo, imports, project changes) decoded once and gathered into one value
    /// per editor frame. Objects are kept by instance id; <see cref="Find"/> resolves each id once for every listener.
    /// Changes made without telling the editor (no Undo, no change event, no import) are not in it.
    /// </summary>
    public sealed class EditorChanges
    {
        private readonly HashSet<int> properties = new HashSet<int>(), structure = new HashSet<int>(), subtrees = new HashSet<int>(), destroyed = new HashSet<int>(),
            rewritten = new HashSet<int>();
        private readonly HashSet<string> assets = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> imported = new List<string>(), deleted = new List<string>(), moved = new List<string>(), movedFrom = new List<string>();
        private Dictionary<int, Object> found;

        internal EditorChanges() { }

        /// <summary>Counts the values delivered, starting at 1.</summary>
        public long Revision { get; internal set; }

        /// <summary>
        /// Something changed that says no more (a scene opened, closed or changed as a whole, a kind of change newer than
        /// this code): anything may have changed.
        /// </summary>
        public bool Everything { get; private set; }

        /// <summary>An Undo or Redo ran: it may have changed anything it recorded, in the scene or in assets.</summary>
        public bool UndoRedo { get; private set; }

        /// <summary>Unity's hierarchyChanged was raised (objects created, removed, renamed, moved, shown or hidden...).</summary>
        public bool Hierarchy { get; private set; }

        /// <summary>Unity's projectChanged was raised.</summary>
        public bool Project { get; private set; }

        /// <summary>GameObjects and components whose properties changed (a Transform's too: posing changes those).</summary>
        public IReadOnlyCollection<int> Properties => properties;

        /// <summary>GameObjects that gained or lost components.</summary>
        public IReadOnlyCollection<int> Structure => structure;

        /// <summary>
        /// GameObjects whose subtree changed: created, destroyed, moved (and their old and new parents), children reordered,
        /// a prefab instance updated, or their whole subtree's structure changed.
        /// </summary>
        public IReadOnlyCollection<int> Subtrees => subtrees;

        /// <summary>
        /// The roots of subtrees anything below may have changed in (objects, components, properties): a hierarchy's
        /// structure changed as a whole, or a prefab instance was updated. Also in <see cref="Subtrees"/>.
        /// </summary>
        public IReadOnlyCollection<int> Rewritten => rewritten;

        /// <summary>The roots of the hierarchies destroyed (their parents are in <see cref="Subtrees"/>).</summary>
        public IReadOnlyCollection<int> Destroyed => destroyed;

        /// <summary>GUIDs of assets changed in the editor (created, removed or edited, as a material's colour).</summary>
        public IReadOnlyCollection<string> Assets => assets;

        /// <summary>Asset files imported (new or changed on disk), deleted, moved (their new paths) and moved from (old paths).</summary>
        public IReadOnlyList<string> Imported => imported;
        public IReadOnlyList<string> Deleted => deleted;
        public IReadOnlyList<string> Moved => moved;
        public IReadOnlyList<string> MovedFrom => movedFrom;

        /// <summary>Whether an object of a scene (or of a prefab being edited) may have changed.</summary>
        public bool ObjectsChanged => Everything || UndoRedo || properties.Count > 0 || structure.Count > 0 || subtrees.Count > 0;

        /// <summary>Whether objects were added, removed or moved, or components added or removed (not only properties).</summary>
        public bool StructureChanged => Everything || UndoRedo || structure.Count > 0 || subtrees.Count > 0;

        /// <summary>Whether an asset may have changed, in the editor or on disk.</summary>
        public bool AssetsChanged => Everything || UndoRedo || assets.Count > 0 || imported.Count > 0 || deleted.Count > 0 || moved.Count > 0;

        public bool IsEmpty => !ObjectsChanged && !AssetsChanged && !Hierarchy && !Project && movedFrom.Count == 0;

        /// <summary>Every path the imports name: imported, deleted, moved and moved from.</summary>
        public IEnumerable<string> ImportPaths()
        {
            foreach (string path in imported) yield return path;
            foreach (string path in deleted) yield return path;
            foreach (string path in moved) yield return path;
            foreach (string path in movedFrom) yield return path;
        }

        /// <summary>The object of an id, looked up once per value (null when it is gone).</summary>
        public Object Find(int id)
        {
            if (id == 0) return null;
            found ??= new Dictionary<int, Object>();
            if (!found.TryGetValue(id, out var value)) found[id] = value = EditorUtility.InstanceIDToObject(id);
            return value;
        }

        /// <summary>The transform of an id's GameObject or component, or null.</summary>
        public Transform TransformOf(int id) => Find(id) switch
        {
            GameObject go => go != null ? go.transform : null,
            Component component => component != null ? component.transform : null,
            _ => null,
        };

        /// <summary>
        /// Whether a reported object change is at or below <paramref name="root"/> (also when <see cref="Everything"/> or an
        /// Undo may have changed it). Asset changes are not looked at.
        /// </summary>
        public bool Touches(Transform root)
        {
            if (root == null) return false;
            if (Everything || UndoRedo) return true;
            return Under(properties, root) || Under(structure, root) || Under(subtrees, root);
        }

        private bool Under(HashSet<int> ids, Transform root)
        {
            int rootId = root.gameObject.GetInstanceID();
            foreach (int id in ids)
            {
                if (id == rootId) return true;
                var transform = TransformOf(id);
                if (transform != null && transform.IsChildOf(root)) return true;
            }
            return false;
        }

        // ---- Filled by the bus ----

        internal void AddProperties(int id) { if (id != 0) properties.Add(id); }
        internal void AddStructure(int id) { if (id != 0) structure.Add(id); }
        internal void AddSubtree(int id) { if (id != 0) subtrees.Add(id); }
        internal void AddDestroyed(int id) { if (id != 0) { destroyed.Add(id); subtrees.Add(id); } }
        internal void AddRewritten(int id) { if (id != 0) { rewritten.Add(id); subtrees.Add(id); } }
        internal void AddAsset(string guid) { if (!string.IsNullOrEmpty(guid)) assets.Add(guid); }
        internal void SetEverything() => Everything = true;
        internal void SetUndoRedo() => UndoRedo = true;
        internal void SetHierarchy() => Hierarchy = true;
        internal void SetProject() => Project = true;

        internal void AddImports(string[] importedPaths, string[] deletedPaths, string[] movedPaths, string[] movedFromPaths)
        {
            if (importedPaths != null) imported.AddRange(importedPaths);
            if (deletedPaths != null) deleted.AddRange(deletedPaths);
            if (movedPaths != null) moved.AddRange(movedPaths);
            if (movedFromPaths != null) movedFrom.AddRange(movedFromPaths);
        }

        internal void Decode(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties); AddProperties(properties.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure); AddStructure(structure.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy); AddRewritten(hierarchy.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        AddSubtree(parent.instanceId); AddSubtree(parent.previousParentInstanceId); AddSubtree(parent.newParentInstanceId); break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created); AddSubtree(created.instanceId); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var gone); AddDestroyed(gone.instanceId); AddSubtree(gone.parentInstanceId); break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var order); AddSubtree(order.instanceId); break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset); AddAsset(asset.guid.ToString()); break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var made); AddAsset(made.guid.ToString()); break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var removed); AddAsset(removed.guid.ToString()); break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (int id in prefabs.instanceIds) AddRewritten(id);
                        break;
                    // A scene opened, closed or changed as a whole, and changes added in later Unity versions.
                    default: SetEverything(); break;
                }
            }
        }
    }

    /// <summary>
    /// The one listener of Unity's change streams that Orbiters tools share (<see cref="OrbitersEditorHost"/>): object
    /// changes, hierarchy changes, Undo and Redo, imports and project changes are gathered as they come and handed to
    /// <see cref="Changed"/> once per editor frame as one <see cref="EditorChanges"/>. Nothing listens to Unity before the
    /// first handler is added. A reader of facts kept from earlier changes calls <see cref="Flush"/> first, so changes
    /// published earlier in the same frame are not missed.
    /// </summary>
    public static class EditorChangeBus
    {
        private static Action<EditorChanges> handlers;
        private static EditorChanges pending;
        private static bool installed, flushing;

        /// <summary>Counts the values delivered.</summary>
        public static long Revision { get; private set; }

        /// <summary>Once per editor frame with what changed, when something did. Handlers run on the main thread.</summary>
        public static event Action<EditorChanges> Changed
        {
            add { Install(); handlers += value; }
            remove => handlers -= value;
        }

        /// <summary>Whether changes wait to be delivered.</summary>
        public static bool Pending => pending != null && !pending.IsEmpty;

        /// <summary>Delivers what changed so far now (does nothing when nothing did, or while delivering).</summary>
        public static void Flush()
        {
            if (flushing || pending == null) return;
            var changes = pending;
            pending = null;
            if (changes.IsEmpty) return;
            changes.Revision = ++Revision;
            var all = handlers;
            if (all == null) return;
            flushing = true;
            try
            {
                foreach (Action<EditorChanges> handler in all.GetInvocationList())
                {
                    try { handler(changes); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            finally { flushing = false; }
        }

        private static EditorChanges Current => pending ??= new EditorChanges();

        private static void Install()
        {
            if (installed) return;
            installed = true;
            ObjectChangeEvents.changesPublished += Published;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            EditorApplication.projectChanged += ProjectChanged;
            Undo.undoRedoPerformed += UndoRedo;
            OrbitersEditorHost.EnsurePump();
        }

        private static void Published(ref ObjectChangeEventStream stream) => Current.Decode(ref stream);
        private static void HierarchyChanged() => Current.SetHierarchy();
        private static void ProjectChanged() => Current.SetProject();
        private static void UndoRedo() => Current.SetUndoRedo();

        internal static void Imported(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!installed) return;
            if (imported.Length + deleted.Length + moved.Length + movedFrom.Length == 0) return;
            Current.AddImports(imported, deleted, moved, movedFrom);
        }

        /// <summary>For tests: adds changes as if the editor had reported them.</summary>
        internal static void Report(Action<EditorChanges> fill)
        {
            Install();
            fill(Current);
        }
    }

    /// <summary>Hands asset imports, deletions and moves to the <see cref="EditorChangeBus"/>.</summary>
    internal sealed class EditorChangeBusImports : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) =>
            EditorChangeBus.Imported(imported, deleted, moved, movedFrom);
    }
}
