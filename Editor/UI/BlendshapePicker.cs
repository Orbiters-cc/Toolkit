using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Searchable batch selection shared by ReFit and MCB. Filtering never changes the selection; names that are not
    /// available (stale or duplicate) are dropped from it. An optional label maps stable keys to display names.
    /// </summary>
    public class BlendshapePicker : VisualElement
    {
        private readonly List<string> available;
        private readonly List<string> selection;
        private readonly Action changed;
        private readonly Func<string, string> labelOf;
        private readonly ToolbarSearchField search;
        private readonly VisualElement results;
        private readonly VisualElement chips;
        private readonly Label count;
        private readonly Button selectVisible;
        private readonly Button clear;
        private readonly Dictionary<string, Button> rows = new Dictionary<string, Button>(StringComparer.Ordinal);
        private List<string> matches;

        public BlendshapePicker(IEnumerable<string> names, List<string> selection, Action changed, List<string> recent = null,
            Func<string, string> labelOf = null, string searchTooltip = "Search all blendshapes; selections stay selected across searches")
        {
            this.selection = selection;
            this.changed = changed;
            this.labelOf = labelOf ?? (name => name);
            recent = recent ?? new List<string>();
            var sheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Editor/UI/blendshape-picker.uss");
            if (sheet != null) styleSheets.Add(sheet);
            AddToClassList("orbiters-shape-picker");
            available = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal)
                .OrderBy(n => recent.Contains(n) ? recent.IndexOf(n) : int.MaxValue)
                .ThenBy(n => this.labelOf(n), StringComparer.OrdinalIgnoreCase).ToList();
            var valid = new HashSet<string>(available, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            selection.RemoveAll(n => !valid.Contains(n) || !seen.Add(n));

            search = new ToolbarSearchField { name = "orbiters-blendshape-search", tooltip = searchTooltip };
            search.AddToClassList("orbiters-shape-search");
            Add(search);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("orbiters-shape-toolbar");
            count = new Label();
            count.AddToClassList("orbiters-shape-count");
            toolbar.Add(count);
            selectVisible = ImmediateButton("Select all shown", () =>
            {
                foreach (var name in matches)
                    if (!selection.Contains(name)) selection.Add(name);
                UpdateSelection(true);
            });
            selectVisible.name = "orbiters-shape-select-visible";
            selectVisible.AddToClassList("orbiters-shape-action");
            toolbar.Add(selectVisible);
            clear = ImmediateButton("Clear", () => { selection.Clear(); UpdateSelection(true); });
            clear.name = "orbiters-shape-clear";
            clear.AddToClassList("orbiters-shape-action");
            toolbar.Add(clear);
            Add(toolbar);

            results = new VisualElement { name = "orbiters-shape-results" };
            results.AddToClassList("orbiters-shape-results");
            var resultScroll = new ScrollView(ScrollViewMode.Vertical) { name = "orbiters-shape-scroll" };
            resultScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            resultScroll.AddToClassList("orbiters-shape-scroll");
            resultScroll.Add(results);
            Add(resultScroll);
            chips = new VisualElement { name = "orbiters-shape-selections" };
            chips.AddToClassList("orbiters-shape-suggestions");
            var selectionScroll = new ScrollView(ScrollViewMode.Vertical) { name = "orbiters-selection-scroll" };
            selectionScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            selectionScroll.AddToClassList("orbiters-selection-scroll");
            selectionScroll.Add(chips);
            Add(selectionScroll);
            search.RegisterValueChangedCallback(_ => RefreshMatches());
            RefreshMatches();
        }

        private void RefreshMatches()
        {
            string term = (search.value ?? string.Empty).Trim();
            matches = available.Where(n => labelOf(n).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            results.Clear();
            rows.Clear();
            foreach (string name in matches)
            {
                var row = ImmediateButton(labelOf(name), () =>
                {
                    if (!selection.Remove(name)) selection.Add(name);
                    UpdateSelection(true);
                });
                row.userData = name;
                row.tooltip = labelOf(name);
                row.AddToClassList("orbiters-shape-suggestion");
                row.AddToClassList("orbiters-shape-row");
                rows.Add(name, row);
                results.Add(row);
            }
            if (matches.Count == 0)
            {
                var empty = new Label("No matches. Your selection is kept below.");
                empty.AddToClassList("orbiters-help");
                results.Add(empty);
            }
            UpdateSelection(false);
        }

        /// <summary>Re-reads the selection list after a caller changed it, and publishes it.</summary>
        protected void RefreshSelection() => UpdateSelection(true);

        private void UpdateSelection(bool notify)
        {
            count.text = $"{selection.Count} selected · {matches.Count} shown";
            foreach (var pair in rows)
            {
                bool selected = selection.Contains(pair.Key);
                pair.Value.text = (selected ? "✓  " : "+  ") + labelOf(pair.Key);
                pair.Value.EnableInClassList("orbiters-shape-row--selected", selected);
                pair.Value.EnableInClassList("orbiters-shape-suggestion--selected", selected);
            }
            clear.SetEnabled(selection.Count > 0);
            selectVisible.SetEnabled(matches.Any(n => !selection.Contains(n)));
            chips.Clear();
            foreach (string name in selection)
            {
                var chip = ImmediateButton(labelOf(name) + "  ×", () => { selection.Remove(name); UpdateSelection(true); });
                chip.userData = name;
                chip.tooltip = "Remove " + labelOf(name);
                chip.AddToClassList("orbiters-shape-suggestion");
                chip.AddToClassList("orbiters-shape-suggestion--selected");
                chips.Add(chip);
            }
            if (notify) changed?.Invoke();
        }

        private static Button ImmediateButton(string text, Action action)
        {
            var button = new Button { text = text };
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }

        /// <summary>Adds every available name containing <paramref name="text"/> (case-insensitive).</summary>
        public void SelectMatching(string text)
        {
            foreach (var name in available.Where(n => labelOf(n).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0))
                if (!selection.Contains(name)) selection.Add(name);
            UpdateSelection(true);
        }
    }
}
