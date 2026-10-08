using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>The warning badge VRChat puts on an avatar card: none, Poor or Very Poor.</summary>
    public enum VrcPerformanceWarning { None, Poor, VeryPoor }

    /// <summary>
    /// A copy of the avatar card in VRChat's in-game menu, measured on the game at 1:1, so a thumbnail is judged where it
    /// will be seen: the card crops the 4:3 image to a wide strip, with platform and performance badges over it and the
    /// name and author below. <see cref="VrcCardStage"/> places it in the menu grid. It knows nothing of the VRChat SDK: the
    /// host says which platforms, which performance warning and which author to show.
    /// </summary>
    public sealed class VrcAvatarCard : VisualElement
    {
        internal const string StyleSheetPath = "Packages/orbiters.toolkit/Editor/UI/vrc-avatar-card.uss";

        // Badges are drawn translucent over the image, as in the game.
        private static readonly Color Pc = new Color(8 / 255f, 147 / 255f, 237 / 255f, .6f);
        private static readonly Color Android = new Color(30 / 255f, 180 / 255f, 88 / 255f, .6f);
        private static readonly Color VeryPoor = new Color(224 / 255f, 82 / 255f, 58 / 255f, .6f);
        private static readonly Color Poor = new Color(236 / 255f, 146 / 255f, 44 / 255f, .6f);
        private static readonly Color BadgeInk = new Color(38 / 255f, 38 / 255f, 38 / 255f, .92f);
        private const float TitleLineHeight = 26f;
        private static Texture2D bodyGradient;

        private readonly Image image;
        private readonly Label empty, live, line1, line2, author;
        private readonly VisualElement pcBadge, androidBadge, performanceBadge, flash;
        private string title = "";
        private VrcPerformanceWarning warning;

        /// <summary>The image strip, e.g. to frame a live camera right on it.</summary>
        public VisualElement Media { get; }

        public VrcAvatarCard(bool placeholder = false)
        {
            AddStyleSheet(this);
            AddToClassList("vrc-card");
            Media = new VisualElement(); Media.AddToClassList("vrc-card__media"); Add(Media);
            var body = new VisualElement(); body.AddToClassList("vrc-card__body"); Add(body);
            body.style.backgroundImage = new StyleBackground(BodyGradient());
            var text = new VisualElement(); text.AddToClassList("vrc-card__text"); body.Add(text);
            var titleBox = new VisualElement(); titleBox.AddToClassList("vrc-card__title"); text.Add(titleBox);
            line1 = TitleLine(titleBox); line2 = TitleLine(titleBox);
            author = new Label(); author.AddToClassList("vrc-card__author"); text.Add(author);
            if (placeholder)
            {
                // Skeleton of a neighbouring card: shapes only, no content.
                AddToClassList("vrc-card--placeholder");
                pickingMode = PickingMode.Ignore;
                line1.AddToClassList("vrc-card__bar"); line2.style.display = DisplayStyle.None;
                author.AddToClassList("vrc-card__bar"); author.AddToClassList("vrc-card__bar--author");
                return;
            }

            image = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore }; image.AddToClassList("vrc-card__image"); Media.Add(image);
            empty = new Label("No thumbnail yet") { pickingMode = PickingMode.Ignore }; empty.AddToClassList("vrc-card__empty"); Media.Add(empty);
            live = new Label("LIVE") { pickingMode = PickingMode.Ignore }; live.AddToClassList("vrc-card__live"); Media.Add(live);
            var badges = new VisualElement { pickingMode = PickingMode.Ignore }; badges.AddToClassList("vrc-card__badges"); Media.Add(badges);
            pcBadge = Badge(DrawWindows); badges.Add(pcBadge);
            androidBadge = Badge(DrawAndroid); badges.Add(androidBadge);
            androidBadge.style.display = DisplayStyle.None;
            performanceBadge = Badge(DrawPerformance); badges.Add(performanceBadge);
            performanceBadge.style.display = DisplayStyle.None;
            flash = new VisualElement { pickingMode = PickingMode.Ignore }; flash.AddToClassList("vrc-card__flash"); Media.Add(flash);
            titleBox.RegisterCallback<GeometryChangedEvent>(_ => LayoutTitle());
            SetAuthor(null);
        }

        /// <summary>What the empty image strip says.</summary>
        public string EmptyText
        {
            get => empty?.text ?? "";
            set { if (empty != null) empty.text = value ?? ""; }
        }

        /// <summary>The avatar's name (up to two lines, as in the game), its thumbnail and whether that is a live camera.</summary>
        public void Show(string name, Texture thumbnail, bool isLive = false)
        {
            image.image = thumbnail;
            image.MarkDirtyRepaint();
            empty.style.display = thumbnail ? DisplayStyle.None : DisplayStyle.Flex;
            live.style.display = thumbnail && isLive ? DisplayStyle.Flex : DisplayStyle.None;
            if (title != (name ?? "")) { title = name ?? ""; LayoutTitle(); }
        }

        /// <summary>"By: <paramref name="displayName"/>", or "By: you" without one.</summary>
        public void SetAuthor(string displayName)
        {
            author.text = "By: " + (string.IsNullOrWhiteSpace(displayName) ? "you" : displayName.Trim());
        }

        /// <summary>The platform badges: Windows for PC, the Android head for Android (Quest).</summary>
        public void SetPlatforms(bool pc, bool android)
        {
            pcBadge.style.display = pc ? DisplayStyle.Flex : DisplayStyle.None;
            androidBadge.style.display = android ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>VRChat's warning badge for Poor and Very Poor avatars; none otherwise.</summary>
        public void SetPerformance(VrcPerformanceWarning value)
        {
            warning = value;
            bool warn = value != VrcPerformanceWarning.None;
            performanceBadge.style.display = warn ? DisplayStyle.Flex : DisplayStyle.None;
            performanceBadge.tooltip = warn ? (value == VrcPerformanceWarning.VeryPoor ? "Very Poor" : "Poor") + " performance rank" : null;
            performanceBadge.MarkDirtyRepaint();
        }

        /// <summary>Shutter feedback for a capture: the image flashes white and fades back.</summary>
        public void Flash()
        {
            flash.AddToClassList("vrc-card__flash--on");
            flash.schedule.Execute(() => flash.RemoveFromClassList("vrc-card__flash--on")).StartingIn(16);
        }

        internal static void AddStyleSheet(VisualElement element)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet && !element.styleSheets.Contains(sheet)) element.styleSheets.Add(sheet);
        }

        private static Label TitleLine(VisualElement parent)
        {
            var line = new Label(); line.AddToClassList("vrc-card__title-line");
            line.style.height = TitleLineHeight;
            parent.Add(line);
            return line;
        }

        // Up to two lines broken between words, as the game does; a longer name ends in an ellipsis.
        private void LayoutTitle()
        {
            float width = line1.parent.contentRect.width;
            if (float.IsNaN(width) || width <= 0f) { line1.text = title; line2.style.display = DisplayStyle.None; return; }
            string[] words = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string first = "";
            int next = 0;
            for (; next < words.Length; next++)
            {
                string candidate = first.Length == 0 ? words[next] : first + " " + words[next];
                if (first.Length > 0 && Measure(candidate) > width) break;
                first = candidate;
            }
            string rest = string.Join(" ", words, next, words.Length - next);
            if (rest.Length > 0 && Measure(rest) > width)
            {
                while (rest.Length > 1 && Measure(rest + "…") > width) rest = rest.Substring(0, rest.Length - 1).TrimEnd();
                rest += "…";
            }
            line1.text = first;
            line2.text = rest;
            line2.style.display = rest.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private float Measure(string text) => line1.MeasureTextSize(text, 0f, MeasureMode.Undefined, 0f, MeasureMode.Undefined).x;

        // Measured on the game: the card body fades from its top colour into the frame colour at the bottom.
        private static Texture2D BodyGradient()
        {
            if (bodyGradient) return bodyGradient;
            bodyGradient = new Texture2D(1, 64, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            Color32 top = new Color32(0x21, 0x24, 0x2d, 0xff), bottom = new Color32(0x2e, 0x32, 0x3e, 0xff);
            // Texture rows start at the bottom.
            for (int y = 0; y < 64; y++) bodyGradient.SetPixel(0, y, Color32.Lerp(bottom, top, y / 63f));
            bodyGradient.Apply(false, true);
            return bodyGradient;
        }

        private VisualElement Badge(Action<Painter2D, Vector2, float> glyph)
        {
            var badge = new VisualElement();
            badge.AddToClassList("vrc-card__badge");
            badge.generateVisualContent += context =>
            {
                var rect = badge.contentRect;
                glyph(context.painter2D, rect.center, Mathf.Min(rect.width, rect.height) * .5f);
            };
            return badge;
        }

        private static void Ring(Painter2D painter, Vector2 centre, float radius, Color color)
        {
            painter.strokeColor = color;
            painter.lineWidth = 2f;
            painter.BeginPath();
            painter.Arc(centre, radius - 1f, 0f, 360f);
            painter.Stroke();
        }

        private static void Rect(Painter2D painter, float left, float top, float width, float height)
        {
            painter.BeginPath();
            painter.MoveTo(new Vector2(left, top)); painter.LineTo(new Vector2(left + width, top));
            painter.LineTo(new Vector2(left + width, top + height)); painter.LineTo(new Vector2(left, top + height));
            painter.ClosePath(); painter.Fill();
        }

        // Ring with the four Windows panes inside.
        private static void DrawWindows(Painter2D painter, Vector2 centre, float radius)
        {
            Ring(painter, centre, radius, Pc);
            painter.fillColor = Pc;
            float pane = radius * .38f, gap = radius * .08f, start = -pane - gap * .5f;
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                Rect(painter, centre.x + start + x * (pane + gap), centre.y + start + y * (pane + gap), pane, pane);
        }

        // Ring with the Android head: a dome with two antennae and two eyes.
        private static void DrawAndroid(Painter2D painter, Vector2 centre, float radius)
        {
            Ring(painter, centre, radius, Android);
            var head = centre + new Vector2(0f, radius * .28f);
            float dome = radius * .56f;
            painter.fillColor = Android;
            painter.BeginPath();
            painter.Arc(head, dome, 180f, 360f);
            painter.ClosePath(); painter.Fill();
            painter.strokeColor = Android;
            painter.lineWidth = 1.2f;
            foreach (float side in new[] { -1f, 1f })
            {
                painter.BeginPath();
                painter.MoveTo(head + new Vector2(side * dome * .5f, -dome * .78f));
                painter.LineTo(head + new Vector2(side * dome * .78f, -dome * 1.18f));
                painter.Stroke();
            }
            painter.fillColor = BadgeInk;
            foreach (float side in new[] { -1f, 1f })
            {
                painter.BeginPath();
                painter.Arc(head + new Vector2(side * dome * .42f, -dome * .42f), radius * .07f, 0f, 360f);
                painter.Fill();
            }
        }

        // Outer ring, a thin dark gap, a filled disc and a dark exclamation mark.
        private void DrawPerformance(Painter2D painter, Vector2 centre, float radius)
        {
            Color color = warning == VrcPerformanceWarning.VeryPoor ? VeryPoor : Poor;
            Ring(painter, centre, radius, color);
            painter.strokeColor = BadgeInk;
            painter.lineWidth = 1f;
            painter.BeginPath(); painter.Arc(centre, radius - 2.5f, 0f, 360f); painter.Stroke();
            painter.fillColor = color;
            painter.BeginPath(); painter.Arc(centre, radius - 3f, 0f, 360f); painter.Fill();
            painter.fillColor = BadgeInk;
            float bar = radius * .2f;
            Rect(painter, centre.x - bar * .5f, centre.y - radius * .52f, bar, radius * .62f);
            painter.BeginPath(); painter.Arc(centre + new Vector2(0f, radius * .38f), bar * .62f, 0f, 360f); painter.Fill();
        }
    }

    /// <summary>
    /// The card in its menu context: the dark menu backdrop with faint neighbouring cards cut by the edges. An optional
    /// <see cref="SetAmbient">ambient</see> washes the backdrop in the thumbnail's colours, like a light from the card.
    /// </summary>
    public sealed class VrcCardStage : VisualElement
    {
        private readonly VisualElement ambient;
        private Texture2D ambientTexture;

        public VrcCardStage(VrcAvatarCard card)
        {
            VrcAvatarCard.AddStyleSheet(this);
            AddToClassList("vrc-stage");
            ambient = new VisualElement { pickingMode = PickingMode.Ignore };
            ambient.AddToClassList("vrc-stage__ambient");
            ambient.style.display = DisplayStyle.None;
            Add(ambient);
            var row = new VisualElement(); row.AddToClassList("vrc-stage__row"); Add(row);
            row.Add(new VrcAvatarCard(true));
            row.Add(card);
            row.Add(new VrcAvatarCard(true));
            RegisterCallback<DetachFromPanelEvent>(_ => ReleaseAmbient());
        }

        /// <summary>
        /// Lights the backdrop with a soft, blurred copy of <paramref name="thumbnail"/> (null turns it off). The copy is a
        /// few pixels big and stretched with bilinear filtering, so it costs nothing to draw.
        /// </summary>
        public void SetAmbient(Texture thumbnail)
        {
            ReleaseAmbient();
            ambientTexture = thumbnail ? AmbientWash(thumbnail) : null;
            ambient.style.backgroundImage = ambientTexture ? new StyleBackground(ambientTexture) : new StyleBackground(StyleKeyword.None);
            ambient.style.display = ambientTexture ? DisplayStyle.Flex : DisplayStyle.None;
            EnableInClassList("vrc-stage--ambient", ambientTexture);
        }

        private void ReleaseAmbient()
        {
            if (ambientTexture) UnityEngine.Object.DestroyImmediate(ambientTexture);
            ambientTexture = null;
        }

        // The thumbnail averaged down to 8 × 6 cells.
        private static Texture2D AmbientWash(Texture source)
        {
            const int Width = 8, Height = 6, Sample = 64;
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(Sample, Sample * 3 / 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Color[] read;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var copy = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                read = copy.GetPixels();
                UnityEngine.Object.DestroyImmediate(copy);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }

            int sourceWidth = Sample, sourceHeight = Sample * 3 / 4;
            var wash = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                Color sum = Color.clear;
                int count = 0;
                for (int sy = y * sourceHeight / Height; sy < (y + 1) * sourceHeight / Height; sy++)
                for (int sx = x * sourceWidth / Width; sx < (x + 1) * sourceWidth / Width; sx++)
                {
                    sum += read[sy * sourceWidth + sx];
                    count++;
                }
                Color cell = count > 0 ? sum / count : Color.black;
                cell.a = 1f;
                wash.SetPixel(x, y, cell);
            }
            wash.Apply(false, true);
            return wash;
        }
    }
}
