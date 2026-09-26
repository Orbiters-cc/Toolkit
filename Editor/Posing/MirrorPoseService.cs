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

        private static readonly Dictionary<Transform, Bone> Bones = new Dictionary<Transform, Bone>();
        private static bool applying;
        public static Transform Root { get; private set; }
        public static bool Enabled => Root != null && Bones.Count != 0;
        public static int PairCount => Bones.Count / 2;
        public static int RelativePairCount { get; private set; }
        public static string LastStatus { get; private set; } = "Select an avatar or bone, then enable Mirror.";
        public static event Action Changed;

        static MirrorPoseService()
        {
            Undo.postprocessModifications += MirrorEdits;
            EditorApplication.playModeStateChanged += _ => Disable();
            EditorApplication.hierarchyChanged += CheckHierarchy;
            AssemblyReloadEvents.beforeAssemblyReload += Disable;
        }

        public static void Disable()
        {
            Root = null;
            Bones.Clear();
            RelativePairCount = 0;
            LastStatus = "Mirror off. Select an avatar or bone to enable it.";
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        public static bool Enable(Transform root, SkinnedMeshRenderer renderer)
        {
            Disable();
            if (root == null || renderer == null || !root.gameObject.scene.IsValid() ||
                EditorUtility.IsPersistent(root) || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail("Select an editable scene avatar with a skinned mesh.");
            if (AnimationMode.InAnimationMode())
                return Fail("Exit animation preview/recording before enabling Mirror.");

            var transforms = root.GetComponentsInChildren<Transform>(true);
            var skinBones = renderer.bones;
            var skeleton = renderer.rootBone != null && renderer.rootBone.IsChildOf(root)
                ? renderer.rootBone.GetComponentsInChildren<Transform>(true)
                : skinBones.Where(t => t != null && t.IsChildOf(root)).ToArray();
            var skeletonFrames = new HashSet<Transform>();
            foreach (var bone in skeleton)
                for (var ancestor = bone; ancestor != null; ancestor = ancestor.parent)
                    skeletonFrames.Add(ancestor);
            // Reflections of rotations assume orthogonal frames; reject shear and negative scale.
            var invalidScale = skeletonFrames.FirstOrDefault(t => !UniformPositive(t.localScale));
            if (invalidScale != null)
                return Fail($"Mirror cannot start: {invalidScale.name} has non-uniform or non-positive scale. Bones and their ancestors require positive, uniform scale.");

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
            if (Bones.Count == 0) return Fail("No matching left/right bone pairs found on this avatar.");
            // A pair must share the same reference strategy. Partial bind data uses enable-time pose.
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
                (RelativePairCount > 0 ? $" · {RelativePairCount} use the enable-time pose" : " · mesh bind pose");
            Changed?.Invoke();
            SceneView.RepaintAll();
            return true;
        }

        public static Transform GetPartner(Transform bone)
        {
            return bone != null && Bones.TryGetValue(bone, out var entry) ? entry.Other.Transform : null;
        }

        private static bool Fail(string message)
        {
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
                b.Transform.parent != b.Parent || !b.Transform.IsChildOf(Root)))) Disable();
        }

        private static UndoPropertyModification[] MirrorEdits(UndoPropertyModification[] modifications)
        {
            if (!Enabled || applying || EditorApplication.isPlayingOrWillChangePlaymode ||
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
