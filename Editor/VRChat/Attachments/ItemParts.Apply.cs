using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor.Storage;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    public static partial class ItemParts
    {
        /// <summary>Where the files made for placed items' variants go: <c>&lt;avatar&gt;/&lt;item&gt;/</c> below it.</summary>
        public const string VariantsFolder = "Assets/Orbiters/Variants";
        private const string RecordKey = "orbitersParts";

        /// <summary>
        /// The variant a part of an item uses: written into the files made for it (their importer's user data), which the
        /// item's renderers use, so it is read back from the item itself and follows Undo.
        /// </summary>
        public sealed class Applied
        {
            public string part, name, language, author, sha256;
            /// <summary>The hash of its thumbnail, kept in Library when it was applied (null for a base variant).</summary>
            public string thumbnail;
            /// <summary>For a base variant (the creator's, shipped in the asset), its name; else null.</summary>
            public string baseName;
            public int id;
            public bool official;
            /// <summary>Its <c>variant.json</c>, and the folder its files were copied to (an update of the item takes it again from there).</summary>
            public string json, files;
            /// <summary>True when read from the item's files; false for an item as its prefab made it (its first base variant).</summary>
            [JsonIgnore] public bool Recorded;
            [JsonIgnore] public bool IsBase => baseName != null;

            internal static Applied Of(PartVariant variant, string files) => new Applied
            {
                part = variant.part, name = variant.name, language = variant.language, author = variant.Author, sha256 = variant.Sha256,
                baseName = variant.BaseName, id = variant.Id, official = variant.Official, json = variant.IsBase ? null : variant.ToJson(), files = files,
                thumbnail = !variant.IsBase && variant.Thumbnail != null ? PartVariant.Hash(variant.Thumbnail) : null,
            };
        }

        private sealed class Record { public List<Applied> parts = new List<Applied>(); }

        /// <summary>
        /// The variant a part of a placed item uses now: what the files its renderers use say, else (the item as its prefab
        /// made it) the part's first base variant.
        /// </summary>
        public static Applied Current(Transform item, Definition definition, Part part)
        {
            if (item != null && part != null)
                foreach (var asset in AnchorAssets(item, part))
                {
                    var found = RecordOf(asset)?.parts.FirstOrDefault(a => a != null && a.part == part.key);
                    if (found != null) { found.Recorded = true; return found; }
                }
            var first = part?.bases?.FirstOrDefault(b => b != null);
            return first == null ? null : new Applied { part = part.key, name = first.name, language = first.language, baseName = first.name, official = true };
        }

        // The renderers showing a part, and the files they use that were made for variants.
        private static IEnumerable<Object> AnchorAssets(Transform item, Part part)
        {
            foreach (var renderer in Anchors(item, part).Where(r => r != null))
            {
                foreach (var material in renderer.sharedMaterials) if (material != null) yield return material;
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null) yield return mesh;
            }
        }

        internal static IEnumerable<Renderer> Anchors(Transform item, Part part)
        {
            if (part.kind == Values)
                foreach (var target in part.targets ?? new List<Target>())
                    if (target != null && Find(item, target.path) is Transform t && t.TryGetComponent(out Renderer renderer)) yield return renderer;
            if (part.kind == Model)
                foreach (var path in (part.slots ?? new List<Slot>()).Where(s => s != null).SelectMany(s => s.paths ?? new List<string>()))
                    if (Find(item, path) is Transform t && t.TryGetComponent(out Renderer renderer)) yield return renderer;
            if (!string.IsNullOrEmpty(part.renderer) && Renderers.TryGetValue(part.renderer, out var made) && made.Anchors != null)
                foreach (var renderer in made.Anchors(item) ?? Enumerable.Empty<Renderer>()) yield return renderer;
        }

        private static readonly Dictionary<string, (DateTime written, Record record)> Records = new Dictionary<string, (DateTime, Record)>();

        private static Record RecordOf(Object asset)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(path) || !path.StartsWith(VariantsFolder + "/", StringComparison.Ordinal)) return null;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path + ".meta"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            if (Records.TryGetValue(path, out var known) && known.written == written) return known.record;
            Record record = null;
            string data = AssetImporter.GetAtPath(path)?.userData;
            if (!string.IsNullOrEmpty(data))
                try { record = JObject.Parse(data)[RecordKey]?.ToObject<Record>(); }
                catch (Exception e) when (e is JsonException || e is ArgumentException) { record = null; }
            Records[path] = (written, record);
            return record;
        }

        private static void WriteRecord(string path, IEnumerable<Applied> parts)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return;
            string data = RecordData(parts);
            // A file kept from an earlier apply already says this: its .meta stays as it is.
            if (importer.userData == data) return;
            importer.userData = data;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(path);
            Records.Remove(path);
        }

        private static string RecordData(IEnumerable<Applied> parts)
        {
            var record = new Record { parts = parts.Where(p => p != null).GroupBy(p => p.part).Select(g => g.Last()).ToList() };
            return new JObject { [RecordKey] = JToken.FromObject(record, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore })) }.ToString(Formatting.None);
        }

        /// <summary>The variant an applied record stands for, its files taken from where they were copied (or, base, from the asset).</summary>
        public static PartVariant VariantOf(Applied applied, string partsPath, Part part)
        {
            if (applied == null) return null;
            if (applied.IsBase) return Bases(partsPath, part).FirstOrDefault(b => b.BaseName == applied.baseName);
            var variant = PartVariant.Parse(applied.json, null);
            if (variant == null) return null;
            variant.Folder = applied.files; variant.Id = applied.id; variant.Official = applied.official; variant.Author = applied.author; variant.Sha256 = applied.sha256;
            variant.Thumbnail = ThumbnailBytes(applied);
            // Checked when it was applied; its thumbnail may be gone from Library since.
            variant.Checked = true;
            return variant;
        }

        // ---- The item's folder ----

        /// <summary>Where the files made for an item's variants go: <c>Assets/Orbiters/Variants/&lt;avatar&gt;/&lt;item&gt;</c>.</summary>
        public static string ItemFolder(Transform item)
        {
            var avatar = item.GetComponentInParent<VRCAvatarDescriptor>(true);
            var top = avatar != null ? avatar.transform : item.root;
            return VariantsFolder + "/" + Clean(top.name, 24) + "/" + Clean(item.name, 24);
        }

        private static readonly Regex Unsafe = new Regex("[^A-Za-z0-9 _.()-]+");

        public static string Clean(string name, int max)
        {
            string clean = Unsafe.Replace(name ?? "", " ").Trim().Trim('.');
            if (clean.Length > max) clean = clean.Substring(0, max).Trim();
            return clean.Length == 0 ? "Item" : clean;
        }

        public static string ShortHash(string text)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")).Take(4).Select(b => b.ToString("x2")));
        }

        /// <summary>Makes an Assets/ folder and the folders above it.</summary>
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Writes a file into an Assets/ folder and imports it (kept when it is already there with these bytes).</summary>
        public static string WriteAsset(string folder, string fileName, byte[] bytes)
        {
            EnsureFolder(folder);
            string path = folder + "/" + fileName;
            // Unity's 259-character path limit: a long name gets a short one.
            if (Path.GetFullPath(path).Length > 240) path = folder + "/" + ShortHash(fileName) + Path.GetExtension(fileName);
            if (File.Exists(path) && new FileInfo(path).Length == bytes.LongLength && File.ReadAllBytes(path).SequenceEqual(bytes) && AssetImporter.GetAtPath(path) != null) return path;
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return path;
        }

        /// <summary>
        /// Saves a material made for a variant and returns its path: the file already at <paramref name="path"/> when it holds
        /// this very material and record, else a new file (at a free path beside it). A file other items, prefabs or Undo
        /// steps may use is never changed, so an apply that fails later leaves nothing but unused new files.
        /// </summary>
        private static string SaveMaterial(Material material, string path, IEnumerable<Applied> record)
        {
            string data = RecordData(record);
            string stem = path.Substring(0, path.Length - Path.GetExtension(path).Length);
            for (int copy = 0; ; copy++)
            {
                string candidate = copy == 0 ? path : stem + " " + copy + Path.GetExtension(path);
                material.name = Path.GetFileNameWithoutExtension(candidate);
                if (!File.Exists(candidate))
                {
                    AssetDatabase.CreateAsset(material, candidate);
                    WriteRecord(candidate, record);
                    return candidate;
                }
                var existing = AssetDatabase.LoadAssetAtPath<Material>(candidate);
                if (existing != null && AssetImporter.GetAtPath(candidate)?.userData == data && EditorJsonUtility.ToJson(existing) == EditorJsonUtility.ToJson(material))
                {
                    Object.DestroyImmediate(material);
                    return candidate;
                }
            }
        }

        internal static Texture2D ImportTexture(string path, bool normal, bool picture)
        {
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                bool changed = importer.textureType != type || importer.sRGBTexture == normal || importer.npotScale != (picture ? TextureImporterNPOTScale.None : TextureImporterNPOTScale.ToNearest);
                if (changed)
                {
                    importer.textureType = type; importer.sRGBTexture = !normal;
                    importer.alphaSource = normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = !normal;
                    importer.mipmapEnabled = true; importer.maxTextureSize = MaxPicture;
                    importer.npotScale = picture ? TextureImporterNPOTScale.None : TextureImporterNPOTScale.ToNearest;
                    importer.wrapMode = picture ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    importer.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---- Applying ----

        public sealed class ApplyResult
        {
            public bool Applied;
            public readonly List<string> Problems = new List<string>();
            /// <summary>Something to say on the card (a renderer's program missing), or null.</summary>
            public string Note;
        }

        /// <summary>
        /// Puts a variant on a placed item's part, in place and as one Undo step: it is checked first (as Orbiters checks
        /// posts), its files go into the item's folder, then values go on the item's materials (copies of them), a model in
        /// its renderers, and the part's renderer makes the item's pictures (which can take seconds: the item changes only
        /// once everything is ready). Nothing changes when a check fails: the problems say why.
        /// </summary>
        public static Task<ApplyResult> ApplyAsync(Placed placed, PartVariant variant, Action<string> progress = null, CancellationToken cancellation = default) =>
            ApplyAsync(placed.Item, placed.Definition, placed.PrefabPath, placed.PartsPath, new List<(Part, PartVariant)> { (placed.Part, variant) }, progress, cancellation);

        public static async Task<ApplyResult> ApplyAsync(Transform item, Definition definition, string prefabPath, string partsPath,
            IReadOnlyList<(Part part, PartVariant variant)> choices, Action<string> progress = null, CancellationToken cancellation = default)
        {
            if (item == null) return await ApplyNowAsync(item, definition, prefabPath, partsPath, choices, progress, cancellation);
            // One apply at a time per item (a card's choice, an update's carry, another part's choice): a renderer draws every
            // part of the item from the choices it reads as it starts, so an apply waits for the one before it to finish.
            int key = item.GetInstanceID();
            Running.TryGetValue(key, out var before);
            var mine = new TaskCompletionSource<bool>();
            Running[key] = mine.Task;
            try
            {
                if (before != null && !before.IsCompleted) { progress?.Invoke("Waiting for the item's other change…"); await before; }
                return await ApplyNowAsync(item, definition, prefabPath, partsPath, choices, progress, cancellation);
            }
            finally
            {
                mine.SetResult(true);
                if (Running.TryGetValue(key, out var last) && last == mine.Task) Running.Remove(key);
            }
        }

        // The last apply started on each item (by instance id), done once it finished.
        private static readonly Dictionary<int, Task> Running = new Dictionary<int, Task>();

        private static async Task<ApplyResult> ApplyNowAsync(Transform item, Definition definition, string prefabPath, string partsPath,
            IReadOnlyList<(Part part, PartVariant variant)> choices, Action<string> progress, CancellationToken cancellation)
        {
            var result = new ApplyResult();
            progress ??= _ => { };
            if (item == null || definition == null || choices == null || choices.Count == 0) { result.Problems.Add("Nothing to apply."); return result; }
            var prefab = string.IsNullOrEmpty(prefabPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            progress("Checking…");
            foreach (var (part, variant) in choices)
            {
                if (part == null || variant == null) { result.Problems.Add("Nothing to apply."); continue; }
                var problems = new List<string>();
                Check(part, variant, prefab, problems, shipped: variant.IsBase, partsPath: partsPath, applied: variant.Checked);
                result.Problems.AddRange(problems.Select(p => part.Label + ": " + p));
            }
            if (result.Problems.Count > 0) return result;

            var plan = new Plan { Item = item, Folder = ItemFolder(item), Definition = definition, PrefabPath = prefabPath, PartsPath = partsPath, Prefab = prefab };
            var jobs = new List<RenderJob>();
            // What was made goes once nothing uses it: all of it when the apply stops before the item changed.
            void Forget() { GeneratedAssets.Track(plan.Made.Concat(jobs.SelectMany(j => j.Made))); GeneratedAssets.SweepSoon(); }
            int group = -1;
            try
            {
                // New files only (existing ones are kept or left alone, see SaveMaterial): stopping between steps changes nothing.
                cancellation.ThrowIfCancellationRequested();
                foreach (var (part, variant) in choices.Where(c => c.part.kind != Model)) { PrepareFiles(plan, part, variant); cancellation.ThrowIfCancellationRequested(); }
                foreach (var (part, variant) in choices)
                {
                    progress("Preparing " + part.Label.ToLowerInvariant() + "…");
                    if (part.kind == Model) PrepareModel(plan, part, variant, result.Problems);
                    if (part.kind == Values) PrepareTargets(plan, part, variant);
                    cancellation.ThrowIfCancellationRequested();
                }
                if (result.Problems.Count > 0) { Forget(); return result; }

                // Every renderer named by a chosen part makes the item's pictures for all its parts' current choices.
                foreach (string name in choices.Select(c => c.part.renderer).Where(r => !string.IsNullOrEmpty(r)).Distinct())
                {
                    if (!Renderers.TryGetValue(name, out var renderer) || renderer.Render == null) { result.Problems.Add("There is no renderer \"" + name + "\" in this Toolkit."); continue; }
                    var job = Job(item, definition, prefabPath, partsPath, name, choices, progress, cancellation);
                    jobs.Add(job);
                    progress("Drawing " + string.Join(" and ", job.Parts.Values.Select(p => p.part.Label.ToLowerInvariant())) + "…");
                    await renderer.Render(job);
                    cancellation.ThrowIfCancellationRequested();
                    if (job.Problems.Count > 0) result.Problems.AddRange(job.Problems);
                    if (job.Note != null) result.Note = job.Note;
                }
                if (result.Problems.Count > 0) { Forget(); return result; }
                if (item == null) { result.Problems.Add("The item is not in the scene any more."); Forget(); return result; }
                cancellation.ThrowIfCancellationRequested();

                // The item changes in one Undo step, once everything is ready, and all at once: a change that fails takes
                // back the ones made before it.
                Undo.IncrementCurrentGroup();
                group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(choices.Count == 1 ? "Customize " + choices[0].part.Label + ": " + choices[0].variant.name : "Customize " + item.name);
                foreach (var assign in plan.Assign) assign();
                foreach (var job in jobs)
                {
                    foreach (string path in job.Made.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
                        WriteRecord(path, job.Parts.Values.Select(p => Applied.Of(p.variant, FilesFolder(plan, p.part, p.variant))));
                    job.Assign?.Invoke();
                }
                foreach (var after in plan.After) after();
                Undo.CollapseUndoOperations(group);
                group = -1;
                GeneratedAssets.Track(plan.Made.Concat(jobs.SelectMany(j => j.Made)));
                foreach (var (_, variant) in choices) KeepThumbnail(variant);
                result.Applied = true;
                Changed?.Invoke(item);
                return result;
            }
            catch (OperationCanceledException) { Forget(); result.Problems.Add("Cancelled."); return result; }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // The changes made in this apply's Undo step (synchronous: nothing else was recorded since it began) go back.
                if (group >= 0) Undo.RevertAllDownToGroup(group);
                Forget();
                Debug.LogException(e);
                result.Problems.Add("It could not be applied: " + e.Message);
                return result;
            }
        }

        /// <summary>A placed item's parts changed (a variant applied): cards showing it refresh.</summary>
        public static event Action<Transform> Changed;

        private sealed class Plan
        {
            public Transform Item;
            public string Folder, PrefabPath, PartsPath;
            public Definition Definition;
            public GameObject Prefab;
            public readonly List<string> Made = new List<string>();
            /// <summary>Changes to the item, made in the apply's Undo step.</summary>
            public readonly List<Action> Assign = new List<Action>();
            /// <summary>Run after every change (another part's values put back on new materials).</summary>
            public readonly List<Action> After = new List<Action>();
            /// <summary>Where each chosen variant's files were copied, by part key.</summary>
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            /// <summary>Each chosen variant's own folder (<see cref="VariantFolder"/>), by part key.</summary>
            public readonly Dictionary<string, string> Folders = new Dictionary<string, string>();
            /// <summary>The imported files of each chosen variant, by part key then file name.</summary>
            public readonly Dictionary<string, Dictionary<string, string>> Imported = new Dictionary<string, Dictionary<string, string>>();
        }

        private static string FilesFolder(Plan plan, Part part, PartVariant variant) =>
            plan.Files.TryGetValue(part.key, out var folder) ? folder : variant.IsBase ? variant.Folder : variant.Folder;

        // A variant's own folder: by part, its name and a hash of what it is (its record, the bytes of every file it names and
        // how files are made from them), so the same variant applied again uses the same files and another variant never
        // lands in its folder.
        private static string VariantFolder(Plan plan, Part part, PartVariant variant)
        {
            if (plan.Folders.TryGetValue(part.key, out var known)) return known;
            string folder = plan.Folder + "/" + Clean(part.key, 24) + "/" + Clean(variant.name, 16) + " " + VariantIdentity(part, variant);
            plan.Folders[part.key] = folder;
            return folder;
        }

        // Raised when what is made from a variant's files changes (ModelMesh's bake): earlier folders keep what they hold.
        private const int MadeRevision = 2;

        /// <summary>16 hex digits naming a variant by its content: its record (JSON, id, author, published hash, thumbnail) and its files' SHA-256.</summary>
        internal static string VariantIdentity(Part part, PartVariant variant)
        {
            var text = new StringBuilder("made ").Append(MadeRevision).Append('|')
                .Append(JsonConvert.SerializeObject(Applied.Of(variant, null), new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
            foreach (string name in variant.NamedFiles(part).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = variant.FileBytes(name);
                text.Append('|').Append(name.ToLowerInvariant()).Append(':').Append(bytes == null ? "missing" : PartVariant.Hash(bytes));
            }
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Take(8).Select(b => b.ToString("x2")));
        }

        // The pictures a values variant names, copied in and imported.
        private static void PrepareFiles(Plan plan, Part part, PartVariant variant)
        {
            var names = variant.NamedFiles(part);
            if (variant.IsBase || names.Count == 0) { plan.Files[part.key] = variant.Folder; return; }
            string folder = VariantFolder(plan, part, variant);
            var imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                var bytes = variant.FileBytes(name);
                if (bytes == null) continue;
                imported[name] = WriteAsset(folder, name, bytes);
                plan.Made.Add(imported[name]);
            }
            plan.Files[part.key] = folder;
            plan.Imported[part.key] = imported;
            plan.Made.Add(folder);
        }

        // An imported picture of the variant (copied in, or a base variant's file beside its JSON in the asset).
        private static Texture2D VariantTexture(Plan plan, Part part, PartVariant variant, string name, bool normal, bool picture)
        {
            string path = null;
            if (plan.Imported.TryGetValue(part.key, out var imported) && imported.TryGetValue(name, out var copy)) path = copy;
            else if (!string.IsNullOrEmpty(variant.Folder) && (variant.Folder.StartsWith("Assets/", StringComparison.Ordinal) || variant.Folder.StartsWith("Packages/", StringComparison.Ordinal)))
                path = variant.Folder + "/" + name;
            return path == null ? null : ImportTexture(path, normal, picture);
        }

        // ---- Values on the item's materials ----

        private static void PrepareTargets(Plan plan, Part part, PartVariant variant, bool after = false)
        {
            var targets = (part.targets ?? new List<Target>()).Where(t => t != null && part.Field(t.field) != null).ToList();
            if (targets.Count == 0) return;
            string folder = VariantFolder(plan, part, variant);
            var record = Applied.Of(variant, FilesFolder(plan, part, variant));
            // Each renderer's materials as they will be: copies of the current ones with the values set, carrying what the
            // current ones say of the other parts.
            foreach (var group in targets.GroupBy(t => (t.path ?? "").Trim('/')))
            {
                var renderer = Find(plan.Item, group.Key)?.GetComponent<Renderer>();
                if (renderer == null) continue;
                var copies = new Dictionary<int, Material>();
                var current = renderer.sharedMaterials;
                foreach (var bySlot in group.SelectMany(t => TargetSlots(renderer, t).Select(slot => (slot, t))).GroupBy(x => x.slot))
                {
                    var source = current[bySlot.Key];
                    string name = Regex.Replace(source.name, " [0-9a-f]{8}$", "") + " " + ShortHash(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source)) + source.name);
                    EnsureFolder(folder);
                    string path = folder + "/" + Clean(name, 48) + ".mat";
                    var copy = new Material(source) { name = Path.GetFileNameWithoutExtension(path) };
                    foreach (var (_, target) in bySlot) SetValue(plan, part, variant, target, copy);
                    var others = RecordOf(source)?.parts.Where(p => p != null && p.part != part.key) ?? Enumerable.Empty<Applied>();
                    path = SaveMaterial(copy, path, others.Concat(new[] { record }).ToList());
                    copies[bySlot.Key] = AssetDatabase.LoadAssetAtPath<Material>(path);
                    plan.Made.Add(path);
                }
                if (copies.Count == 0) continue;
                plan.Made.Add(folder);
                (after ? plan.After : plan.Assign).Add(() =>
                {
                    if (renderer == null) return;
                    Undo.RecordObject(renderer, "Customize " + part.Label);
                    var materials = renderer.sharedMaterials;
                    foreach (var pair in copies) if (pair.Key < materials.Length && pair.Value != null) materials[pair.Key] = pair.Value;
                    renderer.sharedMaterials = materials;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                });
            }
        }

        // A field's value (the variant's, else the field's default) on a material's property.
        private static void SetValue(Plan plan, Part part, PartVariant variant, Target target, Material material)
        {
            var field = part.Field(target.field);
            var value = variant.values?[field.key] ?? field.defaultValue;
            if (value == null) return;
            switch (field.type)
            {
                case Color:
                    if (TryColor((string)value, out var colour)) material.SetColor(target.property, colour);
                    break;
                case Number:
                    material.SetFloat(target.property, value.Value<float>());
                    break;
                case Picture:
                    var texture = value.Type == JTokenType.String ? VariantTexture(plan, part, variant, (string)value, false, true) : null;
                    if (texture != null) material.SetTexture(target.property, texture);
                    break;
            }
        }

        // ---- A model in the item's renderers ----

        private static void PrepareModel(Plan plan, Part part, PartVariant variant, List<string> problems)
        {
            var slots = (part.slots ?? new List<Slot>()).Where(s => s != null).ToList();
            var changed = new List<Renderer>();
            // A base variant without a mesh: the item's own model, as its prefab has it.
            if (variant.model == null || string.IsNullOrEmpty(variant.model.mesh))
            {
                if (plan.Prefab == null) { problems.Add(part.Label + ": the item's prefab is not in the project, so its own model can't come back."); return; }
                foreach (var path in slots.SelectMany(s => s.paths ?? new List<string>()))
                {
                    var own = Find(plan.Prefab.transform, path)?.GetComponent<Renderer>();
                    var renderer = Find(plan.Item, path)?.GetComponent<Renderer>();
                    if (own == null || renderer == null) continue;
                    var mesh = own is SkinnedMeshRenderer skinned ? skinned.sharedMesh : own.GetComponent<MeshFilter>()?.sharedMesh;
                    var materials = own.sharedMaterials;
                    var bounds = own is SkinnedMeshRenderer s ? s.localBounds : (Bounds?)null;
                    changed.Add(renderer);
                    plan.Assign.Add(() => SetModel(renderer, mesh, materials, bounds, part.Label));
                }
                ReapplyValues(plan, part, changed);
                return;
            }

            string folder = VariantFolder(plan, part, variant);
            var imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in variant.NamedFiles(part))
            {
                var bytes = variant.FileBytes(name);
                if (bytes == null) continue;
                imported[name] = WriteAsset(folder, name, bytes);
                plan.Made.Add(imported[name]);
            }
            plan.Made.Add(folder);
            plan.Files[part.key] = folder;
            plan.Imported[part.key] = imported;
            string fbx = imported.TryGetValue(variant.model.mesh, out var f) ? f : null;
            if (fbx == null) { problems.Add(part.Label + ": its FBX could not be imported."); return; }
            ImportModel(fbx);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (root == null) { problems.Add(part.Label + ": Unity could not read " + variant.model.mesh + "."); return; }
            var record = Applied.Of(variant, folder);
            WriteRecord(fbx, new[] { record });
            foreach (var slot in slots)
            {
                if (!variant.model.slots.TryGetValue(slot.key, out var given) || given == null) continue;
                var mesh = ModelMesh(root, given.objectName, folder + "/" + Clean(slot.key, 16) + " mesh.asset");
                if (mesh != null && AssetDatabase.GetAssetPath(mesh) != fbx) { WriteRecord(AssetDatabase.GetAssetPath(mesh), new[] { record }); plan.Made.Add(AssetDatabase.GetAssetPath(mesh)); }
                var slotProblems = new List<string>();
                CheckMesh(part, slot.Label, mesh, (given.materials ?? new List<VariantMaterial>()).Count, slotProblems);
                problems.AddRange(slotProblems.Select(p => part.Label + " › " + p));
                if (slotProblems.Count > 0 || mesh == null) continue;
                var materials = new Material[given.materials.Count];
                for (int i = 0; i < materials.Length; i++)
                {
                    string path = SaveMaterial(MakeMaterial(plan, part, variant, given.materials[i], Clean(slot.key, 16) + " " + (i + 1)),
                        folder + "/" + Clean(slot.key, 16) + " " + (i + 1) + ".mat", new[] { record });
                    materials[i] = AssetDatabase.LoadAssetAtPath<Material>(path);
                    plan.Made.Add(path);
                }
                foreach (string path in slot.paths ?? new List<string>())
                {
                    var renderer = Find(plan.Item, path)?.GetComponent<Renderer>();
                    if (renderer == null) continue;
                    changed.Add(renderer);
                    var shown = mesh;
                    plan.Assign.Add(() => SetModel(renderer, shown, materials, shown.bounds, part.Label));
                }
            }
            AssetDatabase.SaveAssets();
            ReapplyValues(plan, part, changed);
        }

        /// <summary>
        /// Imports a variant's FBX as applying does: its mesh and nothing else (no materials or textures from the file, no
        /// animation, rig, camera or light), its objects keeping their names.
        /// </summary>
        public static void ImportModel(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return;
            // Already imported so (a variant's FBX kept from an earlier apply): other items' meshes from it are not imported again.
            if (importer.preserveHierarchy && importer.materialImportMode == ModelImporterMaterialImportMode.None && !importer.importAnimation &&
                importer.animationType == ModelImporterAnimationType.None && !importer.importCameras && !importer.importLights && !importer.importVisibility &&
                importer.importBlendShapes && !importer.bakeAxisConversion && !importer.isReadable) return;
            importer.preserveHierarchy = true;
            // A variant brings a mesh and nothing else: no materials or textures from the file, no animation, rig, camera or light.
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false; importer.importLights = false; importer.importVisibility = false;
            importer.importBlendShapes = true;
            // Its objects' turns stay on their transforms (ModelMesh bakes them): an FBX Toolkit wrote reads back as it was.
            importer.bakeAxisConversion = false;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// The mesh of an imported variant FBX's object, as it stands in the file: its transform below the file's root (a
        /// Blender object's turn, a scale) baked into a copy, saved at <paramref name="saveTo"/> (else a temporary copy the
        /// caller destroys); the imported mesh itself when the object stands at the root. Null when there is no such object.
        /// A mesh already saved at <paramref name="saveTo"/> is returned as it is: a variant's folder is named by its content
        /// (<see cref="VariantIdentity"/>), so it was baked from these very bytes, and other items may use it.
        /// </summary>
        public static Mesh ModelMesh(GameObject root, string objectName, string saveTo = null)
        {
            Transform found = null;
            Mesh mesh = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != objectName) continue;
                if (t.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null) { found = t; mesh = skinned.sharedMesh; break; }
                if (t.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) { found = t; mesh = filter.sharedMesh; break; }
            }
            if (mesh == null) return null;
            var matrix = root.transform.worldToLocalMatrix * found.localToWorldMatrix;
            bool identity = true;
            for (int i = 0; i < 16 && identity; i++) identity = Mathf.Abs(matrix[i] - Matrix4x4.identity[i]) < 1e-5f;
            if (identity) return mesh;
            if (saveTo != null && AssetDatabase.LoadAssetAtPath<Mesh>(saveTo) is Mesh kept) return kept;
            var baked = BakeTransform(mesh, matrix);
            if (saveTo == null) { baked.hideFlags = HideFlags.HideAndDontSave; return baked; }
            AssetDatabase.CreateAsset(baked, saveTo);
            return baked;
        }

        /// <summary>
        /// A copy of <paramref name="mesh"/> with <paramref name="matrix"/> baked in: positions and blendshape offsets by the
        /// whole matrix, normals by its inverse transpose (right under non-uniform scale), tangents as directions; a mirroring
        /// matrix also turns the triangles around and flips the tangents' handedness, so the faces still face out.
        /// </summary>
        internal static Mesh BakeTransform(Mesh mesh, Matrix4x4 matrix)
        {
            var baked = Object.Instantiate(mesh);
            baked.name = mesh.name;
            float determinant = matrix.determinant;
            bool singular = Mathf.Abs(determinant) < 1e-12f;
            // A flattened axis has no inverse: normals then only turn.
            var normalMatrix = singular ? Matrix4x4.Rotate(matrix.rotation) : matrix.inverse.transpose;
            bool mirrored = determinant < 0f;
            var turn = matrix.rotation;
            Vector3 Normal(Vector3 n) => normalMatrix.MultiplyVector(n).normalized;
            Vector3 Direction(Vector3 d) => matrix.MultiplyVector(d).normalized;

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            baked.vertices = vertices;
            var baseNormals = mesh.normals;
            var normals = new Vector3[baseNormals.Length];
            for (int i = 0; i < normals.Length; i++) normals[i] = Normal(baseNormals[i]);
            baked.normals = normals;
            var baseTangents = mesh.tangents;
            var tangents = new Vector4[baseTangents.Length];
            for (int i = 0; i < tangents.Length; i++)
            {
                var t = baseTangents[i];
                var d = Direction(new Vector3(t.x, t.y, t.z));
                tangents[i] = new Vector4(d.x, d.y, d.z, mirrored ? -t.w : t.w);
            }
            baked.tangents = tangents;
            if (mirrored)
                for (int sub = 0; sub < baked.subMeshCount; sub++)
                {
                    if (baked.GetTopology(sub) != MeshTopology.Triangles) continue;
                    var indices = baked.GetIndices(sub, false);
                    for (int i = 0; i + 2 < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                    baked.SetIndices(indices, MeshTopology.Triangles, sub, false, (int)baked.GetBaseVertex(sub));
                }

            // Blendshape offsets move as the vertices do; a shape's normal and tangent offsets are the change of a unit
            // direction, so they are taken as the change of the transformed direction (exact where the base one is known).
            baked.ClearBlendShapes();
            int count = mesh.vertexCount;
            var deltas = new Vector3[count]; var normalDeltas = new Vector3[count]; var tangentDeltas = new Vector3[count];
            var outDeltas = new Vector3[count]; var outNormals = new Vector3[count]; var outTangents = new Vector3[count];
            bool hasNormals = baseNormals.Length == count, hasTangents = baseTangents.Length == count;
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                mesh.GetBlendShapeFrameVertices(shape, frame, deltas, normalDeltas, tangentDeltas);
                for (int i = 0; i < count; i++)
                {
                    outDeltas[i] = matrix.MultiplyVector(deltas[i]);
                    var dn = normalDeltas[i];
                    outNormals[i] = dn == Vector3.zero ? Vector3.zero
                        : hasNormals && baseNormals[i] != Vector3.zero ? Normal(baseNormals[i] + dn) - normals[i] : turn * dn;
                    var dt = tangentDeltas[i];
                    if (dt == Vector3.zero) { outTangents[i] = Vector3.zero; continue; }
                    var bt = hasTangents ? new Vector3(baseTangents[i].x, baseTangents[i].y, baseTangents[i].z) : Vector3.zero;
                    outTangents[i] = bt != Vector3.zero ? Direction(bt + dt) - new Vector3(tangents[i].x, tangents[i].y, tangents[i].z) : turn * dt;
                }
                baked.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, frame), outDeltas, outNormals, outTangents);
            }
            baked.RecalculateBounds();
            return baked;
        }

        // A material from the variant's description: one of the part's shaders, its colours, numbers and textures, and the
        // shader's keywords for what is given (normal map, matcap, emission…).
        private static Material MakeMaterial(Plan plan, Part part, PartVariant variant, VariantMaterial given, string name)
        {
            var material = new Material(Shader.Find(given.shader)) { name = name };
            foreach (var pair in given.colors ?? new Dictionary<string, string>())
                if (TryColor(pair.Value, out var colour)) material.SetColor(pair.Key, colour);
            foreach (var pair in given.floats ?? new Dictionary<string, float>()) material.SetFloat(pair.Key, pair.Value);
            foreach (var pair in given.textures ?? new Dictionary<string, string>())
            {
                bool normal = pair.Key.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0;
                var texture = VariantTexture(plan, part, variant, pair.Value, normal, false);
                if (texture != null) material.SetTexture(pair.Key, texture);
            }
            Keywords(material, given);
            return material;
        }

        private static void Keywords(Material material, VariantMaterial given)
        {
            bool Has(params string[] properties) => properties.Any(p => (given.textures ?? new Dictionary<string, string>()).ContainsKey(p));
            bool Lit(string property) => (given.colors ?? new Dictionary<string, string>()).TryGetValue(property, out var text) && TryColor(text, out var c) && (c.r > 0 || c.g > 0 || c.b > 0);
            void Set(string keyword, bool on)
            {
                if (!material.shader.keywordSpace.keywordNames.Contains(keyword)) return;
                if (on) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
            }
            Set("_EMISSION", Lit("_EmissionColor") || Has("_EmissionMap"));
            Set("_DETAIL", Has("_DetailAlbedoMap"));
            Set("USE_NORMAL_MAPS", Has("_BumpMap", "_DetailNormalMap"));
            Set("USE_MATCAP", Has("_Matcap"));
            Set("USE_DETAIL_MAPS", Has("_DetailAlbedoMap", "_DetailNormalMap"));
            Set("USE_OCCLUSION_MAP", Has("_OcclusionMap"));
            Set("USE_SPECULAR", Has("_MetallicMap", "_GlossMap") || (given.floats ?? new Dictionary<string, float>()).TryGetValue("_MetallicStrength", out float metal) && metal > 0);
            Set("USE_RIMLIGHT", (given.floats ?? new Dictionary<string, float>()).TryGetValue("_RimIntensity", out float rim) && rim > 0);
        }

        private static void SetModel(Renderer renderer, Mesh mesh, Material[] materials, Bounds? bounds, string label)
        {
            if (renderer == null) return;
            var filter = renderer.GetComponent<MeshFilter>();
            Undo.RecordObjects(filter != null ? new Object[] { renderer, filter } : new Object[] { renderer }, "Customize " + label);
            if (renderer is SkinnedMeshRenderer skinned)
            {
                // Blendshape weights follow their names: the new mesh may order them otherwise.
                var weights = new Dictionary<string, float>();
                var old = skinned.sharedMesh;
                for (int i = 0; old != null && i < old.blendShapeCount; i++) weights[old.GetBlendShapeName(i)] = skinned.GetBlendShapeWeight(i);
                skinned.sharedMesh = mesh;
                for (int i = 0; mesh != null && i < mesh.blendShapeCount; i++) skinned.SetBlendShapeWeight(i, weights.TryGetValue(mesh.GetBlendShapeName(i), out float w) ? w : 0);
                if (bounds.HasValue) skinned.localBounds = bounds.Value;
            }
            else if (filter != null) filter.sharedMesh = mesh;
            renderer.sharedMaterials = materials;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            if (filter != null) PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
        }

        // Another values part's choice on renderers whose materials a model replaces: put on the new materials again.
        private static void ReapplyValues(Plan plan, Part model, List<Renderer> changed)
        {
            var paths = new HashSet<string>(changed.Where(r => r != null).Select(r => RelativePath(plan.Item, r.transform)));
            foreach (var other in plan.Definition.parts.Where(p => p != model && p.kind == Values && (p.targets ?? new List<Target>()).Any(t => t != null && paths.Contains((t.path ?? "").Trim('/')))))
            {
                var current = Current(plan.Item, plan.Definition, other);
                if (current == null || !current.Recorded) continue;
                var variant = VariantOf(current, plan.PartsPath, other);
                if (variant == null) continue;
                // Once the model's materials are on the renderers.
                plan.After.Add(() => { var again = new Plan { Item = plan.Item, Folder = plan.Folder, Definition = plan.Definition, PrefabPath = plan.PrefabPath, PartsPath = plan.PartsPath, Prefab = plan.Prefab };
                    PrepareTargets(again, other, variant); foreach (var assign in again.Assign) assign(); GeneratedAssets.Track(again.Made); });
            }
        }

        // ---- Renderers ----

        /// <summary>
        /// One of Orbiters' renderers (<see cref="RegisterRenderer"/>): a built-in program that turns the current choices of
        /// every part naming it into the item's pictures. Creators' own code never runs.
        /// </summary>
        public sealed class PartRenderer
        {
            /// <summary>The renderers of a placed item showing what it makes (their files carry the parts' record).</summary>
            public Func<Transform, IEnumerable<Renderer>> Anchors;
            /// <summary>
            /// Makes the item's pictures for <see cref="RenderJob.Parts"/> in the job's folder; may take seconds (an outside
            /// program). It changes nothing on the item itself: it sets <see cref="RenderJob.Assign"/>, which runs in the
            /// apply's Undo step. With <see cref="RenderJob.ThumbnailFor"/>, it only makes that part's thumbnail.
            /// </summary>
            public Func<RenderJob, Task> Render;
            /// <summary>A note for a placed item's card (its program missing, say), or null.</summary>
            public Func<Transform, string> Note;
        }

        public sealed class RenderJob
        {
            public Transform Item;
            /// <summary>The item's folder (<see cref="ItemFolder"/>): the renderer makes its files below it.</summary>
            public string Folder;
            public Definition Definition;
            public string PrefabPath, PartsPath;
            /// <summary>Every part naming the renderer, by key, with the variant it gets (the new choice, or the one it uses now).</summary>
            public readonly Dictionary<string, (Part part, PartVariant variant)> Parts = new Dictionary<string, (Part, PartVariant)>();
            public Action<string> Progress = _ => { };
            public CancellationToken Cancellation;
            /// <summary>Set by the renderer: changes the item, on the main thread, inside the apply's Undo step.</summary>
            public Action Assign;
            /// <summary>The files it made (Assets/ paths): tracked, and its materials carry the parts' record.</summary>
            public readonly List<string> Made = new List<string>();
            public readonly List<string> Problems = new List<string>();
            public string Note;
            /// <summary>Only a thumbnail is wanted, of this part's variant: the renderer puts a 512 × 512 PNG in <see cref="Thumbnail"/>.</summary>
            public string ThumbnailFor;
            public byte[] Thumbnail;
        }

        private static readonly Dictionary<string, PartRenderer> Renderers = new Dictionary<string, PartRenderer>();

        /// <summary>Registers one of Orbiters' renderers under the name parts files use (<c>"renderer": "orbiters-n4n0-menu"</c>).</summary>
        public static void RegisterRenderer(string name, PartRenderer renderer)
        {
            if (string.IsNullOrEmpty(name) || renderer == null) return;
            Renderers[name] = renderer;
        }

        public static IReadOnlyCollection<string> RendererNames => Renderers.Keys;

        /// <summary>What a placed item's renderers have to say on its card (a program missing), or null.</summary>
        public static string Note(Transform item, Definition definition) =>
            definition?.parts.Select(p => p.renderer).Where(r => !string.IsNullOrEmpty(r)).Distinct()
                .Select(r => Renderers.TryGetValue(r, out var renderer) ? renderer.Note?.Invoke(item) : null).FirstOrDefault(n => !string.IsNullOrEmpty(n));

        private static RenderJob Job(Transform item, Definition definition, string prefabPath, string partsPath, string name,
            IReadOnlyList<(Part part, PartVariant variant)> choices, Action<string> progress, CancellationToken cancellation)
        {
            var job = new RenderJob { Item = item, Folder = ItemFolder(item), Definition = definition, PrefabPath = prefabPath, PartsPath = partsPath, Progress = progress ?? (_ => { }), Cancellation = cancellation };
            foreach (var part in definition.parts.Where(p => p.renderer == name))
            {
                var chosen = choices.FirstOrDefault(c => c.part.key == part.key);
                var variant = chosen.variant ?? (item != null ? VariantOf(Current(item, definition, part), partsPath, part) : Bases(partsPath, part).FirstOrDefault());
                if (variant != null) job.Parts[part.key] = (part, variant);
            }
            return job;
        }

        // ---- Thumbnails ----

        private static string ThumbnailCache => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "Orbiters", "PartVariants", "Thumbnails");

        private static string ThumbnailFile(string sha256) => string.IsNullOrEmpty(sha256) ? null : Path.Combine(ThumbnailCache, sha256 + ".png");

        // The thumbnail of a variant put on an item is kept, so its card shows it later (by its own hash).
        private static void KeepThumbnail(PartVariant variant)
        {
            string file = variant.Thumbnail != null && !variant.IsBase ? ThumbnailFile(PartVariant.Hash(variant.Thumbnail)) : null;
            if (file == null || File.Exists(file)) return;
            try { Directory.CreateDirectory(ThumbnailCache); File.WriteAllBytes(file, variant.Thumbnail); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>The thumbnail kept for an applied variant (downloaded or made here), or null.</summary>
        public static byte[] ThumbnailBytes(Applied applied)
        {
            string file = applied != null && !applied.IsBase ? ThumbnailFile(applied.thumbnail) : null;
            try { return file != null && File.Exists(file) ? File.ReadAllBytes(file) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        // ---- Carried over to an update of the item ----

        [InitializeOnLoadMethod]
        private static void RegisterCarry() => AttachmentHooks.RegisterCarry(Carry);

        /// <summary>
        /// An update placed in place of an older copy keeps the variants its parts used (by part key: a base variant by its
        /// name in the new version, another from the files it was applied with).
        /// </summary>
        public static Task Carry(GameObject from, GameObject to)
        {
            var before = Of(from).Where(p => p.Current != null && p.Current.Recorded).ToList();
            if (before.Count == 0) return Task.CompletedTask;
            var tasks = new List<Task>();
            foreach (var group in Of(to).GroupBy(p => p.Item))
            {
                var choices = new List<(Part, PartVariant)>();
                foreach (var now in group)
                {
                    var was = before.FirstOrDefault(p => p.Part.key == now.Part.key && p.Item.name == now.Item.name) ?? before.FirstOrDefault(p => p.Part.key == now.Part.key);
                    if (was == null || was.Part.kind != now.Part.kind) continue;
                    var variant = was.Current.IsBase ? Bases(now.PartsPath, now.Part).FirstOrDefault(b => b.BaseName == was.Current.baseName) : VariantOf(was.Current, was.PartsPath, was.Part);
                    if (variant != null) choices.Add((now.Part, variant));
                }
                if (choices.Count == 0) continue;
                var first = group.First();
                tasks.Add(CarryAsync(first, choices));
            }
            return Task.WhenAll(tasks);
        }

        private static async Task CarryAsync(Placed placed, List<(Part, PartVariant)> choices)
        {
            var result = await ApplyAsync(placed.Item, placed.Definition, placed.PrefabPath, placed.PartsPath, choices);
            if (!result.Applied && result.Problems.Count > 0)
                Debug.LogWarning("[Orbiters] " + placed.Item.name + " was updated without its earlier variants (choose them again on its card): " + string.Join(" ", result.Problems));
        }
    }
}
