using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>Animation data an avatar plays, and where its paths start.</summary>
    public sealed class AnimationSource
    {
        /// <summary>A controller, clip, blend tree or mask.</summary>
        public Object Asset;
        /// <summary>The object its paths start from: the avatar's (playable layers), an Animator's, a build tool's merge root.</summary>
        public Transform Root;
        /// <summary>A path not found from <see cref="Root"/> is looked up from each object above it, up to the avatar (VRCFury).</summary>
        public bool WalksUp;
        /// <summary>What plays it (a descriptor, an Animator, a build tool's component): pointed at a copy made for this avatar.</summary>
        public Object Via;
        /// <summary>Set when its paths can't be followed (why): it is left alone.</summary>
        public string Unknown;
    }

    public enum PathRefKind { Curve, ObjectCurve, Mask, Behaviour }

    /// <summary>One path an animation asset binds, and where it should point.</summary>
    public sealed class PathChange
    {
        public PathRefKind Kind;
        /// <summary>The path now, and the path it should have (null: no fix), both from the asset's root.</summary>
        public string From, To;
        /// <summary>The animated property ("m_IsActive", "blendShape.Smile"), the behaviour's field, or "" for a mask entry.</summary>
        public string Property;
        /// <summary>The animated component's type (curves), or the component a behaviour's path names.</summary>
        public Type Type;
        /// <summary>What it should animate, when known.</summary>
        public Transform Target;
        /// <summary>It resolved to nothing before the change: <see cref="To"/> is a guess (the one object of that name).</summary>
        public bool WasBroken;
        /// <summary>Why it isn't fixed, or null.</summary>
        public string Problem;
        /// <summary>The component type the rewritten curve animates, when it changes (a joined mesh's renderer).</summary>
        public Type NewType;
        internal EditorCurveBinding Binding;
        internal int Index;

        public bool Fixable => To != null && Problem == null;

        public override string ToString() => From + " → " + (To ?? "?") + " (" + Property + ")" + (Problem != null ? ": " + Problem : "");
    }

    /// <summary>The changes to one asset (a clip, a mask, a behaviour holding a path).</summary>
    public sealed class RepathAsset
    {
        public Object Asset;
        public Transform Root;
        public readonly List<PathChange> Changes = new List<PathChange>();
        /// <summary>Why the asset can't be changed (in a package, read-only, inside a model file), or null.</summary>
        public string Locked;
        /// <summary>What uses it besides this avatar (other avatars and objects in the open scenes, other controllers).</summary>
        public readonly List<string> SharedWith = new List<string>();

        public bool Fixable => Locked == null && Changes.Any(c => c.Fixable);
        public int FixableCount => Locked == null ? Changes.Count(c => c.Fixable) : 0;
        public bool Shared => SharedWith.Count > 0;
    }

    /// <summary>A dry run: the assets whose paths would change, what can't be fixed, and what is shared.</summary>
    public sealed class RepathPlan
    {
        public GameObject Avatar;
        public readonly List<RepathAsset> Assets = new List<RepathAsset>();
        internal List<AnimationSource> Sources = new List<AnimationSource>();

        public IEnumerable<PathChange> Changes => Assets.SelectMany(a => a.Changes);
        /// <summary>The paths that can be fixed.</summary>
        public int Fixable => Assets.Sum(a => a.FixableCount);
        /// <summary>The assets that can be fixed (animations, masks).</summary>
        public int FixableAssets => Assets.Count(a => a.Fixable);
        /// <summary>Some asset to fix is used elsewhere too: fix it for this avatar only (a copy), or everywhere.</summary>
        public bool Shared => Assets.Any(a => a.Fixable && a.Shared);
        public IEnumerable<RepathAsset> LockedAssets => Assets.Where(a => a.Locked != null && a.Changes.Any(c => c.Fixable));

        /// <summary>The same plan with only the changes <paramref name="keep"/> keeps.</summary>
        public RepathPlan Where(Func<RepathAsset, PathChange, bool> keep)
        {
            var plan = new RepathPlan { Avatar = Avatar, Sources = Sources };
            foreach (var asset in Assets)
            {
                var copy = new RepathAsset { Asset = asset.Asset, Root = asset.Root, Locked = asset.Locked };
                copy.SharedWith.AddRange(asset.SharedWith);
                copy.Changes.AddRange(asset.Changes.Where(c => keep(asset, c)));
                if (copy.Changes.Count > 0) plan.Assets.Add(copy);
            }
            return plan;
        }

        public override string ToString() => Fixable + " fixable in " + FixableAssets + " assets; " + string.Join("; ", Assets.Select(a => a.Asset.name + ": " + string.Join(", ", a.Changes)));
    }

    /// <summary>Bindings of <see cref="From"/> that <see cref="Applies"/> keeps go to <see cref="To"/> (a mesh join's result).</summary>
    public sealed class Retarget
    {
        public Transform From, To;
        public Func<EditorCurveBinding, bool> Applies;
    }

    /// <summary>
    /// Keeps the avatar's own animations (the project's clips and masks its animators, legacy Animation components and build
    /// tools' components play) pointing at their objects when objects are renamed or moved: Plan lists what would break (and what
    /// is broken already, with the one object it most likely meant), <see cref="Apply"/> rewrites it in one Undo step.
    /// Assets in packages, read-only or inside model files are listed, never changed. An asset something else uses too is
    /// copied for this avatar (its controllers and components then point at the copy), unless it is fixed everywhere.
    /// VRChat's parts (playable layers, VRCFury and Modular Avatar merge roots, state audio) come from
    /// <see cref="AddSources"/> and <see cref="AddPathProperty"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class AnimationPaths
    {
        private static readonly List<Func<GameObject, IEnumerable<AnimationSource>>> Providers = new List<Func<GameObject, IEnumerable<AnimationSource>>>();
        private static readonly List<(Type Behaviour, string Property, Type Target)> PathProperties = new List<(Type, string, Type)>();
        private static readonly Dictionary<int, (int Version, List<AssetRefs> Refs)> Cache = new Dictionary<int, (int, List<AssetRefs>)>();
        private static int version;

        /// <summary>
        /// The avatar an object belongs to: its nearest ancestor with an avatar descriptor once VRChat's part registers
        /// itself, else the topmost one with an Animator. Null when none.
        /// </summary>
        public static Func<Transform, Transform> AvatarRootOf = t =>
        {
            Transform found = null;
            for (; t != null; t = t.parent) if (t.GetComponent<Animator>() != null) found = t;
            return found;
        };

        /// <summary>
        /// The avatars of the open scenes <see cref="AnimationPathWatch"/> follows (VRChat's part: those with a descriptor, not
        /// in preview scenes nor build copies). Null: none.
        /// </summary>
        public static Func<IEnumerable<GameObject>> Avatars;

        static AnimationPaths()
        {
            ObjectChangeEvents.changesPublished += OnChanges;
            Undo.undoRedoPerformed += Invalidate;
        }

        /// <summary>More animation data under an avatar (VRChat's playable layers, build tools' components).</summary>
        public static void AddSources(Func<GameObject, IEnumerable<AnimationSource>> provider)
        {
            if (provider != null && !Providers.Contains(provider)) Providers.Add(provider);
            Invalidate();
        }

        public static void RemoveSources(Func<GameObject, IEnumerable<AnimationSource>> provider)
        {
            if (provider != null && Providers.Remove(provider)) Invalidate();
        }

        /// <summary>A state machine behaviour's string field holding a path from its animator's object (VRChat's state audio).</summary>
        public static void AddPathProperty(Type behaviour, string property, Type target = null)
        {
            if (behaviour == null || string.IsNullOrEmpty(property) || PathProperties.Any(p => p.Behaviour == behaviour && p.Property == property)) return;
            PathProperties.Add((behaviour, property, target));
            Invalidate();
        }

        /// <summary>Forgets the bindings read for <see cref="Plan"/>'s cache (they are read again when asked).</summary>
        public static void Invalidate() { version++; Cache.Clear(); }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                    case ObjectChangeKind.CreateAssetObject:
                    case ObjectChangeKind.DestroyAssetObject:
                    case ObjectChangeKind.ChangeGameObjectStructure:
                    case ObjectChangeKind.UpdatePrefabInstances:
                    case ObjectChangeKind.ChangeScene:
                        Invalidate();
                        return;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        // A renamed object changes no binding; a component's change may (another controller, a layer).
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        var changed = EditorUtility.InstanceIDToObject(properties.instanceId);
                        if (!(changed is GameObject) && !(changed is Transform)) { Invalidate(); return; }
                        break;
                }
            }
        }

        // ---- Reading ----

        /// <summary>The path→object map animations resolve against (<see cref="PathSnapshot.Of"/>).</summary>
        public static PathSnapshot Snapshot(GameObject root) => PathSnapshot.Of(root != null ? root.transform : null);

        /// <summary>
        /// Every source of animation data under the avatar: Animator and legacy Animation components (from their objects)
        /// and what <see cref="AddSources"/> adds, without duplicates.
        /// </summary>
        public static List<AnimationSource> Sources(GameObject root)
        {
            var sources = new List<AnimationSource>();
            if (root == null) return sources;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null)
                    sources.Add(new AnimationSource { Asset = animator.runtimeAnimatorController, Root = animator.transform, Via = animator });
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                foreach (var clip in LegacyAnimationClips.Of(animation))
                    sources.Add(new AnimationSource { Asset = clip, Root = animation.transform, Via = animation });
            foreach (var provider in Providers)
            {
                IEnumerable<AnimationSource> more;
                try { more = provider(root)?.ToList(); }
                catch (Exception e) { Debug.LogException(e); continue; }
                if (more != null) sources.AddRange(more.Where(s => s != null && s.Asset != null && s.Root != null));
            }
            var seen = new HashSet<(Object, Transform, bool)>();
            return sources.Where(s => seen.Add((s.Asset, s.Root, s.WalksUp))).ToList();
        }

        // One asset's paths as read, with the sources that play it.
        private sealed class AssetRefs
        {
            public Object Asset;
            public Transform Root;
            public bool WalksUp;
            public readonly List<AnimationSource> Sources = new List<AnimationSource>();
            public readonly List<PathChange> Refs = new List<PathChange>();
        }

        // The assets a source plays: a controller's clips, masks and path-holding behaviours; a blend tree's clips.
        private static IEnumerable<Object> Expand(Object asset)
        {
            switch (asset)
            {
                case AnimationClip clip: yield return clip; break;
                case AvatarMask mask: yield return mask; break;
                case BlendTree tree:
                    foreach (var clip in TreeClips(tree, new HashSet<BlendTree>())) yield return clip;
                    break;
                case RuntimeAnimatorController controller:
                    foreach (var clip in controller.animationClips ?? Array.Empty<AnimationClip>()) if (clip != null) yield return clip;
                    var editable = BaseController(controller);
                    if (editable == null) break;
                    foreach (var layer in editable.layers)
                    {
                        if (layer.avatarMask != null) yield return layer.avatarMask;
                        if (PathProperties.Count > 0 && layer.stateMachine != null)
                            foreach (var behaviour in Behaviours(layer.stateMachine, new HashSet<AnimatorStateMachine>()))
                                if (behaviour != null && PathProperties.Any(p => p.Behaviour.IsInstanceOfType(behaviour))) yield return behaviour;
                    }
                    break;
            }
        }

        private static IEnumerable<AnimationClip> TreeClips(BlendTree tree, HashSet<BlendTree> seen)
        {
            if (tree == null || !seen.Add(tree)) yield break;
            foreach (var child in tree.children)
            {
                if (child.motion is AnimationClip clip) yield return clip;
                else if (child.motion is BlendTree inner) foreach (var c in TreeClips(inner, seen)) yield return c;
            }
        }

        private static AnimatorController BaseController(RuntimeAnimatorController controller)
        {
            for (int depth = 0; controller is AnimatorOverrideController overrides && depth < 16; depth++) controller = overrides.runtimeAnimatorController;
            return controller as AnimatorController;
        }

        private static IEnumerable<StateMachineBehaviour> Behaviours(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> seen)
        {
            if (machine == null || !seen.Add(machine)) yield break;
            foreach (var behaviour in machine.behaviours) yield return behaviour;
            foreach (var state in machine.states)
                if (state.state != null) foreach (var behaviour in state.state.behaviours) yield return behaviour;
            foreach (var child in machine.stateMachines)
                foreach (var behaviour in Behaviours(child.stateMachine, seen)) yield return behaviour;
        }

        private static List<AssetRefs> Refs(GameObject root, List<AnimationSource> sources, bool cached)
        {
            int key = root.GetInstanceID();
            if (cached && Cache.TryGetValue(key, out var hit) && hit.Version == version && hit.Refs.All(r => r.Asset != null)) return hit.Refs;
            var byKey = new Dictionary<(Object, Transform, bool), AssetRefs>();
            var list = new List<AssetRefs>();
            foreach (var source in sources)
            {
                if (source.Unknown != null) continue;
                foreach (var asset in Expand(source.Asset).Distinct())
                {
                    if (!byKey.TryGetValue((asset, source.Root, source.WalksUp), out var refs))
                    {
                        refs = new AssetRefs { Asset = asset, Root = source.Root, WalksUp = source.WalksUp };
                        Read(refs);
                        byKey.Add((asset, source.Root, source.WalksUp), refs);
                        list.Add(refs);
                    }
                    if (!refs.Sources.Contains(source)) refs.Sources.Add(source);
                }
            }
            if (cached) Cache[key] = (version, list);
            return list;
        }

        private static void Read(AssetRefs refs)
        {
            switch (refs.Asset)
            {
                case AnimationClip clip:
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (!string.IsNullOrEmpty(binding.path))
                            refs.Refs.Add(new PathChange { Kind = PathRefKind.Curve, From = binding.path, Property = binding.propertyName, Type = binding.type, Binding = binding });
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        if (!string.IsNullOrEmpty(binding.path))
                            refs.Refs.Add(new PathChange { Kind = PathRefKind.ObjectCurve, From = binding.path, Property = binding.propertyName, Type = binding.type, Binding = binding });
                    break;
                case AvatarMask mask:
                    for (int i = 0; i < mask.transformCount; i++)
                    {
                        string path = mask.GetTransformPath(i);
                        if (!string.IsNullOrEmpty(path)) refs.Refs.Add(new PathChange { Kind = PathRefKind.Mask, From = path, Property = "", Index = i });
                    }
                    break;
                case StateMachineBehaviour behaviour:
                    using (var serialized = new SerializedObject(behaviour))
                        foreach (var (type, property, target) in PathProperties)
                        {
                            if (!type.IsInstanceOfType(behaviour)) continue;
                            var field = serialized.FindProperty(property);
                            if (field != null && field.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(field.stringValue))
                                refs.Refs.Add(new PathChange { Kind = PathRefKind.Behaviour, From = field.stringValue, Property = property, Type = target });
                        }
                    break;
            }
        }

        // ---- Resolving ----

        // The objects a source's paths are looked up from, nearest first.
        private static IEnumerable<Transform> Chain(Transform from, bool walksUp, Transform avatar)
        {
            for (var t = from; t != null; t = t.parent)
            {
                yield return t;
                if (!walksUp || t == avatar) yield break;
            }
        }

        private static bool Holds(Transform t, Type type) =>
            type == null || type == typeof(Transform) || !typeof(Component).IsAssignableFrom(type) || t.GetComponent(type) != null;

        // What a path resolves to in a snapshot (and from which object of the chain); with a type, only an object holding it
        // counts for a chain walking up (VRCFury looks further up otherwise).
        private static Transform Resolve(PathSnapshot snapshot, AssetRefs refs, string path, Type type, Transform avatar, out Transform from)
        {
            Transform found = null;
            from = null;
            foreach (var r in Chain(refs.Root, refs.WalksUp, avatar))
            {
                string at = snapshot.PathOf(r);
                if (at == null) continue;
                string full = at.Length == 0 ? path : path.Length == 0 ? at : at + "/" + path;
                if (!snapshot.TryGet(full, out var t) || t == null) continue;
                if (!refs.WalksUp || Holds(t, type)) { from = r; return t; }
                if (found == null) { found = t; from = r; }
            }
            return found;
        }

        // The path to <paramref name="target"/> from the nearest object of the chain holding it (the one it resolved from
        // first), or null when it is outside every one.
        private static string PathTo(Transform target, AssetRefs refs, Transform avatar, Transform preferred)
        {
            if (preferred != null && (target == preferred || target.IsChildOf(preferred))) return Relative(target, preferred);
            foreach (var r in Chain(refs.Root, refs.WalksUp, avatar))
                if (target == r || target.IsChildOf(r)) return Relative(target, r);
            return null;
        }

        private static string Relative(Transform target, Transform root) => target == root ? "" : AnimationUtility.CalculateTransformPath(target, root);

        // ---- Planning ----

        /// <summary>
        /// The dry run after <paramref name="moves"/> (objects renamed or put elsewhere, each with its former path from the
        /// avatar's root; the objects below follow) and <paramref name="retargets"/>: per asset, each path that no longer
        /// reaches its object and the path that does. With <paramref name="broken"/>, also the paths broken already, with the
        /// one object of that name they most likely meant (none when several match). Nothing changes.
        /// </summary>
        public static RepathPlan Plan(GameObject avatarRoot, IReadOnlyList<(Transform Target, string FormerPath)> moves,
            IReadOnlyList<Retarget> retargets = null, bool broken = true, bool cached = false)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            return Plan(avatarRoot, PathSnapshot.Before(avatarRoot.transform, moves), retargets, broken, cached);
        }

        /// <summary>The dry run against the hierarchy as <paramref name="before"/> recorded it (a watcher's snapshot).</summary>
        public static RepathPlan Plan(GameObject avatarRoot, PathSnapshot before, IReadOnlyList<Retarget> retargets = null, bool broken = true, bool cached = false)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            var avatar = avatarRoot.transform;
            var now = PathSnapshot.Of(avatar);
            before ??= now;
            var sources = Sources(avatarRoot);
            var plan = new RepathPlan { Avatar = avatarRoot, Sources = sources };
            var guesses = new Dictionary<Transform, Dictionary<string, List<Transform>>>();
            foreach (var refs in Refs(avatarRoot, sources, cached))
            {
                // The VRChat SDK's own placeholder clips (proxy_stand_still, proxy_hands_idle…) animate a generic humanoid
                // that no avatar has: they are swapped at runtime, never broken, and Modular Avatar/NDMF bring 57 paths of them.
                if (VrchatPlaceholder(refs.Asset)) continue;
                RepathAsset entry = null;
                foreach (var original in refs.Refs)
                {
                    var change = Plan(refs, original, before, now, avatar, retargets, broken, guesses);
                    if (change == null) continue;
                    entry ??= new RepathAsset { Asset = refs.Asset, Root = refs.Root, Locked = Locked(refs.Asset) };
                    entry.Changes.Add(change);
                }
                if (entry == null) continue;
                Conflicts(entry);
                plan.Assets.Add(entry);
            }
            SharedUses(plan, sources);
            return plan;
        }

        /// <summary>
        /// Every clip the avatar plays, each with the object its paths start from (sources whose paths can't be followed
        /// left out). A VRCFury component's clips start from its object, though VRCFury also looks further up.
        /// </summary>
        public static IEnumerable<(AnimationClip Clip, Transform AnimatorRoot)> Clips(GameObject avatarRoot)
        {
            if (avatarRoot == null) return Enumerable.Empty<(AnimationClip, Transform)>();
            return Refs(avatarRoot, Sources(avatarRoot), false).Where(r => r.Asset is AnimationClip).Select(r => ((AnimationClip)r.Asset, r.Root)).Distinct().ToList();
        }

        /// <summary>How many paths the avatar's animations hold (curves, mask entries, behaviours' paths), each asset once per root.</summary>
        public static int CountPaths(GameObject avatarRoot) => avatarRoot == null ? 0 : Refs(avatarRoot, Sources(avatarRoot), false).Sum(r => r.Refs.Count);

        /// <summary>A clip of the VRChat SDK's packages named proxy_*: a placeholder VRChat replaces with its own animation.</summary>
        internal static bool VrchatPlaceholder(UnityEngine.Object asset)
        {
            if (!(asset is AnimationClip clip) || !clip.name.StartsWith("proxy_", StringComparison.Ordinal)) return false;
            string path = AssetDatabase.GetAssetPath(clip);
            return path.StartsWith("Packages/com.vrchat.", StringComparison.Ordinal);
        }

        /// <summary>The bindings under the avatar that reach no object now, each with the one object it most likely meant.</summary>
        public static RepathPlan Broken(GameObject avatarRoot, bool cached = false) => Plan(avatarRoot, (PathSnapshot)null, null, true, cached);

        private static PathChange Plan(AssetRefs refs, PathChange original, PathSnapshot before, PathSnapshot now, Transform avatar,
            IReadOnlyList<Retarget> retargets, bool broken, Dictionary<Transform, Dictionary<string, List<Transform>>> guesses)
        {
            var target = Resolve(before, refs, original.From, original.Type, avatar, out var from);
            bool missing = target == null || !Holds(target, original.Type);
            if (missing)
            {
                if (!broken) return null;
                var resolvedNow = Resolve(now, refs, original.From, original.Type, avatar, out _);
                if (resolvedNow != null && Holds(resolvedNow, original.Type)) return null;
                var change = Copy(original);
                change.WasBroken = true;
                var guess = Guess(refs, original, avatar, guesses, out string why);
                if (guess == null) { change.Problem = why; return change; }
                change.Target = guess;
                change.To = PathTo(guess, refs, avatar, null);
                if (change.To == null) change.Problem = "“" + guess.name + "” is outside what plays this animation";
                return change;
            }
            var retarget = original.Kind == PathRefKind.Curve || original.Kind == PathRefKind.ObjectCurve
                ? retargets?.FirstOrDefault(r => r != null && r.From == target && r.To != null && (r.Applies == null || r.Applies(original.Binding))) : null;
            if (retarget != null) target = retarget.To;
            if (target == null)
            {
                var gone = Copy(original);
                gone.Problem = "its object was deleted";
                return gone;
            }
            var reached = Resolve(now, refs, original.From, original.Type, avatar, out _);
            if (reached == target && retarget == null) return null;
            var moved = Copy(original);
            moved.Target = target;
            // A renderer's switch on a joined mesh goes to its renderer, whatever kind the source had.
            if (retarget != null && original.Type != null && typeof(Renderer).IsAssignableFrom(original.Type) && target.GetComponent(original.Type) == null &&
                target.GetComponent<Renderer>() is Renderer renderer)
                moved.NewType = renderer.GetType();
            moved.To = PathTo(target, refs, avatar, from);
            if (moved.To == null) { moved.Problem = "“" + target.name + "” moved out of what plays this animation"; return moved; }
            if (moved.To == moved.From && reached == target) return null;
            if (Resolve(now, refs, moved.To, moved.NewType ?? original.Type, avatar, out _) != target)
                moved.Problem = "another object named “" + target.name + "” comes first there: rename one of them";
            return moved;
        }

        private static PathChange Copy(PathChange c) => new PathChange
        {
            Kind = c.Kind, From = c.From, Property = c.Property, Type = c.Type, Binding = c.Binding, Index = c.Index
        };

        // The one object a broken path most likely meant: of the objects named as its last part (holding the animated
        // component), the one whose path ends with most of the same parts. Several equally close: none.
        private static Transform Guess(AssetRefs refs, PathChange change, Transform avatar, Dictionary<Transform, Dictionary<string, List<Transform>>> guesses, out string why)
        {
            var scope = refs.WalksUp ? avatar : refs.Root;
            if (!guesses.TryGetValue(scope, out var byName))
            {
                byName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
                foreach (var t in scope.GetComponentsInChildren<Transform>(true))
                {
                    if (!byName.TryGetValue(t.name, out var named)) byName[t.name] = named = new List<Transform>();
                    named.Add(t);
                }
                guesses[scope] = byName;
            }
            var parts = change.From.Split('/');
            string leaf = parts[parts.Length - 1];
            if (!byName.TryGetValue(leaf, out var candidates) || (candidates = candidates.Where(t => t != scope && Holds(t, change.Type)).ToList()).Count == 0)
            {
                bool named = byName.ContainsKey(leaf);
                why = named && change.Type != null ? "nothing named “" + leaf + "” has a " + change.Type.Name : "nothing named “" + leaf + "” is there";
                return null;
            }
            int Score(Transform t)
            {
                var path = Relative(t, scope).Split('/');
                int n = 0;
                while (n < path.Length && n < parts.Length && path[path.Length - 1 - n] == parts[parts.Length - 1 - n]) n++;
                return n;
            }
            var scored = candidates.Select(t => (t, Score(t))).ToList();
            int best = scored.Max(s => s.Item2);
            var top = scored.Where(s => s.Item2 == best).ToList();
            if (top.Count > 1)
            {
                why = top.Count + " objects are named “" + leaf + "”: fix it by hand";
                return null;
            }
            why = null;
            return top[0].t;
        }

        // Two curves can't land on the same property, nor on one the clip animates already.
        private static void Conflicts(RepathAsset entry)
        {
            if (!(entry.Asset is AnimationClip clip)) return;
            var leaving = new HashSet<(string, Type, string)>(entry.Changes.Where(c => c.Fixable).Select(c => (c.From, c.Type, c.Property)));
            var taken = new HashSet<(string, Type, string)>(
                AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    .Select(b => (b.path, b.type, b.propertyName)).Where(k => !leaving.Contains(k)));
            foreach (var change in entry.Changes.Where(c => c.Fixable && (c.Kind == PathRefKind.Curve || c.Kind == PathRefKind.ObjectCurve)))
                if (!taken.Add((change.To, change.NewType ?? change.Type, change.Property)))
                    change.Problem = "“" + change.To + "” is animated there already (" + change.Property + ")";
        }

        /// <summary>Why an asset can't be changed (in a package, read-only, inside a model file), or null.</summary>
        public static string Locked(Object asset)
        {
            if (asset == null) return "missing";
            string path = AssetDatabase.GetAssetPath(asset).Replace('\\', '/');
            if (path.Length == 0) return null;
            if (path.StartsWith("Packages/", StringComparison.Ordinal)) return "it is in a package";
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return "it is outside the project's Assets";
            if (AssetImporter.GetAtPath(path) is ModelImporter) return "it is inside a model file";
            if (!AssetDatabase.IsOpenForEdit(asset)) return "it is read-only (version control)";
            try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return "the file is read-only"; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        // ---- Sharing ----

        // Build data that comes and goes with a build: never a use.
        private static bool BuildData(string path) =>
            path.IndexOf("com.vrcfury.temp", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("/_VRCFury/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.StartsWith("Assets/OrbitersToolkitBuildCache/", StringComparison.Ordinal);

        // What else uses each asset the plan changes: sources of other objects in the open scenes, and controllers or blend
        // trees of the project this avatar doesn't play. Avatar prefabs aren't searched.
        private static void SharedUses(RepathPlan plan, List<AnimationSource> sources)
        {
            var wanted = plan.Assets.Where(a => a.Fixable).ToList();
            if (wanted.Count == 0) return;
            var others = OtherUses(plan.Avatar);
            var ours = OurFiles(sources);
            Dictionary<string, List<string>> dependents = null;
            foreach (var entry in wanted)
            {
                if (others.TryGetValue(entry.Asset, out var users)) entry.SharedWith.AddRange(users.Distinct().Take(5));
                string path = AssetDatabase.GetAssetPath(entry.Asset);
                if (path.Length == 0 || entry.Asset is StateMachineBehaviour) continue;
                dependents ??= Dependents(wanted.Select(w => AssetDatabase.GetAssetPath(w.Asset)).Where(p => p.Length > 0), ours);
                if (dependents.TryGetValue(path, out var by)) entry.SharedWith.AddRange(by.Where(b => !entry.SharedWith.Contains(b)).Take(5));
            }
        }

        // The assets (and their controllers) other objects of the open scenes play, by the name of what plays them.
        private static Dictionary<Object, List<string>> OtherUses(GameObject avatar)
        {
            var uses = new Dictionary<Object, List<string>>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var top in scene.GetRootGameObjects())
                    foreach (var source in Sources(top))
                    {
                        // The avatar's own sources (it may sit below this object) aren't another use.
                        if (source.Root.IsChildOf(avatar.transform) || source.Via is Component c && c != null && c.transform.IsChildOf(avatar.transform)) continue;
                        string by = source.Via is Component component && component != null ? component.gameObject.name : source.Root.name;
                        foreach (var asset in Expand(source.Asset).Prepend(source.Asset))
                        {
                            if (!uses.TryGetValue(asset, out var list)) uses[asset] = list = new List<string>();
                            if (!list.Contains(by)) list.Add(by);
                        }
                    }
            }
            return uses;
        }

        // The files this avatar's sources hold, directly or through other files (an override's controller, blend trees).
        private static HashSet<string> OurFiles(IEnumerable<AnimationSource> sources)
        {
            var paths = sources.Select(s => AssetDatabase.GetAssetPath(s.Asset)).Where(p => p.Length > 0).Distinct().ToArray();
            var ours = new HashSet<string>(paths, StringComparer.Ordinal);
            if (paths.Length > 0) ours.UnionWith(AssetDatabase.GetDependencies(paths, true));
            return ours;
        }

        // For each asset path, the project's controllers, override controllers and blend trees outside this avatar holding it.
        private static Dictionary<string, List<string>> Dependents(IEnumerable<string> paths, HashSet<string> ours)
        {
            var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var guids = new[] { "t:AnimatorController", "t:AnimatorOverrideController", "t:BlendTree" }.SelectMany(t => AssetDatabase.FindAssets(t, new[] { "Assets" })).Distinct();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (ours.Contains(path) || wanted.Contains(path) || BuildData(path)) continue;
                foreach (var dependency in AssetDatabase.GetDependencies(path, false))
                {
                    if (!wanted.Contains(dependency)) continue;
                    if (!found.TryGetValue(dependency, out var list)) found[dependency] = list = new List<string>();
                    list.Add(Path.GetFileNameWithoutExtension(path));
                }
            }
            return found;
        }

        // ---- Applying ----

        public sealed class Applied
        {
            /// <summary>Paths rewritten, assets changed, copies made for this avatar.</summary>
            public int Paths, Assets, Copies;
            public readonly List<string> Skipped = new List<string>();
        }

        /// <summary>
        /// Rewrites every fixable path of the plan in the project's assets, in one Undo step (assets copied for the avatar
        /// stay on the disk after an Undo, unused). A shared asset is copied for this avatar unless <paramref name="everywhere"/>:
        /// its controllers (copied too when shared) and components then point at the copy. Locked assets are skipped.
        /// </summary>
        public static Applied Apply(RepathPlan plan, bool everywhere = false, string undoName = null)
        {
            var result = new Applied();
            if (plan == null) return result;
            var todo = plan.Assets.Where(a => a.Fixable && a.Asset != null).ToList();
            foreach (var locked in plan.LockedAssets) result.Skipped.Add(locked.Asset.name + ": " + locked.Locked);
            if (todo.Count == 0) return result;
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName ?? "Update animation paths");
            var map = new Dictionary<Object, Object>();
            try
            {
                if (!everywhere && todo.Any(a => a.Shared)) result.Copies = OwnCopies(plan, todo.Where(a => a.Shared).ToList(), map);
                var controllers = plan.Sources.Select(s => s.Asset).OfType<RuntimeAnimatorController>().Select(c => map.TryGetValue(c, out var copy) ? copy as RuntimeAnimatorController : c)
                    .Select(BaseController).Where(c => c != null).Distinct().ToList();
                var changed = new List<Object>();
                // Each clip a controller plays rebuilds it when it changes: detached meanwhile, one rebuild each.
                using (ControllerEdits.Batch(controllers))
                    foreach (var entry in todo)
                    {
                        var asset = map.TryGetValue(entry.Asset, out var copy) ? copy : entry.Asset;
                        int n = Rewrite(asset, entry.Changes.Where(c => c.Fixable).ToList(), undoName ?? "Update animation paths", result.Skipped);
                        if (n == 0) continue;
                        result.Paths += n;
                        result.Assets++;
                        changed.Add(asset);
                    }
                foreach (var asset in changed) if (EditorUtility.IsPersistent(asset)) AssetDatabase.SaveAssetIfDirty(asset);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
                Invalidate();
            }
            return result;
        }

        private static int Rewrite(Object asset, List<PathChange> changes, string undoName, List<string> skipped)
        {
            switch (asset)
            {
                case AnimationClip clip:
                {
                    var curves = new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(clip));
                    var objects = new HashSet<EditorCurveBinding>(AnimationUtility.GetObjectReferenceCurveBindings(clip));
                    var removed = new List<EditorCurveBinding>();
                    var added = new List<EditorCurveBinding>();
                    var addedCurves = new List<AnimationCurve>();
                    var movedObjects = new List<(EditorCurveBinding From, EditorCurveBinding To, ObjectReferenceKeyframe[] Keys)>();
                    foreach (var change in changes)
                    {
                        var to = change.Binding;
                        to.path = change.To;
                        if (change.NewType != null) to.type = change.NewType;
                        if (change.Kind == PathRefKind.Curve && curves.Contains(change.Binding))
                        {
                            removed.Add(change.Binding);
                            added.Add(to);
                            addedCurves.Add(AnimationUtility.GetEditorCurve(clip, change.Binding));
                        }
                        else if (change.Kind == PathRefKind.ObjectCurve && objects.Contains(change.Binding))
                            movedObjects.Add((change.Binding, to, AnimationUtility.GetObjectReferenceCurve(clip, change.Binding)));
                        else skipped.Add(clip.name + ": " + change.From + " changed since the plan");
                    }
                    int count = removed.Count + movedObjects.Count;
                    if (count == 0) return 0;
                    Undo.RegisterCompleteObjectUndo(clip, undoName);
                    // Batched: setting curves one by one rebuilds the clip each time.
                    if (removed.Count > 0) AnimationUtility.SetEditorCurves(clip, removed.ToArray(), new AnimationCurve[removed.Count]);
                    if (added.Count > 0) AnimationUtility.SetEditorCurves(clip, added.ToArray(), addedCurves.ToArray());
                    foreach (var entry in movedObjects) AnimationUtility.SetObjectReferenceCurve(clip, entry.From, null);
                    foreach (var entry in movedObjects) AnimationUtility.SetObjectReferenceCurve(clip, entry.To, entry.Keys);
                    EditorUtility.SetDirty(clip);
                    return count;
                }
                case AvatarMask mask:
                {
                    var valid = changes.Where(c => c.Index < mask.transformCount && mask.GetTransformPath(c.Index) == c.From).ToList();
                    foreach (var change in changes.Except(valid)) skipped.Add(mask.name + ": " + change.From + " changed since the plan");
                    if (valid.Count == 0) return 0;
                    Undo.RecordObject(mask, undoName);
                    foreach (var change in valid) mask.SetTransformPath(change.Index, change.To);
                    EditorUtility.SetDirty(mask);
                    return valid.Count;
                }
                case StateMachineBehaviour behaviour:
                {
                    int count = 0;
                    using (var serialized = new SerializedObject(behaviour))
                    {
                        foreach (var change in changes)
                        {
                            var field = serialized.FindProperty(change.Property);
                            if (field == null || field.propertyType != SerializedPropertyType.String || field.stringValue != change.From)
                            {
                                skipped.Add(behaviour.name + ": " + change.From + " changed since the plan");
                                continue;
                            }
                            field.stringValue = change.To;
                            count++;
                        }
                        if (count > 0) serialized.ApplyModifiedProperties();
                    }
                    return count;
                }
            }
            return 0;
        }

        // Copies of the shared assets for this avatar, then every controller and component of the avatar leading to them
        // points at the copies (a shared controller on the way is copied too). Returns the files made.
        private static int OwnCopies(RepathPlan plan, List<RepathAsset> shared, Dictionary<Object, Object> map)
        {
            int made = 0;
            string suffix = " (" + Sanitize(plan.Avatar.name) + ")";
            foreach (var entry in shared)
            {
                if (map.ContainsKey(entry.Asset)) continue;
                if (CopyFile(entry.Asset, suffix, map)) made++;
            }
            if (made == 0) return 0;
            var others = OtherUses(plan.Avatar);
            var ours = OurFiles(plan.Sources);
            // The avatar's own controllers and blend trees: those its sources hold, and the files they play.
            var holders = plan.Sources.Select(s => s.Asset).Where(a => a is RuntimeAnimatorController || a is BlendTree)
                .Concat(ours.Where(p => p.EndsWith(".controller", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase) ||
                                        p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    .Select(AssetDatabase.LoadMainAssetAtPath).Where(a => a is RuntimeAnimatorController || a is BlendTree)).Distinct().ToList();
            var holderPaths = holders.Select(AssetDatabase.GetAssetPath).Where(p => p.Length > 0).ToList();
            var dependents = Dependents(holderPaths, ours);
            for (bool changedAny = true; changedAny;)
            {
                changedAny = false;
                foreach (var holder in holders)
                {
                    if (map.ContainsKey(holder) || !Refers(holder, map)) continue;
                    string path = AssetDatabase.GetAssetPath(holder);
                    bool sharedHolder = others.ContainsKey(holder) || dependents.ContainsKey(path);
                    if (sharedHolder && Locked(holder) == null && CopyFile(holder, suffix, map)) { made++; changedAny = true; }
                    var target = map.TryGetValue(holder, out var copy) ? copy : holder;
                    if (Locked(target) == null) Replace(target, map);
                }
            }
            foreach (var via in plan.Sources.Select(s => s.Via).OfType<Component>().Distinct())
                if (via != null) ReplaceIn(via, map);
            return made;
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        // Copies the asset's file beside it; its objects (the asset, its sub-assets) map to the copy's.
        private static bool CopyFile(Object asset, string suffix, Dictionary<Object, Object> map)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (path.Length == 0) return false;
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main != null && map.ContainsKey(main))
            {
                MapSubAssets(path, AssetDatabase.GetAssetPath(map[main]), map);
                return false;
            }
            string target = AssetDatabase.GenerateUniqueAssetPath(Path.GetDirectoryName(path).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));
            if (!AssetDatabase.CopyAsset(path, target)) return false;
            MapSubAssets(path, target, map);
            return true;
        }

        private static void MapSubAssets(string from, string to, Dictionary<Object, Object> map)
        {
            var copies = new Dictionary<long, Object>();
            foreach (var copy in AssetDatabase.LoadAllAssetsAtPath(to))
                if (copy != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(copy, out string _, out long id)) copies[id] = copy;
            foreach (var original in AssetDatabase.LoadAllAssetsAtPath(from))
                if (original != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string _, out long id) && copies.TryGetValue(id, out var copy))
                    map[original] = copy;
        }

        // Whether a controller's or blend tree's objects (its file's) point at something being replaced.
        private static bool Refers(Object holder, Dictionary<Object, Object> map)
        {
            foreach (var part in Parts(holder))
                using (var serialized = new SerializedObject(part))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null && map.ContainsKey(property.objectReferenceValue))
                            return true;
                }
            return false;
        }

        private static IEnumerable<Object> Parts(Object holder)
        {
            string path = AssetDatabase.GetAssetPath(holder);
            if (path.Length == 0) return new[] { holder };
            return AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o != null);
        }

        private static void Replace(Object holder, Dictionary<Object, Object> map)
        {
            foreach (var part in Parts(holder).ToList()) ReplaceIn(part, map);
        }

        // Points every reference of the object at its replacement (recorded for Undo).
        private static void ReplaceIn(Object target, Dictionary<Object, Object> map)
        {
            using (var serialized = new SerializedObject(target))
            {
                bool changed = false;
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_Script") continue;
                    var value = property.objectReferenceValue;
                    if (value != null && map.TryGetValue(value, out var replacement) && replacement != value)
                    {
                        property.objectReferenceValue = replacement;
                        changed = true;
                    }
                }
                if (changed) serialized.ApplyModifiedProperties();
            }
        }

        // ---- For a mesh join ----

        /// <summary>
        /// What shows and hides each renderer (m_IsActive of it or an object above it, below the avatar's root; m_Enabled of
        /// it): the clips and properties, as text. Renderers joined into one keep one answer: different sets can't be kept.
        /// </summary>
        public static Dictionary<Renderer, HashSet<string>> Toggles(GameObject avatarRoot, IEnumerable<Renderer> renderers)
        {
            var result = new Dictionary<Renderer, HashSet<string>>();
            var list = renderers.Where(r => r != null).Distinct().ToList();
            foreach (var r in list) result[r] = new HashSet<string>(StringComparer.Ordinal);
            if (avatarRoot == null || list.Count == 0) return result;
            var avatar = avatarRoot.transform;
            var now = PathSnapshot.Of(avatar);
            var common = CommonParent(list);
            foreach (var refs in Refs(avatarRoot, Sources(avatarRoot), true))
            {
                if (!(refs.Asset is AnimationClip clip)) continue;
                foreach (var change in refs.Refs)
                {
                    if (change.Kind != PathRefKind.Curve || change.Property != "m_IsActive" && change.Property != "m_Enabled") continue;
                    var target = Resolve(now, refs, change.From, change.Type, avatar, out _);
                    if (target == null) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, change.Binding);
                    string key = clip.name + "#" + clip.GetInstanceID() + ":" + change.Property + ":" + Keys(curve);
                    foreach (var r in list)
                    {
                        bool hits = change.Property == "m_Enabled"
                            ? target == r.transform && change.Type != null && change.Type.IsInstanceOfType(r)
                            : r.transform == target || r.transform.IsChildOf(target) && target != avatar && !common.IsChildOf(target);
                        if (hits) result[r].Add(key);
                    }
                }
            }
            return result;
        }

        private static string Keys(AnimationCurve curve) =>
            curve == null ? "" : string.Join(",", curve.keys.Select(k => k.time.ToString("0.###") + "=" + k.value.ToString("0.###")));

        private static Transform CommonParent(List<Renderer> renderers)
        {
            var parent = renderers[0].transform.parent;
            while (parent != null && !renderers.All(r => r.transform.IsChildOf(parent))) parent = parent.parent;
            return parent != null ? parent : renderers[0].transform.root;
        }
    }

    internal sealed class AnimationPathImports : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Length + deleted.Length + moved.Length > 0) AnimationPaths.Invalidate();
        }
    }
}
