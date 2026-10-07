using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEngine.UIElements;
using static Orbiters.Toolkit.Editor.Animations.AnimationExtractorWindow;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>One model in the extractor: its rig, its clips to tick and name, where they go and how each went.</summary>
    internal sealed class AnimationModelCard : VisualElement
    {
        private sealed class Row
        {
            public ClipEntry Entry;
            public CheckBox Check;
            public TextField Name;
            public VisualElement Status;
            public Label StatusLabel, Message;
        }

        private readonly Action changed;
        private readonly List<Row> rows = new List<Row>();
        private readonly Label title, path, rig, count, folderLabel;
        private readonly VisualElement list;
        private readonly Button showFolder;
        private AnimationSourceInfo info;
        private string folder;

        public ModelEntry Entry { get; }
        public string Path => info?.Path;
        public int SelectedCount => rows.Count(r => r.Entry.Selected);

        public AnimationModelCard(ModelEntry entry, Action changed, Action remove)
        {
            Entry = entry;
            this.changed = changed;
            AddToClassList("ae-card");

            var header = new VisualElement(); header.AddToClassList("ae-model__header"); Add(header);
            var titles = new VisualElement(); titles.AddToClassList("ae-model__titles"); header.Add(titles);
            var titleRow = new VisualElement(); titleRow.AddToClassList("ae-model__title-row"); titles.Add(titleRow);
            title = Text("", "ae-model__title"); titleRow.Add(title);
            rig = Text("", "ae-chip"); titleRow.Add(rig);
            path = Text("", "ae-caption", "ae-model__path"); titles.Add(path);
            var close = new IconButton(IconGlyph.Close, "", "Remove from the list. The model stays in the project.", remove);
            close.AddToClassList("orb-icon-button--small");
            header.Add(close);

            var bar = new VisualElement(); bar.AddToClassList("ae-model__bar"); Add(bar);
            count = Text("", "ae-caption", "ae-model__count"); bar.Add(count);
            bar.Add(Pill("All", () => SelectAll(true), "ae-link"));
            bar.Add(Pill("None", () => SelectAll(false), "ae-link"));

            list = new VisualElement(); Add(list);

            var footer = new VisualElement(); footer.AddToClassList("ae-model__footer"); Add(footer);
            folderLabel = Text("", "ae-caption", "ae-model__folder"); footer.Add(folderLabel);
            showFolder = Pill("Show in Project", Ping, "ae-link", "ae-link--accent");
            showFolder.tooltip = "Highlights the folder in the Project window.";
            footer.Add(showFolder);
        }

        /// <summary>Lists <paramref name="source"/>'s clips, keeping the choices and names of clips listed before.</summary>
        public void Bind(AnimationSourceInfo source, string outputFolder)
        {
            info = source;
            title.text = source.Name;
            path.text = source.Path;
            rig.text = RigName(source.Rig);
            rig.style.display = source.Rig.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            rig.EnableInClassList("ae-chip--accent", source.Rig == ModelImporterAnimationType.Human);

            var kept = Entry.Clips.ToList();
            Entry.Clips.Clear();
            rows.Clear();
            list.Clear();
            foreach (var clip in source.Clips)
            {
                var entry = kept.FirstOrDefault(c => c.Clip == clip.Name && c.Occurrence == clip.Occurrence) ??
                            new ClipEntry { Clip = clip.Name, Occurrence = clip.Occurrence, Name = AnimationExtraction.DefaultName(source, clip) };
                Entry.Clips.Add(entry);
                AddRow(entry, clip);
            }
            if (source.Clips.Count == 0) list.Add(Text("This model has no animations any more.", "ae-caption", "ae-model__empty"));
            SetFolder(outputFolder);
            SyncCount();
        }

        public void SetFolder(string outputFolder)
        {
            folder = outputFolder;
            folderLabel.text = "Saves to " + folder;
            folderLabel.tooltip = folder;
            SyncFolderLink();
        }

        public List<ExtractionJob> Jobs() => info == null ? new List<ExtractionJob>() : rows.Where(r => r.Entry.Selected).Select(r => new ExtractionJob
        {
            Source = info.Path, Clip = r.Entry.Clip, Occurrence = r.Entry.Occurrence, Name = r.Entry.Name, Folder = folder,
        }).ToList();

        public void ClearResults()
        {
            foreach (var row in rows)
            {
                row.Status.style.display = DisplayStyle.None;
                row.Message.style.display = DisplayStyle.None;
            }
            SetFolder(folder);
        }

        public void ShowResults(IEnumerable<ExtractionResult> results)
        {
            int done = 0, skipped = 0, failed = 0;
            foreach (var result in results)
            {
                if (result.Status == ExtractionStatus.Done) done++;
                else if (result.Status == ExtractionStatus.Skipped) skipped++;
                else failed++;
                var row = rows.FirstOrDefault(r => r.Entry.Clip == result.Job.Clip && r.Entry.Occurrence == result.Job.Occurrence);
                if (row == null) continue;
                row.StatusLabel.text = result.Status == ExtractionStatus.Done ? (result.Replaced ? "Replaced" : "Saved")
                    : result.Status == ExtractionStatus.Skipped ? "Skipped" : "Error";
                row.Status.tooltip = result.Path ?? "";
                row.Status.EnableInClassList("ae-status--done", result.Status == ExtractionStatus.Done);
                row.Status.EnableInClassList("ae-status--error", result.Status == ExtractionStatus.Failed);
                row.Status.style.display = DisplayStyle.Flex;
                // A numbered name is worth saying; the plain one is what the field shows.
                string saved = result.Path == null ? null : System.IO.Path.GetFileNameWithoutExtension(result.Path);
                string message = result.Status == ExtractionStatus.Done ? (saved != result.Job.Name ? $"Saved as {saved}.anim" : null) : result.Message;
                row.Message.text = message ?? "";
                row.Message.EnableInClassList("ae-clip__message--error", result.Status == ExtractionStatus.Failed);
                row.Message.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            }
            var parts = new List<string> { $"Saved {done} in {folder}" };
            if (skipped > 0) parts.Add($"{skipped} skipped");
            if (failed > 0) parts.Add(failed == 1 ? "1 error" : $"{failed} errors");
            folderLabel.text = string.Join(" · ", parts);
            SyncFolderLink();
        }

        private void AddRow(ClipEntry entry, AnimationClipInfo clip)
        {
            var row = new Row { Entry = entry };
            var element = new VisualElement(); element.AddToClassList("ae-clip"); list.Add(element);
            element.EnableInClassList("ae-clip--first", rows.Count == 0);
            row.Check = new CheckBox(entry.Selected, on =>
            {
                entry.Selected = on;
                SyncCount();
                changed();
            });
            row.Check.AddToClassList("ae-clip__check");
            element.Add(row.Check);

            var body = new VisualElement(); body.AddToClassList("ae-clip__body"); element.Add(body);
            row.Name = new TextField { value = entry.Name, isDelayed = true, tooltip = "Name of the .anim file" };
            row.Name.AddToClassList("ae-clip__name");
            row.Name.RegisterValueChangedCallback(evt =>
            {
                // Shown as it will be saved: characters a file name cannot hold become "_".
                entry.Name = AssetPaths.FileName(evt.newValue, AssetPaths.FileName(clip.Name, "Animation"));
                row.Name.SetValueWithoutNotify(entry.Name);
            });
            body.Add(row.Name);

            var meta = new VisualElement(); meta.AddToClassList("ae-clip__meta"); body.Add(meta);
            var timing = Text($"{clip.Name} · {Timing(clip)}", "ae-clip__timing");
            timing.tooltip = $"Clip “{clip.Name}” in {info.Name}";
            meta.Add(timing);
            meta.Add(Chip(clip.Humanoid ? "Humanoid" : clip.Legacy ? "Legacy" : "Generic", clip.Humanoid));
            if (clip.Loop) meta.Add(Chip("Loop", false));
            if (clip.Events > 0) meta.Add(Chip(clip.Events == 1 ? "1 event" : $"{clip.Events} events", false));
            row.Message = Text("", "ae-clip__message");
            row.Message.style.display = DisplayStyle.None;
            body.Add(row.Message);

            row.Status = new VisualElement(); row.Status.AddToClassList("ae-status"); row.Status.style.display = DisplayStyle.None; element.Add(row.Status);
            var dot = new VisualElement(); dot.AddToClassList("ae-status__dot"); row.Status.Add(dot);
            row.StatusLabel = Text("", "ae-status__label"); row.Status.Add(row.StatusLabel);
            rows.Add(row);
        }

        private void SelectAll(bool on)
        {
            foreach (var row in rows)
            {
                row.Entry.Selected = on;
                row.Check.SetValueWithoutNotify(on);
            }
            SyncCount();
            changed();
        }

        private void SyncCount()
        {
            count.text = $"{SelectedCount} of {rows.Count} selected";
        }

        private void SyncFolderLink() =>
            showFolder.style.display = !string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder) ? DisplayStyle.Flex : DisplayStyle.None;

        // Highlights the folder in the Project window; the selection stays the user's.
        private void Ping()
        {
            var asset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder);
            if (asset != null) EditorGUIUtility.PingObject(asset);
        }

        private static string RigName(ModelImporterAnimationType? rig)
        {
            switch (rig)
            {
                case ModelImporterAnimationType.Human: return "Humanoid";
                case ModelImporterAnimationType.Generic: return "Generic";
                case ModelImporterAnimationType.Legacy: return "Legacy";
                case ModelImporterAnimationType.None: return "No rig";
                default: return "";
            }
        }

        private static string Timing(AnimationClipInfo clip)
        {
            string frames = clip.Frames == 0 ? "single pose" : clip.Frames == 1 ? "1 frame" : $"{clip.Frames} frames";
            return $"{clip.Length:0.00} s · {frames} · {clip.FrameRate:0.##} fps";
        }

        private static Label Chip(string text, bool accent)
        {
            var chip = Text(text, "ae-chip");
            chip.EnableInClassList("ae-chip--accent", accent);
            return chip;
        }
    }
}
