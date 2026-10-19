using System;
using System.Collections.Generic;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>Registry used by other editor tools to expose their scene gizmos in the XRay toolbar.</summary>
    public static class XRayExternalGizmoRegistry
    {
        private static readonly Dictionary<string, XRayExternalGizmoEntry> EntriesById =
            new Dictionary<string, XRayExternalGizmoEntry>();

        public static event Action Changed;

        public static IReadOnlyList<XRayExternalGizmoEntry> Entries
        {
            get
            {
                var entries = new List<XRayExternalGizmoEntry>(EntriesById.Values);
                entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                return entries;
            }
        }

        public static bool AnyEnabled
        {
            get
            {
                foreach (var entry in EntriesById.Values)
                    if (entry != null && entry.IsEnabled)
                        return true;
                return false;
            }
        }

        public static void Register(string id, string displayName, Func<bool> getEnabled, Action<bool> setEnabled,
            string tooltip = null)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("External gizmo id cannot be empty.", nameof(id));
            if (getEnabled == null)
                throw new ArgumentNullException(nameof(getEnabled));
            if (setEnabled == null)
                throw new ArgumentNullException(nameof(setEnabled));

            EntriesById[id] = new XRayExternalGizmoEntry(id, displayName, getEnabled, setEnabled, tooltip);
            NotifyChanged();
        }

        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (EntriesById.Remove(id))
                NotifyChanged();
        }

        public static void SetAll(bool enabled)
        {
            foreach (var entry in EntriesById.Values)
                entry.SetEnabled(enabled);
            NotifyChanged();
            SceneView.RepaintAll();
        }

        public static void NotifyChanged()
        {
            Changed?.Invoke();
        }
    }

    public sealed class XRayExternalGizmoEntry
    {
        private readonly Func<bool> getEnabled;
        private readonly Action<bool> setEnabled;

        internal XRayExternalGizmoEntry(string id, string displayName, Func<bool> getEnabled, Action<bool> setEnabled,
            string tooltip)
        {
            Id = id;
            DisplayName = string.IsNullOrEmpty(displayName) ? id : displayName;
            Tooltip = tooltip;
            this.getEnabled = getEnabled;
            this.setEnabled = setEnabled;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Tooltip { get; }

        public bool IsEnabled
        {
            get { return getEnabled(); }
        }

        public void SetEnabled(bool enabled)
        {
            setEnabled(enabled);
        }
    }
}
