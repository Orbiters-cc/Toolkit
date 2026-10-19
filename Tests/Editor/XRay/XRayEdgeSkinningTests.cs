using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    public static class XRayEdgeSkinningTests
    {
        public static void RunOrThrow()
        {
            var selection = Selection.objects;
            bool enabled = XRayMeshEdgeService.Enabled;
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Edge skinning fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh();
            var sourceBake = new Mesh();
            var edgeBake = new Mesh();
            try
            {
                var a = new GameObject("A").transform; a.SetParent(root.transform, false);
                var b = new GameObject("B").transform; b.SetParent(root.transform, false); b.localPosition = Vector3.right * 2;
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                var source = root.AddComponent<SkinnedMeshRenderer>();
                source.sharedMesh = mesh; source.bones = new[] { a, b }; source.rootBone = a;
                Selection.activeGameObject = root;
                XRayMeshEdgeService.SetEnabled(true); XRayMeshEdgeService.Refresh();
                var overlay = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r != source);
                var edgeMesh = overlay.sharedMesh;
                Action verify = () =>
                {
                    if (overlay == null || overlay.sharedMesh != edgeMesh) throw new InvalidOperationException("Skinning sync rebuilt edge geometry.");
                    if (!overlay.bones.SequenceEqual(source.bones) || overlay.rootBone != source.rootBone || overlay.quality != source.quality)
                        throw new InvalidOperationException($"Edge skinning is stale: bones={overlay.bones.SequenceEqual(source.bones)}, root={overlay.rootBone == source.rootBone}, quality={overlay.quality}/{source.quality}.");
                    source.BakeMesh(sourceBake); overlay.BakeMesh(edgeBake);
                    for (int i = 0; i < 3; i++)
                        if ((sourceBake.vertices[i] - edgeBake.vertices[i]).sqrMagnitude > 0.00000001f)
                            throw new InvalidOperationException("Baked edge vertices differ from source.");
                };
                verify();
                source.bones = new[] { b, a }; source.rootBone = b;
                XRayMeshEdgeService.Refresh(); verify();
                source.bones = new[] { a, a }; source.rootBone = a;
                source.quality = SkinQuality.Bone1;
                Sync(); verify();
                source.rootBone = b; Sync(); verify();
                a.localPosition = Vector3.up * 3; Sync(); verify();
            }
            finally
            {
                XRayMeshEdgeService.SetEnabled(false);
                Selection.objects = selection;
                Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(sourceBake); Object.DestroyImmediate(edgeBake);
                EditorSceneManager.ClosePreviewScene(scene);
                XRayMeshEdgeService.SetEnabled(enabled);
            }
        }

        /// <summary>The overlay keeps every influence of a vertex skinned to more than four bones.</summary>
        public static void AllInfluencesOrThrow()
        {
            var selection = Selection.objects;
            bool enabled = XRayMeshEdgeService.Enabled;
            var skinWeights = QualitySettings.skinWeights;
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Edge influence fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh();
            var sourceBake = new Mesh();
            var edgeBake = new Mesh();
            try
            {
                var bones = Enumerable.Range(0, 5).Select(i =>
                {
                    var bone = new GameObject("Bone" + i).transform;
                    bone.SetParent(root.transform, false);
                    return bone;
                }).ToArray();
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right, Vector3.one };
                var triangles = new[] { 2, 0, 1, 1, 3, 2 };
                mesh.triangles = triangles;
                mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, bones.Length).ToArray();
                var perVertex = new byte[] { 5, 1, 2, 3 };
                var weights = new[]
                {
                    new BoneWeight1 { boneIndex = 0, weight = 0.3f }, new BoneWeight1 { boneIndex = 1, weight = 0.2f },
                    new BoneWeight1 { boneIndex = 2, weight = 0.2f }, new BoneWeight1 { boneIndex = 3, weight = 0.15f },
                    new BoneWeight1 { boneIndex = 4, weight = 0.15f },
                    new BoneWeight1 { boneIndex = 4, weight = 1f },
                    new BoneWeight1 { boneIndex = 1, weight = 0.6f }, new BoneWeight1 { boneIndex = 4, weight = 0.4f },
                    new BoneWeight1 { boneIndex = 2, weight = 0.5f }, new BoneWeight1 { boneIndex = 0, weight = 0.3f },
                    new BoneWeight1 { boneIndex = 4, weight = 0.2f }
                };
                using (var perVertexArray = new Unity.Collections.NativeArray<byte>(perVertex, Unity.Collections.Allocator.Temp))
                using (var weightArray = new Unity.Collections.NativeArray<BoneWeight1>(weights, Unity.Collections.Allocator.Temp))
                    mesh.SetBoneWeights(perVertexArray, weightArray);
                // Unity stores skin weights at finite precision (at least 16-bit normalized), so the source of truth
                // for an exact copy is the mesh readback, not the original arbitrary-precision input literals.
                weights = mesh.GetAllBoneWeights().ToArray();
                var source = root.AddComponent<SkinnedMeshRenderer>();
                source.sharedMesh = mesh; source.bones = bones; source.rootBone = bones[0];
                source.quality = SkinQuality.Auto;
                Selection.activeGameObject = root;
                XRayMeshEdgeService.SetEnabled(true); XRayMeshEdgeService.Refresh();
                var overlay = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r != source);
                var edgePerVertex = overlay.sharedMesh.GetBonesPerVertex().ToArray();
                var edgeWeights = overlay.sharedMesh.GetAllBoneWeights().ToArray();
                var expectedPerVertex = triangles.Select(i => perVertex[i]).ToArray();
                var expectedWeights = triangles.SelectMany(i => weights.Skip(perVertex.Take(i).Sum(n => (int)n)).Take(perVertex[i])).ToArray();
                if (!edgePerVertex.SequenceEqual(expectedPerVertex) || edgePerVertex[1] != 5 ||
                    edgeWeights.Length != expectedWeights.Length)
                    throw new InvalidOperationException($"Edge overlay influences differ from the source: {string.Join(",", edgePerVertex)} / {edgeWeights.Length} weights.");
                for (int i = 0; i < expectedWeights.Length; i++)
                    if (edgeWeights[i].boneIndex != expectedWeights[i].boneIndex ||
                        Mathf.Abs(edgeWeights[i].weight - expectedWeights[i].weight) > 1f / 65535f)
                        throw new InvalidOperationException($"Edge influence {i} differs: bone {edgeWeights[i].boneIndex}/{expectedWeights[i].boneIndex}, weight {edgeWeights[i].weight:R}/{expectedWeights[i].weight:R}.");

                QualitySettings.skinWeights = SkinWeights.Unlimited;
                bones[4].localPosition = new Vector3(0.4f, 0.7f, -0.3f);
                bones[3].localRotation = Quaternion.Euler(20f, 40f, 0f);
                source.BakeMesh(sourceBake); overlay.BakeMesh(edgeBake);
                for (int i = 0; i < triangles.Length; i++)
                    if ((sourceBake.vertices[triangles[i]] - edgeBake.vertices[i]).sqrMagnitude > 0.00000001f)
                        throw new InvalidOperationException("Baked edge vertices differ from a source with more than four influences.");
            }
            finally
            {
                QualitySettings.skinWeights = skinWeights;
                XRayMeshEdgeService.SetEnabled(false);
                Selection.objects = selection;
                Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(sourceBake); Object.DestroyImmediate(edgeBake);
                EditorSceneManager.ClosePreviewScene(scene);
                XRayMeshEdgeService.SetEnabled(enabled);
            }
        }

        private static void Sync()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(XRayMeshEdgeService).GetField("nextBlendShapeSyncTime", flags).SetValue(null, 0d);
            typeof(XRayMeshEdgeService).GetMethod("SyncBlendShapeDrivenInstances", flags).Invoke(null, null);
        }
    }
}
