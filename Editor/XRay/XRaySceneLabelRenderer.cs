using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    public readonly struct XRaySceneLabel
    {
        public readonly string Text;
        public readonly Vector3 TargetPosition;

        public XRaySceneLabel(string text, Vector3 targetPosition)
        {
            Text = text;
            TargetPosition = targetPosition;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(Text);
    }

    public static class XRaySceneLabelRenderer
    {
        private const float PreferredYOffset = 100f;
        private const float PaddingX = 9f;
        private const float PaddingY = 5f;
        private const float MaxLabelWidth = 280f;
        private const float ViewMargin = 8f;
        private const float CollisionPadding = 6f;
        private const float ConnectorWidth = 1.75f;
        private const int SeparationPasses = 18;

        private static readonly Color TextColor = Color.white;
        private static readonly Color ConnectorColor = new Color(1f, 1f, 1f, 0.82f);
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.68f);

        private static readonly List<LabelLayout> Layouts = new List<LabelLayout>();
        private static GUIStyle textStyle;
        private static GUIStyle backgroundStyle;
        private static GUIContent scratchContent;

        public static void Draw(SceneView sceneView, IReadOnlyList<XRaySceneLabel> labels)
        {
            if (sceneView == null || labels == null || labels.Count == 0)
            {
                return;
            }

            var evt = Event.current;
            if (evt == null || evt.type != EventType.Repaint)
            {
                return;
            }

            EnsureStyles();
            BuildLayouts(sceneView, labels);
            if (Layouts.Count == 0)
            {
                return;
            }

            SeparateLayouts(BuildViewRect(sceneView));
            DrawLayouts();
        }

        private static void BuildLayouts(SceneView sceneView, IReadOnlyList<XRaySceneLabel> labels)
        {
            Layouts.Clear();

            var camera = sceneView.camera != null ? sceneView.camera : Camera.current;
            if (camera == null)
            {
                return;
            }

            var viewRect = BuildViewRect(sceneView);
            foreach (var label in labels)
            {
                if (!label.IsValid)
                {
                    continue;
                }

                var viewport = camera.WorldToViewportPoint(label.TargetPosition);
                if (viewport.z <= 0f)
                {
                    continue;
                }

                Vector2 targetGui = HandleUtility.WorldToGUIPoint(label.TargetPosition);
                if (!IsReasonablyNearView(targetGui, viewRect))
                {
                    continue;
                }

                Rect rect = CreatePreferredRect(label.Text, targetGui);
                rect = ClampToView(rect, viewRect);
                Layouts.Add(new LabelLayout(label.Text, targetGui, rect));
            }
        }

        private static Rect CreatePreferredRect(string text, Vector2 targetGui)
        {
            scratchContent.text = text;
            Vector2 unconstrained = textStyle.CalcSize(scratchContent);
            float contentWidth = Mathf.Min(unconstrained.x, MaxLabelWidth - (PaddingX * 2f));
            float contentHeight = textStyle.CalcHeight(scratchContent, contentWidth);
            var size = new Vector2(contentWidth + (PaddingX * 2f), contentHeight + (PaddingY * 2f));
            return new Rect(targetGui.x - (size.x * 0.5f), targetGui.y - PreferredYOffset - size.y, size.x, size.y);
        }

        private static Rect BuildViewRect(SceneView sceneView)
        {
            return new Rect(
                ViewMargin,
                ViewMargin,
                Mathf.Max(1f, sceneView.position.width - (ViewMargin * 2f)),
                Mathf.Max(1f, sceneView.position.height - (ViewMargin * 2f)));
        }

        private static bool IsReasonablyNearView(Vector2 point, Rect viewRect)
        {
            const float slack = 240f;
            return point.x >= viewRect.xMin - slack &&
                   point.x <= viewRect.xMax + slack &&
                   point.y >= viewRect.yMin - slack &&
                   point.y <= viewRect.yMax + slack;
        }

        private static void SeparateLayouts(Rect viewRect)
        {
            for (int pass = 0; pass < SeparationPasses; pass++)
            {
                bool moved = false;
                for (int i = 0; i < Layouts.Count; i++)
                {
                    for (int j = i + 1; j < Layouts.Count; j++)
                    {
                        if (!TrySeparate(Layouts[i], Layouts[j], out var moveA, out var moveB))
                        {
                            continue;
                        }

                        Layouts[i].Rect = ClampToView(Move(Layouts[i].Rect, moveA), viewRect);
                        Layouts[j].Rect = ClampToView(Move(Layouts[j].Rect, moveB), viewRect);
                        moved = true;
                    }
                }

                if (!moved)
                {
                    break;
                }
            }
        }

        private static bool TrySeparate(LabelLayout a, LabelLayout b, out Vector2 moveA, out Vector2 moveB)
        {
            moveA = default;
            moveB = default;

            Rect ar = Expand(a.Rect, CollisionPadding);
            Rect br = Expand(b.Rect, CollisionPadding);
            if (!ar.Overlaps(br))
            {
                return false;
            }

            Vector2 delta = b.Rect.center - a.Rect.center;
            if (delta.sqrMagnitude < 0.001f)
            {
                delta = new Vector2(1f, 1f);
            }

            float overlapX = ((ar.width + br.width) * 0.5f) - Mathf.Abs(delta.x);
            float overlapY = ((ar.height + br.height) * 0.5f) - Mathf.Abs(delta.y);
            if (overlapX <= 0f || overlapY <= 0f)
            {
                return false;
            }

            if (overlapX < overlapY)
            {
                float direction = delta.x >= 0f ? 1f : -1f;
                var push = new Vector2((overlapX * 0.5f + 0.5f) * direction, 0f);
                moveA = -push;
                moveB = push;
            }
            else
            {
                float direction = delta.y >= 0f ? 1f : -1f;
                var push = new Vector2(0f, (overlapY * 0.5f + 0.5f) * direction);
                moveA = -push;
                moveB = push;
            }

            return true;
        }

        private static Rect Expand(Rect rect, float amount)
        {
            return new Rect(
                rect.x - amount,
                rect.y - amount,
                rect.width + (amount * 2f),
                rect.height + (amount * 2f));
        }

        private static Rect Move(Rect rect, Vector2 delta)
        {
            rect.position += delta;
            return rect;
        }

        private static Rect ClampToView(Rect rect, Rect viewRect)
        {
            rect.x = Mathf.Clamp(rect.x, viewRect.xMin, Mathf.Max(viewRect.xMin, viewRect.xMax - rect.width));
            rect.y = Mathf.Clamp(rect.y, viewRect.yMin, Mathf.Max(viewRect.yMin, viewRect.yMax - rect.height));
            return rect;
        }

        private static void DrawLayouts()
        {
            Handles.BeginGUI();
            var previousColor = Handles.color;
            try
            {
                Handles.color = ConnectorColor;
                foreach (var layout in Layouts)
                {
                    Vector2 edge = ClosestRectEdgePoint(layout.Rect, layout.TargetGui);
                    Handles.DrawAAPolyLine(
                        ConnectorWidth,
                        new Vector3(edge.x, edge.y, 0f),
                        new Vector3(layout.TargetGui.x, layout.TargetGui.y, 0f));
                }

                foreach (var layout in Layouts)
                {
                    GUI.Box(layout.Rect, GUIContent.none, backgroundStyle);
                    GUI.Label(
                        new Rect(
                            layout.Rect.x + PaddingX,
                            layout.Rect.y + PaddingY,
                            layout.Rect.width - (PaddingX * 2f),
                            layout.Rect.height - (PaddingY * 2f)),
                        layout.Text,
                        textStyle);
                }
            }
            finally
            {
                Handles.color = previousColor;
                Handles.EndGUI();
            }
        }

        private static Vector2 ClosestRectEdgePoint(Rect rect, Vector2 target)
        {
            Vector2 center = rect.center;
            Vector2 direction = target - center;
            if (direction.sqrMagnitude < 0.001f)
            {
                return center;
            }

            float halfWidth = rect.width * 0.5f;
            float halfHeight = rect.height * 0.5f;
            float tx = Mathf.Abs(direction.x) > 0.001f ? halfWidth / Mathf.Abs(direction.x) : float.PositiveInfinity;
            float ty = Mathf.Abs(direction.y) > 0.001f ? halfHeight / Mathf.Abs(direction.y) : float.PositiveInfinity;
            float t = Mathf.Min(tx, ty);
            return center + (direction * t);
        }

        private static void EnsureStyles()
        {
            if (scratchContent == null)
            {
                scratchContent = new GUIContent();
            }

            if (textStyle == null)
            {
                textStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                    fontSize = 11,
                    richText = false,
                    wordWrap = true
                };
                textStyle.normal.textColor = TextColor;
            }

            if (backgroundStyle == null)
            {
                backgroundStyle = new GUIStyle();
                backgroundStyle.normal.background = ReloadScopedObjects.Keep(CreateRoundedRectTexture(18, 6f, BackgroundColor));
                backgroundStyle.border = new RectOffset(6, 6, 6, 6);
            }
        }

        private static Texture2D CreateRoundedRectTexture(int size, float radius, Color color)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "Avatar gizmos label background",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color[size * size];
            float max = size - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x;
                    float py = y;
                    float cx = Mathf.Clamp(px, radius, max - radius);
                    float cy = Mathf.Clamp(py, radius, max - radius);
                    float distance = Vector2.Distance(new Vector2(px, py), new Vector2(cx, cy));
                    float alpha = Mathf.Clamp01(radius + 0.5f - distance);
                    var pixel = color;
                    pixel.a *= alpha;
                    pixels[(y * size) + x] = pixel;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private sealed class LabelLayout
        {
            public readonly string Text;
            public readonly Vector2 TargetGui;
            public Rect Rect;

            public LabelLayout(string text, Vector2 targetGui, Rect rect)
            {
                Text = text;
                TargetGui = targetGui;
                Rect = rect;
            }
        }
    }
}
