using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Orbiters - N4N0's setup request, asked in a small window that blocks nothing: Unity and its other windows keep
    /// working (in the background too, and for tools driving the editor), and the request waits in its folder until it is
    /// answered. It says what is added to which avatar, in which scene, and what happens after; Add, or Not now (closing
    /// the window is Not now). It keeps its request through script reloads.
    /// </summary>
    internal sealed class SetupRequestWindow : EditorWindow
    {
        private const string ThemePath = "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss";
        private const string StylePath = "Packages/orbiters.toolkit/Editor/VRChat/setup-request.uss";
        private static readonly Vector2 Size = new Vector2(440f, 318f);

        /// <summary>The request a window is created for (a window's capture, made without one, shows it too).</summary>
        internal static string Current;

        [SerializeField] private string requestPath;
        [SerializeField] private string id;
        [SerializeField] private bool answered;

        /// <summary>Opens the window for the request at <paramref name="path"/>, unless one already asks it.</summary>
        internal static void Ask(string path, string requestId)
        {
            if (Resources.FindObjectsOfTypeAll<SetupRequestWindow>().Any(w => w.id == requestId)) return;
            Current = path;
            var window = CreateInstance<SetupRequestWindow>();
            window.requestPath = path;
            window.id = requestId;
            window.titleContent = new GUIContent("Orbiters - N4N0");
            window.minSize = window.maxSize = Size;
            var main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.center - Size * 0.5f, Size);
            window.ShowUtility();
        }

        public void CreateGUI()
        {
            if (string.IsNullOrEmpty(requestPath)) requestPath = Current;
            JObject request = null;
            try { if (!string.IsNullOrEmpty(requestPath)) request = JObject.Parse(File.ReadAllText(requestPath)); }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException) { }
            if (string.IsNullOrEmpty(id)) id = (string)request?["id"] ?? Path.GetFileNameWithoutExtension(requestPath ?? "");

            var root = rootVisualElement;
            foreach (var sheet in new[] { ThemePath, StylePath }.Select(AssetDatabase.LoadAssetAtPath<StyleSheet>))
                if (sheet) root.styleSheets.Add(sheet);
            root.AddToClassList("orb-setup");
            if (request == null)
            {
                var gone = new Label("This request was answered or withdrawn."); gone.AddToClassList("orb-setup__line"); root.Add(gone);
                return;
            }

            var items = ((JArray)request["items"] ?? new JArray()).OfType<JObject>().ToList();
            string what = items.Count == 0 ? "an item" : string.Join(" and ", items.Select(i => (string)i["name"] ?? "an item"));
            string avatar = (string)request["avatar"]?["name"] ?? (string)request["avatar"]?["id"] ?? "your avatar";
            string scene = (string)request["avatar"]?["scene"];

            var header = new VisualElement(); header.AddToClassList("orb-setup__header"); root.Add(header);
            var tile = new VisualElement(); tile.AddToClassList("orb-setup__tile"); header.Add(tile);
            tile.Add(Orbiters.Toolkit.Editor.N4N0Logo.Image(new Color(0f, 0.855f, 0.427f)));
            var texts = new VisualElement(); texts.AddToClassList("orb-setup__texts"); header.Add(texts);
            var eyebrow = new Label("ORBITERS - N4N0 ASKS"); eyebrow.AddToClassList("orb-setup__eyebrow"); texts.Add(eyebrow);
            var title = new Label($"Add {what} to {avatar}?"); title.AddToClassList("orb-setup__title"); texts.Add(title);

            // What is added, and where.
            var card = new VisualElement(); card.AddToClassList("orb-setup__card"); root.Add(card);
            foreach (var item in items)
                Row(card, IconGlyph.Download, (string)item["name"] ?? "Item", (string)item["version"] ?? "latest", "From the Orbiters gallery, installed by My Avatar");
            Row(card, IconGlyph.Person, avatar, null, string.IsNullOrEmpty(scene) ? "Found by its VRChat avatar id" : "In " + Path.GetFileName(scene));
            var then = ((JArray)request["then"] ?? new JArray()).Select(t => (string)t).ToList();
            string after = string.Join(" and ", then.Select(t => t == "save-scene" ? "the scene is saved" : t == "open-build-panel" ? "the VRChat SDK opens, ready to upload" : null).Where(t => t != null));
            if (after.Length > 0)
            {
                var line = new Label("Then " + after + "."); line.AddToClassList("orb-setup__line"); root.Add(line);
            }

            var push = new VisualElement(); push.style.flexGrow = 1; root.Add(push);
            var actions = new VisualElement(); actions.AddToClassList("orb-setup__actions"); root.Add(actions);
            string server = (string)request["server"];
            if (!string.IsNullOrEmpty(server) && (server.Contains("127.0.0.1") || server.Contains("localhost")))
            {
                var dev = new Label("Development server"); dev.AddToClassList("orb-setup__chip"); actions.Add(dev);
            }
            var spacer = new VisualElement(); spacer.style.flexGrow = 1; actions.Add(spacer);
            var later = Button("Not now", "Nothing changes; Orbiters - N4N0 hears that you said not now.", () => Answer(false));
            later.AddToClassList("orb-setup__later"); actions.Add(later);
            var add = Button("Add " + what, "Install it on the avatar with My Avatar, with Undo.", () => Answer(true));
            add.AddToClassList("mcb-button--primary"); add.AddToClassList("orb-setup__add"); actions.Add(add);

            // Rises in once laid out.
            root.AddToClassList("orb-setup--entering");
            root.schedule.Execute(() => root.RemoveFromClassList("orb-setup--entering")).StartingIn(30);
        }

        private static void Row(VisualElement parent, IconGlyph glyph, string name, string chip, string detail)
        {
            var row = new VisualElement(); row.AddToClassList("orb-setup__row");
            row.EnableInClassList("orb-setup__row--divided", parent.childCount > 0);
            parent.Add(row);
            var badge = new VisualElement(); badge.AddToClassList("orb-setup__icon"); row.Add(badge);
            badge.Add(new VectorIcon(glyph));
            var texts = new VisualElement(); texts.AddToClassList("orb-setup__row-texts"); row.Add(texts);
            var head = new VisualElement(); head.AddToClassList("orb-setup__row-head"); texts.Add(head);
            var label = new Label(name); label.AddToClassList("orb-setup__name"); head.Add(label);
            if (chip != null)
            {
                var version = new Label(chip); version.AddToClassList("orb-setup__version"); head.Add(version);
            }
            var line = new Label(detail); line.AddToClassList("orb-setup__detail"); texts.Add(line);
        }

        private static Button Button(string text, string tip, Action action)
        {
            var button = new Button { text = text, tooltip = tip };
            button.AddToClassList("mcb-button");
            ButtonInteraction.RegisterImmediateClick(button, action);
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("pressed"));
            return button;
        }

        private void Answer(bool add)
        {
            if (answered) return;
            answered = true;
            ProjectSetupRequests.Answer(requestPath, id, add);
            Close();
        }

        // Closed without an answer: Not now.
        private void OnDestroy()
        {
            if (!answered && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(requestPath)) ProjectSetupRequests.Answer(requestPath, id, false);
        }
    }
}
