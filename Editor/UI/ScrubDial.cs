using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A ruler dragged like a physical dial: the ticks slide under a fixed centre mark. It snaps at its rest value,
    /// steps with the arrow keys when focused, and double-click returns it to rest. A looping dial has no ends: past its
    /// maximum it continues from its minimum, like a turntable.
    /// </summary>
    public sealed class ScrubDial : VisualElement
    {
        private readonly float min, max, tick, pixelsPerUnit, rest, snapRange;
        private readonly int majorEvery;
        private readonly bool loops;
        private readonly Func<float, string> format;
        private readonly Action<float> changed;
        private readonly VisualElement strip;
        private readonly Label readout;
        private float value, dragStartX, dragStartValue;
        private readonly PointerDragCapture drag;

        public ScrubDial(string label, float min, float max, float rest, float tick, int majorEvery, float pixelsPerUnit, float snapRange,
            Func<float, string> format, Action<float> changed, bool loops = false)
        {
            this.min = min; this.max = max; this.rest = rest; this.tick = tick; this.majorEvery = Mathf.Max(1, majorEvery); this.loops = loops;
            this.pixelsPerUnit = pixelsPerUnit; this.snapRange = snapRange; this.format = format; this.changed = changed;
            AddToClassList("orb-dial");
            var sheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Editor/UI/scrub-dial.uss");
            if (sheet) styleSheets.Add(sheet);

            var name = new Label(label);
            name.AddToClassList("orb-dial__label");
            Add(name);
            strip = new VisualElement { focusable = true, tabIndex = 0 };
            strip.AddToClassList("orb-dial__strip");
            strip.generateVisualContent += DrawTicks;
            Add(strip);
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.AddToClassList("orb-dial__mark");
            strip.Add(mark);
            readout = new Label();
            readout.AddToClassList("orb-dial__value");
            Add(readout);

            strip.RegisterCallback<PointerDownEvent>(OnPointerDown);
            strip.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            drag = new PointerDragCapture(strip, () => RemoveFromClassList("orb-dial--active"));
            strip.RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        private float Limit(float raw) => loops ? min + Mathf.Repeat(raw - min, max - min) : Mathf.Clamp(raw, min, max);

        // Distance from rest, the short way round on a looping dial.
        private float FromRest(float raw) => loops ? Mathf.DeltaAngle(0f, (raw - rest) * 360f / (max - min)) * (max - min) / 360f : raw - rest;

        public void SetValueWithoutNotify(float newValue)
        {
            value = Limit(newValue);
            readout.text = format(value);
            strip.MarkDirtyRepaint();
        }

        private void Set(float newValue)
        {
            newValue = Limit(newValue);
            if (Mathf.Approximately(newValue, value)) return;
            SetValueWithoutNotify(newValue);
            changed(value);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            strip.Focus();
            if (evt.clickCount == 2) { Set(rest); evt.StopPropagation(); return; }
            dragStartX = evt.position.x;
            dragStartValue = value;
            drag.Begin(evt.pointerId);
            AddToClassList("orb-dial--active");
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!drag.Owns(evt.pointerId)) return;
            // Dragging the ruler left brings higher values under the mark. Near rest the value holds, like a detent.
            float raw = dragStartValue - (evt.position.x - dragStartX) / pixelsPerUnit;
            if (Mathf.Abs(FromRest(raw)) < snapRange) raw = rest;
            try { Set(raw); }
            catch { drag.End(); throw; }
            evt.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            float step = evt.shiftKey ? tick * majorEvery : tick;
            if (evt.keyCode == KeyCode.LeftArrow) Set(value - step);
            else if (evt.keyCode == KeyCode.RightArrow) Set(value + step);
            else if (evt.keyCode == KeyCode.Home) Set(rest);
            else return;
            evt.StopPropagation();
        }

        private void DrawTicks(MeshGenerationContext context)
        {
            var rect = strip.contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;
            var painter = context.painter2D;
            float centre = rect.width * 0.5f;
            painter.lineWidth = 1.5f;
            painter.lineCap = LineCap.Round;
            // A looping dial draws the ticks around the current value and wraps their meaning; a bounded one stops at its ends.
            float reach = centre / pixelsPerUnit;
            int first = loops ? Mathf.FloorToInt((value - reach) / tick) : Mathf.CeilToInt(min / tick - 0.001f);
            int last = loops ? Mathf.CeilToInt((value + reach) / tick) : Mathf.FloorToInt(max / tick + 0.001f);
            for (int i = first; i <= last; i++)
            {
                float at = i * tick;
                float x = centre + (at - value) * pixelsPerUnit;
                if (x < 2f || x > rect.width - 2f) continue;
                // Ticks fade towards the ends, so the ruler reads as a wheel turning away from view.
                float fade = 1f - Mathf.Abs(x - centre) / centre;
                fade = fade * fade * (3f - 2f * fade);
                bool major = ((i % majorEvery) + majorEvery) % majorEvery == 0;
                bool restTick = Mathf.Abs(FromRest(at)) < tick * 0.5f;
                float length = rect.height * (restTick ? 0.62f : major ? 0.46f : 0.24f);
                painter.strokeColor = new Color(1f, 1f, 1f, (restTick ? 0.85f : major ? 0.6f : 0.3f) * fade);
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, (rect.height - length) * 0.5f));
                painter.LineTo(new Vector2(x, (rect.height + length) * 0.5f));
                painter.Stroke();
            }
        }
    }
}
