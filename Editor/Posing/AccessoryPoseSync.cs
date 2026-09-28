using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Posing
{
    /// <summary>
    /// Keeps clothing and accessories that are not merged into the avatar yet (their own armature under the avatar,
    /// merged at build by VRCFury Armature Link or a similar tool) in the avatar's pose. Each clothing bone keeps the
    /// offset it has from its avatar bone at rest, taken from both meshes' bind poses, so the result matches what the
    /// build produces. While on, every pose edit on the avatar is followed within the same editor frame.
    /// </summary>
    [InitializeOnLoad]
    public static class AccessoryPoseSync
    {
        public sealed class Accessory
        {
            public Transform Root;
            public string Name;
            public int MatchedBones;
            public int TotalBones;
        }

        private struct Link
        {
            public Transform Avatar, Clothing;
            public Vector3 OffsetPosition;
            public Quaternion OffsetRotation;
        }

        private static readonly List<Link> Links = new List<Link>();
        private static readonly List<Accessory> Found = new List<Accessory>();
        private static HashSet<Transform> avatarBones = new HashSet<Transform>();
        private static bool pending;

        public static Transform Root { get; private set; }
        public static bool Enabled => Root != null;
        public static IReadOnlyList<Accessory> Accessories => Found;
        public static string LastStatus { get; private set; } = "Off";
        public static event Action Changed;

        static AccessoryPoseSync()
        {
            Undo.postprocessModifications += Follow;
            Undo.undoRedoPerformed += () => { if (Enabled) Schedule(); };
            EditorApplication.playModeStateChanged += _ => Disable();
            AssemblyReloadEvents.beforeAssemblyReload += Disable;
        }

        /// <summary>Clothing armatures under the avatar that are not part of its skeleton, with how many bones match it.</summary>
        public static List<Accessory> Find(Transform avatarRoot, SkinnedMeshRenderer body)
        {
            var result = new List<Accessory>();
            if (avatarRoot == null || body == null) return result;
            var skeleton = Skeleton(avatarRoot, body);
            var index = BoneIndex(avatarRoot, skeleton);
            foreach (var group in ClothingRenderers(avatarRoot, skeleton).GroupBy(r => ArmatureRoot(r, avatarRoot)))
            {
                if (group.Key == null) continue;
                var bones = group.SelectMany(r => r.bones).Where(b => b != null).Distinct().ToList();
                int matched = bones.Count(b => Match(b, index) != null);
                // Props with their own bones that match nothing of the avatar (physics spheres, orbits) are not clothing.
                if (matched == 0) continue;
                result.Add(new Accessory
                {
                    Root = group.Key, Name = AccessoryName(group.Key, avatarRoot),
                    TotalBones = bones.Count, MatchedBones = matched,
                });
            }
            return result;
        }

        public static bool Enable(Transform avatarRoot, SkinnedMeshRenderer body)
        {
            Disable();
            if (avatarRoot == null || body == null || EditorUtility.IsPersistent(avatarRoot) || EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail("Select an editable scene avatar.");
            var skeleton = Skeleton(avatarRoot, body);
            var index = BoneIndex(avatarRoot, skeleton);
            var avatarRest = RestFrames(avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => skeleton.Contains(r.rootBone)));
            foreach (var renderer in ClothingRenderers(avatarRoot, skeleton))
            {
                var clothingRest = RestFrames(new[] { renderer });
                foreach (var bone in renderer.bones)
                {
                    if (bone == null || Links.Any(l => l.Clothing == bone)) continue;
                    var target = Match(bone, index);
                    if (target == null) continue;
                    // Rest offset from bind poses when both bones are skinned, else from where they stand now.
                    var avatarFrame = avatarRest.TryGetValue(target, out var a) ? a : Matrix4x4.TRS(target.position, target.rotation, Vector3.one);
                    var clothingFrame = clothingRest.TryGetValue(bone, out var c) ? c : Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one);
                    var offset = avatarFrame.inverse * clothingFrame;
                    Links.Add(new Link { Avatar = target, Clothing = bone, OffsetPosition = offset.GetColumn(3), OffsetRotation = offset.rotation });
                }
            }
            Found.Clear();
            Found.AddRange(Find(avatarRoot, body));
            if (Links.Count == 0) return Fail(Found.Count == 0 ? "No separate clothing armature found under this avatar." : "No clothing bone matches the avatar's bones.");
            // Parents first, so each child is placed from its parent's new pose.
            Links.Sort((x, y) => Depth(x.Clothing).CompareTo(Depth(y.Clothing)));
            avatarBones = new HashSet<Transform>(skeleton);
            Root = avatarRoot;
            LastStatus = $"{Found.Count} accessor{(Found.Count == 1 ? "y" : "ies")} follow the avatar · {Links.Count} bones";
            Sync(recordUndo: true);
            Changed?.Invoke();
            return true;
        }

        public static void Disable()
        {
            if (Root == null && Links.Count == 0) return;
            Root = null;
            Links.Clear();
            Found.Clear();
            LastStatus = "Off";
            Changed?.Invoke();
        }

        /// <summary>Places every matched clothing bone at its rest offset from its avatar bone.</summary>
        public static void Sync(bool recordUndo)
        {
            if (!Enabled) return;
            Links.RemoveAll(l => l.Avatar == null || l.Clothing == null);
            if (recordUndo) Undo.RecordObjects(Links.Select(l => (UnityEngine.Object)l.Clothing).ToArray(), "Sync accessory pose");
            foreach (var link in Links)
            {
                var rotation = link.Avatar.rotation * link.OffsetRotation;
                var position = link.Avatar.position + link.Avatar.rotation * link.OffsetPosition;
                if (link.Clothing.rotation == rotation && link.Clothing.position == position) continue;
                link.Clothing.SetPositionAndRotation(position, rotation);
                PrefabUtility.RecordPrefabInstancePropertyModifications(link.Clothing);
            }
        }

        private static UndoPropertyModification[] Follow(UndoPropertyModification[] modifications)
        {
            if (!Enabled) return modifications;
            foreach (var modification in modifications)
            {
                if (modification.currentValue?.target is Transform t && avatarBones.Contains(t) &&
                    (modification.currentValue.propertyPath.StartsWith("m_LocalRotation") || modification.currentValue.propertyPath.StartsWith("m_LocalPosition")))
                {
                    Schedule();
                    break;
                }
            }
            return modifications;
        }

        // Runs after the edit is applied (and after mirrored bones are updated), merged into the same Undo step.
        private static void Schedule()
        {
            if (pending) return;
            pending = true;
            int group = Undo.GetCurrentGroup();
            EditorApplication.delayCall += () =>
            {
                pending = false;
                if (!Enabled) return;
                Sync(recordUndo: true);
                Undo.CollapseUndoOperations(group);
                SceneView.RepaintAll();
            };
        }

        private static bool Fail(string message)
        {
            LastStatus = message;
            Changed?.Invoke();
            return false;
        }

        private static HashSet<Transform> Skeleton(Transform avatarRoot, SkinnedMeshRenderer body) => AvatarSkeleton.Bones(avatarRoot, body);

        private static IEnumerable<SkinnedMeshRenderer> ClothingRenderers(Transform avatarRoot, HashSet<Transform> skeleton) =>
            avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.bones.Length > 0 && r.bones.Where(b => b != null).All(b => !skeleton.Contains(b)));

        private static Transform ArmatureRoot(SkinnedMeshRenderer renderer, Transform avatarRoot)
        {
            // The smallest ancestor holding the renderer and all its bones, below the avatar root.
            var bones = renderer.bones.Where(b => b != null).ToList();
            for (var candidate = renderer.transform; candidate != null && candidate != avatarRoot; candidate = candidate.parent)
                if (bones.All(b => b.IsChildOf(candidate))) return candidate;
            return null;
        }

        private static string AccessoryName(Transform root, Transform avatarRoot)
        {
            // "Armature" says nothing: name the accessory after the object that holds it.
            var named = root;
            while (named.parent != null && named.parent != avatarRoot && Normalize(named.name) == "armature") named = named.parent;
            return named.name;
        }

        private sealed class Index
        {
            public readonly Dictionary<string, Transform> ByName = new Dictionary<string, Transform>();
            public readonly Dictionary<Transform, HumanoidNames.BodySide> Sides = new Dictionary<Transform, HumanoidNames.BodySide>();
            public readonly Dictionary<HumanBodyBones, Transform> ByHuman = new Dictionary<HumanBodyBones, Transform>();
        }

        private static Index BoneIndex(Transform avatarRoot, HashSet<Transform> skeleton)
        {
            var index = new Index();
            foreach (var bone in skeleton)
            {
                var key = Normalize(bone.name);
                if (!index.ByName.ContainsKey(key)) index.ByName[key] = bone;
                index.Sides[bone] = HumanoidNames.Side(bone.name);
            }
            var animator = avatarRoot.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.isHuman && a.avatar != null && a.avatar.isValid);
            if (animator != null)
                foreach (HumanBodyBones id in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (id == HumanBodyBones.LastBone) continue;
                    var bone = animator.GetBoneTransform(id);
                    if (bone != null) index.ByHuman[id] = bone;
                }
            return index;
        }

        // Same name (ignoring case and separators), then the humanoid role the name describes (upper_arm.L, Left arm,
        // Thigh_R...), then the longest avatar bone name the clothing bone contains ("Hips_Shirt").
        private static Transform Match(Transform clothingBone, Index index)
        {
            var key = Normalize(clothingBone.name);
            if (index.ByName.TryGetValue(key, out var exact)) return exact;
            if (HumanoidNames.TryInfer(clothingBone.name, out var id) && index.ByHuman.TryGetValue(id, out var human)) return human;
            Transform best = null;
            int bestLength = 3;
            var side = HumanoidNames.Side(clothingBone.name);
            foreach (var entry in index.ByName)
                if (entry.Key.Length > bestLength && key.Contains(entry.Key) && index.Sides[entry.Value] == side) { best = entry.Value; bestLength = entry.Key.Length; }
            return best;
        }

        private static Dictionary<Transform, Matrix4x4> RestFrames(IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var frames = new Dictionary<Transform, Matrix4x4>();
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                var bindposes = mesh.bindposes;
                var bones = renderer.bones;
                var meshFrame = renderer.transform.localToWorldMatrix;
                for (int i = 0; i < Mathf.Min(bindposes.Length, bones.Length); i++)
                {
                    if (bones[i] == null || frames.ContainsKey(bones[i]) || Mathf.Abs(bindposes[i].determinant) < 1e-8f) continue;
                    var frame = meshFrame * bindposes[i].inverse;
                    frames[bones[i]] = Matrix4x4.TRS(frame.GetColumn(3), frame.rotation, Vector3.one);
                }
            }
            return frames;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }

        internal static string Normalize(string name)
        {
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name.Substring(colon + 1);
            var chars = name.ToLowerInvariant().Where(c => c != ' ' && c != '_' && c != '.' && c != '-').ToArray();
            return new string(chars);
        }
    }
}
