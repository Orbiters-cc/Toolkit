using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>
    /// Polygon edges of the selected skinned meshes (or of the meshes a selected bone moves), drawn by the GPU in the
    /// Scene view only. A command buffer on each Scene view camera draws the renderers a second time with a wireframe
    /// material whose geometry shader finds the edges of each triangle, so the edges are those of the mesh as it shows:
    /// skinned, with its blendshapes and with Mixer's live edits and sculpts, at no CPU or memory cost. Nothing is added
    /// to the scene, so nothing can be saved, built or uploaded with it, and the Game view never shows it. A GPU without
    /// geometry shaders (Metal) gets the rasterizer's wireframe of a copy baked at each Scene view repaint instead.
    /// </summary>
    [InitializeOnLoad]
    public static class XRayMeshEdgeService
    {
        private const string EnabledKey = "Orbiters.XRayGizmos.MeshEdges.Enabled";
        private const string AlphaKey = "Orbiters.XRayGizmos.MeshEdges.Alpha";
        private const string ColorKey = "Orbiters.XRayGizmos.MeshEdges.Color";
        internal const string BufferName = "Orbiters mesh edges";
        internal const CameraEvent BufferEvent = CameraEvent.AfterForwardAlpha;
        private const float LineWidthPixels = 1.25f;
        // The edges are drawn this fraction of their distance nearer the camera: in front of their own surface.
        private const float DepthPull = 0.0006f;

        private static readonly List<SkinnedMeshRenderer> activeRenderers = new List<SkinnedMeshRenderer>();
        private static readonly List<Drawn> drawn = new List<Drawn>();
        private static readonly HashSet<Camera> cameras = new HashSet<Camera>();
        private static readonly Dictionary<int, Mesh> bakedForWire = new Dictionary<int, Mesh>();
        private static CommandBuffer buffer;
        private static Material material;
        private static bool isRefreshing;

        public static event Action Changed;

        static XRayMeshEdgeService()
        {
            EditorApplication.hierarchyChanged += Refresh;
            Selection.selectionChanged += Refresh;
            SceneVisibilityManager.visibilityChanged += Refresh;
            Camera.onPreCull += BeforeCameraRender;
            SceneView.duringSceneGui += DrawWithoutGeometryShaders;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
            RemoveOrphanedBuffers();
            Refresh();
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            private set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static float EdgeAlpha
        {
            get => EditorPrefs.GetFloat(AlphaKey, 0.65f);
            private set => EditorPrefs.SetFloat(AlphaKey, Mathf.Clamp01(value));
        }

        public static Color EdgeColor
        {
            get => TryParseColor(EditorPrefs.GetString(ColorKey, string.Empty), out var color)
                ? color
                : Color.white;
            private set => EditorPrefs.SetString(ColorKey, ColorUtility.ToHtmlStringRGBA(value));
        }



        public static string LastStatus { get; private set; }

        public static IReadOnlyList<SkinnedMeshRenderer> ActiveRenderers => activeRenderers;

        /// <summary>The GPU draws the edges itself (geometry shaders); false on GPUs that fall back to the rasterizer's wireframe.</summary>
        internal static bool UsesGeometryShader => SystemInfo.supportsGeometryShaders;

        /// <summary>The command buffer the Scene view cameras run (null until edges were shown once).</summary>
        internal static CommandBuffer Buffer => buffer;

        /// <summary>The renderers the command buffer draws, with the mesh and submesh count it was recorded for.</summary>
        internal static IReadOnlyList<Drawn> DrawnRenderers => drawn;

        /// <summary>How many times the command buffer was recorded (tests: a mesh edit or a pose must not re-record it).</summary>
        internal static int BufferRecordings { get; private set; }

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            LastStatus = enabled ? "Mesh edges enabled." : "Mesh edges disabled.";
            Refresh();
            // Switched off with nothing shown, Refresh has nothing to tell: the windows still hear of the switch.
            if (!enabled) NotifyChanged();
        }

        public static void SetEdgeAlpha(float alpha)
        {
            float clamped = Mathf.Clamp01(alpha);
            if (Mathf.Approximately(EdgeAlpha, clamped))
            {
                return;
            }

            EdgeAlpha = clamped;
            ApplyMaterialSettings();
            SceneView.RepaintAll();
            NotifyChanged();
        }

        public static void SetEdgeColor(Color color)
        {
            if (EdgeColor == color)
            {
                return;
            }

            EdgeColor = color;
            ApplyMaterialSettings();
            SceneView.RepaintAll();
            NotifyChanged();
        }


        /// <summary>Records the edges again from scratch (a new material and command buffer).</summary>
        public static void RebuildAll()
        {
            Release();
            Refresh();
        }

        public static void Refresh()
        {
            if (isRefreshing)
            {
                return;
            }

            // Off with nothing shown (most selection and hierarchy changes in most projects): nothing to do or tell.
            if (!Enabled && activeRenderers.Count == 0 && drawn.Count == 0 && cameras.Count == 0 && bakedForWire.Count == 0)
            {
                return;
            }

            isRefreshing = true;
            try
            {
                activeRenderers.Clear();
                if (!Enabled)
                {
                    Clear();
                    return;
                }

                var selection = new HashSet<Transform>(Selection.transforms.Where(t => t != null));
                var renderers = CollectSelectedRenderers();
                bool searchedSceneByBone = false;
                if (renderers.Count == 0 && selection.Count > 0)
                {
                    renderers = CollectSceneRenderersUsingSelection(selection);
                    searchedSceneByBone = true;
                }

                foreach (var renderer in renderers)
                {
                    if (HasTriangles(renderer.sharedMesh))
                    {
                        activeRenderers.Add(renderer);
                    }
                }

                ReleaseBakedForWire(keep: activeRenderers);
                Record();
                LastStatus = BuildStatus(renderers.Count, searchedSceneByBone);
                SceneView.RepaintAll();
            }
            finally
            {
                isRefreshing = false;
                NotifyChanged();
            }
        }

        private static List<SkinnedMeshRenderer> CollectSelectedRenderers()
        {
            var renderers = new List<SkinnedMeshRenderer>();
            var seen = new HashSet<int>();

            foreach (var go in Selection.gameObjects)
            {
                if (go == null || XRayArmatureMeshGenerator.IsPackageGizmoObject(go))
                {
                    continue;
                }

                foreach (var renderer in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!XRayArmatureMeshGenerator.IsUsableRenderer(renderer) ||
                        renderer.sharedMesh == null)
                    {
                        continue;
                    }

                    if (seen.Add(renderer.GetInstanceID()))
                    {
                        renderers.Add(renderer);
                    }
                }
            }

            renderers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return renderers;
        }

        private static List<SkinnedMeshRenderer> CollectSceneRenderersUsingSelection(HashSet<Transform> selection)
        {
            var renderers = new List<SkinnedMeshRenderer>();
            var seen = new HashSet<int>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (!XRayArmatureMeshGenerator.IsUsableRenderer(renderer) ||
                            renderer.sharedMesh == null ||
                            !RendererUsesSelection(renderer, selection))
                        {
                            continue;
                        }

                        if (seen.Add(renderer.GetInstanceID()))
                        {
                            renderers.Add(renderer);
                        }
                    }
                }
            }

            renderers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return renderers;
        }

        private static bool RendererUsesSelection(
            SkinnedMeshRenderer renderer,
            HashSet<Transform> selectedTransforms)
        {
            if (renderer == null || renderer.bones == null || selectedTransforms == null || selectedTransforms.Count == 0)
            {
                return false;
            }

            foreach (var bone in renderer.bones)
            {
                if (bone == null)
                {
                    continue;
                }

                if (selectedTransforms.Contains(bone))
                {
                    return true;
                }

                foreach (var selectedTransform in selectedTransforms)
                {
                    if (selectedTransform != null && bone.IsChildOf(selectedTransform))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasTriangles(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0)
            {
                return false;
            }

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) == MeshTopology.Triangles && mesh.GetIndexCount(submesh) >= 3)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- The command buffer ----------------------------------------------------------------------------------------

        /// <summary>
        /// Records one draw of each shown renderer's triangle submeshes. The buffer names renderers, not meshes: what
        /// it draws each frame is the renderer's current mesh, pose and blendshapes, so edits never need a new recording.
        /// </summary>
        private static void Record()
        {
            drawn.Clear();
            if (activeRenderers.Count == 0 || !UsesGeometryShader)
            {
                Detach();
                buffer?.Clear();
                return;
            }

            if (buffer == null)
            {
                buffer = new CommandBuffer { name = BufferName };
            }

            buffer.Clear();
            var mat = GetMaterial();
            if (mat == null)
            {
                Detach();
                return;
            }

            foreach (var renderer in activeRenderers)
            {
                var mesh = renderer.sharedMesh;
                bool shown = IsShown(renderer);
                if (shown)
                {
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        if (mesh.GetTopology(submesh) == MeshTopology.Triangles)
                        {
                            buffer.DrawRenderer(renderer, mat, submesh, 0);
                        }
                    }
                }

                drawn.Add(new Drawn(renderer, mesh, mesh != null ? mesh.subMeshCount : 0, shown));
            }

            BufferRecordings++;
            AttachToSceneViews();
        }

        // A renderer Unity does not draw is not skinned either: its edges would show a stale pose.
        private static bool IsShown(SkinnedMeshRenderer renderer)
        {
            return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.sharedMesh != null &&
                   !SceneVisibilityManager.instance.IsHidden(renderer.gameObject);
        }

        /// <summary>
        /// Before a camera renders: on a Scene view camera, the buffer is attached and re-recorded when a drawn renderer
        /// went away, swapped its mesh (Mixer's working copy, a commit) or was hidden. Any other camera is left alone.
        /// </summary>
        internal static void BeforeCameraRender(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.SceneView || drawn.Count == 0 || buffer == null)
            {
                return;
            }

            if (IsStale())
            {
                activeRenderers.RemoveAll(r => r == null || !HasTriangles(r.sharedMesh));
                Record();
                // The windows hear of it from the hierarchy change that follows; nothing is rebuilt during a render.
                LastStatus = BuildStatus(activeRenderers.Count, false);
                if (drawn.Count == 0)
                {
                    return;
                }
            }

            Attach(camera);
        }

        private static bool IsStale()
        {
            foreach (var entry in drawn)
            {
                var renderer = entry.Renderer;
                if (renderer == null)
                {
                    return true;
                }

                var mesh = renderer.sharedMesh;
                if (mesh != entry.Mesh || (mesh != null && mesh.subMeshCount != entry.SubmeshCount) || IsShown(renderer) != entry.Shown)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AttachToSceneViews()
        {
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view != null && view.camera != null)
                {
                    Attach(view.camera);
                }
            }
        }

        private static void Attach(Camera camera)
        {
            if (buffer == null || cameras.Contains(camera))
            {
                return;
            }

            cameras.RemoveWhere(c => c == null);
            RemoveNamedBuffers(camera);
            camera.AddCommandBuffer(BufferEvent, buffer);
            cameras.Add(camera);
        }

        private static void Detach()
        {
            foreach (var camera in cameras)
            {
                if (camera != null && buffer != null)
                {
                    camera.RemoveCommandBuffer(BufferEvent, buffer);
                }
            }

            cameras.Clear();
        }

        // A script reload loses the managed buffer but not the native one a camera holds: it would draw a second time.
        private static void RemoveOrphanedBuffers()
        {
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view != null && view.camera != null)
                {
                    RemoveNamedBuffers(view.camera);
                }
            }
        }

        private static void RemoveNamedBuffers(Camera camera)
        {
            foreach (var attached in camera.GetCommandBuffers(BufferEvent))
            {
                if (attached != null && attached.name == BufferName)
                {
                    camera.RemoveCommandBuffer(BufferEvent, attached);
                }
            }
        }

        // ---- Without geometry shaders ---------------------------------------------------------------------------------

        // Metal has no geometry shaders: the edges come from the rasterizer's wireframe of each renderer as it shows now.
        private static void DrawWithoutGeometryShaders(SceneView view)
        {
            if (UsesGeometryShader || activeRenderers.Count == 0 || Event.current == null || Event.current.type != EventType.Repaint)
            {
                return;
            }

            var mat = GetMaterial();
            if (mat == null)
            {
                return;
            }

            bool wireframe = GL.wireframe;
            try
            {
                GL.wireframe = true;
                foreach (var renderer in activeRenderers)
                {
                    if (!IsShown(renderer))
                    {
                        continue;
                    }

                    int id = renderer.GetInstanceID();
                    if (!bakedForWire.TryGetValue(id, out var mesh) || mesh == null)
                    {
                        mesh = new Mesh { name = "Orbiters mesh edges", hideFlags = HideFlags.HideAndDontSave };
                        bakedForWire[id] = mesh;
                    }

                    renderer.BakeMesh(mesh, true);
                    var matrix = renderer.transform.localToWorldMatrix;
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        if (mesh.GetTopology(submesh) == MeshTopology.Triangles && mat.SetPass(0))
                        {
                            Graphics.DrawMeshNow(mesh, matrix, submesh);
                        }
                    }
                }
            }
            finally
            {
                GL.wireframe = wireframe;
            }
        }

        // The baked copies of renderers no longer shown (all of them without keep).
        private static void ReleaseBakedForWire(List<SkinnedMeshRenderer> keep = null)
        {
            if (bakedForWire.Count == 0)
            {
                return;
            }

            var kept = keep != null ? new HashSet<int>(keep.Where(r => r != null).Select(r => r.GetInstanceID())) : null;
            foreach (var pair in bakedForWire.ToList())
            {
                if (kept != null && kept.Contains(pair.Key) && pair.Value != null)
                {
                    continue;
                }

                if (pair.Value != null)
                {
                    UnityEngine.Object.DestroyImmediate(pair.Value);
                }

                bakedForWire.Remove(pair.Key);
            }
        }

        // ---- Material, status, cleanup ---------------------------------------------------------------------------------

        private static string BuildStatus(int rendererCount, bool searchedSceneByBone)
        {
            if (activeRenderers.Count > 0 && GetMaterial() == null)
            {
                return "The mesh edge shader is missing or did not compile.";
            }

            if (rendererCount == 0 && activeRenderers.Count == 0)
            {
                return searchedSceneByBone
                    ? "No skinned mesh uses the selected bone or armature."
                    : "No selected skinned mesh.";
            }

            if (activeRenderers.Count == 0)
            {
                return "Selected mesh(es) have no triangle edges.";
            }

            return $"Showing polygon edges on {activeRenderers.Count} mesh(es).";
        }

        // Null when the shader is missing or did not compile: a stand-in would cover the whole mesh instead of its edges.
        private static Material GetMaterial()
        {
            if (material != null)
            {
                return material;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(XRayPaths.Base + "/Shaders/XRayMeshEdges.shader");
            if (shader == null)
            {
                shader = Shader.Find("Hidden/Orbiters/XRayGizmos/MeshEdges");
            }

            if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
            {
                return null;
            }

            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            ApplyMaterialSettings();
            return material;
        }

        private static void ApplyMaterialSettings()
        {
            if (material == null)
            {
                return;
            }

            if (material.HasProperty("_Color"))
            {
                var color = EdgeColor;
                color.a = EdgeAlpha;
                material.SetColor("_Color", color);
            }

            if (material.HasProperty("_LineWidth"))
            {
                material.SetFloat("_LineWidth", LineWidthPixels);
            }

            if (material.HasProperty("_DepthPull"))
            {
                material.SetFloat("_DepthPull", DepthPull);
            }

            // Only the edges of the surface that shows: hidden parts and the back of the mesh are left out.
            if (material.HasProperty("_ZTest"))
            {
                material.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
            }
        }

        // Nothing shown: the buffer leaves the cameras (it is kept for the next time).
        private static void Clear()
        {
            bool shown = activeRenderers.Count > 0 || drawn.Count > 0 || cameras.Count > 0 || bakedForWire.Count > 0;
            activeRenderers.Clear();
            drawn.Clear();
            Detach();
            buffer?.Clear();
            ReleaseBakedForWire();
            if (shown) SceneView.RepaintAll();
        }

        private static void Release()
        {
            Clear();
            RemoveOrphanedBuffers();
            buffer?.Release();
            buffer = null;
            if (material != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
                material = null;
            }
        }

        private static bool TryParseColor(string html, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            if (!html.StartsWith("#", StringComparison.Ordinal))
            {
                html = "#" + html;
            }

            return ColorUtility.TryParseHtmlString(html, out color);
        }

        private static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        internal readonly struct Drawn
        {
            public readonly SkinnedMeshRenderer Renderer;
            public readonly Mesh Mesh;
            public readonly int SubmeshCount;
            public readonly bool Shown;

            public Drawn(SkinnedMeshRenderer renderer, Mesh mesh, int submeshCount, bool shown)
            {
                Renderer = renderer;
                Mesh = mesh;
                SubmeshCount = submeshCount;
                Shown = shown;
            }
        }
    }
}
