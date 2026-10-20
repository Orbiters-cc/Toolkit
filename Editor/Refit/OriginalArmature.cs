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
    /// a minute for a body with hundreds of blendshapes): kept for the session and in Library/Orbiters (the most recent
    /// <see cref="MaxEntries"/>, read again once the original's model file was reimported). An original that cannot be read
    /// is not asked again in the session until a custom base changes.
    /// </summary>
    public sealed class OriginalArmature : IDisposable
    {
        private const string CachePath = "Library/Orbiters/original-armatures.json";
        // Changes when entries keep something new: older files are then read again.
        private const int Format = 2;
        private const int MaxEntries = 32;

        [Serializable] private sealed class Cache { public int format; public List<Entry> entries = new List<Entry>(); }
        // Parents first. A top bone's pose is in the original body's space, the others' in their parent's. The model file the
        // original's body came from and its dependency hash, when it is a project file (a reimport changes the hash).
        [Serializable] private sealed class Entry { public string key, source, hash; public List<Bone> bones = new List<Bone>(); }
        [Serializable] private sealed class Bone { public string name; public int parent; public Vector3 position; public Quaternion rotation; public Vector3 scale; }

        private static Cache cache;
        // Custom bases whose original could not be read this session.
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);
        private GameObject root;

        /// <summary>The bones, under a root lying where the avatar's body is; null once disposed.</summary>
        public Transform Root => root != null ? root.transform : null;

        /// <summary>Its bones by name and humanoid role, the roles from their names (the original's humanoid avatar is not kept).</summary>
        public AvatarBoneIndex Index { get; private set; }

        [InitializeOnLoadMethod]
        private static void ForgetFailuresOnChange() => CustomBases.Changed += _ => Failed.Clear();

        /// <summary>
        /// The original base's armature over the avatar's body. Null when no custom base is known for the avatar, when it
        /// cannot give its original, and when reading the original fails (said in the Console, once a session).
        /// </summary>
        public static OriginalArmature Over(Transform avatarRoot)
        {
            var info = CustomBases.Describe(avatarRoot);
            if (info == null || !info.CanFit || info.Body == null) return null;
            bool keep = !string.IsNullOrEmpty(info.Key);
            if (keep && Failed.Contains(info.Key)) return null;
            List<Bone> bones;
            try { bones = Bones(info); }
            catch (Exception ex)
            {
                Debug.LogWarning("[Orbiters] Could not read the original base of " + avatarRoot.name + ": " + ex.Message);
                bones = null;
            }
            if (bones == null || bones.Count == 0)
            {
                if (keep) Failed.Add(info.Key);
                return null;
            }
            try { return Build(bones, info.Body.transform); }
            catch (Exception ex)
            {
                // Data that does not make an armature: read again next time.
                Debug.LogWarning("[Orbiters] The original base armature of " + avatarRoot.name + " could not be placed: " + ex.Message);
                if (keep) Forget(info.Key);
                return null;
            }
        }

        public void Dispose()
        {
            if (root != null) Object.DestroyImmediate(root);
            root = null;
        }

        private static List<Bone> Bones(CustomBaseInfo info)
        {
            bool keep = !string.IsNullOrEmpty(info.Key);
            var data = keep ? Load() : null;
            var entry = keep ? data.entries.FirstOrDefault(e => e.key == info.Key) : null;
            if (entry != null && Valid(entry))
            {
                // Most recently used last: the oldest go first.
                data.entries.Remove(entry);
                data.entries.Add(entry);
                return entry.bones;
            }
            List<Bone> bones;
            string source = null, hash = null;
            using (var original = info.ResolveOriginal())
            {
                bones = Read(original);
                source = ModelFile(original);
                if (source != null) hash = AssetDatabase.GetAssetDependencyHash(source).ToString();
            }
            if (keep && bones.Count > 0)
            {
                data.entries.RemoveAll(e => e.key == info.Key);
                data.entries.Add(new Entry { key = info.Key, source = source, hash = hash, bones = bones });
                if (data.entries.Count > MaxEntries) data.entries.RemoveRange(0, data.entries.Count - MaxEntries);
                Save(data);
            }
            return bones;
        }

        // Parents before their children, and the model file as it was read (when it is still a project file).
        private static bool Valid(Entry entry)
        {
            if (entry.bones == null || entry.bones.Count == 0) return false;
            for (int i = 0; i < entry.bones.Count; i++)
                if (entry.bones[i] == null || entry.bones[i].parent >= i || entry.bones[i].parent < -1) return false;
            if (string.IsNullOrEmpty(entry.source) || !File.Exists(entry.source)) return true;
            return AssetDatabase.GetAssetDependencyHash(entry.source).ToString() == entry.hash;
        }

        // The project file the original's body mesh comes from, or null (a temporary import, a scene mesh).
        private static string ModelFile(CustomBaseOriginal original)
        {
            var mesh = original?.Body != null ? original.Body.sharedMesh : null;
            string path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : null;
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) ? path : null;
        }

        private static void Forget(string key)
        {
            var data = Load();
            if (data.entries.RemoveAll(e => e.key == key) > 0) Save(data);
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
            try
            {
                var top = armature.root.transform;
                top.SetPositionAndRotation(body.position, body.rotation);
                top.localScale = body.lossyScale;
                var made = new Transform[bones.Count];
                for (int i = 0; i < bones.Count; i++)
                {
                    var bone = bones[i];
                    if (bone.parent >= i) throw new InvalidDataException("a bone comes before its parent");
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
            catch
            {
                armature.Dispose();
                throw;
            }
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new Vector3(b.x != 0f ? a.x / b.x : 1f, b.y != 0f ? a.y / b.y : 1f, b.z != 0f ? a.z / b.z : 1f);

        private static Cache Load()
        {
            if (cache != null) return cache;
            try { cache = File.Exists(CachePath) ? JsonUtility.FromJson<Cache>(File.ReadAllText(CachePath)) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { cache = null; }
            if (cache != null && (cache.format != Format || cache.entries == null)) cache = null;
            return cache ??= new Cache { format = Format };
        }

        private static void Save(Cache data)
        {
            try
            {
                data.format = Format;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, JsonUtility.ToJson(data));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
