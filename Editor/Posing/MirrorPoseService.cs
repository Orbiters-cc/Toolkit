using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Posing
{
    /// <summary>Mirrors explicit editor pose edits, scoped to one rig and one Undo operation.</summary>
    [InitializeOnLoad]
    public static class MirrorPoseService
    {
        private sealed class Bone
        {
            public Transform Transform;
            public Transform Parent;
            public Bone Other;
            public Quaternion Rotation;
            public Vector3 Position;
            public Matrix4x4 ParentFrame;
            public bool FromBindPose;
        }

        /// <summary>Finds the rig to mirror from what is selected: its root and the mesh whose bind pose defines rest.</summary>
        public delegate bool RigResolver(GameObject selected, out Transform root, out SkinnedMeshRenderer renderer);

        private const string EnabledKey = "Orbiters.Toolkit.MirrorPose.Enabled";
        private const string OffStatus = "Mirror off.";
        private const string WaitingStatus = "Mirror on · select an avatar or one of its bones.";
        private static readonly Dictionary<Transform, Bone> Bones = new Dictionary<Transform, Bone>();
        private static bool applying;

        /// <summary>Mirror mode is on. It stays on while nothing can be mirrored and binds to the next rig selected.</summary>
        public static bool Enabled { get; private set; }
        /// <summary>The rig being mirrored, or null while mirror mode waits for one.</summary>
        public static Transform Root { get; private set; }
        public static bool IsMirroring => Root != null && Bones.Count != 0;
        public static int PairCount => Bones.Count / 2;
        public static int RelativePairCount { get; private set; }
        public static string LastStatus { get; private set; } = OffStatus;
        public static event Action Changed;
        /// <summary>
        /// How a selection outside any humanoid becomes a rig (props, tails, non-humanoid models). The nearest humanoid
        /// above the selection always wins, so accessories with their own small armature do not capture it.
        /// </summary>
        public static RigResolver ResolveOtherRig;

        static MirrorPoseService()
        {
            Undo.postprocessModifications += MirrorEdits;
            Selection.selectionChanged += () => { if (Enabled) BindFromSelection(); };
            EditorApplication.hierarchyChanged += CheckHierarchy;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Unbind(WaitingStatus);
                else if (state == PlayModeStateChange.EnteredEditMode && Enabled) BindFromSelection();
            };
            // Mirror mode survives script reloads; the rig is found again from the selection.
            Enabled = SessionState.GetBool(EnabledKey, false);
            if (Enabled)
            {
                LastStatus = WaitingStatus;
                EditorApplication.delayCall += () => { if (Enabled && Root == null) BindFromSelection(); };
            }
        }

        /// <summary>Turns mirror mode on, bound to <paramref name="root"/> when given, else to the selected rig.</summary>
        public static bool Enable(Transform root = null, SkinnedMeshRenderer renderer = null)
        {
            SetMode(true);
            if (root != null) Bind(root, renderer);
            else BindFromSelection(force: true);
            return IsMirroring;
        }

        public static void Disable()
        {
            SetMode(false);
            Unbind(OffStatus);
        }

        private static void SetMode(bool enabled)
        {
            Enabled = enabled;
            SessionState.SetBool(EnabledKey, enabled);
        }

        private static void BindFromSelection(bool force = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var selected = Selection.activeGameObject;
            if (selected == null || !(HumanoidRig(selected, out var root, out var renderer) || ResolveOtherRig != null && ResolveOtherRig(selected, out root, out renderer)) || root == null)
            {
                // Keep the current rig while the selection is elsewhere (a light, the Project window...).
                if (Root == null) Unbind(WaitingStatus);
                return;
            }
            if (force || root != Root) Bind(root, renderer);
        }

        private static void Unbind(string status)
        {
            Root = null;
            Bones.Clear();
            RelativePairCount = 0;
            LastStatus = status;
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        private static bool Bind(Transform root, SkinnedMeshRenderer renderer)
        {
            Root = null;
            Bones.Clear();
            RelativePairCount = 0;
            if (renderer == null && root != null) renderer = BestRenderer(root);
            if (root == null || renderer == null || !root.gameObject.scene.IsValid() ||
                EditorUtility.IsPersistent(root) || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail("Mirror on · select a scene avatar with a skinned mesh.");

            var transforms = root.GetComponentsInChildren<Transform>(true);
            var skinBones = renderer.bones;
            var skeleton = renderer.rootBone != null && renderer.rootBone.IsChildOf(root)
                ? renderer.rootBone.GetComponentsInChildren<Transform>(true)
                : skinBones.Where(t => t != null && t.IsChildOf(root)).ToArray();

            var frames = transforms.ToDictionary(t => t, t => root.worldToLocalMatrix * t.localToWorldMatrix);
            var bound = new HashSet<Transform>();
            var mesh = renderer.sharedMesh;
            if (mesh != null)
            {
                var bindposes = mesh.bindposes;
                var meshFrame = root.worldToLocalMatrix * renderer.localToWorldMatrix;
                for (int i = 0; i < Math.Min(bindposes.Length, skinBones.Length); i++)
                {
                    var bone = skinBones[i];
                    if (bone == null || bone == root || !frames.ContainsKey(bone) ||
                        Mathf.Abs(bindposes[i].determinant) < 1e-8f) continue;
                    frames[bone] = meshFrame * bindposes[i].inverse;
                    bound.Add(bone);
                }
            }

            var pairs = new Dictionary<Transform, Transform>();
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (!animator.isHuman || animator.avatar == null || !animator.avatar.isValid) continue;
                foreach (HumanBodyBones id in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    var name = id.ToString();
                    if (!name.StartsWith("Left", StringComparison.Ordinal)) continue;
                    if (!Enum.TryParse("Right" + name.Substring(4), out HumanBodyBones otherId)) continue;
                    AddPair(pairs, animator.GetBoneTransform(id), animator.GetBoneTransform(otherId), frames);
                }
            }

            // Match full relative paths, including mirrored parents. Duplicate paths are ambiguous.
            var paths = skeleton.Where(t => t != root)
                .GroupBy(t => AnimationUtility.CalculateTransformPath(t, root))
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            foreach (var item in paths)
            {
                var opposite = string.Join("/", item.Key.Split('/').Select(MirrorName));
                if (opposite != item.Key && paths.TryGetValue(opposite, out var other))
                    AddPair(pairs, item.Value, other, frames);
            }

            // Reflections of rotations assume orthogonal frames: a pair is mirrored only when both bones and all their
            // ancestors have positive, uniform scale. Scaled props elsewhere in the hierarchy do not matter.
            int skipped = 0;
            foreach (var pair in pairs.Where(p => !UniformChain(p.Key, root) || !UniformChain(p.Value, root)).ToList())
            {
                if (pairs.Remove(pair.Key)) skipped++;
                pairs.Remove(pair.Value);
            }

            foreach (var pair in pairs)
            {
                var t = pair.Key;
                var parentFrame = t.parent != null && frames.TryGetValue(t.parent, out var frame)
                    ? frame : Matrix4x4.identity;
                var local = parentFrame.inverse * frames[t];
                Bones[t] = new Bone
                {
                    Transform = t, Parent = t.parent, Rotation = local.rotation,
                    Position = local.GetColumn(3), ParentFrame = parentFrame,
                    FromBindPose = bound.Contains(t) && (t.parent == root || bound.Contains(t.parent))
                };
            }
            foreach (var pair in pairs) Bones[pair.Key].Other = Bones[pair.Value];
            if (Bones.Count == 0)
                return Fail(skipped > 0 ? $"Mirror on · the left and right bones of {root.name} are unevenly scaled and cannot be mirrored."
                    : $"Mirror on · {root.name} has no left and right bones to mirror.");
            // A pair must share the same reference strategy. Partial bind data uses the pose at binding time.
            foreach (var bone in Bones.Values)
            {
                if (bone.FromBindPose && bone.Other.FromBindPose) continue;
                bone.Rotation = bone.Transform.localRotation;
                bone.Position = bone.Transform.localPosition;
                bone.ParentFrame = root.worldToLocalMatrix * bone.Transform.parent.localToWorldMatrix;
            }
            RelativePairCount = Bones.Values.Count(b => !b.FromBindPose || !b.Other.FromBindPose) / 2;
            Root = root;
            LastStatus = $"Mirror: {root.name} · {PairCount} pairs" +
                (RelativePairCount > 0 ? $" · {RelativePairCount} use the pose at start" : "") +
                (skipped > 0 ? $" · {skipped} unevenly scaled pair{(skipped == 1 ? "" : "s")} skipped" : "");
            Changed?.Invoke();
            SceneView.RepaintAll();
            return true;
        }

        private static bool UniformChain(Transform bone, Transform root)
        {
            for (var t = bone; t != null && t != root; t = t.parent)
                if (!UniformPositive(t.localScale)) return false;
            return true;
        }

        private static bool HumanoidRig(GameObject selected, out Transform root, out SkinnedMeshRenderer renderer)
        {
            root = null; renderer = null;
            // The nearest humanoid above the selection, or the selection itself.
            for (var t = selected.transform; t != null; t = t.parent)
            {
                var animator = t.GetComponent<Animator>();
                if (animator == null || !animator.isHuman || animator.avatar == null) continue;
                root = t;
                renderer = BestRenderer(t);
                return renderer != null;
            }
            return false;
        }

        // The skinned mesh with the most bones: the body, whose bind pose defines the rest pose.
        private static SkinnedMeshRenderer BestRenderer(Transform root) =>
            root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
                .OrderByDescending(r => r.bones.Length).FirstOrDefault();

        public static Transform GetPartner(Transform bone)
        {
            return bone != null && Bones.TryGetValue(bone, out var entry) ? entry.Other.Transform : null;
        }

        private static bool Fail(string message)
        {
            Root = null;
            Bones.Clear();
            LastStatus = message;
            Changed?.Invoke();
            return false;
        }

        private static void AddPair(Dictionary<Transform, Transform> pairs, Transform left, Transform right,
            Dictionary<Transform, Matrix4x4> frames)
        {
            if (left == null || right == null || left == right || !frames.ContainsKey(left) ||
                !frames.ContainsKey(right) || pairs.ContainsKey(left) || pairs.ContainsKey(right) ||
                left.IsChildOf(right) || right.IsChildOf(left)) return;
            pairs.Add(left, right);
            pairs.Add(right, left);
        }

        private static string MirrorName(string name)
        {
            var sides = new[] { "Left", "Right", "left", "right", "LEFT", "RIGHT" };
            for (int i = 0; i < sides.Length; i++)
            {
                if (name.StartsWith(sides[i], StringComparison.Ordinal))
                    return sides[i ^ 1] + name.Substring(sides[i].Length);
                if (name.EndsWith(sides[i], StringComparison.Ordinal))
                    return name.Substring(0, name.Length - sides[i].Length) + sides[i ^ 1];
            }
            foreach (var separator in new[] { '.', '_', '-', ' ' })
            {
                foreach (var side in new[] { 'L', 'R', 'l', 'r' })
                {
                    char other = side == 'L' ? 'R' : side == 'R' ? 'L' : side == 'l' ? 'r' : 'l';
                    if (name.EndsWith(separator.ToString() + side, StringComparison.Ordinal))
                        return name.Substring(0, name.Length - 1) + other;
                    if (name.StartsWith(side.ToString() + separator, StringComparison.Ordinal))
                        return other + name.Substring(1);
                }
            }
            // Namespaced rigs such as mixamorig:LeftArm.
            int colon = name.LastIndexOf(':');
            return colon >= 0 ? name.Substring(0, colon + 1) + MirrorName(name.Substring(colon + 1)) : name;
        }

        private static bool UniformPositive(Vector3 scale)
        {
            // Imported rigs often contain tiny component differences around an intended uniform scale.
            // Mathf.Approximately is too strict here (roughly one part per million).
            float largest = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            float smallest = Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
            return smallest > 0 && largest - smallest <= largest * 0.0001f;
        }

        private static void CheckHierarchy()
        {
            if (Bones.Count > 0 && (Root == null || Bones.Values.Any(b => b.Transform == null ||
                b.Transform.parent != b.Parent || !b.Transform.IsChildOf(Root))))
            {
                // The rig changed shape: bind it again, or wait for the next one, without leaving mirror mode.
                var root = Root;
                Unbind(WaitingStatus);
                if (Enabled && root != null) Bind(root, null);
            }
        }

        private static UndoPropertyModification[] MirrorEdits(UndoPropertyModification[] modifications)
        {
            if (!IsMirroring || applying || EditorApplication.isPlayingOrWillChangePlaymode ||
                AnimationMode.InAnimationMode()) return modifications;
            var edits = new Dictionary<Transform, int>();
            foreach (var modification in modifications)
            {
                var value = modification.currentValue;
                var t = value?.target as Transform;
                if (t == null || !Bones.ContainsKey(t)) continue;
                int flags = value.propertyPath.StartsWith("m_LocalRotation.", StringComparison.Ordinal) ? 1 :
                    value.propertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal) ? 2 : 0;
                edits.TryGetValue(t, out var previous);
                edits[t] = previous | flags;
            }
            applying = true;
            try
            {
                foreach (var edit in edits)
                {
                    var source = Bones[edit.Key];
                    var target = source.Other;
                    if (edit.Value == 0 || target.Transform == null || edits.ContainsKey(target.Transform)) continue;
                    // Complete-object snapshots avoid recursively generating transform modifications.
                    Undo.RegisterCompleteObjectUndo(target.Transform, "Mirror pose");
                    if ((edit.Value & 1) != 0)
                    {
                        var basis = source.ParentFrame.rotation;
                        var delta = basis * (source.Transform.localRotation * Quaternion.Inverse(source.Rotation)) * Quaternion.Inverse(basis);
                        var reflected = new Quaternion(delta.x, -delta.y, -delta.z, delta.w);
                        var targetBasis = target.ParentFrame.rotation;
                        target.Transform.localRotation = Quaternion.Inverse(targetBasis) * reflected * targetBasis * target.Rotation;
                    }
                    if ((edit.Value & 2) != 0)
                    {
                        var delta = source.ParentFrame.MultiplyVector(source.Transform.localPosition - source.Position);
                        delta.x = -delta.x;
                        target.Transform.localPosition = target.Position + target.ParentFrame.inverse.MultiplyVector(delta);
                    }
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target.Transform);
                }
            }
            finally { applying = false; }
            SceneView.RepaintAll();
            return modifications;
        }
    }
}
