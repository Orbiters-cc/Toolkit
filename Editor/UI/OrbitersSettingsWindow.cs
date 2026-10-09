using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Settings shared by the Orbiters tools: which Orbiters server they talk to and which opt-in features are on.</summary>
    public sealed class OrbitersSettingsWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/settings-window.uss";
        private SegmentedControl server;
        private Label caption;
        private readonly Dictionary<string, (ToggleSwitch toggle, VisualElement row)> featureRows = new Dictionary<string, (ToggleSwitch, VisualElement)>();
        private static readonly List<(string title, Func<VisualElement> build)> sections = new List<(string, Func<VisualElement>)>();

        /// <summary>A tool's own settings card ("My Avatar · Asset gallery files"), shown after the server card.</summary>
        public static void RegisterSection(string title, Func<VisualElement> build)
        {
            if (string.IsNullOrEmpty(title) || build == null || sections.Any(s => s.title == title)) return;
            sections.Add((title, build));
        }

        public static void Open()
        {
            var window = GetWindow<OrbitersSettingsWindow>(true, "Orbiters settings");
            window.minSize = new Vector2(400f, 200f);
            window.maxSize = new Vector2(640f, 1200f);
            float height = OrbitersFeatures.All.Count == 0 ? 200f : Mathf.Min(680f, 250f + 70f * OrbitersFeatures.All.Count);
            window.position = new Rect(window.position.position, new Vector2(Mathf.Max(420f, window.position.width), height));
            window.Show();
        }

        private void CreateGUI()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) rootVisualElement.styleSheets.Add(sheet);
            rootVisualElement.AddToClassList("orb-settings");
            // Padding on an inner element: Unity's own ScrollView content-container rules override padding set on it.
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("orb-settings__scroll"); rootVisualElement.Add(scroll);
            var content = new VisualElement(); content.AddToClassList("orb-settings__content"); scroll.Add(content);

            var serverCard = new VisualElement(); serverCard.AddToClassList("orb-settings__card"); content.Add(serverCard);
            serverCard.Add(Title("Server"));
            server = new SegmentedControl(new[] { "Production", "Development" }, index => OrbitersEnvironment.IsDevelopment = index == 1);
            serverCard.Add(server);
            caption = new Label(); caption.AddToClassList("orb-settings__caption"); serverCard.Add(caption);

            foreach (var (title, build) in sections)
            {
                var card = new VisualElement(); card.AddToClassList("orb-settings__card"); content.Add(card);
                card.Add(Title(title));
                try { card.Add(build()); }
                catch (Exception ex) { card.Add(new Label("This section could not be shown: " + ex.Message)); }
            }

            BuildFeatures(content);

            OrbitersEnvironment.Changed += Sync;
            OrbitersFeatures.Changed += OnFeatureChanged;
            rootVisualElement.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                OrbitersEnvironment.Changed -= Sync;
                OrbitersFeatures.Changed -= OnFeatureChanged;
            });
            Sync();
        }

        private void BuildFeatures(VisualElement content)
        {
            featureRows.Clear();
            if (OrbitersFeatures.All.Count == 0) return;
            var title = Title("Features"); title.AddToClassList("orb-settings__title--section"); content.Add(title);
            var note = new Label("Early features are off by default. They may change or break between updates.");
            note.AddToClassList("orb-settings__caption"); note.AddToClassList("orb-settings__caption--lead"); content.Add(note);

            foreach (var product in OrbitersFeatures.All.GroupBy(f => string.IsNullOrEmpty(f.Product) ? "Orbiters" : f.Product))
            {
                var group = new VisualElement(); group.AddToClassList("orb-settings__group"); content.Add(group);
                var name = new Label(product.Key); name.AddToClassList("orb-settings__product"); group.Add(name);
                foreach (var feature in product)
                {
                    var row = FeatureRow(feature);
                    // USS has no sibling selector: the first row of a group drops its separator here.
                    row.EnableInClassList("orb-settings__feature--first", group.childCount == 1);
                    group.Add(row);
                }
            }
        }

        private VisualElement FeatureRow(OrbitersFeature feature)
        {
            var row = new VisualElement(); row.AddToClassList("orb-settings__feature");
            var texts = new VisualElement(); texts.AddToClassList("orb-settings__feature-texts"); row.Add(texts);
            var head = new VisualElement(); head.AddToClassList("orb-settings__feature-head"); texts.Add(head);
            var label = new Label(feature.Label); label.AddToClassList("orb-settings__feature-label"); head.Add(label);
            head.Add(new StageBadge(feature.Stage));
            if (!string.IsNullOrEmpty(feature.Description))
            {
                var description = new Label(feature.Description); description.AddToClassList("orb-settings__feature-description"); texts.Add(description);
            }
            var toggle = new ToggleSwitch(OrbitersFeatures.IsEnabled(feature.Key), on => OrbitersFeatures.SetEnabled(feature.Key, on));
            toggle.AddToClassList("orb-settings__feature-toggle");
            row.Add(toggle);
            featureRows[feature.Key] = (toggle, row);
            return row;
        }

        private void OnFeatureChanged(string key)
        {
            foreach (var pair in featureRows)
            {
                var feature = OrbitersFeatures.Find(pair.Key);
                bool available = feature == null || string.IsNullOrEmpty(feature.Requires) || OrbitersFeatures.IsEnabled(feature.Requires);
                pair.Value.row.SetEnabled(available);
                pair.Value.toggle.SetValueWithoutNotify(OrbitersFeatures.IsEnabled(pair.Key));
            }
        }

        private void Sync()
        {
            bool development = OrbitersEnvironment.IsDevelopment;
            server.SetIndex(development ? 1 : 0);
            caption.text = development
                ? "Development: the tools talk to a local Orbiters server (127.0.0.1:4100) and use its own login. For Orbiters developers."
                : "Production (default): the tools talk to orbiters.cc. Each server keeps its own login.";
            OnFeatureChanged(null);
        }

        private static Label Title(string text)
        {
            var title = new Label(text); title.AddToClassList("orb-settings__title");
            return title;
        }
    }
}
