using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's customizable parts (My Avatar's Customize, <see cref="ItemParts"/>): its menu theme (Modern and Retro), its
    /// bot model and the language of its menu (English, French, Spanish, Portuguese, Japanese), declared in
    /// <c>N4N0.parts.json</c> beside the prefab with their base files in "N4N0 parts". The theme and the language reach the
    /// avatar through the <see cref="MenuRenderer"/>: Orbiters - N4N0 on this computer draws N4N0's menu pages for them
    /// (<c>orbiters-desktop --orb-menu &lt;out&gt; --theme … --strings …</c>, in the background), and the screen gets its
    /// own materials with those pictures, the theme's look (modern or retro) and its screen colours. Without Orbiters -
    /// N4N0, the screen keeps the shipped pictures (English, Modern or Retro) and the card says so.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>The renderer N4N0's theme and language parts name.</summary>
        public const string MenuRenderer = "orbiters-n4n0-menu";
        public const string PartsFolderName = "N4N0 parts";
        /// <summary>The renderers N4N0's model part puts a variant's model in: the PC body and the phone's, which share the model.</summary>
        internal static readonly string[] ModelSlotPaths = { Body, MobileBody };
        internal static readonly string[] PartShaders = { "VRChat/Mobile/Standard Lite", "VRChat/Mobile/Toon Lit", "VRChat/Mobile/Toon Standard", "VRChat/Mobile/MatCap Lit", "VRChat/Mobile/Diffuse" };
        // N4N0's base themes and translations, as Orbiters - N4N0 ships them (crates/orbiters-orb/menu-themes and
        // menu-strings: what its --theme and --strings read, the English one written by --orb-menu-strings with each text's
        // longest length), copied into N4N0's folder.
        private const string PartsTemplates = "Packages/orbiters.toolkit/Editor/VRChat/N4N0 parts";
        internal static readonly (string file, string name)[] Themes = { ("theme-modern.json", "Modern"), ("theme-retro.json", "Retro") };
        internal static readonly (string file, string name)[] Translations =
        {
            ("strings-en.json", "English"), ("strings-fr.json", "Français"), ("strings-es.json", "Español"), ("strings-pt.json", "Português (Brasil)"), ("strings-ja.json", "日本語"),
        };
        /// <summary>The oldest Orbiters - N4N0 that draws N4N0's menu for a theme and a language (--theme, --strings).</summary>
        internal const string ThemedMenuVersion = "0.3.2-dev.3";

        // The theme's fields (what Orbiters - N4N0's --theme reads, flattened), their defaults the Modern theme's.
        private static readonly (string key, string label)[] ThemeFields =
        {
            ("colors.accent", "Accent"), ("colors.background", "Background"), ("colors.surface", "Surface"), ("colors.text", "Text"),
            ("colors.muted", "Muted text"), ("colors.green", "Green"), ("colors.red", "Red"), ("colors.blue", "Blue"), ("colors.violet", "Violet"),
            ("colors.cyan", "Cyan"), ("colors.amber", "Amber"), ("screen.tint", "Screen tint"), ("screen.edge", "Screen edge"),
        };

        // ---- In the prefab ----

        /// <summary>
        /// Writes N4N0's parts file (<c>N4N0.parts.json</c>) and its base variants (themes, the model, the translations,
        /// and pictures of those it can draw here) into N4N0's folder.
        /// </summary>
        private static void AddParts(Transform root, string folder)
        {
            string parts = folder + "/" + PartsFolderName;
            ItemParts.EnsureFolder(parts);
            foreach (var (file, _) in Themes.Concat(Translations).Where(t => Template(t.file) != null)) CopyFile(PartsTemplates + "/" + file, parts + "/" + file);
            Write(parts + "/model-n4n0.json", new JObject { ["schema"] = 1, ["part"] = "model", ["name"] = "N4N0", ["model"] = new JObject() }.ToString(Formatting.Indented));
            Write(folder + "/" + Path.GetFileNameWithoutExtension(PrefabName) + ItemParts.Extension, PartsFile());
            // The model's picture, drawn from it as built. The themes' and languages' are N4N0's menu as Orbiters - N4N0 draws
            // it: drawn when shown (My Avatar's Customize keeps them in Library) and when the gallery's publishing posts them.
            try
            {
                var body = root.Find(Body);
                var picture = body != null && body.TryGetComponent(out Renderer renderer) ? ItemParts.RenderPicture(new[] { renderer }, ItemParts.ThumbnailSize, 0, true) : null;
                if (picture != null)
                {
                    try { Picture(parts + "/model-n4n0.png", picture.EncodeToPNG()); }
                    finally { Object.DestroyImmediate(picture); }
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Debug.LogWarning("[Orbiters] N4N0's base variant pictures were not drawn: " + e.Message); }
        }

        private static TextAsset Template(string file) => AssetDatabase.LoadAssetAtPath<TextAsset>(PartsTemplates + "/" + file);

        private static JObject TemplateJson(string file)
        {
            var text = Template(file);
            try { return text != null ? JObject.Parse(text.text) : null; }
            catch (JsonException) { return null; }
        }

        private static void Write(string path, string text)
        {
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void Picture(string path, byte[] png)
        {
            if (png == null) return;
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static byte[] Encoded(Texture texture) => texture is Texture2D picture && picture.isReadable ? picture.EncodeToPNG() : null;

        private static Color Hex(string text) => ItemParts.TryColor(text, out var colour) ? (Color)colour : Color.black;

        /// <summary>N4N0's parts file: its theme (fields defaulting to the Modern theme), model and language parts.</summary>
        internal static string PartsFile()
        {
            var modern = TemplateJson(Themes[0].file) ?? new JObject();
            var fields = new JArray { new JObject { ["key"] = "style", ["label"] = "Style", ["type"] = "choice", ["options"] = new JArray("modern", "retro"), ["default"] = "modern" } };
            foreach (var (key, label) in ThemeFields)
                fields.Add(new JObject { ["key"] = key, ["label"] = label, ["type"] = "color", ["default"] = ((string)modern.SelectToken(key) ?? "#FFFFFF").ToUpperInvariant() });
            fields.Add(new JObject { ["key"] = "screen.glow", ["label"] = "Screen glow", ["type"] = "number", ["min"] = 0, ["max"] = 2, ["default"] = modern.SelectToken("screen.glow")?.Value<double>() ?? 1 });
            var translations = Translations.Where(t => Template(t.file) != null).Select(t => t.file).ToList();
            var parts = new JArray
            {
                new JObject
                {
                    ["key"] = "theme", ["label"] = "Menu theme", ["description"] = "Colours and look of N4N0's menu.", ["kind"] = "values", ["fields"] = fields,
                    ["renderer"] = MenuRenderer,
                    ["base"] = new JArray(Themes.Select(t => Base(t.name, PartsFolderName + "/" + t.file))),
                },
                new JObject
                {
                    ["key"] = "model", ["label"] = "Bot model", ["description"] = "N4N0's body: its mesh and materials, the same on PC and phones.", ["kind"] = "model",
                    ["slots"] = new JArray(new JObject { ["key"] = "body", ["label"] = "N4N0", ["paths"] = new JArray(ModelSlotPaths) }),
                    ["blendshapes"] = new JArray(ModelShapes), ["maxTriangles"] = 3000, ["maxTextureSize"] = 1024, ["maxRadius"] = 1.2,
                    ["shaders"] = new JArray(PartShaders),
                    ["base"] = new JArray(Base("N4N0", PartsFolderName + "/model-n4n0.json")),
                },
            };
            if (translations.Count > 0)
            {
                var bases = new JArray();
                foreach (var (file, name) in Translations.Where(t => translations.Contains(t.file)))
                {
                    var entry = Base(name, PartsFolderName + "/" + file);
                    string language = (string)TemplateJson(file)?["language"];
                    if (!string.IsNullOrEmpty(language)) entry["language"] = language;
                    bases.Add(entry);
                }
                parts.Add(new JObject
                {
                    ["key"] = "translation", ["label"] = "Language", ["description"] = "The language of N4N0's menu.", ["kind"] = "strings",
                    ["strings"] = PartsFolderName + "/" + translations[0], ["renderer"] = MenuRenderer, ["base"] = bases,
                });
            }
            return new JObject { ["schema"] = 1, ["parts"] = parts }.ToString(Formatting.Indented) + "\n";
        }

        private static JObject Base(string name, string file) => new JObject { ["name"] = name, ["file"] = file };

        // ---- The renderer ----

        [InitializeOnLoadMethod]
        private static void RegisterMenuRenderer() => ItemParts.RegisterRenderer(MenuRenderer, new ItemParts.PartRenderer
        {
            Anchors = item => new Renderer[] { ScreenRenderer(OrbOf(item)), CardRenderer(OrbOf(item)) }.Where(r => r != null),
            Render = RenderMenuAsync,
            Note = item => MenuNote(),
        });

        private static Transform OrbOf(Transform item) => item == null ? null : FindAll(item.gameObject).FirstOrDefault() ?? item;

        private static SkinnedMeshRenderer CardRenderer(Transform orb)
        {
            var card = orb != null ? orb.Find(CardModel) : null;
            return card != null && card.TryGetComponent(out SkinnedMeshRenderer renderer) ? renderer : null;
        }

        /// <summary>A build of Orbiters - N4N0 to draw with instead of the installed one (tests, a development build), or null.</summary>
        internal static string DesktopAppOverride;

        // The page N4N0's menu shows first (Home with ten places, page 16): the thumbnail of a theme or a language.
        private const int HomePage = 16;

        /// <summary>Orbiters - N4N0 on this computer (its install folder from its uninstall entry, else the default one), or null.</summary>
        internal static string DesktopApp()
        {
            if (!string.IsNullOrEmpty(DesktopAppOverride) && File.Exists(DesktopAppOverride)) return DesktopAppOverride;
            string location = null;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OrbitersDesktop"))
                    location = key?.GetValue("InstallLocation") as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { location = null; }
            if (string.IsNullOrEmpty(location))
                location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Orbiters - N4N0");
            string exe = Path.Combine(location.Trim('"'), "orbiters-desktop.exe");
            return File.Exists(exe) ? exe : null;
        }

        // Its version, from the install record beside it.
        internal static string DesktopVersion(string exe)
        {
            try
            {
                string record = Path.Combine(Path.GetDirectoryName(exe) ?? "", "install.json");
                return File.Exists(record) ? (string)JObject.Parse(File.ReadAllText(record))["version"] : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException) { return null; }
        }

        private static string MenuNote()
        {
            string exe = DesktopApp();
            if (exe == null) return "Orbiters - N4N0 isn't on this computer: N4N0's menu keeps its English pictures in the Modern or Retro look. Install it to see the menu in your theme and language.";
            string version = DesktopVersion(exe);
            if (version != null && CompareVersions(version, ThemedMenuVersion) < 0)
                return "Orbiters - N4N0 " + version + " can't draw N4N0's menu in a theme or language yet: update it (Settings › About) to see them.";
            return null;
        }

        private static async Task RenderMenuAsync(ItemParts.RenderJob job)
        {
            var theme = job.Parts.Values.FirstOrDefault(p => p.part.kind == ItemParts.Values);
            var strings = job.Parts.Values.FirstOrDefault(p => p.part.kind == ItemParts.Strings);
            var themeJson = ThemeJson(theme.part, theme.variant);
            bool retro = (string)themeJson["style"] == "retro";
            var stringsJson = strings.variant != null ? new JObject { ["schema"] = 1, ["language"] = strings.variant.language ?? "en", ["strings"] = JObject.FromObject(strings.variant.strings ?? new Dictionary<string, string>()) } : null;

            // Orbiters - N4N0 draws the pages, kept by what it was asked (and its version) in Library.
            string exe = DesktopApp();
            string version = exe != null ? DesktopVersion(exe) : null;
            bool themed = exe != null && (version == null || CompareVersions(version, ThemedMenuVersion) >= 0);
            string drawn = null;
            if (themed)
            {
                string key = ItemParts.ShortHash(themeJson.ToString(Formatting.None) + "|" + stringsJson?.ToString(Formatting.None) + "|" + version + "|" + new FileInfo(exe).LastWriteTimeUtc.Ticks);
                drawn = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Orbiters", "PartVariants", "Menu", key);
                if (!File.Exists(Path.Combine(drawn, "N4N0 screen layout.png")))
                {
                    job.Progress("Orbiters - N4N0 is drawing N4N0's menu…");
                    string error = await DrawMenuAsync(exe, drawn, themeJson, stringsJson);
                    if (error != null) { job.Note = "Orbiters - N4N0 could not draw N4N0's menu (" + error + "): it keeps its shipped pictures."; drawn = null; }
                }
            }
            else job.Note = MenuNote();

            // With a theme, Orbiters - N4N0 writes that style's pages (whichever the style), the layout and the screen
            // shader's colours the pages were drawn with.
            string pagesFile = drawn != null && File.Exists(Path.Combine(drawn, "N4N0 screen pages.png")) ? Path.Combine(drawn, "N4N0 screen pages.png") : null;
            string colorsFile = drawn != null && File.Exists(Path.Combine(drawn, "N4N0 screen colors.json")) ? Path.Combine(drawn, "N4N0 screen colors.json") : null;
            if (job.ThumbnailFor != null)
            {
                // The menu's Home in that theme and language.
                var bytes = (pagesFile != null ? Slice(File.ReadAllBytes(pagesFile), HomePage) : null) ?? Encoded(LookPictures()[retro ? 1 : 0]);
                job.Thumbnail = ItemParts.Fit(bytes, ItemParts.ThumbnailSize, Hex((string)themeJson["colors"]?["background"] ?? "#101012"));
                return;
            }

            var orb = OrbOf(job.Item);
            var renderer = ScreenRenderer(orb);
            if (renderer == null) { job.Problems.Add(HasScreen(orb) ? OlderScreen : "This N4N0 has no screen: choose its options again."); return; }
            int thumbnailBytes = OptionsOf(orb).thumbnailBytes;
            var (_, shared) = ScreenAssets(ScreenFolder, thumbnailBytes, retro);
            string folder = job.Folder + "/menu " + ItemParts.ShortHash(themeJson.ToString(Formatting.None) + "|" + stringsJson?.ToString(Formatting.None) + "|" + (drawn != null ? Path.GetFileName(drawn) : "shipped") + "|" + thumbnailBytes);
            ItemParts.EnsureFolder(folder);
            job.Made.Add(folder);
            var pages = shared.GetTexture("_Pages");
            var layout = shared.GetTexture("_Layout");
            if (pagesFile != null)
            {
                var drawnPages = DrawnPages(folder, pagesFile, job);
                var drawnLayout = DrawnLayout(folder, Path.Combine(drawn, "N4N0 screen layout.png"), job);
                if (drawnPages != null && drawnLayout != null) { pages = drawnPages; layout = drawnLayout; }
                else job.Note = "Orbiters - N4N0 " + version + " draws another version of N4N0's menu than this N4N0's screen: N4N0 keeps its shipped pictures (update both).";
            }
            var material = new Material(shared) { name = "N4N0 screen" };
            material.SetTexture("_Pages", pages);
            material.SetTexture("_Layout", layout);
            SetThemeColours(material, themeJson, retro);
            var drawnColours = pages != shared.GetTexture("_Pages") ? DrawnColours(colorsFile) : null;
            if (drawnColours != null) SetDrawnColours(material, drawnColours);
            string materialPath = folder + "/N4N0 screen " + thumbnailBytes + " B.mat";
            SaveAsset(material, materialPath);
            job.Made.Add(materialPath);
            var accent = drawnColours?["shader"]?["_Edge"] is JValue drawnEdge && ItemParts.TryColor((string)drawnEdge, out var edge) ? (Color)edge
                : Hex((string)themeJson["screen"]?["edge"] ?? (string)themeJson["colors"]?["accent"] ?? "#00DA6D");
            var beam = new Material(ScreenBeam(ScreenFolder, retro)) { name = "N4N0 screen beam" };
            beam.SetColor("_EdgeColor", accent);
            string beamPath = folder + "/N4N0 screen beam.mat";
            SaveAsset(beam, beamPath);
            job.Made.Add(beamPath);
            var ray = new Material(ScreenRay(ScreenFolder, retro)) { name = "N4N0 ray" };
            ray.SetColor("_Color", Hex((string)themeJson["colors"]?["accent"] ?? "#00DA6D"));
            string rayPath = folder + "/N4N0 ray.mat";
            SaveAsset(ray, rayPath);
            job.Made.Add(rayPath);
            AssetDatabase.SaveAssets();
            var screenMaterials = new[] { AssetDatabase.LoadAssetAtPath<Material>(materialPath), AssetDatabase.LoadAssetAtPath<Material>(beamPath), AssetDatabase.LoadAssetAtPath<Material>(rayPath) };
            job.Assign = () =>
            {
                foreach (var target in new[] { ScreenRenderer(orb), CardRenderer(orb) }.Where(r => r != null))
                {
                    Undo.RecordObject(target, "N4N0 menu");
                    target.sharedMaterials = screenMaterials;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                }
            };
        }

        private static void SaveAsset(Material material, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(material, existing);
                existing.name = Path.GetFileNameWithoutExtension(path);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(material);
            }
            else AssetDatabase.CreateAsset(material, path);
        }

        // "N4N0 screen colors.json": { schema, style, shader: { _Card, _Text, … }, glow }, the colours the pages were drawn with.
        private static JObject DrawnColours(string file)
        {
            try { return file != null ? JObject.Parse(File.ReadAllText(file)) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException) { return null; }
        }

        private static void SetDrawnColours(Material material, JObject colours)
        {
            if (colours["shader"] is JObject shader)
                foreach (var property in shader.Properties())
                    if (property.Value.Type == JTokenType.String && ItemParts.TryColor((string)property.Value, out var colour) && material.HasProperty(property.Name))
                        material.SetColor(property.Name, colour);
            if (colours["glow"] is JValue glow && (glow.Type == JTokenType.Float || glow.Type == JTokenType.Integer) && material.HasProperty("_Glow"))
                material.SetFloat("_Glow", Mathf.Clamp(glow.Value<float>(), 0, 2));
        }

        // A page of the pages picture (1024 × 576 slices, slice 0 at the top) as a PNG, or null.
        private static byte[] Slice(byte[] pages, int page)
        {
            var whole = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var slice = new Texture2D(1024, 576, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!whole.LoadImage(pages) || whole.width != 1024 || whole.height < 576 * (page + 1)) return null;
                // Unity's rows start at the bottom.
                slice.SetPixels(whole.GetPixels(0, whole.height - 576 * (page + 1), 1024, 576));
                slice.Apply();
                return slice.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(whole); Object.DestroyImmediate(slice); }
        }

        /// <summary>The theme a theme variant stands for, as Orbiters - N4N0's --theme reads it (its fields' defaults filling the rest).</summary>
        internal static JObject ThemeJson(ItemParts.Part part, PartVariant variant)
        {
            var theme = new JObject { ["schema"] = 1, ["style"] = "modern", ["colors"] = new JObject(), ["screen"] = new JObject() };
            void Put(string key, JToken value)
            {
                var path = key.Split('.');
                JObject at = theme;
                for (int i = 0; i < path.Length - 1; i++)
                {
                    if (!(at[path[i]] is JObject inner)) at[path[i]] = inner = new JObject();
                    at = inner;
                }
                at[path[path.Length - 1]] = value;
            }
            foreach (var field in part?.fields ?? new List<ItemParts.Field>())
                if (field?.defaultValue != null) Put(field.key, field.defaultValue);
            foreach (var property in variant?.values?.Properties() ?? Enumerable.Empty<JProperty>()) Put(property.Name, property.Value);
            return theme;
        }

        // The theme's colours on the screen's shader: what changes on the pages is drawn in them, and the projection's plate,
        // edge and glow (Docs/orb.md, Screen).
        private static void SetThemeColours(Material material, JObject theme, bool retro)
        {
            void Colour(string property, string group, string key)
            {
                if (theme[group]?[key] is JValue value && ItemParts.TryColor((string)value, out var colour) && material.HasProperty(property)) material.SetColor(property, colour);
            }
            material.SetFloat(RetroProperty, retro ? 1 : 0);
            Colour("_Card", "colors", "background");
            Colour("_Text", "colors", "text");
            Colour("_Text2", "colors", "muted");
            Colour("_Accent", "colors", "accent");
            Colour("_Placeholder", "colors", "surface");
            Colour("_Edge", "screen", "edge");
            if (theme["screen"]?["tint"] is JValue tint && ItemParts.TryColor((string)tint, out var plate))
            {
                // The plate's see-through amount is the tint's alpha when it gives one.
                float alpha = ((string)tint).Length == 9 ? plate.a / 255f : material.GetColor("_Plate").a;
                material.SetColor("_Plate", new Color(plate.r / 255f, plate.g / 255f, plate.b / 255f, alpha));
            }
            if (theme["screen"]?["glow"] is JValue glow && (glow.Type == JTokenType.Float || glow.Type == JTokenType.Integer) && material.HasProperty("_Glow"))
                material.SetFloat("_Glow", Mathf.Clamp(glow.Value<float>(), 0, 2));
            if (retro && theme["colors"]?["accent"] is JValue accent && ItemParts.TryColor((string)accent, out var a))
            {
                // The retro look's dim and hot shades follow its accent, as its phosphor palette does.
                material.SetColor("_Dim", Color.Lerp(Color.black, (Color)a, .38f));
                material.SetColor("_Hot", Color.Lerp((Color)a, Color.white, .75f));
            }
        }

        // Runs Orbiters - N4N0 in the background (never on the editor's main thread); null once it drew, else why not.
        private static async Task<string> DrawMenuAsync(string exe, string folder, JObject theme, JObject strings)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string themeFile = Path.Combine(folder, "theme.json"), stringsFile = Path.Combine(folder, "strings.json");
                File.WriteAllText(themeFile, theme.ToString(Formatting.Indented));
                string arguments = "--orb-menu " + Quote(folder) + " --theme " + Quote(themeFile);
                if (strings != null)
                {
                    File.WriteAllText(stringsFile, strings.ToString(Formatting.Indented));
                    arguments += " --strings " + Quote(stringsFile);
                }
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder,
                };
                var exit = await Task.Run(() =>
                {
                    using (var process = Process.Start(start))
                    {
                        if (process == null) return (code: -1, error: "it did not start");
                        var output = process.StandardError.ReadToEndAsync();
                        process.StandardOutput.ReadToEnd();
                        if (!process.WaitForExit(120000)) { try { process.Kill(); } catch (InvalidOperationException) { } return (code: -1, error: "it took over two minutes"); }
                        return (code: process.ExitCode, error: output.Result);
                    }
                });
                if (exit.code != 0)
                {
                    string why = (exit.error ?? "").Trim();
                    return "exit " + exit.code + (why.Length > 0 ? ": " + why.Split('\n').Last().Trim() : "");
                }
                return File.Exists(Path.Combine(folder, "N4N0 screen layout.png")) ? null : "it wrote no pages";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                return e.Message;
            }
        }

        private static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";

        // The drawn pages in the item's folder as the screen's texture array, when they are this screen's version's.
        private static Texture2DArray DrawnPages(string folder, string file, ItemParts.RenderJob job)
        {
            var bytes = File.ReadAllBytes(file);
            if (!ItemParts.PictureSize(bytes, out int width, out int height) || width != 1024 || height != 576 * ScreenPageSlices) return null;
            string path = ItemParts.WriteAsset(folder, "N4N0 screen pages.png", bytes);
            job.Made.Add(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (settings.textureShape != TextureImporterShape.Texture2DArray || settings.flipbookRows != ScreenPageSlices)
            {
                settings.textureShape = TextureImporterShape.Texture2DArray;
                settings.flipbookRows = ScreenPageSlices;
                settings.flipbookColumns = 1;
                importer.SetTextureSettings(settings);
                importer.sRGBTexture = true; importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true; importer.maxTextureSize = 8192; importer.npotScale = TextureImporterNPOTScale.None;
                importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 2;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        }

        // The drawn layout table: exact bytes (no colour space, no compression, no filtering).
        private static Texture2D DrawnLayout(string folder, string file, ItemParts.RenderJob job)
        {
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            if (!ItemParts.PictureSize(bytes, out _, out int rows) || rows != ScreenPageSlices) return null;
            string path = ItemParts.WriteAsset(folder, "N4N0 screen layout.png", bytes);
            job.Made.Add(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer.filterMode != FilterMode.Point || importer.sRGBTexture)
            {
                importer.textureType = TextureImporterType.Default; importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None; importer.filterMode = FilterMode.Point; importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// A placed N4N0's screen took the shared files again (another thumbnail size): its theme and language, when it had
        /// others than the first, are drawn for it again.
        /// </summary>
        private static void DrawPartsAgain(Transform orb, IEnumerable<ItemParts.Applied> before)
        {
            var kept = before?.Where(a => a != null).ToList();
            if (kept == null || kept.Count == 0) return;
            foreach (var group in ItemParts.Of(orb.gameObject).Where(p => p.Part.renderer == MenuRenderer).GroupBy(p => p.Item))
            {
                var first = group.First();
                var choices = group.Select(p => (p.Part, ItemParts.VariantOf(kept.FirstOrDefault(a => a.part == p.Part.key), p.PartsPath, p.Part)))
                    .Where(c => c.Item2 != null).Select(c => (c.Part, c.Item2)).ToList();
                if (choices.Count > 0) _ = ItemParts.ApplyAsync(first.Item, first.Definition, first.PrefabPath, first.PartsPath, choices);
            }
        }

        // "1.10.0" after "1.9.2"; a pre-release before its release, pre-release parts compared in turn.
        internal static int CompareVersions(string a, string b)
        {
            int[] Numbers(string v) => (v ?? "").Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
            var x = Numbers(a); var y = Numbers(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
                if (l != r) return l.CompareTo(r);
            }
            string Pre(string v) { string core = (v ?? "").Split('+')[0]; int dash = core.IndexOf('-'); return dash < 0 ? null : core.Substring(dash + 1); }
            string pa = Pre(a), pb = Pre(b);
            if ((pa == null) != (pb == null)) return pa == null ? 1 : -1;
            if (pa == null) return 0;
            var da = pa.Split('.'); var db = pb.Split('.');
            for (int i = 0; i < Math.Min(da.Length, db.Length); i++)
            {
                bool na = int.TryParse(da[i], out int va), nb = int.TryParse(db[i], out int vb);
                int order = na && nb ? va.CompareTo(vb) : na ? -1 : nb ? 1 : string.CompareOrdinal(da[i], db[i]);
                if (order != 0) return Math.Sign(order);
            }
            return da.Length.CompareTo(db.Length);
        }
    }
}
