using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    public enum IconGlyph { Bones, Mirror, Clothes, Refresh, Person, PersonOff, People, Robot, RobotOff, Lock, Unlock, Branch, Close, Chevron, Gauge, Sliders, Sound }

    /// <summary>
    /// A small line icon drawn with the vector API, crisp at any size. Its colour comes from the USS custom property
    /// <c>--icon-color</c>, so hover and selected states restyle it like text.
    /// </summary>
    public sealed class VectorIcon : VisualElement
    {
        private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--icon-color");
        private Color color = new Color(0.85f, 0.85f, 0.85f);
        private IconGlyph glyph;

        public IconGlyph Glyph
        {
            get => glyph;
            set { glyph = value; MarkDirtyRepaint(); }
        }

        public VectorIcon(IconGlyph glyph)
        {
            this.glyph = glyph;
            AddToClassList("orb-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; MarkDirtyRepaint(); }
            });
        }

        // Glyphs are drawn in a 24 x 24 box, scaled to the element.
        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float scale = Mathf.Min(rect.width, rect.height) / 24f;
            if (scale <= 0f) return;
            var offset = new Vector2(rect.x + (rect.width - 24f * scale) * 0.5f, rect.y + (rect.height - 24f * scale) * 0.5f);
            Vector2 P(float x, float y) => offset + new Vector2(x, y) * scale;
            var painter = context.painter2D;
            painter.strokeColor = color;
            painter.fillColor = color;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            switch (glyph)
            {
                case IconGlyph.Bones:
                    painter.lineWidth = 3.2f * scale;
                    painter.BeginPath(); painter.MoveTo(P(8f, 16f)); painter.LineTo(P(16f, 8f)); painter.Stroke();
                    foreach (var knob in new[] { P(6.2f, 15.4f), P(8.6f, 17.8f), P(15.4f, 6.2f), P(17.8f, 8.6f) })
                    {
                        painter.BeginPath(); painter.Arc(knob, 2.4f * scale, 0f, 360f); painter.Fill();
                    }
                    break;
                case IconGlyph.Mirror:
                    painter.lineWidth = 1.6f * scale;
                    for (float y = 3f; y < 21f; y += 4f) { painter.BeginPath(); painter.MoveTo(P(12f, y)); painter.LineTo(P(12f, y + 2f)); painter.Stroke(); }
                    painter.BeginPath(); painter.MoveTo(P(9.5f, 6f)); painter.LineTo(P(9.5f, 18f)); painter.LineTo(P(3f, 18f)); painter.ClosePath(); painter.Fill();
                    painter.BeginPath(); painter.MoveTo(P(14.5f, 6f)); painter.LineTo(P(14.5f, 18f)); painter.LineTo(P(21f, 18f)); painter.ClosePath(); painter.Stroke();
                    break;
                case IconGlyph.Clothes:
                    painter.lineWidth = 1.8f * scale;
                    painter.BeginPath();
                    painter.MoveTo(P(8.5f, 4f)); painter.LineTo(P(3.5f, 6.8f)); painter.LineTo(P(2.5f, 11f)); painter.LineTo(P(6.2f, 11.8f));
                    painter.LineTo(P(6.2f, 20f)); painter.LineTo(P(17.8f, 20f)); painter.LineTo(P(17.8f, 11.8f)); painter.LineTo(P(21.5f, 11f));
                    painter.LineTo(P(20.5f, 6.8f)); painter.LineTo(P(15.5f, 4f)); painter.QuadraticCurveTo(P(12f, 8f), P(8.5f, 4f));
                    painter.ClosePath(); painter.Stroke();
                    break;
                case IconGlyph.Refresh:
                    painter.lineWidth = 2f * scale;
                    painter.BeginPath(); painter.Arc(P(12f, 12f), 7f * scale, 30f, 300f); painter.Stroke();
                    // Arrow head at the end of the arc, pointing along it.
                    var end = P(12f + 7f * Mathf.Cos(300f * Mathf.Deg2Rad), 12f + 7f * Mathf.Sin(300f * Mathf.Deg2Rad));
                    var along = new Vector2(-Mathf.Sin(300f * Mathf.Deg2Rad), Mathf.Cos(300f * Mathf.Deg2Rad));
                    var across = new Vector2(Mathf.Cos(300f * Mathf.Deg2Rad), Mathf.Sin(300f * Mathf.Deg2Rad));
                    painter.BeginPath(); painter.MoveTo(end + along * 3f * scale); painter.LineTo(end + across * 3f * scale); painter.LineTo(end - across * 3f * scale); painter.ClosePath(); painter.Fill();
                    break;
                case IconGlyph.Person:
                    painter.lineWidth = 1.9f * scale;
                    Person(painter, P, 12f, 1f);
                    break;
                case IconGlyph.PersonOff:
                    painter.lineWidth = 1.9f * scale;
                    Person(painter, P, 12f, 1f);
                    Slash(painter, P);
                    break;
                case IconGlyph.People:
                    // One person in front, a second peeking out behind: reads as "people" even at 14 px.
                    painter.lineWidth = 1.8f * scale;
                    Person(painter, P, 9f, 0.95f);
                    painter.BeginPath(); painter.Arc(P(16.6f, 7.4f), 2.9f * scale, 250f, 110f + 360f); painter.Stroke();
                    painter.BeginPath(); painter.MoveTo(P(15.8f, 13.2f)); painter.BezierCurveTo(P(19.4f, 13.4f), P(21.8f, 15.4f), P(21.8f, 20.2f)); painter.Stroke();
                    break;
                case IconGlyph.Robot:
                case IconGlyph.RobotOff:
                    painter.lineWidth = 1.8f * scale;
                    painter.BeginPath(); painter.MoveTo(P(12f, 8.5f)); painter.LineTo(P(12f, 5.5f)); painter.Stroke();
                    painter.BeginPath(); painter.Arc(P(12f, 4.2f), 1.4f * scale, 0f, 360f); painter.Fill();
                    RoundRect(painter, P, 5f, 8.5f, 14f, 11f, 3f); painter.Stroke();
                    painter.BeginPath(); painter.MoveTo(P(2.8f, 12.5f)); painter.LineTo(P(2.8f, 15.5f)); painter.Stroke();
                    painter.BeginPath(); painter.MoveTo(P(21.2f, 12.5f)); painter.LineTo(P(21.2f, 15.5f)); painter.Stroke();
                    painter.BeginPath(); painter.Arc(P(9.4f, 13.4f), 1.5f * scale, 0f, 360f); painter.Fill();
                    painter.BeginPath(); painter.Arc(P(14.6f, 13.4f), 1.5f * scale, 0f, 360f); painter.Fill();
                    if (glyph == IconGlyph.RobotOff) Slash(painter, P);
                    break;
                case IconGlyph.Lock:
                case IconGlyph.Unlock:
                    painter.lineWidth = 1.9f * scale;
                    RoundRect(painter, P, 5f, 10.5f, 14f, 10f, 2.2f); painter.Fill();
                    // The shackle closes on the body when locked and swings up and open to the right when not.
                    painter.BeginPath();
                    if (glyph == IconGlyph.Lock) { painter.MoveTo(P(8.2f, 10.5f)); painter.LineTo(P(8.2f, 7.5f)); painter.Arc(P(12f, 7.5f), 3.8f * scale, 180f, 360f); painter.LineTo(P(15.8f, 10.5f)); }
                    else { painter.MoveTo(P(8.2f, 10.5f)); painter.LineTo(P(8.2f, 6f)); painter.Arc(P(12f, 6f), 3.8f * scale, 180f, 340f); }
                    painter.Stroke();
                    break;
                case IconGlyph.Branch:
                    // A commit line with a branch leaving it: the git graph in miniature.
                    painter.lineWidth = 1.8f * scale;
                    painter.BeginPath(); painter.MoveTo(P(7f, 3.5f)); painter.LineTo(P(7f, 20.5f)); painter.Stroke();
                    painter.BeginPath(); painter.MoveTo(P(7f, 17f)); painter.BezierCurveTo(P(7f, 12f), P(17f, 13f), P(17f, 8.8f)); painter.Stroke();
                    painter.BeginPath(); painter.Arc(P(17f, 6.2f), 2.6f * scale, 0f, 360f); painter.Stroke();
                    break;
                case IconGlyph.Close:
                    painter.lineWidth = 2.2f * scale;
                    painter.BeginPath(); painter.MoveTo(P(6.5f, 6.5f)); painter.LineTo(P(17.5f, 17.5f)); painter.Stroke();
                    painter.BeginPath(); painter.MoveTo(P(17.5f, 6.5f)); painter.LineTo(P(6.5f, 17.5f)); painter.Stroke();
                    break;
                case IconGlyph.Chevron:
                    // Points right: rotate the element (USS rotate) to point down when open.
                    painter.lineWidth = 2.4f * scale;
                    painter.BeginPath(); painter.MoveTo(P(9.5f, 5.5f)); painter.LineTo(P(16f, 12f)); painter.LineTo(P(9.5f, 18.5f)); painter.Stroke();
                    break;
                case IconGlyph.Gauge:
                    // A speedometer with its needle high: optimization.
                    painter.lineWidth = 1.9f * scale;
                    painter.BeginPath(); painter.Arc(P(12f, 15f), 8.5f * scale, 165f, 375f); painter.Stroke();
                    painter.lineWidth = 2.2f * scale;
                    painter.BeginPath(); painter.MoveTo(P(12f, 15f)); painter.LineTo(P(16.6f, 9.4f)); painter.Stroke();
                    painter.BeginPath(); painter.Arc(P(12f, 15f), 1.9f * scale, 0f, 360f); painter.Fill();
                    break;
                case IconGlyph.Sliders:
                    // Three sliders at different values: a body shaped by its settings.
                    painter.lineWidth = 1.8f * scale;
                    foreach (var (y, knob) in new[] { (6f, 15f), (12f, 8f), (18f, 13f) })
                    {
                        painter.BeginPath(); painter.MoveTo(P(3.5f, y)); painter.LineTo(P(20.5f, y)); painter.Stroke();
                        painter.BeginPath(); painter.Arc(P(knob, y), 2.5f * scale, 0f, 360f); painter.Fill();
                    }
                    break;
                case IconGlyph.Sound:
                    // Bars of an audio spectrum.
                    painter.lineWidth = 2.4f * scale;
                    foreach (var (x, half) in new[] { (4f, 2.5f), (8f, 6f), (12f, 8.5f), (16f, 5f), (20f, 3f) })
                    {
                        painter.BeginPath(); painter.MoveTo(P(x, 12f - half)); painter.LineTo(P(x, 12f + half)); painter.Stroke();
                    }
                    break;
            }
        }

        // A head and shoulders centred on x, scaled around the bottom of the box and optionally raised.
        private static void Person(Painter2D painter, Func<float, float, Vector2> P, float x, float size, float raise = 0f)
        {
            float Y(float y) => 21f - (21f - y) * size + raise;
            float X(float dx) => x + dx * size;
            painter.BeginPath(); painter.Arc(P(x, Y(7.6f)), 3.4f * size * (P(1f, 0f) - P(0f, 0f)).x, 0f, 360f); painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(P(X(-7f), Y(20.5f)));
            painter.BezierCurveTo(P(X(-7f), Y(15f)), P(X(-3.5f), Y(13.4f)), P(x, Y(13.4f)));
            painter.BezierCurveTo(P(X(3.5f), Y(13.4f)), P(X(7f), Y(15f)), P(X(7f), Y(20.5f)));
            painter.Stroke();
        }

        private static void Slash(Painter2D painter, Func<float, float, Vector2> P)
        {
            painter.BeginPath(); painter.MoveTo(P(3f, 3f)); painter.LineTo(P(21f, 21f)); painter.Stroke();
        }

        private static void RoundRect(Painter2D painter, Func<float, float, Vector2> P, float x, float y, float width, float height, float radius)
        {
            float r = radius * (P(1f, 0f) - P(0f, 0f)).x;
            painter.BeginPath();
            painter.MoveTo(P(x + radius, y));
            painter.LineTo(P(x + width - radius, y)); painter.Arc(P(x + width - radius, y + radius), r, 270f, 360f);
            painter.LineTo(P(x + width, y + height - radius)); painter.Arc(P(x + width - radius, y + height - radius), r, 0f, 90f);
            painter.LineTo(P(x + radius, y + height)); painter.Arc(P(x + radius, y + height - radius), r, 90f, 180f);
            painter.LineTo(P(x, y + radius)); painter.Arc(P(x + radius, y + radius), r, 180f, 270f);
            painter.ClosePath();
        }
    }
}
