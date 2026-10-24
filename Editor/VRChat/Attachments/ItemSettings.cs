using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Settings a creator gives their asset, like N4N0's on its card: a file named like the asset's prefab with
    /// ".settings.json" beside it (<c>Jacket.prefab</c>, <c>Jacket.settings.json</c>). Each setting is a switch or a choice
    /// of a few options; an option shows objects of the item (and hides those the setting's other options show), hides
    /// others, puts materials in a renderer's slots and sets blendshapes, in place and with Undo. What holds is read back
    /// from the item itself, and an update from the gallery keeps it (matched by label). Paths of objects are below the
    /// item's root (as the prefab has them); assets are paths beside the file, <c>Assets/…</c> paths or <c>guid:…</c>.
    /// Every tool showing a placed item's card offers them (<see cref="AttachmentHooks.Options"/>); Orbiters' documentation
    /// explains the file ("Item settings").
    /// </summary>
    public static class ItemSettings
    {
        public const string Extension = ".settings.json";
        public const int Schema = 1;
        internal const string Toggle = "toggle", Choice = "choice";

        [Serializable]
        public sealed class Definition
        {
            public int schema = Schema;
            public List<Setting> settings = new List<Setting>();
        }

        [Serializable]
        public sealed class Setting
        {
            public string label, tooltip;
            /// <summary>"toggle" (a switch: <see cref="on"/>, <see cref="off"/>) or "choice" (<see cref="choices"/>).</summary>
            public string type;
            public Option on, off;
            public List<Option> choices = new List<Option>();

            public bool IsToggle => type == Toggle || (string.IsNullOrEmpty(type) && (choices == null || choices.Count == 0));

            /// <summary>The options in order: off and on for a switch.</summary>
            public List<Option> Options() => IsToggle
                ? new List<Option> { Named(off, "Off"), Named(on, "On") }
                : (choices ?? new List<Option>()).Where(c => c != null).ToList();

            private static Option Named(Option option, string label)
            {
                option ??= new Option();
                if (string.IsNullOrEmpty(option.label)) option.label = label;
                return option;
            }
        }

        [Serializable]
        public sealed class Option
        {
            public string label, tooltip;
            /// <summary>A small picture of the option, shown on the card (a path beside the file).</summary>
            public string picture;
            public List<string> show = new List<string>(), hide = new List<string>();
            public List<MaterialSlot> materials = new List<MaterialSlot>();
            public List<BlendShapeWeight> blendshapes = new List<BlendShapeWeight>();
        }

        [Serializable]
        public sealed class MaterialSlot
        {
            public string renderer;
            public int slot;
            public string material;
        }

        [Serializable]
        public sealed class BlendShapeWeight
        {
            public string renderer, name;
            public float weight;
        }

        private static readonly Dictionary<string, (DateTime written, Definition definition)> Cache = new Dictionary<string, (DateTime, Definition)>();

        [InitializeOnLoadMethod]
        private static void Register() => AttachmentHooks.RegisterOptions(Options);

        /// <summary>Where a prefab's settings file is: beside it, named like it.</summary>
        public static string PathFor(string prefabPath) =>
            string.IsNullOrEmpty(prefabPath) ? null : prefabPath.Substring(0, prefabPath.Length - Path.GetExtension(prefabPath).Length) + Extension;

        /// <summary>The prefab a placed item came from (its gallery or dropped copy), or null.</summary>
        public static string PrefabOf(OrbitersAttachment attachment)
        {
            if (attachment == null) return null;
            if (!string.IsNullOrEmpty(attachment.variant) && attachment.variant.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && File.Exists(attachment.variant))
                return attachment.variant;
            string path = string.IsNullOrEmpty(attachment.source) ? null : AssetDatabase.GUIDToAssetPath(attachment.source);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        /// <summary>
        /// Reads a settings file; with <paramref name="prefab"/>, also checks it against the prefab and its assets, each
        /// problem said in <paramref name="problems"/>. Null when the file can't be read.
        /// </summary>
        public static Definition Read(string settingsPath, GameObject prefab = null, List<string> problems = null)
        {
            Definition definition;
            try { definition = JsonUtility.FromJson<Definition>(File.ReadAllText(settingsPath)); }
            catch (Exception e)
            {
                problems?.Add(Path.GetFileName(settingsPath) + " is not valid JSON: " + e.Message);
                return null;
            }

            if (definition == null || definition.settings == null)
            {
                problems?.Add(Path.GetFileName(settingsPath) + " has no settings list.");
                return null;
            }

            if (problems != null) Check(settingsPath, definition, prefab, problems);
            return definition;
        }

        /// <summary>The assets the settings use (materials, pictures), which a package of the prefab needs too.</summary>
        public static List<string> AssetsOf(string settingsPath, Definition definition)
        {
            var paths = new List<string>();
            foreach (var option in definition.settings.Where(s => s != null).SelectMany(s => s.Options()))
            {
                paths.Add(AssetPath(settingsPath, option.picture));
                paths.AddRange(option.materials.Where(m => m != null).Select(m => AssetPath(settingsPath, m.material)));
            }

            return paths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Distinct().ToList();
        }

        /// <summary>A first settings file for a prefab: a switch showing or hiding its first child.</summary>
        public static string Template(GameObject prefab)
        {
            var child = prefab != null && prefab.transform.childCount > 0 ? prefab.transform.GetChild(0).name : "Child object";
            return "{\n" +
                   "    \"schema\": 1,\n" +
                   "    \"settings\": [\n" +
                   "        {\n" +
                   "            \"label\": \"" + Escape(child) + "\",\n" +
                   "            \"type\": \"toggle\",\n" +
                   "            \"on\": { \"show\": [\"" + Escape(child) + "\"] }\n" +
                   "        }\n" +
                   "    ]\n" +
                   "}\n";
        }

        // ---- On a placed item ----

        private static IEnumerable<AttachmentOption> Options(GameObject placed)
        {
            if (placed == null) yield break;
            foreach (var attachment in placed.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                string settingsPath = PathFor(PrefabOf(attachment));
                var definition = settingsPath != null ? Cached(settingsPath) : null;
                if (definition == null) continue;
                foreach (var setting in definition.settings)
                {
                    var option = setting != null && !string.IsNullOrEmpty(setting.label) ? Build(attachment.transform, settingsPath, setting) : null;
                    if (option != null) yield return option;
                }
            }
        }

        private static Definition Cached(string settingsPath)
        {
            if (!File.Exists(settingsPath)) return null;
            var written = File.GetLastWriteTimeUtc(settingsPath);
            if (Cache.TryGetValue(settingsPath, out var known) && known.written == written) return known.definition;
            var definition = Read(settingsPath);
            Cache[settingsPath] = (written, definition);
            return definition;
        }

        private static AttachmentOption Build(Transform root, string settingsPath, Setting setting)
        {
            var options = setting.Options();
            if (options.Count < 2) return null;
            // A setting's options take turns showing their objects: what one shows, the others hide.
            var shownByAny = options.SelectMany(o => o.show ?? new List<string>()).Distinct().ToList();
            var pictures = options.Select(o => AssetDatabase.LoadAssetAtPath<Texture>(AssetPath(settingsPath, o.picture))).ToList();
            var built = new AttachmentOption
            {
                Label = setting.label,
                Tooltip = setting.tooltip,
                IsToggle = setting.IsToggle,
                Choices = options.Select(o => o.label ?? string.Empty).ToList(),
                ChoiceTooltips = options.Select(o => o.tooltip).ToList(),
                ChoicePictures = pictures.Any(p => p != null) ? pictures : null,
                Current = options.FindIndex(o => Holds(root, settingsPath, o, shownByAny)),
            };
            built.Choose = index =>
            {
                if (index < 0 || index >= options.Count) return;
                Apply(root, settingsPath, options[index], shownByAny, setting.label);
            };
            return built;
        }

        private static bool Holds(Transform root, string settingsPath, Option option, List<string> shownByAny)
        {
            var show = option.show ?? new List<string>();
            foreach (var path in show)
                if (Find(root, path) is Transform t && !t.gameObject.activeSelf) return false;
            foreach (var path in shownByAny.Except(show).Concat(option.hide ?? new List<string>()))
                if (Find(root, path) is Transform t && t.gameObject.activeSelf) return false;
            foreach (var swap in option.materials ?? new List<MaterialSlot>())
            {
                var renderer = Find(root, swap.renderer)?.GetComponent<Renderer>();
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetPath(settingsPath, swap.material));
                if (renderer == null || material == null) continue;
                var slots = renderer.sharedMaterials;
                if (swap.slot < 0 || swap.slot >= slots.Length || slots[swap.slot] != material) return false;
            }

            foreach (var shape in option.blendshapes ?? new List<BlendShapeWeight>())
            {
                var renderer = Find(root, shape.renderer)?.GetComponent<SkinnedMeshRenderer>();
                int index = renderer != null && renderer.sharedMesh != null ? renderer.sharedMesh.GetBlendShapeIndex(shape.name ?? string.Empty) : -1;
                if (index >= 0 && Mathf.Abs(renderer.GetBlendShapeWeight(index) - shape.weight) > 0.5f) return false;
            }

            return true;
        }

        private static void Apply(Transform root, string settingsPath, Option option, List<string> shownByAny, string label)
        {
            var show = option.show ?? new List<string>();
            var changes = new List<(GameObject target, bool active)>();
            foreach (var path in show)
                if (Find(root, path) is Transform t) changes.Add((t.gameObject, true));
            foreach (var path in shownByAny.Except(show).Concat(option.hide ?? new List<string>()))
                if (Find(root, path) is Transform t && !show.Contains(path)) changes.Add((t.gameObject, false));
            var swaps = (option.materials ?? new List<MaterialSlot>())
                .Select(s => (renderer: Find(root, s.renderer)?.GetComponent<Renderer>(), s.slot, material: AssetDatabase.LoadAssetAtPath<Material>(AssetPath(settingsPath, s.material))))
                .Where(s => s.renderer != null && s.material != null).ToList();
            var shapes = (option.blendshapes ?? new List<BlendShapeWeight>())
                .Select(b => (renderer: Find(root, b.renderer)?.GetComponent<SkinnedMeshRenderer>(), b.name, b.weight))
                .Where(b => b.renderer != null && b.renderer.sharedMesh != null && b.renderer.sharedMesh.GetBlendShapeIndex(b.name ?? string.Empty) >= 0).ToList();

            var recorded = changes.Select(c => (UnityEngine.Object)c.target).Concat(swaps.Select(s => (UnityEngine.Object)s.renderer))
                .Concat(shapes.Select(s => (UnityEngine.Object)s.renderer)).Distinct().ToArray();
            if (recorded.Length == 0) return;
            Undo.RecordObjects(recorded, "Change " + label);
            foreach (var (target, active) in changes) target.SetActive(active);
            foreach (var (renderer, slot, material) in swaps)
            {
                var slots = renderer.sharedMaterials;
                if (slot < 0 || slot >= slots.Length) continue;
                slots[slot] = material;
                renderer.sharedMaterials = slots;
            }

            foreach (var (renderer, name, weight) in shapes) renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(name), weight);
            foreach (var target in recorded) EditorUtility.SetDirty(target);
        }

        // ---- Paths ----

        // An object below the item's root: "" or "." is the root itself.
        private static Transform Find(Transform root, string path)
        {
            if (root == null || path == null) return null;
            string clean = path.Trim().Trim('/');
            return clean.Length == 0 || clean == "." ? root : root.Find(clean);
        }

        // An asset the file names: beside it, under Assets/ or Packages/, or by GUID.
        internal static string AssetPath(string settingsPath, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            reference = reference.Trim().Replace('\\', '/');
            if (reference.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) return AssetDatabase.GUIDToAssetPath(reference.Substring(5));
            if (reference.StartsWith("Assets/", StringComparison.Ordinal) || reference.StartsWith("Packages/", StringComparison.Ordinal)) return reference;
            var parts = new List<string>((Path.GetDirectoryName(settingsPath) ?? string.Empty).Replace('\\', '/').Split('/'));
            foreach (var part in reference.Split('/'))
            {
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (part.Length > 0 && part != ".") parts.Add(part);
            }

            return string.Join("/", parts);
        }

        private static string Escape(string text) => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ---- Checks ----

        private static void Check(string settingsPath, Definition definition, GameObject prefab, List<string> problems)
        {
            string file = Path.GetFileName(settingsPath);
            if (definition.schema != Schema) problems.Add(file + ": schema " + definition.schema + " is not known (this Toolkit reads schema " + Schema + ").");
            if (definition.settings.Count == 0) problems.Add(file + " lists no settings.");
            var labels = new HashSet<string>();
            foreach (var setting in definition.settings)
            {
                if (setting == null) continue;
                string name = string.IsNullOrWhiteSpace(setting.label) ? "A setting" : setting.label;
                if (string.IsNullOrWhiteSpace(setting.label)) problems.Add(file + ": a setting has no label.");
                else if (!labels.Add(setting.label)) problems.Add(name + ": two settings have this label (an update finds a setting by its label).");
                if (!string.IsNullOrEmpty(setting.type) && setting.type != Toggle && setting.type != Choice)
                    problems.Add(name + ": type is \"" + setting.type + "\" (use \"toggle\" or \"choice\").");
                if (setting.IsToggle && Empty(setting.on) && Empty(setting.off))
                    problems.Add(name + ": the switch changes nothing (give \"on\" a show, hide, materials or blendshapes list).");
                var options = setting.Options();
                if (!setting.IsToggle && options.Count < 2) problems.Add(name + ": a choice needs at least two options in \"choices\".");
                var optionLabels = new HashSet<string>();
                foreach (var option in options)
                {
                    string where = name + " › " + (string.IsNullOrWhiteSpace(option.label) ? "an option" : option.label);
                    if (!setting.IsToggle && string.IsNullOrWhiteSpace(option.label)) problems.Add(where + ": no label.");
                    else if (!optionLabels.Add(option.label ?? string.Empty)) problems.Add(where + ": two options have this label.");
                    CheckOption(settingsPath, prefab, option, where, problems);
                }
            }
        }

        private static bool Empty(Option option) => option == null ||
            (option.show?.Count ?? 0) + (option.hide?.Count ?? 0) + (option.materials?.Count ?? 0) + (option.blendshapes?.Count ?? 0) == 0;

        private static void CheckOption(string settingsPath, GameObject prefab, Option option, string where, List<string> problems)
        {
            var root = prefab != null ? prefab.transform : null;
            foreach (var path in (option.show ?? new List<string>()).Concat(option.hide ?? new List<string>()))
                if (root != null && Find(root, path) == null) problems.Add(where + ": no object \"" + path + "\" below " + prefab.name + ".");
            if (!string.IsNullOrWhiteSpace(option.picture) && AssetDatabase.LoadAssetAtPath<Texture>(AssetPath(settingsPath, option.picture)) == null)
                problems.Add(where + ": no picture at " + option.picture + ".");
            foreach (var swap in option.materials ?? new List<MaterialSlot>())
            {
                if (swap == null) continue;
                var renderer = root != null ? Find(root, swap.renderer)?.GetComponent<Renderer>() : null;
                if (root != null && renderer == null) problems.Add(where + ": no renderer \"" + swap.renderer + "\" below " + prefab.name + ".");
                else if (renderer != null && (swap.slot < 0 || swap.slot >= renderer.sharedMaterials.Length))
                    problems.Add(where + ": " + swap.renderer + " has no material slot " + swap.slot + " (it has " + renderer.sharedMaterials.Length + ").");
                if (AssetDatabase.LoadAssetAtPath<Material>(AssetPath(settingsPath, swap.material)) == null) problems.Add(where + ": no material at " + swap.material + ".");
            }

            foreach (var shape in option.blendshapes ?? new List<BlendShapeWeight>())
            {
                if (shape == null || root == null) continue;
                var renderer = Find(root, shape.renderer)?.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) problems.Add(where + ": no skinned mesh \"" + shape.renderer + "\" below " + prefab.name + ".");
                else if (renderer.sharedMesh == null || renderer.sharedMesh.GetBlendShapeIndex(shape.name ?? string.Empty) < 0)
                    problems.Add(where + ": " + shape.renderer + " has no blendshape \"" + shape.name + "\".");
                if (shape.weight < 0f || shape.weight > 100f) problems.Add(where + ": blendshape weights go from 0 to 100.");
            }
        }
    }
}
