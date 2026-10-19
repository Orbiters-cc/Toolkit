using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.XRay
{
    [InitializeOnLoad]
    public static class XRayWeightPaintService
    {
        private const string EnabledKey = "Orbiters.XRayGizmos.WeightPaint.Enabled";
        private const string AlphaKey = "Orbiters.XRayGizmos.WeightPaint.Alpha";
        private const string OverlayObjectNamePrefix = XRayArmatureMeshGenerator.GizmoObjectNamePrefix + "WeightPaint";

        private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
        private static readonly List<SkinnedMeshRenderer> activeRenderers = new List<SkinnedMeshRenderer>();
        private static readonly List<Transform> activeBones = new List<Transform>();
        private static bool isRefreshing;
        private static Material material;
        private static int settingsRevision;

        public static event Action Changed;

        static XRayWeightPaintService()
        {
            EditorApplication.hierarchyChanged += Refresh;
            Selection.selectionChanged += Refresh;
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

        public static float OverlayAlpha
        {
            get => EditorPrefs.GetFloat(AlphaKey, 0.82f);
            private set => EditorPrefs.SetFloat(AlphaKey, Mathf.Clamp01(value));
        }

        public static string LastStatus { get; private set; }

        public static IReadOnlyList<SkinnedMeshRenderer> ActiveRenderers => activeRenderers;

        public static IReadOnlyList<Transform> ActiveBones => activeBones;

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            if (!enabled)
            {
                LastStatus = "Weight paint disabled.";
                ClearAll();
            }
            else
            {
                LastStatus = "Weight paint enabled.";
                Refresh();
            }

            NotifyChanged();
        }

        public static void SetOverlayAlpha(float alpha)
        {
            float clamped = Mathf.Clamp01(alpha);
            if (Mathf.Approximately(OverlayAlpha, clamped))
            {
                return;
            }

            OverlayAlpha = clamped;
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
                activeBones.Clear();

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

                var targets = new List<WeightPaintTarget>();
                var targetIds = new HashSet<int>();
                var boneSet = new HashSet<Transform>();

                foreach (var renderer in renderers)
                {
                    if (!TryResolveSelectedBoneIndices(renderer, selection, out var boneIndices, out var selectedBones))
                    {
                        continue;
                    }

                    var target = new WeightPaintTarget(renderer, boneIndices);
                    targets.Add(target);
                    targetIds.Add(target.RendererKey);
                    activeRenderers.Add(renderer);

                    foreach (var bone in selectedBones)
                    {
                        if (bone != null && boneSet.Add(bone))
                        {
                            activeBones.Add(bone);
                        }
                    }
                }

                foreach (int existingId in Instances.Keys.ToList())
                {
                    if (!targetIds.Contains(existingId))
                    {
                        DestroyInstance(existingId);
                    }
                }

                foreach (var target in targets)
                {
                    int id = target.RendererKey;
                    if (!Instances.TryGetValue(id, out var instance) ||
                        instance.MeshObject == null ||
                        instance.Revision != settingsRevision ||
                        !SameBoneSelection(instance.BoneIndices, target.BoneIndices))
                    {
                        DestroyInstance(id);
                        CreateInstance(target);
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
                    if (!XRayArmatureMeshGenerator.IsUsableRenderer(renderer))
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
                        if (!XRayArmatureMeshGenerator.IsUsableRenderer(renderer))
                        {
                            continue;
                        }

                        if (!TryResolveSelectedBoneIndices(renderer, selection, out var boneIndices, out _) ||
                            !HasAnySelectedWeight(renderer, boneIndices))
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

        private static bool TryResolveSelectedBoneIndices(
            SkinnedMeshRenderer renderer,
            HashSet<Transform> selectedTransforms,
            out HashSet<int> boneIndices,
            out List<Transform> selectedBones)
        {
            boneIndices = new HashSet<int>();
            selectedBones = new List<Transform>();

            if (renderer == null || renderer.bones == null || selectedTransforms == null || selectedTransforms.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < renderer.bones.Length; i++)
            {
                var bone = renderer.bones[i];
                if (bone == null)
                {
                    continue;
                }

                bool selected = selectedTransforms.Contains(bone);
                if (!selected)
                {
                    foreach (var selectedTransform in selectedTransforms)
                    {
                        if (IsArmatureRootSelection(selectedTransform, renderer) && IsChildOf(bone, selectedTransform))
                        {
                            selected = true;
                            break;
                        }
                    }
                }

                if (selected && boneIndices.Add(i))
                {
                    selectedBones.Add(bone);
                }
            }

            return boneIndices.Count > 0;
        }

        private static bool HasAnySelectedWeight(SkinnedMeshRenderer renderer, HashSet<int> selectedBoneIndices)
        {
            if (renderer == null || renderer.sharedMesh == null || selectedBoneIndices == null || selectedBoneIndices.Count == 0)
            {
                return false;
            }

            var weights = renderer.sharedMesh.boneWeights;
            if (weights == null || weights.Length == 0)
            {
                return false;
            }

            foreach (var weight in weights)
            {
                if (GetSelectedWeight(weight, selectedBoneIndices) > 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsArmatureRootSelection(Transform selected, SkinnedMeshRenderer renderer)
        {
            if (selected == null || renderer == null)
            {
                return false;
            }

            if (selected == renderer.rootBone)
            {
                return true;
            }

            return string.Equals(selected.name, "Armature", StringComparison.OrdinalIgnoreCase) &&
                   renderer.rootBone != null &&
                   IsChildOf(renderer.rootBone, selected);
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

        private static void CreateInstance(WeightPaintTarget target)
        {
            if (!TryCreateOverlay(target, out var overlay, out string message))
            {
                LastStatus = message;
                return;
            }

            Instances[target.RendererKey] = new Instance(target.Renderer, overlay, target.BoneIndices, settingsRevision);
            LastStatus = message;
        }

        private static bool TryCreateOverlay(WeightPaintTarget target, out GameObject overlay, out string message)
        {
            overlay = null;
            var renderer = target.Renderer;
            if (renderer == null || renderer.sharedMesh == null)
            {
                message = "Selected renderer has no mesh.";
                return false;
            }

            var sourceMesh = renderer.sharedMesh;
            if (sourceMesh.vertexCount == 0)
            {
                message = $"Renderer '{renderer.name}' mesh has no vertices.";
                return false;
            }

            var weights = sourceMesh.boneWeights;
            if (weights == null || weights.Length != sourceMesh.vertexCount)
            {
                message = $"Renderer '{renderer.name}' mesh has no compatible bone weights.";
                return false;
            }

            var mesh = UnityEngine.Object.Instantiate(sourceMesh);
            mesh.name = sourceMesh.name + "_XRayWeightPaint";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.colors32 = BuildWeightColors(weights, target.BoneIndices);

            overlay = new GameObject(GetOverlayObjectName(target.RendererKey));
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
            overlayRenderer.sharedMaterials = BuildMaterialArray(Mathf.Max(1, sourceMesh.subMeshCount));

            CopyBlendShapeWeights(renderer, overlayRenderer, sourceMesh.blendShapeCount);

            message = $"Showing weight paint on '{renderer.name}'.";
            return true;
        }

        private static Color32[] BuildWeightColors(BoneWeight[] weights, HashSet<int> selectedBoneIndices)
        {
            var colors = new Color32[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                colors[i] = EvaluateBlenderWeightColor(GetSelectedWeight(weights[i], selectedBoneIndices));
            }

            return colors;
        }

        private static float GetSelectedWeight(BoneWeight weight, HashSet<int> selectedBoneIndices)
        {
            float value = 0f;
            if (selectedBoneIndices.Contains(weight.boneIndex0)) value += weight.weight0;
            if (selectedBoneIndices.Contains(weight.boneIndex1)) value += weight.weight1;
            if (selectedBoneIndices.Contains(weight.boneIndex2)) value += weight.weight2;
            if (selectedBoneIndices.Contains(weight.boneIndex3)) value += weight.weight3;
            return Mathf.Clamp01(value);
        }

        private static Color32 EvaluateBlenderWeightColor(float weight)
        {
            Color color;
            if (weight <= 0.25f)
            {
                color = Color.Lerp(new Color(0f, 0f, 1f, 1f), new Color(0f, 1f, 1f, 1f), weight / 0.25f);
            }
            else if (weight <= 0.5f)
            {
                color = Color.Lerp(new Color(0f, 1f, 1f, 1f), new Color(0f, 1f, 0f, 1f), (weight - 0.25f) / 0.25f);
            }
            else if (weight <= 0.75f)
            {
                color = Color.Lerp(new Color(0f, 1f, 0f, 1f), new Color(1f, 1f, 0f, 1f), (weight - 0.5f) / 0.25f);
            }
            else
            {
                color = Color.Lerp(new Color(1f, 1f, 0f, 1f), new Color(1f, 0f, 0f, 1f), (weight - 0.75f) / 0.25f);
            }

            color.a = 1f;
            return color;
        }

        private static Material[] BuildMaterialArray(int count)
        {
            var materials = new Material[count];
            var mat = GetMaterial();
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = mat;
            }

            return materials;
        }

        private static void CopyBlendShapeWeights(
            SkinnedMeshRenderer source,
            SkinnedMeshRenderer destination,
            int blendShapeCount)
        {
            if (source == null || destination == null)
            {
                return;
            }

            for (int i = 0; i < blendShapeCount; i++)
            {
                destination.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            }
        }

        private static Material GetMaterial()
        {
            if (material != null)
            {
                return material;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                XRayPaths.Base + "/Shaders/XRayWeightPaint.shader");
            if (shader == null)
            {
                shader = Shader.Find("Hidden/Orbiters/XRayGizmos/WeightPaint");
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

            if (material.HasProperty("_Alpha"))
            {
                material.SetFloat("_Alpha", OverlayAlpha);
            }

            if (material.HasProperty("_SurfaceOffset"))
            {
                material.SetFloat("_SurfaceOffset", 0.001f);
            }
        }

        private static void ClearAll()
        {
            foreach (int id in Instances.Keys.ToList())
            {
                DestroyInstance(id);
            }

            activeRenderers.Clear();
            activeBones.Clear();
            ClearOrphanedOverlays();
            SceneView.RepaintAll();
        }

        private static void DestroyInstance(int id)
        {
            if (!Instances.TryGetValue(id, out var instance))
            {
                return;
            }

            if (instance.MeshObject != null)
            {
                UnityEngine.Object.DestroyImmediate(instance.MeshObject);
            }

            Instances.Remove(id);
        }

        private static void ClearOrphanedOverlays()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (IsWeightPaintOverlay(go))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        private static bool IsWeightPaintOverlay(GameObject go)
        {
            return go != null && go.name.StartsWith(OverlayObjectNamePrefix, StringComparison.Ordinal);
        }

        private static string GetOverlayObjectName(int rendererKey)
        {
            return OverlayObjectNamePrefix + "_" + rendererKey;
        }

        private static bool SameBoneSelection(HashSet<int> a, HashSet<int> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }

            return a.SetEquals(b);
        }

        private static string BuildStatus(int selectedRendererCount, bool searchedSceneByBone)
        {
            if (selectedRendererCount == 0)
            {
                return searchedSceneByBone
                    ? "No skinned mesh uses the selected bone."
                    : "No selected skinned mesh.";
            }

            if (activeBones.Count == 0)
            {
                return searchedSceneByBone
                    ? "Selected object is not a bone used by any skinned mesh."
                    : "No selected bone for selected mesh.";
            }

            if (activeRenderers.Count == 0)
            {
                return "Selected bones do not affect selected mesh.";
            }

            return $"Showing weight paint on {activeRenderers.Count} mesh(es) for {activeBones.Count} bone(s).";
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

        private static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        private readonly struct WeightPaintTarget
        {
            public readonly SkinnedMeshRenderer Renderer;
            public readonly HashSet<int> BoneIndices;
            public readonly int RendererKey;

            public WeightPaintTarget(SkinnedMeshRenderer renderer, HashSet<int> boneIndices)
            {
                Renderer = renderer;
                BoneIndices = new HashSet<int>(boneIndices);
                RendererKey = renderer != null ? renderer.GetInstanceID() : 0;
            }
        }

        private readonly struct Instance
        {
            public readonly SkinnedMeshRenderer Renderer;
            public readonly GameObject MeshObject;
            public readonly HashSet<int> BoneIndices;
            public readonly int Revision;

            public Instance(
                SkinnedMeshRenderer renderer,
                GameObject meshObject,
                HashSet<int> boneIndices,
                int revision)
            {
                Renderer = renderer;
                MeshObject = meshObject;
                BoneIndices = new HashSet<int>(boneIndices);
                Revision = revision;
            }
        }
    }
}
