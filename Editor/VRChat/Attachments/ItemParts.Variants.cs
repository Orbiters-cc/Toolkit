using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// A variant of a customizable part (<see cref="ItemParts"/>): its <c>variant.json</c> (only the block of the part's
    /// kind: <see cref="values"/>, <see cref="strings"/> or <see cref="model"/>), the files it names and its 512 × 512
    /// thumbnail. Posted to Orbiters and downloaded as one flat zip (<see cref="ReadZip"/>, <see cref="WriteZip"/>); a
    /// creator's base variants are files in the asset (<see cref="ReadFile"/>), their files beside them.
    /// </summary>
    public sealed class PartVariant
    {
        public const string JsonName = "variant.json", ThumbnailName = "thumbnail.png";

        public int schema = ItemParts.Schema;
        public string part, name, description, language;
        public JObject values;
        public Dictionary<string, string> strings;
        public VariantModel model;

        /// <summary>The files it brings (pictures, an FBX and its textures) by name, besides its JSON and thumbnail.</summary>
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Its 512 × 512 PNG, what the galleries show.</summary>
        public byte[] Thumbnail;
        /// <summary>A file read from disk (a base variant, or one carried over): its files are beside it, read when needed.</summary>
        public string Folder;
        /// <summary>Orbiters' id for it (0 for a base variant or one not posted).</summary>
        public int Id;
        public bool Official;
        public string Author, Sha256;
        /// <summary>For a base variant (the creator's, shipped in the asset), its name in the parts file; else null.</summary>
        public string BaseName;
        /// <summary>The JSON as read (or as <see cref="ToJson"/> writes it).</summary>
        public string Json;
        /// <summary>Checked and applied once already (taken again from an item's files): its thumbnail is not asked for again.</summary>
        public bool Checked;

        public bool IsBase => BaseName != null;

        // ---- Reading ----

        /// <summary>Reads a <c>variant.json</c>; null (with the problem said) when it isn't one.</summary>
        public static PartVariant Parse(string text, List<string> problems, string file = JsonName)
        {
            JObject root;
            try { root = JObject.Parse(text ?? ""); }
            catch (JsonException e) { problems?.Add(file + " is not valid JSON: " + e.Message); return null; }
            var variant = new PartVariant { Json = text };
            try
            {
                variant.schema = root["schema"] != null ? (int)root["schema"] : ItemParts.Schema;
                variant.part = (string)root["part"];
                variant.name = (string)root["name"];
                variant.description = (string)root["description"];
                variant.language = (string)root["language"];
                if (root["values"] is JObject values) variant.values = values;
                else if (root["values"] != null) problems?.Add(file + ": \"values\" is not an object.");
                if (root["strings"] is JObject strings)
                {
                    variant.strings = new Dictionary<string, string>();
                    foreach (var property in strings.Properties())
                    {
                        // A base strings file may give each text with its longest length.
                        if (property.Value is JObject entry) variant.strings[property.Name] = (string)entry["text"] ?? "";
                        else if (property.Value.Type == JTokenType.String) variant.strings[property.Name] = (string)property.Value;
                        else problems?.Add(file + ": the text of \"" + property.Name + "\" is not a string.");
                    }
                }
                else if (root["strings"] != null) problems?.Add(file + ": \"strings\" is not an object.");
                if (root["model"] is JObject model) variant.model = model.ToObject<VariantModel>();
                else if (root["model"] != null) problems?.Add(file + ": \"model\" is not an object.");
                // A theme written as Orbiters - N4N0 reads it ({ "style", "colors": { … }, "screen": { … } }): its values,
                // the nested ones named with dots ("colors.accent").
                if (variant.values == null && variant.strings == null && variant.model == null)
                {
                    var flat = new JObject();
                    foreach (var property in root.Properties().Where(p => !Known.Contains(p.Name))) Flatten(property.Name, property.Value, flat);
                    if (flat.Count > 0) variant.values = flat;
                }
            }
            catch (Exception e) when (e is InvalidCastException || e is ArgumentException || e is FormatException || e is JsonException)
            {
                problems?.Add(file + ": " + e.Message);
                return null;
            }
            return variant;
        }

        private static readonly HashSet<string> Known = new HashSet<string> { "schema", "part", "name", "description", "language", "values", "strings", "model", "max" };

        private static void Flatten(string prefix, JToken value, JObject into)
        {
            if (value is JObject inner) foreach (var property in inner.Properties()) Flatten(prefix + "." + property.Name, property.Value, into);
            else into[prefix] = value;
        }

        /// <summary>A variant file on disk (a base variant): its files are beside it.</summary>
        public static PartVariant ReadFile(string path, List<string> problems)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { problems?.Add("No file at " + path + "."); return null; }
                if (info.Length > ItemParts.MaxJsonBytes) { problems?.Add(Path.GetFileName(path) + " is larger than " + ItemParts.MaxJsonBytes / 1024 + " KB."); return null; }
                var variant = Parse(File.ReadAllText(path), problems, Path.GetFileName(path));
                if (variant != null) variant.Folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                return variant;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problems?.Add(Path.GetFileName(path) + " could not be read: " + e.Message);
                return null;
            }
        }

        private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9 _.-]{1,64}$");

        /// <summary>Whether a file name may be in a variant's zip (flat, letters, digits, spaces, dots, dashes, underscores).</summary>
        public static bool AllowedName(string fileName) => fileName != null && NamePattern.IsMatch(fileName) && fileName != "." && fileName != "..";

        /// <summary>
        /// Reads a variant's zip, checking its shape (§2: size, file count, flat names, what is unpacked) before reading any
        /// file; null (with the problems said) when it can't be one. <see cref="ItemParts.Check"/> checks it against the part.
        /// </summary>
        public static PartVariant ReadZip(byte[] zip, List<string> problems)
        {
            problems ??= new List<string>();
            if (zip == null || zip.Length == 0) { problems.Add("The variant's file is empty."); return null; }
            if (zip.LongLength > ItemParts.MaxZipBytes) { problems.Add("The variant's zip is larger than " + ItemParts.MaxZipBytes / (1024 * 1024) + " MB."); return null; }
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(zip, false), ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count > ItemParts.MaxFiles) { problems.Add("The variant holds " + archive.Entries.Count + " files (" + ItemParts.MaxFiles + " at most)."); return null; }
                    long declared = 0;
                    foreach (var entry in archive.Entries)
                    {
                        string fileName = entry.FullName;
                        if (fileName.Contains("/") || fileName.Contains("\\")) { problems.Add("The variant's files must be at its zip's top level (no folders): " + fileName + "."); return null; }
                        if (!AllowedName(fileName)) { problems.Add("\"" + fileName + "\" is not an allowed file name (letters, digits, spaces, dots, dashes, underscores; 64 at most)."); return null; }
                        if (files.ContainsKey(fileName)) { problems.Add("Two files are named " + fileName + "."); return null; }
                        declared += Math.Max(0, entry.Length);
                        if (declared > ItemParts.MaxUnpackedBytes) { problems.Add("The variant unpacks to more than " + ItemParts.MaxUnpackedBytes / (1024 * 1024) + " MB."); return null; }
                        files[fileName] = null;
                    }
                    long total = 0;
                    foreach (var entry in archive.Entries)
                    {
                        // Never more than the entry says it holds, nor than the whole budget.
                        long limit = Math.Min(entry.Length, ItemParts.MaxUnpackedBytes - total);
                        var bytes = ReadAtMost(entry, limit, out bool over);
                        if (over) { problems.Add(entry.FullName + " unpacks to more than it says."); return null; }
                        total += bytes.LongLength;
                        files[entry.FullName] = bytes;
                    }
                }
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is NotSupportedException)
            {
                problems.Add("The variant's file is not a zip it can read: " + e.Message);
                return null;
            }
            if (!files.TryGetValue(JsonName, out var json)) { problems.Add("The variant has no " + JsonName + "."); return null; }
            if (json.LongLength > ItemParts.MaxJsonBytes) { problems.Add(JsonName + " is larger than " + ItemParts.MaxJsonBytes / 1024 + " KB."); return null; }
            var variant = Parse(Utf8(json), problems);
            if (variant == null) return null;
            files.Remove(JsonName);
            if (files.TryGetValue(ThumbnailName, out var thumbnail)) { variant.Thumbnail = thumbnail; files.Remove(ThumbnailName); }
            foreach (var pair in files) variant.Files[pair.Key] = pair.Value;
            variant.Sha256 = Hash(zip);
            return variant;
        }

        private static byte[] ReadAtMost(ZipArchiveEntry entry, long limit, out bool over)
        {
            over = false;
            using (var stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    read += n;
                    if (read > limit) { over = true; return Array.Empty<byte>(); }
                    memory.Write(buffer, 0, n);
                }
                return memory.ToArray();
            }
        }

        private static string Utf8(byte[] bytes)
        {
            // A byte order mark is no part of the JSON.
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        // ---- Its files ----

        /// <summary>The bytes of a file it names: brought with it, or beside its file on disk; null when missing.</summary>
        public byte[] FileBytes(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (Files.TryGetValue(fileName, out var bytes) && bytes != null) return bytes;
            if (string.IsNullOrEmpty(Folder) || !AllowedName(fileName)) return null;
            string path = Folder + "/" + fileName;
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>The names of the files its JSON names (pictures; the FBX and its textures).</summary>
        public List<string> NamedFiles(ItemParts.Part definition = null)
        {
            var names = new List<string>();
            if (values != null)
                foreach (var property in values.Properties())
                {
                    var field = definition?.Field(property.Name);
                    bool picture = field != null ? field.type == ItemParts.Picture : property.Value.Type == JTokenType.String && ItemParts.IsPictureName((string)property.Value);
                    if (picture && property.Value.Type == JTokenType.String) names.Add((string)property.Value);
                }
            if (model != null)
            {
                if (!string.IsNullOrEmpty(model.mesh)) names.Add(model.mesh);
                foreach (var slot in (model.slots ?? new Dictionary<string, VariantSlot>()).Values.Where(s => s != null))
                foreach (var material in (slot.materials ?? new List<VariantMaterial>()).Where(m => m?.textures != null))
                    names.AddRange(material.textures.Values.Where(v => !string.IsNullOrEmpty(v)));
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Takes what the parts file says of a base variant (its part, name and language).</summary>
        public void Fill(ItemParts.Part definition, ItemParts.Base entry)
        {
            if (string.IsNullOrEmpty(part)) part = definition?.key;
            if (entry != null)
            {
                if (string.IsNullOrEmpty(name)) name = entry.name;
                if (string.IsNullOrEmpty(language)) language = entry.language;
                BaseName = entry.name;
                Official = true;
                if (Thumbnail == null && !string.IsNullOrEmpty(entry.file) && !string.IsNullOrEmpty(Folder))
                {
                    string picture = Folder + "/" + Path.GetFileNameWithoutExtension(entry.file) + ".png";
                    try { if (File.Exists(picture)) Thumbnail = File.ReadAllBytes(picture); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            }
        }

        // ---- Writing ----

        /// <summary>Its <c>variant.json</c>: only the block of its kind, files by name.</summary>
        public string ToJson()
        {
            var root = new JObject { ["schema"] = schema, ["part"] = part, ["name"] = name };
            if (!string.IsNullOrEmpty(description)) root["description"] = description;
            if (!string.IsNullOrEmpty(language)) root["language"] = language;
            if (values != null) root["values"] = values;
            if (strings != null) root["strings"] = JObject.FromObject(strings);
            if (model != null) root["model"] = JObject.FromObject(model, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            return root.ToString(Formatting.Indented);
        }

        /// <summary>Writes the zip Orbiters takes: <c>variant.json</c>, <c>thumbnail.png</c> and the files it names, flat.</summary>
        public void WriteZip(string path, ItemParts.Part definition = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            string temporary = path + ".part";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Add(string entryName, byte[] bytes)
                {
                    var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                    using (var output = entry.Open()) output.Write(bytes, 0, bytes.Length);
                }
                Add(JsonName, Encoding.UTF8.GetBytes(ToJson()));
                if (Thumbnail != null) Add(ThumbnailName, Thumbnail);
                foreach (string fileName in NamedFiles(definition))
                {
                    var bytes = FileBytes(fileName);
                    if (bytes != null) Add(fileName, bytes);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        /// <summary>A copy to change (the + form starts from the variant shown).</summary>
        public PartVariant Copy()
        {
            var copy = Parse(ToJson(), null) ?? new PartVariant();
            foreach (string fileName in NamedFiles()) { var bytes = FileBytes(fileName); if (bytes != null) copy.Files[fileName] = bytes; }
            copy.Thumbnail = Thumbnail;
            return copy;
        }
    }

    public sealed class VariantModel
    {
        /// <summary>The FBX in the variant (binary or ASCII).</summary>
        public string mesh;
        /// <summary>Per slot of the part (its key), the object of the FBX it takes and its materials.</summary>
        public Dictionary<string, VariantSlot> slots = new Dictionary<string, VariantSlot>();
    }

    public sealed class VariantSlot
    {
        [JsonProperty("object")] public string objectName;
        /// <summary>A material per submesh of the object's mesh, in order.</summary>
        public List<VariantMaterial> materials = new List<VariantMaterial>();
    }

    /// <summary>A material described, never a .mat: one of the part's shaders and its properties.</summary>
    public sealed class VariantMaterial
    {
        public string shader;
        public Dictionary<string, string> colors = new Dictionary<string, string>();
        public Dictionary<string, float> floats = new Dictionary<string, float>();
        public Dictionary<string, string> textures = new Dictionary<string, string>();
    }

    public static partial class ItemParts
    {
        public const long MaxZipBytes = 16L * 1024 * 1024, MaxUnpackedBytes = 48L * 1024 * 1024, MaxFbxBytes = 12L * 1024 * 1024, MaxJsonBytes = 256L * 1024;
        public const int MaxFiles = 24, MaxName = 60, MaxDescription = 500, ThumbnailSize = 512, MaxPicture = 2048;
        // A language tag as Orbiters takes it: fr, pt-BR, zh-Hans, zh-Hant-TW.
        private static readonly Regex LanguagePattern = new Regex("^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?$");
        // Texts may break lines; no other character below a space.
        private static readonly Regex TextControl = new Regex("[\\u0000-\\u0009\\u000b-\\u001f\\u007f]");

        /// <summary>The shaders a variant's material may ever use (VRChat's mobile shaders, in the SDK): a part lists some of them.</summary>
        public static readonly IReadOnlyList<string> AllowedShaders = new[]
        {
            "VRChat/Mobile/Standard Lite", "VRChat/Mobile/Toon Lit", "VRChat/Mobile/Toon Standard", "VRChat/Mobile/Toon Standard (Outline)",
            "VRChat/Mobile/MatCap Lit", "VRChat/Mobile/Diffuse", "VRChat/Mobile/Bumped Diffuse", "VRChat/Mobile/Bumped Mapped Specular",
        };

        internal static bool IsPictureName(string fileName)
        {
            string extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        }

        /// <summary>
        /// Checks a variant against its part (§2, as Orbiters does before taking a post): its JSON, the files it names and
        /// brings, pictures and textures (decoded), texts and their lengths, the model's materials. The model's meshes are
        /// checked once imported (<see cref="CheckMeshes"/>). Each problem is said in <paramref name="problems"/>.
        /// </summary>
        /// <param name="partsPath">The parts file (a strings part's base file is beside it).</param>
        /// <param name="shipped">A base variant in the asset: its files are beside it and it needs no thumbnail.</param>
        public static void Check(Part part, PartVariant variant, GameObject prefab, List<string> problems, bool shipped = false, string partsPath = null, string baseFile = null, bool applied = false)
        {
            if (variant.schema != Schema) problems.Add("schema " + variant.schema + " is not known (this Toolkit reads schema " + Schema + ").");
            if (variant.part != part.key) problems.Add("it is a variant of \"" + variant.part + "\", not of " + part.key + ".");
            if (string.IsNullOrWhiteSpace(variant.name)) problems.Add("give it a name.");
            else if (variant.name.Trim().Length > MaxName) problems.Add("its name is longer than " + MaxName + " characters.");
            if ((variant.description ?? "").Trim().Length > MaxDescription) problems.Add("its description is longer than " + MaxDescription + " characters.");
            // Only the block of the part's kind.
            if (part.kind != Values && variant.values != null) problems.Add("a " + part.kind + " variant has no values.");
            if (part.kind != Strings && variant.strings != null) problems.Add("a " + part.kind + " variant has no strings.");
            if (part.kind != Model && variant.model != null) problems.Add("a " + part.kind + " variant has no model.");
            if (!shipped && !(applied && variant.Thumbnail == null))
            {
                if (variant.Thumbnail == null) problems.Add("it has no " + PartVariant.ThumbnailName + " (" + ThumbnailSize + " × " + ThumbnailSize + ").");
                else if (!PictureSize(variant.Thumbnail, out int w, out int h) || w != ThumbnailSize || h != ThumbnailSize || !Decodes(variant.Thumbnail))
                    problems.Add(PartVariant.ThumbnailName + " must be a " + ThumbnailSize + " × " + ThumbnailSize + " PNG.");
                else if (!IsPng(variant.Thumbnail)) problems.Add(PartVariant.ThumbnailName + " must be a PNG.");
            }
            var named = variant.NamedFiles(part);
            foreach (string fileName in named)
            {
                if (!PartVariant.AllowedName(fileName)) { problems.Add("\"" + fileName + "\" is not an allowed file name (letters, digits, spaces, dots, dashes, underscores; 64 at most)."); continue; }
                if (variant.FileBytes(fileName) == null) problems.Add("it names " + fileName + ", which it does not bring.");
            }
            // Every file it brings is one it names: nothing else rides along.
            if (!shipped)
                foreach (string fileName in variant.Files.Keys.Where(f => !named.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    problems.Add(fileName + " is in the variant but its " + PartVariant.JsonName + " never names it.");
            if (part.kind == Values) CheckValues(part, variant, problems);
            else if (part.kind == Strings) CheckStrings(part, variant, partsPath ?? baseFile, problems);
            else if (part.kind == Model) CheckModel(part, variant, problems, shipped);
        }

        private static void CheckValues(Part part, PartVariant variant, List<string> problems)
        {
            var values = variant.values ?? new JObject();
            var files = new HashSet<string>(variant.NamedFiles(part), StringComparer.OrdinalIgnoreCase);
            foreach (var property in values.Properties())
            {
                var field = part.Field(property.Name);
                if (field == null) { problems.Add("\"" + property.Name + "\" is not a field of " + part.Label + "."); continue; }
                if (ValueProblem(field, property.Value, files) is string bad) { problems.Add(field.Label + " " + bad); continue; }
                if (field.type == Picture) CheckPicture(field, variant.FileBytes((string)property.Value), (string)property.Value, problems);
            }
            // Every field has its value (Orbiters refuses a variant leaving one out), but an optional picture.
            foreach (var field in part.fields ?? new List<Field>())
                if (field != null && (values[field.key] == null || values[field.key].Type == JTokenType.Null) && !(field.type == Picture && field.optional))
                    problems.Add(field.Label + ": give it " + (field.type == Picture ? "a picture." : "a value."));
        }

        private static void CheckPicture(Field field, byte[] bytes, string fileName, List<string> problems)
        {
            if (bytes == null) return;
            if (!PictureSize(bytes, out int width, out int height) || !Decodes(bytes)) { problems.Add(fileName + " is not a PNG or JPEG picture."); return; }
            if (field.width > 0 && field.height > 0 && (width != field.width || height != field.height))
                problems.Add(field.Label + ": " + fileName + " is " + width + " × " + height + " (it must be " + field.width + " × " + field.height + ").");
            else if (width > MaxPicture || height > MaxPicture) problems.Add(field.Label + ": " + fileName + " is larger than " + MaxPicture + " pixels.");
        }

        private static void CheckStrings(Part part, PartVariant variant, string partsPath, List<string> problems)
        {
            if (string.IsNullOrEmpty(variant.language) || !LanguagePattern.IsMatch(variant.language)) problems.Add("give its language (a code such as fr, pt-BR or ja).");
            var texts = variant.strings ?? new Dictionary<string, string>();
            var basePath = string.IsNullOrEmpty(partsPath) ? null : AssetPath(partsPath, part.strings);
            var baseStrings = basePath != null && File.Exists(basePath) ? BaseStrings.Read(basePath, problems) : null;
            if (baseStrings == null) { problems.Add("the part's strings file could not be read."); return; }
            foreach (var key in baseStrings.Texts.Keys)
            {
                if (!texts.TryGetValue(key, out var text) || text == null || text.Trim().Length == 0 && (baseStrings.Texts[key] ?? "").Trim().Length > 0)
                { problems.Add("\"" + key + "\" has no text."); continue; }
                if (TextControl.IsMatch(text)) { problems.Add("\"" + key + "\" holds a control character."); continue; }
                int max = baseStrings.MaxOf(key);
                if (text.Length > max) problems.Add("\"" + key + "\" is longer than " + max + " characters.");
            }
            foreach (var key in texts.Keys.Where(k => !baseStrings.Texts.ContainsKey(k))) problems.Add("\"" + key + "\" is not a text of " + part.Label + ".");
        }

        private static void CheckModel(Part part, PartVariant variant, List<string> problems, bool shipped)
        {
            var model = variant.model;
            // A base variant without a mesh is the item's own model, as its prefab has it.
            if (model == null || string.IsNullOrEmpty(model.mesh))
            {
                if (!shipped) problems.Add("give it a model (one .fbx).");
                return;
            }
            if (!model.mesh.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) problems.Add(model.mesh + " is not an .fbx.");
            var fbx = variant.FileBytes(model.mesh);
            if (fbx != null)
            {
                if (fbx.LongLength > MaxFbxBytes) problems.Add(model.mesh + " is larger than " + MaxFbxBytes / (1024 * 1024) + " MB.");
                if (!IsFbx(fbx)) problems.Add(model.mesh + " is not an FBX file.");
            }
            if (variant.Files.Keys.Count(f => f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) > 1) problems.Add("a model variant brings one .fbx.");
            var slots = model.slots ?? new Dictionary<string, VariantSlot>();
            foreach (var slot in part.slots ?? new List<Slot>())
                if (slot != null && !slots.ContainsKey(slot.key ?? "")) problems.Add("it gives nothing for " + slot.Label + " (slot \"" + slot.key + "\").");
            foreach (var pair in slots)
            {
                var slot = (part.slots ?? new List<Slot>()).FirstOrDefault(s => s != null && s.key == pair.Key);
                if (slot == null) { problems.Add("\"" + pair.Key + "\" is not a slot of " + part.Label + "."); continue; }
                if (pair.Value == null || string.IsNullOrWhiteSpace(pair.Value.objectName)) { problems.Add(slot.Label + ": name the FBX's object it takes (\"object\")."); continue; }
                if ((pair.Value.materials ?? new List<VariantMaterial>()).Count == 0) problems.Add(slot.Label + ": give its materials.");
                int index = 0;
                foreach (var material in pair.Value.materials ?? new List<VariantMaterial>())
                    CheckMaterial(part, variant, material, slot.Label + " › material " + ++index, problems);
            }
        }

        private static void CheckMaterial(Part part, PartVariant variant, VariantMaterial material, string where, List<string> problems)
        {
            if (material == null) { problems.Add(where + ": empty."); return; }
            if (!(part.shaders ?? new List<string>()).Contains(material.shader) || !AllowedShaders.Contains(material.shader))
            { problems.Add(where + ": the shader \"" + material.shader + "\" is not one of " + string.Join(", ", part.shaders ?? new List<string>()) + "."); return; }
            var shader = Shader.Find(material.shader);
            if (shader == null) { problems.Add(where + ": the shader \"" + material.shader + "\" is not in this project."); return; }
            foreach (var pair in material.colors ?? new Dictionary<string, string>())
            {
                var type = PropertyType(shader, pair.Key);
                if (type != ShaderPropertyType.Color && type != ShaderPropertyType.Vector) problems.Add(where + ": " + material.shader + " has no colour " + pair.Key + ".");
                else if (!TryColor(pair.Value, out _)) problems.Add(where + ": " + pair.Key + " is not a colour (#RRGGBB or #RRGGBBAA).");
            }
            foreach (var pair in material.floats ?? new Dictionary<string, float>())
            {
                var type = PropertyType(shader, pair.Key);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range && type != ShaderPropertyType.Int) problems.Add(where + ": " + material.shader + " has no number " + pair.Key + ".");
                else if (float.IsNaN(pair.Value) || float.IsInfinity(pair.Value)) problems.Add(where + ": " + pair.Key + " is not a number.");
            }
            foreach (var pair in material.textures ?? new Dictionary<string, string>())
            {
                if (PropertyType(shader, pair.Key) != ShaderPropertyType.Texture) { problems.Add(where + ": " + material.shader + " has no texture " + pair.Key + "."); continue; }
                if (!IsPictureName(pair.Value)) { problems.Add(where + ": " + pair.Value + " is not a PNG or JPEG."); continue; }
                var bytes = variant.FileBytes(pair.Value);
                if (bytes == null) continue;
                if (!PictureSize(bytes, out int width, out int height) || !Decodes(bytes)) problems.Add(where + ": " + pair.Value + " is not a PNG or JPEG picture.");
                else if (width > part.maxTextureSize || height > part.maxTextureSize)
                    problems.Add(where + ": " + pair.Value + " is " + width + " × " + height + " (" + part.maxTextureSize + " pixels at most).");
            }
        }

        private static ShaderPropertyType? PropertyType(Shader shader, string property)
        {
            int index = shader.FindPropertyIndex(property ?? "");
            return index < 0 ? (ShaderPropertyType?)null : shader.GetPropertyType(index);
        }

        /// <summary>What a slot's mesh measures, for the form's live checks and the checks after import.</summary>
        public sealed class MeshFacts
        {
            public int Triangles, Submeshes;
            public float Radius;
            public List<string> MissingShapes = new List<string>();
        }

        public static MeshFacts Measure(Part part, Mesh mesh)
        {
            var facts = new MeshFacts();
            if (mesh == null) return facts;
            facts.Submeshes = mesh.subMeshCount;
            for (int i = 0; i < mesh.subMeshCount; i++) facts.Triangles += (int)(mesh.GetIndexCount(i) / 3);
            var vertices = mesh.vertices;
            float radius = 0;
            foreach (var v in vertices) radius = Mathf.Max(radius, v.sqrMagnitude);
            // Each shape at full weight: an expression may reach further than the rest pose.
            var deltas = new Vector3[vertices.Length];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                mesh.GetBlendShapeFrameVertices(shape, frame, deltas, null, null);
                for (int i = 0; i < vertices.Length; i++) radius = Mathf.Max(radius, (vertices[i] + deltas[i]).sqrMagnitude);
            }
            facts.Radius = Mathf.Sqrt(radius);
            foreach (string shape in part.blendshapes ?? new List<string>())
                if (mesh.GetBlendShapeIndex(shape) < 0) facts.MissingShapes.Add(shape);
            return facts;
        }

        /// <summary>Checks a slot's mesh (imported from the variant's FBX) against the part's limits.</summary>
        public static void CheckMesh(Part part, string slotLabel, Mesh mesh, int materials, List<string> problems)
        {
            if (mesh == null) { problems.Add(slotLabel + ": the FBX has no such object with a mesh."); return; }
            var facts = Measure(part, mesh);
            if (part.maxTriangles > 0 && facts.Triangles > part.maxTriangles) problems.Add(slotLabel + ": " + facts.Triangles + " triangles (" + part.maxTriangles + " at most).");
            if (facts.MissingShapes.Count > 0) problems.Add(slotLabel + ": its mesh has no blendshape " + string.Join(", ", facts.MissingShapes) + " (the item's animations use them).");
            if (part.maxRadius > 0 && facts.Radius > part.maxRadius * 1.0001f)
                problems.Add(slotLabel + ": it reaches " + facts.Radius.ToString("0.###", CultureInfo.InvariantCulture) + " from its middle (" + part.maxRadius.ToString("0.###", CultureInfo.InvariantCulture) + " at most).");
            if (materials != facts.Submeshes) problems.Add(slotLabel + ": its mesh has " + facts.Submeshes + " material slot" + (facts.Submeshes == 1 ? "" : "s") + " and the variant gives " + materials + " material" + (materials == 1 ? "" : "s") + ".");
        }

        // ---- Files ----

        internal static int CodePoints(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int i = 0; i < text.Length; i++) { count++; if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++; }
            return count;
        }

        internal static bool IsPng(byte[] bytes) =>
            bytes != null && bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

        /// <summary>A PNG's or JPEG's size from its header (IHDR, or the first frame marker).</summary>
        public static bool PictureSize(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (IsPng(bytes))
            {
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return width > 0 && height > 0;
            }
            if (bytes == null || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            int i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                byte marker = bytes[i + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                int length = (bytes[i + 2] << 8) | bytes[i + 3];
                bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (frame)
                {
                    height = (bytes[i + 5] << 8) | bytes[i + 6];
                    width = (bytes[i + 7] << 8) | bytes[i + 8];
                    return width > 0 && height > 0;
                }
                if (length < 2) return false;
                i += 2 + length;
            }
            return false;
        }

        // Whether Unity decodes it (a picture whose header lies, or a broken one, does not).
        private static bool Decodes(byte[] bytes)
        {
            if (!PictureSize(bytes, out int width, out int height) || width > 8192 || height > 8192) return false;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try { return texture.LoadImage(bytes, false) && texture.width == width && texture.height == height; }
            finally { Object.DestroyImmediate(texture); }
        }

        internal static bool IsFbx(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 23) return false;
            if (Encoding.ASCII.GetString(bytes, 0, 18) == "Kaydara FBX Binary") return true;
            // An ASCII FBX starts with its comment header (after a byte order mark or blank lines).
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            while (start < bytes.Length && (bytes[start] == ' ' || bytes[start] == '\t' || bytes[start] == '\r' || bytes[start] == '\n')) start++;
            return start + 5 <= bytes.Length && Encoding.ASCII.GetString(bytes, start, 5) == "; FBX";
        }
    }
}
