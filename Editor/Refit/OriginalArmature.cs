using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Refit
{
    /// <summary>
    /// The armature of the original base an avatar's custom base was made from, placed over the avatar's body: bones only,
    /// hidden and never saved, gone on Dispose. Clothing sits on a body where its bones stand in it, and a custom base may
    /// move its bones on the same body (the Ultipaw's hips, spine and legs stand 8 cm above the Masculine Canine's, its neck
    /// 3 cm, its shoulders 1 cm). Read once per custom base and version, as resolving the original may import a model (half
    /// a minute for a body with hundreds of blendshapes): kept for the session and in Library/Orbiters.
    /// </summary>
    public sealed class OriginalArmature : IDisposable
    {
        private const string CachePath = "Library/Orbiters/original-armatures.json";

        [Serializable] private sealed class Cache { public List<Entry> entries = new List<Entry>(); }
        // Parents first. A top bone's pose is in the original body's space, the others' in their parent's.
        [Serializable] private sealed class Entry { public string key; public List<Bone> bones = new List<Bone>(); }
        [Serializable] private sealed class Bone { public string name; public int parent; public Vector3 position; public Quaternion rotation; public Vector3 scale; }

        private static Cache cache;
        private GameObject root;

        /// <summary>The bones, under a root lying where the avatar's body is; null once disposed.</summary>
        public Transform Root => root != null ? root.transform : null;

        /// <summary>Its bones by name and humanoid role, the roles from their names (the original's humanoid avatar is not kept).</summary>
        public AvatarBoneIndex Index { get; private set; }

        /// <summary>
        /// The original base's armature over the avatar's body. Null when no custom base is known for the avatar, when it
        /// cannot give its original, and when reading the original fails (said in the Console).
        /// </summary>
        public static OriginalArmature Over(Transform avatarRoot)
        {
            var info = CustomBases.Describe(avatarRoot);
            if (info == null || !info.CanFit || info.Body == null) return null;
            List<Bone> bones;
            try { bones = Bones(info); }
            catch (Exception ex)
            {
                Debug.LogWarning("[Orbiters] Could not read the original base of " + avatarRoot.name + ": " + ex.Message);
                return null;
            }
            return bones.Count > 0 ? Build(bones, info.Body.transform) : null;
        }

        public void Dispose()
        {
            if (root != null) Object.DestroyImmediate(root);
            root = null;
        }

        private static List<Bone> Bones(CustomBaseInfo info)
        {
            bool keep = !string.IsNullOrEmpty(info.Key);
            var entry = keep ? Load().entries.FirstOrDefault(e => e.key == info.Key) : null;
            if (entry != null) return entry.bones;
            List<Bone> bones;
            using (var original = info.ResolveOriginal()) bones = Read(original);
            if (keep && bones.Count > 0)
            {
                var data = Load();
                data.entries.Add(new Entry { key = info.Key, bones = bones });
                Save(data);
            }
            return bones;
        }

        // The original's armature (the highest ancestor of its hips or of its body's root bone), as it stands from its body.
        private static List<Bone> Read(CustomBaseOriginal original)
        {
            var bones = new List<Bone>();
            var top = original?.Avatar != null && original.Body != null ? AvatarSkeleton.Root(original.Avatar.transform, original.Body) : null;
            if (top == null) return bones;
            var body = original.Body.transform;
            var indexOf = new Dictionary<Transform, int>();
            foreach (var t in top.GetComponentsInChildren<Transform>(true))
            {
                indexOf[t] = bones.Count;
                bones.Add(t != top && indexOf.TryGetValue(t.parent, out int parent)
                    ? new Bone { name = t.name, parent = parent, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }
                    : new Bone { name = t.name, parent = -1, position = body.InverseTransformPoint(t.position), rotation = Quaternion.Inverse(body.rotation) * t.rotation,
                        scale = Divide(t.lossyScale, body.lossyScale) });
            }
            return bones;
        }

        private static OriginalArmature Build(List<Bone> bones, Transform body)
        {
            var armature = new OriginalArmature { root = EditorUtility.CreateGameObjectWithHideFlags("Original base armature", HideFlags.HideAndDontSave) };
            var top = armature.root.transform;
            top.SetPositionAndRotation(body.position, body.rotation);
            top.localScale = body.lossyScale;
            var made = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                var t = EditorUtility.CreateGameObjectWithHideFlags(bone.name, HideFlags.HideAndDontSave).transform;
                t.SetParent(bone.parent >= 0 ? made[bone.parent] : top, false);
                t.localPosition = bone.position;
                t.localRotation = bone.rotation;
                t.localScale = bone.scale;
                made[i] = t;
            }
            armature.Index = AvatarBoneIndex.Build(top, made);
            return armature;
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0f ? a.x / b.x : 1f, b.y != 0f ? a.y / b.y : 1f, b.z != 0f ? a.z / b.z : 1f);

        private static Cache Load()
        {
            if (cache != null) return cache;
            try { cache = File.Exists(CachePath) ? JsonUtility.FromJson<Cache>(File.ReadAllText(CachePath)) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { cache = null; }
            return cache ??= new Cache();
        }

        private static void Save(Cache data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, JsonUtility.ToJson(data));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
