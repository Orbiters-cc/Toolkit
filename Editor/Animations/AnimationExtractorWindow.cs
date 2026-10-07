using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// Saves the animations inside models as standalone .anim clips: drop models from the Project window or the computer (files
    /// from outside the project are copied into it first), choose clips and names, extract. <see cref="AnimationExtraction"/>
    /// does the work.
    /// </summary>
    public sealed class AnimationExtractorWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Animations/animation-extractor.uss";
        private const string Prefs = "Orbiters.Toolkit.AnimationExtractor.";
        private const string DefaultImportFolder = "Assets/Animations/Imported";
        // Each slice of an extraction saves clips for about this long, then the progress bar moves.
        private const double SliceSeconds = 0.12;
        private const int ListedProblems = 3;

        [Serializable]
        internal sealed class ClipEntry
        {
            public string Clip;
            public int Occurrence;
            public string Name;
            public bool Selected = true;
        }

        [Serializable]
        internal sealed class ModelEntry
        {
            public string Guid;
            public List<ClipEntry> Clips = new List<ClipEntry>();
        }

        // Kept across script reloads with the window; the clips themselves are read again from the models.
        [SerializeField] private List<ModelEntry> models = new List<ModelEntry>();
        // Empty: each model's clips go next to it (AnimationExtraction.DefaultFolder).
        [SerializeField] private string outputFolder;

        private readonly List<AnimationModelCard> cards = new List<AnimationModelCard>();
        private AnimationDropZone dropZone;
        private VisualElement cardList, options, footer, progress, progressFill;
        private Label summary, progressLabel, saveTo, existingCaption, importTo;
        private Button extract, resetFolder;
        private ExtractionRun run;
        private Dictionary<ExtractionJob, AnimationModelCard> runCards;
        private readonly HashSet<string> changedDuringRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [MenuItem("Tools/Orbiters/Animation Extractor")]
        public static void Open()
        {
            var window = GetWindow<AnimationExtractorWindow>();
            window.titleContent = new GUIContent("Animation Extractor");
            window.minSize = new Vector2(440f, 480f);
            window.Show();
        }

        private static LoopOverride Loop
        {
            get { int value = EditorPrefs.GetInt(Prefs + "Loop", 0); return Enum.IsDefined(typeof(LoopOverride), value) ? (LoopOverride)value : LoopOverride.Keep; }
            set => EditorPrefs.SetInt(Prefs + "Loop", (int)value);
        }

        private static ExistingAnimation Existing
        {
            get { int value = EditorPrefs.GetInt(Prefs + "Existing", 0); return Enum.IsDefined(typeof(ExistingAnimation), value) ? (ExistingAnimation)value : ExistingAnimation.Replace; }
            set => EditorPrefs.SetInt(Prefs + "Existing", (int)value);
        }

        /// <summary>Where files dropped from outside the project are copied.</summary>
        private static string ImportFolder
        {
            get { string folder = EditorPrefs.GetString(Prefs + "ImportFolder", DefaultImportFolder); return AssetPaths.IsProjectPath(folder) ? folder : DefaultImportFolder; }
            set => EditorPrefs.SetString(Prefs + "ImportFolder", value);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) root.styleSheets.Add(sheet);
            root.AddToClassList("ae");

            var top = new VisualElement(); top.AddToClassList("ae__top"); root.Add(top);
            Button browse = null;
            browse = Pill("Browse…", () => Later(browse, Browse), "ae-pill");
            browse.tooltip = "Choose a model file on your computer or in the project.";
            dropZone = new AnimationDropZone(browse);
            top.Add(dropZone);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("ae__scroll");
            root.Add(scroll);
            // Padding on an inner element: Unity's own ScrollView content-container rules override padding set on it.
            var content = new VisualElement(); content.AddToClassList("ae__content"); scroll.Add(content);
            cardList = new VisualElement(); content.Add(cardList);
            options = BuildOptions(); content.Add(options);
            footer = BuildFooter(); root.Add(footer);
            RegisterDrops(root);

            cards.Clear();
            foreach (var entry in models.ToList())
            {
                string path = AssetDatabase.GUIDToAssetPath(entry.Guid);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.GetMainAssetTypeAtPath(path) != null) AddCard(entry, AnimationExtraction.Read(path));
                else models.Remove(entry);
            }
            SyncOptions();
            Sync();
        }

        // ---- Layout ---------------------------------------------------------------------------------------------

        private VisualElement BuildOptions()
        {
            var card = new VisualElement(); card.AddToClassList("ae-card");
            card.Add(Text("Options", "ae-card__title"));

            var save = Option(card, "Save to");
            saveTo = Text("", "ae-option__value"); save.Add(saveTo);
            resetFolder = Pill("Next to each model", () => SetOutputFolder(null), "ae-link");
            resetFolder.tooltip = "Save each model's clips in a folder next to it again.";
            save.Add(resetFolder);
            Button chooseOutput = null;
            chooseOutput = Pill("Choose…", () => Later(chooseOutput, () =>
            {
                string folder = PickFolder("Save the animations in", string.IsNullOrEmpty(outputFolder) ? "Assets" : outputFolder);
                if (folder != null) SetOutputFolder(folder);
            }), "ae-pill", "ae-pill--small");
            save.Add(chooseOutput);

            var loop = new SegmentedControl(new[] { "As in the model", "Loop", "Don't loop" }, index => Loop = (LoopOverride)index);
            loop.SetIndex((int)Loop);
            Option(card, "Loop time", loop);

            var existing = new SegmentedControl(new[] { "Replace it", "Keep both", "Skip" }, index =>
            {
                Existing = (ExistingAnimation)index;
                SyncOptions();
            });
            existing.SetIndex((int)Existing);
            var existingRow = Option(card, "When the .anim already exists", existing);
            existingCaption = Text("", "ae-caption", "ae-option__caption");
            existingRow.parent.Add(existingCaption);

            var import = Option(card, "Files from your computer are copied to");
            importTo = Text("", "ae-option__value"); import.Add(importTo);
            Button chooseImport = null;
            chooseImport = Pill("Choose…", () => Later(chooseImport, () =>
            {
                string folder = PickFolder("Copy dropped files to", ImportFolder);
                if (folder == null) return;
                ImportFolder = folder;
                SyncOptions();
            }), "ae-pill", "ae-pill--small");
            import.Add(chooseImport);
            return card;
        }

        // A labelled option; returns the row its controls go in.
        private static VisualElement Option(VisualElement card, string label, VisualElement control = null)
        {
            var option = new VisualElement(); option.AddToClassList("ae-option");
            // USS has no sibling selector: the option right under the title drops its top margin here.
            option.EnableInClassList("ae-option--first", card.childCount == 1);
            card.Add(option);
            option.Add(Text(label, "ae-option__label"));
            var row = new VisualElement(); row.AddToClassList("ae-option__row"); option.Add(row);
            if (control != null)
            {
                control.AddToClassList("ae-option__control");
                row.Add(control);
            }
            return row;
        }

        private VisualElement BuildFooter()
        {
            var bar = new VisualElement(); bar.AddToClassList("ae__footer");
            summary = Text("", "ae-summary"); bar.Add(summary);
            progress = new VisualElement(); progress.AddToClassList("ae-progress"); bar.Add(progress);
            progressLabel = Text("", "ae-progress__label"); progress.Add(progressLabel);
            var track = new VisualElement(); track.AddToClassList("ae-progress__track"); progress.Add(track);
            progressFill = new VisualElement(); progressFill.AddToClassList("ae-progress__fill"); track.Add(progressFill);
            extract = Pill("", StartExtraction, "ae-pill", "ae-pill--accent", "ae-extract");
            bar.Add(extract);
            return bar;
        }

        private void Sync()
        {
            if (extract == null) return;
            bool busy = run != null;
            int count = cards.Sum(c => c.SelectedCount);
            dropZone.SetCompact(cards.Count > 0);
            dropZone.SetEnabled(!busy);
            cardList.SetEnabled(!busy);
            options.SetEnabled(!busy);
            footer.style.display = cards.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            summary.style.display = string.IsNullOrEmpty(summary.text) ? DisplayStyle.None : DisplayStyle.Flex;
            progress.style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            extract.style.display = busy ? DisplayStyle.None : DisplayStyle.Flex;
            extract.text = count == 0 ? "Select animations to extract" : count == 1 ? "Extract 1 animation" : $"Extract {count} animations";
            extract.SetEnabled(!busy && count > 0);
        }

        private void SyncOptions()
        {
            if (saveTo == null) return;
            bool custom = !string.IsNullOrEmpty(outputFolder);
            saveTo.text = custom ? outputFolder : "Next to each model, in a “<model> Animations” folder";
            saveTo.tooltip = custom ? outputFolder : "";
            resetFolder.style.display = custom ? DisplayStyle.Flex : DisplayStyle.None;
            existingCaption.text = Existing == ExistingAnimation.Replace ? "The .anim is updated in place: controllers and everything else using it keep working."
                : Existing == ExistingAnimation.KeepBoth ? "The new clip gets a numbered name, such as “Run 1”."
                : "The existing .anim is left as it is.";
            importTo.text = ImportFolder;
            importTo.tooltip = ImportFolder;
        }

        // ---- Models -------------------------------------------------------------------------------------------

        private string FolderFor(string path) => string.IsNullOrEmpty(outputFolder) ? AnimationExtraction.DefaultFolder(path) : outputFolder;

        private void AddCard(ModelEntry entry, AnimationSourceInfo info)
        {
            var card = new AnimationModelCard(entry, Sync, () => Remove(entry));
            card.Bind(info, FolderFor(info.Path));
            cards.Add(card);
            cardList.Add(card);
        }

        private void Remove(ModelEntry entry)
        {
            if (run != null) return;
            models.Remove(entry);
            var card = cards.FirstOrDefault(c => c.Entry == entry);
            if (card != null)
            {
                cards.Remove(card);
                card.RemoveFromHierarchy();
            }
            Sync();
        }

        private void SetOutputFolder(string folder)
        {
            outputFolder = folder;
            foreach (var card in cards) card.SetFolder(FolderFor(card.Path));
            SyncOptions();
        }

        /// <summary>Called after imports: rebuilds the cards of reimported or moved models and drops those deleted.</summary>
        internal void SourcesChanged(ICollection<string> changed)
        {
            if (cardList == null) return;
            var stale = cards.Where(card =>
            {
                string path = AssetDatabase.GUIDToAssetPath(card.Entry.Guid);
                return string.IsNullOrEmpty(path) || path != card.Path || changed.Contains(path);
            }).ToList();
            if (stale.Count == 0) return;
            // A running extraction's results point at the current rows (and its own clips import between slices): models
            // that changed meanwhile are read again once it is done.
            if (run != null)
            {
                changedDuringRun.UnionWith(changed);
                return;
            }
            foreach (var card in stale)
            {
                string path = AssetDatabase.GUIDToAssetPath(card.Entry.Guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == null) Remove(card.Entry);
                else card.Bind(AnimationExtraction.Read(path), FolderFor(path));
            }
            Sync();
        }

        // ---- Dropping and browsing ------------------------------------------------------------------------------

        private void RegisterDrops(VisualElement root)
        {
            // Anywhere in the window, before the fields inside it see the drag.
            root.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (DraggedPaths().Count == 0) return;
                DragAndDrop.visualMode = run == null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                dropZone.SetOver(run == null);
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<DragPerformEvent>(evt =>
            {
                var paths = DraggedPaths();
                if (paths.Count == 0 || run != null) return;
                DragAndDrop.AcceptDrag();
                dropZone.SetOver(false);
                evt.StopPropagation();
                Add(paths);
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<DragExitedEvent>(_ => dropZone.SetOver(false), TrickleDown.TrickleDown);
            root.RegisterCallback<DragLeaveEvent>(evt => { if (evt.target == root) dropZone.SetOver(false); }, TrickleDown.TrickleDown);
        }

        // Project assets come as asset paths (a model's clip as its model), files from the computer as full paths.
        private static List<string> DraggedPaths() =>
            (DragAndDrop.paths ?? Array.Empty<string>())
            .Concat((DragAndDrop.objectReferences ?? Array.Empty<Object>()).Where(o => o != null).Select(AssetDatabase.GetAssetPath))
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private void Browse()
        {
            string start = EditorPrefs.GetString(Prefs + "BrowseFolder", "");
            if (!Directory.Exists(start)) start = Application.dataPath;
            string extensions = string.Join(",", AnimationExtraction.ModelExtensions.Select(e => e.TrimStart('.')));
            string path = EditorUtility.OpenFilePanelWithFilters("Choose a model with animations", start, new[] { "Models", extensions, "All files", "*" });
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString(Prefs + "BrowseFolder", Path.GetDirectoryName(path));
            Add(new List<string> { path });
        }

        private void Add(List<string> paths)
        {
            var project = new List<string>();
            var external = new List<string>();
            var problems = new List<string>();
            // Models found in a dropped folder that carry no clip are passed over without a word.
            var quiet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dropped in paths)
            {
                string asset = AssetPaths.IsProjectPath(dropped) ? AssetPaths.Normalize(dropped) : AssetPaths.FromFullPath(dropped);
                if (asset != null && AssetDatabase.IsValidFolder(asset))
                {
                    var found = AssetDatabase.FindAssets("t:Model", new[] { asset }).Select(AssetDatabase.GUIDToAssetPath).ToList();
                    quiet.UnionWith(found);
                    project.AddRange(found);
                }
                else if (asset != null) project.Add(asset);
                else if (Directory.Exists(dropped)) external.AddRange(ExternalModels(dropped));
                else if (AnimationExtraction.IsModelFile(dropped)) external.Add(dropped);
                else problems.Add($"{Path.GetFileName(dropped)} is not a model file.");
            }
            if (external.Count == 0)
            {
                AddSources(project, quiet, problems, null);
                return;
            }
            // Copying and importing hold the editor up: say so before they start.
            dropZone.SetMessage(external.Count == 1 ? "Copying 1 file into the project…" : $"Copying {external.Count} files into the project…", false);
            string folder = ImportFolder;
            rootVisualElement.schedule.Execute(() =>
            {
                var imported = AnimationExtraction.ImportExternal(external, folder, problems);
                project.AddRange(imported);
                AddSources(project, quiet, problems, imported.Count == 0 ? null : imported.Count == 1 ? $"Copied 1 file to {folder}." : $"Copied {imported.Count} files to {folder}.");
            }).StartingIn(30);
        }

        private void AddSources(List<string> paths, HashSet<string> quiet, List<string> problems, string note)
        {
            int added = 0, listed = 0;
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                if (models.Any(m => m.Guid == guid)) { listed++; continue; }
                var info = AnimationExtraction.Read(path);
                string name = Path.GetFileName(path);
                if (info.IsClipFile || info.Clips.Count == 0)
                {
                    if (!quiet.Contains(path))
                        problems.Add(info.IsClipFile ? $"{name} is already an animation clip." : info.Rig.HasValue ? $"{name} has no animations." : $"{name} is not a model.");
                    continue;
                }
                var entry = new ModelEntry { Guid = guid };
                models.Add(entry);
                AddCard(entry, info);
                added++;
            }
            var lines = new List<string>();
            if (note != null) lines.Add(note);
            lines.AddRange(problems.Take(ListedProblems));
            if (problems.Count > ListedProblems) lines.Add($"…and {problems.Count - ListedProblems} more.");
            if (added == 0 && problems.Count == 0) lines.Add(listed > 0 ? "Already in the list." : "No models with animations there.");
            dropZone.SetMessage(string.Join("\n", lines), problems.Count > 0 || added + listed == 0);
            Sync();
        }

        // The model files in a folder from the computer, subfolders included; folders that cannot be read are passed over.
        private static List<string> ExternalModels(string folder)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    found.AddRange(Directory.GetFiles(current).Where(AnimationExtraction.IsModelFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                    foreach (string child in Directory.GetDirectories(current)) pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return found;
        }

        // A project folder chosen on disk; null when cancelled or outside the project.
        private static string PickFolder(string title, string current)
        {
            string start = AssetDatabase.IsValidFolder(current) ? Path.GetFullPath(current) : Application.dataPath;
            string picked = EditorUtility.OpenFolderPanel(title, start, "");
            if (string.IsNullOrEmpty(picked)) return null;
            string folder = AssetPaths.FromFullPath(picked);
            if (folder != null) return folder;
            EditorUtility.DisplayDialog("Animation Extractor", "Choose a folder inside this project: under Assets, or in an embedded package.", "OK");
            return null;
        }

        // ---- Extraction ---------------------------------------------------------------------------------------

        private void StartExtraction()
        {
            if (run != null) return;
            var jobs = new List<ExtractionJob>();
            runCards = new Dictionary<ExtractionJob, AnimationModelCard>();
            foreach (var card in cards)
            {
                card.ClearResults();
                foreach (var job in card.Jobs())
                {
                    jobs.Add(job);
                    runCards[job] = card;
                }
            }
            if (jobs.Count == 0) return;
            run = new ExtractionRun(jobs, new ExtractionOptions { Loop = Loop, Existing = Existing });
            summary.text = "";
            ShowProgress();
            Sync();
            // The progress bar paints before the first slice holds the editor up.
            rootVisualElement.schedule.Execute(Step).StartingIn(30);
        }

        private void Step()
        {
            if (run == null) return;
            bool more;
            string failure = null;
            try { more = run.Step(SliceSeconds); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                failure = ex.Message;
                more = false;
            }
            ShowProgress();
            if (more) rootVisualElement.schedule.Execute(Step).StartingIn(1);
            else Finish(failure);
        }

        private void ShowProgress()
        {
            progressFill.style.width = Length.Percent(run.Count == 0 ? 100f : 100f * run.Completed / run.Count);
            var next = run.Next;
            progressLabel.text = next == null ? "Saving…" : $"Extracting {run.Completed + 1} of {run.Count} · {next.Name}";
        }

        private void Finish(string failure)
        {
            var finished = run;
            run = null;
            foreach (var group in finished.Results.GroupBy(r => runCards.TryGetValue(r.Job, out var card) ? card : null))
                group.Key?.ShowResults(group);
            runCards = null;
            int done = finished.Results.Count(r => r.Status == ExtractionStatus.Done);
            int skipped = finished.Results.Count(r => r.Status == ExtractionStatus.Skipped);
            int failed = finished.Results.Count(r => r.Status == ExtractionStatus.Failed);
            var parts = new List<string> { done == 1 ? "Extracted 1 animation" : $"Extracted {done} animations" };
            if (skipped > 0) parts.Add($"{skipped} skipped");
            if (failed > 0) parts.Add(failed == 1 ? "1 error" : $"{failed} errors");
            if (failure != null) parts.Add("stopped: " + failure);
            summary.text = string.Join(" · ", parts);
            summary.EnableInClassList("ae-summary--error", failed > 0 || failure != null);
            Sync();
            if (changedDuringRun.Count == 0) return;
            var changed = new HashSet<string>(changedDuringRun, StringComparer.OrdinalIgnoreCase);
            changedDuringRun.Clear();
            SourcesChanged(changed);
        }

        // ---- Small UI helpers ---------------------------------------------------------------------------------

        internal static Label Text(string text, params string[] classNames)
        {
            // Clip and file names are shown as they are, never as markup.
            var label = new Label(text) { enableRichText = false };
            foreach (string name in classNames) label.AddToClassList(name);
            return label;
        }

        // Acts on press, with a press state for immediate feedback; Unity's click remains the fallback.
        internal static Button Pill(string text, Action onClick, params string[] classNames)
        {
            var button = new Button { text = text };
            foreach (string name in classNames) button.AddToClassList(name);
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("ae-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ae-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ae-pressed"));
            ButtonInteraction.RegisterImmediateClick(button, onClick);
            return button;
        }

        // File and folder pickers are modal: open them once the press has painted, without leaving the button pressed.
        private void Later(Button button, Action open) =>
            rootVisualElement.schedule.Execute(() =>
            {
                button.RemoveFromClassList("ae-pressed");
                open();
            }).StartingIn(16);
    }

    /// <summary>The extractor's drop target: a quiet field with a dashed outline that lights up while files are dragged over the window.</summary>
    internal sealed class AnimationDropZone : VisualElement
    {
        private static readonly Color Dash = new Color(0.36f, 0.36f, 0.36f), Accent = new Color(0f, 0.855f, 0.427f);
        private readonly Label message;
        private bool over;

        public AnimationDropZone(Button browse)
        {
            AddToClassList("ae-drop");
            var icon = new VectorIcon(IconGlyph.Download); icon.AddToClassList("ae-drop__icon"); Add(icon);
            var texts = new VisualElement(); texts.AddToClassList("ae-drop__texts"); Add(texts);
            texts.Add(AnimationExtractorWindow.Text("Drop FBX files here", "ae-drop__title"));
            texts.Add(AnimationExtractorWindow.Text("From the Project window or your computer · files or folders", "ae-drop__hint"));
            browse.AddToClassList("ae-drop__browse");
            Add(browse);
            message = AnimationExtractorWindow.Text("", "ae-drop__message");
            message.style.display = DisplayStyle.None;
            Add(message);
            generateVisualContent += DrawOutline;
        }

        public void SetOver(bool value)
        {
            if (over == value) return;
            over = value;
            EnableInClassList("ae-drop--over", value);
            MarkDirtyRepaint();
        }

        /// <summary>One row once models are listed, leaving the room to them.</summary>
        public void SetCompact(bool compact) => EnableInClassList("ae-drop--compact", compact);

        public void SetMessage(string text, bool warning)
        {
            message.text = text ?? "";
            message.EnableInClassList("ae-drop__message--warning", warning);
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // USS has no dashed borders: straight dashes along the edges, solid arcs in the corners, concentric with the field's own.
        private void DrawOutline(MeshGenerationContext context)
        {
            const float inset = 3f, dash = 6f, gap = 6f;
            var rect = new Rect(inset, inset, layout.width - 2f * inset, layout.height - 2f * inset);
            if (rect.width <= 0f || rect.height <= 0f) return;
            float radius = Mathf.Min(13f, rect.height / 2f, rect.width / 2f);
            var painter = context.painter2D;
            painter.strokeColor = over ? Accent : Dash;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            for (float x = rect.xMin + radius; x < rect.xMax - radius; x += dash + gap)
            {
                float end = Mathf.Min(x + dash, rect.xMax - radius);
                painter.MoveTo(new Vector2(x, rect.yMin)); painter.LineTo(new Vector2(end, rect.yMin));
                painter.MoveTo(new Vector2(x, rect.yMax)); painter.LineTo(new Vector2(end, rect.yMax));
            }
            for (float y = rect.yMin + radius; y < rect.yMax - radius; y += dash + gap)
            {
                float end = Mathf.Min(y + dash, rect.yMax - radius);
                painter.MoveTo(new Vector2(rect.xMin, y)); painter.LineTo(new Vector2(rect.xMin, end));
                painter.MoveTo(new Vector2(rect.xMax, y)); painter.LineTo(new Vector2(rect.xMax, end));
            }
            painter.Stroke();
            foreach (var (center, start) in new[]
            {
                (new Vector2(rect.xMin + radius, rect.yMin + radius), 180f), (new Vector2(rect.xMax - radius, rect.yMin + radius), 270f),
                (new Vector2(rect.xMax - radius, rect.yMax - radius), 0f), (new Vector2(rect.xMin + radius, rect.yMax - radius), 90f),
            })
            {
                painter.BeginPath();
                painter.Arc(center, radius, start, start + 90f);
                painter.Stroke();
            }
        }
    }

    // Keeps open extractor windows in step with their models: reimported (rig or clips changed), moved or deleted.
    internal sealed class AnimationExtractorRefresh : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!EditorWindow.HasOpenInstances<AnimationExtractorWindow>()) return;
            var changed = new HashSet<string>(imported.Concat(deleted).Concat(moved).Concat(movedFrom), StringComparer.OrdinalIgnoreCase);
            foreach (var window in Resources.FindObjectsOfTypeAll<AnimationExtractorWindow>()) window.SourcesChanged(changed);
        }
    }
}
