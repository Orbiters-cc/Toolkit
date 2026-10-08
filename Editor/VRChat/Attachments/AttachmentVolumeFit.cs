using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.Meshes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>Coarse body clearance after skeleton alignment, before optional ReFit shape transfer.</summary>
    public static class AttachmentVolumeFit
    {
        public static void Apply(OrbitersAttachment attachment, Transform avatarRoot, AttachmentFit.Measure measure)
        {
            var body = AttachmentPlanner.Body(avatarRoot, attachment.transform);
            if (!body || !body.sharedMesh || body.sharedMesh.vertexCount < 32) return;
            var garments = attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh && r.sharedMesh.vertexCount >= 32 && ClothingCoverage.Eligible(attachment, r) &&
                    !RefitRecords.IsApplied(r) && !attachment.fittedMeshes.Any(f => f.renderer == r)).ToArray();
            if (garments.Length == 0) return;
            var scene = EditorSceneManager.NewPreviewScene();
            Mesh surface = null;
            try
            {
                var bodyPose = new SkinPose(body);
                surface = UnityEngine.Object.Instantiate(body.sharedMesh);
                surface.vertices = bodyPose.World;
                surface.RecalculateBounds();
                var colliderObject = new GameObject("Clothing fit surface") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(colliderObject, scene);
                var collider = colliderObject.AddComponent<MeshCollider>();
                collider.sharedMesh = surface;
                float height = surface.bounds.size.y;
                float clearance = height * .009f;
                var bodyRegions = measure.Pairs.ToDictionary(p => p.target, p => Region(measure.Roles[p.source]));
                var clothingRegions = measure.Roles.ToDictionary(p => p.Key, p => Region(p.Value));
                var bodyMasks = Masks(body, bodyRegions);
                var bodyTriangles = surface.triangles;
                var triangleMasks = new int[bodyTriangles.Length / 3];
                for (int i = 0; i < triangleMasks.Length; i++)
                    triangleMasks[i] = bodyMasks[bodyTriangles[i * 3]] | bodyMasks[bodyTriangles[i * 3 + 1]] | bodyMasks[bodyTriangles[i * 3 + 2]];
                var segments = new Dictionary<Transform, (Vector3 from, Vector3 to)>();
                foreach (var pair in measure.Pairs)
                {
                    var child = measure.Pairs.Where(p => p.source != pair.source && p.source.IsChildOf(pair.source))
                        .OrderBy(p => Depth(p.source)).ThenBy(p => Mathf.Abs(pair.source.InverseTransformPoint(p.source.position).x)).FirstOrDefault();
                    if (child.source != null) segments[pair.source] = (pair.target.position, child.target.position);
                    else if (measure.Tips.TryGetValue(pair.source, out var tip)) segments[pair.source] = (pair.target.position, tip.target.position);
                }
                foreach (var renderer in garments)
                {
                    var vertices = renderer.sharedMesh.vertices;
                    var initialVertices = (Vector3[])vertices.Clone();
                    var pose = new SkinPose(renderer);
                    var origins = new Vector3[pose.World.Length];
                    var weights = renderer.sharedMesh.boneWeights;
                    if (weights.Length != origins.Length) continue;
                    var boneSegments = renderer.bones.Select(b => Segment(b, segments)).ToArray();
                    for (int i = 0; i < origins.Length; i++)
                    {
                        var w = weights[i];
                        origins[i] = Anchor(w.boneIndex0, w.weight0) + Anchor(w.boneIndex1, w.weight1) +
                                     Anchor(w.boneIndex2, w.weight2) + Anchor(w.boneIndex3, w.weight3);
                        Vector3 Anchor(int bone, float weight)
                        {
                            if (weight <= 0 || bone >= boneSegments.Length) return Vector3.zero;
                            var segment = boneSegments[bone];
                            var axis = segment.to - segment.from;
                            float t = axis.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(pose.World[i] - segment.from, axis) / axis.sqrMagnitude) : 0;
                            return (segment.from + axis * t) * weight;
                        }
                    }
                    var moved = Expand(pose.World, origins, renderer.sharedMesh.triangles, collider, height, clearance,
                        Masks(renderer, clothingRegions), triangleMasks);
                    for (int i = 0; i < vertices.Length; i++)
                        vertices[i] += pose.Matrices[i].inverse.MultiplyVector(moved[i] - pose.World[i]);
                    if (!vertices.Where((p, i) => (p - initialVertices[i]).sqrMagnitude > 1e-16f).Any()) continue;
                    var copy = UnityEngine.Object.Instantiate(renderer.sharedMesh);
                    copy.name = renderer.sharedMesh.name + "_AutoFit";
                    copy.vertices = vertices;
                    copy.RecalculateBounds();
                    // Keep authored split normals and all morphs; the smooth displacement retains the fabric detail.
                    string folder = "Assets/Orbiters/Attachments/Fits";
                    string parent = "Assets";
                    foreach (var part in folder.Split('/').Skip(1))
                    {
                        if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                        parent += "/" + part;
                    }
                    string fileName = string.Concat(copy.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + fileName + ".asset"));
                    Undo.RecordObjects(new UnityEngine.Object[] { attachment, renderer }, "Fit clothing to body volume");
                    attachment.fittedMeshes.Add(new OrbitersAttachment.FittedMesh { renderer = renderer, before = renderer.sharedMesh,
                        after = copy, beforeBounds = renderer.localBounds, afterBounds = copy.bounds });
                    renderer.sharedMesh = copy;
                    renderer.localBounds = copy.bounds;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }
                AttachmentInstaller.Dirty(attachment);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                if (surface) UnityEngine.Object.DestroyImmediate(surface);
            }
        }

        private static (Vector3 from, Vector3 to) Segment(Transform bone, Dictionary<Transform, (Vector3 from, Vector3 to)> segments)
        {
            for (var parent = bone; parent; parent = parent.parent)
                if (segments.TryGetValue(parent, out var segment)) return segment;
            return (bone ? bone.position : Vector3.zero, bone ? bone.position : Vector3.zero);
        }

        // Smooth the displacement, not the garment: seams and folds must not shrink or lose their shape.
        internal static Vector3[] Expand(Vector3[] vertices, Vector3[] origins, int[] triangles, MeshCollider body, float height, float clearance,
            int[] regions = null, int[] bodyRegions = null)
        {
            var groups = new Dictionary<Vector3Int, int>();
            var group = new int[vertices.Length];
            var positions = new List<Vector3>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var key = Vector3Int.RoundToInt(vertices[i] / (height * 1e-5f));
                if (!groups.TryGetValue(key, out int index)) { index = positions.Count; groups.Add(key, index); positions.Add(vertices[i]); }
                group[i] = index;
            }
            var neighbours = positions.Select(_ => new HashSet<int>()).ToArray();
            for (int i = 0; i < triangles.Length; i += 3)
                for (int edge = 0; edge < 3; edge++)
                {
                    int a = group[triangles[i + edge]], b = group[triangles[i + (edge + 1) % 3]];
                    if (a != b) { neighbours[a].Add(b); neighbours[b].Add(a); }
                }
            var required = new float[positions.Count];
            var directions = new Vector3[positions.Count];
            for (int i = 0; i < vertices.Length; i++)
            {
                int g = group[i];
                var radial = vertices[i] - origins[i];
                if (radial.sqrMagnitude < 1e-8f) continue;
                var direction = radial.normalized;
                directions[g] = direction;
                var start = origins[i] + direction * height;
                RaycastHit hit = default;
                bool found = false;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    float remaining = Vector3.Dot(start - origins[i], direction);
                    if (remaining <= 0 || !body.Raycast(new Ray(start, -direction), out hit, remaining)) break;
                    if (regions == null || bodyRegions == null || (regions[i] & bodyRegions[hit.triangleIndex]) != 0) { found = true; break; }
                    start = hit.point - direction * (height * .00001f);
                }
                if (!found) continue;
                float radius = Vector3.Dot(hit.point - origins[i], direction);
                // Sleeves need ease for the different rigs' joint deformation when the arms lower.
                float ease = regions != null && (regions[i] == 2 || regions[i] == 4) ? 2.3f : 1f;
                float distance = radius + clearance * ease - radial.magnitude;
                // A ray that crossed an unrelated limb or prop must not drag a panel across the body.
                if (distance > height * .12f) continue;
                required[g] = Mathf.Max(required[g], distance);
            }
            var offsets = (float[])required.Clone();
            for (int iteration = 0; iteration < 24; iteration++)
            {
                var next = new float[offsets.Length];
                for (int i = 0; i < offsets.Length; i++)
                {
                    float sum = offsets[i] * 2f, weight = 2f;
                    foreach (int neighbour in neighbours[i]) { sum += offsets[neighbour]; weight++; }
                    next[i] = Mathf.Max(required[i], sum / weight);
                }
                offsets = next;
            }
            return vertices.Select((v, i) => v + directions[group[i]] * offsets[group[i]]).ToArray();
        }

        internal static void Cancel(OrbitersAttachment attachment)
        {
            foreach (var fit in attachment.fittedMeshes)
            {
                var refit = RefitRecords.Find(fit.renderer);
                if (refit && refit.Applied && refit.original.mesh == fit.after) RefitRecords.Discard(refit);
                if (!fit.renderer || fit.renderer.sharedMesh != fit.after) continue;
                Undo.RecordObject(fit.renderer, "Cancel clothing volume fit");
                fit.renderer.sharedMesh = fit.before;
                fit.renderer.localBounds = fit.beforeBounds;
                PrefabUtility.RecordPrefabInstancePropertyModifications(fit.renderer);
            }
            Undo.RecordObject(attachment, "Cancel clothing volume fit");
            attachment.fittedMeshes.Clear();
        }

        private static int Depth(Transform t) { int n = 0; for (; t; t = t.parent) n++; return n; }

        private static int Region(HumanBodyBones role)
        {
            switch (role)
            {
                case HumanBodyBones.LeftShoulder: case HumanBodyBones.LeftUpperArm: case HumanBodyBones.LeftLowerArm: case HumanBodyBones.LeftHand: return 2;
                case HumanBodyBones.RightShoulder: case HumanBodyBones.RightUpperArm: case HumanBodyBones.RightLowerArm: case HumanBodyBones.RightHand: return 4;
                case HumanBodyBones.LeftUpperLeg: case HumanBodyBones.LeftLowerLeg: case HumanBodyBones.LeftFoot: return 8;
                case HumanBodyBones.RightUpperLeg: case HumanBodyBones.RightLowerLeg: case HumanBodyBones.RightFoot: return 16;
                case HumanBodyBones.Neck: case HumanBodyBones.Head: return 32;
                default: return 1;
            }
        }

        private static int[] Masks(SkinnedMeshRenderer renderer, Dictionary<Transform, int> regions)
        {
            var bones = renderer.bones.Select(b =>
            {
                for (var p = b; p; p = p.parent) if (regions.TryGetValue(p, out int region)) return region;
                return 0;
            }).ToArray();
            return renderer.sharedMesh.boneWeights.Select(w =>
                Mask(w.boneIndex0, w.weight0) | Mask(w.boneIndex1, w.weight1) | Mask(w.boneIndex2, w.weight2) | Mask(w.boneIndex3, w.weight3)).ToArray();
            int Mask(int bone, float weight) => weight >= .15f && bone < bones.Length ? bones[bone] : 0;
        }

        private sealed class SkinPose
        {
            public readonly Vector3[] World;
            public readonly Matrix4x4[] Matrices;

            public SkinPose(SkinnedMeshRenderer renderer)
            {
                var mesh = renderer.sharedMesh;
                var vertices = mesh.vertices;
                var scratch = new BlendShapeEvaluation.Scratch(vertices.Length);
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    BlendShapeEvaluation.Add(mesh, shape, renderer.GetBlendShapeWeight(shape), BlendShapeEvaluation.ClampWeights, vertices, null, scratch);
                var bind = mesh.bindposes;
                var bones = renderer.bones;
                var transforms = new Matrix4x4[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    transforms[i] = bones[i] && i < bind.Length
                        ? bones[i].localToWorldMatrix * bind[i]
                        : renderer.localToWorldMatrix;
                World = new Vector3[vertices.Length]; Matrices = new Matrix4x4[vertices.Length];
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    int cursor = 0;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        var matrix = Matrix4x4.zero;
                        int count = counts.Length > i ? counts[i] : 0;
                        for (int j = 0; j < count; j++)
                        {
                            var weight = weights[cursor++];
                            if (weight.boneIndex >= transforms.Length) continue;
                            var transform = transforms[weight.boneIndex];
                            for (int c = 0; c < 16; c++) matrix[c] += transform[c] * weight.weight;
                        }
                        if (count == 0) matrix = renderer.localToWorldMatrix;
                        Matrices[i] = matrix;
                        World[i] = matrix.MultiplyPoint3x4(vertices[i]);
                    }
                }
            }
        }
    }
}
