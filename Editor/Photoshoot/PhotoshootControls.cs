using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>Inline colour picker: saturation and brightness area, hue bar, a few curated colours, hex entry and reset.</summary>
    internal sealed class InlineColorPicker : VisualElement
    {
        private static readonly string[] Swatches = { "#303030", "#101218", "#F2F2F2", "#00DA6D", "#1D3B8B", "#7C3AED", "#F472B6", "#F59E0B", "#0EA5E9" };
        private readonly Color rest;
        private readonly Action<Color> changed;
        private readonly VisualElement area, areaKnob, hue, hueKnob;
        private readonly TextField hex;
        private float h, s, v;

        internal InlineColorPicker(Color color, Color rest, Action<Color> changed)
        {
            this.rest = rest; this.changed = changed;
            AddToClassList("ps-picker");

            area = new VisualElement();
            area.AddToClassList("ps-picker__area");
            area.generateVisualContent += DrawArea;
            Add(area);
            areaKnob = new VisualElement { pickingMode = PickingMode.Ignore };
            areaKnob.AddToClassList("ps-picker__knob");
            area.Add(areaKnob);
            area.RegisterCallback<GeometryChangedEvent>(_ => PlaceKnobs());
            Drag(area, local =>
            {
                s = Mathf.Clamp01(local.x / Mathf.Max(1f, area.contentRect.width));
                v = 1f - Mathf.Clamp01(local.y / Mathf.Max(1f, area.contentRect.height));
                Apply(true);
            });

            hue = new VisualElement();
            hue.AddToClassList("ps-picker__hue");
            hue.generateVisualContent += DrawHue;
            Add(hue);
            hueKnob = new VisualElement { pickingMode = PickingMode.Ignore };
            hueKnob.AddToClassList("ps-picker__knob");
            hueKnob.AddToClassList("ps-picker__knob--hue");
            hue.Add(hueKnob);
            hue.RegisterCallback<GeometryChangedEvent>(_ => PlaceKnobs());
            Drag(hue, local =>
            {
                h = Mathf.Clamp01(local.x / Mathf.Max(1f, hue.contentRect.width));
                if (h >= 1f) h = 0.9999f;
                area.MarkDirtyRepaint();
                Apply(true);
            });

            var footer = new VisualElement();
            footer.AddToClassList("ps-picker__footer");
            Add(footer);
            var swatches = new VisualElement();
            swatches.AddToClassList("ps-picker__swatches");
            footer.Add(swatches);
            foreach (string html in Swatches)
            {
                if (!ColorUtility.TryParseHtmlString(html, out Color swatchColor)) continue;
                var dot = new VisualElement { tooltip = html };
                dot.AddToClassList("ps-picker__swatch");
                dot.style.backgroundColor = swatchColor;
                dot.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { SetColor(swatchColor, true); evt.StopPropagation(); } });
                swatches.Add(dot);
            }

            var entry = new VisualElement();
            entry.AddToClassList("ps-picker__entry");
            footer.Add(entry);
            hex = new TextField { isDelayed = true, maxLength = 7 };
            hex.AddToClassList("ps-picker__hex");
            hex.RegisterValueChangedCallback(evt =>
            {
                string text = evt.newValue.Trim();
                if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
                if (ColorUtility.TryParseHtmlString(text, out Color parsed)) SetColor(parsed, true);
                else hex.SetValueWithoutNotify(Html());
            });
            entry.Add(hex);
            var reset = new Button { text = "Reset", tooltip = "Back to the default background colour." };
            reset.AddToClassList("ps-link");
            reset.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { SetColor(this.rest, true); evt.StopPropagation(); } }, TrickleDown.TrickleDown);
            reset.clicked += () => SetColor(this.rest, true);
            entry.Add(reset);

            SetColor(color, false);
        }

        internal Color Value => Color.HSVToRGB(h, s, v);

        private void SetColor(Color color, bool notify)
        {
            Color.RGBToHSV(color, out float newH, out float newS, out float newV);
            // Greys carry no hue; keep the current one so the area does not jump.
            if (newS > 0.001f && newV > 0.001f) h = newH;
            s = newS; v = newV;
            area.MarkDirtyRepaint();
            Apply(notify);
        }

        private void Apply(bool notify)
        {
            Color color = Value;
            areaKnob.style.backgroundColor = color;
            hueKnob.style.backgroundColor = Color.HSVToRGB(h, 1f, 1f);
            hex.SetValueWithoutNotify(Html());
            PlaceKnobs();
            if (notify) changed(color);
        }

        private string Html() => "#" + ColorUtility.ToHtmlStringRGB(Value);

        private void PlaceKnobs()
        {
            var rect = area.contentRect;
            if (rect.width > 0f)
            {
                areaKnob.style.left = s * rect.width - 7f;
                areaKnob.style.top = (1f - v) * rect.height - 7f;
            }
            var hueRect = hue.contentRect;
            if (hueRect.width > 0f) hueKnob.style.left = h * hueRect.width - 7f;
        }

        // Pointer drag with capture that reports the local position on press and while moving.
        private static void Drag(VisualElement target, Action<Vector2> moved)
        {
            var drag = new PointerDragCapture(target);
            target.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                drag.Begin(evt.pointerId);
                try { moved(evt.localPosition); }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            });
            target.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!drag.Owns(evt.pointerId)) return;
                try { moved(evt.localPosition); }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            });
        }

        // White to hue across, then transparent to black down: two linear gradients give exact saturation and value.
        private void DrawArea(MeshGenerationContext context)
        {
            var rect = area.contentRect;
            Quad(context, rect, Color.white, Color.HSVToRGB(h, 1f, 1f), Color.HSVToRGB(h, 1f, 1f), Color.white);
            Quad(context, rect, new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f), Color.black, Color.black);
        }

        private void DrawHue(MeshGenerationContext context)
        {
            var rect = hue.contentRect;
            const int segments = 6;
            float width = rect.width / segments;
            for (int i = 0; i < segments; i++)
            {
                Color from = Color.HSVToRGB(i / (float)segments, 1f, 1f);
                Color to = Color.HSVToRGB(((i + 1) % segments) / (float)segments, 1f, 1f);
                Quad(context, new Rect(rect.x + i * width, rect.y, width + 0.5f, rect.height), from, to, to, from);
            }
        }

        // Corners in clockwise order from the top left.
        private static void Quad(MeshGenerationContext context, Rect rect, Color topLeft, Color topRight, Color bottomRight, Color bottomLeft)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            var mesh = context.Allocate(4, 6);
            mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ), tint = topLeft });
            mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ), tint = topRight });
            mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ), tint = bottomRight });
            mesh.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ), tint = bottomLeft });
            mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
            mesh.SetNextIndex(0); mesh.SetNextIndex(2); mesh.SetNextIndex(3);
        }
    }
}
