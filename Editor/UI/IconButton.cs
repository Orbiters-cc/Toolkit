using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A tile with an icon and a short label under it, acting on press. Toggle tiles light up with <see cref="SetOn"/>;
    /// the tooltip explains what the tile does.
    /// </summary>
    public sealed class IconButton : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/icon-button.uss";
        private readonly Action pressed;

        public VectorIcon Icon { get; }
        public Label Label { get; }
        public bool On { get; private set; }

        public IconButton(IconGlyph glyph, string label, string tooltip, Action pressed)
        {
            this.pressed = pressed;
            this.tooltip = tooltip;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-icon-button");
            focusable = true;
            Icon = new VectorIcon(glyph); Icon.AddToClassList("orb-icon-button__icon"); Add(Icon);
            Label = new Label(label); Label.AddToClassList("orb-icon-button__label"); Label.pickingMode = PickingMode.Ignore; Add(Label);
            RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !enabledInHierarchy) return;
                AddToClassList("orb-icon-button--pressed");
                pressed?.Invoke();
                evt.StopPropagation();
            });
            RegisterCallback<PointerUpEvent>(_ => RemoveFromClassList("orb-icon-button--pressed"));
            RegisterCallback<PointerLeaveEvent>(_ => RemoveFromClassList("orb-icon-button--pressed"));
            RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Space) return;
                pressed?.Invoke();
                evt.StopPropagation();
            });
        }

        public void SetOn(bool on)
        {
            On = on;
            EnableInClassList("orb-icon-button--on", on);
        }
    }
}
