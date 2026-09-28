using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>A stacked bar with a legend: how a fixed budget (such as VRChat's 256 synced parameter bits) is shared.</summary>
    public sealed class BudgetBar : VisualElement
    {
        public readonly struct Segment
        {
            public readonly string Label;
            public readonly float Value;
            public readonly Color Color;

            public Segment(string label, float value, Color color)
            {
                Label = label; Value = value; Color = color;
            }
        }

        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/budget-bar.uss";
        private readonly VisualElement bar, legend;

        public BudgetBar()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-budget");
            bar = new VisualElement(); bar.AddToClassList("orb-budget__bar"); Add(bar);
            legend = new VisualElement(); legend.AddToClassList("orb-budget__legend"); Add(legend);
        }

        /// <summary>Segments share the bar by value; a segment of zero keeps its legend row but takes no width.</summary>
        public void SetSegments(IEnumerable<Segment> segments)
        {
            bar.Clear(); legend.Clear();
            foreach (var segment in segments)
            {
                var part = new VisualElement();
                part.AddToClassList("orb-budget__segment");
                part.style.backgroundColor = segment.Color;
                part.style.flexGrow = Mathf.Max(0f, segment.Value);
                part.style.display = segment.Value > 0f ? DisplayStyle.Flex : DisplayStyle.None;
                part.tooltip = segment.Label;
                bar.Add(part);

                var row = new VisualElement(); row.AddToClassList("orb-budget__row");
                var swatch = new VisualElement(); swatch.AddToClassList("orb-budget__swatch");
                swatch.style.backgroundColor = segment.Color;
                row.Add(swatch);
                var label = new Label(segment.Label); label.AddToClassList("orb-budget__label");
                row.Add(label);
                legend.Add(row);
            }
        }
    }
}
