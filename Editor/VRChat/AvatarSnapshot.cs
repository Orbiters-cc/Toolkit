using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// What Orbiters tools derive from an avatar again and again, worked out once per avatar root: its hierarchy (preorder
    /// transforms, parents, subtree ranges, paths), its components by type, its body, its armature and skeleton, its bone
    /// index, its VRCFury features, its PhysBones, the bones its skins use and what its animations and components use
    /// (<see cref="AvatarUsage"/>). Each fact is computed on first use and kept until a change it depends on:
    /// <list type="bullet">
    /// <item>structure (objects added, removed or moved; components added or removed);</item>
    /// <item>contents (a property of anything below the root other than a Transform: names, references, settings);</item>
    /// <item>assets (anything edited or imported in the project).</item>
    /// </list>
    /// None depends on where bones stand: posing keeps every fact.
    /// <para>
    /// Two kinds: <see cref="Of(GameObject)"/> is kept per root for the editor session and kept up to date by the
    /// <see cref="EditorChangeBus"/>, for code reading a scene avatar between edits (inspectors, estimates, checks, previews);
    /// a change the editor is not told of (no Undo) shows at the next one it is told of, except objects added or removed,
    /// which are always noticed. <see cref="Take"/> is made for one operation and never updated: for code that runs inside a
    /// build or changes the avatar itself, where changes are not reported until later. Main thread only.
    /// </para>
    /// </summary>
    public sealed class AvatarSnapshot
    {
        [Flags]
        private enum Depends { Structure = 1, Contents = 2, Assets = 4 }

        private sealed class Facet
        {
            public object Value;
            public bool Made;
            public int Structure = -1, Contents = -1, Assets = -1;
        }

        private static readonly Dictionary<int, AvatarSnapshot> Kept = new Dictionary<int, AvatarSnapshot>();
        private static bool listening;

        private readonly Dictionary<object, Facet> facets = new Dictionary<object, Facet>();
        private int structure, contents, assets, hierarchyCount;

        /// <summary>The avatar's root object.</summary>
        public GameObject Root { get; }

        /// <summary>The root's transform.</summary>
        public Transform Transform { get; }

        /// <summary>True for <see cref="Of(GameObject)"/>'s snapshots, kept up to date; false for <see cref="Take"/>'s.</summary>
        public bool Live { get; }

        private AvatarSnapshot(GameObject root, bool live)
        {
            Root = root;
            Transform = root.transform;
            Live = live;
            hierarchyCount = Transform.hierarchyCount;
        }

        /// <summary>The kept snapshot of <paramref name="root"/>, up to date with the changes the editor reported. Null for null.</summary>
        public static AvatarSnapshot Of(GameObject root)
        {
            if (root == null) return null;
            if (!listening)
            {
                listening = true;
                EditorChangeBus.Changed += Changed;
            }
            int id = root.GetInstanceID();
            if (!Kept.TryGetValue(id, out var snapshot) || snapshot.Root == null) Kept[id] = snapshot = new AvatarSnapshot(root, true);
            return snapshot;
        }

        public static AvatarSnapshot Of(Transform root) => root != null ? Of(root.gameObject) : null;

        /// <summary>A snapshot for one operation (see the class): facts are computed on first use and never updated. Null for null.</summary>
        public static AvatarSnapshot Take(GameObject root) => root != null ? new AvatarSnapshot(root, false) : null;

        public static AvatarSnapshot Take(Transform root) => root != null ? Take(root.gameObject) : null;

        /// <summary>Forgets every fact of this snapshot (a live one recomputes them on next use).</summary>
        public void Invalidate()
        {
            structure++;
            contents++;
            assets++;
        }

        // ---- Hierarchy ----

        private sealed class HierarchyTable
        {
            public Transform[] Transforms;
            public int[] Parents, Ends;
            public Dictionary<Transform, int> Index;
        }

        private HierarchyTable Hierarchy => Get("hierarchy", Depends.Structure, () =>
        {
            // Unity lists children depth first, parents before children: a preorder.
            var transforms = Root.GetComponentsInChildren<Transform>(true);
            var table = new HierarchyTable
            {
                Transforms = transforms, Parents = new int[transforms.Length], Ends = new int[transforms.Length],
                Index = new Dictionary<Transform, int>(transforms.Length),
            };
            for (int i = 0; i < transforms.Length; i++) table.Index[transforms[i]] = i;
            for (int i = transforms.Length - 1; i >= 0; i--)
            {
                table.Parents[i] = i == 0 || !table.Index.TryGetValue(transforms[i].parent, out int parent) ? -1 : parent;
                if (table.Ends[i] == 0) table.Ends[i] = i + 1;
                if (table.Parents[i] >= 0) table.Ends[table.Parents[i]] = Math.Max(table.Ends[table.Parents[i]], table.Ends[i]);
            }
            return table;
        });

        /// <summary>Every transform below the root, inactive ones included, root first, each parent before its children.</summary>
        public IReadOnlyList<Transform> Transforms => Hierarchy.Transforms;

        /// <summary>A transform's place in <see cref="Transforms"/>, or -1 when it is not below the root.</summary>
        public int IndexOf(Transform transform) => transform != null && Hierarchy.Index.TryGetValue(transform, out int index) ? index : -1;

        /// <summary>The place of the parent of the transform at <paramref name="index"/> in <see cref="Transforms"/>; -1 for the root.</summary>
        public int ParentOf(int index) => Hierarchy.Parents[index];

        /// <summary>
        /// Whether <paramref name="transform"/> is <paramref name="ancestor"/> or below it, both below the root (a range
        /// test, no walk up the hierarchy).
        /// </summary>
        public bool IsAtOrBelow(Transform transform, Transform ancestor)
        {
            var table = Hierarchy;
            if (transform == null || ancestor == null || !table.Index.TryGetValue(transform, out int t) || !table.Index.TryGetValue(ancestor, out int a)) return false;
            return t >= a && t < table.Ends[a];
        }

        /// <summary>The path of a transform below the root, as animations name it ("" for the root); null when it is not below.</summary>
        public string PathOf(Transform transform)
        {
            int index = IndexOf(transform);
            if (index < 0) return null;
            var paths = Get("paths", Depends.Structure | Depends.Contents, () => new string[Hierarchy.Transforms.Length]);
            return paths[index] ??= AnimationUtility.CalculateTransformPath(transform, Transform);
        }

        // ---- Components ----

        /// <summary>Every component of type <typeparamref name="T"/> (an interface too) on the root and below, inactive ones included.</summary>
        public IReadOnlyList<T> All<T>() where T : class => Get(typeof(T), Depends.Structure, () => Root.GetComponentsInChildren<T>(true));

        /// <summary>Every skinned mesh renderer below the root.</summary>
        public IReadOnlyList<SkinnedMeshRenderer> Skins => All<SkinnedMeshRenderer>();

        /// <summary>Every PhysBone below the root.</summary>
        public IReadOnlyList<VRCPhysBoneBase> PhysBones => All<VRCPhysBoneBase>();

        // ---- Body and bones ----

        /// <summary>
        /// The armature found from the avatar's humanoid hips alone (<see cref="AvatarSkeleton.Bones"/> without a body):
        /// what picks the body. Empty without a humanoid Animator.
        /// </summary>
        // Assets too: the humanoid's hips come through its Avatar asset (validity and mapping change when it is imported again).
        public IReadOnlyCollection<Transform> Armature => Get("armature", Depends.Structure | Depends.Contents | Depends.Assets, () => AvatarSkeleton.Bones(Transform));

        /// <summary>The avatar's main body mesh (see <see cref="Attachments.AttachmentPlanner.Body(AvatarSnapshot, Transform)"/>).</summary>
        public SkinnedMeshRenderer Body => Get("body", Depends.Structure | Depends.Contents | Depends.Assets, () => FindBody(null));

        /// <summary>The body with every mesh at or below <paramref name="exclude"/> left out.</summary>
        public SkinnedMeshRenderer BodyExcluding(Transform exclude)
        {
            var body = Body;
            // The best mesh stays the best when others are left out.
            if (exclude == null || body == null || !body.transform.IsChildOf(exclude)) return body;
            return FindBody(exclude);
        }

        private SkinnedMeshRenderer FindBody(Transform exclude)
        {
            // Without a humanoid Animator the skeleton is unknown: any mesh that is not an attached accessory.
            var skeleton = Armature;
            SkinnedMeshRenderer best = null;
            foreach (var r in Skins)
            {
                if (r == null || r.sharedMesh == null || exclude != null && r.transform.IsChildOf(exclude)) continue;
                if (skeleton.Count > 0 ? !r.bones.Any(b => b != null && skeleton.Contains(b)) : r.GetComponentInParent<OrbitersAttachment>(true) != null) continue;
                if (best == null || Better(r, best)) best = r;
            }
            return best;
        }

        // Named "Body" first, then the most blendshapes, then the most vertices; the first found wins a tie.
        private static bool Better(SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            bool aBody = a.name.Equals("Body", StringComparison.OrdinalIgnoreCase), bBody = b.name.Equals("Body", StringComparison.OrdinalIgnoreCase);
            if (aBody != bBody) return aBody;
            int shapes = a.sharedMesh.blendShapeCount.CompareTo(b.sharedMesh.blendShapeCount);
            if (shapes != 0) return shapes > 0;
            return a.sharedMesh.vertexCount > b.sharedMesh.vertexCount;
        }

        /// <summary>The avatar's own armature (<see cref="AvatarSkeleton.Bones"/> with <see cref="Body"/>). Do not change it: copy it.</summary>
        public IReadOnlyCollection<Transform> Skeleton => SkeletonSet;

        private HashSet<Transform> SkeletonSet => Get("skeleton", Depends.Structure | Depends.Contents | Depends.Assets, () => AvatarSkeleton.Bones(Transform, Body));

        /// <summary>
        /// A new set of the avatar's armature found with <paramref name="body"/> (<see cref="AvatarSkeleton.Bones"/>): a copy of
        /// <see cref="Skeleton"/> when it is <see cref="Body"/>, for the caller to change.
        /// </summary>
        public HashSet<Transform> SkeletonWith(SkinnedMeshRenderer body) =>
            body == Body ? new HashSet<Transform>(SkeletonSet) : AvatarSkeleton.Bones(Transform, body);

        /// <summary>Whether a transform belongs to <see cref="Skeleton"/>.</summary>
        public bool InSkeleton(Transform transform) => transform != null && SkeletonSet.Contains(transform);

        /// <summary>The <see cref="Skeleton"/>'s bones by name and humanoid role.</summary>
        public AvatarBoneIndex BoneIndex => Get("bone index", Depends.Structure | Depends.Contents | Depends.Assets, () => AvatarBoneIndex.Build(Transform, SkeletonSet));

        /// <summary>Every transform the skins use as a bone (what VRChat counts as bones).</summary>
        public IReadOnlyCollection<Transform> SkinnedBones => Get("skinned bones", Depends.Structure | Depends.Contents, () =>
        {
            var bones = new HashSet<Transform>();
            foreach (var skin in Skins)
                if (skin != null)
                    foreach (var bone in skin.bones)
                        if (bone != null) bones.Add(bone);
            return bones;
        });

        // ---- Tools' setups ----

        /// <summary>Each VRCFury component below the root with its feature (<see cref="VrcFury.Features"/>).</summary>
        public IReadOnlyList<(Component component, object feature, string kind)> VrcFuryFeatures =>
            Get("vrcfury", Depends.Structure | Depends.Contents, () => VrcFury.Features(Root).ToList());

        /// <summary>What the avatar's animations and components use, as it is now (<see cref="AvatarUsage"/>).</summary>
        public AvatarUsage Usage => Get("usage", Depends.Structure | Depends.Contents | Depends.Assets, () => new AvatarUsage(Root));

        // ---- Keeping facts ----

        private T Get<T>(object key, Depends depends, Func<T> make)
        {
            if (Live) Refresh();
            if (!facets.TryGetValue(key, out var facet)) facets[key] = facet = new Facet();
            if (facet.Made && Current(facet.Structure, structure, depends & Depends.Structure) && Current(facet.Contents, contents, depends & Depends.Contents) &&
                Current(facet.Assets, assets, depends & Depends.Assets))
                return (T)facet.Value;
            var value = make();
            facet.Value = value;
            facet.Made = true;
            facet.Structure = structure;
            facet.Contents = contents;
            facet.Assets = assets;
            return value;
        }

        private static bool Current(int at, int now, Depends depends) => depends == 0 || at == now;

        // Changes reported earlier in this frame count; objects added or removed without telling the editor too.
        private void Refresh()
        {
            EditorChangeBus.Flush();
            if (Transform == null) return;
            int count = Transform.hierarchyCount;
            if (count != hierarchyCount)
            {
                hierarchyCount = count;
                structure++;
            }
        }

        private static void Changed(EditorChanges changes)
        {
            List<int> gone = null;
            foreach (var pair in Kept)
                if (pair.Value.Root == null) (gone ??= new List<int>()).Add(pair.Key);
            if (gone != null) foreach (int id in gone) Kept.Remove(id);
            if (Kept.Count == 0) return;

            bool everything = changes.Everything || changes.UndoRedo;
            bool assetsChanged = changes.AssetsChanged;
            foreach (var snapshot in Kept.Values)
            {
                if (everything) { snapshot.Invalidate(); continue; }
                if (assetsChanged) snapshot.assets++;
                var root = snapshot.Transform;
                if (Any(changes, changes.Structure, root, false) || Any(changes, changes.Subtrees, root, false)) snapshot.structure++;
                if (Any(changes, changes.Properties, root, true)) snapshot.contents++;
            }
        }

        // Whether one of the objects is at or below the root; with skipPose, a Transform's own change (posing) does not count.
        private static bool Any(EditorChanges changes, IReadOnlyCollection<int> ids, Transform root, bool skipPose)
        {
            int rootId = root.gameObject.GetInstanceID();
            foreach (int id in ids)
            {
                if (id == rootId) return true;
                var value = changes.Find(id);
                if (skipPose && value is Transform) continue;
                var transform = value is GameObject go ? go.transform : value is Component component ? component.transform : null;
                if (transform != null && transform.IsChildOf(root)) return true;
            }
            return false;
        }

        /// <summary>For tests: forgets every kept snapshot.</summary>
        internal static void ForgetAll() => Kept.Clear();
    }
}
