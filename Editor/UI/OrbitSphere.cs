using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A ball turned like the thing it stands for: dragging sideways turns it around its vertical axis, up and down tilts
    /// it toward or away from the viewer. Its lines of latitude and longitude turn with it and a dot shows where it faces.
    /// Snaps upright and facing at zero, steps with the arrow keys when focused, and double-click asks for a reset.
    /// </summary>
    public sealed class OrbitSphere : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/orbit-sphere.uss";
        private const float DegreesPerPixel = .6f, Snap = 2f, Step = 5f;
        private static readonly Color Accent = new Color32(0, 218, 109, 255);

        private readonly VisualElement ball;
        private readonly Label readout;
        private readonly Action began, reset;
        private readonly Action<float, float> changed;
        private readonly float maxTilt;
        private readonly PointerDragCapture drag;
        private Vector2 last;

        public float Yaw { get; private set; }
        public float Tilt { get; private set; }

        /// <param name="changed">New yaw (degrees, -180 to 180) and tilt.</param>
        /// <param name="began">A drag or key turn starts.</param>
        /// <param name="reset">Double-click: the owner animates back to facing.</param>
        public OrbitSphere(float maxTilt, Action<float, float> changed, Action began = null, Action reset = null)
        {
            this.maxTilt = maxTilt; this.changed = changed; this.began = began; this.reset = reset;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-sphere");
            ball = new VisualElement { focusable = true, tabIndex = 0 };
            ball.AddToClassList("orb-sphere__ball");
            ball.generateVisualContent += Draw;
            Add(ball);
            readout = new Label();
            readout.AddToClassList("orb-sphere__readout");
            Add(readout);
            drag = new PointerDragCapture(ball, () => RemoveFromClassList("orb-sphere--active"));
            ball.RegisterCallback<PointerDownEvent>(OnDown);
            ball.RegisterCallback<PointerMoveEvent>(OnMove);
            ball.RegisterCallback<KeyDownEvent>(OnKey);
            UpdateReadout();
        }

        public void SetValueWithoutNotify(float yaw, float tilt)
        {
            Yaw = Mathf.DeltaAngle(0f, yaw);
            Tilt = Mathf.Clamp(tilt, -maxTilt, maxTilt);
            UpdateReadout();
            ball.MarkDirtyRepaint();
        }

        private void UpdateReadout() =>
            readout.text = Mathf.Abs(Tilt) < .5f ? $"{Mathf.RoundToInt(Yaw)}°" : $"{Mathf.RoundToInt(Yaw)}° · tilt {Mathf.RoundToInt(Tilt)}°";

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            if (evt.clickCount == 2) { reset?.Invoke(); evt.StopPropagation(); return; }
            ball.Focus();
            last = evt.position;
            began?.Invoke();
            drag.Begin(evt.pointerId);
            AddToClassList("orb-sphere--active");
            evt.StopPropagation();
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (!drag.Owns(evt.pointerId)) return;
            Vector2 delta = (Vector2)evt.position - last;
            last = evt.position;
            Set(Yaw - delta.x * DegreesPerPixel, Tilt + delta.y * DegreesPerPixel);
        }

        private void OnKey(KeyDownEvent evt)
        {
            float yaw = Yaw, tilt = Tilt;
            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow: yaw += Step; break;
                case KeyCode.RightArrow: yaw -= Step; break;
                case KeyCode.UpArrow: tilt -= Step; break;
                case KeyCode.DownArrow: tilt += Step; break;
                default: return;
            }
            began?.Invoke();
            Set(yaw, tilt);
            evt.StopPropagation();
        }

        private void Set(float yaw, float tilt)
        {
            yaw = Mathf.DeltaAngle(0f, yaw);
            if (Mathf.Abs(yaw) < Snap) yaw = 0f;
            tilt = Mathf.Clamp(tilt, -maxTilt, maxTilt);
            if (Mathf.Abs(tilt) < Snap) tilt = 0f;
            if (Mathf.Approximately(yaw, Yaw) && Mathf.Approximately(tilt, Tilt)) return;
            SetValueWithoutNotify(yaw, tilt);
            changed?.Invoke(Yaw, Tilt);
        }

        // ---- Drawing: a lit ball, its turning grid, and the facing dot ----

        // The ball as the viewer sees it: x to the right, y down, z toward the viewer.
        private Vector3 View(Vector3 point)
        {
            var turned = Quaternion.AngleAxis(Tilt, Vector3.right) * Quaternion.AngleAxis(Yaw, Vector3.up) * point;
            return new Vector3(-turned.x, -turned.y, turned.z);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = ball.contentRect;
            float radius = Mathf.Min(rect.width, rect.height) * .5f - 1f;
            if (radius <= 4f) return;
            var center = rect.center;
            DrawShading(context, center, radius);

            var painter = context.painter2D;
            painter.lineCap = LineCap.Round;
            var front = new List<(Vector2, Vector2)>();
            var back = new List<(Vector2, Vector2)>();
            var equatorFront = new List<(Vector2, Vector2)>();
            void Segment(Vector3 a, Vector3 b, List<(Vector2, Vector2)> frontList)
            {
                Vector3 va = View(a), vb = View(b);
                var segment = (center + new Vector2(va.x, va.y) * radius, center + new Vector2(vb.x, vb.y) * radius);
                (va.z + vb.z >= 0f ? frontList : back).Add(segment);
            }
            const int steps = 48;
            // Meridians every 30°, parallels every 30°; the equator in the accent colour.
            for (int m = 0; m < 6; m++)
            {
                float longitude = m * 30f * Mathf.Deg2Rad;
                for (int i = 0; i < steps; i++)
                {
                    float a0 = i * Mathf.PI * 2f / steps, a1 = (i + 1) * Mathf.PI * 2f / steps;
                    Vector3 P(float a) => new Vector3(Mathf.Cos(a) * Mathf.Sin(longitude), Mathf.Sin(a), Mathf.Cos(a) * Mathf.Cos(longitude));
                    Segment(P(a0), P(a1), front);
                }
            }
            foreach (float latitudeDegrees in new[] { -60f, -30f, 0f, 30f, 60f })
            {
                float latitude = latitudeDegrees * Mathf.Deg2Rad, y = Mathf.Sin(latitude), r = Mathf.Cos(latitude);
                for (int i = 0; i < steps; i++)
                {
                    float a0 = i * Mathf.PI * 2f / steps, a1 = (i + 1) * Mathf.PI * 2f / steps;
                    Segment(new Vector3(Mathf.Sin(a0) * r, y, Mathf.Cos(a0) * r), new Vector3(Mathf.Sin(a1) * r, y, Mathf.Cos(a1) * r),
                        latitudeDegrees == 0f ? equatorFront : front);
                }
            }
            Stroke(painter, back, new Color(1f, 1f, 1f, .05f), 1f);
            Stroke(painter, front, new Color(1f, 1f, 1f, .16f), 1f);
            Stroke(painter, equatorFront, new Color(Accent.r, Accent.g, Accent.b, .55f), 1.4f);

            // Where it faces: a glowing dot, dimmed when it faces away.
            Vector3 facing = View(Vector3.forward);
            var dot = center + new Vector2(facing.x, facing.y) * radius * .98f;
            bool visible = facing.z > -.05f;
            float alpha = visible ? 1f : .3f;
            painter.fillColor = new Color(Accent.r, Accent.g, Accent.b, .22f * alpha);
            painter.BeginPath(); painter.Arc(dot, 9f, 0f, 360f); painter.Fill();
            painter.fillColor = new Color(Accent.r, Accent.g, Accent.b, alpha);
            painter.BeginPath(); painter.Arc(dot, 4.2f, 0f, 360f); painter.Fill();
            // The top of the head, so a tilt reads at a glance.
            Vector3 top = View(Vector3.up);
            if (top.z > -.05f)
            {
                painter.fillColor = new Color(1f, 1f, 1f, .75f);
                painter.BeginPath(); painter.Arc(center + new Vector2(top.x, top.y) * radius * .98f, 2.4f, 0f, 360f); painter.Fill();
            }
        }

        private static void Stroke(Painter2D painter, List<(Vector2 a, Vector2 b)> segments, Color color, float width)
        {
            if (segments.Count == 0) return;
            painter.strokeColor = color;
            painter.lineWidth = width;
            painter.BeginPath();
            foreach (var (a, b) in segments) { painter.MoveTo(a); painter.LineTo(b); }
            painter.Stroke();
        }

        // A disc lit from the top left, darker toward its rim: rings of vertices with per-vertex colours.
        private static void DrawShading(MeshGenerationContext context, Vector2 center, float radius)
        {
            const int rings = 10, segments = 64;
            var light = new Vector3(-.45f, -.55f, .7f).normalized;
            var vertices = new Vertex[1 + rings * segments];
            Color32 Shade(Vector2 p)
            {
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - p.sqrMagnitude));
                var normal = new Vector3(p.x, p.y, z);
                float lambert = Mathf.Max(0f, Vector3.Dot(normal, light));
                float rim = Mathf.Pow(1f - z, 3f);
                float v = Mathf.Lerp(.085f, .26f, lambert) + rim * .05f;
                var c = new Color(v, v * 1.02f, v * 1.06f, 1f);
                return c;
            }
            vertices[0] = new Vertex { position = new Vector3(center.x, center.y, Vertex.nearZ), tint = Shade(Vector2.zero) };
            for (int r = 0; r < rings; r++)
            {
                float t = (r + 1f) / rings;
                for (int s = 0; s < segments; s++)
                {
                    float a = s * Mathf.PI * 2f / segments;
                    var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * t;
                    vertices[1 + r * segments + s] = new Vertex { position = new Vector3(center.x + p.x * radius, center.y + p.y * radius, Vertex.nearZ), tint = Shade(p) };
                }
            }
            var indices = new List<ushort>();
            for (int s = 0; s < segments; s++)
            {
                int n = (s + 1) % segments;
                indices.Add(0); indices.Add((ushort)(1 + s)); indices.Add((ushort)(1 + n));
            }
            for (int r = 1; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int n = (s + 1) % segments;
                    ushort a = (ushort)(1 + (r - 1) * segments + s), b = (ushort)(1 + (r - 1) * segments + n);
                    ushort c = (ushort)(1 + r * segments + s), d = (ushort)(1 + r * segments + n);
                    indices.Add(a); indices.Add(c); indices.Add(d);
                    indices.Add(a); indices.Add(d); indices.Add(b);
                }
            }
            var mesh = context.Allocate(vertices.Length, indices.Count);
            mesh.SetAllVertices(vertices);
            mesh.SetAllIndices(indices.ToArray());
        }
    }
}
