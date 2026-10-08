using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// The refitted meshes of an avatar (<see cref="OrbitersRefit"/> on each renderer), shared by every Orbiters tool: MCB,
    /// My Avatar and ReFit's own window record their refits here, restore them, and keep the generated blendshapes' weights
    /// with the body's.
    /// </summary>
    public static class RefitRecords
    {
        /// <summary>Raised with the renderer after one of its refit records was added, changed or removed.</summary>
        public static event Action<SkinnedMeshRenderer> Changed;

        /// <summary>The avatar a renderer belongs to: its VRChat avatar descriptor, else the scene root.</summary>
        public static Transform AvatarRoot(Transform transform)
        {
            if (transform == null) return null;
            var descriptor = transform.GetComponentInParent<VRCAvatarDescriptor>(true);
            return descriptor != null ? descriptor.transform : transform.root;
        }

        public static OrbitersRefit Find(SkinnedMeshRenderer renderer) => renderer != null ? renderer.GetComponent<OrbitersRefit>() : null;

        /// <summary>True while the renderer uses the mesh its refit produced.</summary>
        public static bool IsApplied(SkinnedMeshRenderer renderer)
        {
            var record = Find(renderer);
            return record != null && record.Applied;
        }

        public static List<OrbitersRefit> All(Transform avatarRoot) =>
            avatarRoot == null ? new List<OrbitersRefit>() : avatarRoot.GetComponentsInChildren<OrbitersRefit>(true).ToList();

        // ---- Renderer state -----------------------------------------------------------------------------------------

        /// <summary>The renderer's mesh, skinning, local pose and blendshape weights; transforms also by path under the root.</summary>
        public static RefitRendererState Capture(Transform avatarRoot, SkinnedMeshRenderer renderer)
        {
            var state = new RefitRendererState
            {
                mesh = renderer.sharedMesh,
                rootBoneCaptured = true,
                rootBone = renderer.rootBone,
                rootBonePath = PathUnder(avatarRoot, renderer.rootBone),
                rendererCaptured = true,
                updateWhenOffscreen = renderer.updateWhenOffscreen,
                localBounds = renderer.localBounds,
            };
            var mesh = renderer.sharedMesh;
            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    state.blendShapeNames.Add(mesh.GetBlendShapeName(i));
                    state.blendShapeWeights.Add(renderer.GetBlendShapeWeight(i));
                }
            var bones = renderer.bones ?? Array.Empty<Transform>();
            foreach (var bone in bones)
            {
                state.bones.Add(bone);
                state.bonePaths.Add(PathUnder(avatarRoot, bone));
            }
            var captured = new HashSet<Transform>();
            CaptureTransform(avatarRoot, renderer.transform, state.transforms, captured);
            CaptureTransform(avatarRoot, renderer.rootBone, state.transforms, captured);
            foreach (var bone in bones) CaptureTransform(avatarRoot, bone, state.transforms, captured);
            CaptureSiblingOrdinals(avatarRoot, state);
            return state;
        }

        /// <summary>
        /// Gives each path of the state the sibling ordinals of the scene object it refers to, so objects that share a name
        /// (two "Jacket" accessories) stay apart once only the paths remain (<see cref="Persistent"/>). The paths of objects
        /// already gone keep plain names.
        /// </summary>
        public static void CaptureSiblingOrdinals(Transform avatarRoot, RefitRendererState state)
        {
            state.rootBoneSiblingOrdinals = SiblingOrdinals(avatarRoot, state.rootBone);
            state.boneSiblingOrdinals = state.bones.Select(bone => new RefitSiblingOrdinals { ordinals = SiblingOrdinals(avatarRoot, bone) }).ToList();
            if (state.boneSiblingOrdinals.All(bone => bone.ordinals.Count == 0)) state.boneSiblingOrdinals.Clear();
            foreach (var transformState in state.transforms)
                if (transformState != null) transformState.siblingOrdinals = SiblingOrdinals(avatarRoot, transformState.transform);
        }

        /// <summary>
        /// A copy of the state that keeps Unity assets (meshes) but refers to scene objects only by path, for saving outside
        /// the scene (MCB's per-version fits).
        /// </summary>
        public static RefitRendererState Persistent(RefitRendererState state)
        {
            if (state == null) return null;
            var copy = JsonUtility.FromJson<RefitRendererState>(JsonUtility.ToJson(state));
            copy.bones.Clear();
            copy.rootBone = null;
            foreach (var transform in copy.transforms) transform.transform = null;
            return copy;
        }

        /// <summary>
        /// Puts a captured state back with Undo: the pose of every captured transform (recreating a missing one from its path),
        /// the mesh, bones, root bone, bounds and blendshape weights. Removes the refit engine's binding data from the renderer.
        /// </summary>
        public static bool Restore(Transform avatarRoot, SkinnedMeshRenderer renderer, RefitRendererState state, string undoName)
        {
            if (avatarRoot == null || renderer == null || state == null) return false;
            bool restored = false;
            var poses = SavedPoses(state);

            foreach (var transformState in state.transforms)
            {
                if (transformState == null) continue;
                var transform = transformState.transform != null
                    ? transformState.transform
                    : ResolveOrCreate(avatarRoot, transformState.path, transformState.siblingOrdinals, poses, undoName);
                if (transform == null) continue;
                Undo.RecordObject(transform, undoName);
                transform.localPosition = transformState.localPosition;
                transform.localRotation = transformState.localRotation;
                transform.localScale = transformState.localScale;
                EditorUtility.SetDirty(transform);
                restored = true;
            }

            Undo.RecordObject(renderer, undoName);
            if (state.mesh != null)
            {
                renderer.sharedMesh = state.mesh;
                restored = true;
            }
            if (state.bonePaths.Count > 0 || state.bones.Count > 0)
            {
                int count = Math.Max(state.bonePaths.Count, state.bones.Count);
                var bones = new Transform[count];
                for (int i = 0; i < count; i++)
                    bones[i] = i < state.bones.Count && state.bones[i] != null
                        ? state.bones[i]
                        : ResolveOrCreate(avatarRoot, i < state.bonePaths.Count ? state.bonePaths[i] : null, BoneSiblingOrdinals(state, i),
                            poses, undoName);
                renderer.bones = bones;
                restored = true;
            }
            if (state.rootBoneCaptured)
            {
                renderer.rootBone = state.rootBone != null
                    ? state.rootBone
                    : ResolveOrCreate(avatarRoot, state.rootBonePath, state.rootBoneSiblingOrdinals, poses, undoName);
                restored = true;
            }
            if (state.rendererCaptured)
            {
                renderer.updateWhenOffscreen = state.updateWhenOffscreen;
                renderer.localBounds = state.localBounds;
                restored = true;
            }
            if (RestoreWeights(renderer, state)) restored = true;
            RefitEngine.Current?.RemoveMetadata(renderer);
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            return restored;
        }

        /// <summary>
        /// True when <see cref="Restore"/> can bind every bone of a state saved by path: each bone exists, or can be created
        /// again from its saved pose under an existing parent, in its saved place among same-named siblings.
        /// </summary>
        public static bool CanResolve(Transform avatarRoot, RefitRendererState state)
        {
            if (avatarRoot == null || state == null || state.bonePaths.Any(path => path == null)) return false;
            var saved = SavedPoses(state);
            for (int bone = -1; bone < state.bonePaths.Count; bone++)
            {
                string path = bone < 0 ? state.rootBonePath : state.bonePaths[bone];
                var ordinals = bone < 0 ? state.rootBoneSiblingOrdinals : BoneSiblingOrdinals(state, bone);
                if (string.IsNullOrEmpty(path)) continue;
                string[] names = path.Split('/');
                var parent = avatarRoot;
                for (int segment = 0; segment < names.Length; segment++)
                {
                    int ordinal = Ordinal(ordinals, segment);
                    var child = parent != null ? Child(parent, names[segment], ordinal) : null;
                    if (child == null && (!saved.ContainsKey(PathKey(names, ordinals, segment + 1)) ||
                                          (parent != null ? Count(parent, names[segment]) : 0) != ordinal)) return false;
                    parent = child;
                }
            }
            return true;
        }

        private static bool RestoreWeights(SkinnedMeshRenderer renderer, RefitRendererState state)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null || state.blendShapeNames.Count == 0) return false;
            for (int i = 0; i < mesh.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 0f);
            int count = Math.Min(state.blendShapeNames.Count, state.blendShapeWeights.Count);
            for (int i = 0; i < count; i++)
            {
                int index = string.IsNullOrEmpty(state.blendShapeNames[i]) ? -1 : mesh.GetBlendShapeIndex(state.blendShapeNames[i]);
                if (index >= 0) renderer.SetBlendShapeWeight(index, state.blendShapeWeights[i]);
            }
            return true;
        }

        // ---- Records --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Records a refit on its renderer with Undo (the renderer already uses <paramref name="mesh"/>), then gives the
        /// generated blendshapes the body's current weights.
        /// </summary>
        public static OrbitersRefit Register(SkinnedMeshRenderer renderer, RefitRendererState original, Mesh mesh, string meshPath,
            SkinnedMeshRenderer body, IEnumerable<RefitShape> shapes, OrbitersRefit.FitKind kind, string baseKey, string baseName, string tool)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            var unique = Unique(shapes);
            var record = renderer.GetComponent<OrbitersRefit>();
            if (record == null) record = Undo.AddComponent<OrbitersRefit>(renderer.gameObject);
            else Undo.RecordObject(record, "Refit");
            record.original = original ?? new RefitRendererState();
            record.mesh = mesh;
            record.meshPath = meshPath;
            record.body = body;
            record.kind = kind;
            record.baseKey = baseKey;
            record.baseName = baseName;
            record.tool = tool;
            record.shapes = unique;
            Dirty(record);
            SyncWeights(record);
            Changed?.Invoke(renderer);
            return record;
        }

        /// <summary>Restores the renderer to how it was before its refit (when still applied) and removes the record, with Undo.</summary>
        public static void Remove(OrbitersRefit record, bool restore = true)
        {
            if (record == null) return;
            var renderer = record.GetComponent<SkinnedMeshRenderer>();
            if (restore && renderer != null && record.Applied)
                Restore(AvatarRoot(renderer.transform), renderer, record.original, "Restore original mesh");
            Undo.DestroyObjectImmediate(record);
            if (renderer != null) Changed?.Invoke(renderer);
        }

        /// <summary>
        /// Takes a refit back for good, as the user asked (any tool's Restore): <see cref="Remove"/>, and the custom base
        /// providers forget the fits of theirs that would bring it back later (MCB's saved fit for the applied version).
        /// </summary>
        public static void Discard(OrbitersRefit record, bool restore = true)
        {
            if (record == null) return;
            var renderer = record.GetComponent<SkinnedMeshRenderer>();
            if (renderer != null) CustomBases.ForgetFit(AvatarRoot(renderer.transform), renderer);
            Remove(record, restore);
        }

        /// <summary>Restores every refitted mesh of the avatar and removes their records (resetting to the original base).</summary>
        public static int RemoveAll(Transform avatarRoot)
        {
            int restored = 0;
            foreach (var record in All(avatarRoot))
            {
                if (record.Applied) restored++;
                Remove(record);
            }
            return restored;
        }

        /// <summary>A body blendshape and every shape generated from it on the avatar's refitted meshes (for sliders).</summary>
        public static List<string> ShapeNames(Transform avatarRoot, string sourceShape)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(sourceShape)) return names;
            names.Add(sourceShape);
            foreach (var record in All(avatarRoot))
                foreach (var shape in record.shapes)
                    if (string.Equals(shape.source, sourceShape, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(shape.generated) &&
                        !names.Any(n => string.Equals(n, shape.generated, StringComparison.OrdinalIgnoreCase)))
                        names.Add(shape.generated);
            return names;
        }

        /// <summary>Sets a body blendshape and the shapes generated from it on each renderer that has them; true when one changed.</summary>
        public static bool SetWeight(Transform avatarRoot, IEnumerable<SkinnedMeshRenderer> renderers, string sourceShape, float weight)
        {
            if (renderers == null || string.IsNullOrEmpty(sourceShape)) return false;
            var names = ShapeNames(avatarRoot, sourceShape);
            bool changed = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                var done = new HashSet<int>();
                foreach (string name in names)
                {
                    int index = renderer.sharedMesh.GetBlendShapeIndex(name);
                    if (index < 0 || !done.Add(index)) continue;
                    renderer.SetBlendShapeWeight(index, weight);
                    EditorUtility.SetDirty(renderer);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Gives each generated blendshape of the record its body shape's current weight, with Undo.</summary>
        public static void SyncWeights(OrbitersRefit record)
        {
            var renderer = record != null ? record.GetComponent<SkinnedMeshRenderer>() : null;
            var body = record != null ? record.body : null;
            if (renderer == null || renderer.sharedMesh == null || body == null || body.sharedMesh == null) return;
            bool recorded = false;
            foreach (var shape in record.shapes)
            {
                int source = string.IsNullOrEmpty(shape.source) ? -1 : body.sharedMesh.GetBlendShapeIndex(shape.source);
                int generated = string.IsNullOrEmpty(shape.generated) ? -1 : renderer.sharedMesh.GetBlendShapeIndex(shape.generated);
                if (source < 0 || generated < 0) continue;
                float weight = body.GetBlendShapeWeight(source);
                if (Mathf.Approximately(renderer.GetBlendShapeWeight(generated), weight)) continue;
                if (!recorded) { Undo.RecordObject(renderer, "Sync refit blendshapes"); recorded = true; }
                renderer.SetBlendShapeWeight(generated, weight);
            }
            if (recorded) EditorUtility.SetDirty(renderer);
        }

        /// <summary>Syncs every refitted mesh of the avatar with its body (after sliders or a version changed the body).</summary>
        public static void SyncAll(Transform avatarRoot)
        {
            foreach (var record in All(avatarRoot)) if (record.Applied) SyncWeights(record);
        }

        // ---- Helpers --------------------------------------------------------------------------------------------------

        /// <summary>Hierarchy path under the root ("" for the root itself), or null for a transform outside it.</summary>
        public static string PathUnder(Transform root, Transform transform)
        {
            if (root == null || transform == null || !transform.IsChildOf(root)) return null;
            return AnimationUtility.CalculateTransformPath(transform, root);
        }

        /// <summary>
        /// For each segment of the transform's <see cref="PathUnder"/>, its position among same-named siblings (0 for the
        /// first); empty when each is the first of its name, so paths without duplicates stay plain names.
        /// </summary>
        public static List<int> SiblingOrdinals(Transform root, Transform transform)
        {
            var ordinals = new List<int>();
            if (root == null || transform == null || !transform.IsChildOf(root)) return ordinals;
            for (var current = transform; current != root; current = current.parent)
            {
                string name = current.name;
                int ordinal = 0, index = current.GetSiblingIndex();
                for (int i = 0; i < index; i++)
                    if (current.parent.GetChild(i).name == name) ordinal++;
                ordinals.Insert(0, ordinal);
            }
            if (ordinals.All(ordinal => ordinal == 0)) ordinals.Clear();
            return ordinals;
        }

        /// <summary>The transform at a path under the root, each segment taken at its sibling ordinal (the first without one).</summary>
        public static Transform FindUnder(Transform root, string path, IReadOnlyList<int> siblingOrdinals)
        {
            if (root == null || path == null) return null;
            if (path.Length == 0) return root;
            var current = root;
            string[] names = path.Split('/');
            for (int segment = 0; segment < names.Length && current != null; segment++)
                current = Child(current, names[segment], Ordinal(siblingOrdinals, segment));
            return current;
        }

        /// <summary>A path and its sibling ordinals as one key: the plain path when each segment is the first of its name.</summary>
        public static string PathKey(string path, IReadOnlyList<int> siblingOrdinals) =>
            siblingOrdinals != null && siblingOrdinals.Any(ordinal => ordinal != 0) ? path + "|" + string.Join(",", siblingOrdinals) : path;

        private static string PathKey(string[] names, IReadOnlyList<int> siblingOrdinals, int segments) =>
            PathKey(string.Join("/", names, 0, segments),
                siblingOrdinals == null || siblingOrdinals.Count <= segments ? siblingOrdinals : siblingOrdinals.Take(segments).ToList());

        private static int Ordinal(IReadOnlyList<int> siblingOrdinals, int segment) =>
            siblingOrdinals != null && segment < siblingOrdinals.Count ? siblingOrdinals[segment] : 0;

        private static IReadOnlyList<int> BoneSiblingOrdinals(RefitRendererState state, int bone) =>
            state.boneSiblingOrdinals != null && bone < state.boneSiblingOrdinals.Count ? state.boneSiblingOrdinals[bone]?.ordinals : null;

        // The child of that name at that ordinal among same-named siblings.
        private static Transform Child(Transform parent, string name, int ordinal)
        {
            for (int i = 0, seen = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name && seen++ == ordinal) return child;
            }
            return null;
        }

        private static int Count(Transform parent, string name)
        {
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name) count++;
            return count;
        }

        // Saved poses by path and sibling ordinals, for the transforms Restore creates again.
        private static Dictionary<string, RefitTransformState> SavedPoses(RefitRendererState state)
        {
            var poses = new Dictionary<string, RefitTransformState>(StringComparer.Ordinal);
            foreach (var transformState in state.transforms)
                if (transformState != null && !string.IsNullOrEmpty(transformState.path))
                {
                    string key = PathKey(transformState.path, transformState.siblingOrdinals);
                    if (!poses.ContainsKey(key)) poses.Add(key, transformState);
                }
            return poses;
        }

        private static void CaptureTransform(Transform root, Transform transform, List<RefitTransformState> states, HashSet<Transform> captured)
        {
            if (transform == null || !captured.Add(transform)) return;
            states.Add(new RefitTransformState
            {
                transform = transform, path = PathUnder(root, transform),
                localPosition = transform.localPosition, localRotation = transform.localRotation, localScale = transform.localScale
            });
        }

        // A bone deleted since the capture (a replaced armature) is created again under its old parents, at its saved pose,
        // only where it takes its saved place among same-named siblings. Existing objects never change places.
        private static Transform ResolveOrCreate(Transform root, string path, IReadOnlyList<int> siblingOrdinals,
            Dictionary<string, RefitTransformState> states, string undoName)
        {
            if (root == null || path == null) return null;
            if (path.Length == 0) return root;
            var parent = root;
            string[] names = path.Split('/');
            for (int segment = 0; segment < names.Length; segment++)
            {
                string name = names[segment];
                int ordinal = Ordinal(siblingOrdinals, segment);
                if (name.Length == 0) return null;
                var child = Child(parent, name, ordinal);
                if (child == null)
                {
                    if (Count(parent, name) != ordinal) return null;
                    var go = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(go, undoName);
                    child = go.transform;
                    child.SetParent(parent, false);
                    if (states.TryGetValue(PathKey(names, siblingOrdinals, segment + 1), out var state))
                    {
                        child.localPosition = state.localPosition;
                        child.localRotation = state.localRotation;
                        child.localScale = state.localScale;
                    }
                }
                parent = child;
            }
            return parent;
        }

        private static List<RefitShape> Unique(IEnumerable<RefitShape> shapes)
        {
            var list = new List<RefitShape>();
            var destinations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var shape in shapes ?? Enumerable.Empty<RefitShape>())
            {
                if (string.IsNullOrEmpty(shape.source) || string.IsNullOrEmpty(shape.generated)) continue;
                if (destinations.TryGetValue(shape.generated, out string previous))
                {
                    if (previous != shape.source)
                        throw new InvalidOperationException("Two body blendshapes drive the same generated shape: " + shape.generated);
                    continue;
                }
                destinations.Add(shape.generated, shape.source);
                list.Add(shape);
            }
            return list;
        }

        private static void Dirty(Component component)
        {
            EditorUtility.SetDirty(component);
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }
    }
}
