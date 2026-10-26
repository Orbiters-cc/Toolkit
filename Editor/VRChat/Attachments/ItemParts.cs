using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Customizable parts a creator gives their asset, like N4N0's menu theme, bot model and language: a file named like
    /// the asset's prefab with ".parts.json" beside it (<c>N4N0.prefab</c>, <c>N4N0.parts.json</c>), as Item settings do.
    /// Each part has a kind (values, model or strings: the standard, nothing else), the variants the creator ships in the
    /// asset (<c>base</c>, what an item uses until its wearer picks another) and anyone's variants from the gallery
    /// (<see cref="PartVariant"/>: a <c>variant.json</c> with pictures, or an FBX and its textures, never a script). A
    /// variant goes on a placed item in place and with Undo (<see cref="ApplyAsync"/>): values on the item's materials, a
    /// model in its renderers, and through a renderer Orbiters ships (<see cref="RegisterRenderer"/>) the item's pictures.
    /// Which variant each part uses is read back from the item: the files made for it, under
    /// <c>Assets/Orbiters/Variants/&lt;avatar&gt;/&lt;item&gt;/</c>, carry it (<see cref="Current"/>), so the item needs no
    /// Orbiters script, Undo puts back the earlier variant, and an update from the gallery keeps it
    /// (<see cref="AttachmentHooks.CarryOptions"/>). Paths of objects are below the item's root; files are paths beside the
    /// file, <c>Assets/…</c> paths or <c>guid:…</c>. Orbiters' documentation explains the file ("Customizable parts and
    /// variants").
    /// </summary>
    public static partial class ItemParts
    {
        public const string Extension = ".parts.json";
        public const int Schema = 1;
        public const string Values = "values", Model = "model", Strings = "strings";
        public static readonly IReadOnlyList<string> Kinds = new[] { Values, Model, Strings };
        public const string Color = "color", Number = "number", Choice = "choice", Toggle = "toggle", Picture = "picture";
        public static readonly IReadOnlyList<string> FieldTypes = new[] { Color, Number, Choice, Toggle, Picture };
        private static readonly Regex KeyPattern = new Regex("^[a-z0-9-]{1,32}$");
        private static readonly Regex FieldKeyPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");

        /// <summary>The declaration (<c>.parts.json</c>), as the creator wrote it.</summary>
        public sealed class Definition
        {
            public int schema = Schema;
            public List<Part> parts = new List<Part>();
            /// <summary>The file's text, which a release sends to Orbiters as its parts' definition.</summary>
            [JsonIgnore] public string Text;
            [JsonIgnore] public string File;

            public Part Find(string key) => parts?.FirstOrDefault(p => p != null && p.key == key);
        }

        public sealed class Part
        {
            public string key, label, description, kind;
            // values
            public List<Field> fields = new List<Field>();
            public List<Target> targets = new List<Target>();
            /// <summary>The Orbiters renderer making the item's pictures from this part's choice (values and strings).</summary>
            public string renderer;
            // model
            public List<Slot> slots = new List<Slot>();
            public List<string> blendshapes = new List<string>();
            public int maxTriangles;
            public int maxTextureSize;
            public float maxRadius;
            public List<string> shaders = new List<string>();
            // strings
            /// <summary>The base strings file: every key, the base language's texts and optional max lengths.</summary>
            public string strings;
            [JsonProperty("base")] public List<Base> bases = new List<Base>();

            public string Label => string.IsNullOrWhiteSpace(label) ? key : label;
            public Field Field(string fieldKey) => fields?.FirstOrDefault(f => f != null && f.key == fieldKey);
        }

        public sealed class Field
        {
            public string key, label, type;
            public List<string> options = new List<string>();
            public float? min, max;
            public int width, height;
            public bool optional;
            [JsonProperty("default")] public JToken defaultValue;

            public string Label => string.IsNullOrWhiteSpace(label) ? key : label;
        }

        public sealed class Target
        {
            public string field, path, property;
            /// <summary>A material slot of the renderer; null: every slot whose material has the property.</summary>
            public int? slot;
        }

        public sealed class Slot
        {
            public string key, label;
            public List<string> paths = new List<string>();

            public string Label => string.IsNullOrWhiteSpace(label) ? key : label;
        }

        public sealed class Base
        {
            public string name, language, file;
        }

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore, MissingMemberHandling = MissingMemberHandling.Ignore,
            FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None,
        };

        private static readonly Dictionary<string, (DateTime written, Definition definition)> Cache = new Dictionary<string, (DateTime, Definition)>();

        /// <summary>Where a prefab's parts file is: beside it, named like it.</summary>
        public static string PathFor(string prefabPath) =>
            string.IsNullOrEmpty(prefabPath) ? null : prefabPath.Substring(0, prefabPath.Length - Path.GetExtension(prefabPath).Length) + Extension;

        /// <summary>
        /// Reads a parts file; with <paramref name="prefab"/>, also checks it against the prefab and the files it names,
        /// each problem said in <paramref name="problems"/>. Null when the file can't be read.
        /// </summary>
        public static Definition Read(string partsPath, GameObject prefab = null, List<string> problems = null)
        {
            string text;
            try { text = File.ReadAllText(partsPath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problems?.Add(Path.GetFileName(partsPath) + " could not be read: " + e.Message);
                return null;
            }
            var definition = Parse(text, Path.GetFileName(partsPath), problems);
            if (definition == null) return null;
            definition.File = partsPath;
            if (problems != null) Check(partsPath, definition, prefab, problems);
            return definition;
        }

        /// <summary>A parts definition from its text (what a release sends, what Orbiters stores), or null.</summary>
        public static Definition Parse(string text, string name = "The parts definition", List<string> problems = null)
        {
            Definition definition;
            try { definition = JsonConvert.DeserializeObject<Definition>(text, Json); }
            catch (JsonException e)
            {
                problems?.Add(name + " is not valid JSON: " + e.Message);
                return null;
            }
            if (definition?.parts == null)
            {
                problems?.Add(name + " has no parts list.");
                return null;
            }
            definition.parts.RemoveAll(p => p == null);
            definition.Text = text;
            return definition;
        }

        internal static Definition Cached(string partsPath)
        {
            if (string.IsNullOrEmpty(partsPath) || !File.Exists(partsPath)) return null;
            var written = File.GetLastWriteTimeUtc(partsPath);
            if (Cache.TryGetValue(partsPath, out var known) && known.written == written) return known.definition;
            var definition = Read(partsPath);
            Cache[partsPath] = (written, definition);
            return definition;
        }

        /// <summary>The files the parts file uses (base variants, their pictures, models and textures), which a package of the prefab needs too.</summary>
        public static List<string> AssetsOf(string partsPath, Definition definition)
        {
            var paths = new List<string>();
            foreach (var part in definition.parts)
            {
                if (part.kind == Strings) paths.Add(AssetPath(partsPath, part.strings));
                foreach (var entry in part.bases ?? new List<Base>())
                {
                    string file = AssetPath(partsPath, entry?.file);
                    if (string.IsNullOrEmpty(file)) continue;
                    paths.Add(file);
                    paths.Add(BaseThumbnailPath(file));
                    var variant = PartVariant.ReadFile(file, null);
                    if (variant != null) paths.AddRange(variant.NamedFiles().Select(f => AssetPath(file, f)));
                }
            }
            return paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct().ToList();
        }

        /// <summary>The picture shipped beside a base variant's file (<c>theme-modern.json</c>, <c>theme-modern.png</c>).</summary>
        public static string BaseThumbnailPath(string baseFile) =>
            string.IsNullOrEmpty(baseFile) ? null : baseFile.Substring(0, baseFile.Length - Path.GetExtension(baseFile).Length) + ".png";

        /// <summary>A first parts file for a prefab: a values part with a colour on its first renderer's material.</summary>
        public static string Template(GameObject prefab)
        {
            var renderer = prefab != null ? prefab.GetComponentsInChildren<Renderer>(true).FirstOrDefault() : null;
            string path = renderer != null ? RelativePath(prefab.transform, renderer.transform) : "Body";
            return "{\n" +
                   "    \"schema\": 1,\n" +
                   "    \"parts\": [\n" +
                   "        {\n" +
                   "            \"key\": \"colour\", \"label\": \"Colour\", \"kind\": \"values\",\n" +
                   "            \"fields\": [ { \"key\": \"main\", \"label\": \"Main colour\", \"type\": \"color\", \"default\": \"#FFFFFF\" } ],\n" +
                   "            \"targets\": [ { \"field\": \"main\", \"path\": \"" + Escape(path) + "\", \"property\": \"_Color\" } ]\n" +
                   "        }\n" +
                   "    ]\n" +
                   "}\n";
        }

        // ---- Paths ----

        // An object below the item's root: "" or "." is the root itself.
        internal static Transform Find(Transform root, string path)
        {
            if (root == null || path == null) return null;
            string clean = path.Trim().Trim('/');
            return clean.Length == 0 || clean == "." ? root : root.Find(clean);
        }

        internal static string RelativePath(Transform root, Transform target)
        {
            var names = new List<string>();
            for (var t = target; t != null && t != root; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        /// <summary>A file the parts file names: beside it, under Assets/ or Packages/, or by GUID.</summary>
        public static string AssetPath(string partsPath, string reference) => ItemSettings.AssetPath(partsPath, reference);

        private static string Escape(string text) => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ---- Checks ----

        private static void Check(string partsPath, Definition definition, GameObject prefab, List<string> problems)
        {
            string file = Path.GetFileName(partsPath);
            if (definition.schema != Schema) problems.Add(file + ": schema " + definition.schema + " is not known (this Toolkit reads schema " + Schema + ").");
            if (definition.parts.Count == 0) problems.Add(file + " lists no parts.");
            Unknown(file, definition.Text, problems);
            var keys = new HashSet<string>();
            foreach (var part in definition.parts)
            {
                string name = string.IsNullOrWhiteSpace(part.key) ? "A part" : part.key;
                if (string.IsNullOrEmpty(part.key) || !KeyPattern.IsMatch(part.key)) problems.Add(name + ": its key must be 1 to 32 small letters, digits or dashes.");
                else if (!keys.Add(part.key)) problems.Add(name + ": two parts have this key.");
                if (string.IsNullOrWhiteSpace(part.label)) problems.Add(name + ": no label.");
                if (!Kinds.Contains(part.kind)) { problems.Add(name + ": kind is \"" + part.kind + "\" (use \"values\", \"model\" or \"strings\")."); continue; }
                if (!string.IsNullOrEmpty(part.renderer) && !Renderers.ContainsKey(part.renderer))
                    problems.Add(name + ": there is no renderer \"" + part.renderer + "\" (Orbiters' renderers: " + (Renderers.Count == 0 ? "none" : string.Join(", ", Renderers.Keys)) + ").");
                if (part.kind == Values) CheckValues(part, name, prefab, problems);
                else if (part.kind == Model) CheckModel(part, name, prefab, problems);
                else CheckStrings(partsPath, part, name, problems);
                if ((part.bases ?? new List<Base>()).Count == 0) problems.Add(name + ": list at least one variant of your own in \"base\" (what an item uses until its wearer picks another).");
                var baseNames = new HashSet<string>();
                foreach (var entry in part.bases ?? new List<Base>())
                {
                    if (entry == null) continue;
                    string where = name + " › " + (string.IsNullOrWhiteSpace(entry.name) ? "a base variant" : entry.name);
                    if (string.IsNullOrWhiteSpace(entry.name)) problems.Add(where + ": no name.");
                    if (part.kind == Strings && (string.IsNullOrEmpty(entry.language) || !LanguagePattern.IsMatch(entry.language))) problems.Add(where + ": give its language (a code such as fr or pt-BR).");
                    else if (!baseNames.Add(entry.name)) problems.Add(where + ": two base variants have this name.");
                    string path = AssetPath(partsPath, entry.file);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) { problems.Add(where + ": no file at " + entry.file + "."); continue; }
                    var variantProblems = new List<string>();
                    var variant = PartVariant.ReadFile(path, variantProblems);
                    if (variant == null) { problems.AddRange(variantProblems.Select(p => where + ": " + p)); continue; }
                    variant.Fill(part, entry);
                    Check(part, variant, prefab, variantProblems, shipped: true, partsPath: partsPath);
                    problems.AddRange(variantProblems.Select(p => where + ": " + p));
                }
            }
        }

        // What Orbiters refuses in a definition: settings the standard does not have (a misspelt one would be ignored here).
        private static readonly Dictionary<string, string[]> PartSettings = new Dictionary<string, string[]>
        {
            [Values] = new[] { "fields", "targets" },
            [Model] = new[] { "slots", "blendshapes", "maxTriangles", "maxTextureSize", "maxRadius", "shaders" },
            [Strings] = new[] { "strings" },
        };
        private static readonly Dictionary<string, string[]> FieldSettings = new Dictionary<string, string[]>
        {
            [Color] = new[] { "default" }, [Number] = new[] { "default", "min", "max" }, [Choice] = new[] { "default", "options" },
            [Toggle] = new[] { "default" }, [Picture] = new[] { "width", "height", "optional" },
        };

        private static void Unknown(string file, string text, List<string> problems)
        {
            JObject root;
            try { root = JObject.Parse(text ?? "{}"); }
            catch (JsonException) { return; }
            void Only(JObject value, IEnumerable<string> allowed, string where)
            {
                if (value == null) return;
                var known = new HashSet<string>(allowed);
                foreach (var property in value.Properties().Where(p => !known.Contains(p.Name)))
                    problems.Add(where + ": \"" + property.Name + "\" is not a setting of the standard.");
            }
            Only(root, new[] { "schema", "parts" }, file);
            foreach (var part in (root["parts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = (string)part["key"] ?? "A part", kind = (string)part["kind"] ?? "";
                if (!PartSettings.TryGetValue(kind, out var own)) continue;
                Only(part, new[] { "key", "label", "description", "kind", "renderer", "base" }.Concat(own), key);
                if (kind == Model && part["renderer"] != null) problems.Add(key + ": a model part has no renderer (its model replaces the item's meshes itself).");
                foreach (var field in (part["fields"] as JArray ?? new JArray()).OfType<JObject>())
                    if (FieldSettings.TryGetValue((string)field["type"] ?? "", out var settings))
                        Only(field, new[] { "key", "label", "type" }.Concat(settings), key + " › " + ((string)field["key"] ?? "a field"));
                foreach (var target in (part["targets"] as JArray ?? new JArray()).OfType<JObject>()) Only(target, new[] { "field", "path", "slot", "property" }, key + " › a target");
                foreach (var slot in (part["slots"] as JArray ?? new JArray()).OfType<JObject>()) Only(slot, new[] { "key", "label", "paths" }, key + " › a slot");
                foreach (var entry in (part["base"] as JArray ?? new JArray()).OfType<JObject>())
                    Only(entry, kind == Strings ? new[] { "name", "file", "language" } : new[] { "name", "file" }, key + " › " + ((string)entry["name"] ?? "a base variant"));
            }
        }

        private static void CheckValues(Part part, string name, GameObject prefab, List<string> problems)
        {
            if ((part.fields ?? new List<Field>()).Count == 0) problems.Add(name + ": a values part needs fields.");
            var fieldKeys = new HashSet<string>();
            foreach (var field in part.fields ?? new List<Field>())
            {
                if (field == null) continue;
                string where = name + " › " + (field.key ?? "a field");
                if (string.IsNullOrEmpty(field.key) || !FieldKeyPattern.IsMatch(field.key)) problems.Add(where + ": its key must be letters, digits, dots, dashes or underscores (64 at most).");
                else if (!fieldKeys.Add(field.key)) problems.Add(where + ": two fields have this key.");
                if (!FieldTypes.Contains(field.type)) { problems.Add(where + ": type is \"" + field.type + "\" (use color, number, choice, toggle or picture)."); continue; }
                if (field.type == Choice && (field.options == null || field.options.Count < 2)) problems.Add(where + ": a choice needs at least two options.");
                if (field.type == Number && (!field.min.HasValue || !field.max.HasValue || field.min >= field.max)) problems.Add(where + ": a number needs a min below its max.");
                if (string.IsNullOrWhiteSpace(field.label)) problems.Add(where + ": no label.");
                if (field.type == Picture && (field.width < 0 || field.height < 0 || field.width > MaxPicture || field.height > MaxPicture))
                    problems.Add(where + ": pictures are at most " + MaxPicture + " pixels wide and high.");
                if (field.defaultValue != null && field.type != Picture && ValueProblem(field, field.defaultValue, null) is string bad) problems.Add(where + ": its default " + bad);
                if (field.defaultValue == null && field.type != Picture) problems.Add(where + ": give it a default.");
            }
            if ((part.targets ?? new List<Target>()).Count == 0 && string.IsNullOrEmpty(part.renderer))
                problems.Add(name + ": a values part needs targets (materials its values go on) or a renderer.");
            foreach (var target in part.targets ?? new List<Target>())
            {
                if (target == null) continue;
                var field = part.Field(target.field);
                string where = name + " › target " + (target.field ?? "?");
                if (field == null) { problems.Add(where + ": no field \"" + target.field + "\"."); continue; }
                if (field.type == Choice || field.type == Toggle) problems.Add(where + ": only colours, numbers and pictures go on materials.");
                if (string.IsNullOrWhiteSpace(target.property)) problems.Add(where + ": no material property.");
                if (prefab == null) continue;
                var renderer = Find(prefab.transform, target.path)?.GetComponent<Renderer>();
                if (renderer == null) { problems.Add(where + ": no renderer \"" + target.path + "\" below " + prefab.name + "."); continue; }
                var materials = renderer.sharedMaterials;
                if (target.slot.HasValue && (target.slot < 0 || target.slot >= materials.Length))
                    problems.Add(where + ": " + target.path + " has no material slot " + target.slot + " (it has " + materials.Length + ").");
                else if (!TargetSlots(renderer, target).Any())
                    problems.Add(where + ": no material of " + target.path + " has the property " + target.property + ".");
            }
        }

        private static void CheckModel(Part part, string name, GameObject prefab, List<string> problems)
        {
            if ((part.slots ?? new List<Slot>()).Count == 0) problems.Add(name + ": a model part needs slots (the renderers its model goes in).");
            var slotKeys = new HashSet<string>();
            foreach (var slot in part.slots ?? new List<Slot>())
            {
                if (slot == null) continue;
                string where = name + " › slot " + (slot.key ?? "?");
                if (string.IsNullOrEmpty(slot.key) || !KeyPattern.IsMatch(slot.key)) problems.Add(where + ": its key must be 1 to 32 small letters, digits or dashes.");
                else if (!slotKeys.Add(slot.key)) problems.Add(where + ": two slots have this key.");
                if ((slot.paths ?? new List<string>()).Count == 0) problems.Add(where + ": no paths.");
                if (string.IsNullOrWhiteSpace(slot.label)) problems.Add(where + ": no label.");
                if (prefab == null) continue;
                foreach (string path in slot.paths ?? new List<string>())
                {
                    var renderer = Find(prefab.transform, path)?.GetComponent<Renderer>();
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) problems.Add(where + ": no mesh renderer \"" + path + "\" below " + prefab.name + ".");
                    else if (renderer is SkinnedMeshRenderer skinned && skinned.bones != null && skinned.bones.Length > 0)
                        problems.Add(where + ": " + path + " is skinned to bones: a variant's model can only replace meshes without bones.");
                }
            }
            if (part.maxTriangles <= 0) problems.Add(name + ": give maxTriangles (the most triangles a variant's model may have).");
            if (part.maxTextureSize <= 0 || part.maxTextureSize > MaxPicture) problems.Add(name + ": give maxTextureSize (" + MaxPicture + " at most).");
            if (part.maxRadius <= 0) problems.Add(name + ": give maxRadius (in the slot's units, the base model fitting a radius of 1).");
            if ((part.shaders ?? new List<string>()).Count == 0) problems.Add(name + ": list the shaders a variant's materials may use.");
            foreach (string shader in part.shaders ?? new List<string>())
            {
                if (!AllowedShaders.Contains(shader)) problems.Add(name + ": \"" + shader + "\" is not a shader variants may use (" + string.Join(", ", AllowedShaders) + ").");
                else if (Shader.Find(shader) == null) problems.Add(name + ": the shader \"" + shader + "\" is not in this project.");
            }
            if (prefab == null) return;
            foreach (string shape in part.blendshapes ?? new List<string>())
            foreach (var path in (part.slots ?? new List<Slot>()).Where(s => s != null).SelectMany(s => s.paths ?? new List<string>()))
            {
                var mesh = (Find(prefab.transform, path)?.GetComponent<SkinnedMeshRenderer>())?.sharedMesh;
                if (mesh != null && mesh.GetBlendShapeIndex(shape) < 0) problems.Add(name + ": " + path + " has no blendshape \"" + shape + "\" (blendshapes lists the item's own).");
            }
        }

        private static void CheckStrings(string partsPath, Part part, string name, List<string> problems)
        {
            if (string.IsNullOrEmpty(part.renderer)) problems.Add(name + ": strings only reach the item through a renderer.");
            string path = AssetPath(partsPath, part.strings);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { problems.Add(name + ": no strings file at " + part.strings + "."); return; }
            var texts = BaseStrings.Read(path, problems);
            if (texts != null && texts.Texts.Count == 0) problems.Add(name + ": " + part.strings + " has no strings.");
        }

        /// <summary>Why a value doesn't fit its field ("is not a colour"), or null.</summary>
        internal static string ValueProblem(Field field, JToken value, ICollection<string> files)
        {
            switch (field.type)
            {
                case Color:
                    return value.Type == JTokenType.String && TryColor((string)value, out _) ? null : "is not a colour (#RRGGBB or #RRGGBBAA).";
                case Number:
                    if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) return "is not a number.";
                    double number = value.Value<double>();
                    if (double.IsNaN(number) || double.IsInfinity(number)) return "is not a number.";
                    if (field.min.HasValue && number < field.min.Value) return "is below " + field.min.Value.ToString(CultureInfo.InvariantCulture) + ".";
                    if (field.max.HasValue && number > field.max.Value) return "is above " + field.max.Value.ToString(CultureInfo.InvariantCulture) + ".";
                    return null;
                case Choice:
                    return value.Type == JTokenType.String && (field.options ?? new List<string>()).Contains((string)value) ? null : "is not one of " + string.Join(", ", field.options ?? new List<string>()) + ".";
                case Toggle:
                    return value.Type == JTokenType.Boolean ? null : "is not true or false.";
                case Picture:
                    if (value.Type != JTokenType.String) return "is not a file name.";
                    return files == null || files.Contains((string)value) ? null : "names " + (string)value + ", which is not in the variant.";
                default:
                    return "has an unknown type.";
            }
        }

        /// <summary>A colour written #RRGGBB or #RRGGBBAA.</summary>
        public static bool TryColor(string text, out Color32 colour)
        {
            colour = new Color32(0, 0, 0, 255);
            if (string.IsNullOrEmpty(text) || text[0] != '#' || (text.Length != 7 && text.Length != 9)) return false;
            var bytes = new byte[4] { 0, 0, 0, 255 };
            for (int i = 0; i < (text.Length - 1) / 2; i++)
                if (!byte.TryParse(text.Substring(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return false;
            colour = new Color32(bytes[0], bytes[1], bytes[2], bytes[3]);
            return true;
        }

        /// <summary>A colour as a part file writes it: #RRGGBB, with its alpha (#RRGGBBAA) when not opaque.</summary>
        public static string ColorText(Color32 colour) =>
            "#" + colour.r.ToString("X2") + colour.g.ToString("X2") + colour.b.ToString("X2") + (colour.a < 255 ? colour.a.ToString("X2") : "");

        /// <summary>The material slots a target puts its value in.</summary>
        internal static IEnumerable<int> TargetSlots(Renderer renderer, Target target)
        {
            var materials = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
            for (int i = 0; i < materials.Length; i++)
                if ((!target.slot.HasValue || target.slot == i) && materials[i] != null && materials[i].HasProperty(target.property ?? ""))
                    yield return i;
        }

        // ---- A base strings file ----

        /// <summary>
        /// The base strings file of a strings part: every key with the base language's text and its longest length
        /// (<c>max</c>, else twice the base text's). Reads <c>"strings": { "key": "text" }</c> or
        /// <c>{ "key": { "text": "…", "max": 20 } }</c>, and a <c>"max": { "key": 20 }</c> table beside them.
        /// </summary>
        public sealed class BaseStrings
        {
            public string Language;
            public readonly Dictionary<string, string> Texts = new Dictionary<string, string>();
            public readonly Dictionary<string, int> Max = new Dictionary<string, int>();

            public int MaxOf(string key) => Max.TryGetValue(key, out int max) && max > 0 ? max : Math.Max(1, 2 * (Texts.TryGetValue(key, out var text) ? (text ?? "").Length : 0));

            public static BaseStrings Read(string path, List<string> problems)
            {
                try { return Parse(File.ReadAllText(path)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is InvalidCastException)
                {
                    problems?.Add(Path.GetFileName(path) + " is not a strings file: " + e.Message);
                    return null;
                }
            }

            public static BaseStrings Parse(string text)
            {
                var root = JObject.Parse(text);
                var result = new BaseStrings { Language = (string)root["language"] };
                if (root["strings"] is JObject strings)
                    foreach (var property in strings.Properties())
                    {
                        if (property.Value is JObject entry)
                        {
                            result.Texts[property.Name] = (string)entry["text"] ?? "";
                            if (entry["max"] != null && entry["max"].Type == JTokenType.Integer) result.Max[property.Name] = (int)entry["max"];
                        }
                        else result.Texts[property.Name] = (string)property.Value ?? "";
                    }
                if (root["max"] is JObject max)
                    foreach (var property in max.Properties())
                        if (property.Value.Type == JTokenType.Integer) result.Max[property.Name] = (int)property.Value;
                return result;
            }
        }

        // ---- On a placed item ----

        /// <summary>A part of an item on an avatar: what it is, its base variants and which variant it uses now.</summary>
        public sealed class Placed
        {
            public Transform Item;
            public OrbitersAttachment Attachment;
            public string PrefabPath, PartsPath;
            public Definition Definition;
            public Part Part;
            /// <summary>The variant it uses now (read back from the item).</summary>
            public Applied Current;
            /// <summary>The asset's gallery id when it came from the gallery, else 0.</summary>
            public int AssetId => Attachment != null && Attachment.gallery != null ? Attachment.gallery.assetId : 0;
        }

        /// <summary>The customizable parts of the items below <paramref name="placed"/> (an item on an avatar).</summary>
        public static List<Placed> Of(GameObject placed)
        {
            var found = new List<Placed>();
            if (placed == null) return found;
            foreach (var attachment in placed.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                string prefab = ItemSettings.PrefabOf(attachment);
                string partsPath = PathFor(prefab);
                var definition = Cached(partsPath);
                if (definition == null) continue;
                foreach (var part in definition.parts)
                {
                    if (part == null || !Kinds.Contains(part.kind) || string.IsNullOrEmpty(part.key)) continue;
                    found.Add(new Placed
                    {
                        Item = attachment.transform, Attachment = attachment, PrefabPath = prefab, PartsPath = partsPath, Definition = definition, Part = part,
                        Current = Current(attachment.transform, definition, part),
                    });
                }
            }
            return found;
        }

        /// <summary>The base variants of a part as variants, read from their files (the creator's, shipped in the package).</summary>
        public static List<PartVariant> Bases(string partsPath, Part part)
        {
            var list = new List<PartVariant>();
            foreach (var entry in part.bases ?? new List<Base>())
            {
                string file = AssetPath(partsPath, entry?.file);
                var variant = string.IsNullOrEmpty(file) ? null : PartVariant.ReadFile(file, null);
                if (variant == null) continue;
                variant.Fill(part, entry);
                list.Add(variant);
            }
            return list;
        }
    }
}
