using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>Build-copy animation ownership and path mapping for attachments without VRCFury.</summary>
    [InitializeOnLoad]
    public sealed class AttachmentAnimationBuild
    {
        private const string CacheFolder = "Assets/OrbitersToolkitBuildCache";
        private static readonly List<AttachmentAnimationBuild> Builds = new List<AttachmentAnimationBuild>();
        private static readonly HashSet<AnimatorController> OwnedControllers = new HashSet<AnimatorController>();
        private static IVRCSdkAvatarBuilderApi sdkBuilder;
        private static bool sdkBuilding;
        private static bool createdCacheFolder;
        private readonly GameObject root;
        private readonly Dictionary<Transform, Original> original = new Dictionary<Transform, Original>();
        private readonly List<Graph> graphs = new List<Graph>();
        private readonly Dictionary<Transform, List<Transform>> activationCopies = new Dictionary<Transform, List<Transform>>();
        private string assetPath, meshesPath;
        private AnimatorController container;
        private AttachmentBuildMeshes meshes;
        private bool cloned;

        private sealed class Original
        {
            public Transform Parent;
            public Matrix4x4 ParentWorld;
            public Vector3 Position, LocalPosition, LocalScale;
            public Quaternion Rotation, LocalRotation;
            public bool Active;
        }

        static AttachmentAnimationBuild()
        {
            VRCSdkControlPanel.OnSdkPanelEnable += (_, __) => HookBuilder();
            EditorApplication.delayCall += () =>
            {
                HookBuilder();
                DeleteStaleCache();
            };
            EditorApplication.update += () =>
            {
                if (sdkBuilding || BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling) return;
                foreach (var build in Builds.Where(b => b.root == null).ToArray()) build.Release();
            };
        }

        private static void HookBuilder()
        {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var next) || next == sdkBuilder) return;
            if (sdkBuilder != null)
            {
                sdkBuilder.OnSdkBuildStart -= BuildStart;
                sdkBuilder.OnSdkBuildFinish -= BuildFinish;
            }
            sdkBuilder = next;
            sdkBuilder.OnSdkBuildStart += BuildStart;
            sdkBuilder.OnSdkBuildFinish += BuildFinish;
        }
        // Build data left by a build that never finished (editor closed or crashed, scripts reloaded mid-build). Not while
        // entering or in play mode: the play mode avatar may still use it.
        private static void DeleteStaleCache()
        {
            if (Builds.Count > 0 || sdkBuilding || EditorApplication.isPlayingOrWillChangePlaymode || !AssetDatabase.IsValidFolder(CacheFolder)) return;
            foreach (var guid in AssetDatabase.FindAssets("", new[] { CacheFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileName(path).StartsWith("attachment-", StringComparison.Ordinal)) AssetDatabase.DeleteAsset(path);
            }
            if (AssetDatabase.FindAssets("", new[] { CacheFolder }).Length == 0) AssetDatabase.DeleteAsset(CacheFolder);
        }

        private static void BuildStart(object sender, object avatar) { sdkBuilding = true; }
        private static void BuildFinish(object sender, string message) { sdkBuilding = false; }

        public static bool Owns(AnimatorController controller) => controller != null && OwnedControllers.Contains(controller);

        private AttachmentAnimationBuild(GameObject avatar)
        {
            root = avatar;
            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
                original[t] = new Original
                {
                    Parent = t.parent, ParentWorld = t.parent == null ? Matrix4x4.identity : t.parent.localToWorldMatrix,
                    Position = t.position, Rotation = t.rotation, LocalPosition = t.localPosition,
                    LocalRotation = t.localRotation, LocalScale = t.localScale, Active = t.gameObject.activeSelf
                };
        }

        public static AttachmentAnimationBuild Prepare(GameObject avatar)
        {
            var build = For(avatar);
            if (build.cloned) return build;
            try { build.CloneControllers(); build.cloned = true; return build; }
            catch { build.Release(); throw; }
        }

        /// <summary>
        /// Makes objects created for this build copy (meshes, materials) assets until the build is released, without
        /// taking over the avatar's animations, then writes all of the copy's build data (objects kept earlier too).
        /// The SDK saves the copy as a prefab: in-memory objects would be left out.
        /// </summary>
        public static void Keep(GameObject avatar, IEnumerable<Object> values)
        {
            var created = values.ToList();
            var build = created.Count > 0 ? For(avatar) : Builds.FirstOrDefault(b => b.root == avatar);
            if (build == null) return;
            foreach (var value in created) build.Persist(value);
            if (build.container != null) AssetDatabase.SaveAssetIfDirty(build.container);
            if (build.meshes != null) AssetDatabase.SaveAssetIfDirty(build.meshes);
        }

        private static AttachmentAnimationBuild For(GameObject avatar)
        {
            var existing = Builds.FirstOrDefault(b => b.root == avatar);
            if (existing != null) return existing;
            var build = new AttachmentAnimationBuild(avatar);
            Builds.Add(build);
            return build;
        }

        private void CloneControllers()
        {
            var byRoot = new Dictionary<Transform, Graph>();
            Func<Transform, Graph> graphFor = t =>
            {
                if (!byRoot.TryGetValue(t, out var graph))
                {
                    graph = new Graph(this, t);
                    byRoot[t] = graph;
                    graphs.Add(graph);
                }
                return graph;
            };
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                var graph = graphFor(descriptor.transform);
                descriptor.baseAnimationLayers = CloneLayers(descriptor.baseAnimationLayers, graph);
                descriptor.specialAnimationLayers = CloneLayers(descriptor.specialAnimationLayers, graph);
            }
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null)
                    animator.runtimeAnimatorController = (RuntimeAnimatorController)graphFor(animator.transform).Clone(animator.runtimeAnimatorController);
        }

        private static VRCAvatarDescriptor.CustomAnimLayer[] CloneLayers(VRCAvatarDescriptor.CustomAnimLayer[] source, Graph graph)
        {
            if (source == null) return null;
            var layers = (VRCAvatarDescriptor.CustomAnimLayer[])source.Clone();
            for (int i = 0; i < layers.Length; i++)
            {
                if (!layers[i].isDefault) layers[i].animatorController = (RuntimeAnimatorController)graph.Clone(layers[i].animatorController);
                layers[i].mask = (AvatarMask)graph.Clone(layers[i].mask);
            }
            return layers;
        }

        /// <summary>Makes an object created for this build copy (a mesh, a clip) an asset until the build is released.</summary>
        public void Keep(Object value) => Persist(value);

        /// <summary>
        /// Animations written before a tool moved these transforms keep animating them: bindings to each former path
        /// (avatar-relative, its own name included) and to its children's are rewritten to where they are now.
        /// </summary>
        public void Moved(IReadOnlyList<(Transform target, string formerPath)> moves)
        {
            if (moves == null || moves.Count == 0) return;
            foreach (var graph in graphs)
                foreach (var (target, formerPath) in moves)
                    if (target != null && !string.IsNullOrEmpty(formerPath)) graph.Alias(target, formerPath, root.transform);
            foreach (var graph in graphs) graph.Rewrite(activationCopies);
            if (container != null) AssetDatabase.SaveAssetIfDirty(container);
        }

        private void Persist(Object value)
        {
            value.hideFlags = HideFlags.HideInHierarchy;
            // Meshes in a binary file: a body with its blendshapes is hundreds of MB, several GB as text (Unity fails to write it).
            if (value is Mesh)
            {
                if (meshes == null)
                {
                    meshesPath = NewCachePath(".asset");
                    meshes = ScriptableObject.CreateInstance<AttachmentBuildMeshes>();
                    meshes.name = "Orbiters attachment build meshes";
                    AssetDatabase.CreateAsset(meshes, meshesPath);
                }
                AssetDatabase.AddObjectToAsset(value, meshes);
                return;
            }
            if (container == null)
            {
                assetPath = NewCachePath(".controller");
                container = new AnimatorController { name = "Orbiters attachment build data" };
                AssetDatabase.CreateAsset(container, assetPath);
            }
            AssetDatabase.AddObjectToAsset(value, container);
            if (value is AnimatorController controller) OwnedControllers.Add(controller);
        }

        private static string NewCachePath(string extension)
        {
            if (!AssetDatabase.IsValidFolder(CacheFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OrbitersToolkitBuildCache");
                createdCacheFolder = true;
            }
            return CacheFolder + "/attachment-" + Guid.NewGuid().ToString("N") + extension;
        }

        /// <summary>Moves each follower under its bone with an offset frame, preserving authored local animation values.</summary>
        public void Move(IReadOnlyList<FollowLink> moves)
        {
            // Capture all final poses first: reparenting one bone must not change the pose used for another.
            var desired = moves.Select(m => Matrix4x4.TRS(m.Follower.position, m.Follower.rotation, Vector3.one)).ToArray();
            var frames = new Dictionary<Transform, Transform>();
            var corrections = new Dictionary<Transform, Matrix4x4>();
            var physics = root.GetComponentsInChildren<VRCPhysBone>(true).ToDictionary(p => p,
                p => new HashSet<Transform>((p.rootTransform != null ? p.rootTransform : p.transform).GetComponentsInChildren<Transform>(true)));
            for (int i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                var before = original[move.Follower];
                if (move.Target == move.Follower || move.Target.IsChildOf(move.Follower))
                    throw new InvalidOperationException("An attachment cannot follow itself or one of its children.");
                var frame = new GameObject(UniqueName(move.Target, "Orbiters attachment frame")).transform;
                frame.SetParent(move.Target, false);
                var correction = desired[i] * Matrix4x4.TRS(before.Position, before.Rotation, Vector3.one).inverse;
                frames[move.Follower] = frame;
                corrections[move.Follower] = correction;
                SetMatrix(frame, move.Target.worldToLocalMatrix * correction * before.ParentWorld);
                move.Follower.SetParent(frame, false);
                move.Follower.localPosition = before.LocalPosition;
                move.Follower.localRotation = before.LocalRotation;
                move.Follower.localScale = before.LocalScale;
            }
            // Moving a clothing bone out of its accessory must keep the accessory's visibility toggle, including
            // animation of any former ancestor. Independent identity frames preserve the AND of those active states.
            var animatedActive = new HashSet<Transform>(graphs.SelectMany(g => g.AnimatedActiveTargets()));
            var animatedTransform = new HashSet<Transform>(graphs.SelectMany(g => g.AnimatedTransformTargets()));
            foreach (var move in moves)
            {
                var follower = move.Follower;
                var parents = new List<Transform>();
                for (var parent = original[follower].Parent; parent != null && parent != root.transform; parent = original.TryGetValue(parent, out var p) ? p.Parent : null) parents.Add(parent);
                var highestAnimated = parents.FindLastIndex(p => !follower.IsChildOf(p) && animatedTransform.Contains(p));
                if (highestAnimated >= 0)
                {
                    // Recreate just the necessary authored parent frames. Keeping their original local TRS means
                    // position/rotation/scale curves retain their exact meaning, with no curve baking or constraints.
                    var frame = frames[follower];
                    SetMatrix(frame, move.Target.worldToLocalMatrix * corrections[follower] * original[parents[highestAnimated]].ParentWorld);
                    var last = frame;
                    for (int j = highestAnimated; j >= 0; j--)
                    {
                        var parent = parents[j]; var before = original[parent];
                        var proxy = new GameObject("Orbiters animation " + parent.name).transform;
                        proxy.SetParent(last, false); proxy.localPosition = before.LocalPosition;
                        proxy.localRotation = before.LocalRotation; proxy.localScale = before.LocalScale;
                        proxy.gameObject.SetActive(before.Active);
                        AddCopy(parent, proxy); last = proxy;
                    }
                    follower.SetParent(last, false);
                }
                foreach (var parent in parents.Skip(highestAnimated + 1))
                {
                    if (follower.IsChildOf(parent) || original[parent].Active && !animatedActive.Contains(parent)) continue;
                    var proxy = new GameObject("Orbiters visibility " + parent.name).transform;
                    proxy.SetParent(follower.parent, false); follower.SetParent(proxy, false);
                    proxy.gameObject.SetActive(original[parent].Active); AddCopy(parent, proxy);
                }
                foreach (var pair in physics)
                {
                    var physicsRoot = pair.Key.rootTransform != null ? pair.Key.rootTransform : pair.Key.transform;
                    // A new child must not extend the destination bone's existing PhysBone simulation chain.
                    if (pair.Value.Contains(follower) || !frames[follower].IsChildOf(physicsRoot)) continue;
                    if (pair.Key.ignoreTransforms == null) pair.Key.ignoreTransforms = new List<Transform>();
                    pair.Key.ignoreTransforms.Add(frames[follower]);
                }
            }
            foreach (var graph in graphs) graph.Rewrite(activationCopies);
            if (container != null) AssetDatabase.SaveAssetIfDirty(container);
        }

        private void AddCopy(Transform originalTransform, Transform copy)
        {
            if (!activationCopies.TryGetValue(originalTransform, out var copies)) activationCopies[originalTransform] = copies = new List<Transform>();
            copies.Add(copy);
        }

        private static string UniqueName(Transform parent, string name)
        {
            var candidate = name;
            for (int n = 1; parent.Find(candidate) != null; n++) candidate = name + " " + n;
            return candidate;
        }

        private static void SetMatrix(Transform transform, Matrix4x4 matrix)
        {
            var x = (Vector3)matrix.GetColumn(0); var y = (Vector3)matrix.GetColumn(1); var z = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) scale.x = -scale.x;
            transform.localPosition = matrix.GetColumn(3);
            // A sheared matrix (non-uniform scale above a rotated bone) or a zero scale cannot be a transform: keep the
            // axes' lengths and main orientation, as Unity's SetParent does when keeping world pose.
            transform.localRotation = scale.y > 1e-7f && scale.z > 1e-7f ? Quaternion.LookRotation(z, y) : Quaternion.identity;
            transform.localScale = scale;
        }

        /// <summary>Release only after the upload/build copy is no longer needed. Source assets are never owned here.</summary>
        public static void Release(GameObject avatar)
        {
            foreach (var build in Builds.Where(b => b.root == avatar).ToArray()) build.Release();
        }
        private void Release()
        {
            foreach (var graph in graphs)
                foreach (var controller in graph.Copies.Values.OfType<AnimatorController>()) OwnedControllers.Remove(controller);
            if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            if (!string.IsNullOrEmpty(meshesPath)) AssetDatabase.DeleteAsset(meshesPath);
            Builds.Remove(this);
            if (createdCacheFolder && Builds.Count == 0 && AssetDatabase.IsValidFolder(CacheFolder) &&
                AssetDatabase.FindAssets("", new[] { CacheFolder }).Length == 0)
            {
                AssetDatabase.DeleteAsset(CacheFolder);
                createdCacheFolder = false;
            }
        }

        private sealed class Graph
        {
            private readonly AttachmentAnimationBuild owner;
            private readonly Transform animatorRoot;
            private readonly Dictionary<string, Transform> paths;
            private readonly Dictionary<AnimationClip, AnimationClip> overrides = new Dictionary<AnimationClip, AnimationClip>();
            internal readonly Dictionary<Object, Object> Copies = new Dictionary<Object, Object>();
            internal Graph(AttachmentAnimationBuild owner, Transform animatorRoot)
            {
                this.owner = owner; this.animatorRoot = animatorRoot;
                paths = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (var t in animatorRoot.GetComponentsInChildren<Transform>(true))
                {
                    var path = AnimationUtility.CalculateTransformPath(t, animatorRoot);
                    // Unity itself resolves duplicate sibling paths to the first transform.
                    if (!paths.ContainsKey(path)) paths.Add(path, t);
                }
            }
            internal Object Clone(Object source)
            {
                if (source == null) return null;
                if (Copies.TryGetValue(source, out var existing)) return existing;
                if (source is AnimatorOverrideController overrideController)
                {
                    // Each override graph needs its own clip copies. Flatten it so later blendshape linking operates
                    // on the clips that will actually play, rather than on a base clip hidden by an override.
                    var graph = new Graph(owner, animatorRoot);
                    owner.graphs.Add(graph);
                    RuntimeAnimatorController current = overrideController;
                    while (current is AnimatorOverrideController layer)
                    {
                        var entries = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                        layer.GetOverrides(entries);
                        foreach (var entry in entries)
                            if (entry.Value != null && !graph.overrides.ContainsKey(entry.Key)) graph.overrides.Add(entry.Key, entry.Value);
                        current = layer.runtimeAnimatorController;
                    }
                    var flattened = graph.Clone(current);
                    Copies.Add(source, flattened);
                    return flattened;
                }
                if (!(source is RuntimeAnimatorController || source is AnimatorStateMachine || source is AnimatorState ||
                      source is AnimatorTransitionBase || source is StateMachineBehaviour || source is Motion || source is AvatarMask)) return source;
                var template = source is AnimationClip originalClip && overrides.TryGetValue(originalClip, out var replacement) ? replacement : source;
                // Instantiate invokes Unity's special controller/state-machine deep-copy path, which asserts on
                // strong native references. A fresh native object plus serialized data keeps ownership explicit.
                var copy = template is ScriptableObject
                    ? ScriptableObject.CreateInstance(template.GetType())
                    : (Object)Activator.CreateInstance(template.GetType());
                EditorUtility.CopySerialized(template, copy);
                copy.name = source.name;
                Copies.Add(source, copy);
                owner.Persist(copy);
                // Remap the entire serialized graph, including behaviours, synced layers, nested trees and overrides.
                using (var originalData = new SerializedObject(template))
                using (var serialized = new SerializedObject(copy))
                {
                    // Read references from the source. Unity may instantiate parts of a controller internally; walking
                    // those newly-created back-references would clone the same graph indefinitely.
                    var property = originalData.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.name != "m_Script")
                        {
                            var value = property.objectReferenceValue;
                            var mapped = Clone(value);
                            var destination = serialized.FindProperty(property.propertyPath);
                            if (destination != null) destination.objectReferenceValue = mapped;
                        }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(copy);
                return copy;
            }

            // A former path resolves to the moved transform, unless something else lives there now.
            internal void Alias(Transform target, string formerPath, Transform avatarRoot)
            {
                string prefix = animatorRoot == avatarRoot ? "" : AnimationUtility.CalculateTransformPath(animatorRoot, avatarRoot) + "/";
                if (!formerPath.StartsWith(prefix, StringComparison.Ordinal)) return;
                string former = formerPath.Substring(prefix.Length);
                foreach (var t in target.GetComponentsInChildren<Transform>(true))
                {
                    string path = t == target ? former : former + "/" + AnimationUtility.CalculateTransformPath(t, target);
                    if (!paths.ContainsKey(path)) paths.Add(path, t);
                }
            }

            private string NewPath(string path)
            {
                if (!paths.TryGetValue(path, out var target) || target == null) return path;
                if (!(target == animatorRoot || target.IsChildOf(animatorRoot)))
                    throw new InvalidOperationException("An attachment moved an animated object outside its nested Animator. Put this accessory's animations in the avatar's playable layers before building.");
                return AnimationUtility.CalculateTransformPath(target, animatorRoot);
            }
            internal IEnumerable<Transform> AnimatedActiveTargets()
            {
                foreach (var clip in Copies.Values.OfType<AnimationClip>())
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive" && paths.TryGetValue(binding.path, out var target))
                            yield return target;
            }
            internal IEnumerable<Transform> AnimatedTransformTargets()
            {
                foreach (var clip in Copies.Values.OfType<AnimationClip>())
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Transform) && paths.TryGetValue(binding.path, out var target)) yield return target;
            }
            internal void Rewrite(Dictionary<Transform, List<Transform>> activationCopies)
            {
                foreach (var clip in Copies.Values.OfType<AnimationClip>())
                {
                    // Only the curves of moved objects change: setting curves one by one rebuilds the clip each time, and
                    // most clips (locomotion, humanoid muscles) animate nothing that moved.
                    var removed = new List<EditorCurveBinding>();
                    var added = new List<EditorCurveBinding>();
                    var addedCurves = new List<AnimationCurve>();
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        var moved = binding;
                        moved.path = NewPath(binding.path);
                        var proxies = (binding.type == typeof(Transform) || binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive") &&
                                      paths.TryGetValue(binding.path, out var target) && activationCopies.TryGetValue(target, out var copies)
                            ? copies.Where(p => p.IsChildOf(animatorRoot)).ToList() : null;
                        if (moved.path == binding.path && (proxies == null || proxies.Count == 0)) continue;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (moved.path != binding.path)
                        {
                            removed.Add(binding);
                            added.Add(moved); addedCurves.Add(curve);
                        }
                        foreach (var proxy in proxies ?? new List<Transform>())
                        {
                            var copy = binding;
                            copy.path = AnimationUtility.CalculateTransformPath(proxy, animatorRoot);
                            added.Add(copy); addedCurves.Add(curve);
                        }
                    }
                    var objects = new List<(EditorCurveBinding from, EditorCurveBinding to, ObjectReferenceKeyframe[] keys)>();
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var moved = binding;
                        moved.path = NewPath(binding.path);
                        if (moved.path != binding.path) objects.Add((binding, moved, AnimationUtility.GetObjectReferenceCurve(clip, binding)));
                    }
                    if (removed.Count == 0 && added.Count == 0 && objects.Count == 0) continue;
                    if (removed.Count > 0) AnimationUtility.SetEditorCurves(clip, removed.ToArray(), new AnimationCurve[removed.Count]);
                    if (added.Count > 0) AnimationUtility.SetEditorCurves(clip, added.ToArray(), addedCurves.ToArray());
                    foreach (var entry in objects) AnimationUtility.SetObjectReferenceCurve(clip, entry.from, null);
                    foreach (var entry in objects) AnimationUtility.SetObjectReferenceCurve(clip, entry.to, entry.keys);
                    EditorUtility.SetDirty(clip);
                }
                foreach (var mask in Copies.Values.OfType<AvatarMask>())
                {
                    var additions = new Dictionary<string, bool>(StringComparer.Ordinal);
                    for (int i = 0; i < mask.transformCount; i++)
                    {
                        var oldPath = mask.GetTransformPath(i);
                        if (paths.TryGetValue(oldPath, out var target) && activationCopies.TryGetValue(target, out var proxies))
                            foreach (var proxy in proxies.Where(p => p.IsChildOf(animatorRoot)))
                                additions[AnimationUtility.CalculateTransformPath(proxy, animatorRoot)] = mask.GetTransformActive(i);
                        mask.SetTransformPath(i, NewPath(oldPath));
                    }
                    int index = mask.transformCount; mask.transformCount += additions.Count;
                    foreach (var pair in additions) { mask.SetTransformPath(index, pair.Key); mask.SetTransformActive(index++, pair.Value); }
                    EditorUtility.SetDirty(mask);
                }
            }
        }
    }
}
