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
        /// <summary>
        /// The host uploads banners to a server that applies the banner effect itself: <see cref="SetShot"/> then gets the
        /// banner as rendered (or browsed), and the panel shows it with the effect, as it will look once uploaded.
        /// </summary>
        public bool ServerAppliesBannerEffect;
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
        /// <summary>
        /// When set, the panel can show a second photoshoot instead of its shots: the avatar from the front, the back and
        /// the side on one <see cref="PhotoshootRefSheet.Size"/> sheet. The host opens it with its own button
        /// (<see cref="PhotoshootPanel.ShowRefSheet"/>, or <see cref="PhotoshootState.RefSheetOpen"/> before building the
        /// panel). Receives each captured sheet; the host owns it.
        /// </summary>
        public Action<Texture2D> RefSheet;
        /// <summary>What the thumbnail shot is called in the panel (My Avatar's gallery calls it a picture).</summary>
        public string ThumbnailLabel = "Thumbnail";
        /// <summary>
        /// Fonts (asset paths, an empty one for Unity's default font) for a line of text on the thumbnail
        /// (<see cref="PhotoshootState.Text"/>): its Text tab and the text on the previews. Null: no text.
        /// </summary>
        public IReadOnlyList<string> TextFonts;
        /// <summary>Called once a change to the thumbnail's text is done (not during a drag), so the host can keep it.</summary>
        public Action<PhotoshootText> TextChanged;
    }

    /// <summary>Live photoshoot editor: preview, shot capture, framing controls and pose, light, background and expression pickers.</summary>
    public sealed partial class PhotoshootPanel : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/Photoshoot/photoshoot.uss";
        private static readonly Vector2Int BannerPreviewSize = new Vector2Int(768, 432);
        private static readonly string[] StyleTabs = { "Pose", "Light", "Background", "Expression", "Effects" };
        private const int PoseTab = 0, LightTab = 1, BackgroundTab = 2, ExpressionTab = 3, EffectsTab = 4, TextTab = 5;

        private readonly PhotoshootState state;
        private readonly PhotoshootOptions options;
        private readonly List<Button> swatches = new List<Button>();
        private readonly Dictionary<string, Button> expressionChips = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<PhotoshootService.ShotKind, ShotRow> shotRows = new Dictionary<PhotoshootService.ShotKind, ShotRow>();
        private readonly Dictionary<VisualElement, FramingDrag> framingSurfaces = new Dictionary<VisualElement, FramingDrag>();
        private readonly Dictionary<VisualElement, PhotoshootTextLayer> textLayers = new Dictionary<VisualElement, PhotoshootTextLayer>();
        private static readonly Vector2Int RefSheetPreviewSize = new Vector2Int(1152, 648);
        private Image bannerImage, thumbnailImage, refSheetImage;
        private VisualElement ownStage, flash;
        // The two columns the cards go in (side by side once wide), the one being built, and the host's lead content.
        private VisualElement mainColumn, column, lead;
        private const float WideWidth = 820f;
        private Button refSheetCapture;
        private SegmentedControl sideControl;
        private Texture effectPreviewSource;
        private Texture2D effectPreview;
        private Button backButton;
        private ScrubDial zoomDial, lookDial;
        private OrbitSphere orbit;
        private ToggleSwitch lookSwitch;
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
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (effectPreview != null) UnityEngine.Object.DestroyImmediate(effectPreview);
                effectPreview = null;
                effectPreviewSource = null;
            });
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("ps-panel");
            // Wide enough for two columns: the shot and its framing on the left, its style beside them.
            RegisterCallback<GeometryChangedEvent>(_ => EnableInClassList("ps-panel--wide", resolvedStyle.width >= WideWidth));

            state.EnsureCatalog(AvatarRoot);
            if (options.RefSheet == null) state.RefSheetOpen = false;
            if (!state.RefSheetOpen && CanGenerate() && ShotKinds().Any(kind => !state.HasPreviewTexture(kind))) RenderPreviews(false);
            Build();
            if (state.RefSheetOpen && CanGenerate()) RenderRefSheet(state.RefSheetPreview == null);
        }

        internal static string DisplayName(PhotoshootService.ShotKind shotKind) => shotKind == PhotoshootService.ShotKind.Banner ? "Banner" : "Thumbnail";

        /// <summary>The ref sheet is shown instead of the shots.</summary>
        public bool RefSheetOpen => state.RefSheetOpen;

        private string ShotName(PhotoshootService.ShotKind shotKind) =>
            shotKind == PhotoshootService.ShotKind.Banner || string.IsNullOrWhiteSpace(options.ThumbnailLabel) ? DisplayName(shotKind) : options.ThumbnailLabel;

        // The photoshoot, or its ref sheet: the cards are built again when switching between them.
        private void Build()
        {
            if (ownStage != null) DetachFraming(ownStage);
            Clear();
            shotRows.Clear();
            bannerImage = thumbnailImage = refSheetImage = null;
            ownStage = flash = null;
            backButton = refSheetCapture = null;
            zoomDial = lookDial = null;
            orbit = null;
            lookSwitch = null;
            presets = sideControl = null;
            message = null;
            mainColumn = new VisualElement();
            mainColumn.AddToClassList("ps-column");
            mainColumn.AddToClassList("ps-column--main");
            Add(mainColumn);
            var styleColumn = new VisualElement();
            styleColumn.AddToClassList("ps-column");
            styleColumn.AddToClassList("ps-column--style");
            Add(styleColumn);
            if (lead != null) mainColumn.Add(lead);
            column = mainColumn;
            if (state.RefSheetOpen)
            {
                // The views reuse the thumbnail's render target: a host showing the live thumbnail lets go of it.
                options.ThumbnailPreview?.Invoke(null);
                BuildRefSheetStage();
                BuildRefSheetShot();
                BuildRefSheetFraming();
            }
            else
            {
                if (options.Back != null)
                {
                    backButton = CreateButton("‹  " + options.BackText, options.Back, "ps-back");
                    column.Add(backButton);
                }
                if (options.ThumbnailPreview == null) BuildStage();
                BuildShots();
                BuildFraming();
            }
            column = styleColumn;
            BuildStyle();
            column = mainColumn;
            Refresh();
        }

        /// <summary>
        /// Host content shown first in the panel, above its shots (and beside the style once the panel is wide), such as
        /// the card a host shows the live thumbnail on. Null takes it back out; the host puts it where it belongs.
        /// </summary>
        public void SetLead(VisualElement value)
        {
            if (lead == value) return;
            lead?.RemoveFromHierarchy();
            lead = value;
            if (lead != null) mainColumn?.Insert(0, lead);
        }

        /// <summary>Re-reads host state (chosen shots, blocked input) without rebuilding the panel.</summary>
        public void Refresh()
        {
            foreach (var layer in textLayers.Values) layer.style.display = TextOn ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var pair in shotRows)
            {
                bool chosen = IsFixed(pair.Key);
                var row = pair.Value;
                row.Primary.text = chosen ? "Retake" : "Capture";
                row.Primary.tooltip = chosen ? "Discard this image and follow the live preview again." : $"Capture the live preview as the {ShotName(pair.Key).ToLowerInvariant()}.";
                row.Primary.EnableInClassList("ps-pill--accent", !chosen);
                row.Primary.SetEnabled(!InputBlocked() && (chosen || CanGenerate()));
                row.Browse.SetEnabled(!InputBlocked());
                row.Dot.EnableInClassList("ps-dot--set", chosen);
                var size = CaptureSize(pair.Key);
                row.Detail.text = chosen ? "Captured · kept while you adjust" : $"Live · {size.x}×{size.y}";
            }
            backButton?.SetEnabled(!InputBlocked());
            refSheetCapture?.SetEnabled(!InputBlocked() && CanGenerate());
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
            column.Add(stage);
            ownStage = stage;
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
                var title = new Label(ShotName(shotKind));
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

        // ---- Ref sheet ----------------------------------------------------------------------------------------

        /// <summary>Shows the ref sheet (front, back and side) instead of the shots, or the shots again.</summary>
        public void ShowRefSheet(bool show)
        {
            if (state.RefSheetOpen == show || (show && options.RefSheet == null)) return;
            framingTween?.Pause();
            state.RefSheetOpen = show;
            state.Status = null;
            Build();
            // Both use the thumbnail's render target: whichever shows now renders again at its own size.
            if (show) RenderRefSheet(true);
            else RenderPreviews(false);
            options.Changed?.Invoke();
        }

        private void BuildRefSheetStage()
        {
            var stage = new VisualElement();
            stage.AddToClassList("ps-stage");
            stage.AddToClassList("ps-stage--refsheet");
            column.Add(stage);
            stage.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float width = stage.resolvedStyle.width;
                if (width > 0f && !float.IsNaN(width)) stage.style.height = Mathf.Min(width * PhotoshootRefSheet.Size.y / PhotoshootRefSheet.Size.x, 440f);
            });
            refSheetImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            refSheetImage.AddToClassList("ps-stage__image");
            stage.Add(refSheetImage);
            flash = new VisualElement { pickingMode = PickingMode.Ignore };
            flash.AddToClassList("ps-stage__flash");
            stage.Add(flash);
            stage.AddManipulator(new RefSheetDrag(this));
        }

        private void BuildRefSheetShot()
        {
            var card = Card("ps-shots");
            var row = new VisualElement();
            row.AddToClassList("ps-shot");
            card.Add(row);
            var icon = new VectorIcon(IconGlyph.RefSheet);
            icon.AddToClassList("ps-shot__icon");
            row.Add(icon);
            var text = new VisualElement();
            text.AddToClassList("ps-shot__text");
            row.Add(text);
            var title = new Label("Ref sheet");
            title.AddToClassList("ps-shot__title");
            text.Add(title);
            var size = PhotoshootRefSheet.Size;
            var detail = new Label($"Live · {size.x}×{size.y}");
            detail.AddToClassList("ps-shot__detail");
            text.Add(detail);
            refSheetCapture = CreateButton("Capture", CaptureRefSheet, "ps-pill");
            refSheetCapture.AddToClassList("ps-pill--accent");
            refSheetCapture.tooltip = $"Capture the sheet as shown, at {size.x}×{size.y}.";
            row.Add(refSheetCapture);
            message = new Label();
            message.AddToClassList("ps-message");
            card.Add(message);
        }

        private void BuildRefSheetFraming()
        {
            var card = Card("ps-framing");
            var header = Header(card, "Framing");
            var fit = CreateButton("Fit", FitRefSheet, "ps-link");
            fit.tooltip = "The whole body in every view, at one scale.";
            header.Add(fit);

            zoomDial = new ScrubDial("Zoom", Mathf.Log(PhotoshootState.MinRefSheetZoom, 2f), Mathf.Log(PhotoshootState.MaxRefSheetZoom, 2f), 0f,
                0.1f, 5, 70f, 0.04f,
                value => Mathf.Pow(2f, value).ToString("0.00") + "×",
                value =>
                {
                    framingTween?.Pause();
                    state.RefSheetZoom = Mathf.Pow(2f, value);
                    RenderRefSheet(false);
                });
            zoomDial.tooltip = "Every view zooms together, so they keep one scale.";
            card.Add(zoomDial);

            var side = new VisualElement();
            side.AddToClassList("ps-look");
            var sideLabel = new Label("Side view");
            sideLabel.AddToClassList("ps-look__label");
            side.Add(sideLabel);
            sideControl = new SegmentedControl(new[] { "Faces left", "Faces right" }, index =>
            {
                if (state.RefSheetSideFacesRight == (index == 1)) return;
                state.RefSheetSideFacesRight = index == 1;
                RenderRefSheet(true);
            });
            sideControl.AddToClassList("ps-refsheet__side");
            side.Add(sideControl);
            card.Add(side);

            var tip = new Label("On the sheet: drag up or down to move, scroll to zoom, double-click to fit");
            tip.AddToClassList("ps-caption");
            tip.AddToClassList("ps-framing__tip");
            card.Add(tip);
            Foldable(card, header);
            SyncFraming();
        }

        // The three views render in the same event as the input, like framing: each reuses the posed avatar copy.
        private void RenderRefSheet(bool refit, bool forceFaceBlendshapeApply = false)
        {
            ++state.RefreshTicket;
            var avatarRoot = AvatarRoot;
            if (avatarRoot != null && CanGenerate()) state.RenderRefSheet(avatarRoot, RefSheetPreviewSize, refit, forceFaceBlendshapeApply);
            RefreshImages();
            UpdateMessage();
            RepaintPreview();
            if (state.TakeUnsettled()) Settle();
        }

        private void CaptureRefSheet()
        {
            if (state.IsGenerating || !CanGenerate() || options.RefSheet == null) return;
            state.IsGenerating = true;
            state.Error = null;
            bool captured = false;
            try
            {
                var sheet = state.CaptureRefSheet(AvatarRoot);
                state.Status = "Ref sheet captured";
                options.RefSheet(sheet);
                captured = true;
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

            if (captured) Flash();
            // The capture rendered the views at full size: the live sheet renders again at its own.
            RenderRefSheet(false);
            Refresh();
            options.Changed?.Invoke();
        }

        private void Flash()
        {
            if (flash == null) return;
            flash.AddToClassList("ps-stage__flash--on");
            flash.schedule.Execute(() => flash.RemoveFromClassList("ps-stage__flash--on")).StartingIn(40);
        }

        // Back to the whole body in every view, gliding like the framing presets.
        private void FitRefSheet()
        {
            framingTween?.Pause();
            float fromZoom = Mathf.Log(state.RefSheetZoom), fromLift = state.RefSheetLift;
            double start = EditorApplication.timeSinceStartup;
            framingTween = schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - start) / FramingTweenSeconds));
                float eased = EaseOutBack(t);
                state.RefSheetZoom = Mathf.Exp(Mathf.LerpUnclamped(fromZoom, 0f, eased));
                state.RefSheetLift = Mathf.LerpUnclamped(fromLift, 0f, eased);
                SyncFraming();
                RenderRefSheet(false);
                if (t >= 1f) framingTween?.Pause();
            }).Every(16);
        }

        private void LiftRefSheet(float pixels, float sheetHeight)
        {
            float viewHeight = sheetHeight * (1f - PhotoshootRefSheet.LabelBand - PhotoshootRefSheet.Floor);
            if (viewHeight <= 0f) return;
            framingTween?.Pause();
            // Dragging up moves the views up: the avatar follows the pointer.
            state.RefSheetLift -= pixels / viewHeight * state.RefSheetLiftPerView(RefSheetPreviewSize);
            RenderRefSheet(false);
        }

        private void ZoomRefSheet(float wheel)
        {
            framingTween?.Pause();
            state.RefSheetZoom *= Mathf.Exp(-wheel * 0.05f);
            SyncFraming();
            RenderRefSheet(false);
        }

        /// <summary>On the ref sheet: drag up and down to move every view, scroll to zoom them, double-click to fit.</summary>
        private sealed class RefSheetDrag : PointerManipulator
        {
            private readonly PhotoshootPanel owner;
            private Label hint;
            private PointerDragCapture drag;
            private float last;

            public RefSheetDrag(PhotoshootPanel owner) => this.owner = owner;

            protected override void RegisterCallbacksOnTarget()
            {
                target.AddToClassList("ps-surface");
                target.AddToClassList("ps-surface--vertical");
                hint = new Label("Drag to move · Scroll to zoom · Double-click to fit") { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ps-surface__hint");
                target.Add(hint);
                target.RegisterCallback<PointerDownEvent>(OnDown);
                target.RegisterCallback<PointerMoveEvent>(OnMove);
                drag = new PointerDragCapture(target, () => target.RemoveFromClassList("ps-surface--dragging"));
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                drag.Dispose();
                target.UnregisterCallback<PointerDownEvent>(OnDown);
                target.UnregisterCallback<PointerMoveEvent>(OnMove);
                target.UnregisterCallback<WheelEvent>(OnWheel);
                hint?.RemoveFromHierarchy();
                target.RemoveFromClassList("ps-surface");
                target.RemoveFromClassList("ps-surface--vertical");
                target.RemoveFromClassList("ps-surface--dragging");
            }

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                if (evt.clickCount == 2) { owner.FitRefSheet(); evt.StopPropagation(); return; }
                last = evt.position.y;
                drag.Begin(evt.pointerId);
                target.AddToClassList("ps-surface--dragging");
                evt.StopPropagation();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (!drag.Owns(evt.pointerId)) return;
                float delta = evt.position.y - last;
                last = evt.position.y;
                // The sheet fills the stage's width or its height, whichever it reaches first.
                var rect = target.contentRect;
                float sheetHeight = Mathf.Min(rect.height, rect.width * PhotoshootRefSheet.Size.y / PhotoshootRefSheet.Size.x);
                try { owner.LiftRefSheet(delta, sheetHeight); }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            }

            private void OnWheel(WheelEvent evt)
            {
                owner.ZoomRefSheet(evt.delta.y);
                evt.StopPropagation();
                evt.PreventDefault();
            }
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

            // The sphere turns and tilts the avatar; beside it, zoom and where the avatar looks.
            var body = new VisualElement();
            body.AddToClassList("ps-framing__body");
            card.Add(body);
            orbit = new OrbitSphere(PhotoshootState.MaxTilt, (yaw, tilt) =>
            {
                framingTween?.Pause();
                state.RotationDegrees = yaw;
                state.TiltDegrees = tilt;
                RenderKeepingFraming();
            }, state.BeginTurn, () => AnimateFraming(state.Zoom, state.Placement, 0f, state.FramingPreset));
            orbit.tooltip = "Drag to turn the avatar, up and down to tilt it. It turns around whichever of its hips, chest and head is in the middle of the view. Double-click to face the camera again.";
            orbit.AddToClassList("ps-framing__orbit");
            body.Add(orbit);
            var controls = new VisualElement();
            controls.AddToClassList("ps-framing__controls");
            body.Add(controls);

            zoomDial = new ScrubDial("Zoom", Mathf.Log(PhotoshootState.MinZoom, 2f), Mathf.Log(PhotoshootState.MaxZoom, 2f), Mathf.Log(PhotoshootState.DefaultZoom, 2f),
                0.1f, 5, 70f, 0.04f,
                value => Mathf.Pow(2f, value).ToString("0.00") + "×",
                value =>
                {
                    framingTween?.Pause();
                    state.Zoom = Mathf.Pow(2f, value);
                    ManualFraming();
                });
            controls.Add(zoomDial);

            var look = new VisualElement();
            look.AddToClassList("ps-look");
            var lookLabel = new Label("Look at the camera");
            lookLabel.AddToClassList("ps-look__label");
            look.Add(lookLabel);
            lookSwitch = new ToggleSwitch(state.LookAtCamera, on =>
            {
                state.LookAtCamera = on;
                SyncLook();
                RenderKeepingFraming();
            }) { tooltip = "The avatar looks straight at the camera." };
            look.Add(lookSwitch);
            controls.Add(look);
            lookDial = new ScrubDial("Look", 0f, 100f, 50f, 10f, 5, 2.4f, 3f,
                value => value < .5f ? "Head" : value > 99.5f ? "Eyes" : $"Eyes {value:0}%",
                value =>
                {
                    state.LookWithEyes = value / 100f;
                    RenderKeepingFraming();
                });
            lookDial.tooltip = "The avatar always looks straight at the camera: this splits the turn between the head (left) and the eyes (right).";
            controls.Add(lookDial);

            var tip = new Label("On the preview: drag to move, scroll to zoom, Shift-drag to turn and tilt");
            tip.AddToClassList("ps-caption");
            tip.AddToClassList("ps-framing__tip");
            card.Add(tip);
            Foldable(card, header);
            SyncFraming();
        }

        // The framing card folds under its header (a press anywhere on it but its links); it stays as left, open at first.
        private void Foldable(VisualElement card, VisualElement header)
        {
            var body = new VisualElement();
            body.AddToClassList("ps-card__body");
            foreach (var child in card.Children().Where(child => child != header).ToList()) body.Add(child);
            card.Add(body);
            card.AddToClassList("ps-card--foldable");
            var chevron = new VectorIcon(IconGlyph.Chevron) { pickingMode = PickingMode.Ignore };
            chevron.AddToClassList("ps-card__chevron");
            header.Insert(0, chevron);
            header.tooltip = "Fold or unfold";
            void Show()
            {
                card.EnableInClassList("ps-card--folded", !state.FramingOpen);
                body.style.display = state.FramingOpen ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // The links in the header (Reset, Fit) act on press and stop the event there.
            header.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                state.FramingOpen = !state.FramingOpen;
                Show();
            });
            Show();
        }

        private void SyncFraming()
        {
            if (state.RefSheetOpen)
            {
                zoomDial?.SetValueWithoutNotify(Mathf.Log(state.RefSheetZoom, 2f));
                sideControl?.SetIndex(state.RefSheetSideFacesRight ? 1 : 0);
                return;
            }
            orbit?.SetValueWithoutNotify(state.RotationDegrees, state.TiltDegrees);
            zoomDial?.SetValueWithoutNotify(Mathf.Log(state.Zoom, 2f));
            MovePresetIndicator();
            SyncLook();
        }

        private void SyncLook()
        {
            lookSwitch?.SetValueWithoutNotify(state.LookAtCamera);
            if (lookDial == null) return;
            lookDial.SetValueWithoutNotify(state.LookWithEyes * 100f);
            lookDial.style.display = state.LookAtCamera ? DisplayStyle.Flex : DisplayStyle.None;
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

        // Turning, tilting and where the avatar looks keep the zoom and placement: a framing preset is not fitted again to
        // the changed outline.
        private void RenderKeepingFraming()
        {
            ++state.RefreshTicket;
            if (CanGenerate()) RenderPreviews(false, false);
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
            float fromRotation = state.RotationDegrees, fromTilt = state.TiltDegrees;
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
                    state.TiltDegrees = Mathf.LerpUnclamped(fromTilt, 0f, eased);
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

        private void TurnBy(Vector2 delta)
        {
            framingTween?.Pause();
            state.RotationDegrees -= delta.x * TurnDegreesPerPixel;
            state.TiltDegrees += delta.y * TurnDegreesPerPixel;
            SyncFraming();
            RenderKeepingFraming();
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
            if (textLayers.TryGetValue(surface, out var layer)) { layer.RemoveFromHierarchy(); textLayers.Remove(surface); }
        }

        private void AttachFraming(VisualElement surface, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
        {
            if (surface == null || frameSize == null || framingSurfaces.ContainsKey(surface)) return;
            var drag = new FramingDrag(this, kind, frameSize);
            framingSurfaces[surface] = drag;
            surface.AddManipulator(drag);
            // The thumbnail's text sits on the same surface: pressing it moves the text, pressing elsewhere the avatar.
            if (kind == PhotoshootService.ShotKind.Thumbnail && options.TextFonts != null)
            {
                var layer = new PhotoshootTextLayer(() => state.Text, frameSize, TextChanged);
                layer.style.display = TextOn ? DisplayStyle.Flex : DisplayStyle.None;
                surface.Add(layer);
                textLayers[surface] = layer;
            }
        }

        private sealed class FramingDrag : PointerManipulator
        {
            private readonly PhotoshootPanel owner;
            private readonly PhotoshootService.ShotKind kind;
            private readonly Func<Vector2> frameSize;
            private StyleSheet addedSheet;
            private Label hint;
            private PointerDragCapture drag;
            private Vector2 last;
            private bool turning;

            public FramingDrag(PhotoshootPanel owner, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
            {
                this.owner = owner; this.kind = kind; this.frameSize = frameSize;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
                if (sheet && !target.styleSheets.Contains(sheet)) { target.styleSheets.Add(sheet); addedSheet = sheet; }
                target.AddToClassList("ps-surface");
                hint = new Label("Drag to move · Scroll to zoom · Shift-drag to turn and tilt") { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ps-surface__hint");
                target.Add(hint);
                target.RegisterCallback<PointerDownEvent>(OnDown);
                target.RegisterCallback<PointerMoveEvent>(OnMove);
                drag = new PointerDragCapture(target, End);
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                drag.Dispose();
                target.UnregisterCallback<PointerDownEvent>(OnDown);
                target.UnregisterCallback<PointerMoveEvent>(OnMove);
                target.UnregisterCallback<WheelEvent>(OnWheel);
                hint?.RemoveFromHierarchy();
                target.RemoveFromClassList("ps-surface");
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
                if (addedSheet) target.styleSheets.Remove(addedSheet);
            }

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0 || owner.state.RefSheetOpen) return;
                if (evt.clickCount == 2) { owner.ResetFraming(); evt.StopPropagation(); return; }
                last = evt.position;
                drag.Begin(evt.pointerId);
                target.AddToClassList("ps-surface--dragging");
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                evt.StopPropagation();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (!drag.Owns(evt.pointerId)) return;
                Vector2 delta = (Vector2)evt.position - last;
                last = evt.position;
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                try
                {
                    if (evt.shiftKey)
                    {
                        // A turn starts around the bone nearest the middle of the view.
                        if (!turning) { turning = true; owner.state.BeginTurn(); }
                        owner.TurnBy(delta);
                    }
                    else
                    {
                        turning = false;
                        owner.MoveBy(delta, frameSize(), kind);
                    }
                }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            }

            private void End()
            {
                turning = false;
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
            }

            // Scrolling over the preview zooms instead of scrolling the window.
            private void OnWheel(WheelEvent evt)
            {
                if (owner.state.RefSheetOpen) return;
                owner.ZoomBy(evt.delta.y);
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }

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

        // ---- Live preview and shots -------------------------------------------------------------------------

        private void RenderPreviews(bool forceFaceBlendshapeApply, bool refitPreset = true)
        {
            if (state.RefSheetOpen)
            {
                // A pose or expression changes the outline: the full body is fitted again.
                RenderRefSheet(refitPreset, forceFaceBlendshapeApply);
                return;
            }

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
            // Within the zoom dial's range, as when the preset was chosen.
            zoom = Mathf.Clamp(zoom, PhotoshootState.MinZoom, PhotoshootState.MaxZoom);
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
            if (chosen == null) return state.GetPreviewTexture(shotKind);
            if (!options.ServerAppliesBannerEffect || shotKind != PhotoshootService.ShotKind.Banner) return chosen;
            // The chosen banner is the plain image the server will process: shown as it will come back.
            if (effectPreviewSource != chosen)
            {
                if (effectPreview != null) UnityEngine.Object.DestroyImmediate(effectPreview);
                effectPreview = PhotoshootService.ApplyBannerEffect(chosen);
                effectPreviewSource = chosen;
            }
            return effectPreview != null ? effectPreview : chosen;
        }

        private void RefreshImages()
        {
            if (refSheetImage != null)
            {
                refSheetImage.image = state.RefSheetPreview;
                refSheetImage.MarkDirtyRepaint();
                // Any letterbox around the sheet blends into it.
                if (ownStage != null) ownStage.style.backgroundColor = state.RefSheetBackground;
            }
            // The views share the thumbnail's render target: the host's thumbnail keeps its last live image meanwhile.
            if (state.RefSheetOpen) return;
            if (bannerImage != null) { bannerImage.image = DisplayTexture(PhotoshootService.ShotKind.Banner); bannerImage.MarkDirtyRepaint(); }
            if (thumbnailImage != null) { thumbnailImage.image = DisplayTexture(PhotoshootService.ShotKind.Thumbnail); thumbnailImage.MarkDirtyRepaint(); }
            options.ThumbnailPreview?.Invoke(DisplayTexture(PhotoshootService.ShotKind.Thumbnail));
        }

        private void RepaintPreview()
        {
            refSheetImage?.MarkDirtyRepaint();
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
                bool plain = options.ServerAppliesBannerEffect && shotKind == PhotoshootService.ShotKind.Banner;
                var text = TextOn && shotKind == PhotoshootService.ShotKind.Thumbnail ? state.Text : null;
                var texture = state.Capture(AvatarRoot, shotKind, size, withShotEffect: !plain, text: text);
                options.SetShot?.Invoke(shotKind, texture);
                // A host that keeps following the live preview after a capture gets a confirmation instead of a Retake.
                state.Status = IsFixed(shotKind) ? null : $"{ShotName(shotKind)} captured";
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
            column.Add(card);
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
