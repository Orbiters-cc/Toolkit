using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    // The thumbnail's line of text: the Text tab (the line, its font, look, colour and size) and the text layers on the
    // previews (PhotoshootTextLayer), kept in step. Captured thumbnails get the text drawn in (PhotoshootTextRenderer).
    public sealed partial class PhotoshootPanel
    {
        private static readonly Color[] TextColors =
        {
            Color.white, new Color(.07f, .07f, .09f), new Color(1f, .83f, .3f), new Color(1f, .36f, .52f), new Color(.3f, .85f, 1f), new Color(0f, .855f, .427f),
        };

        private TextField textField;
        private Label textPlaceholder;
        private readonly List<(Button tile, Label sample, string font)> fontTiles = new List<(Button, Label, string)>();
        private readonly List<(Button chip, Image preview, PhotoshootTextStyle style)> lookChips = new List<(Button, Image, PhotoshootTextStyle)>();
        private readonly List<(Button swatch, Color color)> textSwatches = new List<(Button, Color)>();
        private PhotoshootSlider textSize;
        private Label textSizeValue;

        private bool TextOn => options.TextFonts != null && options.TextFonts.Count > 0 && !state.RefSheetOpen;

        private string[] Tabs => TextOn ? StyleTabs.Concat(new[] { "Text" }).ToArray() : StyleTabs;

        // From the layers (dragging, writing on the picture) or the tab: both show the same text.
        private void TextChanged(bool done)
        {
            foreach (var layer in textLayers.Values) layer.Refresh();
            if (state.StyleTab == TextTab) UpdateTextControls();
            if (done) options.TextChanged?.Invoke(state.Text);
        }

        private void BuildTextTab(VisualElement grid)
        {
            ReleaseLookPreviews();
            fontTiles.Clear(); textSwatches.Clear();
            var text = state.Text;

            // The line, written here or right on the picture.
            var line = new VisualElement();
            line.AddToClassList("ps-text-line");
            textField = new TextField { value = text.value };
            textField.AddToClassList("ps-text-line__field");
            textPlaceholder = new Label("Write a line on the picture…") { pickingMode = PickingMode.Ignore };
            textPlaceholder.AddToClassList("ps-text-line__placeholder");
            textField.Add(textPlaceholder);
            textField.RegisterValueChangedCallback(evt =>
            {
                text.value = evt.newValue;
                TextChanged(false);
            });
            textField.RegisterCallback<FocusOutEvent>(_ => TextChanged(true));
            line.Add(textField);
            var clear = CreateButton(null, () =>
            {
                text.value = "";
                textField.SetValueWithoutNotify("");
                TextChanged(true);
            }, "ps-text-line__clear");
            clear.tooltip = "Remove the text";
            var cross = new VectorIcon(IconGlyph.Close) { pickingMode = PickingMode.Ignore };
            cross.AddToClassList("ps-text-line__clear-icon");
            clear.Add(cross);
            line.Add(clear);
            styleContent.Insert(0, line);

            // Every font shows the line itself, so the choice is made by looking.
            grid.AddToClassList("ps-grid--fonts");
            foreach (string path in options.TextFonts)
            {
                string font = path ?? "";
                var loaded = PhotoshootText.LoadFont(font);
                var tile = CreateButton(null, () =>
                {
                    text.font = font;
                    if (text.IsEmpty) { text.value = "Your text"; textField.SetValueWithoutNotify(text.value); }
                    TextChanged(true);
                }, "ps-font");
                tile.tooltip = PhotoshootText.FamilyName(loaded);
                var sample = new Label { pickingMode = PickingMode.Ignore };
                sample.AddToClassList("ps-font__sample");
                sample.style.unityFontDefinition = FontDefinition.FromFont(loaded);
                tile.Add(sample);
                var name = new Label(PhotoshootText.FamilyName(loaded)) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("ps-font__name");
                tile.Add(name);
                grid.Add(tile);
                fontTiles.Add((tile, sample, font));
            }

            // The look, each previewed with the line's own font and colour.
            styleContent.Add(Heading("Look"));
            var looks = new VisualElement();
            looks.AddToClassList("ps-grid");
            looks.AddToClassList("ps-grid--text-looks");
            foreach (PhotoshootTextStyle style in Enum.GetValues(typeof(PhotoshootTextStyle)))
            {
                var value = style;
                var chip = CreateButton(null, () => { text.style = value; TextChanged(true); }, "ps-text-look");
                chip.tooltip = LookTooltip(value);
                var preview = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                preview.AddToClassList("ps-text-look__preview");
                chip.Add(preview);
                var name = new Label(ObjectNames.NicifyVariableName(value.ToString())) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("ps-text-look__name");
                chip.Add(name);
                looks.Add(chip);
                lookChips.Add((chip, preview, value));
            }
            styleContent.Add(looks);

            // Colours: a few that read well, then the shot's own, then any.
            styleContent.Add(Heading("Colour"));
            var colors = new VisualElement();
            colors.AddToClassList("ps-text-colors");
            foreach (var color in TextColors.Concat(ShotColors()))
            {
                var value = color;
                var swatch = CreateButton(null, () => { text.color = value; TextChanged(true); }, "ps-text-color");
                swatch.style.backgroundColor = value;
                swatch.tooltip = "#" + ColorUtility.ToHtmlStringRGB(value);
                colors.Add(swatch);
                textSwatches.Add((swatch, value));
            }
            styleContent.Add(colors);
            colorPicker = new InlineColorPicker(text.color, Color.white, color => { text.color = color; TextChanged(false); });
            colorPicker.RegisterCallback<PointerUpEvent>(_ => options.TextChanged?.Invoke(state.Text));
            styleContent.Add(colorPicker);

            var size = SliderRow("Size", "How big the letters are. You can also drag the text's corner or scroll over it on the picture.",
                PhotoshootText.MinSize, PhotoshootText.MaxSize, text.size, false, value => Mathf.RoundToInt(value * 100f) + "%",
                value => { text.size = value; TextChanged(false); }, "ps-effect--on ps-text-size");
            textSize = size.Q<PhotoshootSlider>();
            textSizeValue = size.Q<Label>(className: "ps-effect__value");
            size.RegisterCallback<PointerUpEvent>(_ => options.TextChanged?.Invoke(state.Text));
            styleContent.Add(size);
            styleContent.RegisterCallback<DetachFromPanelEvent>(_ => ReleaseLookPreviews());
        }

        private static Label Heading(string text)
        {
            var label = new Label(text);
            label.AddToClassList("ps-text-heading");
            return label;
        }

        private static string LookTooltip(PhotoshootTextStyle style) =>
            style == PhotoshootTextStyle.Clean ? "Just the letters"
            : style == PhotoshootTextStyle.Outline ? "A contrasting edge: reads on any picture"
            : style == PhotoshootTextStyle.Shadow ? "A soft shadow under the letters"
            : "Neon: the letters glow in their own colour";

        private void UpdateTextControls()
        {
            var text = state.Text;
            if (textField != null && textField.value != text.value && textField.focusController?.focusedElement != textField) textField.SetValueWithoutNotify(text.value);
            if (textPlaceholder != null) textPlaceholder.style.display = string.IsNullOrEmpty(text.value) ? DisplayStyle.Flex : DisplayStyle.None;
            string sample = text.IsEmpty ? "Aa" : text.value.Length > 14 ? text.value.Substring(0, 13) + "…" : text.value;
            foreach (var (tile, label, font) in fontTiles)
            {
                label.text = sample;
                tile.EnableInClassList("ps-font--selected", font == (text.font ?? ""));
            }
            foreach (var (swatch, color) in textSwatches) swatch.EnableInClassList("ps-text-color--selected", Same(color, text.color));
            if (textSize != null) textSize.SetValueWithoutNotify(text.size);
            if (textSizeValue != null) textSizeValue.text = Mathf.RoundToInt(text.size * 100f) + "%";
            RenderLookPreviews();
            styleCaption.text = text.IsEmpty ? "Add a line of text: write it here or press “Add text” on the picture."
                : "Drag the text on the picture to move it, drag its corner or scroll over it to resize it.";
        }

        private static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < .01f;

        private string lookKey;

        private void RenderLookPreviews()
        {
            var text = state.Text;
            string key = text.font + "|" + ColorUtility.ToHtmlStringRGB(text.color) + "|" + text.style;
            foreach (var (chip, _, style) in lookChips) chip.EnableInClassList("ps-text-look--selected", style == text.style);
            if (key == lookKey) return;
            lookKey = key;
            foreach (var (_, preview, style) in lookChips)
            {
                var look = text.Clone();
                look.value = "Aa"; look.style = style; look.center = new Vector2(.5f, .5f); look.size = .55f;
                if (preview.image) UnityEngine.Object.DestroyImmediate(preview.image);
                preview.image = PhotoshootTextRenderer.Render(look, 160, 80, out _);
            }
        }

        private void ReleaseLookPreviews()
        {
            foreach (var (_, preview, _) in lookChips)
                if (preview.image) UnityEngine.Object.DestroyImmediate(preview.image);
            lookChips.Clear();
            lookKey = null;
        }

        // The shot's own vivid colours (up to three distinct hues), so the text can match the avatar.
        private IEnumerable<Color> ShotColors()
        {
            var source = state.GetPreviewTexture(PhotoshootService.ShotKind.Thumbnail);
            if (source == null) return Enumerable.Empty<Color>();
            const int w = 24, h = 18;
            var small = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(source, small);
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var bins = new Dictionary<int, (float weight, Color sum)>();
                foreach (var pixel in read.GetPixels())
                {
                    Color.RGBToHSV(pixel, out float hue, out float sat, out float val);
                    if (sat < .35f || val < .3f) continue;
                    int bin = Mathf.FloorToInt(hue * 12f) % 12;
                    bins.TryGetValue(bin, out var entry);
                    bins[bin] = (entry.weight + sat * val, entry.sum + pixel * (sat * val));
                }
                return bins.OrderByDescending(b => b.Value.weight).Take(3).Select(b =>
                {
                    var mean = b.Value.sum / b.Value.weight;
                    Color.RGBToHSV(mean, out float hue, out float sat, out float val);
                    return Color.HSVToRGB(hue, Mathf.Max(sat, .55f), Mathf.Max(val, .85f));
                }).ToList();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(small);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }
    }
}
