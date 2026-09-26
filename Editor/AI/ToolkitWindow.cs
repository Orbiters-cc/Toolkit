using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    public sealed class ToolkitWindow : EditorWindow
    {
        private Toggle codex;
        private Toggle claude;
        private Label codexStatus;
        private Label claudeStatus;
        private HelpBox feedback;
        private Button install;
        private bool busy;

        [MenuItem("Tools/Orbiters/Toolkit")]
        public static void Open()
        {
            var window = GetWindow<ToolkitWindow>(false, "Orbiters Toolkit", true);
            window.minSize = new Vector2(460, 410);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = root.style.paddingRight = 24;
            root.style.paddingTop = root.style.paddingBottom = 22;
            var heading = new Label("Orbiters Toolkit");
            heading.style.fontSize = 23;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(heading);
            var subtitle = new Label("AI integration");
            subtitle.style.fontSize = 15;
            subtitle.style.marginTop = 8;
            root.Add(subtitle);
            var intro = new Label("Teach your assistant to capture Unity windows in the background and inspect the result.");
            intro.style.whiteSpace = WhiteSpace.Normal;
            intro.style.marginTop = 8;
            intro.style.marginBottom = 16;
            root.Add(intro);
            codex = AddClient(root, "Codex", true, out codexStatus);
            claude = AddClient(root, "Claude Code", false, out claudeStatus);
            install = new Button(QueueInstall) { text = "Install AI integration" };
            install.style.height = 36;
            install.style.marginTop = 16;
            install.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) QueueInstall(); });
            root.Add(install);
            feedback = new HelpBox("Installs instructions for this project. Your MCP connection and other assistant settings stay as configured.", HelpBoxMessageType.Info);
            feedback.style.marginTop = 12;
            root.Add(feedback);
            var refresh = new Button(RefreshStatus) { text = "Refresh status" };
            refresh.style.marginTop = 8;
            root.Add(refresh);
            codex.RegisterValueChangedCallback(_ => RefreshStatus());
            claude.RegisterValueChangedCallback(_ => RefreshStatus());
            RefreshStatus();
        }

        private static Toggle AddClient(VisualElement root, string title, bool isCodex, out Label status)
        {
            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f);
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius = 7;
            card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 7;
            card.style.paddingLeft = card.style.paddingRight = 12;
            card.style.paddingTop = card.style.paddingBottom = 10;
            card.style.marginBottom = 8;
            var toggle = new Toggle(title) { value = true };
            card.Add(toggle);
            status = new Label();
            status.style.marginTop = 5;
            card.Add(status);
            var location = new Label(SkillInstaller.RelativePath(isCodex));
            location.style.fontSize = 10;
            location.style.color = new Color(0.7f, 0.7f, 0.7f);
            location.style.whiteSpace = WhiteSpace.Normal;
            card.Add(location);
            root.Add(card);
            return toggle;
        }

        private void RefreshStatus()
        {
            try
            {
                string content = SkillInstaller.BundledSkill;
                codexStatus.text = SkillInstaller.Status(SkillInstaller.ProjectRoot, true, content);
                claudeStatus.text = SkillInstaller.Status(SkillInstaller.ProjectRoot, false, content);
                bool selectedInstalled = (!codex.value || codexStatus.text == "Installed") &&
                    (!claude.value || claudeStatus.text == "Installed");
                install.text = selectedInstalled ? "Integration is up to date" : "Install / update AI integration";
                install.SetEnabled(!busy && (codex.value || claude.value) && !selectedInstalled);
            }
            catch (Exception ex) { feedback.text = ex.Message; feedback.messageType = HelpBoxMessageType.Error; }
        }

        private void QueueInstall()
        {
            if (busy || (!codex.value && !claude.value)) return;
            busy = true;
            install.text = "Installing…";
            install.SetEnabled(false);
            bool installCodex = codex.value, installClaude = claude.value;
            // Let the pressed state render before filesystem work; normal Button.clicked remains supported.
            rootVisualElement.schedule.Execute(() =>
            {
                try
                {
                    feedback.text = SkillInstaller.Install(SkillInstaller.ProjectRoot, installCodex, installClaude, SkillInstaller.BundledSkill);
                    feedback.messageType = HelpBoxMessageType.Info;
                }
                catch (Exception ex) { feedback.text = ex.Message; feedback.messageType = HelpBoxMessageType.Error; }
                finally { busy = false; RefreshStatus(); }
            }).ExecuteLater(50);
        }
    }
}
