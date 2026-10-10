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
        // Far outside any display arrangement. Unity keeps a window on a display (this lands in a display's corner), so the
        // native window is moved there again through the system (NativeWindows); the capture reads the window's own surface.
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
            NativeWindows.ShowHidden(() =>
            {
                ShowWithoutFocus(window);
                window.minSize = window.maxSize = size;
                window.position = rect;
            });
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

        // Windows a show creates never reach the screen: each is cloaked (still drawn, never displayed) the moment it
        // exists, before Windows first shows it, then moved off every display without being activated. The hook lives
        // only for the show, on Unity's main thread, so no reload can leave it calling into unloaded code.
        private static class NativeWindows
        {
#if UNITY_EDITOR_WIN
            private delegate bool EnumProc(IntPtr window, IntPtr data);
            private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct CallResult { public IntPtr result, lParam, wParam; public uint message; public IntPtr window; }
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
            [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
            private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
            private const int AfterWindowProcedure = 12, Created = 0x0001, Cloak = 13;

            internal static void ShowHidden(Action show)
            {
                HookProc cloak = (code, wParam, lParam) =>
                {
                    if (code >= 0 && System.Runtime.InteropServices.Marshal.PtrToStructure<CallResult>(lParam) is var call && call.message == Created)
                    { int on = 1; DwmSetWindowAttribute(call.window, Cloak, ref on, sizeof(int)); }
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                };
                var before = Own();
                var hook = SetWindowsHookEx(AfterWindowProcedure, cloak, IntPtr.Zero, GetCurrentThreadId());
                try { show(); }
                finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); GC.KeepAlive(cloak); }
                foreach (var window in Own().Except(before))
                    SetWindowPos(window, IntPtr.Zero, (int)Offscreen.x, (int)Offscreen.y, 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder);
            }

            private static HashSet<IntPtr> Own()
            {
                uint own = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var found = new HashSet<IntPtr>();
                EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint process); if (process == own) found.Add(window); return true; }, IntPtr.Zero);
                return found;
            }
#else
            internal static void ShowHidden(Action show) => show();
#endif
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
