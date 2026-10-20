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
        // A tool (Mixer) pinning the overlay to one renderer and bone set, or to per-vertex weights of its own.
        private static bool pinned;
        private static SkinnedMeshRenderer pinnedRenderer;
        private static List<Transform> pinnedBones = new List<Transform>();
        private static float[] pinnedWeights;
        // Per renderer, the bone it showed last: what it shows again when the mesh is selected without any of its bones.
        private static readonly Dictionary<int, int> lastBoneOf = new Dictionary<int, int>();
        private static string fallbackNote;
        // When the shown renderers' meshes were last compared with those the overlays were made from.
        private static double nextSourceCheck;
        private const string MeshSuffix = "_XRayWeightPaint";

        public static event Action Changed;

        static XRayWeightPaintService()
        {
            EditorApplication.hierarchyChanged += Refresh;
            Selection.selectionChanged += Refresh;
            SceneView.duringSceneGui += WatchSourceMeshes;
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
                ClearOrphanedOverlays();
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

        /// <summary>Whether a tool pins the overlay (shown even while the user's switch is off).</summary>
        public static bool Pinned => pinned;

        /// <summary>
        /// Shows the overlay on <paramref name="renderer"/> for <paramref name="bones"/>' weights, or for
        /// <paramref name="customWeights"/> (one value per vertex, 0..1) when given, whatever the selection is and whether
        /// the user's switch is on. One pin at a time; pinning the same renderer again only recolours.
        /// </summary>
        public static void Pin(SkinnedMeshRenderer renderer, IReadOnlyList<Transform> bones, float[] customWeights = null)
        {
            if (renderer == null) return;
            var list = bones != null ? new List<Transform>(bones) : new List<Transform>();
            bool sameRenderer = pinned && pinnedRenderer == renderer;
            bool sameTarget = sameRenderer && SameBones(pinnedBones, list) && (pinnedWeights == null) == (customWeights == null);
            bool wasBones = pinnedWeights == null;
            pinned = true;
            pinnedRenderer = renderer;
            pinnedBones = list;
            pinnedWeights = customWeights;
            if (sameTarget && Instances.ContainsKey(renderer.GetInstanceID()))
            {
                Repaint(renderer, customWeights);
                return;
            }
            // Another bone of the same renderer (a bone clicked while painting): recoloured in place at once, without
            // copying the mesh again.
            if (sameRenderer && wasBones && customWeights == null && TryShowOtherBones(renderer, list))
            {
                return;
            }
            settingsRevision++;
            Refresh();
        }

        private static bool TryShowOtherBones(SkinnedMeshRenderer renderer, List<Transform> bones)
        {
            int id = renderer.GetInstanceID();
            if (!Instances.TryGetValue(id, out var instance) || instance.MeshObject == null || instance.Revision != settingsRevision)
            {
                return false;
            }

            var overlayMesh = instance.Mesh;
            var source = renderer.sharedMesh;
            if (overlayMesh == null || source == null || source != instance.Source || source.vertexCount != overlayMesh.vertexCount)
            {
                return false;
            }

            var wanted = new HashSet<Transform>(bones.Where(b => b != null));
            var rendererBones = renderer.bones;
            var indices = new HashSet<int>();
            var shown = new List<Transform>();
            for (int i = 0; i < rendererBones.Length; i++)
            {
                if (rendererBones[i] != null && wanted.Contains(rendererBones[i]) && indices.Add(i))
                {
                    shown.Add(rendererBones[i]);
                }
            }

            if (indices.Count == 0)
            {
                return false;
            }

            overlayMesh.colors32 = BuildWeightColors(source, indices);
            Instances[id] = new Instance(renderer, instance.MeshObject, overlayMesh, source, indices, settingsRevision);
            lastBoneOf[id] = Array.IndexOf(rendererBones, shown[0]);
            activeBones.Clear();
            activeBones.AddRange(shown);
            if (!activeRenderers.Contains(renderer))
            {
                activeRenderers.Add(renderer);
            }

            fallbackNote = null;
            LastStatus = BuildStatus(1, false);
            SceneView.RepaintAll();
            NotifyChanged();
            return true;
        }

        /// <summary>Ends the pin of <paramref name="renderer"/> (or any pin when null); the overlay follows the selection again.</summary>
        public static void Unpin(SkinnedMeshRenderer renderer = null)
        {
            if (!pinned || (renderer != null && pinnedRenderer != renderer && pinnedRenderer != null)) return;
            pinned = false;
            pinnedRenderer = null;
            pinnedBones = new List<Transform>();
            pinnedWeights = null;
            settingsRevision++;
            Refresh();
        }

        /// <summary>Recolours the overlay of <paramref name="renderer"/> from its mesh's current weights (or the custom ones), without rebuilding it.</summary>
        public static void Repaint(SkinnedMeshRenderer renderer, float[] customWeights = null)
        {
            if (renderer == null) return;
            int id = renderer.GetInstanceID();
            if (!Instances.TryGetValue(id, out var instance) || instance.MeshObject == null || instance.Mesh == null) { Rebuild(id); return; }
            var overlayMesh = instance.Mesh;
            var source = renderer.sharedMesh;
            // Another mesh (Mixer's working copy, MCB applying an import), or one with other vertices: made again from it.
            if (source == null || source != instance.Source || source.vertexCount != overlayMesh.vertexCount) { Rebuild(id); return; }
            overlayMesh.colors32 = customWeights != null && customWeights.Length == overlayMesh.vertexCount
                ? BuildCustomColors(customWeights)
                : BuildWeightColors(source, instance.BoneIndices);
            SceneView.RepaintAll();
        }

        private static void Rebuild(int rendererKey)
        {
            DestroyInstance(rendererKey);
            Refresh();
        }

        // A mesh swapped under a shown renderer raises no hierarchy change: compared a few times a second while shown.
        private static void WatchSourceMeshes(SceneView _)
        {
            if (Instances.Count == 0 || isRefreshing || EditorApplication.timeSinceStartup < nextSourceCheck) return;
            nextSourceCheck = EditorApplication.timeSinceStartup + 0.25;
            foreach (var pair in Instances)
            {
                var renderer = pair.Value.Renderer;
                if (renderer != null && pair.Value.Mesh != null && renderer.sharedMesh == pair.Value.Source) continue;
                Rebuild(pair.Key);
                return;
            }
        }

        private static bool SameBones(List<Transform> a, List<Transform> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// The bone with the most weight over a renderer's mesh, or -1: what weight paint shows first on a mesh selected
        /// without any of its bones (Blender's weight paint always shows a group).
        /// </summary>
        public static int MostWeightedBone(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            var bones = renderer != null ? renderer.bones : null;
            // Rigid meshes (one bone per vertex) store bone indices without weights: each counts as 1.
            if (mesh == null || bones == null || bones.Length == 0 || !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendIndices)) return -1;
            var totals = new float[bones.Length];
            foreach (var weight in mesh.GetAllBoneWeights())
                if (weight.boneIndex >= 0 && weight.boneIndex < totals.Length) totals[weight.boneIndex] += weight.weight;
            int best = -1;
            for (int i = 0; i < totals.Length; i++)
                if (bones[i] != null && totals[i] > 0f && (best < 0 || totals[i] > totals[best])) best = i;
            return best;
        }

        // The bone a selected mesh shows while none of its bones is selected: the last one it showed, else the heaviest.
        private static bool TryFallbackBone(SkinnedMeshRenderer renderer, out HashSet<int> boneIndices, out List<Transform> selectedBones)
        {
            boneIndices = new HashSet<int>();
            selectedBones = new List<Transform>();
            var bones = renderer.bones;
            if (!lastBoneOf.TryGetValue(renderer.GetInstanceID(), out int bone) || bone < 0 || bone >= bones.Length || bones[bone] == null)
                bone = MostWeightedBone(renderer);
            if (bone < 0) return false;
            // Kept, so the next refresh does not weigh the whole mesh again.
            lastBoneOf[renderer.GetInstanceID()] = bone;
            boneIndices.Add(bone);
            selectedBones.Add(bones[bone]);
            return true;
        }

        private static Color32[] BuildCustomColors(float[] weights)
        {
            var colors = new Color32[weights.Length];
            for (int i = 0; i < weights.Length; i++) colors[i] = EvaluateBlenderWeightColor(Mathf.Clamp01(weights[i]));
            return colors;
        }

        public static void Refresh()
        {
            if (isRefreshing)
            {
                return;
            }

            // A pin whose renderer went away (its tool's object deleted, its scene closed) ends with it.
            if (pinned && pinnedRenderer == null)
            {
                pinned = false;
                pinnedBones = new List<Transform>();
                pinnedWeights = null;
            }

            // Off with nothing shown (most selection and hierarchy changes in most projects): nothing to do or tell.
            if (!Enabled && !pinned && Instances.Count == 0 && activeRenderers.Count == 0)
            {
                return;
            }

            isRefreshing = true;
            try
            {
                activeRenderers.Clear();
                activeBones.Clear();

                if (!Enabled && !pinned)
                {
                    ClearAll();
                    return;
                }

                HashSet<Transform> selection;
                List<SkinnedMeshRenderer> renderers;
                bool searchedSceneByBone = false;
                if (pinned && pinnedRenderer != null)
                {
                    selection = new HashSet<Transform>(pinnedBones.Where(t => t != null));
                    renderers = new List<SkinnedMeshRenderer> { pinnedRenderer };
                }
                else
                {
                    selection = new HashSet<Transform>(Selection.transforms.Where(t => t != null));
                    renderers = CollectSelectedRenderers();
                    if (renderers.Count == 0 && selection.Count > 0)
                    {
                        renderers = CollectSceneRenderersUsingSelection(selection);
                        searchedSceneByBone = true;
                    }
                }

                var targets = new List<WeightPaintTarget>();
                var targetIds = new HashSet<int>();
                var boneSet = new HashSet<Transform>();
                fallbackNote = null;

                foreach (var renderer in renderers)
                {
                    HashSet<int> boneIndices;
                    List<Transform> selectedBones;
                    if (pinned && renderer == pinnedRenderer)
                    {
                        // A tool's pin shows its own weights, or its bones' (none: no weight anywhere), never a bone of
                        // the overlay's choosing.
                        if (pinnedWeights != null || !TryResolveSelectedBoneIndices(renderer, selection, out boneIndices, out selectedBones))
                        {
                            boneIndices = new HashSet<int>();
                            selectedBones = new List<Transform>();
                        }
                    }
                    else if (TryResolveSelectedBoneIndices(renderer, selection, out boneIndices, out selectedBones))
                    {
                        lastBoneOf[renderer.GetInstanceID()] = boneIndices.First();
                    }
                    else if (searchedSceneByBone || !Selection.Contains(renderer.gameObject) || !TryFallbackBone(renderer, out boneIndices, out selectedBones))
                    {
                        continue;
                    }
                    else
                    {
                        fallbackNote = $"Showing {selectedBones[0].name} on {renderer.name}: select a bone (or click one in the Scene view) to see its weights.";
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
                        instance.Mesh == null ||
                        instance.Source != target.Renderer.sharedMesh ||
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

            // Read once: renderer.bones copies the whole array at every call.
            var bones = renderer != null ? renderer.bones : null;
            if (bones == null || selectedTransforms == null || selectedTransforms.Count == 0)
            {
                return false;
            }

            // The armature's own object (not a bone of the mesh) stands for all of its bones; a bone, even the root one
            // (Hips), stands for itself only, as in Blender.
            List<Transform> containers = null;
            foreach (var selectedTransform in selectedTransforms)
            {
                if (IsArmatureRootSelection(selectedTransform, renderer) && Array.IndexOf(bones, selectedTransform) < 0)
                {
                    (containers ?? (containers = new List<Transform>())).Add(selectedTransform);
                }
            }

            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                bool selected = selectedTransforms.Contains(bone);
                if (!selected && containers != null)
                {
                    foreach (var container in containers)
                    {
                        if (IsChildOf(bone, container))
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
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || selectedBoneIndices == null || selectedBoneIndices.Count == 0)
            {
                return false;
            }

            // The mesh's own weight data: no copy of every vertex's weights for each renderer of the scene.
            foreach (var weight in mesh.GetAllBoneWeights())
            {
                if (weight.weight > 0.0001f && selectedBoneIndices.Contains(weight.boneIndex))
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

            Instances[target.RendererKey] = new Instance(target.Renderer, overlay, overlay.GetComponent<SkinnedMeshRenderer>().sharedMesh,
                target.Renderer.sharedMesh, target.BoneIndices, settingsRevision);
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

            if (sourceMesh.GetBonesPerVertex().Length != sourceMesh.vertexCount)
            {
                message = $"Renderer '{renderer.name}' mesh has no compatible bone weights.";
                return false;
            }

            var mesh = UnityEngine.Object.Instantiate(sourceMesh);
            mesh.name = sourceMesh.name + MeshSuffix;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.colors32 = pinned && pinnedWeights != null && renderer == pinnedRenderer && pinnedWeights.Length == sourceMesh.vertexCount
                ? BuildCustomColors(pinnedWeights)
                : BuildWeightColors(sourceMesh, target.BoneIndices);

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

        // Every influence of each vertex (not only the four strongest the legacy weights keep), summed over the bones shown.
        private static Color32[] BuildWeightColors(Mesh mesh, HashSet<int> selectedBoneIndices)
        {
            int count = mesh.vertexCount;
            var colors = new Color32[count];
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int at = 0;
            for (int v = 0; v < count; v++)
            {
                float value = 0f;
                int influences = v < perVertex.Length ? perVertex[v] : 0;
                for (int k = 0; k < influences && at + k < weights.Length; k++)
                {
                    var weight = weights[at + k];
                    if (selectedBoneIndices.Contains(weight.boneIndex)) value += weight.weight;
                }

                at += influences;
                colors[v] = EvaluateBlenderWeightColor(Mathf.Clamp01(value));
            }

            return colors;
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

            material = ReloadScopedObjects.Keep(new Material(shader) { hideFlags = HideFlags.HideAndDontSave });
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
            bool shown = Instances.Count > 0 || activeRenderers.Count > 0;
            foreach (int id in Instances.Keys.ToList())
            {
                DestroyInstance(id);
            }

            activeRenderers.Clear();
            activeBones.Clear();
            if (shown) SceneView.RepaintAll();
        }

        // The overlay object and its mesh: the mesh too when the object already went with its parent (the renderer
        // deleted, its scene closed, a prefab stage or Play mode left), which destroys only the object.
        private static void DestroyInstance(int id)
        {
            if (!Instances.TryGetValue(id, out var instance))
            {
                return;
            }

            XRayArmatureMeshGenerator.DestroyGizmoObject(instance.MeshObject);
            XRayArmatureMeshGenerator.DestroyHiddenMesh(instance.Mesh);
            Instances.Remove(id);
        }

        // Overlays and meshes no instance holds (left by the scripts before a reload): at load, and when switched off.
        private static void ClearOrphanedOverlays()
        {
            var kept = new HashSet<GameObject>(Instances.Values.Select(i => i.MeshObject).Where(o => o != null));
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (IsWeightPaintOverlay(go) && !kept.Contains(go))
                {
                    XRayArmatureMeshGenerator.DestroyGizmoObject(go);
                }
            }

            XRayArmatureMeshGenerator.DestroyOrphanedMeshes(MeshSuffix, true,
                new HashSet<Mesh>(Instances.Values.Select(i => i.Mesh).Where(m => m != null)));
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

            if (fallbackNote != null && activeRenderers.Count == 1)
            {
                return fallbackNote;
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
            /// <summary>The overlay's own mesh (a hidden copy of <see cref="Source"/>).</summary>
            public readonly Mesh Mesh;
            /// <summary>The renderer's mesh the overlay was made from.</summary>
            public readonly Mesh Source;
            public readonly HashSet<int> BoneIndices;
            public readonly int Revision;

            public Instance(
                SkinnedMeshRenderer renderer,
                GameObject meshObject,
                Mesh mesh,
                Mesh source,
                HashSet<int> boneIndices,
                int revision)
            {
                Renderer = renderer;
                MeshObject = meshObject;
                Mesh = mesh;
                Source = source;
                BoneIndices = new HashSet<int>(boneIndices);
                Revision = revision;
            }
        }
    }
}
