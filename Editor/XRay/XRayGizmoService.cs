using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    [InitializeOnLoad]
    public static class XRayGizmoService
    {
        private const string EnabledKey = "Orbiters.XRayGizmos.Enabled";
        private const string ModeKey = "Orbiters.XRayGizmos.Mode";
        private const string MeshTypeKey = "Orbiters.XRayGizmos.MeshType";
        private const string ThicknessKey = "Orbiters.XRayGizmos.Thickness";
        private const string ColorKey = "Orbiters.XRayGizmos.Color";

        private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
        private static bool isRefreshing;
        private static Material material;
        private static int settingsRevision;
        private static double nextGeometryCheck;

        public static event Action Changed;

        static XRayGizmoService()
        {
            EditorApplication.hierarchyChanged += Refresh;
            SceneView.duringSceneGui += RefreshChangedGeometry;
            Selection.selectionChanged += Refresh;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += ClearAll;
            EditorApplication.quitting += ClearAll;
            ClearOrphanedGizmos();
            Refresh();
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            private set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static XRayGizmoTargetMode TargetMode
        {
            get => (XRayGizmoTargetMode)EditorPrefs.GetInt(ModeKey, (int)XRayGizmoTargetMode.SelectedObject);
            private set => EditorPrefs.SetInt(ModeKey, (int)value);
        }

        public static XRayArmatureMeshType MeshType
        {
            get => (XRayArmatureMeshType)EditorPrefs.GetInt(MeshTypeKey, (int)XRayArmatureMeshType.Pyramid);
            private set => EditorPrefs.SetInt(MeshTypeKey, (int)value);
        }

        public static float Thickness
        {
            get => EditorPrefs.GetFloat(ThicknessKey, 0.025f);
            private set => EditorPrefs.SetFloat(ThicknessKey, Mathf.Clamp(value, 0.0025f, 0.2f));
        }

        public static Color GizmoColor
        {
            get => TryParseColor(EditorPrefs.GetString(ColorKey, string.Empty), out var color)
                ? color
                : new Color(0.25f, 0.9f, 1f, 0.45f);
            private set => EditorPrefs.SetString(ColorKey, ColorUtility.ToHtmlStringRGBA(value));
        }

        public static GameObject PinnedObject { get; private set; }
        public static string LastStatus { get; private set; }

        public static IReadOnlyList<GameObject> ActiveTargets
        {
            get
            {
                return Instances.Values
                    .Select(instance => instance.Target.Owner)
                    .Where(target => target != null)
                    .ToList();
            }
        }

        public static IReadOnlyList<XRayBoneSegment> ActiveBoneSegments
        {
            get
            {
                var segments = new List<XRayBoneSegment>();
                foreach (var instance in Instances.Values)
                {
                    AppendSegments(instance.Target, segments);
                }

                return segments;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            if (!enabled)
            {
                LastStatus = "Bones hidden.";
                ClearAll();
            }
            else
            {
                LastStatus = "Bones shown.";
                Refresh();
            }

            NotifyChanged();
        }

        public static void SetTargetMode(XRayGizmoTargetMode mode)
        {
            if (TargetMode == mode)
            {
                return;
            }

            TargetMode = mode;
            RebuildAll();
        }

        public static void SetPinnedObject(GameObject targetObject)
        {
            GameObject resolved = null;
            if (XRayGizmoTargetFinder.TryResolveTarget(targetObject, out var target))
            {
                resolved = target.Owner;
            }

            if (PinnedObject == resolved)
            {
                return;
            }

            PinnedObject = resolved;
            if (TargetMode == XRayGizmoTargetMode.PinnedObject)
            {
                RebuildAll();
            }
            else
            {
                NotifyChanged();
            }
        }

        public static void SetMeshType(XRayArmatureMeshType meshType)
        {
            if (MeshType == meshType)
            {
                return;
            }

            MeshType = meshType;
            RebuildAll();
        }

        public static void SetThickness(float thickness)
        {
            float clamped = Mathf.Clamp(thickness, 0.0025f, 0.2f);
            if (Mathf.Approximately(Thickness, clamped))
            {
                return;
            }

            Thickness = clamped;
            RebuildAll();
        }

        public static void SetColor(Color color)
        {
            if (GizmoColor == color)
            {
                return;
            }

            GizmoColor = color;
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
                if (!Enabled)
                {
                    ClearAll();
                    return;
                }

                var targets = CollectTargets();
                var targetIds = new HashSet<int>(targets.Select(target => target.ArmatureKey));

                foreach (int existingId in Instances.Keys.ToList())
                {
                    if (!targetIds.Contains(existingId))
                    {
                        DestroyInstance(existingId);
                    }
                }

                foreach (var target in targets)
                {
                    int id = target.ArmatureKey;
                    if (!Instances.TryGetValue(id, out var instance) ||
                        instance.MeshObject == null ||
                        instance.Revision != settingsRevision ||
                        !instance.Geometry.Matches(target.Renderer))
                    {
                        DestroyInstance(id);
                        CreateInstance(target);
                    }
                }

                LastStatus = Instances.Count == 0
                    ? "No armature is currently visible."
                    : $"Showing {Instances.Count} armature(s).";

                SceneView.RepaintAll();
            }
            finally
            {
                isRefreshing = false;
                NotifyChanged();
            }
        }

        private static void RefreshChangedGeometry(SceneView sceneView)
        {
            if (!Enabled || isRefreshing || EditorApplication.timeSinceStartup < nextGeometryCheck) return;
            nextGeometryCheck = EditorApplication.timeSinceStartup + 0.1;
            foreach (var instance in Instances.Values)
            {
                if (instance.MeshObject != null && instance.Geometry.Matches(instance.Target.Renderer)) continue;
                Refresh();
                break;
            }
        }

        private static List<XRayArmatureTarget> CollectTargets()
        {
            var targets = new List<XRayArmatureTarget>();
            var seenArmatures = new HashSet<int>();
            switch (TargetMode)
            {
                case XRayGizmoTargetMode.PinnedObject:
                    if (XRayGizmoTargetFinder.TryResolveTarget(PinnedObject, out var pinnedTarget))
                    {
                        AddTarget(pinnedTarget, targets, seenArmatures);
                    }
                    break;
                case XRayGizmoTargetMode.WholeScene:
                    foreach (var target in XRayGizmoTargetFinder.FindSceneTargets())
                    {
                        AddTarget(target, targets, seenArmatures);
                    }
                    break;
                default:
                    if (XRayGizmoTargetFinder.TryResolveTarget(Selection.activeGameObject, out var selectedTarget))
                    {
                        AddTarget(selectedTarget, targets, seenArmatures);
                    }
                    break;
            }

            return targets;
        }

        private static void AddTarget(
            XRayArmatureTarget target,
            List<XRayArmatureTarget> targets,
            HashSet<int> seenArmatures)
        {
            if (!target.IsValid || !target.Owner.scene.IsValid())
            {
                return;
            }

            if (seenArmatures.Add(target.ArmatureKey))
            {
                targets.Add(target);
            }
        }

        private static void CreateInstance(XRayArmatureTarget target)
        {
            var mat = GetMaterial();
            if (XRayArmatureMeshGenerator.TryCreate(
                    target,
                    MeshType,
                    Thickness,
                    mat,
                    out var meshObject,
                    out string message))
            {
                Instances[target.ArmatureKey] = new Instance(target, meshObject, settingsRevision);
                LastStatus = message;
            }
            else
            {
                LastStatus = message;
            }
        }

        private static Material GetMaterial()
        {
            if (material != null)
            {
                return material;
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(
                XRayPaths.Base + "/Shaders/XRayArmature.shader");
            if (shader == null)
            {
                shader = Shader.Find("Hidden/Orbiters/XRayGizmos/Armature");
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
                material.SetColor("_Color", GizmoColor);
            }

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        }

        private static void ClearAll()
        {
            foreach (int id in Instances.Keys.ToList())
            {
                DestroyInstance(id);
            }

            ClearOrphanedGizmos();
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

        private static void ClearOrphanedGizmos()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (XRayArmatureMeshGenerator.IsArmatureGizmoObject(go))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
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

        private static void AppendSegments(XRayArmatureTarget target, List<XRayBoneSegment> segments)
        {
            if (!target.IsValid || target.Renderer.bones == null)
            {
                return;
            }

            var boneSet = new HashSet<Transform>();
            foreach (var bone in target.Renderer.bones)
            {
                if (bone != null)
                {
                    boneSet.Add(bone);
                }
            }

            foreach (var bone in target.Renderer.bones)
            {
                if (bone == null)
                {
                    continue;
                }

                bool hasDisplayedChild = false;
                foreach (Transform child in bone)
                {
                    if (child != null && boneSet.Contains(child))
                    {
                        hasDisplayedChild = true;
                        segments.Add(new XRayBoneSegment(target.Owner, bone, child));
                    }
                }

                if (!hasDisplayedChild && TryGetLeafTailPosition(bone, boneSet, out var leafTail))
                {
                    segments.Add(new XRayBoneSegment(target.Owner, bone, leafTail));
                }
            }
        }

        private static bool TryGetLeafTailPosition(Transform bone, HashSet<Transform> boneSet, out Vector3 tailPosition)
        {
            tailPosition = default;
            if (bone == null)
            {
                return false;
            }

            Vector3 childDirection = Vector3.zero;
            float childLength = 0f;
            foreach (Transform child in bone)
            {
                if (child == null || boneSet.Contains(child))
                {
                    continue;
                }

                var delta = child.position - bone.position;
                if (delta.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                childDirection += delta.normalized;
                childLength = Mathf.Max(childLength, delta.magnitude);
            }

            if (childDirection.sqrMagnitude > 0.000001f)
            {
                tailPosition = bone.position + childDirection.normalized * Mathf.Clamp(childLength, 0.025f, 0.2f);
                return true;
            }

            if (bone.parent != null && boneSet.Contains(bone.parent))
            {
                var delta = bone.position - bone.parent.position;
                if (delta.sqrMagnitude > 0.000001f)
                {
                    tailPosition = bone.position + delta.normalized * Mathf.Clamp(delta.magnitude * 0.45f, 0.025f, 0.15f);
                    return true;
                }
            }

            var fallback = bone.up.sqrMagnitude > 0.000001f ? bone.up : Vector3.up;
            tailPosition = bone.position + fallback.normalized * 0.05f;
            return true;
        }

        private readonly struct Instance
        {
            public readonly XRayArmatureTarget Target;
            public readonly GameObject MeshObject;
            public readonly int Revision;
            public readonly XRayArmatureGeometryState Geometry;

            public Instance(XRayArmatureTarget target, GameObject meshObject, int revision)
            {
                Target = target;
                MeshObject = meshObject;
                Revision = revision;
                Geometry = new XRayArmatureGeometryState(target.Renderer);
            }
        }
    }

    public readonly struct XRayBoneSegment
    {
        public readonly GameObject Owner;
        public readonly Transform Bone;
        public readonly Transform Child;
        public readonly Vector3 TailPosition;
        public readonly bool HasTailPosition;

        public XRayBoneSegment(GameObject owner, Transform bone, Transform child)
        {
            Owner = owner;
            Bone = bone;
            Child = child;
            TailPosition = default;
            HasTailPosition = false;
        }

        public XRayBoneSegment(GameObject owner, Transform bone, Vector3 tailPosition)
        {
            Owner = owner;
            Bone = bone;
            Child = null;
            TailPosition = tailPosition;
            HasTailPosition = true;
        }

        public bool IsValid => Owner != null && Bone != null && (Child != null || HasTailPosition);

        public bool IsLeaf => IsValid && Child == null;

        public Vector3 EndPosition => Child != null ? Child.position : TailPosition;
    }
}
