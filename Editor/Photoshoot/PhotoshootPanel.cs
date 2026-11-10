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
