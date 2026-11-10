using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// Weight paint's two paths show the same weights: the hidden copy's vertex colours and the GPU path's buffer both come
    /// from <see cref="XRayWeightPaintService.BuildWeights"/>, which sums every influence of the shown bones (checked against
    /// a sum over the mesh's own weight list). The GPU path makes no object or mesh, recolours without recording again, and
    /// lets go of its buffer when its mesh is swapped or its renderer goes. <see cref="ShaderReadsEachVertexWeightByItsIndex"/>
    /// is the experiment for the path's one assumption (SV_VertexID is the mesh's vertex index on a skinned renderer).
    /// </summary>
    public sealed class XRayWeightPaintGpuTests
    {
        [Test]
        public void WeightsSumEveryInfluenceOfTheShownBones()
        {
            var mesh = new Mesh { vertices = new Vector3[4], triangles = new[] { 0, 1, 2, 0, 2, 3 } };
            try
            {
                // Five influences on vertex 0 (more than legacy BoneWeight keeps), a sum above 1 on vertex 2 when 0 and 1 show.
                var perVertex = new byte[] { 5, 1, 2, 1 };
                var weights = new[]
                {
                    W(0, 0.3f), W(1, 0.25f), W(2, 0.2f), W(3, 0.15f), W(4, 0.1f),
                    W(4, 1f),
                    W(0, 0.6f), W(1, 0.4f),
                    W(2, 1f),
                };
                using (var counts = new Unity.Collections.NativeArray<byte>(perVertex, Unity.Collections.Allocator.Temp))
                using (var all = new Unity.Collections.NativeArray<BoneWeight1>(weights, Unity.Collections.Allocator.Temp))
                {
                    mesh.SetBoneWeights(counts, all);
                }

                foreach (var shown in new[] { new[] { 0 }, new[] { 4 }, new[] { 0, 1 }, new[] { 1, 3, 4 }, new int[0] })
                {
                    var set = new System.Collections.Generic.HashSet<int>(shown);
                    var actual = XRayWeightPaintService.BuildWeights(mesh, set);
                    var expected = new float[4];
                    int at = 0;
                    for (int v = 0; v < 4; v++)
                    {
                        for (int k = 0; k < perVertex[v]; k++)
                            if (set.Contains(weights[at + k].boneIndex)) expected[v] += weights[at + k].weight;
                        at += perVertex[v];
                    }

                    for (int v = 0; v < 4; v++)
                        Assert.That(actual[v], Is.EqualTo(expected[v]).Within(1e-6f), $"bones {string.Join(",", shown)}, vertex {v}");
                    var colors = XRayWeightPaintService.ColorsOf(actual);
                    for (int v = 0; v < 4; v++)
                        Assert.That(colors[v], Is.EqualTo(XRayWeightPaintService.EvaluateBlenderWeightColor(Mathf.Clamp01(expected[v]))));
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GpuOverlayDrawsTheRendererItselfAndLetsGoOfItsBuffers()
        {
            bool gpu = XRayWeightPaintService.GpuOverlay;
            var fixture = new Fixture();
            Mesh swapped = null;
            try
            {
                XRayWeightPaintService.SetGpuOverlay(true);
                if (!XRayWeightPaintGpu.CanDraw(fixture.Renderer)) Assert.Ignore("The weight paint buffer shader does not run on this GPU.");
                int objectsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;

                XRayWeightPaintService.Pin(fixture.Renderer, new[] { fixture.Hips });
                var weights = XRayWeightPaintService.GpuWeightsOf(fixture.Renderer);
                Assert.That(weights != null && weights.IsValid(), Is.True, "the pin is drawn on the GPU");
                Assert.That(Resources.FindObjectsOfTypeAll<GameObject>().Length, Is.EqualTo(objectsBefore), "no overlay object");
                Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshesBefore), "no mesh copy");
                Assert.That(Read(weights), Is.EqualTo(new[] { 1f, 0f, 0.5f }), "Hips' own weights, vertex by vertex");
                Assert.That(XRayWeightPaintGpu.DrawnRenderers.Any(d => d.Renderer == fixture.Renderer && d.Shown), Is.True, "the renderer is recorded");

                // A tool's own weights.
                XRayWeightPaintService.Pin(fixture.Renderer, null, new[] { 0.25f, 0.75f, 1f });
                XRayWeightPaintService.Repaint(fixture.Renderer, new[] { 0.25f, 0.75f, 1f });
                var custom = XRayWeightPaintService.GpuWeightsOf(fixture.Renderer);
                Assert.That(Read(custom), Is.EqualTo(new[] { 0.25f, 0.75f, 1f }));

                // Another bone of the same renderer: recoloured in place.
                XRayWeightPaintService.Pin(fixture.Renderer, new[] { fixture.Hips });
                int recordings = XRayWeightPaintGpu.BufferRecordings;
                var before = XRayWeightPaintService.GpuWeightsOf(fixture.Renderer);
                XRayWeightPaintService.Pin(fixture.Renderer, new[] { fixture.Spine });
                Assert.That(XRayWeightPaintService.GpuWeightsOf(fixture.Renderer), Is.SameAs(before), "the same buffer");
                Assert.That(XRayWeightPaintGpu.BufferRecordings, Is.EqualTo(recordings), "no new recording for a recolour");
                Assert.That(Read(before), Is.EqualTo(new[] { 0f, 1f, 0.5f }), "Spine's weights");

                // Another mesh under the renderer: new weights from it, the old buffer released.
                swapped = Object.Instantiate(fixture.Mesh);
                fixture.Renderer.sharedMesh = swapped;
                XRayWeightPaintService.Repaint(fixture.Renderer);
                var again = XRayWeightPaintService.GpuWeightsOf(fixture.Renderer);
                Assert.That(again != null && again != before && again.IsValid(), Is.True, "a buffer for the new mesh");
                Assert.That(before.IsValid(), Is.False, "the old buffer is released");
                Assert.That(XRayWeightPaintGpu.DrawnRenderers.Single(d => d.Renderer == fixture.Renderer).Source, Is.SameAs(swapped));

                // The renderer goes: its buffer and the recording go with it.
                Object.DestroyImmediate(fixture.Renderer.gameObject);
                XRayWeightPaintService.Refresh();
                Assert.That(again.IsValid(), Is.False, "the buffer of a renderer that went is released");
                Assert.That(XRayWeightPaintGpu.DrawnRenderers.Count, Is.EqualTo(0));
                Assert.That(XRayWeightPaintService.Pinned, Is.False);
            }
            finally
            {
                XRayWeightPaintService.Unpin();
                XRayWeightPaintService.SetGpuOverlay(gpu);
                if (swapped != null) Object.DestroyImmediate(swapped);
                fixture.Dispose();
            }
        }

        /// <summary>
        /// The experiment for SV_VertexID: four separate triangles of one weight each are drawn by the weight paint shader
        /// from a skinned renderer, as the Scene view's command buffer draws it, and each triangle's middle pixel must show
        /// its own weight's colour. A wrong vertex index shows another triangle's colour (or blue for none). Inconclusive
        /// when Unity draws nothing for a skinned renderer outside a camera's render: then the Scene view check in
        /// XRayWeightPaintGpu's notes is the one to do.
        /// </summary>
        [Test]
        public void ShaderReadsEachVertexWeightByItsIndex()
        {
            var material = XRayWeightPaintGpu.GetMaterial();
            if (material == null || SystemInfo.maxComputeBufferInputsVertex < 1) Assert.Ignore("The weight paint buffer shader does not run on this GPU.");
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Weight paint index fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh();
            var target = new RenderTexture(128, 32, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(128, 32, TextureFormat.RGBA32, false, true);
            var shown = new[] { 0.1f, 0.4f, 0.65f, 0.95f };
            GraphicsBuffer buffer = null;
            var commands = new CommandBuffer { name = "Weight paint index test" };
            float alpha = XRayWeightPaintService.OverlayAlpha;
            try
            {
                var bone = new GameObject("Bone").transform;
                bone.SetParent(root.transform, false);
                var vertices = new Vector3[12];
                var values = new float[12];
                for (int t = 0; t < 4; t++)
                {
                    float x = -1f + 0.5f * t + 0.05f;
                    vertices[t * 3] = new Vector3(x, -0.9f, -1f);
                    vertices[t * 3 + 1] = new Vector3(x + 0.4f, -0.9f, -1f);
                    vertices[t * 3 + 2] = new Vector3(x + 0.2f, 0.9f, -1f);
                    for (int k = 0; k < 3; k++) values[t * 3 + k] = shown[t];
                }

                mesh.vertices = vertices;
                mesh.triangles = Enumerable.Range(0, 12).ToArray();
                mesh.bindposes = new[] { Matrix4x4.identity };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 12).ToArray();
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.bones = new[] { bone };
                renderer.rootBone = bone;
                renderer.sharedMesh = mesh;
                renderer.updateWhenOffscreen = true;

                buffer = XRayWeightPaintGpu.NewWeights(values);
                XRayWeightPaintGpu.SetAlpha(1f);
                commands.SetRenderTarget(target);
                commands.ClearRenderTarget(true, true, Color.clear);
                commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -10f, 10f));
                commands.SetGlobalBuffer("_OrbitersWeightPaintWeights", buffer);
                commands.SetGlobalInteger("_OrbitersWeightPaintCount", buffer.count);
                commands.DrawRenderer(renderer, material, 0, 0);
                Graphics.ExecuteCommandBuffer(commands);

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, 128, 32), 0, 0);
                read.Apply();
                RenderTexture.active = previous;

                var middles = Enumerable.Range(0, 4).Select(t => (Color32)read.GetPixel(Mathf.RoundToInt((0.5f * t + 0.25f) * 64f), 16)).ToArray();
                if (middles.All(c => c.a == 0)) Assert.Inconclusive("Nothing was drawn for a skinned renderer outside a camera's render: check in the Scene view.");
                for (int t = 0; t < 4; t++)
                {
                    var expected = XRayWeightPaintService.EvaluateBlenderWeightColor(shown[t]);
                    Assert.That(Math.Abs(middles[t].r - expected.r) <= 3 && Math.Abs(middles[t].g - expected.g) <= 3 && Math.Abs(middles[t].b - expected.b) <= 3,
                        Is.True, $"triangle {t} (weight {shown[t]}) shows {middles[t]}, expected {expected}: SV_VertexID is not the mesh's vertex index");
                }
            }
            finally
            {
                XRayWeightPaintGpu.SetAlpha(alpha);
                commands.Release();
                XRayWeightPaintGpu.Release(buffer);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(read);
                target.Release();
                Object.DestroyImmediate(target);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static BoneWeight1 W(int bone, float weight) => new BoneWeight1 { boneIndex = bone, weight = weight };

        private static float[] Read(GraphicsBuffer buffer)
        {
            var values = new float[buffer.count];
            buffer.GetData(values);
            return values;
        }

        private sealed class Fixture : IDisposable
        {
            public readonly UnityEngine.SceneManagement.Scene Scene = EditorSceneManager.NewPreviewScene();
            public readonly GameObject Root;
            public readonly Transform Hips, Spine;
            public readonly SkinnedMeshRenderer Renderer;
            public readonly Mesh Mesh;

            public Fixture()
            {
                Root = new GameObject("Weight paint GPU fixture");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Root, Scene);
                Hips = new GameObject("Hips").transform; Hips.SetParent(Root.transform, false);
                Spine = new GameObject("Spine").transform; Spine.SetParent(Hips, false); Spine.localPosition = Vector3.up * 0.3f;
                var body = new GameObject("Body");
                body.transform.SetParent(Root.transform, false);
                Renderer = body.AddComponent<SkinnedMeshRenderer>();
                Mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up * 0.3f, Vector3.right * 0.1f }, triangles = new[] { 0, 1, 2 } };
                Mesh.boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                };
                Mesh.bindposes = new[] { Hips.worldToLocalMatrix, Spine.worldToLocalMatrix };
                Renderer.sharedMesh = Mesh;
                Renderer.bones = new[] { Hips, Spine };
                Renderer.rootBone = Hips;
            }

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                if (Mesh != null) Object.DestroyImmediate(Mesh);
                EditorSceneManager.ClosePreviewScene(Scene);
            }
        }
    }
}
