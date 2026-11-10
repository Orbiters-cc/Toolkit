using System.Collections.Generic;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>
    /// Weight paint drawn the way the mesh edges are (<see cref="XRayMeshEdgeService"/>): a command buffer on the Scene view
    /// cameras draws each shown renderer itself once more with the weight material, and the weights come from one float per
    /// vertex in a <see cref="GraphicsBuffer"/> read by the vertex index. Nothing is instantiated, the mesh is not skinned
    /// a second time, and the overlay is always the mesh as it shows (pose, blendshapes, live edits). A recolour is one
    /// <see cref="GraphicsBuffer.SetData(System.Array)"/>.
    ///
    /// It rests on one assumption to confirm in Unity 2022.3: that <c>SV_VertexID</c> in a draw of a skinned renderer by
    /// <see cref="CommandBuffer.DrawRenderer(Renderer, Material, int, int)"/> is the mesh's vertex index. Direct3D's
    /// <c>SV_VertexID</c> of an indexed draw is the index read from the index buffer, without the submesh's base vertex,
    /// so renderers with a submesh whose base vertex is not 0 are left to the hidden-copy path
    /// (<see cref="CanDraw"/>). How to check it: switch the path on (<see cref="XRayWeightPaintService.SetGpuOverlay"/>, kept in
    /// the EditorPrefs key <c>Orbiters.XRayGizmos.WeightPaint.GpuOverlay</c>), show weight paint on an avatar body with a
    /// bone selected, and compare with the switch off: the colours must be the same, vertex for vertex, in every pose and
    /// with blendshapes on. A wrong assumption shows as colours scrambled over the mesh (noise of reds and blues, not the smooth
    /// ramp around the bone), or one submesh shifted.
    /// </summary>
    internal static class XRayWeightPaintGpu
    {
        internal const string BufferName = "Orbiters weight paint";
        internal const CameraEvent BufferEvent = CameraEvent.AfterForwardAlpha;
        private static readonly int WeightsId = Shader.PropertyToID("_OrbitersWeightPaintWeights");
        private static readonly int CountId = Shader.PropertyToID("_OrbitersWeightPaintCount");
        private static readonly SceneViewBuffer weights = new SceneViewBuffer(BufferName, BufferEvent);
        private static readonly List<Drawn> drawn = new List<Drawn>();
        private static Material material;
        private static bool hooked;

        /// <summary>The command buffer the Scene view cameras run (null until weights were shown once on the GPU).</summary>
        internal static CommandBuffer Buffer => weights.Buffer;

        /// <summary>The renderers the buffer draws, with the mesh it was recorded for.</summary>
        internal static IReadOnlyList<Drawn> DrawnRenderers => drawn;

        /// <summary>How many times the buffer was recorded (a recolour must not re-record it).</summary>
        internal static int BufferRecordings { get; private set; }

        /// <summary>
        /// Whether this GPU path can draw <paramref name="renderer"/>'s mesh: the shader runs here (structured buffers in the
        /// vertex stage) and every triangle submesh starts at base vertex 0, so the index the GPU sees is the vertex's.
        /// </summary>
        internal static bool CanDraw(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0 || SystemInfo.maxComputeBufferInputsVertex < 1 || GetMaterial() == null)
            {
                return false;
            }

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetBaseVertex(submesh) != 0) return false;
            }

            return true;
        }

        /// <summary>A buffer of one weight per vertex of <paramref name="weightsPerVertex"/>; the caller owns it (<see cref="Release(GraphicsBuffer)"/>).</summary>
        internal static GraphicsBuffer NewWeights(float[] weightsPerVertex)
        {
            var made = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, weightsPerVertex.Length), sizeof(float));
            try
            {
                made.SetData(weightsPerVertex);
            }
            catch
            {
                // Nobody owns it yet.
                made.Release();
                throw;
            }
            return made;
        }

        internal static void Release(GraphicsBuffer buffer)
        {
            buffer?.Release();
        }

        /// <summary>
        /// Records one draw per triangle submesh of each shown renderer in <paramref name="entries"/> (renderer, the mesh its
        /// weights were made from, the weights). An entry whose renderer shows another mesh now is left out: its weights
        /// belong to the old one.
        /// </summary>
        internal static void Record(IEnumerable<(SkinnedMeshRenderer Renderer, Mesh Source, GraphicsBuffer Weights)> entries)
        {
            drawn.Clear();
            var mat = GetMaterial();
            CommandBuffer buffer = null;
            if (mat != null)
            {
                foreach (var entry in entries)
                {
                    var renderer = entry.Renderer;
                    if (renderer == null || entry.Weights == null || !entry.Weights.IsValid()) continue;
                    var mesh = renderer.sharedMesh;
                    bool shown = SceneViewBuffer.IsShown(renderer) && mesh == entry.Source;
                    if (shown)
                    {
                        if (buffer == null) buffer = weights.Begin();
                        buffer.SetGlobalBuffer(WeightsId, entry.Weights);
                        buffer.SetGlobalInteger(CountId, entry.Weights.count);
                        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                        {
                            if (mesh.GetTopology(submesh) == MeshTopology.Triangles) buffer.DrawRenderer(renderer, mat, submesh, 0);
                        }
                    }

                    drawn.Add(new Drawn(renderer, mesh, entry.Source, shown));
                }
            }

            if (buffer == null)
            {
                weights.Clear();
                Hook(drawn.Count > 0);
                return;
            }

            BufferRecordings++;
            weights.AttachToSceneViews();
            Hook(true);
        }

        /// <summary>Nothing drawn: the buffer leaves the cameras.</summary>
        internal static void Clear()
        {
            drawn.Clear();
            weights.Clear();
            Hook(false);
        }

        internal static void Release()
        {
            Clear();
            weights.Release();
            if (material != null)
            {
                Object.DestroyImmediate(material);
                material = null;
            }
        }

        internal static void RemoveOrphaned() => weights.RemoveOrphaned();

        /// <summary>
        /// Before a Scene view camera renders: the buffer is attached, and recorded again (nothing is built during a render)
        /// when a drawn renderer went away, swapped its mesh or was hidden or shown. Any other camera is left alone.
        /// </summary>
        internal static void BeforeCameraRender(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.SceneView || drawn.Count == 0)
            {
                return;
            }

            if (IsStale())
            {
                XRayWeightPaintService.RecordGpuOverlay();
                if (weights.Buffer == null || drawn.Count == 0) return;
            }

            weights.Attach(camera);
        }

        private static bool IsStale()
        {
            foreach (var entry in drawn)
            {
                var renderer = entry.Renderer;
                if (renderer == null) return true;
                var mesh = renderer.sharedMesh;
                if (mesh != entry.Mesh || (SceneViewBuffer.IsShown(renderer) && mesh == entry.Source) != entry.Shown) return true;
            }

            return false;
        }

        // Camera culling is only listened to while something is drawn.
        private static void Hook(bool on)
        {
            if (on == hooked) return;
            hooked = on;
            if (on) Camera.onPreCull += BeforeCameraRender;
            else Camera.onPreCull -= BeforeCameraRender;
        }

        internal static void SetAlpha(float alpha)
        {
            if (material != null) material.SetFloat("_Alpha", alpha);
        }

        // Null when the shader is missing, unsupported or did not compile: the hidden-copy path is used instead.
        internal static Material GetMaterial()
        {
            if (material != null)
            {
                return material;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(XRayPaths.Base + "/Shaders/XRayWeightPaintBuffer.shader");
            if (shader == null)
            {
                shader = Shader.Find("Hidden/Orbiters/XRayGizmos/WeightPaintBuffer");
            }

            if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
            {
                return null;
            }

            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetFloat("_Alpha", XRayWeightPaintService.OverlayAlpha);
            material.SetFloat("_SurfaceOffset", XRayWeightPaintService.SurfaceOffset);
            return material;
        }

        internal readonly struct Drawn
        {
            public readonly SkinnedMeshRenderer Renderer;
            /// <summary>The renderer's mesh when recorded.</summary>
            public readonly Mesh Mesh;
            /// <summary>The mesh the weights were made from.</summary>
            public readonly Mesh Source;
            public readonly bool Shown;

            public Drawn(SkinnedMeshRenderer renderer, Mesh mesh, Mesh source, bool shown)
            {
                Renderer = renderer;
                Mesh = mesh;
                Source = source;
                Shown = shown;
            }
        }
    }
}
