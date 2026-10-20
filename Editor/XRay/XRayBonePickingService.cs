using System;
using System.Collections.Generic;
using Orbiters.Toolkit.XRay;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.XRay
{
    [InitializeOnLoad]
    public static class XRayBonePickingService
    {
        private const string EnabledKey = "Orbiters.XRayGizmos.BonePicking.Enabled";
        private const float PickRadiusPixels = 12f;
        private const float HoverLineWidth = 8f;
        private const float SelectedLineWidth = 7f;
        private const float BoneCapScale = 0.018f;

        private static readonly Dictionary<int, XRayBoneSegment> hoveredSegments = new Dictionary<int, XRayBoneSegment>();
        private static readonly int PickingControlHint = "Orbiters.XRayGizmos.BonePicking".GetHashCode();
        private static double lastRepaintTime;

        public static event Action Changed;

        static XRayBonePickingService()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            XRayGizmoService.Changed += RepaintSceneViews;
            XRayBonePickHandlers.Changed += RepaintSceneViews;
            AssemblyReloadEvents.beforeAssemblyReload += ClearHover;
            EditorApplication.quitting += ClearHover;
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            private set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static Transform HoveredBone => GetHover(SceneView.lastActiveSceneView).Bone;

        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
            {
                return;
            }

            Enabled = enabled;
            if (!enabled)
            {
                ClearHover();
            }

            RepaintSceneViews();
            Changed?.Invoke();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            var evt = Event.current;
            if (evt == null || sceneView == null)
            {
                return;
            }

            // Allocate on every event, including when disabled, to keep Unity's control sequence stable.
            int controlId = GUIUtility.GetControlID(PickingControlHint, FocusType.Passive);
            if (!XRayGizmoService.Enabled)
            {
                SetHover(sceneView, default);
                return;
            }

            bool canHover = Enabled && !evt.alt && !Tools.viewToolActive && Tools.current != Tool.View && GUIUtility.hotControl == 0;
            if (evt.type == EventType.Layout || evt.type == EventType.MouseMove)
            {
                float distance = PickRadiusPixels;
                var next = canHover ? FindHoveredSegment(sceneView.camera, evt.mousePosition, out distance) : default;
                SetHover(sceneView, next);
                if (next.IsValid)
                {
                    // Unity's normal handles compete in a five-point pick range. Preserve our larger bone hit area
                    // without registering a default control that could swallow empty-space clicks.
                    HandleUtility.AddControl(controlId, distance * (5f / PickRadiusPixels));
                }
            }
            else if (!canHover || evt.type == EventType.MouseLeaveWindow)
            {
                SetHover(sceneView, default);
            }

            var hovered = GetHover(sceneView);
            if (Enabled && hovered.IsValid && CanPickBone(evt, controlId))
            {
                Pick(hovered.Bone, evt.shift, evt.control || evt.command);
                evt.Use();
                return;
            }

            if (evt.type == EventType.Repaint)
            {
                DrawSelectedHighlights();
                if (canHover && hovered.IsValid && HandleUtility.nearestControl == controlId)
                {
                    DrawSegmentHighlight(hovered, HoverLineWidth);
                }
            }
        }

        internal static bool CanPickBone(Event evt, int controlId)
        {
            return evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt &&
                !Tools.viewToolActive && Tools.current != Tool.View && GUIUtility.hotControl == 0 &&
                HandleUtility.nearestControl == controlId;
        }

        private static XRayBoneSegment GetHover(SceneView view)
        {
            return view != null && hoveredSegments.TryGetValue(view.GetInstanceID(), out var segment) ? segment : default;
        }

        private static void SetHover(SceneView view, XRayBoneSegment next)
        {
            if (SameSegment(GetHover(view), next))
            {
                return;
            }

            if (next.IsValid) hoveredSegments[view.GetInstanceID()] = next;
            else hoveredSegments.Remove(view.GetInstanceID());
            RepaintSceneViewsThrottled();
            Changed?.Invoke();
        }

        private static XRayBoneSegment FindHoveredSegment(Camera camera, Vector2 mousePosition, out float bestDistance)
        {
            XRayBoneSegment best = default;
            bestDistance = PickRadiusPixels;

            foreach (var segment in XRayGizmoService.ActiveBoneSegments)
            {
                if (!segment.IsValid)
                {
                    continue;
                }

                if (!TryProjectSegment(camera, segment.Bone.position, segment.EndPosition, out var a, out var b))
                {
                    continue;
                }

                float distance = DistanceToSegment(mousePosition, a, b);
                if (distance < bestDistance ||
                    (Mathf.Abs(distance - bestDistance) < 0.001f && segment.IsLeaf && !best.IsLeaf))
                {
                    bestDistance = distance;
                    best = segment;
                }
            }

            return best;
        }

        internal static bool TryProjectSegment(Camera camera, Vector3 head, Vector3 tail, out Vector2 a, out Vector2 b)
        {
            a = default;
            b = default;

            if (camera == null || !IsFinite(head) || !IsFinite(tail))
            {
                return false;
            }

            if ((tail - head).sqrMagnitude < 0.000001f)
            {
                return false;
            }

            float headDepth = Vector3.Dot(head - camera.transform.position, camera.transform.forward);
            float tailDepth = Vector3.Dot(tail - camera.transform.position, camera.transform.forward);
            float near = Mathf.Max(0.0001f, camera.nearClipPlane);
            if (headDepth < near && tailDepth < near)
            {
                return false;
            }

            // Project only the visible part. An endpoint behind the camera otherwise creates an invisible line
            // across the viewport, which can take clicks far away from the rendered bone.
            if (headDepth < near) head = Vector3.Lerp(head, tail, (near - headDepth) / (tailDepth - headDepth));
            else if (tailDepth < near) tail = Vector3.Lerp(head, tail, (near - headDepth) / (tailDepth - headDepth));
            using (new Handles.DrawingScope(Matrix4x4.identity))
            {
                a = HandleUtility.WorldToGUIPointWithDepth(camera, head);
                b = HandleUtility.WorldToGUIPointWithDepth(camera, tail);
            }
            return IsFinite(a) && IsFinite(b);
        }

        private static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        internal static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 0.0001f)
            {
                return Vector2.Distance(point, a);
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
            return Vector2.Distance(point, a + ab * t);
        }

        private static void DrawSelectedHighlights()
        {
            var selected = new HashSet<Transform>();
            var selectedTransforms = Selection.transforms;
            if (selectedTransforms != null)
            {
                selected.UnionWith(selectedTransforms);
            }

            // A tool's own pick (Mixer's sub-selected bone) shows as a selected bone does.
            XRayBonePickHandlers.CollectPicked(selected);
            if (selected.Count == 0)
            {
                return;
            }

            foreach (var segment in XRayGizmoService.ActiveBoneSegments)
            {
                if (!segment.IsValid ||
                    (!selected.Contains(segment.Bone) && !selected.Contains(segment.Child)))
                {
                    continue;
                }

                DrawSegmentHighlight(segment, SelectedLineWidth);
            }
        }

        private static void DrawSegmentHighlight(XRayBoneSegment segment, float lineWidth)
        {
            if (!segment.IsValid)
            {
                return;
            }

            var previousZTest = Handles.zTest;
            try
            {
                using (new Handles.DrawingScope(Color.white))
                {
                    Handles.zTest = CompareFunction.Always;
                    Handles.DrawAAPolyLine(lineWidth, segment.Bone.position, segment.EndPosition);

                    float handleSize = HandleUtility.GetHandleSize(segment.Bone.position) * BoneCapScale;
                    Handles.SphereHandleCap(
                        0,
                        segment.Bone.position,
                        Quaternion.identity,
                        handleSize,
                        EventType.Repaint);
                }
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
        }

        /// <summary>
        /// A click on a bone: a plain click goes to a tool's handler first (Mixer's Weight Paint sub-selects the bone);
        /// Shift and Ctrl clicks, and clicks no tool takes, select it in Unity.
        /// </summary>
        internal static void Pick(Transform bone, bool additive, bool toggle)
        {
            if (bone == null)
            {
                return;
            }

            if (!additive && !toggle && XRayBonePickHandlers.TryPick(bone))
            {
                SceneView.RepaintAll();
                return;
            }

            SelectBone(bone, additive, toggle);
        }

        private static void SelectBone(Transform bone, bool additive, bool toggle)
        {
            var selected = new List<UnityEngine.Object>(Selection.objects);
            var item = bone.gameObject;
            if (toggle && selected.Contains(item)) selected.Remove(item);
            else if (additive || toggle) { if (!selected.Contains(item)) selected.Add(item); }
            else { selected.Clear(); selected.Add(item); }
            Selection.objects = selected.ToArray();
            SceneView.RepaintAll();
        }

        private static bool SameSegment(XRayBoneSegment a, XRayBoneSegment b)
        {
            return a.Owner == b.Owner && a.Bone == b.Bone && a.Child == b.Child &&
                a.HasTailPosition == b.HasTailPosition && (!a.HasTailPosition || a.TailPosition == b.TailPosition);
        }

        private static void ClearHover()
        {
            hoveredSegments.Clear();
            RepaintSceneViews();
            Changed?.Invoke();
        }

        private static void RepaintSceneViews()
        {
            SceneView.RepaintAll();
        }

        private static void RepaintSceneViewsThrottled()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastRepaintTime < 0.025d)
            {
                return;
            }

            lastRepaintTime = now;
            SceneView.RepaintAll();
        }
    }
}
