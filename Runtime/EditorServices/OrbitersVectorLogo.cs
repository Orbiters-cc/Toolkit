#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>Small vector logo renderer for absolute SVG M/L/C/Z paths.</summary>
public sealed class OrbitersVectorLogo : VisualElement
{
    private sealed class PathData { public readonly List<Command> commands = new List<Command>(); public FillRule rule; }
    private sealed class Command { public char kind; public Vector2[] points; }
    private readonly List<PathData> paths = new List<PathData>();
    private readonly Vector2 size;
    public OrbitersVectorLogo(string svg, Vector2 viewBoxSize)
    {
        size = viewBoxSize; pickingMode = PickingMode.Ignore;
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(svg);
        foreach (XmlNode path in doc.GetElementsByTagName("path"))
        {
            var data = new PathData { rule = path.Attributes?["fill-rule"]?.Value == "evenodd" ? FillRule.OddEven : FillRule.NonZero };
            var tokens = Regex.Matches(path.Attributes?["d"]?.Value ?? "", @"[MLCZ]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?");
            int i = 0; char kind = 'M';
            while (i < tokens.Count)
            {
                if (char.IsLetter(tokens[i].Value[0])) kind = tokens[i++].Value[0];
                int count = kind == 'Z' ? 0 : kind == 'C' ? 3 : 1;
                var points = new Vector2[count];
                for (int p = 0; p < count; p++) points[p] = new Vector2(float.Parse(tokens[i++].Value, CultureInfo.InvariantCulture), float.Parse(tokens[i++].Value, CultureInfo.InvariantCulture));
                data.commands.Add(new Command { kind = kind, points = points });
                if (kind == 'M') kind = 'L';
                if (kind == 'Z' && i < tokens.Count && !char.IsLetter(tokens[i].Value[0])) throw new FormatException("Invalid SVG logo path.");
            }
            paths.Add(data);
        }
        generateVisualContent += Draw;
    }
    private void Draw(MeshGenerationContext context)
    {
        float scale = Mathf.Min(contentRect.width / size.x, contentRect.height / size.y);
        var offset = contentRect.center - size * scale / 2;
        var painter = context.painter2D; painter.fillColor = Color.white;
        foreach (var path in paths)
        {
            painter.BeginPath();
            foreach (var command in path.commands)
            {
                var p = command.points;
                switch (command.kind)
                {
                    case 'M': painter.MoveTo(offset + p[0] * scale); break;
                    case 'L': painter.LineTo(offset + p[0] * scale); break;
                    case 'C': painter.BezierCurveTo(offset + p[0] * scale, offset + p[1] * scale, offset + p[2] * scale); break;
                    case 'Z': painter.ClosePath(); break;
                }
            }
            painter.Fill(path.rule);
        }
    }
}
#endif
