using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Asks before content that contains code is imported: Unity compiles and runs it with the user's permissions. Cancel is
    /// the default (Escape, closing the window). Returns false without asking in batch mode.
    /// </summary>
    public sealed class UntrustedCodeDialog : EditorWindow
    {
        public sealed class Request
        {
            /// <summary>What is being imported ("Kitty Rex 2.1", "Rexouium Hoodie.unitypackage").</summary>
            public string Subject;
            /// <summary>Who published it, when known.</summary>
            public string Author;
            /// <summary>Why the user sees this, in one or two sentences.</summary>
            public string Message;
            public IReadOnlyList<string> Files;
            public string ConfirmLabel = "Import anyway";
        }

        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Security/untrusted-code-dialog.uss";
        private const int MaxListed = 200;
        private Request request;
        private bool answered;

        public static bool Confirm(Request request)
        {
            if (request == null || Application.isBatchMode) return false;
            var window = CreateInstance<UntrustedCodeDialog>();
            window.request = request;
            window.titleContent = new GUIContent("Code in this download");
            var size = new Vector2(540, Mathf.Clamp(300 + 18 * Mathf.Min(request.Files?.Count ?? 0, 10), 340, 520));
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - size / 2, size);
            window.minSize = window.maxSize = size;
            bool result = false;
            window.Answered = value => result = value;
            try { window.ShowModalUtility(); }
            finally { if (window) DestroyImmediate(window); }
            return result;
        }

        private System.Action<bool> Answered;

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) root.styleSheets.Add(sheet);
            root.AddToClassList("orb-code");
            if (request == null) { Close(); return; }

            var header = new VisualElement(); header.AddToClassList("orb-code__header"); root.Add(header);
            var icon = new Image { image = EditorGUIUtility.IconContent("console.warnicon").image, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("orb-code__icon"); header.Add(icon);
            var titles = new VisualElement(); titles.AddToClassList("orb-code__titles"); header.Add(titles);
            titles.Add(Text("This content contains code", "orb-code__title"));
            string subject = string.IsNullOrEmpty(request.Author) ? request.Subject : $"{request.Subject} · by {request.Author}";
            if (!string.IsNullOrEmpty(subject)) titles.Add(Text(subject, "orb-code__subject"));

            var card = new VisualElement(); card.AddToClassList("orb-code__card"); root.Add(card);
            card.Add(Text(request.Message ?? "Orbiters does not review the files and scripts of this content. Unity compiles and runs code as soon as it is imported, with your permissions.", "orb-code__message"));

            int count = request.Files?.Count ?? 0;
            root.Add(Text($"{count} code file{(count == 1 ? "" : "s")}", "orb-code__count"));
            var list = new ScrollView(ScrollViewMode.Vertical); list.AddToClassList("orb-code__list"); root.Add(list);
            foreach (var file in (request.Files ?? new List<string>()).Take(MaxListed)) list.Add(Text(file, "orb-code__file"));
            if (count > MaxListed) list.Add(Text($"…and {count - MaxListed} more", "orb-code__file"));

            var buttons = new VisualElement(); buttons.AddToClassList("orb-code__buttons"); root.Add(buttons);
            var cancel = new Button { name = "cancel", text = "Cancel" }; cancel.AddToClassList("orb-code__button");
            var confirm = new Button { name = "confirm", text = string.IsNullOrEmpty(request.ConfirmLabel) ? "Import anyway" : request.ConfirmLabel };
            confirm.AddToClassList("orb-code__button"); confirm.AddToClassList("orb-code__button--risk");
            ButtonInteraction.RegisterImmediateClick(cancel, () => Answer(false));
            ButtonInteraction.RegisterImmediateClick(confirm, () => Answer(true));
            buttons.Add(cancel); buttons.Add(confirm);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            cancel.Focus();
        }

        private static Label Text(string text, string className)
        {
            var label = new Label(text) { enableRichText = false }; label.AddToClassList(className);
            return label;
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Escape) Answer(false);
        }

        private void Answer(bool value)
        {
            if (answered) return;
            answered = true;
            var callback = Answered;
            Answered = null;
            try { callback?.Invoke(value); }
            finally
            {
                // Unshown/disposed window instances have no host view for EditorWindow.Close to detach from.
                if (this)
                {
                    if (rootVisualElement.panel == null) DestroyImmediate(this);
                    else Close();
                }
            }
        }

        // Closing with the window's own button is a Cancel.
        private void OnDestroy()
        {
            if (answered) return;
            answered = true;
            var callback = Answered;
            Answered = null;
            callback?.Invoke(false);
        }
    }
}
