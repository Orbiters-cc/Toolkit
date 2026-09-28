using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A row of mutually exclusive choices. A highlight slides to the chosen tab, which lights up on pointer down before
    /// the change is applied. With no choice (<see cref="SetIndex"/> -1, e.g. a mixed value) the highlight fades out
    /// where it was.
    /// </summary>
    public sealed class SegmentedControl : VisualElement
    {
        public readonly struct Option
        {
            public readonly string Label;
            public readonly IconGlyph? Icon;
            public readonly string Tooltip;

            public Option(string label, IconGlyph? icon = null, string tooltip = null)
            {
                Label = label; Icon = icon; Tooltip = tooltip;
            }
        }

        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/segmented-control.uss";
        private readonly List<VisualElement> tabs = new List<VisualElement>();
        private readonly VisualElement indicator;
        private readonly Action<int> changed;
        private bool placed;

        public int Index { get; private set; } = -1;

        public SegmentedControl(IEnumerable<string> labels, Action<int> changed) : this(labels.Select(l => new Option(l)), changed) { }

        public SegmentedControl(IEnumerable<Option> options, Action<int> changed)
        {
            this.changed = changed;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-segmented");
            indicator = new VisualElement { pickingMode = PickingMode.Ignore };
            indicator.AddToClassList("orb-segmented__indicator");
            indicator.AddToClassList("orb-segmented__indicator--hidden");
            Add(indicator);
            foreach (var option in options)
            {
                int index = tabs.Count;
                var tab = new VisualElement { focusable = true, tooltip = option.Tooltip };
                tab.AddToClassList("orb-segmented__tab");
                if (option.Icon.HasValue)
                {
                    var icon = new VectorIcon(option.Icon.Value);
                    icon.AddToClassList("orb-segmented__icon");
                    tab.Add(icon);
                }
                if (!string.IsNullOrEmpty(option.Label))
                {
                    var label = new Label(option.Label) { pickingMode = PickingMode.Ignore };
                    label.AddToClassList("orb-segmented__label");
                    tab.Add(label);
                }
                tab.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    tab.AddToClassList("orb-segmented__tab--pressed");
                    Choose(index);
                    evt.StopPropagation();
                });
                tab.RegisterCallback<PointerUpEvent>(_ => tab.RemoveFromClassList("orb-segmented__tab--pressed"));
                tab.RegisterCallback<PointerLeaveEvent>(_ => tab.RemoveFromClassList("orb-segmented__tab--pressed"));
                tab.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space) Choose(index);
                    else if (evt.keyCode == KeyCode.LeftArrow) Step(-1);
                    else if (evt.keyCode == KeyCode.RightArrow) Step(1);
                });
                tabs.Add(tab);
                Add(tab);
            }
            // The highlight follows the chosen tab; USS transitions animate its position and width.
            RegisterCallback<GeometryChangedEvent>(_ => MoveIndicator());
        }

        public void SetIndex(int index)
        {
            Index = index >= 0 && index < tabs.Count ? index : -1;
            for (int i = 0; i < tabs.Count; i++) tabs[i].EnableInClassList("orb-segmented__tab--selected", i == Index);
            MoveIndicator();
        }

        public void SetOptionEnabled(int index, bool enabled) => tabs[index].SetEnabled(enabled);

        public void SetOptionTooltip(int index, string tooltip) => tabs[index].tooltip = tooltip;

        private void MoveIndicator()
        {
            indicator.EnableInClassList("orb-segmented__indicator--hidden", Index < 0);
            if (Index < 0) return;
            var target = tabs[Index].layout;
            if (float.IsNaN(target.width) || target.width <= 0f) return;
            // The first placement snaps: a freshly built control must not slide in from the left edge as if it changed.
            if (!placed)
            {
                placed = true;
                indicator.AddToClassList("orb-segmented__indicator--instant");
                indicator.schedule.Execute(() => indicator.RemoveFromClassList("orb-segmented__indicator--instant")).StartingIn(50);
            }
            indicator.style.left = target.x;
            indicator.style.width = target.width;
        }

        private void Step(int direction)
        {
            for (int i = Index + direction; i >= 0 && i < tabs.Count; i += direction)
                if (tabs[i].enabledInHierarchy) { Choose(i); tabs[i].Focus(); return; }
        }

        private void Choose(int index)
        {
            if (!enabledInHierarchy || !tabs[index].enabledSelf || index == Index) return;
            SetIndex(index);
            changed?.Invoke(index);
        }
    }
}
