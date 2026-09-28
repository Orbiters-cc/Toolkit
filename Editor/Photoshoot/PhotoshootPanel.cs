using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>How a host tool embeds the photoshoot: which shots, their sizes, and where captured images go.</summary>
    public sealed class PhotoshootOptions
    {
        public Func<GameObject> AvatarRoot;
        public bool IncludeBanner = true;
        public Vector2Int ThumbnailSize = new Vector2Int(512, 512);
        public Vector2Int BannerSize = new Vector2Int(1600, 900);
        /// <summary>Whether a live preview or capture may run now (for example, not while the host uploads).</summary>
        public Func<bool> CanGenerate = () => true;
        /// <summary>Whether Set/Browse/Back are temporarily disabled by host work (saving, submitting).</summary>
        public Func<bool> InputBlocked = () => false;
        /// <summary>The image currently chosen for a shot, or null while the shot follows the live preview.</summary>
        public Func<PhotoshootService.ShotKind, Texture2D> GetShot;
        /// <summary>Receives a captured or browsed image (null on Retake). The host owns the texture from then on.</summary>
        public Action<PhotoshootService.ShotKind, Texture2D> SetShot;
        /// <summary>Called after an image chosen with Browse was passed to <see cref="SetShot"/>.</summary>
        public Action<PhotoshootService.ShotKind> Browsed;
        /// <summary>
        /// When set, the panel shows no preview of its own and sends the thumbnail it would show (chosen or live) here,
        /// so the host can present it in its own context, such as the card it will appear on.
        /// </summary>
        public Action<Texture> ThumbnailPreview;
        public Action Back;
        public string BackText = "Back to asset";
        /// <summary>Called after a shot changed, so the host can refresh its own controls.</summary>
        public Action Changed;
        public Action Repaint;
    }

    /// <summary>Live photoshoot editor: preview, shot capture, framing controls and pose, light, background and expression pickers.</summary>
    public sealed class PhotoshootPanel : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Photoshoot/photoshoot.uss";
        private static readonly Vector2Int BannerPreviewSize = new Vector2Int(768, 432);
        private static readonly string[] StyleTabs = { "Pose", "Light", "Background", "Expression" };
        private const int PoseTab = 0, LightTab = 1, BackgroundTab = 2, ExpressionTab = 3;

        private readonly PhotoshootState state;
        private readonly PhotoshootOptions options;
        private readonly List<Button> swatches = new List<Button>();
        private readonly Dictionary<string, Button> expressionChips = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<PhotoshootService.ShotKind, ShotRow> shotRows = new Dictionary<PhotoshootService.ShotKind, ShotRow>();
        private readonly Dictionary<VisualElement, FramingDrag> framingSurfaces = new Dictionary<VisualElement, FramingDrag>();
        private Image bannerImage, thumbnailImage;
        private Button backButton;
        private ScrubDial turnDial, zoomDial;
        private Label styleCaption, message;
        private SegmentedControl styleTabs, presets;
        private VisualElement styleContent, colorSwatch;
        private InlineColorPicker colorPicker;
        private IVisualElementScheduledItem framingTween;
        private int settleGeneration;

        private sealed class ShotRow
        {
            public VisualElement Dot;
            public Label Detail;
            public Button Primary, Browse;
        }

        public PhotoshootPanel(PhotoshootState state, PhotoshootOptions options)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("ps-panel");

            state.EnsureCatalog(AvatarRoot);
            if (CanGenerate() && ShotKinds().Any(kind => !state.HasPreviewTexture(kind))) RenderPreviews(false);

            if (options.Back != null)
            {
                backButton = CreateButton("‹  " + options.BackText, options.Back, "ps-back");
                Add(backButton);
            }
            if (options.ThumbnailPreview == null) BuildStage();
            BuildShots();
            BuildFraming();
            BuildStyle();
            Refresh();
        }

        internal static string DisplayName(PhotoshootService.ShotKind shotKind) => shotKind == PhotoshootService.ShotKind.Banner ? "Banner" : "Thumbnail";

        /// <summary>Re-reads host state (chosen shots, blocked input) without rebuilding the panel.</summary>
        public void Refresh()
        {
            foreach (var pair in shotRows)
            {
                bool chosen = IsFixed(pair.Key);
                var row = pair.Value;
                row.Primary.text = chosen ? "Retake" : "Capture";
                row.Primary.tooltip = chosen ? "Discard this image and follow the live preview again." : $"Capture the live preview as the {DisplayName(pair.Key).ToLowerInvariant()}.";
                row.Primary.EnableInClassList("ps-pill--accent", !chosen);
                row.Primary.SetEnabled(!InputBlocked() && (chosen || CanGenerate()));
                row.Browse.SetEnabled(!InputBlocked());
                row.Dot.EnableInClassList("ps-dot--set", chosen);
                var size = CaptureSize(pair.Key);
                row.Detail.text = chosen ? "Captured · kept while you adjust" : $"Live · {size.x}×{size.y}";
            }
            backButton?.SetEnabled(!InputBlocked());
            RefreshImages();
            UpdateMessage();
        }

        private GameObject AvatarRoot => options.AvatarRoot?.Invoke();
        private bool CanGenerate() => AvatarRoot != null && (options.CanGenerate?.Invoke() ?? true);
        private bool InputBlocked() => state.IsGenerating || (options.InputBlocked?.Invoke() ?? false);
        private Texture2D GetShot(PhotoshootService.ShotKind shotKind) => options.GetShot?.Invoke(shotKind);
        private bool IsFixed(PhotoshootService.ShotKind shotKind) => GetShot(shotKind) != null;

        private IEnumerable<PhotoshootService.ShotKind> ShotKinds()
        {
            yield return PhotoshootService.ShotKind.Thumbnail;
            if (options.IncludeBanner) yield return PhotoshootService.ShotKind.Banner;
        }

        private Vector2Int PreviewSize(PhotoshootService.ShotKind shotKind)
        {
            if (shotKind == PhotoshootService.ShotKind.Banner) return BannerPreviewSize;
            var size = options.ThumbnailSize;
            float scale = Mathf.Min(1f, 512f / Mathf.Max(size.x, size.y));
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(size.x * scale)), Mathf.Max(1, Mathf.RoundToInt(size.y * scale)));
        }

        private Vector2Int CaptureSize(PhotoshootService.ShotKind shotKind) => shotKind == PhotoshootService.ShotKind.Banner ? options.BannerSize : options.ThumbnailSize;

        private float ThumbnailAspect => options.ThumbnailSize.x > 0 && options.ThumbnailSize.y > 0 ? (float)options.ThumbnailSize.x / options.ThumbnailSize.y : 1f;

        // ---- Preview ----------------------------------------------------------------------------------------

        // With a banner: the banner fills the stage and the thumbnail floats over it, as on the asset page.
        // Thumbnail only: the thumbnail fills the stage at its own aspect.
        private void BuildStage()
        {
            var stage = new VisualElement();
            stage.AddToClassList("ps-stage");
            Add(stage);
            float aspect = options.IncludeBanner ? 16f / 9f : ThumbnailAspect;
            stage.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float width = stage.resolvedStyle.width;
                if (width > 0f && !float.IsNaN(width)) stage.style.height = Mathf.Min(width / aspect, options.IncludeBanner ? 480f : 360f);
            });

            if (options.IncludeBanner)
            {
                bannerImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
                bannerImage.AddToClassList("ps-stage__image");
                stage.Add(bannerImage);
                stage.Add(Tag("Banner", "ps-tag--banner"));

                var tile = new VisualElement();
                tile.AddToClassList("ps-stage__tile");
                stage.Add(tile);
                stage.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    float width = Mathf.Clamp(stage.resolvedStyle.width * 0.26f, 96f, 180f);
                    tile.style.width = width; tile.style.height = width / ThumbnailAspect;
                });
                thumbnailImage = new Image { scaleMode = ScaleMode.ScaleAndCrop };
                thumbnailImage.AddToClassList("ps-stage__image");
                tile.Add(thumbnailImage);
                tile.Add(Tag("Thumbnail", "ps-tag--tile"));
            }
            else
            {
                stage.AddToClassList("ps-stage--thumbnail");
                thumbnailImage = new Image { scaleMode = ScaleMode.ScaleToFit };
                thumbnailImage.AddToClassList("ps-stage__image");
                stage.Add(thumbnailImage);
            }

            // The stage shows the banner when there is one, otherwise the thumbnail fitted inside it.
            var stageKind = options.IncludeBanner ? PhotoshootService.ShotKind.Banner : PhotoshootService.ShotKind.Thumbnail;
            AttachFraming(stage, stageKind, () =>
            {
                var rect = stage.contentRect;
                if (options.IncludeBanner) return rect.size;
                float fitted = Mathf.Min(rect.width, rect.height * ThumbnailAspect);
                return new Vector2(fitted, fitted / ThumbnailAspect);
            });
        }

        private static Label Tag(string text, string modifier)
        {
            var tag = new Label(text);
            tag.AddToClassList("ps-tag");
            tag.AddToClassList(modifier);
            return tag;
        }

        // ---- Shots ------------------------------------------------------------------------------------------

        private void BuildShots()
        {
            var card = Card("ps-shots");
            foreach (var kind in ShotKinds())
            {
                var shotKind = kind;
                var row = new VisualElement();
                row.AddToClassList("ps-shot");
                row.EnableInClassList("ps-shot--divided", shotRows.Count > 0);
                card.Add(row);

                var entry = new ShotRow { Dot = new VisualElement() };
                entry.Dot.AddToClassList("ps-dot");
                row.Add(entry.Dot);

                var text = new VisualElement();
                text.AddToClassList("ps-shot__text");
                row.Add(text);
                var title = new Label(DisplayName(shotKind));
                title.AddToClassList("ps-shot__title");
                text.Add(title);
                entry.Detail = new Label();
                entry.Detail.AddToClassList("ps-shot__detail");
                text.Add(entry.Detail);

                entry.Primary = CreateButton("Capture", () =>
                {
                    if (IsFixed(shotKind)) Retake(shotKind);
                    else Capture(shotKind);
                }, "ps-pill");
                row.Add(entry.Primary);
                entry.Browse = CreateButton("Browse", () => Browse(shotKind), "ps-pill");
                entry.Browse.AddToClassList("ps-pill--ghost");
                entry.Browse.tooltip = "Use an image file instead.";
                row.Add(entry.Browse);
                shotRows[shotKind] = entry;
            }

            message = new Label();
            message.AddToClassList("ps-message");
            card.Add(message);
        }

        // ---- Framing ----------------------------------------------------------------------------------------

        private const float FramingTweenSeconds = 0.34f;
        private const float TurnDegreesPerPixel = 0.5f;

        private static readonly (PhotoshootService.FramingPreset preset, string label, string tip)[] FramingPresets =
        {
            (PhotoshootService.FramingPreset.Portrait, "Portrait", "Head and shoulders."),
            (PhotoshootService.FramingPreset.HalfBody, "Half body", "Down to the hips."),
            (PhotoshootService.FramingPreset.FullBody, "Full body", "The whole avatar."),
        };

        private void BuildFraming()
        {
            var card = Card("ps-framing");
            var header = Header(card, "Framing");
            var reset = CreateButton("Reset", ResetFraming, "ps-link");
            reset.tooltip = "Default zoom, centred, facing the camera.";
            header.Add(reset);

            presets = new SegmentedControl(FramingPresets.Select(entry => new SegmentedControl.Option(entry.label, null, entry.tip)),
                index => ApplyPreset(FramingPresets[index].preset));
            presets.AddToClassList("ps-framing__presets");
            card.Add(presets);

            turnDial = new ScrubDial("Turn", -180f, 180f, 0f, 5f, 3, 2.2f, 2.5f,
                value => Mathf.RoundToInt(value) + "°",
                value =>
                {
                    framingTween?.Pause();
                    state.RotationDegrees = value;
                    RenderNow();
                }, loops: true);
            card.Add(turnDial);
            zoomDial = new ScrubDial("Zoom", Mathf.Log(PhotoshootState.MinZoom, 2f), Mathf.Log(PhotoshootState.MaxZoom, 2f), Mathf.Log(PhotoshootState.DefaultZoom, 2f),
                0.1f, 5, 70f, 0.04f,
                value => Mathf.Pow(2f, value).ToString("0.00") + "×",
                value =>
                {
                    framingTween?.Pause();
                    state.Zoom = Mathf.Pow(2f, value);
                    ManualFraming();
                });
            card.Add(zoomDial);

            var tip = new Label("On the preview: drag to move, scroll to zoom, Shift-drag to turn");
            tip.AddToClassList("ps-caption");
            tip.AddToClassList("ps-framing__tip");
            card.Add(tip);
            SyncFraming();
        }

        private void SyncFraming()
        {
            turnDial?.SetValueWithoutNotify(state.RotationDegrees);
            zoomDial?.SetValueWithoutNotify(Mathf.Log(state.Zoom, 2f));
            MovePresetIndicator();
        }

        // With no preset the highlight fades out where it was; it slides to a preset when one is chosen.
        private void MovePresetIndicator() => presets?.SetIndex(Array.FindIndex(FramingPresets, entry => entry.preset == state.FramingPreset));

        // Zoom or placement changed by hand: the framing no longer follows a preset.
        private void ManualFraming()
        {
            state.FramingPreset = null;
            SyncFraming();
            RenderNow();
        }

        // Framing input renders in the same event, so the preview moves with the pointer; a framing render reuses the posed
        // avatar and costs a couple of milliseconds. Any render still queued for this tick is dropped.
        private void RenderNow()
        {
            ++state.RefreshTicket;
            if (CanGenerate()) RenderPreviews(false);
        }

        private void ApplyPreset(PhotoshootService.FramingPreset preset)
        {
            var kind = PhotoshootService.ShotKind.Thumbnail;
            if (!state.TrySuggestFraming(kind, PreviewSize(kind), preset, out float zoom, out Vector2 placement)) return;
            AnimateFraming(zoom, placement, null, preset);
        }

        private void ResetFraming() => AnimateFraming(PhotoshootState.DefaultZoom, Vector2.zero, 0f, null);

        // Glides zoom, placement and optionally rotation to a new framing with a light overshoot, rendering every frame.
        private void AnimateFraming(float toZoom, Vector2 toPlacement, float? toRotation, PhotoshootService.FramingPreset? preset)
        {
            framingTween?.Pause();
            state.FramingPreset = preset;
            MovePresetIndicator();
            float fromZoom = Mathf.Log(state.Zoom), targetZoom = Mathf.Log(Mathf.Clamp(toZoom, PhotoshootState.MinZoom, PhotoshootState.MaxZoom));
            Vector2 fromPlacement = state.Placement;
            float fromRotation = state.RotationDegrees;
            double start = EditorApplication.timeSinceStartup;
            framingTween = schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - start) / FramingTweenSeconds));
                float eased = EaseOutBack(t);
                state.Zoom = Mathf.Clamp(Mathf.Exp(Mathf.LerpUnclamped(fromZoom, targetZoom, eased)), PhotoshootState.MinZoom, PhotoshootState.MaxZoom);
                Vector2 placement = Vector2.LerpUnclamped(fromPlacement, toPlacement, eased);
                state.Placement = new Vector2(Mathf.Clamp(placement.x, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement), Mathf.Clamp(placement.y, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement));
                if (toRotation.HasValue)
                {
                    // The short way round.
                    state.RotationDegrees = fromRotation + Mathf.DeltaAngle(fromRotation, toRotation.Value) * eased;
                }
                SyncFraming();
                RenderPreviews(false, false);
                if (t >= 1f) framingTween?.Pause();
            }).Every(16);
        }

        private static float EaseOutBack(float t)
        {
            const float overshoot = 0.9f;
            float u = t - 1f;
            return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
        }

        private void MoveBy(Vector2 delta, Vector2 frame, PhotoshootService.ShotKind kind)
        {
            if (frame.x <= 0f || frame.y <= 0f) return;
            framingTween?.Pause();
            // Placement is scaled so the avatar follows the pointer across the frame it is shown in.
            Vector2 perFrame = state.PlacementPerFrame(kind, PreviewSize(kind));
            Vector2 placement = state.Placement + new Vector2(delta.x / frame.x * perFrame.x, -delta.y / frame.y * perFrame.y);
            state.Placement = new Vector2(Mathf.Clamp(placement.x, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement), Mathf.Clamp(placement.y, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement));
            ManualFraming();
        }

        private void TurnBy(float deltaX)
        {
            framingTween?.Pause();
            state.RotationDegrees -= deltaX * TurnDegreesPerPixel;
            SyncFraming();
            RenderNow();
        }

        private void ZoomBy(float wheel)
        {
            framingTween?.Pause();
            state.Zoom = Mathf.Clamp(state.Zoom * Mathf.Exp(-wheel * 0.05f), PhotoshootState.MinZoom, PhotoshootState.MaxZoom);
            ManualFraming();
        }

        /// <summary>
        /// Lets the avatar be framed directly on a host element that shows the live thumbnail (see
        /// <see cref="PhotoshootOptions.ThumbnailPreview"/>): drag to move, Shift-drag to turn, scroll to zoom, double-click
        /// to reset. <paramref name="frameSize"/> is the size of the whole thumbnail on that element, also where it is cropped.
        /// </summary>
        public void AttachFraming(VisualElement surface, Func<Vector2> frameSize) =>
            AttachFraming(surface, PhotoshootService.ShotKind.Thumbnail, frameSize);

        public void DetachFraming(VisualElement surface)
        {
            if (surface == null || !framingSurfaces.TryGetValue(surface, out var drag)) return;
            surface.RemoveManipulator(drag);
            framingSurfaces.Remove(surface);
        }

        private void AttachFraming(VisualElement surface, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
        {
            if (surface == null || frameSize == null || framingSurfaces.ContainsKey(surface)) return;
            var drag = new FramingDrag(this, kind, frameSize);
            framingSurfaces[surface] = drag;
            surface.AddManipulator(drag);
        }

        private sealed class FramingDrag : PointerManipulator
        {
            private readonly PhotoshootPanel owner;
            private readonly PhotoshootService.ShotKind kind;
            private readonly Func<Vector2> frameSize;
            private StyleSheet addedSheet;
            private Label hint;
            private int pointer = -1;
            private Vector2 last;

            public FramingDrag(PhotoshootPanel owner, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
            {
                this.owner = owner; this.kind = kind; this.frameSize = frameSize;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
                if (sheet && !target.styleSheets.Contains(sheet)) { target.styleSheets.Add(sheet); addedSheet = sheet; }
                target.AddToClassList("ps-surface");
                hint = new Label("Drag to move · Scroll to zoom · Shift-drag to turn") { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ps-surface__hint");
                target.Add(hint);
                target.RegisterCallback<PointerDownEvent>(OnDown);
                target.RegisterCallback<PointerMoveEvent>(OnMove);
                target.RegisterCallback<PointerUpEvent>(OnUp);
                target.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<PointerDownEvent>(OnDown);
                target.UnregisterCallback<PointerMoveEvent>(OnMove);
                target.UnregisterCallback<PointerUpEvent>(OnUp);
                target.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
                target.UnregisterCallback<WheelEvent>(OnWheel);
                hint?.RemoveFromHierarchy();
                target.RemoveFromClassList("ps-surface");
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
                if (addedSheet) target.styleSheets.Remove(addedSheet);
            }

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                if (evt.clickCount == 2) { owner.ResetFraming(); evt.StopPropagation(); return; }
                pointer = evt.pointerId;
                last = evt.position;
                target.CapturePointer(pointer);
                target.AddToClassList("ps-surface--dragging");
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                evt.StopPropagation();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (evt.pointerId != pointer || !target.HasPointerCapture(pointer)) return;
                Vector2 delta = (Vector2)evt.position - last;
                last = evt.position;
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                if (evt.shiftKey) owner.TurnBy(delta.x);
                else owner.MoveBy(delta, frameSize(), kind);
                evt.StopPropagation();
            }

            private void OnUp(PointerUpEvent evt)
            {
                if (evt.pointerId != pointer) return;
                if (target.HasPointerCapture(pointer)) target.ReleasePointer(pointer);
                End();
            }

            private void OnCaptureOut(PointerCaptureOutEvent evt) => End();

            private void End()
            {
                pointer = -1;
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
            }

            // Scrolling over the preview zooms instead of scrolling the window.
            private void OnWheel(WheelEvent evt)
            {
                owner.ZoomBy(evt.delta.y);
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }

        // ---- Style: pose, light, background, expression ----------------------------------------------------

        private void BuildStyle()
        {
            var card = Card("ps-style");
            state.StyleTab = Mathf.Clamp(state.StyleTab, 0, StyleTabs.Length - 1);
            styleTabs = new SegmentedControl(StyleTabs, SelectTab);
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
                        grid.Add(Swatch("ps-swatch--pose", i, option.displayName, new Image { image = state.GetPoseIcon(AvatarRoot, option), scaleMode = ScaleMode.ScaleToFit }));
                    }
                    break;
                case LightTab:
                    for (int i = 0; i < state.Catalog.lightPresets.Count; i++)
                    {
                        var option = state.Catalog.lightPresets[i];
                        grid.Add(Swatch("ps-swatch--light", i, option.displayName, new Image { image = state.GetLightIcon(option), scaleMode = ScaleMode.ScaleToFit }));
                    }
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
                        grid.Add(Swatch("ps-swatch--background", i, option.displayName, content));
                    }
                    colorPicker = new InlineColorPicker(state.BackgroundColor, PhotoshootService.DefaultBackgroundColor, color =>
                    {
                        state.BackgroundColor = color;
                        if (colorSwatch != null) colorSwatch.style.backgroundColor = color;
                        RenderNow();
                    });
                    styleContent.Add(colorPicker);
                    break;
                default:
                    BuildExpressionChips(grid);
                    break;
            }
            UpdateSelectionVisuals();
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

            int selected = SelectedIndex(tab);
            for (int i = 0; i < swatches.Count; i++) swatches[i].EnableInClassList("ps-swatch--selected", i == selected);
            styleCaption.text = selected >= 0 && selected < swatches.Count ? swatches[selected].tooltip : "";
            if (colorPicker != null)
            {
                bool solid = tab == BackgroundTab && selected >= 0 && selected < state.Catalog.backgrounds.Count && state.Catalog.backgrounds[selected].solidColor;
                colorPicker.style.display = solid ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // ---- Live preview and shots -------------------------------------------------------------------------

        private void RenderPreviews(bool forceFaceBlendshapeApply, bool refitPreset = true)
        {
            var avatarRoot = AvatarRoot;
            if (avatarRoot != null && CanGenerate())
            {
                foreach (var kind in ShotKinds()) state.RenderPreview(avatarRoot, kind, PreviewSize(kind), forceFaceBlendshapeApply);
            }

            RefreshImages();
            UpdateMessage();
            RepaintPreview();
            if (refitPreset) RefitPreset();
            if (state.TakeUnsettled()) Settle();
        }

        // After a new pose or expression, let the editor run an update and draw again over the next two frames, so skinned
        // meshes that were drawn from last frame's skinning (hair, accessories) catch up without any other input.
        private void Settle()
        {
            int generation = ++settleGeneration;
            EditorApplication.QueuePlayerLoopUpdate();
            EditorApplication.delayCall += () =>
            {
                if (generation != settleGeneration || panel == null || !CanGenerate()) return;
                RenderPreviews(false, false);
                EditorApplication.QueuePlayerLoopUpdate();
                EditorApplication.delayCall += () =>
                {
                    if (generation != settleGeneration || panel == null || !CanGenerate()) return;
                    RenderPreviews(false);
                };
            };
        }

        // A pose or rotation changes the avatar's outline; a chosen preset fits it again in the same frame, so it never
        // trails behind a drag.
        private void RefitPreset()
        {
            if (!state.FramingPreset.HasValue || (framingTween != null && framingTween.isActive)) return;
            var kind = PhotoshootService.ShotKind.Thumbnail;
            if (!state.TrySuggestFraming(kind, PreviewSize(kind), state.FramingPreset.Value, out float zoom, out Vector2 placement)) return;
            if (Mathf.Abs(Mathf.Log(zoom) - Mathf.Log(state.Zoom)) < 0.01f && (placement - state.Placement).sqrMagnitude < 0.0001f) return;
            state.Zoom = zoom;
            state.Placement = placement;
            SyncFraming();
            RenderPreviews(false, false);
        }

        // Coalesces bursts of changes (slider drags, quick picks) into one render on the next editor tick.
        private void ScheduleRefresh(bool forceFaceBlendshapeApply)
        {
            int ticket = ++state.RefreshTicket;
            EditorApplication.delayCall += () =>
            {
                if (ticket != state.RefreshTicket || panel == null || !CanGenerate()) return;
                RenderPreviews(forceFaceBlendshapeApply);
            };
        }

        private Texture DisplayTexture(PhotoshootService.ShotKind shotKind)
        {
            Texture chosen = GetShot(shotKind);
            return chosen != null ? chosen : state.GetPreviewTexture(shotKind);
        }

        private void RefreshImages()
        {
            if (bannerImage != null) { bannerImage.image = DisplayTexture(PhotoshootService.ShotKind.Banner); bannerImage.MarkDirtyRepaint(); }
            if (thumbnailImage != null) { thumbnailImage.image = DisplayTexture(PhotoshootService.ShotKind.Thumbnail); thumbnailImage.MarkDirtyRepaint(); }
            options.ThumbnailPreview?.Invoke(DisplayTexture(PhotoshootService.ShotKind.Thumbnail));
        }

        private void RepaintPreview()
        {
            bannerImage?.MarkDirtyRepaint();
            thumbnailImage?.MarkDirtyRepaint();
            options.Repaint?.Invoke();
        }

        private void UpdateMessage()
        {
            if (message == null) return;
            bool error = !string.IsNullOrWhiteSpace(state.Error);
            string text = error ? state.Error : state.Status;
            message.text = text ?? "";
            message.EnableInClassList("ps-message--error", error);
            message.style.display = string.IsNullOrWhiteSpace(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void Capture(PhotoshootService.ShotKind shotKind)
        {
            if (state.IsGenerating || !CanGenerate()) return;
            state.IsGenerating = true;
            state.Error = null;
            try
            {
                var size = CaptureSize(shotKind);
                var texture = state.Capture(AvatarRoot, shotKind, size);
                options.SetShot?.Invoke(shotKind, texture);
                // A host that keeps following the live preview after a capture gets a confirmation instead of a Retake.
                state.Status = IsFixed(shotKind) ? null : $"{DisplayName(shotKind)} captured";
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                state.Status = null;
            }
            finally
            {
                state.IsGenerating = false;
            }

            Refresh();
            options.Changed?.Invoke();
        }

        private void Retake(PhotoshootService.ShotKind shotKind)
        {
            options.SetShot?.Invoke(shotKind, null);
            state.Status = null;
            var avatarRoot = AvatarRoot;
            if (avatarRoot != null && CanGenerate()) state.RenderPreview(avatarRoot, shotKind, PreviewSize(shotKind), false);
            Refresh();
            RepaintPreview();
            options.Changed?.Invoke();
        }

        private void Browse(PhotoshootService.ShotKind shotKind)
        {
            string path = EditorUtility.OpenFilePanelWithFilters($"Choose {DisplayName(shotKind)}", Application.dataPath,
                new[] { "Image files", "png,jpg,jpeg", "All files", "*" });
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                Texture2D texture = LoadImage(path);
                if (texture == null) throw new InvalidOperationException("Selected image could not be loaded.");
                options.SetShot?.Invoke(shotKind, texture);
                state.Error = null;
                state.Status = null;
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                state.Status = null;
                Refresh();
                return;
            }

            Refresh();
            options.Changed?.Invoke();
            options.Browsed?.Invoke(shotKind);
        }

        private static Texture2D LoadImage(string path)
        {
            string normalizedPath = path.Replace('\\', '/');
            string normalizedAssetsPath = Application.dataPath.Replace('\\', '/');
            if (normalizedPath.StartsWith(normalizedAssetsPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var assetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets" + normalizedPath.Substring(normalizedAssetsPath.Length));
                if (assetTexture != null) return assetTexture;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = Path.GetFileNameWithoutExtension(path),
                hideFlags = HideFlags.DontSave
            };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return null;
            }

            return texture;
        }

        // ---- Small UI helpers -------------------------------------------------------------------------------

        private VisualElement Card(string modifier)
        {
            var card = new VisualElement();
            card.AddToClassList("ps-card");
            card.AddToClassList(modifier);
            Add(card);
            return card;
        }

        private static VisualElement Header(VisualElement card, string text)
        {
            var header = new VisualElement();
            header.AddToClassList("ps-card__header");
            card.Add(header);
            var title = new Label(text);
            title.AddToClassList("ps-card__title");
            header.Add(title);
            return header;
        }

        // The editor font cannot draw astral-plane characters such as emoji; drop them rather than show boxes.
        private static string WithoutSurrogates(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new System.Text.StringBuilder(text.Length);
            foreach (char c in text) if (!char.IsSurrogate(c)) builder.Append(c);
            return builder.ToString();
        }

        // Acts on press, with a press state for immediate visual feedback; Unity's click remains the fallback.
        private static Button CreateButton(string text, Action onClick, string className)
        {
            var button = new Button { text = text ?? string.Empty };
            button.AddToClassList(className);
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("ps-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("ps-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("ps-pressed"));
            ButtonInteraction.RegisterImmediateClick(button, onClick);
            return button;
        }
    }
}
