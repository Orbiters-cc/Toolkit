using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;

namespace Orbiters.Toolkit.Editor.VRChat.Posing
{
    /// <summary>
    /// A preview that keeps clothing and accessories in the avatar's pose the way they will be attached at build: My Avatar
    /// attachments, VRCFury Armature Links (merged bones and props on one bone), and the bone matcher for clothing nothing
    /// links yet. It is temporary: the accessories go back where they were when it is switched off, before a scene is saved,
    /// on a script reload and on entering Play Mode, and while their own edits run (<see cref="Pause"/>).
    /// </summary>
    [InitializeOnLoad]
    public static class AccessoryPoseSync
    {
        public sealed class Accessory
        {
            public Transform Root;
            public string Name, Rule;
            public int Following, Total;
            /// <summary>Why the preview is partial, or null.</summary>
            public string Reason;
        }

        private struct Snapshot
        {
            public Transform Transform;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            // Where the preview last put it: anything else (an Undo) set it since.
            public Vector3 ShownPosition, ShownScale;
            public Quaternion ShownRotation;
        }

        private static readonly List<FollowLink> Links = new List<FollowLink>();
        private static readonly List<Snapshot> Snapshots = new List<Snapshot>();
        private static readonly List<Accessory> Found = new List<Accessory>();
        private static readonly Dictionary<Transform, Matrix4x4> TargetFrames = new Dictionary<Transform, Matrix4x4>();
        private static readonly Dictionary<SkinnedMeshRenderer, bool> RendererFrames = new Dictionary<SkinnedMeshRenderer, bool>();
        private static HashSet<Transform> avatarBones = new HashSet<Transform>();
        private static bool pending, saving;
        private static int paused;
        // The links followed when the preview paused, with their followers' rest: offsets kept across the pause.
        private static List<(FollowLink link, Matrix4x4 rest)> held;

        public static Transform Root { get; private set; }
        public static bool Enabled => Root != null;
        public static IReadOnlyList<Accessory> Accessories => Found;
        public static string LastStatus { get; private set; } = "Off";
        public static event Action Changed;

        static AccessoryPoseSync()
        {
            Undo.postprocessModifications += Follow;
            EditorApplication.update += FollowChangedPose;
            Undo.undoRedoPerformed += UndoRedo;
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) Disable(); };
            AssemblyReloadEvents.beforeAssemblyReload += Disable;
            EditorSceneManager.sceneSaving += (scene, _) => { if (Enabled && Root.gameObject.scene == scene) { saving = true; Restore(); } };
            EditorSceneManager.sceneSaved += scene => { if (saving) { saving = false; if (Enabled) Sync(); } };
        }

        // A custom base's logic (MCB's proxies and contacts) links to the bones too, but it is not clothing.
        private static Orbiters.Toolkit.Editor.Refit.CustomBaseFootprint CustomBaseObjects(Transform avatarRoot) =>
            Orbiters.Toolkit.Editor.Refit.CustomBases.Describe(avatarRoot)?.Footprint?.Invoke() ?? new Orbiters.Toolkit.Editor.Refit.CustomBaseFootprint();

        /// <summary>The accessories under the avatar and how each follows it, without changing anything.</summary>
        public static List<Accessory> Find(Transform avatarRoot)
        {
            var result = new List<Accessory>();
            if (avatarRoot == null) return result;
            var skeleton = AvatarSkeleton.Bones(avatarRoot, AttachmentPlanner.Body(avatarRoot));
            var footprint = CustomBaseObjects(avatarRoot);
            foreach (var group in AttachmentFollow.Links(avatarRoot).GroupBy(l => l.Accessory))
            {
                if (group.Key == null || footprint.Owns(group.Key)) continue;
                var bones = group.Key.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones)
                    .Where(b => b != null && !skeleton.Contains(b)).Distinct().Count();
                var links = group.ToList();
                int driven = links.Count(l => Driven(l.Follower));
                bool modular = group.Key.GetComponentsInChildren<Component>(true).Any(c => c != null && (c.GetType().Namespace ?? "").StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal));
                bool rigid = links.Count == 1 && bones <= 1;
                result.Add(new Accessory
                {
                    Root = group.Key, Name = group.Key.name,
                    Rule = rigid ? "on " + links[0].Target.name : links[0].Source,
                    Following = links.Count - driven, Total = rigid ? 1 : Mathf.Max(bones, links.Count),
                    Reason = modular ? "Modular Avatar attaches it: shown by name match."
                        : driven > 0 ? $"{driven} bone{(driven == 1 ? " is" : "s are")} driven by constraints and left alone." : null,
                });
            }
            return result;
        }

        public static bool Enable(Transform avatarRoot)
        {
            Disable();
            if (avatarRoot == null || EditorUtility.IsPersistent(avatarRoot) || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail("Select an editable scene avatar.");
            // An edit of accessories at work (a fit, a refit): the preview starts once it is done.
            if (paused > 0)
            {
                Root = avatarRoot;
                LastStatus = "Starts once the change at work on the accessories is done";
                Changed?.Invoke();
                return true;
            }
            string error = Follow(avatarRoot, null);
            if (error != null) return Fail(error);
            Changed?.Invoke();
            return true;
        }

        // Collects what follows the avatar, notes where it rests and places it. A link followed before a pause keeps its
        // offset, moved by as much as its follower's rest moved meanwhile: the avatar may be posed now, so the offset could
        // not be measured again.
        private static string Follow(Transform avatarRoot, List<(FollowLink link, Matrix4x4 rest)> earlier)
        {
            // Bones driven by constraints are left to them.
            var footprint = CustomBaseObjects(avatarRoot);
            Links.AddRange(AttachmentFollow.Links(avatarRoot).Where(l => !Driven(l.Follower) && !footprint.Owns(l.Accessory)));
            Found.AddRange(Find(avatarRoot));
            if (Links.Count == 0)
            {
                string error = Found.Count == 0 ? "No clothing or accessory to follow under this avatar." : "No accessory bone matches the avatar's bones.";
                Clear();
                return error;
            }
            for (int i = 0; earlier != null && i < Links.Count; i++)
            {
                var link = Links[i];
                int before = earlier.FindIndex(e => e.link.Follower == link.Follower && e.link.Target == link.Target);
                if (before < 0) continue;
                link.Offset = earlier[before].link.Offset * earlier[before].rest.inverse * link.Follower.localToWorldMatrix;
                Links[i] = link;
            }
            // Parents first, so each child is placed from its parent's new pose.
            Links.Sort((x, y) => Depth(x.Follower).CompareTo(Depth(y.Follower)));
            foreach (var t in Links.Select(l => l.Follower).Distinct())
                Snapshots.Add(new Snapshot { Transform = t, Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale });
            foreach (var renderer in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                RendererFrames[renderer] = renderer.forceMatrixRecalculationPerRender;
            var skeleton = AvatarSkeleton.Bones(avatarRoot, AttachmentPlanner.Body(avatarRoot));
            avatarBones = new HashSet<Transform>(skeleton);
            foreach (var bone in skeleton)
                for (var ancestor = bone.parent; ancestor != null; ancestor = ancestor.parent) avatarBones.Add(ancestor);
            Root = avatarRoot;
            LastStatus = $"{Found.Count} accessor{(Found.Count == 1 ? "y follows" : "ies follow")} the avatar · {Links.Count} bone{(Links.Count == 1 ? "" : "s")} · back in place when switched off";
            Sync();
            return null;
        }

        public static void Disable()
        {
            if (Root == null && Links.Count == 0) return;
            Restore();
            Root = null;
            held = null;
            Clear();
            LastStatus = "Off";
            Changed?.Invoke();
        }

        private static void Clear()
        {
            Links.Clear();
            Snapshots.Clear();
            Found.Clear();
            TargetFrames.Clear();
            RendererFrames.Clear();
        }

        /// <summary>
        /// Puts the accessories back where they rest until disposed, then follows again from where they rest then. Edits of
        /// accessories run inside it (a fit, its cancel, a removal, a refit): their Undo records and their own records must
        /// see where the accessories rest, not where the preview shows them, and switching the preview off must not put back
        /// a rest noted before them. Does nothing while the preview is off or on another avatar.
        /// </summary>
        public static IDisposable Pause(Transform avatarRoot = null)
        {
            if (!Enabled || avatarRoot != null && !avatarRoot.IsChildOf(Root) && !Root.IsChildOf(avatarRoot)) return new Resume(false);
            if (paused++ == 0 && Links.Count > 0)
            {
                Restore();
                held = Links.Where(l => l.Follower != null).Select(l => (l, l.Follower.localToWorldMatrix)).ToList();
                Clear();
            }
            return new Resume(true);
        }

        private sealed class Resume : IDisposable
        {
            private bool active;
            public Resume(bool active) => this.active = active;

            public void Dispose()
            {
                if (!active) return;
                active = false;
                if (--paused > 0) return;
                var earlier = held;
                held = null;
                if (!Enabled) return;
                string error = Follow(Root, earlier);
                if (error != null) { Root = null; Fail(error); }
                else Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        /// <summary>Places every follower at its offset from its avatar bone.</summary>
        public static void Sync()
        {
            if (!Enabled) return;
            Links.RemoveAll(l => l.Target == null || l.Follower == null);
            foreach (var link in Links) link.Apply();
            foreach (var link in Links) TargetFrames[link.Target] = link.Target.localToWorldMatrix;
            for (int i = 0; i < Snapshots.Count; i++)
            {
                var snapshot = Snapshots[i];
                if (snapshot.Transform == null) continue;
                snapshot.ShownPosition = snapshot.Transform.localPosition;
                snapshot.ShownRotation = snapshot.Transform.localRotation;
                snapshot.ShownScale = snapshot.Transform.localScale;
                Snapshots[i] = snapshot;
            }
            foreach (var renderer in RendererFrames.Keys)
                if (renderer != null) renderer.forceMatrixRecalculationPerRender = true;
            // Repainting alone can retain the previous GPU skinning result in edit mode.
            EditorApplication.QueuePlayerLoopUpdate();
        }

        // Scene handles, complete-object Undo and other pose tools do not all emit property modifications.
        // Observe the target frames without consuming Transform.hasChanged (other tools use it too).
        private static void FollowChangedPose()
        {
            if (!Enabled || saving || EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var link in Links)
                if (link.Target != null && (!TargetFrames.TryGetValue(link.Target, out var frame) || frame != link.Target.localToWorldMatrix))
                {
                    Sync();
                    SceneView.RepaintAll();
                    break;
                }
        }

        // Edits of accessories are recorded while they rest (see Pause), so what an Undo or Redo set on a follower is where it
        // rests now: the preview pauses from the rest it knew, takes that one, and follows again from there. Otherwise only
        // the avatar's pose changed.
        private static void UndoRedo()
        {
            if (!Enabled || paused > 0) return;
            var moved = Snapshots.Where(s => s.Transform != null && !Shown(s.Transform, s))
                .Select(s => (transform: s.Transform, position: s.Transform.localPosition, rotation: s.Transform.localRotation, scale: s.Transform.localScale)).ToList();
            if (moved.Count == 0) { Schedule(); return; }
            using (Pause())
                foreach (var rest in moved)
                {
                    rest.transform.localPosition = rest.position;
                    rest.transform.localRotation = rest.rotation;
                    rest.transform.localScale = rest.scale;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(rest.transform);
                }
        }

        private static bool Shown(Transform t, Snapshot snapshot) =>
            (t.localPosition - snapshot.ShownPosition).sqrMagnitude < 1e-10f && (t.localScale - snapshot.ShownScale).sqrMagnitude < 1e-10f &&
            Quaternion.Angle(t.localRotation, snapshot.ShownRotation) < 1e-3f;

        // The preview is not an edit: the accessories get their own transforms back, and their prefab overrides with them.
        private static void Restore()
        {
            foreach (var renderer in RendererFrames)
                if (renderer.Key != null) renderer.Key.forceMatrixRecalculationPerRender = renderer.Value;
            foreach (var snapshot in Snapshots)
            {
                if (snapshot.Transform == null) continue;
                snapshot.Transform.localPosition = snapshot.Position;
                snapshot.Transform.localRotation = snapshot.Rotation;
                snapshot.Transform.localScale = snapshot.Scale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(snapshot.Transform);
            }
        }

        private static UndoPropertyModification[] Follow(UndoPropertyModification[] modifications)
        {
            if (!Enabled) return modifications;
            foreach (var modification in modifications)
            {
                if (modification.currentValue?.target is Transform t && avatarBones.Contains(t) &&
                    (modification.currentValue.propertyPath.StartsWith("m_LocalRotation") || modification.currentValue.propertyPath.StartsWith("m_LocalPosition") || modification.currentValue.propertyPath.StartsWith("m_LocalScale")))
                {
                    Schedule();
                    break;
                }
            }
            return modifications;
        }

        // After the edit is applied (and after mirrored bones are updated).
        private static void Schedule()
        {
            if (pending) return;
            pending = true;
            EditorApplication.delayCall += () =>
            {
                pending = false;
                if (!Enabled) return;
                Sync();
                SceneView.RepaintAll();
            };
        }

        private static bool Driven(Transform t) => t.GetComponent<IConstraint>() != null || t.GetComponent<VRCConstraintBase>() != null;

        private static bool Fail(string message)
        {
            LastStatus = message;
            Changed?.Invoke();
            return false;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }
    }
}
