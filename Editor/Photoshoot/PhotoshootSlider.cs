using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// A slim slider: a rounded track filled from its origin (its minimum, or zero when the range crosses it) to the
    /// value, with a knob. It follows the pointer from the press (click anywhere on it, or drag); the arrow keys step it.
    /// Whole-number sliders snap. Colours come from <c>--track-color</c> and <c>--fill-color</c>.
    /// </summary>
    internal sealed class PhotoshootSlider : VisualElement
    {
        private static readonly CustomStyleProperty<Color> TrackProperty = new CustomStyleProperty<Color>("--track-color");
        private static readonly CustomStyleProperty<Color> FillProperty = new CustomStyleProperty<Color>("--fill-color");
        private const float KnobRadius = 6f;
        private readonly float min, max;
        private readonly bool whole;
        private readonly Action<float> changed;
        private readonly PointerDragCapture drag;
        private Color track = new Color32(0x3a, 0x3a, 0x3a, 0xff), fill = new Color32(0x00, 0xda, 0x6d, 0xff);

        public float Value { get; private set; }

        public PhotoshootSlider(float value, Action<float> changed) : this(0f, 1f, value, changed) { }

        public PhotoshootSlider(float min, float max, float value, Action<float> changed, bool whole = false)
        {
            this.min = min;
            this.max = Mathf.Max(min + 0.0001f, max);
            this.whole = whole;
            this.changed = changed;
            Value = Snap(value);
            AddToClassList("ps-slider");
            focusable = true;
            generateVisualContent += Draw;
            drag = new PointerDragCapture(this, () => RemoveFromClassList("ps-slider--dragging"));
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                drag.Begin(evt.pointerId);
                AddToClassList("ps-slider--dragging");
                SetFrom(evt.localPosition.x);
                evt.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!drag.Owns(evt.pointerId)) return;
                SetFrom(evt.localPosition.x);
                evt.StopPropagation();
            });
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.LeftArrow && evt.keyCode != KeyCode.RightArrow) return;
                float step = whole ? 1f : (this.max - this.min) * 0.05f;
                Set(Value + (evt.keyCode == KeyCode.RightArrow ? step : -step));
                evt.StopPropagation();
            });
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                if (customStyle.TryGetValue(TrackProperty, out var resolvedTrack)) track = resolvedTrack;
                if (customStyle.TryGetValue(FillProperty, out var resolvedFill)) fill = resolvedFill;
                MarkDirtyRepaint();
            });
        }

        public void SetValueWithoutNotify(float value)
        {
            Value = Snap(value);
            MarkDirtyRepaint();
        }

        private float Snap(float value)
        {
            value = Mathf.Clamp(value, min, max);
            return whole ? Mathf.Round(value) : value;
        }

        private float Fraction(float value) => (value - min) / (max - min);

        private void SetFrom(float x)
        {
            float width = contentRect.width - KnobRadius * 2f;
            if (width > 0f) Set(Mathf.Lerp(min, max, (x - KnobRadius) / width));
        }

        private void Set(float value)
        {
            value = Snap(value);
            if (Mathf.Approximately(value, Value)) return;
            Value = value;
            MarkDirtyRepaint();
            changed?.Invoke(value);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= KnobRadius * 2f) return;
            float y = rect.center.y, left = rect.x + KnobRadius, right = rect.xMax - KnobRadius;
            float x = Mathf.Lerp(left, right, Fraction(Value));
            float origin = Mathf.Lerp(left, right, Fraction(min < 0f && max > 0f ? 0f : min));
            var painter = context.painter2D;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = 4f;
            painter.strokeColor = track;
            painter.BeginPath(); painter.MoveTo(new Vector2(left, y)); painter.LineTo(new Vector2(right, y)); painter.Stroke();
            if (Mathf.Abs(x - origin) > 0.01f)
            {
                painter.strokeColor = fill;
                painter.BeginPath(); painter.MoveTo(new Vector2(origin, y)); painter.LineTo(new Vector2(x, y)); painter.Stroke();
            }
            if (min < 0f && max > 0f)
            {
                // The zero mark of a slider that goes both ways.
                painter.fillColor = new Color(1f, 1f, 1f, 0.35f);
                painter.BeginPath(); painter.Arc(new Vector2(origin, y), 1.6f, 0f, 360f); painter.Fill();
            }
            painter.fillColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            painter.BeginPath(); painter.Arc(new Vector2(x, y), KnobRadius, 0f, 360f); painter.Fill();
        }
    }
}
