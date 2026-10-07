using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Hidden copies of editor windows for background captures: shown without focus, far outside every display, closed
    /// after the capture. The user's layout, tabs, scroll positions, selection and focus stay as they are; Unity is never
    /// brought forward. Used for windows that are not open, inactive docked tabs, a taller view of a long window, and
    /// Inspectors locked on any object.
    /// </summary>
    internal static class EditorWindowHiddenHost
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // Far outside any display arrangement.
        private static readonly Vector2 Offscreen = new Vector2(-32000f, -32000f);

        internal static readonly Type InspectorType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.InspectorWindow");

        /// <summary>A hidden <paramref name="type"/> window of <paramref name="size"/> points, with <paramref name="template"/>'s serialized state when given.</summary>
        internal static EditorWindow Open(Type type, Vector2 size, EditorWindow template = null)
        {
            var window = (EditorWindow)ScriptableObject.CreateInstance(type);
            if (template != null) EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(template), window);
            window.hideFlags = HideFlags.HideAndDontSave;
            if (template != null) window.titleContent = new GUIContent(template.titleContent);
            var rect = new Rect(Offscreen, size);
            window.position = rect;
            ShowWithoutFocus(window);
            window.minSize = window.maxSize = size;
            window.position = rect;
            return window;
        }

        /// <summary>A hidden Inspector locked on <paramref name="target"/>; the selection is not changed.</summary>
        internal static EditorWindow Inspector(Object target, Vector2 size)
        {
            if (InspectorType == null) throw new NotSupportedException("This Unity version has no InspectorWindow type.");
            var window = Open(InspectorType, size);
            var setLocked = InspectorType.GetMethod("SetObjectsLocked", Members, null, new[] { typeof(List<Object>) }, null);
            if (setLocked == null) { window.Close(); throw new NotSupportedException("This Unity version cannot lock an Inspector on an object."); }
            setLocked.Invoke(window, new object[] { new List<Object> { target } });
            return window;
        }

        // EditorWindow.ShowPopup() is ShowPopupWithMode(PopupMenu, giveFocus: true): the same without focus.
        private static void ShowWithoutFocus(EditorWindow window)
        {
            var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
            var show = showMode == null ? null : typeof(EditorWindow).GetMethod("ShowPopupWithMode", Members, null, new[] { showMode, typeof(bool) }, null);
            if (show == null)
            {
                Object.DestroyImmediate(window);
                throw new NotSupportedException("This Unity version cannot show a window without focusing it.");
            }
            show.Invoke(window, new[] { Enum.Parse(showMode, "PopupMenu"), (object)false });
        }

        /// <summary>
        /// An object by instance ID, asset path ("Assets/…", "Packages/…") or hierarchy path ("Root/Child", optionally
        /// prefixed by the scene name and a colon), inactive objects included.
        /// </summary>
        internal static Object Resolve(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            reference = reference.Trim();
            if (int.TryParse(reference, out int id)) return EditorUtility.InstanceIDToObject(id);
            if (reference.StartsWith("Assets/", StringComparison.Ordinal) || reference.StartsWith("Packages/", StringComparison.Ordinal))
                return AssetDatabase.LoadMainAssetAtPath(reference);
            string sceneName = null;
            int colon = reference.IndexOf(':');
            if (colon > 0) { sceneName = reference.Substring(0, colon); reference = reference.Substring(colon + 1); }
            var parts = reference.Trim('/').Split('/');
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var roots = new List<GameObject>();
            if (stage != null) roots.Add(stage.prefabContentsRoot);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && (sceneName == null || scene.name == sceneName)) roots.AddRange(scene.GetRootGameObjects());
            }
            foreach (var root in roots.Where(r => r.name == parts[0]))
            {
                var current = root.transform;
                for (int i = 1; current != null && i < parts.Length; i++)
                    current = current.Cast<Transform>().FirstOrDefault(child => child.name == parts[i]);
                if (current != null) return current.gameObject;
            }
            return null;
        }

        /// <summary>
        /// Scrolls the scroll view holding the first element matching <paramref name="key"/> (its name, a USS class, or
        /// text it shows) so the element is at the top. False when nothing matches yet.
        /// </summary>
        internal static bool ScrollTo(EditorWindow window, string key)
        {
            var element = window.rootVisualElement.Query<VisualElement>().Where(e => e.name == key || e.ClassListContains(key) ||
                (e is TextElement text && !string.IsNullOrEmpty(text.text) && text.text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)).First();
            if (element == null) return false;
            for (var parent = element.parent; parent != null; parent = parent.parent)
                if (parent is ScrollView scroll)
                {
                    float offset = element.worldBound.y - scroll.contentContainer.worldBound.y;
                    scroll.scrollOffset = new Vector2(scroll.scrollOffset.x, Mathf.Max(0f, offset - 8f));
                    return true;
                }
            return true;
        }
    }
}
