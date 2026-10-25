using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Merges a build copy's meshes into fewer skinned meshes, one per group of renderers drawn the same way (shadows,
    /// probes, quality, layer): VRChat ranks meshes, and each one costs its own draw and skinning. Nothing an animation
    /// refers to moves: the merged renderer is the one animations or components use (its path, blendshapes and material
    /// slots stay first), and only renderers nothing else animates or points at join it.
    /// <list type="bullet">
    /// <item><see cref="MergeStill"/>: meshes always shown, static props (mesh renderers) too, skinned to their own transform.
    /// Their blendshapes, which nothing animates, are baked in at their weight.</item>
    /// <item><see cref="MergeTogether"/>: meshes every animation shows and hides together (an outfit's pieces under one
    /// toggle). Their animated blendshapes join the merged mesh, and the animations follow them there.</item>
    /// </list>
    /// </summary>
    public static class MeshMerge
    {
        public sealed class Result
        {
            /// <summary>The meshes made (in memory, for the build to keep).</summary>
            public readonly List<Mesh> Meshes = new List<Mesh>();
            /// <summary>How many renderers joined another.</summary>
            public int Merged;
        }

        public static Result MergeStill(AvatarUsage usage)
        {
            var result = new Result();
            // What a build tool still works on stays as it is, merged into nothing and taking nothing in.
            var skinned = usage.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => usage.AlwaysShown(r) && Mergeable(r) && !Emits(r, usage) && !usage.Pending(r.transform)).ToList();
            var props = usage.Root.GetComponentsInChildren<MeshRenderer>(true).Where(r => usage.AlwaysShown(r) && Prop(r) && Movable(r, usage)).ToList();
            // The largest groups first: a prop drawn like several of them joins the one with the most to share.
            foreach (var group in skinned.GroupBy(r => (Drawn(r), Skinning(r))).OrderByDescending(g => g.Sum(r => r.sharedMesh.vertexCount)))
            {
                var members = group.ToList();
                var joining = props.Where(p => Drawn(p).Equals(group.Key.Item1)).ToList();
                if (members.Count + joining.Count < 2) continue;
                props.RemoveAll(joining.Contains);
                // Renderers something animates or points at stay where they are; one of them can take the others in.
                var pinned = members.Where(r => !Movable(r, usage)).ToList();
                var sources = members.Except(pinned).Cast<Renderer>().ToList();
                var target = pinned.OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                if (target == null)
                {
                    target = members.Except(pinned).OrderByDescending(r => r.sharedMesh.vertexCount).First();
                    sources.Remove(target);
                }
                sources.AddRange(joining);
                // Blendshapes of the joining meshes are baked first; one whose shapes something still uses stays apart.
                sources = sources.Where(s => !(s is SkinnedMeshRenderer source) || source.sharedMesh.blendShapeCount == 0 || Bake(usage, source, result))
                    .Where(s => !Crosses(target, s, usage)).ToList();
                if (sources.Count == 0) continue;
                var merged = Merge(target, sources, false);
                if (merged == null) continue;
                result.Meshes.Add(merged.Mesh);
                result.Merged += sources.Count;
                foreach (var source in sources) Remove(source, target, usage);
            }
            return result;
        }

        /// <summary>
        /// Merges the skinned meshes every animation shows and hides together: the same toggles (their objects' or their
        /// own), with the same keys in every clip and the same state at rest. The merged renderer stays under its own
        /// toggles, which do the same to it as to the others. <paramref name="build"/> holds the build's copies of the
        /// animations: the joining meshes' blendshape animations are moved onto the merged one there. Without it (an
        /// estimate on a copy nothing plays) only meshes no animation reshapes join.
        /// </summary>
        public static Result MergeTogether(AvatarUsage usage, AttachmentAnimationBuild build)
        {
            var result = new Result();
            var root = usage.Root.transform;
            var visibility = Visibility(usage);
            var candidates = usage.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => Mergeable(r) && Toggleable(r, usage) && !Emits(r, usage)).ToList();
            foreach (var group in candidates.GroupBy(r => (Drawn(r), Skinning(r), Signature(r, usage, visibility))).Where(g => g.Count() > 1))
            {
                var members = group.ToList();
                // Joining: nothing points at it, its materials aren't swapped, only its toggles and blendshapes are animated.
                bool Joins(SkinnedMeshRenderer r) => !usage.Used(r) && !usage.Used(r.gameObject) && !usage.SwapsMaterials(r) &&
                    usage.AnimatedAt(r.transform).All(p => p == "m_IsActive" || p == "m_Enabled" || build != null && p.StartsWith("blendShape.", StringComparison.Ordinal)) &&
                    (build != null || !usage.AnimatedAt(r.transform).Any(p => p.StartsWith("blendShape.", StringComparison.Ordinal)));
                var target = members.Where(r => !Joins(r)).OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault()
                             ?? members.OrderByDescending(r => r.sharedMesh.vertexCount).First();
                var sources = members.Where(r => r != target && Joins(r)).Where(s => !Crosses(target, s, usage)).ToList();
                // Their unused blendshapes go first (baked); the ones animations use join the merged mesh.
                foreach (var source in sources.Where(s => s.sharedMesh.blendShapeCount > 0))
                {
                    var copy = BlendShapePruning.Prune(usage.Root, source);
                    if (copy != null) result.Meshes.Add(copy);
                }
                if (sources.Count == 0) continue;
                var merged = Merge(target, sources.Cast<Renderer>().ToList(), true);
                if (merged == null) continue;
                result.Meshes.Add(merged.Mesh);
                result.Merged += sources.Count;
                // The joining renderers go even when their animations could not follow: never drawn twice.
                try { if (build != null) MoveShapeAnimations(build, target, merged.Shapes); }
                finally { foreach (var source in sources) Remove(source, target, usage); }
            }
            return result;
        }

        // ---- Who can merge ----

        // A particle system emitting from a renderer's mesh would emit from all the merged geometry: it stays as it is.
        private static bool Emits(Renderer renderer, AvatarUsage usage) =>
            usage.ReferencesTo(renderer).Any(r => r.From is ParticleSystem) ||
            renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && usage.ReferencesTo(filter).Any(r => r.From is ParticleSystem);

        // A skinned mesh whose every vertex follows its bones, drawn as triangles, one material per submesh.
        private static bool Mergeable(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            if (mesh == null || bones.Length == 0 || mesh.bindposes.Length != bones.Length) return false;
            if (renderer.sharedMaterials.Length != mesh.subMeshCount || renderer.GetComponent<Cloth>() != null) return false;
            if (Enumerable.Range(0, mesh.subMeshCount).Any(i => mesh.GetTopology(i) != MeshTopology.Triangles)) return false;
            var counts = mesh.GetBonesPerVertex();
            if (counts.Length != mesh.vertexCount) return false;
            for (int v = 0; v < counts.Length; v++) if (counts[v] == 0) return false;
            return true;
        }

        // A plain static mesh: triangles, one material per submesh, no blendshapes or extra vertex streams.
        private static bool Prop(MeshRenderer renderer)
        {
            var mesh = MeshOptimization.MeshOf(renderer);
            if (mesh == null || mesh.vertexCount == 0 || mesh.blendShapeCount > 0 || renderer.additionalVertexStreams != null) return false;
            if (renderer.sharedMaterials.Length != mesh.subMeshCount) return false;
            return Enumerable.Range(0, mesh.subMeshCount).All(i => mesh.GetTopology(i) == MeshTopology.Triangles);
        }

        // Nothing animates it and nothing points at it (the descriptor's visemes, a constraint, a contact, a build tool's
        // toggle naming its object...).
        private static bool Movable(Renderer renderer, AvatarUsage usage) =>
            !usage.Animated(renderer.transform) && !usage.Used(renderer) && !usage.Used(renderer.gameObject) && !usage.Pending(renderer.transform) &&
            (!(renderer is MeshRenderer) || !renderer.TryGetComponent<MeshFilter>(out var filter) || !usage.Used(filter));

        // Toggled or switched, every object above it that no animation shows is on, nothing builds on it later.
        private static bool Toggleable(SkinnedMeshRenderer renderer, AvatarUsage usage)
        {
            if (usage.Pending(renderer.transform) || !usage.Toggled(renderer.transform) && !usage.Switched(renderer)) return false;
            for (var t = renderer.transform; t != null && t != usage.Root.transform; t = t.parent)
                if (t.CompareTag("EditorOnly") || !t.gameObject.activeSelf && !usage.CanTurnOn(t.gameObject)) return false;
            return renderer.enabled || usage.CanTurnOn(renderer);
        }

        private static (ShadowCastingMode, bool, LightProbeUsage, ReflectionProbeUsage, Transform, int, uint, int) Drawn(Renderer r) =>
            (r.shadowCastingMode, r.receiveShadows, r.lightProbeUsage, r.reflectionProbeUsage, r.probeAnchor, r.gameObject.layer, r.renderingLayerMask, r.sortingOrder);

        private static (bool, bool, SkinQuality) Skinning(SkinnedMeshRenderer r) => (r.skinnedMotionVectors, r.updateWhenOffscreen, r.quality);

        // What an animation sets on a renderer reaches everything it draws: a material property every material with that
        // property, its own settings (shadows, probes, sorting, bounds) the whole renderer. A mesh whose materials have a
        // property the merged renderer's animations set stays apart, and every mesh does when they change its settings.
        // Blendshapes, swapped slots and its switch stay its own.
        private static bool Crosses(Renderer target, Renderer source, AvatarUsage usage)
        {
            var animated = usage.AnimatedAt(target.transform, target.GetType());
            if (animated.Any(p => p != "m_Enabled" && !p.StartsWith("blendShape.", StringComparison.Ordinal) && !p.StartsWith("material.", StringComparison.Ordinal) &&
                                  !p.StartsWith("m_Materials", StringComparison.Ordinal))) return true;
            var properties = animated.Where(p => p.StartsWith("material.", StringComparison.Ordinal))
                .Select(p => p.Substring("material.".Length).Split('.')[0]).Distinct().ToList();
            return properties.Count > 0 && source.sharedMaterials.Any(m => m != null && properties.Any(m.HasProperty));
        }

        private static bool Bake(AvatarUsage usage, SkinnedMeshRenderer source, Result result)
        {
            if (BlendShapePruning.Used(usage.Root, source).Count > 0) return false;
            var copy = BlendShapePruning.Prune(usage.Root, source);
            if (copy != null) result.Meshes.Add(copy);
            return source.sharedMesh.blendShapeCount == 0;
        }

        // The renderer leaves (a prop its mesh filter too); its object as well when nothing else is on it, below it or
        // pointing at it, and the merged mesh doesn't follow it.
        private static void Remove(Renderer source, SkinnedMeshRenderer target, AvatarUsage usage)
        {
            var go = source.gameObject;
            bool prop = source is MeshRenderer;
            Object.DestroyImmediate(source);
            if (prop && go.TryGetComponent<MeshFilter>(out var filter)) Object.DestroyImmediate(filter);
            bool alone = go.transform.childCount == 0 && go.GetComponents<Component>().All(c => c is Transform) && !usage.Used(go) &&
                         !usage.Used(go.transform) && !usage.Animated(go.transform) && go != usage.Root && !target.bones.Contains(go.transform);
            if (alone) Object.DestroyImmediate(go);
        }

        // ---- Toggles ----

        // For each toggle (an object's m_IsActive, a renderer's m_Enabled), its keys in every clip in a fixed order: two
        // toggles with the same fingerprint and the same state at rest are on and off at the same moments.
        private static Dictionary<(string Path, string Property), string> Visibility(AvatarUsage usage)
        {
            var clips = AvatarUsage.Clips(usage.Root).ToList();
            var keys = new Dictionary<(string, string), StringBuilder>();
            for (int i = 0; i < clips.Count; i++)
                foreach (var binding in AnimationUtility.GetCurveBindings(clips[i].Clip))
                {
                    bool active = binding.propertyName == "m_IsActive" && binding.type == typeof(GameObject);
                    bool enabled = binding.propertyName == "m_Enabled" && binding.type.IsAssignableFrom(typeof(SkinnedMeshRenderer));
                    if (!active && !enabled) continue;
                    var curve = AnimationUtility.GetEditorCurve(clips[i].Clip, binding);
                    if (curve == null) continue;
                    var key = (usage.PathOf(clips[i].AnimatorRoot, binding.path), binding.propertyName);
                    if (!keys.TryGetValue(key, out var text)) keys[key] = text = new StringBuilder();
                    text.Append(i).Append('[');
                    foreach (var k in curve.keys) text.Append(k.time.ToString("R")).Append(':').Append(k.value > .5f ? '1' : '0').Append(' ');
                    text.Append(']');
                }
            return keys.ToDictionary(p => p.Key, p => p.Value.ToString());
        }

        private static string Signature(SkinnedMeshRenderer renderer, AvatarUsage usage, Dictionary<(string Path, string Property), string> visibility)
        {
            var parts = new List<string>();
            for (var t = renderer.transform; t != null && t != usage.Root.transform; t = t.parent)
                if (visibility.TryGetValue((usage.PathOf(t), "m_IsActive"), out var keys)) parts.Add("A" + (t.gameObject.activeSelf ? 1 : 0) + keys);
            if (visibility.TryGetValue((usage.PathOf(renderer.transform), "m_Enabled"), out var own)) parts.Add("E" + (renderer.enabled ? 1 : 0) + own);
            else if (!renderer.enabled) parts.Add("E0");
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        // The joining meshes' blendshape curves, onto the merged renderer under the shapes' names there.
        private static void MoveShapeAnimations(AttachmentAnimationBuild build, SkinnedMeshRenderer target, List<(Transform Source, Dictionary<string, string> Names)> shapes)
        {
            using var edits = build.Edits();
            foreach (var (clip, animatorRoot) in build.Clips)
            {
                if (!target.transform.IsChildOf(animatorRoot)) continue;
                string to = AnimationUtility.CalculateTransformPath(target.transform, animatorRoot);
                var removed = new List<EditorCurveBinding>();
                var added = new List<EditorCurveBinding>();
                var curves = new List<AnimationCurve>();
                foreach (var (source, names) in shapes)
                {
                    if (source == null || !source.IsChildOf(animatorRoot) || names.Count == 0) continue;
                    string from = AnimationUtility.CalculateTransformPath(source, animatorRoot);
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (binding.path != from || binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                        if (!names.TryGetValue(binding.propertyName.Substring("blendShape.".Length), out var name)) continue;
                        removed.Add(binding);
                        added.Add(EditorCurveBinding.FloatCurve(to, typeof(SkinnedMeshRenderer), "blendShape." + name));
                        curves.Add(AnimationUtility.GetEditorCurve(clip, binding));
                    }
                }
                if (removed.Count == 0) continue;
                AnimationUtility.SetEditorCurves(clip, removed.ToArray(), new AnimationCurve[removed.Count]);
                AnimationUtility.SetEditorCurves(clip, added.ToArray(), curves.ToArray());
                EditorUtility.SetDirty(clip);
            }
        }

        // ---- The merge ----

        private sealed class Part
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public Transform[] Bones;
            public Matrix4x4[] Poses;
            public int Offset;
            /// <summary>Maps the part's mesh space into the merged one (identity for the target).</summary>
            public Matrix4x4 Space = Matrix4x4.identity;
            public int[] BoneMap;
            /// <summary>Whether Unity drew the part mirrored and draws the merged renderer not, or the other way round.</summary>
            public bool Flip;
        }

        private sealed class Merged
        {
            public Mesh Mesh;
            /// <summary>Each joining mesh's blendshapes kept, by their name in it and in the merged mesh.</summary>
            public readonly List<(Transform Source, Dictionary<string, string> Names)> Shapes = new List<(Transform, Dictionary<string, string>)>();
        }

        // A skinned part brings its bones; a prop is skinned whole to its own transform, in its own space (bind pose identity).
        private static Part PartOf(Renderer renderer) => renderer is SkinnedMeshRenderer skinned
            ? new Part { Renderer = renderer, Mesh = skinned.sharedMesh, Bones = skinned.bones, Poses = skinned.sharedMesh.bindposes }
            : new Part { Renderer = renderer, Mesh = MeshOptimization.MeshOf(renderer), Bones = new[] { renderer.transform }, Poses = new[] { Matrix4x4.identity } };

        private static Merged Merge(SkinnedMeshRenderer target, List<Renderer> sources, bool keepShapes)
        {
            var result = new Merged();
            var bones = target.bones.ToList();
            var poses = target.sharedMesh.bindposes.ToList();
            var parts = new List<Part> { PartOf(target) };
            parts[0].BoneMap = Enumerable.Range(0, bones.Count).ToArray();
            int vertices = target.sharedMesh.vertexCount;
            foreach (var source in sources)
            {
                var part = PartOf(source);
                part.Flip = Mirrored(source) != Mirrored(target);
                part.Offset = vertices;
                vertices += part.Mesh.vertexCount;
                Map(part, bones, poses);
                parts.Add(part);
            }

            var merged = new Mesh { name = target.sharedMesh.name, indexFormat = vertices > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            merged.SetVertices(parts.SelectMany(p => p.Mesh.vertices.Select(v => p.Space.MultiplyPoint3x4(v))).ToList());
            if (parts.Any(p => p.Mesh.normals.Length > 0)) merged.SetNormals(parts.SelectMany(Normals).ToList());
            if (parts.Any(p => p.Mesh.tangents.Length > 0)) merged.SetTangents(parts.SelectMany(Tangents).ToList());
            if (parts.Any(p => p.Mesh.colors.Length > 0))
                merged.SetColors(parts.SelectMany(p => p.Mesh.colors.Length > 0 ? p.Mesh.colors : Enumerable.Repeat(Color.white, p.Mesh.vertexCount)).ToList());
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                int dimension = parts.Max(p => p.Mesh.HasVertexAttribute(attribute) ? p.Mesh.GetVertexAttributeDimension(attribute) : 0);
                if (dimension == 0) continue;
                var uvs = new List<Vector4>(vertices);
                var buffer = new List<Vector4>();
                foreach (var part in parts)
                {
                    if (part.Mesh.HasVertexAttribute(attribute)) { part.Mesh.GetUVs(channel, buffer); uvs.AddRange(buffer); }
                    else uvs.AddRange(Enumerable.Repeat(Vector4.zero, part.Mesh.vertexCount));
                }
                if (dimension <= 2) merged.SetUVs(channel, uvs.Select(u => (Vector2)u).ToList());
                else if (dimension == 3) merged.SetUVs(channel, uvs.Select(u => (Vector3)u).ToList());
                else merged.SetUVs(channel, uvs);
            }

            // Weights, through each part's bone map; a prop's vertices all follow its one bone.
            var counts = new List<byte>(vertices);
            var weights = new List<BoneWeight1>();
            foreach (var part in parts)
            {
                if (part.Renderer is SkinnedMeshRenderer)
                {
                    counts.AddRange(part.Mesh.GetBonesPerVertex());
                    foreach (var weight in part.Mesh.GetAllBoneWeights()) weights.Add(new BoneWeight1 { boneIndex = part.BoneMap[weight.boneIndex], weight = weight.weight });
                }
                else
                {
                    counts.AddRange(Enumerable.Repeat((byte)1, part.Mesh.vertexCount));
                    weights.AddRange(Enumerable.Repeat(new BoneWeight1 { boneIndex = part.BoneMap[0], weight = 1f }, part.Mesh.vertexCount));
                }
            }
            using (var perVertex = new NativeArray<byte>(counts.ToArray(), Allocator.Temp))
            using (var all = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                merged.SetBoneWeights(perVertex, all);
            merged.bindposes = poses.ToArray();

            // The target's slots first, so animations swapping its materials keep their indices. A part drawn mirrored
            // otherwise than the merged renderer turns its triangles round, or they would face inwards.
            var materials = new List<Material>();
            var submeshes = new List<int[]>();
            foreach (var part in parts)
            {
                materials.AddRange(part.Renderer.sharedMaterials);
                for (int i = 0; i < part.Mesh.subMeshCount; i++)
                {
                    var indices = part.Mesh.GetIndices(i, true).Select(index => index + part.Offset).ToArray();
                    if (part.Flip) for (int t = 0; t + 2 < indices.Length; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
                    submeshes.Add(indices);
                }
            }
            merged.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++) merged.SetIndices(submeshes[i], MeshTopology.Triangles, i, false, 0);

            // The target's blendshapes first, under their names; the joining meshes' after it, renamed when the name is taken.
            var weightsAfter = new List<float>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var mv = new Vector3[vertices]; var mn = new Vector3[vertices]; var mt = new Vector3[vertices];
            foreach (var part in parts)
            {
                if (part.Renderer != target && !keepShapes) continue;
                var shaped = part.Mesh;
                int count = shaped.vertexCount;
                var kept = new Dictionary<string, string>(StringComparer.Ordinal);
                var dv = new Vector3[count]; var dn = new Vector3[count]; var dt = new Vector3[count];
                var skinned = part.Renderer as SkinnedMeshRenderer;
                bool same = part.Space == Matrix4x4.identity;
                // Shape offsets add to a vertex's normal and tangent before Unity skins and normalizes them: carried through
                // the part's space and scaled like the part's own (unit length again), they turn them as they did.
                var normalScale = same ? null : Lengths(shaped.normals, count, part.Space);
                var tangentScale = same ? null : Lengths(shaped.tangents.Select(t => (Vector3)t).ToArray(), count, part.Space);
                for (int shape = 0; shape < shaped.blendShapeCount; shape++)
                {
                    string name = shaped.GetBlendShapeName(shape), unique = name;
                    for (int n = 2; names.Contains(unique); n++) unique = name + " " + part.Renderer.name + (n > 2 ? " " + n : "");
                    names.Add(unique);
                    kept[name] = unique;
                    for (int frame = 0; frame < shaped.GetBlendShapeFrameCount(shape); frame++)
                    {
                        shaped.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                        if (same) { Array.Copy(dv, 0, mv, part.Offset, count); Array.Copy(dn, 0, mn, part.Offset, count); Array.Copy(dt, 0, mt, part.Offset, count); }
                        else
                            for (int v = 0; v < count; v++)
                            {
                                mv[part.Offset + v] = part.Space.MultiplyVector(dv[v]);
                                mn[part.Offset + v] = part.Space.MultiplyVector(dn[v]) * normalScale[v];
                                mt[part.Offset + v] = part.Space.MultiplyVector(dt[v]) * tangentScale[v];
                            }
                        merged.AddBlendShapeFrame(unique, shaped.GetBlendShapeFrameWeight(shape, frame), mv, mn, mt);
                    }
                    weightsAfter.Add(skinned != null ? skinned.GetBlendShapeWeight(shape) : 0f);
                }
                // The next part's shapes move nothing here.
                Array.Clear(mv, part.Offset, count); Array.Clear(mn, part.Offset, count); Array.Clear(mt, part.Offset, count);
                if (part.Renderer != target && kept.Count > 0) result.Shapes.Add((part.Renderer.transform, kept));
            }
            merged.RecalculateBounds();

            // Culling bounds around every part's own box as it stands (a hidden renderer's world bounds are empty), in the
            // target's root bone space.
            var bounds = parts.Select(p => Box(p.Renderer)).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            target.sharedMesh = merged;
            target.bones = bones.ToArray();
            target.sharedMaterials = materials.ToArray();
            for (int i = 0; i < weightsAfter.Count; i++) target.SetBlendShapeWeight(i, weightsAfter[i]);
            var space = target.rootBone != null ? target.rootBone : target.transform;
            target.localBounds = Local(bounds, space);
            result.Mesh = merged;
            return result;
        }

        // A part bound in the same rest pose as what it joins (most clothing) moves into its mesh space and shares its
        // bones; one bound otherwise keeps its space and brings its own bind poses (the same bone listed twice).
        private static void Map(Part part, List<Transform> bones, List<Matrix4x4> poses)
        {
            var own = part.Bones;
            var ownPoses = part.Poses;
            part.BoneMap = new int[own.Length];
            Matrix4x4? space = null;
            bool consistent = true;
            for (int i = 0; i < own.Length && consistent; i++)
            {
                int at = bones.IndexOf(own[i]);
                if (own[i] == null || at < 0) continue;
                // poses[at] * space == ownPoses[i]: the part's mesh space seen from the merged one.
                var candidate = poses[at].inverse * ownPoses[i];
                if (space == null) space = candidate;
                else consistent = MeshOptimization.Close(space.Value, candidate);
            }
            if (consistent && space != null)
            {
                part.Space = space.Value;
                var back = space.Value.inverse;
                for (int i = 0; i < own.Length; i++)
                {
                    int at = own[i] != null ? bones.IndexOf(own[i]) : -1;
                    if (at < 0) { at = bones.Count; bones.Add(own[i]); poses.Add(ownPoses[i] * back); }
                    part.BoneMap[i] = at;
                }
                return;
            }
            for (int i = 0; i < own.Length; i++)
            {
                part.BoneMap[i] = bones.Count;
                bones.Add(own[i]);
                poses.Add(ownPoses[i]);
            }
        }

        // Unity skins normals and tangents with the bone matrices themselves (then normalizes them): carried through the
        // part's space the same way, they come out of the merged renderer's bones as they came out of the part's.
        private static IEnumerable<Vector3> Normals(Part part)
        {
            var normals = part.Mesh.normals;
            if (normals.Length == 0) return Enumerable.Repeat(Vector3.up, part.Mesh.vertexCount);
            var space = part.Space;
            return normals.Select(n => space.MultiplyVector(n).normalized);
        }

        // Shaders turn a bitangent round on a renderer drawn mirrored: a part that changes sides turns its sign in w round.
        private static IEnumerable<Vector4> Tangents(Part part)
        {
            var tangents = part.Mesh.tangents;
            if (tangents.Length == 0) return Enumerable.Repeat(new Vector4(1, 0, 0, 1), part.Mesh.vertexCount);
            var space = part.Space;
            float flip = part.Flip ? -1f : 1f;
            return tangents.Select(t => { var v = space.MultiplyVector(t).normalized; return new Vector4(v.x, v.y, v.z, t.w * flip); });
        }

        // For each vertex, one over the length a direction gets through the space (1 without one): what makes it unit length.
        private static float[] Lengths(Vector3[] directions, int count, Matrix4x4 space)
        {
            var scale = new float[count];
            for (int v = 0; v < count; v++)
            {
                float length = v < directions.Length ? space.MultiplyVector(directions[v]).magnitude : 0f;
                scale[v] = length > 1e-12f ? 1f / length : 1f;
            }
            return scale;
        }

        // Whether Unity draws a renderer mirrored (an odd number of negative scales): it then turns its triangles round and
        // its shaders the bitangents. A skinned mesh is drawn from its root bone.
        private static bool Mirrored(Renderer renderer) =>
            (renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null ? skinned.rootBone : renderer.transform).localToWorldMatrix.determinant < 0f;

        // A renderer's own culling box in world space: a skinned mesh's around its root bone, a prop's mesh around its object.
        private static Bounds Box(Renderer renderer)
        {
            Bounds local;
            Transform space;
            if (renderer is SkinnedMeshRenderer skinned) { local = skinned.localBounds; space = skinned.rootBone != null ? skinned.rootBone : skinned.transform; }
            else { var mesh = MeshOptimization.MeshOf(renderer); local = mesh != null ? mesh.bounds : default; space = renderer.transform; }
            var box = new Bounds(space.TransformPoint(local.center), Vector3.zero);
            var e = local.extents;
            for (int i = 0; i < 8; i++)
                box.Encapsulate(space.TransformPoint(local.center + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return box;
        }

        private static Bounds Local(Bounds world, Transform space)
        {
            var local = new Bounds(space.InverseTransformPoint(world.center), Vector3.zero);
            var e = world.extents;
            for (int i = 0; i < 8; i++)
                local.Encapsulate(space.InverseTransformPoint(world.center + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return local;
        }
    }
}
