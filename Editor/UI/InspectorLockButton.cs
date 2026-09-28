using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A padlock for the corner of an Orbiters inspector. It locks the Inspector that shows it, exactly like the padlock
    /// on the Inspector tab, so selecting files in the Project window no longer replaces the tool and they can be dragged
    /// onto it. It follows the tab's padlock both ways.
    /// </summary>
    public sealed class InspectorLockButton : VisualElement
    {
        // Unity keeps the lock on the Inspector window without a public API.
        private static readonly PropertyInfo LockedProperty = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow")
            ?.GetProperty("isLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly IconButton button;
        private readonly string lockedTooltip, unlockedTooltip;
        private EditorWindow host;

        public InspectorLockButton(string unlockedTooltip = null, string lockedTooltip = null)
        {
            this.unlockedTooltip = unlockedTooltip ?? "Lock this Inspector here: selecting files in the Project window won’t replace it, so you can drag them onto it.";
            this.lockedTooltip = lockedTooltip ?? "Locked on this object. Click to follow the selection again.";
            button = new IconButton(IconGlyph.Unlock, "Lock", this.unlockedTooltip, Toggle);
            button.AddToClassList("orb-icon-button--small");
            Add(button);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Editor/UI/icon-button.uss");
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("orb-inspector-lock");
            style.display = DisplayStyle.None;
            RegisterCallback<AttachToPanelEvent>(_ => { host = FindHost(); Show(); });
            // The tab's own padlock can change it too.
            schedule.Execute(Show).Every(400);
        }

        private EditorWindow FindHost()
        {
            if (LockedProperty == null || panel == null) return null;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (LockedProperty.DeclaringType.IsInstanceOfType(window) && window.rootVisualElement.panel == panel) return window;
            return null;
        }

        private bool Locked => host && (bool)LockedProperty.GetValue(host);

        private void Toggle()
        {
            if (!host) host = FindHost();
            if (!host) return;
            // Flip the icon first: the Inspector repaints its header a frame later.
            bool locked = !Locked;
            Show(locked);
            LockedProperty.SetValue(host, locked);
            host.Repaint();
        }

        private void Show() => Show(Locked);

        private void Show(bool locked)
        {
            // Only an Inspector can be locked; other hosts (a preview, a custom window) show nothing.
            style.display = host ? DisplayStyle.Flex : DisplayStyle.None;
            if (button.On == locked) return;
            button.SetOn(locked);
            button.Icon.Glyph = locked ? IconGlyph.Lock : IconGlyph.Unlock;
            button.tooltip = locked ? lockedTooltip : unlockedTooltip;
        }
    }
}
