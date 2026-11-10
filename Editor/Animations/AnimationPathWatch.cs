using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// Notices when renaming or moving objects of an avatar (in Unity's Hierarchy or any tool) breaks its animations: it
    /// keeps each avatar's paths as they were (avatars of the open scenes only, from <see cref="AnimationPaths.Avatars"/>),
    /// compares them a moment after the hierarchy changed and, when a binding would break, holds a notice (<see
    /// cref="Notices"/>, <see cref="Changed"/>) until the user fixes it (<see cref="Fix"/>) or leaves it (<see
    /// cref="Leave"/>). It never changes an asset by itself. Tools that rename or move objects themselves call <see
    /// cref="Check"/> right after, with what they moved.
    /// </summary>
    [InitializeOnLoad]
    public static class AnimationPathWatch
    {
        public sealed class Notice
        {
            public GameObject Avatar;
            /// <summary>The plan when last checked (the fix plans again).</summary>
            public RepathPlan Plan;
            /// <summary>What happened, e.g. "Renaming “Jacket”".</summary>
            public string What;
            /// <summary>The objects whose animations point at the old paths, by name.</summary>
            public List<string> Objects = new List<string>();
            internal PathSnapshot Before;
            internal UndoStep Step;

            /// <summary>e.g. "Renaming “Jacket” broke 3 animations".</summary>
            public string Text => What + " broke " + Plan.FixableAssets + (Plan.FixableAssets == 1 ? " animation" : " animations");
        }

        private const double Quiet = .4;
        private static readonly Dictionary<int, PathSnapshot> Snapshots = new Dictionary<int, PathSnapshot>();
        private static readonly Dictionary<int, Notice> Pending = new Dictionary<int, Notice>();
        private static readonly HashSet<int> Dirty = new HashSet<int>();
        private static bool rescan = true, everywhere;
        private static double due;

        /// <summary>A notice came, changed or went.</summary>
        public static event Action Changed;

        public static IReadOnlyCollection<Notice> Notices => Pending.Values;

        /// <summary>The switch in the Orbiters settings window (Features, Toolkit), also in Mixer's Hierarchy menu and on the notice.</summary>
        public const string Feature = "toolkit.animation-path-watch";
        private const string Label = "Notice renames that break animations";

        /// <summary>
        /// Whether the watcher follows renames and moves in Unity's Hierarchy and other tools (the user's switch, on by
        /// default). Off, it holds no notice; tools that rename or move objects themselves still ask (<see cref="Check"/>).
        /// </summary>
        public static bool Enabled
        {
            get => enabled;
            set => OrbitersFeatures.SetEnabled(Feature, value);
        }

        private static bool enabled;

        static AnimationPathWatch()
        {
            OrbitersFeatures.Register(new OrbitersFeature
            {
                Key = Feature, Product = "Toolkit", Label = Label, PrefKey = "Orbiters.Toolkit.AnimationPathWatch",
                Description = "When renaming or moving objects in Unity's Hierarchy (or any tool) stops an avatar's animations reaching them, " +
                              "My Avatar's card and Mixer's Hierarchy say so, with Fix and Leave. Nothing changes until you press Fix.",
                Stage = FeatureStage.Stable, Default = true,
            });
            enabled = OrbitersFeatures.IsEnabled(Feature);
            OrbitersFeatures.Changed += key =>
            {
                if (key != Feature) return;
                enabled = OrbitersFeatures.IsEnabled(Feature);
                // Off: no notice is held; on again: the avatars' paths as they are now.
                Clear();
            };
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorApplication.update += Tick;
            // Avatars added or loaded get their snapshot a moment later (not on every frame of a drag).
            EditorApplication.hierarchyChanged += () => { rescan = true; Later(); };
            EditorSceneManager.sceneOpened += (_, __) => rescan = true;
            EditorSceneManager.sceneClosed += _ => rescan = true;
            EditorApplication.playModeStateChanged += _ => Clear();
            Undo.undoRedoPerformed += () => { everywhere = true; Later(); };
        }

        public static Notice For(GameObject avatar) => avatar != null && Pending.TryGetValue(avatar.GetInstanceID(), out var notice) ? notice : null;

        private static bool Paused => EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling || !Enabled;

        private static void Clear()
        {
            bool had = Pending.Count > 0;
            Snapshots.Clear(); Pending.Clear(); Dirty.Clear();
            rescan = true;
            if (had) Changed?.Invoke();
        }

        private static void Later() => due = EditorApplication.timeSinceStartup + Quiet;

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            if (Paused) return;
            for (int i = 0; i < stream.length; i++)
            {
                int id;
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectParent: stream.GetChangeGameObjectParentEvent(i, out var parent); id = parent.instanceId; break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        if (!Marks(EditorUtility.InstanceIDToObject(properties.instanceId))) continue;
                        id = properties.instanceId;
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy: stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy); id = hierarchy.instanceId; break;
                    case ObjectChangeKind.UpdatePrefabInstances: everywhere = true; Later(); continue;
                    case ObjectChangeKind.ChangeScene: rescan = true; everywhere = true; Later(); continue;
                    default: continue;
                }
                var found = EditorUtility.InstanceIDToObject(id);
                var t = found is GameObject go ? go.transform : found is Component c ? c.transform : null;
                if (t == null) continue;
                var root = AnimationPaths.AvatarRootOf?.Invoke(t);
                if (root == null) continue;
                Dirty.Add(root.gameObject.GetInstanceID());
                Later();
            }
        }

        /// <summary>
        /// Whether a property change of <paramref name="changed"/> calls for its avatar's paths to be compared again. Only a
        /// GameObject's own properties hold a path (its name): a component's change (a slider dragged on a renderer, a
        /// transform moved) renames nothing. On an avatar holding a notice, a component's change may change what plays its
        /// animations (an Animator given another controller, the descriptor's layers): its notice is checked again.
        /// </summary>
        internal static bool Marks(UnityEngine.Object changed)
        {
            if (changed is GameObject) return true;
            if (Pending.Count == 0 || !(changed is Component component) || component == null || component is Transform) return false;
            var root = AnimationPaths.AvatarRootOf?.Invoke(component.transform);
            return root != null && Pending.ContainsKey(root.gameObject.GetInstanceID());
        }

        private static void Tick()
        {
            if (Paused || AnimationPaths.Avatars == null || EditorApplication.timeSinceStartup < due) return;
            // The changed avatars first, against the snapshots taken before the change.
            if (Dirty.Count == 0 && !everywhere)
            {
                if (rescan) { rescan = false; Scan(); }
                return;
            }
            var ids = everywhere ? Snapshots.Keys.ToList() : Dirty.ToList();
            everywhere = false;
            Dirty.Clear();
            bool changed = false;
            foreach (int id in ids)
            {
                var avatar = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (avatar == null) { Snapshots.Remove(id); changed |= Pending.Remove(id); continue; }
                try { changed |= Compare(avatar); }
                catch (Exception e) { Debug.LogException(e); Snapshots[id] = PathSnapshot.Of(avatar.transform); }
            }
            if (rescan) { rescan = false; Scan(); }
            if (changed) Changed?.Invoke();
        }

        // Snapshots of the open scenes' avatars, kept for those already followed.
        private static void Scan()
        {
            var avatars = AnimationPaths.Avatars().Where(a => a != null).ToList();
            var ids = new HashSet<int>(avatars.Select(a => a.GetInstanceID()));
            bool changed = false;
            foreach (int gone in Snapshots.Keys.Where(k => !ids.Contains(k)).ToList()) { Snapshots.Remove(gone); changed |= Pending.Remove(gone); }
            foreach (var avatar in avatars)
                if (!Snapshots.ContainsKey(avatar.GetInstanceID())) Snapshots[avatar.GetInstanceID()] = PathSnapshot.Of(avatar.transform);
            if (changed) Changed?.Invoke();
        }

        // Compares the avatar with its snapshot; a notice while something is broken, else the snapshot follows.
        private static bool Compare(GameObject avatar)
        {
            int id = avatar.GetInstanceID();
            if (!Snapshots.TryGetValue(id, out var before)) { Snapshots[id] = PathSnapshot.Of(avatar.transform); return false; }
            // The paths now are read once: for the comparison, and as the next snapshot.
            var moves = before.Moves(out var now);
            Pending.TryGetValue(id, out var held);
            if (moves.Count == 0)
            {
                Snapshots[id] = now;
                return held != null && Pending.Remove(id);
            }
            var plan = Breaks(avatar, before);
            if (plan.Fixable == 0)
            {
                // Nothing broke: the snapshot follows the hierarchy.
                Snapshots[id] = now;
                return held != null && Pending.Remove(id);
            }
            var notice = held ?? new Notice { Avatar = avatar, Before = before };
            notice.Plan = plan;
            notice.What = held?.What ?? Describe(avatar.transform, before, moves);
            notice.Objects = plan.Changes.Where(c => c.Fixable && c.Target != null).Select(c => c.Target.name).Distinct().ToList();
            Pending[id] = notice;
            return true;
        }

        private static RepathPlan Breaks(GameObject avatar, PathSnapshot before) =>
            AnimationPaths.Plan(avatar, before, null, broken: false, cached: true).Where((a, c) => !c.WasBroken);

        private static string Describe(Transform avatar, PathSnapshot before, List<(Transform Target, string FormerPath)> moves)
        {
            var moved = new HashSet<Transform>(moves.Select(m => m.Target));
            // Only the topmost: the objects below a renamed or moved one followed it.
            var top = moves.Where(m => m.Target.parent == null || !moved.Contains(m.Target.parent) ||
                                       before.PathOf(m.Target.parent) == AnimationUtility.CalculateTransformPath(m.Target.parent, avatar)).ToList();
            if (top.Count != 1) return "Changes to " + top.Count + " objects";
            var (target, former) = top[0];
            int slash = former.LastIndexOf('/');
            string formerParent = slash < 0 ? "" : former.Substring(0, slash), formerName = former.Substring(slash + 1);
            string parentNow = target.parent == avatar ? "" : AnimationUtility.CalculateTransformPath(target.parent, avatar);
            return formerParent == parentNow ? "Renaming “" + formerName + "”" : "Moving “" + target.name + "”";
        }

        /// <summary>
        /// For a tool that just renamed or moved objects of an avatar itself (<paramref name="moves"/>: each with its former
        /// path from the avatar's root): the notice when animations would break, else null (the avatar's paths are taken as
        /// they are now). The tool asks the user (Update / Leave) and calls <see cref="Fix"/> or <see cref="Leave"/>; a fix
        /// joins the Undo step the tool's change made when nothing else was done since.
        /// </summary>
        public static Notice Check(GameObject avatar, IReadOnlyList<(Transform Target, string FormerPath)> moves, string what = null)
        {
            if (avatar == null) return null;
            int id = avatar.GetInstanceID();
            var before = Pending.TryGetValue(id, out var held) ? held.Before : PathSnapshot.Before(avatar.transform, moves);
            if (held != null)
                // Paths broken earlier still count from before that change; this change's moves add to them.
                foreach (var (target, former) in moves) before.Alias(target, former);
            var plan = Breaks(avatar, before);
            if (plan.Fixable == 0)
            {
                Snapshots[id] = PathSnapshot.Of(avatar.transform);
                if (held != null && Pending.Remove(id)) Changed?.Invoke();
                return null;
            }
            var notice = held ?? new Notice { Avatar = avatar, Before = before };
            notice.Plan = plan;
            notice.What = what ?? Describe(avatar.transform, before, before.Moves());
            notice.Objects = plan.Changes.Where(c => c.Fixable && c.Target != null).Select(c => c.Target.name).Distinct().ToList();
            notice.Step = UndoStep.Last();
            Pending[id] = notice;
            Snapshots[id] = before;
            Changed?.Invoke();
            return notice;
        }

        /// <summary>
        /// Rewrites the animations the notice is about (planned again: the hierarchy may have changed since), in one Undo
        /// step, and takes the avatar's paths as they are now. Shared animations are copied for this avatar unless
        /// <paramref name="everywhere"/>.
        /// </summary>
        public static AnimationPaths.Applied Fix(Notice notice, bool everywhere = false)
        {
            if (notice == null || notice.Avatar == null) return new AnimationPaths.Applied();
            var plan = Breaks(notice.Avatar, notice.Before);
            AnimationPaths.Applied applied = null;
            void Run() => applied = AnimationPaths.Apply(plan, everywhere, "Update animations: " + notice.What);
            // The tool's change still the last Undo step: the fix joins it, one Undo takes both back.
            if (notice.Step != null) notice.Step.Join(Run);
            else { Undo.IncrementCurrentGroup(); Run(); }
            Leave(notice);
            return applied;
        }

        /// <summary>Leaves the animations as they are: the avatar's paths are taken as they are now.</summary>
        public static void Leave(Notice notice)
        {
            if (notice == null) return;
            if (notice.Avatar != null)
            {
                int id = notice.Avatar.GetInstanceID();
                Snapshots[id] = PathSnapshot.Of(notice.Avatar.transform);
                if (Pending.TryGetValue(id, out var held) && held == notice) Pending.Remove(id);
            }
            Changed?.Invoke();
        }

        /// <summary>Takes the avatar's paths as they are now (a tool fixed its animations itself).</summary>
        public static void Accept(GameObject avatar)
        {
            if (avatar == null) return;
            int id = avatar.GetInstanceID();
            Snapshots[id] = PathSnapshot.Of(avatar.transform);
            if (Pending.Remove(id)) Changed?.Invoke();
        }
    }
}
