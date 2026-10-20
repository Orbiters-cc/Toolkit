using Orbiters.Toolkit.Meshes;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    public static class XRayBlendShapeTests
    {
        public static void RunOrThrow()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Blendshape evaluation fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh();
            var baked = new Mesh();
            try
            {
                var bone = new GameObject("Bone").transform;
                bone.SetParent(root.transform, false);
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.bindposes = new[] { Matrix4x4.identity };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.bones = new[] { bone };
                renderer.rootBone = bone;
                renderer.sharedMesh = mesh;
                var configurations = new[] { new[] { 50f, 100f }, new[] { -100f, 100f }, new[] { -100f, -50f },
                    new[] { 25f, 50f, 100f }, new[] { 50f }, new[] { -50f }, new[] { 0f } };
                foreach (var frames in configurations)
                {
                    mesh.ClearBlendShapes();
                    for (int i = 0; i < frames.Length; i++)
                        mesh.AddBlendShapeFrame("Shape", frames[i], Enumerable.Repeat(Vector3.right * (1 + i * 2), 3).ToArray(), null, null);
                    foreach (float weight in new[] { -150f, -100f, -50f, 0f, 0.0005f, 25f, 50f, 75f, 100f, 150f, 200f })
                    {
                        renderer.SetBlendShapeWeight(0, weight);
                        renderer.BakeMesh(baked);
                        var actual = mesh.vertices;
                        ApplyActiveBlendShapes(renderer, mesh, actual);
                        var expected = baked.vertices;
                        for (int vertex = 0; vertex < actual.Length; vertex++)
                            if ((actual[vertex] - expected[vertex]).sqrMagnitude > 0.00000001f)
                                throw new InvalidOperationException($"Clamp={PlayerSettings.legacyClampBlendShapeWeights}, frames {string.Join(",", frames)}, weight {weight}: Toolkit {actual[vertex]} != Unity {expected[vertex]}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(baked);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // The renderer's blendshapes at their weights, as the Toolkit's tools add them (BlendShapeEvaluation).
        private static void ApplyActiveBlendShapes(SkinnedMeshRenderer renderer, Mesh mesh, Vector3[] vertices)
        {
            BlendShapeEvaluation.Scratch scratch = null;
            bool clamp = BlendShapeEvaluation.ClampWeights;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                float weight = renderer.GetBlendShapeWeight(shape);
                if (weight == 0f && mesh.GetBlendShapeFrameCount(shape) <= 1) continue;
                scratch ??= new BlendShapeEvaluation.Scratch(mesh.vertexCount);
                BlendShapeEvaluation.Add(mesh, shape, weight, clamp, vertices, null, scratch);
            }
        }
    }
}
