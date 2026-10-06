using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.BlendShapes
{
    /// <summary>
    /// Build copies: the blendshapes nothing uses take most of a sculpted body's size (several hundred MB, over VRChat's
    /// upload limits). A renderer keeps the shapes its avatar uses; the others leave its mesh, baked in at their weight.
    /// </summary>
    public static class BlendShapePruning
    {
        // Standard MMD morph names: dance worlds animate these by name on the "Body" renderer. Compared normalized.
        private static readonly HashSet<string> MmdMorphs = new HashSet<string>(new[]
        {
            "まばたき", "笑い", "ウィンク", "ウィンク右", "なごみ", "はぅ", "びっくり", "じと目", "キリッ", "はちゅ目", "星目", "はぁと",
            "瞳小", "瞳大", "瞳縦潰れ", "光下", "恐ろしい子!", "ハイライト消", "映り込み消", "喜び", "わぉ?!", "なごみω", "悲しむ", "敵意",
            "白目", "照れ", "涙", "がーん", "青ざめ", "あ", "い", "う", "え", "お", "ん", "▲", "∧", "□", "ワ", "ω", "ω□",
            "にやり", "にっこり", "ぺろっ", "てへぺろ", "口角上げ", "口角下げ", "口横広げ", "歯無し上", "歯無し下", "はんっ!", "えー",
            "真面目", "困る", "にこり", "怒り", "上", "下", "前", "眉頭左", "眉頭右",
        }.Select(Normalize));

        /// <summary>
        /// Blendshapes the built avatar uses on this renderer: animated by one of its controllers (playable layers,
        /// Animator components), driven by the descriptor (visemes, jaw flap, eyelids), or a standard MMD morph on Body.
        /// Call it once every build step that adds animations has run.
        /// </summary>
        public static HashSet<string> Used(GameObject avatarRoot, SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var used = new HashSet<string>();
            string path = UnityEditor.AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot.transform);
            foreach (var clip in Controllers(avatarRoot).SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
                foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
                    if (binding.path == path && binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        used.Add(binding.propertyName.Substring("blendShape.".Length));
            foreach (var descriptor in avatarRoot.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                if (descriptor.VisemeSkinnedMesh == renderer)
                {
                    if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && descriptor.VisemeBlendShapes != null)
                        used.UnionWith(descriptor.VisemeBlendShapes.Where(n => !string.IsNullOrEmpty(n)));
                    if (descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && !string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName))
                        used.Add(descriptor.MouthOpenBlendShapeName);
                }
                var eyes = descriptor.customEyeLookSettings;
                if (descriptor.enableEyeLook && eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eyes.eyelidsSkinnedMesh == renderer && eyes.eyelidsBlendshapes != null)
                    foreach (int index in eyes.eyelidsBlendshapes)
                        if (index >= 0 && index < mesh.blendShapeCount) used.Add(mesh.GetBlendShapeName(index));
            }
            if (path == "Body")
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    if (MmdMorphs.Contains(Normalize(mesh.GetBlendShapeName(i)))) used.Add(mesh.GetBlendShapeName(i));
            return used;
        }

        /// <summary>
        /// Gives the renderer a copy of its mesh without the blendshapes <see cref="Used"/> leaves out; one left at a
        /// non-zero weight is baked into the copy at that weight. Descriptor eyelid indices follow. Returns the copy (in
        /// memory, for the build to keep), or null when every blendshape is used.
        /// </summary>
        public static Mesh Prune(GameObject avatarRoot, SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null || mesh.blendShapeCount == 0 || !mesh.isReadable) return null;
            var used = Used(avatarRoot, renderer);
            int count = mesh.blendShapeCount;
            var keep = new bool[count];
            for (int i = 0; i < count; i++) keep[i] = used.Contains(mesh.GetBlendShapeName(i));
            if (keep.All(k => k)) return null;
            var weights = Enumerable.Range(0, count).Select(renderer.GetBlendShapeWeight).ToArray();

            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.ClearBlendShapes();
            int n = mesh.vertexCount;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            bool hasNormals = normals.Length == n, hasTangents = tangents.Length == n, baked = false;
            var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
            var newIndex = new int[count];
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                newIndex[i] = keep[i] ? kept++ : -1;
                if (keep[i])
                {
                    string name = mesh.GetBlendShapeName(i);
                    for (int f = 0; f < mesh.GetBlendShapeFrameCount(i); f++)
                    {
                        mesh.GetBlendShapeFrameVertices(i, f, dv, dn, dt);
                        copy.AddBlendShapeFrame(name, mesh.GetBlendShapeFrameWeight(i, f), dv, dn, dt);
                    }
                }
                else if (weights[i] != 0)
                {
                    Bake(mesh, i, weights[i], dv, dn, dt, vertices, hasNormals ? normals : null, hasTangents ? tangents : null);
                    baked = true;
                }
            }
            if (baked)
            {
                copy.vertices = vertices;
                if (hasNormals) copy.normals = normals.Select(v => v.sqrMagnitude > 0 ? v.normalized : v).ToArray();
                if (hasTangents) copy.tangents = tangents;
                copy.RecalculateBounds();
            }
            renderer.sharedMesh = copy;
            for (int i = 0; i < count; i++)
                if (keep[i]) renderer.SetBlendShapeWeight(newIndex[i], weights[i]);
            foreach (var descriptor in avatarRoot.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                var eyes = descriptor.customEyeLookSettings;
                if (eyes.eyelidsSkinnedMesh != renderer || eyes.eyelidsBlendshapes == null) continue;
                eyes.eyelidsBlendshapes = eyes.eyelidsBlendshapes.Select(index => index >= 0 && index < count ? newIndex[index] : index).ToArray();
                descriptor.customEyeLookSettings = eyes;
            }
            return copy;
        }

        // A shape's offset at a weight, as Unity skins it: interpolated between frames, from zero below the first.
        private static void Bake(Mesh mesh, int shape, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt,
            Vector3[] vertices, Vector3[] normals, Vector4[] tangents)
        {
            int frames = mesh.GetBlendShapeFrameCount(shape);
            int upper = 0;
            while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
            float upperWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
            float lowerWeight = upper == 0 ? 0 : mesh.GetBlendShapeFrameWeight(shape, upper - 1);
            float t = upperWeight == lowerWeight ? 1 : (weight - lowerWeight) / (upperWeight - lowerWeight);
            Add(mesh, shape, upper, t, dv, dn, dt, vertices, normals, tangents);
            if (upper > 0) Add(mesh, shape, upper - 1, 1 - t, dv, dn, dt, vertices, normals, tangents);
        }

        private static void Add(Mesh mesh, int shape, int frame, float scale, Vector3[] dv, Vector3[] dn, Vector3[] dt,
            Vector3[] vertices, Vector3[] normals, Vector4[] tangents)
        {
            if (scale == 0) return;
            mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
            for (int v = 0; v < vertices.Length; v++)
            {
                vertices[v] += dv[v] * scale;
                if (normals != null) normals[v] += dn[v] * scale;
                if (tangents != null) tangents[v] += (Vector4)(dt[v] * scale);
            }
        }

        private static IEnumerable<RuntimeAnimatorController> Controllers(GameObject avatarRoot)
        {
            foreach (var descriptor in avatarRoot.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                    if (!layer.isDefault && layer.animatorController != null) yield return layer.animatorController;
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null) yield return animator.runtimeAnimatorController;
        }

        // Full- and half-width forms, case and the "2" of alternate morphs ("ウィンク２") do not matter.
        private static string Normalize(string name) => (name ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant()
            .Replace("2", "").Replace(" ", "").Replace("_", "");
    }
}
