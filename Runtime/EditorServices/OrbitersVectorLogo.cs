#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Small vector logo renderer for SVG paths (M, L, H, V, C, S, Q, T, A and Z, absolute or relative), in their own fill colours,
/// white for paths without one.
/// </summary>
public sealed class OrbitersVectorLogo : VisualElement
{
    private sealed class PathData { public readonly List<Command> commands = new List<Command>(); public FillRule rule; public Color? color; }
    private sealed class Command { public char kind; public Vector2[] points; }
    private readonly List<PathData> paths = new List<PathData>();
    private readonly Vector2 origin, size;
    private Color fill = Color.white;

    /// <summary>The logo of <paramref name="svg"/>, framed by its viewBox.</summary>
    public OrbitersVectorLogo(string svg) : this(svg, null) { }

    /// <summary>The logo of <paramref name="svg"/> in a box of <paramref name="viewBoxSize"/> from the origin.</summary>
    public OrbitersVectorLogo(string svg, Vector2 viewBoxSize) : this(svg, (Vector2?)viewBoxSize) { }

    private OrbitersVectorLogo(string svg, Vector2? viewBoxSize)
    {
        pickingMode = PickingMode.Ignore;
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(svg);
        var box = doc.DocumentElement?.Attributes?["viewBox"]?.Value;
        var numbers = box != null ? Regex.Matches(box, Number) : null;
        if (viewBoxSize.HasValue) size = viewBoxSize.Value;
        else if (numbers != null && numbers.Count == 4)
        {
            origin = new Vector2(Parse(numbers[0].Value), Parse(numbers[1].Value));
            size = new Vector2(Parse(numbers[2].Value), Parse(numbers[3].Value));
        }
        else throw new FormatException("The SVG logo has no viewBox.");
        foreach (XmlNode path in doc.GetElementsByTagName("path"))
        {
            var data = Read(path.Attributes?["d"]?.Value ?? "", path.Attributes?["fill-rule"]?.Value == "evenodd" ? FillRule.OddEven : FillRule.NonZero);
            string color = path.Attributes?["fill"]?.Value;
            if (color == "none") continue;
            // Unity knows "grey" but not CSS's "gray" spellings.
            if (color != null && ColorUtility.TryParseHtmlString(color.Replace("gray", "grey"), out var parsed)) data.color = parsed;
            paths.Add(data);
        }
        generateVisualContent += Draw;
    }

    /// <summary>The fill colour of paths without their own (white by default).</summary>
    public Color Fill { get => fill; set { fill = value; MarkDirtyRepaint(); } }

    private const string Number = @"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?";

    private static float Parse(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    // Every command made absolute: M, L, C, Q and Z only (arcs become short lines).
    private static PathData Read(string d, FillRule rule)
    {
        var data = new PathData { rule = rule };
        var tokens = Regex.Matches(d, "[MmLlHhVvCcSsQqTtAaZz]|" + Number);
        int i = 0; char kind = 'M';
        Vector2 current = Vector2.zero, start = Vector2.zero, control = Vector2.zero;
        bool curve = false, quad = false;
        float Next() => Parse(tokens[i++].Value);
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i].Value[0])) kind = tokens[i++].Value[0];
            bool relative = char.IsLower(kind);
            Vector2 Point() { var p = new Vector2(Next(), Next()); return relative ? current + p : p; }
            switch (char.ToUpperInvariant(kind))
            {
                case 'M':
                    current = start = Point();
                    data.commands.Add(new Command { kind = 'M', points = new[] { current } });
                    // Pairs after a move are lines.
                    kind = relative ? 'l' : 'L';
                    curve = false;
                    break;
                case 'L': current = Point(); Line(data, current); curve = false; break;
                case 'H': { float x = Next(); current = new Vector2(relative ? current.x + x : x, current.y); Line(data, current); curve = false; break; }
                case 'V': { float y = Next(); current = new Vector2(current.x, relative ? current.y + y : y); Line(data, current); curve = false; break; }
                case 'C':
                case 'S':
                {
                    // S mirrors the last curve's second control point.
                    var first = char.ToUpperInvariant(kind) == 'S' ? (curve ? 2 * current - control : current) : Point();
                    var second = Point();
                    var end = Point();
                    data.commands.Add(new Command { kind = 'C', points = new[] { first, second, end } });
                    control = second; current = end; curve = true; quad = false;
                    break;
                }
                case 'Q':
                case 'T':
                {
                    // T mirrors the last quadratic's control point.
                    var middle = char.ToUpperInvariant(kind) == 'T' ? (quad ? 2 * current - control : current) : Point();
                    var end = Point();
                    data.commands.Add(new Command { kind = 'Q', points = new[] { middle, end } });
                    control = middle; current = end; quad = true; curve = false;
                    continue;
                }
                case 'A':
                {
                    float rx = Mathf.Abs(Next()), ry = Mathf.Abs(Next()), rotation = Next();
                    bool large = Next() != 0f, sweep = Next() != 0f;
                    var end = Point();
                    Arc(data, current, end, rx, ry, rotation, large, sweep);
                    current = end; curve = quad = false;
                    continue;
                }
                case 'Z':
                    data.commands.Add(new Command { kind = 'Z', points = Array.Empty<Vector2>() });
                    current = start; curve = false;
                    if (i < tokens.Count && !char.IsLetter(tokens[i].Value[0])) throw new FormatException("Invalid SVG logo path.");
                    break;
                default: throw new FormatException("Unsupported SVG path command " + kind + ".");
            }
            // Only a quadratic (which continues the loop) leaves a control point for T.
            quad = false;
        }
        return data;
    }

    private static void Line(PathData data, Vector2 to) => data.commands.Add(new Command { kind = 'L', points = new[] { to } });

    // An elliptical arc (SVG's endpoint form) as short lines: its centre and angles first (SVG 1.1, F.6.5).
    private static void Arc(PathData data, Vector2 from, Vector2 to, float rx, float ry, float rotation, bool large, bool sweep)
    {
        if (rx < 1e-6f || ry < 1e-6f || from == to) { Line(data, to); return; }
        float phi = rotation * Mathf.Deg2Rad, cos = Mathf.Cos(phi), sin = Mathf.Sin(phi);
        var half = (from - to) / 2f;
        var p = new Vector2(cos * half.x + sin * half.y, -sin * half.x + cos * half.y);
        float scale = p.x * p.x / (rx * rx) + p.y * p.y / (ry * ry);
        if (scale > 1f) { rx *= Mathf.Sqrt(scale); ry *= Mathf.Sqrt(scale); }
        float numerator = rx * rx * ry * ry - rx * rx * p.y * p.y - ry * ry * p.x * p.x;
        float denominator = rx * rx * p.y * p.y + ry * ry * p.x * p.x;
        float factor = Mathf.Sqrt(Mathf.Max(0f, numerator / denominator)) * (large == sweep ? -1f : 1f);
        var c = new Vector2(factor * rx * p.y / ry, -factor * ry * p.x / rx);
        var center = new Vector2(cos * c.x - sin * c.y, sin * c.x + cos * c.y) + (from + to) / 2f;
        float Angle(Vector2 u, Vector2 v) => Mathf.Atan2(u.x * v.y - u.y * v.x, Vector2.Dot(u, v));
        var u0 = new Vector2((p.x - c.x) / rx, (p.y - c.y) / ry);
        var u1 = new Vector2((-p.x - c.x) / rx, (-p.y - c.y) / ry);
        float start = Angle(Vector2.right, u0), delta = Angle(u0, u1);
        if (!sweep && delta > 0f) delta -= 2f * Mathf.PI;
        else if (sweep && delta < 0f) delta += 2f * Mathf.PI;
        int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(delta) / (Mathf.PI / 16f)), 1, 64);
        for (int i = 1; i <= steps; i++)
        {
            float a = start + delta * i / steps;
            var q = new Vector2(rx * Mathf.Cos(a), ry * Mathf.Sin(a));
            Line(data, i == steps ? to : center + new Vector2(cos * q.x - sin * q.y, sin * q.x + cos * q.y));
        }
    }

    private void Draw(MeshGenerationContext context)
    {
        float scale = Mathf.Min(contentRect.width / size.x, contentRect.height / size.y);
        if (scale <= 0f) return;
        var offset = contentRect.center - size * scale / 2 - origin * scale;
        var painter = context.painter2D;
        foreach (var path in paths)
        {
            painter.fillColor = path.color ?? fill;
            painter.BeginPath();
            foreach (var command in path.commands)
            {
                var p = command.points;
                switch (command.kind)
                {
                    case 'M': painter.MoveTo(offset + p[0] * scale); break;
                    case 'L': painter.LineTo(offset + p[0] * scale); break;
                    case 'C': painter.BezierCurveTo(offset + p[0] * scale, offset + p[1] * scale, offset + p[2] * scale); break;
                    case 'Q': painter.QuadraticCurveTo(offset + p[0] * scale, offset + p[1] * scale); break;
                    case 'Z': painter.ClosePath(); break;
                }
            }
            painter.Fill(path.rule);
        }
    }
}
#endif
