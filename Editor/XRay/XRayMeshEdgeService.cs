using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.XRay;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.XRay
{
    [InitializeOnLoad]
    public static class XRayMeshEdgeService
    {
        private const string EnabledKey = "Orbiters.XRayGizmos.MeshEdges.Enabled";
        private const string AlphaKey = "Orbiters.XRayGizmos.MeshEdges.Alpha";
        private const string ColorKey = "Orbiters.XRayGizmos.MeshEdges.Color";
        private const string OverlayObjectNamePrefix = XRayArmatureMeshGenerator.GizmoObjectNamePrefix + "MeshEdges";
        private const float BlendShapeWeightEpsilon = 0.001f;
        private const double BlendShapeSyncIntervalSeconds = 0.25d;

        private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
        private static readonly List<SkinnedMeshRenderer> activeRenderers = new List<SkinnedMeshRenderer>();
        private static bool isRefreshing;
        private static Material material;
        private static int settingsRevision;
        private static double nextBlendShapeSyncTime;

        public static event Action Changed;

        static XRayMeshEdgeService()
        {
            EditorApplication.hierarchyChanged += Refresh;
            Selection.selectionChanged += Refresh;
            EditorApplication.update += SyncBlendShapeDrivenInstances;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += ClearAll;
            EditorApplication.quitting += ClearAll;
            ClearOrphanedOverlays();
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

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            if (!enabled)
            {
                LastStatus = "Mesh edges disabled.";
                ClearAll();
            }
            else
            {
                LastStatus = "Mesh edges enabled.";
                Refresh();
            }

            NotifyChanged();
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

        public static void RebuildAll()
        {
            settingsRevision++;
            ClearAll();
            Refresh();
        }

        public static void Refresh()
        {
            if (isRefreshing)
            {
                return;
            }

            isRefreshing = true;
            try
            {
                activeRenderers.Clear();

                if (!Enabled)
                {
                    ClearAll();
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

                var rendererIds = new HashSet<int>(renderers.Select(renderer => renderer.GetInstanceID()));

                foreach (int existingId in Instances.Keys.ToList())
                {
                    if (!rendererIds.Contains(existingId))
                    {
                        DestroyInstance(existingId);
                    }
                }

                foreach (var renderer in renderers)
                {
                    int id = renderer.GetInstanceID();
                    if (!Instances.TryGetValue(id, out var instance) ||
                        instance.MeshObject == null ||
                        instance.SourceMesh != renderer.sharedMesh ||
                        instance.Revision != settingsRevision)
                    {
                        DestroyInstance(id);
                        CreateInstance(renderer);
                    }

                    if (Instances.TryGetValue(id, out var current))
                    {
                        SyncSkinning(current);
                        activeRenderers.Add(renderer);
                    }
                }

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
                    if (selectedTransform != null && IsChildOf(bone, selectedTransform))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CreateInstance(SkinnedMeshRenderer renderer)
        {
            if (!TryCreateOverlay(renderer, out var overlay, out string message))
            {
                LastStatus = message;
                return false;
            }

            Instances[renderer.GetInstanceID()] = new Instance(
                renderer,
                renderer.sharedMesh,
                overlay,
                settingsRevision,
                CaptureBlendShapeWeights(renderer));
            LastStatus = message;
            return true;
        }

        private static bool TryCreateOverlay(
            SkinnedMeshRenderer renderer,
            out GameObject overlay,
            out string message)
        {
            overlay = null;
            if (renderer == null || renderer.sharedMesh == null)
            {
                message = "Selected renderer has no mesh.";
                return false;
            }

            var sourceMesh = renderer.sharedMesh;
            if (!TryBuildEdgeMesh(renderer, sourceMesh, out var mesh, out int edgeCount))
            {
                message = $"Renderer '{renderer.name}' has no triangle edges.";
                return false;
            }

            overlay = new GameObject(GetOverlayObjectName(renderer.GetInstanceID()));
            overlay.hideFlags = HideFlags.HideAndDontSave;
            overlay.transform.SetParent(renderer.transform, false);
            overlay.transform.localPosition = Vector3.zero;
            overlay.transform.localRotation = Quaternion.identity;
            overlay.transform.localScale = Vector3.one;

            var overlayRenderer = overlay.AddComponent<SkinnedMeshRenderer>();
            overlayRenderer.hideFlags = HideFlags.HideAndDontSave;
            overlayRenderer.sharedMesh = mesh;
            overlayRenderer.bones = renderer.bones;
            overlayRenderer.rootBone = renderer.rootBone;
            overlayRenderer.localBounds = renderer.localBounds;
            overlayRenderer.updateWhenOffscreen = true;
            overlayRenderer.quality = renderer.quality;
            overlayRenderer.sharedMaterial = GetMaterial();

            message = $"Showing {edgeCount} polygon edge(s) on '{renderer.name}'.";
            return true;
        }

        private static bool TryBuildEdgeMesh(
            SkinnedMeshRenderer renderer,
            Mesh sourceMesh,
            out Mesh mesh,
            out int edgeCount)
        {
            mesh = null;
            edgeCount = 0;

            if (sourceMesh == null || sourceMesh.vertexCount == 0)
            {
                return false;
            }

            var trianglesBySubmesh = new List<int[]>(sourceMesh.subMeshCount);
            int triangleIndexCount = 0;
            for (int submesh = 0; submesh < sourceMesh.subMeshCount; submesh++)
            {
                int[] triangles;
                try
                {
                    triangles = sourceMesh.GetTriangles(submesh);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is UnityException)
                {
                    triangles = Array.Empty<int>();
                }

                trianglesBySubmesh.Add(triangles);
                triangleIndexCount += triangles.Length;
            }

            if (triangleIndexCount == 0)
            {
                return false;
            }

            var vertices = sourceMesh.vertices;
            if (vertices == null || vertices.Length != sourceMesh.vertexCount)
            {
                return false;
            }

            var sourceNormals = sourceMesh.normals;
            bool hasNormals = sourceNormals != null && sourceNormals.Length == sourceMesh.vertexCount;
            ApplyActiveBlendShapes(renderer, sourceMesh, vertices, hasNormals ? sourceNormals : null);

            // Every influence, not the legacy four-slot boneWeights: the overlay must deform exactly like its source.
            var sourceBonesPerVertex = sourceMesh.GetBonesPerVertex();
            var sourceWeights = sourceMesh.GetAllBoneWeights();
            bool hasWeights = sourceBonesPerVertex.Length == sourceMesh.vertexCount && sourceWeights.Length > 0;
            int[] sourceWeightStart = null;
            if (hasWeights)
            {
                sourceWeightStart = new int[sourceBonesPerVertex.Length];
                for (int i = 1; i < sourceWeightStart.Length; i++)
                {
                    sourceWeightStart[i] = sourceWeightStart[i - 1] + sourceBonesPerVertex[i - 1];
                }
            }

            var edgeVertices = new Vector3[triangleIndexCount];
            var edgeNormals = hasNormals ? new Vector3[triangleIndexCount] : null;
            var edgeBonesPerVertex = hasWeights ? new byte[triangleIndexCount] : null;
            var edgeWeights = hasWeights ? new List<BoneWeight1>(triangleIndexCount * 4) : null;
            var barycentric = new Vector2[triangleIndexCount];
            var indices = new int[triangleIndexCount];
            int writeIndex = 0;

            void WriteEdgeVertex(int sourceIndex, Vector2 barycentricCoordinate)
            {
                int targetIndex = writeIndex;
                edgeVertices[targetIndex] = vertices[sourceIndex];
                if (edgeNormals != null)
                {
                    edgeNormals[targetIndex] = sourceNormals[sourceIndex];
                }

                if (edgeWeights != null)
                {
                    byte count = sourceBonesPerVertex[sourceIndex];
                    edgeBonesPerVertex[targetIndex] = count;
                    for (int w = 0; w < count; w++)
                    {
                        edgeWeights.Add(sourceWeights[sourceWeightStart[sourceIndex] + w]);
                    }
                }

                barycentric[targetIndex] = barycentricCoordinate;
                indices[targetIndex] = targetIndex;
                writeIndex++;
            }

            for (int submesh = 0; submesh < trianglesBySubmesh.Count; submesh++)
            {
                int[] triangles = trianglesBySubmesh[submesh];
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];
                    if (!IsValidVertexIndex(a, vertices.Length) ||
                        !IsValidVertexIndex(b, vertices.Length) ||
                        !IsValidVertexIndex(c, vertices.Length))
                    {
                        continue;
                    }

                    WriteEdgeVertex(a, new Vector2(1f, 0f));
                    WriteEdgeVertex(b, new Vector2(0f, 1f));
                    WriteEdgeVertex(c, new Vector2(0f, 0f));
                }
            }

            if (writeIndex == 0)
            {
                return false;
            }

            if (writeIndex != edgeVertices.Length)
            {
                Array.Resize(ref edgeVertices, writeIndex);
                Array.Resize(ref barycentric, writeIndex);
                Array.Resize(ref indices, writeIndex);
                if (edgeNormals != null)
                {
                    Array.Resize(ref edgeNormals, writeIndex);
                }

                if (edgeBonesPerVertex != null)
                {
                    Array.Resize(ref edgeBonesPerVertex, writeIndex);
                }
            }

            mesh = new Mesh
            {
                name = sourceMesh.name + "_XRayMeshEdges",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = sourceMesh.indexFormat == IndexFormat.UInt32 || edgeVertices.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };

            mesh.vertices = edgeVertices;
            mesh.uv = barycentric;
            if (edgeNormals != null)
            {
                mesh.normals = edgeNormals;
            }

            if (edgeWeights != null)
            {
                using (var bonesPerVertex = new NativeArray<byte>(edgeBonesPerVertex, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(edgeWeights.ToArray(), Allocator.Temp))
                {
                    mesh.SetBoneWeights(bonesPerVertex, weights);
                }
            }

            mesh.bindposes = sourceMesh.bindposes;
            mesh.bounds = sourceMesh.bounds;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            edgeCount = writeIndex;
            return true;
        }

        private static bool IsValidVertexIndex(int index, int vertexCount)
        {
            return index >= 0 && index < vertexCount;
        }

        private static void ApplyActiveBlendShapes(
            SkinnedMeshRenderer renderer,
            Mesh sourceMesh,
            Vector3[] vertices,
            Vector3[] normals)
        {
            if (renderer == null ||
                sourceMesh == null ||
                vertices == null ||
                sourceMesh.blendShapeCount == 0)
            {
                return;
            }

            Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Scratch scratch = null;
            bool clamp = Orbiters.Toolkit.Meshes.BlendShapeEvaluation.ClampWeights;
            for (int shape = 0; shape < sourceMesh.blendShapeCount; shape++)
            {
                float weight = renderer.GetBlendShapeWeight(shape);
                if (weight == 0f && sourceMesh.GetBlendShapeFrameCount(shape) <= 1) continue;
                scratch ??= new Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Scratch(sourceMesh.vertexCount);
                Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Add(sourceMesh, shape, weight, clamp, vertices, normals, scratch);
            }
        }

        private static bool IsChildOf(Transform child, Transform parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static string BuildStatus(int rendererCount, bool searchedSceneByBone)
        {
            if (rendererCount == 0)
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

        private static Material GetMaterial()
        {
            if (material != null)
            {
                return material;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                XRayPaths.Base + "/Shaders/XRayMeshEdges.shader");
            if (shader == null)
            {
                shader = Shader.Find("Hidden/Orbiters/XRayGizmos/MeshEdges");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
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

            if (material.HasProperty("_SurfaceOffset"))
            {
                material.SetFloat("_SurfaceOffset", 0.0015f);
            }

            if (material.HasProperty("_LineWidth"))
            {
                material.SetFloat("_LineWidth", 1.25f);
            }
        }

        private static void ClearAll()
        {
            foreach (int id in Instances.Keys.ToList())
            {
                DestroyInstance(id);
            }

            activeRenderers.Clear();
            ClearOrphanedOverlays();
            SceneView.RepaintAll();
        }

        private static void SyncBlendShapeDrivenInstances()
        {
            if (!Enabled || isRefreshing || Instances.Count == 0)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < nextBlendShapeSyncTime)
            {
                return;
            }

            nextBlendShapeSyncTime = now + BlendShapeSyncIntervalSeconds;
            bool changed = false;
            foreach (int id in Instances.Keys.ToList())
            {
                if (!Instances.TryGetValue(id, out var instance))
                {
                    continue;
                }

                if (instance.Renderer == null || instance.MeshObject == null)
                {
                    DestroyInstance(id);
                    changed = true;
                    continue;
                }

                if (instance.SourceMesh != instance.Renderer.sharedMesh ||
                    BlendShapeWeightsChanged(instance))
                {
                    var renderer = instance.Renderer;
                    DestroyInstance(id);
                    CreateInstance(renderer);
                    changed = true;
                }
                else
                {
                    changed |= SyncSkinning(instance);
                }
            }

            if (changed)
            {
                SceneView.RepaintAll();
                NotifyChanged();
            }
        }

        private static bool SyncSkinning(Instance instance)
        {
            var source = instance.Renderer;
            var overlay = instance.MeshObject.GetComponent<SkinnedMeshRenderer>();
            if (source == null || overlay == null) return false;

            bool changed = false;
            var bones = source.bones;
            if (!overlay.bones.SequenceEqual(bones))
            {
                overlay.bones = bones;
                changed = true;
            }
            if (overlay.rootBone != source.rootBone)
            {
                overlay.rootBone = source.rootBone;
                changed = true;
            }
            if (overlay.quality != source.quality)
            {
                overlay.quality = source.quality;
                changed = true;
            }
            return changed;
        }

        private static float[] CaptureBlendShapeWeights(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            int blendShapeCount = mesh != null ? mesh.blendShapeCount : 0;
            if (blendShapeCount == 0)
            {
                return Array.Empty<float>();
            }

            var weights = new float[blendShapeCount];
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = renderer.GetBlendShapeWeight(i);
            }

            return weights;
        }

        private static bool BlendShapeWeightsChanged(Instance instance)
        {
            var renderer = instance.Renderer;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            int blendShapeCount = mesh != null ? mesh.blendShapeCount : 0;
            if (instance.BlendShapeWeights == null ||
                instance.BlendShapeWeights.Length != blendShapeCount ||
                (blendShapeCount > 0 && instance.ClampBlendShapeWeights != PlayerSettings.legacyClampBlendShapeWeights))
            {
                return true;
            }

            for (int i = 0; i < blendShapeCount; i++)
            {
                if (Mathf.Abs(renderer.GetBlendShapeWeight(i) - instance.BlendShapeWeights[i]) > BlendShapeWeightEpsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DestroyInstance(int id)
        {
            if (!Instances.TryGetValue(id, out var instance))
            {
                return;
            }

            DestroyOverlayObject(instance.MeshObject);
            Instances.Remove(id);
        }

        private static void DestroyOverlayObject(GameObject overlay)
        {
            if (overlay == null)
            {
                return;
            }

            var overlayRenderer = overlay.GetComponent<SkinnedMeshRenderer>();
            var mesh = overlayRenderer != null ? overlayRenderer.sharedMesh : null;
            UnityEngine.Object.DestroyImmediate(overlay);

            if (mesh != null && !AssetDatabase.Contains(mesh))
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static void ClearOrphanedOverlays()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (IsMeshEdgeOverlay(go))
                {
                    DestroyOverlayObject(go);
                }
            }
        }

        private static bool IsMeshEdgeOverlay(GameObject go)
        {
            return go != null && go.name.StartsWith(OverlayObjectNamePrefix, StringComparison.Ordinal);
        }

        private static string GetOverlayObjectName(int rendererKey)
        {
            return OverlayObjectNamePrefix + "_" + rendererKey;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                ClearAll();
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode)
            {
                Refresh();
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

        private readonly struct Instance
        {
            public readonly SkinnedMeshRenderer Renderer;
            public readonly Mesh SourceMesh;
            public readonly GameObject MeshObject;
            public readonly int Revision;
            public readonly float[] BlendShapeWeights;
            public readonly bool ClampBlendShapeWeights;

            public Instance(
                SkinnedMeshRenderer renderer,
                Mesh sourceMesh,
                GameObject meshObject,
                int revision,
                float[] blendShapeWeights)
            {
                Renderer = renderer;
                SourceMesh = sourceMesh;
                MeshObject = meshObject;
                Revision = revision;
                BlendShapeWeights = blendShapeWeights;
                ClampBlendShapeWeights = PlayerSettings.legacyClampBlendShapeWeights;
            }
        }
    }
}
