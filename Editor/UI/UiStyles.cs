using System.Collections.Generic;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The UI kit's stylesheets, loaded once per path instead of once per control (a list with a switch and an icon button
    /// per row asked the asset database twice per row). A sheet deleted or reloaded since is loaded again.
    /// </summary>
    public static class UiStyles
    {
        private static readonly Dictionary<string, StyleSheet> Sheets = new Dictionary<string, StyleSheet>();

        /// <summary>The stylesheet at <paramref name="path"/>, or null when there is none.</summary>
        public static StyleSheet Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Sheets.TryGetValue(path, out var sheet) && sheet != null) return sheet;
            sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet != null) Sheets[path] = sheet;
            else Sheets.Remove(path);
            return sheet;
        }

        /// <summary>Adds the stylesheet at <paramref name="path"/> to <paramref name="element"/> (once), as the controls always did.</summary>
        public static void Attach(VisualElement element, string path)
        {
            var sheet = Load(path);
            if (sheet != null && element != null && !element.styleSheets.Contains(sheet)) element.styleSheets.Add(sheet);
        }
    }
}
