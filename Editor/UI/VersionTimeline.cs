using Orbiters.Toolkit.Versions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The version timeline of Orbiters tools (MCB's version list, My Avatar's gallery releases): a green line with one
    /// marker per version, a dotted connector for collapsed history, and outline chips for scope and state. Each element
    /// carries <c>version-timeline.uss</c>, so any window can use it.
    /// </summary>
    public static class VersionTimeline
    {
        public const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/version-timeline.uss";
        public static readonly Color Accent = new Color32(0, 218, 109, 255);
        private static StyleSheet sheet;

        private static StyleSheet Sheet => sheet ? sheet : sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);

        private static T Styled<T>(T element) where T : VisualElement
        {
            if (Sheet) element.styleSheets.Add(Sheet);
            return element;
        }

        /// <summary>The line and marker beside one version: hidden line ends at the first and last version.</summary>
        public static VisualElement Marker(bool isFirst, bool isLast, bool isSelected, bool isDisabled)
        {
            var timeline = Styled(new VisualElement());
            timeline.AddToClassList("orb-version-timeline");
            timeline.EnableInClassList("orb-version-timeline--disabled", isDisabled);
            timeline.Add(Line(isFirst));
            var shell = new VisualElement();
            shell.AddToClassList("orb-version-timeline__marker-shell");
            shell.EnableInClassList("orb-version-timeline__marker-shell--selected", isSelected);
            var marker = new VisualElement();
            marker.AddToClassList("orb-version-timeline__marker");
            shell.Add(marker);
            timeline.Add(shell);
            timeline.Add(Line(isLast));
            return timeline;
        }

        /// <summary>The line between groups of versions; three dots while older versions are collapsed.</summary>
        public static VisualElement Connector(bool collapsed)
        {
            var timeline = Styled(new VisualElement());
            timeline.AddToClassList("orb-version-timeline");
            timeline.AddToClassList("orb-version-timeline--connector");
            timeline.EnableInClassList("orb-version-timeline--collapsed", collapsed);
            if (collapsed)
            {
                var dots = new VisualElement();
                dots.AddToClassList("orb-version-timeline__connector-dots");
                for (int i = 0; i < 3; i++)
                {
                    var dot = new VisualElement();
                    dot.AddToClassList("orb-version-timeline__connector-dot");
                    dots.Add(dot);
                }
                timeline.Add(dots);
            }
            else
            {
                var line = new VisualElement();
                line.AddToClassList("orb-version-timeline__connector-line");
                timeline.Add(line);
            }
            return timeline;
        }

        private static VisualElement Line(bool hidden)
        {
            var line = new VisualElement();
            line.AddToClassList("orb-version-timeline__line");
            line.EnableInClassList("orb-version-timeline__line--hidden", hidden);
            return line;
        }

        /// <summary>An outline chip ("public", "installed") in <paramref name="color"/>.</summary>
        public static Label Chip(string text, Color color, bool lowercase = true)
        {
            string value = text ?? string.Empty;
            var chip = Styled(new Label(lowercase ? value.ToLowerInvariant() : value));
            chip.AddToClassList("orb-version-chip");
            chip.style.fontSize = 11;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            chip.style.color = color;
            chip.style.borderTopColor = chip.style.borderRightColor = chip.style.borderBottomColor = chip.style.borderLeftColor = color;
            return chip;
        }

        /// <summary>Public green, beta yellow, alpha red, as in MCB's version list.</summary>
        public static Color ScopeColor(string scope)
        {
            switch (VersionScopes.Normalize(scope))
            {
                case VersionScopes.Public: return Accent;
                case VersionScopes.Beta: return Color.yellow;
                case VersionScopes.Alpha: return Color.red;
                default: return Color.magenta;
            }
        }

        public static Label ScopeChip(string scope) => Chip(VersionScopes.Normalize(scope), ScopeColor(scope));

        /// <summary>
        /// A compact read-only timeline of records, newest first: marker, version and title, scope chip, date and changelog.
        /// <paramref name="decorate"/> adds state chips or actions to each row's header.
        /// </summary>
        public static VisualElement List(System.Collections.Generic.IReadOnlyList<IVersionRecord> records,
            System.Action<IVersionRecord, VisualElement> decorate = null, int selected = -1)
        {
            var list = Styled(new VisualElement());
            list.AddToClassList("orb-version-list");
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                var row = new VisualElement();
                row.AddToClassList("orb-version-list__row");
                row.Add(Marker(i == 0, i == records.Count - 1, i == selected, false));
                var body = new VisualElement();
                body.AddToClassList("orb-version-list__body");
                var header = new VisualElement();
                header.AddToClassList("orb-version-list__header");
                var name = new Label(record.Version + (string.IsNullOrWhiteSpace(record.Title) ? "" : " · " + record.Title));
                name.AddToClassList("orb-version-list__name");
                header.Add(name);
                header.Add(ScopeChip(record.Scope));
                decorate?.Invoke(record, header);
                body.Add(header);
                string date = ShortDate(record.Date);
                if (!string.IsNullOrEmpty(date))
                {
                    var when = new Label(date);
                    when.AddToClassList("orb-version-list__date");
                    body.Add(when);
                }
                if (!string.IsNullOrWhiteSpace(record.Changelog))
                {
                    var changes = new Label(record.Changelog.Trim());
                    changes.AddToClassList("orb-version-list__changelog");
                    body.Add(changes);
                }
                row.Add(body);
                list.Add(row);
            }
            return list;
        }

        public static string ShortDate(string value) =>
            System.DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
                ? date.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : value;
    }
}
