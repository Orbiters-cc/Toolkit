using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Settings shared by the Orbiters tools: which Orbiters server they talk to.</summary>
    public sealed class OrbitersSettingsWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/settings-window.uss";
        private SegmentedControl server;
        private Label caption;

        public static void Open()
        {
            var window = GetWindow<OrbitersSettingsWindow>(true, "Orbiters settings");
            window.minSize = window.maxSize = new Vector2(380f, 170f);
            window.Show();
        }

        private void CreateGUI()
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) rootVisualElement.styleSheets.Add(sheet);
            rootVisualElement.AddToClassList("orb-settings");

            var title = new Label("Server"); title.AddToClassList("orb-settings__title"); rootVisualElement.Add(title);
            server = new SegmentedControl(new[] { "Production", "Development" }, index => OrbitersEnvironment.IsDevelopment = index == 1);
            rootVisualElement.Add(server);
            caption = new Label(); caption.AddToClassList("orb-settings__caption"); rootVisualElement.Add(caption);

            OrbitersEnvironment.Changed += Sync;
            rootVisualElement.RegisterCallback<DetachFromPanelEvent>(_ => OrbitersEnvironment.Changed -= Sync);
            Sync();
        }

        private void Sync()
        {
            bool development = OrbitersEnvironment.IsDevelopment;
            server.SetIndex(development ? 1 : 0);
            caption.text = development
                ? "Development: the tools talk to a local Orbiters server (localhost:4100) and use its own login. For Orbiters developers."
                : "Production (default): the tools talk to orbiters.cc. Each server keeps its own login.";
        }
    }
}
