using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// About an Orbiters tool: its logo, name, version and license, then the third-party software it ships with or works
    /// with, one card each with its license. The list is read from the tool's notices file (markdown: "## Title", an intro
    /// paragraph whose last line may be the project URL, then the license in a ``` block), so the window and the file
    /// never differ. Each tool derives a window that describes itself, so it reopens after a script reload.
    /// </summary>
    public abstract class OrbitersAboutWindow : EditorWindow
    {
        private const string ThemePath = "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss";
        private const string StylePath = "Packages/orbiters.toolkit/Editor/UI/about-window.uss";

        public sealed class Notice
        {
            public string title, intro, url, license, text;
        }

        protected sealed class Product
        {
            public string Name, Author, Version, LicensePath, NoticesPath;
            public string Caption = "Click one to read its license.";
            public Func<VisualElement> Logo;
        }

        protected abstract Product Describe();

        protected static void Open<T>(string title) where T : OrbitersAboutWindow
        {
            var window = GetWindow<T>(true, title, true);
            window.minSize = new Vector2(420f, 460f);
            window.Show();
        }

        public void CreateGUI()
        {
            var product = Describe();
            var root = rootVisualElement;
            foreach (var path in new[] { ThemePath, StylePath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet) root.styleSheets.Add(sheet);
            }
            root.AddToClassList("orb-about");

            var scrollView = new ScrollView(); scrollView.AddToClassList("orb-about__scroll"); root.Add(scrollView);
            var scroll = new VisualElement(); scroll.AddToClassList("orb-about__content"); scrollView.Add(scroll);

            var header = new VisualElement(); header.AddToClassList("orb-about__header"); scroll.Add(header);
            var logo = product.Logo?.Invoke();
            if (logo != null) { logo.AddToClassList("orb-about__logo"); header.Add(logo); }
            var name = new Label(product.Name); name.AddToClassList("orb-about__name"); header.Add(name);
            var meta = new Label((string.IsNullOrEmpty(product.Version) ? "" : "Version " + product.Version + " · ") + "by " + product.Author);
            meta.AddToClassList("orb-about__meta"); header.Add(meta);
            if (!string.IsNullOrEmpty(product.LicensePath) && File.Exists(Path.GetFullPath(product.LicensePath)))
            {
                var license = Button("License", "Open " + product.Name + "'s license.", () => OpenFile(product.LicensePath));
                license.AddToClassList("orb-about__license"); header.Add(license);
            }

            var section = new Label("Third-party software"); section.AddToClassList("orb-about__section"); scroll.Add(section);
            var caption = new Label(product.Caption); caption.AddToClassList("orb-about__caption"); scroll.Add(caption);

            var notices = Read(AssetDatabase.LoadAssetAtPath<TextAsset>(product.NoticesPath)?.text);
            if (notices.Count == 0)
            {
                var missing = new Label("The third-party notices file is missing from this installation.");
                missing.AddToClassList("orb-about__caption"); scroll.Add(missing);
            }
            foreach (var notice in notices) scroll.Add(Card(notice));

            var footer = new VisualElement(); footer.AddToClassList("orb-about__footer"); scroll.Add(footer);
            footer.Add(Button("Open notices file", "Open " + Path.GetFileName(product.NoticesPath) + ".", () => OpenFile(product.NoticesPath)));
        }

        // One card per project: its name, what the tool uses it for, a license chip; the full text slides open below.
        private static VisualElement Card(Notice notice)
        {
            var card = new VisualElement(); card.AddToClassList("orb-about__card");
            var head = new VisualElement(); head.AddToClassList("orb-about__card-head"); card.Add(head);
            var texts = new VisualElement(); texts.AddToClassList("orb-about__card-texts"); head.Add(texts);
            var title = new Label(notice.title); title.AddToClassList("orb-about__card-title"); texts.Add(title);
            if (!string.IsNullOrEmpty(notice.intro)) { var intro = new Label(notice.intro); intro.AddToClassList("orb-about__card-intro"); texts.Add(intro); }
            var chip = new Label(notice.license); chip.AddToClassList("orb-about__chip"); head.Add(chip);
            if (!string.IsNullOrEmpty(notice.url))
            {
                var link = Button("↗", notice.url, () => Application.OpenURL(notice.url));
                link.AddToClassList("orb-about__link"); head.Add(link);
            }
            if (string.IsNullOrEmpty(notice.text)) return card;

            // Paragraphs reflow to the window width; the file keeps the license's original line breaks.
            string reflowed = Regex.Replace(Regex.Replace(notice.text, @"(?<!\n)\n(?!\n)", " "), @"[ \t]{2,}", " ");
            var body = new Label(reflowed); body.AddToClassList("orb-about__license-text");
            body.selection.isSelectable = true;
            card.Add(body);
            body.style.display = DisplayStyle.None;
            // Opens on press, and the text fades in.
            head.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.target is Button) return;
                bool open = !card.ClassListContains("orb-about__card--open");
                card.EnableInClassList("orb-about__card--open", open);
                body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                body.RemoveFromClassList("orb-about__license-text--shown");
                if (open) body.schedule.Execute(() => body.AddToClassList("orb-about__license-text--shown"));
            });
            return card;
        }

        public static List<Notice> Read(string markdown)
        {
            var notices = new List<Notice>();
            if (string.IsNullOrEmpty(markdown)) return notices;
            var parts = Regex.Split(markdown.Replace("\r\n", "\n"), @"^## ", RegexOptions.Multiline);
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                int newline = part.IndexOf('\n');
                var notice = new Notice { title = (newline < 0 ? part : part.Substring(0, newline)).Trim() };
                string rest = newline < 0 ? "" : part.Substring(newline + 1);
                var code = Regex.Match(rest, "```\\n(.*?)\\n```", RegexOptions.Singleline);
                notice.text = code.Success ? code.Groups[1].Value.Trim('\n') : null;
                var introLines = new List<string>();
                foreach (string line in (code.Success ? rest.Substring(0, code.Index) : rest).Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)) notice.url = trimmed;
                    // A list item keeps its own line.
                    else if (trimmed.StartsWith("- ", StringComparison.Ordinal)) introLines.Add("\n• " + trimmed.Substring(2).Replace("`", ""));
                    else if (trimmed.Length > 0) introLines.Add(trimmed.Replace("`", ""));
                }
                notice.intro = string.Join(" ", introLines).Replace(" \n", "\n");
                notice.license = LicenseName(notice.text, notice.intro);
                notices.Add(notice);
            }
            return notices;
        }

        private static string LicenseName(string text, string intro)
        {
            string all = (text ?? "") + " " + (intro ?? "");
            if (all.IndexOf("public domain", StringComparison.OrdinalIgnoreCase) >= 0) return "Public domain";
            // Before MIT: the OFL also grants permission "free of charge".
            if (all.IndexOf("SIL Open Font License", StringComparison.OrdinalIgnoreCase) >= 0) return "OFL";
            if (all.IndexOf("MIT License", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("Permission is hereby granted, free of charge", StringComparison.Ordinal) >= 0) return "MIT";
            if (all.IndexOf("BSD", StringComparison.Ordinal) >= 0 || all.IndexOf("Redistribution and use in source and binary forms", StringComparison.Ordinal) >= 0) return "BSD";
            if (all.IndexOf("provided 'as-is'", StringComparison.OrdinalIgnoreCase) >= 0) return "zlib";
            return "License";
        }

        private static void OpenFile(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset != null) AssetDatabase.OpenAsset(asset);
            else EditorUtility.RevealInFinder(Path.GetFullPath(assetPath));
        }

        private static Button Button(string text, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            button.AddToClassList("orb-about__button");
            // Immediate feedback on press; the click itself still follows Unity's normal release behaviour.
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("orb-about__button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("orb-about__button--pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("orb-about__button--pressed"));
            return button;
        }
    }
}
