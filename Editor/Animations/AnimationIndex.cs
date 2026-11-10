using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// What an avatar's clips animate, read once and kept: each clip's curve bindings (float and object curves), and every
    /// binding by the path from the avatar's root of what it animates (a nested Animator's clips start at its own object).
    /// <c>AnimationUtility.GetCurveBindings</c> makes a new array per call, and a VRCFury-built avatar has thousands of
    /// clips: build steps asking "what animates this renderer" each read them all again.
    /// The index stays right as clips change: edits made through <see cref="SetCurves"/> and <see cref="SetObjectCurve"/>
    /// are seen at once; a clip Unity marks changed (its dirty count, which every curve edit raises) is read again when it
    /// is next asked for; <see cref="Sync"/> lists the avatar's clips again (a step that copied a controller plays other
    /// clips) and re-reads the changed ones. <see cref="Changed"/> forces a re-read of one clip.
    /// </summary>
    public sealed class AnimationIndex
    {
        /// <summary>One curve of a clip, as the avatar plays it.</summary>
        public readonly struct Binding
        {
            public readonly AnimationClip Clip;
            /// <summary>The object the clip's paths start from (the avatar's, or a nested Animator's).</summary>
            public readonly Transform AnimatorRoot;
            public readonly EditorCurveBinding Value;
            public readonly bool IsObject;
            /// <summary>Its place in <see cref="ClipBindings.Floats"/> or <see cref="ClipBindings.Objects"/>.</summary>
            public readonly int Index;

            internal Binding(AnimationClip clip, Transform animatorRoot, EditorCurveBinding value, bool isObject, int index)
            {
                Clip = clip; AnimatorRoot = animatorRoot; Value = value; IsObject = isObject; Index = index;
            }
        }

        /// <summary>A clip's bindings as last read, with what was asked of its curves since.</summary>
        public sealed class ClipBindings
        {
            public AnimationClip Clip { get; }
            public EditorCurveBinding[] Floats { get; private set; }
            public EditorCurveBinding[] Objects { get; private set; }
            internal int Dirty;
            internal bool Stale;
            private Dictionary<(string Path, Type Type, string Property), int> floats;
            // Each float curve's lowest and highest key value, read when first asked; NaN: no curve.
            private Vector2[] ranges;
            private bool[] ranged;

            internal ClipBindings(AnimationClip clip) => Clip = clip;

            internal void Read()
            {
                Floats = AnimationUtility.GetCurveBindings(Clip);
                Objects = AnimationUtility.GetObjectReferenceCurveBindings(Clip);
                Dirty = EditorUtility.GetDirtyCount(Clip);
                Stale = false;
                floats = null;
                ranges = null;
                ranged = null;
            }

            /// <summary>The float curve binding of exactly this path, component type and property, when the clip has one.</summary>
            public bool TryFind(string path, Type type, string property, out EditorCurveBinding binding)
            {
                if (floats == null)
                {
                    floats = new Dictionary<(string, Type, string), int>(Floats.Length);
                    for (int i = 0; i < Floats.Length; i++) floats[(Floats[i].path, Floats[i].type, Floats[i].propertyName)] = i;
                }
                bool found = floats.TryGetValue((path, type, property), out int index);
                binding = found ? Floats[index] : default;
                return found;
            }

            /// <summary>
            /// The lowest and highest key value of the float curve at <paramref name="index"/>; false when the clip has no
            /// such curve. A curve without keys gives +∞ and −∞.
            /// </summary>
            public bool Range(int index, out float min, out float max)
            {
                if (ranges == null)
                {
                    ranges = new Vector2[Floats.Length];
                    ranged = new bool[Floats.Length];
                }
                if (!ranged[index])
                {
                    var curve = AnimationUtility.GetEditorCurve(Clip, Floats[index]);
                    var range = new Vector2(float.NaN, float.NaN);
                    if (curve != null)
                    {
                        range = new Vector2(float.PositiveInfinity, float.NegativeInfinity);
                        foreach (var key in curve.keys)
                        {
                            if (key.value < range.x) range.x = key.value;
                            if (key.value > range.y) range.y = key.value;
                        }
                    }
                    ranges[index] = range;
                    ranged[index] = true;
                }
                min = ranges[index].x;
                max = ranges[index].y;
                return !float.IsNaN(min);
            }
        }

        private static readonly IReadOnlyList<Binding> None = Array.Empty<Binding>();

        private readonly Transform root;
        private readonly Func<IEnumerable<(AnimationClip Clip, Transform AnimatorRoot)>> list;
        private readonly Dictionary<AnimationClip, ClipBindings> cache = new Dictionary<AnimationClip, ClipBindings>();
        private readonly List<(AnimationClip Clip, Transform AnimatorRoot)> played = new List<(AnimationClip, Transform)>();
        // Every binding of the played clips by the path from the root of what it animates, and the paths each clip is in.
        private readonly Dictionary<string, List<Binding>> byPath = new Dictionary<string, List<Binding>>(StringComparer.Ordinal);
        private readonly Dictionary<AnimationClip, HashSet<string>> pathsOf = new Dictionary<AnimationClip, HashSet<string>>();
        // Each animator root's path from the root when the paths were filed: one that moved files its clips again.
        private readonly Dictionary<Transform, string> prefixes = new Dictionary<Transform, string>();
        // Clips changed through the index since it last filed them.
        private readonly HashSet<AnimationClip> pending = new HashSet<AnimationClip>();
        private bool filed;

        /// <summary>
        /// An index of the clips <paramref name="clips"/> lists (each with the object its paths start from), which it calls
        /// again on each <see cref="Sync"/>; paths are from <paramref name="root"/>.
        /// </summary>
        public AnimationIndex(Transform root, Func<IEnumerable<(AnimationClip Clip, Transform AnimatorRoot)>> clips)
        {
            this.root = root;
            list = clips;
            Sync();
        }

        /// <summary>Raised by every change seen (a clip read again, the clip list changed): derived answers are out of date.</summary>
        public int Version { get; private set; }

        /// <summary>The clips the avatar plays as of the last <see cref="Sync"/>, each with the object its paths start from.</summary>
        public IReadOnlyList<(AnimationClip Clip, Transform AnimatorRoot)> Clips => played;

        /// <summary>
        /// A clip's bindings, read once and again only after it changed. Any clip may be asked for, played by the avatar or
        /// not (a copy a step is making).
        /// </summary>
        public ClipBindings Bindings(AnimationClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (!cache.TryGetValue(clip, out var bindings))
            {
                bindings = new ClipBindings(clip);
                bindings.Read();
                cache[clip] = bindings;
                return bindings;
            }
            if (bindings.Stale || bindings.Dirty != EditorUtility.GetDirtyCount(clip))
            {
                bindings.Read();
                Version++;
                if (filed && pathsOf.ContainsKey(clip)) Refile(clip);
            }
            return bindings;
        }

        /// <summary>
        /// Lists the avatar's clips again and reads again those that changed. Call it when steps that don't go through this
        /// index may have changed the avatar's animators (another tool's build step ran).
        /// </summary>
        public void Sync()
        {
            var now = new List<(AnimationClip, Transform)>();
            var seen = new HashSet<(AnimationClip, Transform)>();
            foreach (var entry in list())
                if (entry.Clip != null && seen.Add(entry)) now.Add(entry);
            bool same = now.Count == played.Count;
            for (int i = 0; same && i < now.Count; i++) same = now[i] == played[i];
            if (!same)
            {
                played.Clear();
                played.AddRange(now);
                filed = false;
                Version++;
            }
            foreach (var gone in new List<AnimationClip>(cache.Keys))
                if (gone == null) cache.Remove(gone);
            foreach (var (clip, _) in played) Bindings(clip);
            if (filed)
                foreach (var pair in prefixes)
                    if (pair.Key == null || PrefixOf(pair.Key) != pair.Value)
                    {
                        filed = false;
                        Version++;
                        break;
                    }
        }

        /// <summary>
        /// Every curve that animates the object at <paramref name="pathFromRoot"/>, in the order the clips are listed: as of
        /// the last <see cref="Sync"/>, with the edits made through the index since.
        /// </summary>
        public IReadOnlyList<Binding> At(string pathFromRoot)
        {
            File();
            if (pending.Count > 0)
            {
                foreach (var clip in pending) if (clip != null) Bindings(clip);
                pending.Clear();
            }
            return byPath.TryGetValue(pathFromRoot ?? "", out var bindings) ? bindings : None;
        }

        /// <summary>The path from the root of what a clip played from <paramref name="animatorRoot"/> binds to.</summary>
        public string PathFromRoot(Transform animatorRoot, string bindingPath)
        {
            string prefix = animatorRoot != null && prefixes.TryGetValue(animatorRoot, out var known) ? known : PrefixOf(animatorRoot);
            if (prefix == null) return bindingPath;
            return string.IsNullOrEmpty(bindingPath) ? prefix : prefix + "/" + bindingPath;
        }

        /// <summary>Sets curves of a clip at once (null removes one) and keeps the index right.</summary>
        public void SetCurves(AnimationClip clip, EditorCurveBinding[] bindings, AnimationCurve[] curves)
        {
            if (bindings.Length == 0) return;
            AnimationUtility.SetEditorCurves(clip, bindings, curves);
            Changed(clip);
        }

        /// <summary>Sets one object reference curve of a clip (null removes it) and keeps the index right.</summary>
        public void SetObjectCurve(AnimationClip clip, EditorCurveBinding binding, ObjectReferenceKeyframe[] keys)
        {
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
            Changed(clip);
        }

        /// <summary>A clip changed behind the index's back: it is read again when next asked for.</summary>
        public void Changed(AnimationClip clip)
        {
            if (clip == null || !cache.TryGetValue(clip, out var bindings)) return;
            bindings.Stale = true;
            pending.Add(clip);
        }

        // Null for the root itself (its clips' paths are already from the root).
        private string PrefixOf(Transform animatorRoot)
        {
            if (animatorRoot == null || root == null || animatorRoot == root) return null;
            return AnimationUtility.CalculateTransformPath(animatorRoot, root);
        }

        private void File()
        {
            if (filed) return;
            byPath.Clear();
            pathsOf.Clear();
            prefixes.Clear();
            pending.Clear();
            foreach (var (clip, animatorRoot) in played)
            {
                if (animatorRoot != null && !prefixes.ContainsKey(animatorRoot)) prefixes[animatorRoot] = PrefixOf(animatorRoot);
                Add(clip, animatorRoot);
            }
            filed = true;
        }

        private void Refile(AnimationClip clip)
        {
            if (pathsOf.TryGetValue(clip, out var paths))
                foreach (var path in paths)
                    if (byPath.TryGetValue(path, out var bindings)) bindings.RemoveAll(b => b.Clip == clip);
            pathsOf.Remove(clip);
            foreach (var (playedClip, animatorRoot) in played)
                if (playedClip == clip) Add(clip, animatorRoot);
        }

        private void Add(AnimationClip clip, Transform animatorRoot)
        {
            var bindings = cache.TryGetValue(clip, out var known) && !known.Stale ? known : null;
            if (bindings == null)
            {
                bindings = known ?? new ClipBindings(clip);
                bindings.Read();
                cache[clip] = bindings;
            }
            if (!pathsOf.TryGetValue(clip, out var paths)) pathsOf[clip] = paths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bindings.Floats.Length; i++) File(new Binding(clip, animatorRoot, bindings.Floats[i], false, i), paths);
            for (int i = 0; i < bindings.Objects.Length; i++) File(new Binding(clip, animatorRoot, bindings.Objects[i], true, i), paths);
        }

        private void File(Binding binding, HashSet<string> paths)
        {
            string path = PathFromRoot(binding.AnimatorRoot, binding.Value.path);
            if (!byPath.TryGetValue(path, out var bindings)) byPath[path] = bindings = new List<Binding>();
            bindings.Add(binding);
            paths.Add(path);
        }
    }
}
