using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A field that opens a searchable list for large choices (servers, roles, bones, slots). Values are stable keys;
    /// the field shows each key's label.
    /// </summary>
    public sealed class SearchableDropdownField : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/searchable-dropdown.uss";
        private readonly Label valueLabel;
        private readonly string title;
        private readonly string placeholder;
        private readonly Action<string> changed;
        private readonly Func<string, string> shortLabel;
        private List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
        private readonly AdvancedDropdownState state = new AdvancedDropdownState();

        public string Value { get; private set; }

        /// <param name="shortLabel">Optional compact text for the chosen key when list labels carry details (e.g. paths).</param>
        public SearchableDropdownField(string label, string title, IEnumerable<KeyValuePair<string, string>> items, string value,
            Action<string> changed, string placeholder = "Choose…", Func<string, string> shortLabel = null)
        {
            this.title = title;
            this.shortLabel = shortLabel;
            this.placeholder = placeholder;
            this.changed = changed;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-search-dropdown");
            if (!string.IsNullOrEmpty(label))
            {
                var caption = new Label(label);
                caption.AddToClassList("orb-search-dropdown__label");
                Add(caption);
            }
            var button = new VisualElement { focusable = true };
            button.AddToClassList("orb-search-dropdown__button");
            valueLabel = new Label { pickingMode = PickingMode.Ignore };
            valueLabel.AddToClassList("orb-search-dropdown__value");
            button.Add(valueLabel);
            var caret = new VectorIcon(IconGlyph.Chevron);
            caret.AddToClassList("orb-search-dropdown__caret");
            button.Add(caret);
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                Open(button);
                evt.StopPropagation();
            });
            button.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space || evt.keyCode == KeyCode.DownArrow) Open(button);
            });
            Add(button);
            SetItems(items, value);
        }

        public void SetItems(IEnumerable<KeyValuePair<string, string>> values, string value)
        {
            items = (values ?? Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
            SetValueWithoutNotify(value);
        }

        public void SetValueWithoutNotify(string value)
        {
            Value = items.Any(i => i.Key == value) ? value : null;
            string full = Value != null ? items.First(i => i.Key == Value).Value : null;
            valueLabel.text = full == null ? (items.Count == 0 ? "Nothing to choose" : placeholder) : shortLabel != null ? shortLabel(Value) : full;
            valueLabel.EnableInClassList("orb-search-dropdown__value--empty", Value == null);
            tooltip = full;
        }

        private void Open(VisualElement anchor)
        {
            if (items.Count == 0) return;
            var dropdown = new Popup(state, title, items, key =>
            {
                SetValueWithoutNotify(key);
                changed?.Invoke(key);
            });
            dropdown.Show(anchor.worldBound);
        }

        private sealed class Popup : AdvancedDropdown
        {
            private readonly string title;
            private readonly List<KeyValuePair<string, string>> items;
            private readonly Action<string> chosen;

            public Popup(AdvancedDropdownState state, string title, List<KeyValuePair<string, string>> items, Action<string> chosen) : base(state)
            {
                this.title = title;
                this.items = items;
                this.chosen = chosen;
                minimumSize = new Vector2(240, 320);
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem(title);
                for (int i = 0; i < items.Count; i++) root.AddChild(new AdvancedDropdownItem(items[i].Value) { id = i });
                return root;
            }

            protected override void ItemSelected(AdvancedDropdownItem item)
            {
                if (item.id >= 0 && item.id < items.Count) chosen(items[item.id].Key);
            }
        }
    }
}
