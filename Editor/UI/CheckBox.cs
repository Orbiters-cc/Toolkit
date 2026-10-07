using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>A check box for lists (which items to include) that ticks on press, before the change it triggers is applied.</summary>
    public sealed class CheckBox : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/check-box.uss";
        private readonly Action<bool> changed;

        public bool Value { get; private set; }

        public CheckBox(bool value, Action<bool> changed)
        {
            this.changed = changed;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-check");
            focusable = true;
            var mark = new VectorIcon(IconGlyph.Check);
            mark.AddToClassList("orb-check__mark");
            Add(mark);
            SetValueWithoutNotify(value);
            RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { Flip(); evt.StopPropagation(); } });
            RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Space || evt.keyCode == KeyCode.Return) Flip(); });
        }

        public void SetValueWithoutNotify(bool value)
        {
            Value = value;
            EnableInClassList("orb-check--on", value);
        }

        private void Flip()
        {
            if (!enabledInHierarchy) return;
            SetValueWithoutNotify(!Value);
            changed?.Invoke(Value);
        }
    }
}
