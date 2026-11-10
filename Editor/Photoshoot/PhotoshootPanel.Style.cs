using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    public sealed partial class PhotoshootPanel
    {
        // ---- Style: pose, light, background, expression ----------------------------------------------------

        private void BuildStyle()
        {
            var card = Card("ps-style");
            state.StyleTab = Mathf.Clamp(state.StyleTab, 0, Tabs.Length - 1);
            styleTabs = new SegmentedControl(Tabs, SelectTab);
            card.Add(styleTabs);

            styleCaption = new Label();
            styleCaption.AddToClassList("ps-caption");
            styleCaption.AddToClassList("ps-style__caption");
            card.Add(styleCaption);
            styleContent = new VisualElement();
            styleContent.AddToClassList("ps-style__content");
            card.Add(styleContent);
            BuildStyleContent();
        }

        private void SelectTab(int tab)
        {
            if (state.StyleTab == tab) return;
            state.StyleTab = tab;
            BuildStyleContent();
            // Crossfade the new options in: start transparent and slightly lowered, then let the transition settle it.
            styleContent.AddToClassList("ps-style__content--entering");
            styleContent.schedule.Execute(() => styleContent.RemoveFromClassList("ps-style__content--entering")).StartingIn(16);
        }


        private void BuildStyleContent()
        {
            styleTabs.SetIndex(state.StyleTab);
            styleContent.Clear();
            swatches.Clear();
            expressionChips.Clear();
            colorPicker = null;
            var grid = new VisualElement();
            grid.AddToClassList("ps-grid");
            styleContent.Add(grid);
            switch (state.StyleTab)
            {
                case PoseTab:
                    for (int i = 0; i < state.Catalog.bodyPoses.Count; i++)
                    {
                        var option = state.Catalog.bodyPoses[i];
                        var swatch = Swatch("ps-swatch--pose", i, option.displayName, new Image { image = state.GetPoseIcon(AvatarRoot, option), scaleMode = ScaleMode.ScaleToFit });
                        string path = option.assetPath;
                        grid.Add(option.custom ? WithRemoveButton(swatch, "Remove this pose (the animation it was taken from stays).", () =>
                        {
                            PhotoshootPoses.Remove(path);
                            state.ReloadPoses();
                        }) : swatch);
                    }
                    grid.Add(AddSwatch("ps-swatch--pose", "Add a pose of your own from a humanoid animation in this project (an .anim, or a model's clips), or drop animations here. Every project gets it.", BrowsePose));
                    AcceptDrops(grid, PosesDragged, () => AddPoses(DraggedClips()));
                    break;
                case LightTab:
                    for (int i = 0; i < state.Catalog.lightPresets.Count; i++)
                    {
                        var option = state.Catalog.lightPresets[i];
                        grid.Add(Swatch("ps-swatch--light", i, option.displayName, new Image { image = state.GetLightIcon(option), scaleMode = ScaleMode.ScaleToFit }));
                    }
                    styleContent.Add(SliderRow("Environment light", "How much the surrounding light brightens the avatar, over the preset's own (100%).",
                        0f, PhotoshootState.MaxAmbientIntensity, state.AmbientIntensity, false, value => Mathf.RoundToInt(value * 100f) + "%",
                        value => { state.AmbientIntensity = value; RenderKeepingFraming(); }, "ps-effect--on ps-light__ambient"));
                    break;
                case BackgroundTab when state.RefSheetOpen:
                    colorSwatch = null;
                    colorPicker = new InlineColorPicker(state.RefSheetBackground, PhotoshootRefSheet.DefaultBackground, color =>
                    {
                        state.RefSheetBackground = color;
                        RenderRefSheet(false);
                    });
                    styleContent.Add(colorPicker);
                    break;
                case BackgroundTab:
                    colorSwatch = null;
                    for (int i = 0; i < state.Catalog.backgrounds.Count; i++)
                    {
                        var option = state.Catalog.backgrounds[i];
                        VisualElement content;
                        if (option.solidColor)
                        {
                            content = colorSwatch = new VisualElement();
                            colorSwatch.style.backgroundColor = state.BackgroundColor;
                        }
                        else if (option.texture != null) content = new Image { image = option.texture, scaleMode = ScaleMode.ScaleAndCrop };
                        else { content = new VisualElement(); content.AddToClassList("ps-swatch__empty"); }
                        var swatch = Swatch("ps-swatch--background", i, option.displayName, content);
                        string path = option.assetPath;
                        grid.Add(option.custom ? WithRemoveButton(swatch, "Remove this background (the picture it was copied from stays).", () =>
                        {
                            PhotoshootBackgrounds.Remove(path);
                            state.ReloadBackgrounds();
                        }) : swatch);
                    }
                    grid.Add(AddSwatch("ps-swatch--background", "Add a picture of your own (PNG or JPEG), or drop pictures here. Every project gets it.", BrowseBackground));
                    AcceptDrops(grid, () => DraggedPictures().Count > 0, () => AddBackgrounds(DraggedPictures()));
                    colorPicker = new InlineColorPicker(state.BackgroundColor, PhotoshootService.DefaultBackgroundColor, color =>
                    {
                        state.BackgroundColor = color;
                        if (colorSwatch != null) colorSwatch.style.backgroundColor = color;
                        RenderNow();
                    });
                    styleContent.Add(colorPicker);
                    break;
                case TextTab:
                    BuildTextTab(grid);
                    break;
                case EffectsTab:
                    grid.AddToClassList("ps-grid--effects");
                    foreach (var effect in PhotoshootEffectSettings.All) grid.Add(EffectRow(effect));
                    break;
                default:
                    BuildExpressionChips(grid);
                    break;
            }
            UpdateSelectionVisuals();
        }

        // ---- Effects: each one on or off, with its strength ---------------------------------------------------------

        private VisualElement EffectRow(PhotoshootEffect effect)
        {
            var settings = state.Effects;
            bool comic = effect == PhotoshootEffect.Halftone;
            var group = new VisualElement();
            group.AddToClassList("ps-effect-group");
            var row = new VisualElement { tooltip = PhotoshootEffectSettings.Description(effect) };
            row.AddToClassList("ps-effect");
            group.Add(row);
            var value = new Label();
            value.AddToClassList("ps-effect__value");
            PhotoshootSlider slider = null;
            ToggleSwitch toggle = null;
            VisualElement dots = null;
            void Sync()
            {
                bool on = settings.IsOn(effect);
                row.EnableInClassList("ps-effect--on", on);
                slider.SetEnabled(on);
                dots?.SetEnabled(on);
                // The comic is all or nothing: its slider sets how many tones its flat colours have.
                value.text = comic ? settings.ComicColors + " colors" : Mathf.RoundToInt(settings.Amount(effect) * 100f) + "%";
            }
            void Switch(bool on)
            {
                settings.SetOn(effect, on);
                toggle.SetValueWithoutNotify(on);
                Sync();
                UpdateSelectionVisuals();
                RenderKeepingFraming();
            }

            toggle = new ToggleSwitch(settings.IsOn(effect), Switch);
            row.Add(toggle);
            var name = new Label(PhotoshootEffectSettings.Label(effect));
            name.AddToClassList("ps-effect__name");
            // The name switches it too, a bigger target than the switch.
            name.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Switch(!settings.IsOn(effect)); });
            row.Add(name);
            slider = comic
                ? new PhotoshootSlider(PhotoshootEffectSettings.MinComicColors, PhotoshootEffectSettings.MaxComicColors, settings.ComicColors, colors =>
                {
                    settings.ComicColors = Mathf.RoundToInt(colors);
                    Sync();
                    RenderKeepingFraming();
                }, whole: true)
                : new PhotoshootSlider(PhotoshootEffectSettings.Min(effect), PhotoshootEffectSettings.Max(effect), settings.Amount(effect), amount =>
                {
                    settings.SetAmount(effect, amount);
                    Sync();
                    RenderKeepingFraming();
                });
            slider.AddToClassList("ps-effect__slider");
            slider.tooltip = comic ? "How many tones the flat colours have." : effect == PhotoshootEffect.Vignette
                ? "Above zero the edges darken, below zero they brighten." : null;
            row.Add(slider);
            row.Add(value);
            if (comic)
            {
                // The print screen: ink dots growing with the darkness, their size chosen here.
                dots = SliderRow("Dots", "A print screen of ink dots over the comic, larger in the shade until they merge.",
                    0f, 1f, settings.ComicDotSize, false, size => Mathf.RoundToInt(settings.ComicDotCell(1080f)) + " px",
                    size => { settings.ComicDotSize = size; RenderKeepingFraming(); }, "ps-effect--sub", settings.ComicDots,
                    on => { settings.ComicDots = on; RenderKeepingFraming(); });
                group.Add(dots);
            }
            Sync();
            return group;
        }

        // A labelled slider row, with its own switch when <paramref name="switched"/> is given.
        private VisualElement SliderRow(string label, string tooltip, float min, float max, float value, bool whole, Func<float, string> format,
            Action<float> changed, string classes, bool on = true, Action<bool> switched = null)
        {
            var row = new VisualElement { tooltip = tooltip };
            row.AddToClassList("ps-effect");
            foreach (string className in classes.Split(' ')) row.AddToClassList(className);
            var text = new Label();
            text.AddToClassList("ps-effect__value");
            PhotoshootSlider slider = null;
            ToggleSwitch toggle = null;
            void Sync(bool enabled)
            {
                row.EnableInClassList("ps-effect--on", enabled);
                slider.SetEnabled(enabled);
                text.text = format(slider.Value);
            }
            void Switch(bool enabled)
            {
                toggle.SetValueWithoutNotify(enabled);
                Sync(enabled);
                switched(enabled);
            }
            if (switched != null)
            {
                toggle = new ToggleSwitch(on, Switch);
                row.Add(toggle);
            }
            var name = new Label(label);
            name.AddToClassList("ps-effect__name");
            if (switched != null) name.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Switch(!toggle.Value); });
            row.Add(name);
            slider = new PhotoshootSlider(min, max, value, newValue =>
            {
                text.text = format(newValue);
                changed(newValue);
            }, whole);
            slider.AddToClassList("ps-effect__slider");
            row.Add(slider);
            row.Add(text);
            Sync(on);
            return row;
        }

        // ---- The user's own backgrounds and poses ------------------------------------------------------------------------

        private Button AddSwatch(string modifier, string tooltip, Action browse)
        {
            var add = CreateButton(null, browse, "ps-swatch");
            add.AddToClassList(modifier);
            add.AddToClassList("ps-swatch--add");
            add.tooltip = tooltip;
            var icon = new VectorIcon(IconGlyph.Plus) { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("ps-swatch__add-icon");
            add.Add(icon);
            return add;
        }

        // Adds the user's own items, then shows them with the last one added chosen. What was added before a failure stays.
        private void AddOwn<T>(IEnumerable<T> items, Func<T, string> add, string what, Action<string> reload)
        {
            string last = null;
            try
            {
                foreach (var item in items) last = add(item);
                state.Error = null;
            }
            catch (Exception ex)
            {
                state.Error = $"Could not add the {what}: {ex.Message}";
            }

            if (last != null)
            {
                reload(last);
                BuildStyleContent();
                RenderNow();
            }
            UpdateMessage();
        }

        // Beside the swatch, not in it: a press on the swatch would choose it first.
        private VisualElement WithRemoveButton(Button swatch, string tooltip, Action remove)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("ps-swatch-wrap");
            wrap.Add(swatch);
            var button = CreateButton(null, () =>
            {
                remove();
                BuildStyleContent();
                RenderNow();
            }, "ps-swatch__remove");
            button.tooltip = tooltip;
            var icon = new VectorIcon(IconGlyph.Close) { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("ps-swatch__remove-icon");
            button.Add(icon);
            wrap.Add(button);
            return wrap;
        }

        // What is dropped on the grid, from the Project window or from the desktop, is added to it.
        private static void AcceptDrops(VisualElement grid, Func<bool> accepts, Action drop)
        {
            grid.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                bool accepted = accepts();
                DragAndDrop.visualMode = accepted ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                grid.EnableInClassList("ps-grid--drop", accepted);
            });
            grid.RegisterCallback<DragLeaveEvent>(_ => grid.RemoveFromClassList("ps-grid--drop"));
            grid.RegisterCallback<DragExitedEvent>(_ => grid.RemoveFromClassList("ps-grid--drop"));
            grid.RegisterCallback<DragPerformEvent>(_ =>
            {
                grid.RemoveFromClassList("ps-grid--drop");
                if (!accepts()) return;
                DragAndDrop.AcceptDrag();
                drop();
            });
        }

        private void BrowseBackground()
        {
            string file = EditorUtility.OpenFilePanelWithFilters("Add a background", "", new[] { "Pictures", "png,jpg,jpeg" });
            if (!string.IsNullOrEmpty(file)) AddBackgrounds(new[] { file });
        }

        private static List<string> DraggedPictures() => (DragAndDrop.paths ?? new string[0]).Where(PhotoshootBackgrounds.IsPicture).ToList();

        private void AddBackgrounds(IEnumerable<string> files) =>
            AddOwn(files.Where(PhotoshootBackgrounds.IsPicture), file => PhotoshootBackgrounds.Add(Path.GetFullPath(file)), "background", state.ReloadBackgrounds);

        private void BrowsePose()
        {
            string file = EditorUtility.OpenFilePanelWithFilters("Add a pose", Application.dataPath, new[] { "Animations", "anim,fbx" });
            if (!string.IsNullOrEmpty(file)) AddPoses(new[] { file }.SelectMany(PhotoshootPoses.ClipsIn));
        }

        // Read while adding, so a file that can't give a pose (outside the project, no animation) is reported.
        private void AddPoses(IEnumerable<AnimationClip> clips) => AddOwn(clips, PhotoshootPoses.Add, "pose", state.ReloadPoses);

        private static bool PosesDragged() =>
            (DragAndDrop.objectReferences ?? new UnityEngine.Object[0]).OfType<AnimationClip>().Any() ||
            (DragAndDrop.paths ?? new string[0]).Any(PhotoshootPoses.IsAnimationFile);

        // A clip dragged from the Project window (one of a model's) is that clip; other animation and model files bring
        // every clip in them.
        private static IEnumerable<AnimationClip> DraggedClips()
        {
            var clips = (DragAndDrop.objectReferences ?? new UnityEngine.Object[0]).OfType<AnimationClip>().ToList();
            var clipFiles = new HashSet<string>(clips.Select(AssetDatabase.GetAssetPath), StringComparer.OrdinalIgnoreCase);
            var files = (DragAndDrop.paths ?? new string[0]).Where(file => PhotoshootPoses.IsAnimationFile(file) && !clipFiles.Contains(file)).ToList();
            return clips.Concat(files.SelectMany(PhotoshootPoses.ClipsIn));
        }

        private int SelectedIndex(int tab) => tab == PoseTab ? state.BodyPoseIndex : tab == LightTab ? state.LightPresetIndex : state.BackgroundIndex;

        private void SetSelectedIndex(int tab, int index)
        {
            if (tab == PoseTab) state.BodyPoseIndex = index;
            else if (tab == LightTab) state.LightPresetIndex = index;
            else state.BackgroundIndex = index;
        }

        private Button Swatch(string modifier, int index, string tooltip, VisualElement content)
        {
            int tab = state.StyleTab;
            var button = CreateButton(null, () =>
            {
                if (SelectedIndex(tab) == index) return;
                SetSelectedIndex(tab, index);
                // The ring moves on press; the render follows on the next editor tick so the selection is never held up.
                UpdateSelectionVisuals();
                ScheduleRefresh(false);
            }, "ps-swatch");
            button.AddToClassList(modifier);
            button.tooltip = WithoutSurrogates(tooltip);
            content.AddToClassList("ps-swatch__content");
            content.pickingMode = PickingMode.Ignore;
            button.Add(content);
            swatches.Add(button);
            return button;
        }

        private void BuildExpressionChips(VisualElement grid)
        {
            grid.AddToClassList("ps-grid--chips");
            if (state.Catalog.faceBlendshapes.Count == 0)
            {
                var none = new Label("No matching face blendshapes found on this avatar.");
                none.AddToClassList("ps-caption");
                grid.Add(none);
                return;
            }

            foreach (var option in state.Catalog.faceBlendshapes)
            {
                string blendshapeName = option.name;
                var chip = CreateButton(WithoutSurrogates(option.rendererCount > 1 ? $"{blendshapeName} ({option.rendererCount})" : blendshapeName), () =>
                {
                    if (!state.SelectedFaceBlendshapes.Add(blendshapeName)) state.SelectedFaceBlendshapes.Remove(blendshapeName);
                    UpdateSelectionVisuals();
                    ScheduleRefresh(true);
                }, "ps-chip");
                expressionChips[blendshapeName] = chip;
                grid.Add(chip);
            }
        }

        private void UpdateSelectionVisuals()
        {
            int tab = state.StyleTab;
            if (tab == ExpressionTab)
            {
                foreach (var pair in expressionChips) pair.Value.EnableInClassList("ps-chip--selected", state.SelectedFaceBlendshapes.Contains(pair.Key));
                int count = state.SelectedFaceBlendshapes.Count;
                styleCaption.text = count == 0 ? "Neutral face" : count + " blendshape" + (count == 1 ? "" : "s") + " on";
                return;
            }

            if (tab == TextTab)
            {
                UpdateTextControls();
                return;
            }

            if (tab == EffectsTab)
            {
                int on = state.Effects.Count;
                styleCaption.text = on == 0 ? "No effects: the shot as rendered. Turn on as many as you like." : on + " effect" + (on == 1 ? "" : "s") + " on";
                return;
            }

            if (tab == BackgroundTab && state.RefSheetOpen)
            {
                styleCaption.text = "One plain colour behind every view";
                if (colorPicker != null) colorPicker.style.display = DisplayStyle.Flex;
                return;
            }

            int selected = SelectedIndex(tab);
            for (int i = 0; i < swatches.Count; i++) swatches[i].EnableInClassList("ps-swatch--selected", i == selected);
            styleCaption.text = selected >= 0 && selected < swatches.Count ? swatches[selected].tooltip : "";
            if (colorPicker != null)
            {
                bool solid = tab == BackgroundTab && selected >= 0 && selected < state.Catalog.backgrounds.Count && state.Catalog.backgrounds[selected].solidColor;
                colorPicker.style.display = solid ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
