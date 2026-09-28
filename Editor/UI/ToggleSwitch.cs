using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>An on/off switch that flips on press, before the change it triggers is applied.</summary>
    public sealed class ToggleSwitch : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/toggle-switch.uss";
        private readonly Action<bool> changed;

        public bool Value { get; private set; }

        public ToggleSwitch(bool value, Action<bool> changed)
        {
            this.changed = changed;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-switch");
            focusable = true;
            var knob = new VisualElement(); knob.AddToClassList("orb-switch__knob"); knob.pickingMode = PickingMode.Ignore; Add(knob);
            SetValueWithoutNotify(value);
            RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { Flip(); evt.StopPropagation(); } });
            RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Space || evt.keyCode == KeyCode.Return) Flip(); });
        }

        public void SetValueWithoutNotify(bool value)
        {
            Value = value;
            EnableInClassList("orb-switch--on", value);
        }

        private void Flip()
        {
            if (!enabledInHierarchy) return;
            SetValueWithoutNotify(!Value);
            changed?.Invoke(Value);
        }
    }
}
