using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.Toolkit.Editor.VRChat.Manifest
{
    /// <summary>
    /// What an avatar's menu does, in the words of its labels, with the parameter names the avatar is uploaded with
    /// (VRCFury's VF##_… among them): Orbiters contracts' avatar manifest (Docs/avatar-manifest.md). VRChat tells OSC apps
    /// an avatar's parameters, never its menu, so Orbiters desktop (and Orb) read this file to know that "Hoodie" is the
    /// toggle setting VF12_Clothes/Hoodie to 1. <see cref="Read"/> takes it from a built avatar's descriptor;
    /// <see cref="AvatarManifestExporter"/> writes it while the VRChat SDK builds one.
    /// </summary>
    public sealed class AvatarManifest
    {
        public const string Format = "orbiters.avatar";
        public const int Version = 1;

        public string AvatarId, Name, WrittenUtc, Generator;
        public readonly List<Parameter> Parameters = new List<Parameter>();
        public readonly List<Control> Controls = new List<Control>();
        /// <summary>The Orb prop's number of text sync values (4, 8 or 16), when the avatar carries it.</summary>
        public int? OrbTextSync;
        /// <summary>The Orb prop's projected screen: its protocol's version (<see cref="OrbInstaller.ScreenVersion"/>), 0 without one.</summary>
        public int OrbScreen;

        /// <summary>An expression parameter as uploaded. <see cref="Type"/>: "bool", "int" or "float".</summary>
        public sealed class Parameter
        {
            public string Name, Type;
            public bool Synced, Saved;
            public float Default;
        }

        /// <summary>
        /// A control of the menu as VRChat shows it. <see cref="Type"/>: "toggle", "button", "submenu", "radial",
        /// "two_axis" or "four_axis". <see cref="Parameter"/> (null when it sets none) is set to <see cref="Value"/> while
        /// the control is active; a radial's is its first sub-parameter. <see cref="SubParameters"/>: a puppet's (radial:
        /// one; two axis: horizontal, vertical; four axis: up, right, down, left); <see cref="Labels"/>: its axis labels.
        /// </summary>
        public sealed class Control
        {
            public List<string> Path = new List<string>();
            public string Type, Parameter;
            public float? Value;
            public List<string> SubParameters = new List<string>(), Labels = new List<string>();
            public AvatarTags Tags;
        }

        /// <summary>Where the manifests are: %LOCALAPPDATA%\Orbiters\Avatars on Windows (tests set another).</summary>
        public static string Folder
        {
            get => folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "Avatars");
            internal set => folder = value;
        }
        private static string folder;

        /// <summary>
        /// The file of <paramref name="avatarId"/> (contracts' Docs/avatar-manifest.md, Rust <c>avatar::file_name</c>): every
        /// character but A–Z, a–z, 0–9, _ and - becomes _ ("local:x" → "local_x.json"); when one did, - and the FNV-1a
        /// 32-bit hash of the id's UTF-8 bytes (8 lowercase hex digits) follow, so test avatars named "ネコ" and "イヌ"
        /// get files of their own.
        /// </summary>
        public static string FileName(string avatarId)
        {
            string id = avatarId ?? "";
            var name = new StringBuilder(id.Length + 14);
            bool replaced = false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-')
                {
                    name.Append(c);
                    continue;
                }
                // One _ per character, as Rust counts them: a surrogate pair is one character.
                if (char.IsHighSurrogate(c) && i + 1 < id.Length && char.IsLowSurrogate(id[i + 1])) i++;
                name.Append('_');
                replaced = true;
            }
            if (replaced)
            {
                uint hash = 0x811C9DC5;
                foreach (byte b in Encoding.UTF8.GetBytes(id)) hash = unchecked((hash ^ b) * 0x01000193);
                name.Append('-').Append(hash.ToString("x8", CultureInfo.InvariantCulture));
            }
            return name + ".json";
        }

        /// <summary>
        /// The manifest of a built <paramref name="avatar"/>: its expression parameters and every control of its menu, parents
        /// before their contents, each submenu once (at its first place: menus may share submenus or loop back), labels
        /// without their rich text tags.
        /// </summary>
        public static AvatarManifest Read(GameObject avatar, string avatarId)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var manifest = new AvatarManifest { AvatarId = avatarId, Name = avatar.name };
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || !descriptor.customExpressions) return manifest;
            foreach (var p in descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null
                         ? descriptor.expressionParameters.parameters : Array.Empty<VRCExpressionParameters.Parameter>())
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                manifest.Parameters.Add(new Parameter
                {
                    Name = p.name, Synced = p.networkSynced, Saved = p.saved, Default = p.defaultValue,
                    Type = p.valueType == VRCExpressionParameters.ValueType.Bool ? "bool" : p.valueType == VRCExpressionParameters.ValueType.Int ? "int" : "float",
                });
            }
            if (descriptor.expressionsMenu != null)
                manifest.Walk(descriptor.expressionsMenu, new List<string>(), AvatarTags.None, new HashSet<VRCExpressionsMenu> { descriptor.expressionsMenu });
            // The prop is there when its pointer is; its sync values are counted as Orbiters desktop counts them.
            if (manifest.Parameters.Any(p => p.Name == OrbInstaller.TextPointer))
            {
                var sync = new Regex("^" + Regex.Escape(OrbInstaller.TextSyncPrefix) + "[0-9]+$");
                int count = manifest.Parameters.Count(p => sync.IsMatch(p.Name));
                if (OrbInstaller.TextSyncCounts.Contains(count)) manifest.OrbTextSync = count;
                if (manifest.Parameters.Any(p => p.Name == OrbInstaller.ScreenPointer)) manifest.OrbScreen = OrbInstaller.ScreenVersion;
            }
            return manifest;
        }

        private void Walk(VRCExpressionsMenu menu, List<string> path, AvatarTags folder, HashSet<VRCExpressionsMenu> listed)
        {
            foreach (var c in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (c == null) continue;
                bool submenu = c.type == VRCExpressionsMenu.Control.ControlType.SubMenu;
                // A submenu listed earlier (or one of those this one is in): only its first place counts.
                if (submenu && c.subMenu != null && !listed.Add(c.subMenu)) continue;
                var control = new Control { Path = new List<string>(path) { Plain(c.name) }, Type = TypeOf(c.type) };
                var subs = (c.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>()).Select(s => s?.name ?? "").ToList();
                if (c.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet)
                {
                    control.SubParameters = subs.Take(1).ToList();
                    control.Parameter = Named(subs.FirstOrDefault());
                }
                else
                {
                    control.Parameter = Named(c.parameter?.name);
                    if (control.Parameter != null) control.Value = c.value;
                    if (c.type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet) control.SubParameters = Pad(subs, 2);
                    if (c.type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet) control.SubParameters = Pad(subs, 4);
                }
                if (c.type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet || c.type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet)
                    control.Labels = Pad((c.labels ?? Array.Empty<VRCExpressionsMenu.Control.Label>()).Select(l => Plain(l.name)).ToList(), 4);
                var names = new[] { control.Parameter }.Concat(control.SubParameters).Where(n => !string.IsNullOrEmpty(n)).ToList();
                control.Tags = folder | AvatarManifestTags.Of(c.name, names);
                // Orb's own controls, and a folder of nothing else.
                if (names.Any(IsOrb) || submenu && c.subMenu != null && c.subMenu.controls != null && c.subMenu.controls.Count > 0 &&
                    c.subMenu.controls.All(s => s != null && IsOrb(s.parameter?.name)))
                    control.Tags |= AvatarTags.Orb;
                Controls.Add(control);
                // What a folder is about, what it holds is about too.
                if (submenu && c.subMenu != null) Walk(c.subMenu, control.Path, control.Tags, listed);
            }
        }

        private static bool IsOrb(string parameter) => parameter != null && parameter.StartsWith("Orb/", StringComparison.Ordinal);

        private static string Named(string name) => string.IsNullOrEmpty(name) ? null : name;

        // Puppets keep their slots in order: an unset one is an empty name.
        private static List<string> Pad(List<string> values, int count) => values.Concat(Enumerable.Repeat("", count)).Take(count).ToList();

        private static string TypeOf(VRCExpressionsMenu.Control.ControlType type)
        {
            switch (type)
            {
                case VRCExpressionsMenu.Control.ControlType.Button: return "button";
                case VRCExpressionsMenu.Control.ControlType.SubMenu: return "submenu";
                case VRCExpressionsMenu.Control.ControlType.RadialPuppet: return "radial";
                case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet: return "two_axis";
                case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet: return "four_axis";
                default: return "toggle";
            }
        }

        // VRChat's menu draws labels with TextMeshPro: its tags go, line breaks become spaces.
        private static readonly Regex RichText = new Regex(@"</?(?:b|i|u|s|sup|sub|color|colour|size|alpha|mark|font|material|quad|sprite|style|voffset|cspace|mspace|space|indent|line-height|line-indent|margin|width|rotate|pos|align|allcaps|lowercase|uppercase|smallcaps|nobr|noparse|link|gradient|font-weight|br)\b[^<>]*>|<#[0-9a-fA-F]{3,8}>",
            RegexOptions.IgnoreCase);

        /// <summary>A label as people read it: no rich text tags, one line.</summary>
        public static string Plain(string label) => Regex.Replace(RichText.Replace(label ?? "", ""), @"\s+", " ").Trim();

        /// <summary>The manifest as Docs/avatar-manifest.md shows it (empty lists and unknowns left out).</summary>
        public string ToJson()
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var json = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2 })
            {
                json.WriteStartObject();
                Write(json, "format", Format);
                json.WritePropertyName("version"); json.WriteValue(Version);
                Write(json, "avatar_id", AvatarId ?? "");
                if (!string.IsNullOrEmpty(Name)) Write(json, "name", Name);
                if (!string.IsNullOrEmpty(WrittenUtc)) Write(json, "written_utc", WrittenUtc);
                if (!string.IsNullOrEmpty(Generator)) Write(json, "generator", Generator);
                json.WritePropertyName("parameters");
                json.WriteStartArray();
                foreach (var p in Parameters)
                {
                    json.WriteStartObject();
                    Write(json, "name", p.Name);
                    Write(json, "type", p.Type);
                    json.WritePropertyName("synced"); json.WriteValue(p.Synced);
                    json.WritePropertyName("saved"); json.WriteValue(p.Saved);
                    Number(json, "default", p.Default);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WritePropertyName("controls");
                json.WriteStartArray();
                foreach (var c in Controls)
                {
                    json.WriteStartObject();
                    Strings(json, "path", c.Path);
                    Write(json, "type", c.Type);
                    if (c.Parameter != null) Write(json, "parameter", c.Parameter);
                    if (c.Value.HasValue) Number(json, "value", c.Value.Value);
                    if (c.SubParameters.Count > 0) Strings(json, "sub_parameters", c.SubParameters);
                    if (c.Labels.Count > 0) Strings(json, "labels", c.Labels);
                    Strings(json, "tags", AvatarManifestTags.Names(c.Tags));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                if (OrbTextSync.HasValue)
                {
                    json.WritePropertyName("orb");
                    json.WriteStartObject();
                    json.WritePropertyName("text_sync"); json.WriteValue(OrbTextSync.Value);
                    if (OrbScreen > 0) { json.WritePropertyName("screen"); json.WriteValue(OrbScreen); }
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            return text.ToString();
        }

        private static void Write(JsonWriter json, string name, string value) { json.WritePropertyName(name); json.WriteValue(value); }

        private static void Strings(JsonWriter json, string name, IEnumerable<string> values)
        {
            json.WritePropertyName(name);
            json.WriteStartArray();
            foreach (var value in values) json.WriteValue(value);
            json.WriteEndArray();
        }

        // Whole numbers without a decimal point (bools are 0 or 1).
        private static void Number(JsonWriter json, string name, float value)
        {
            json.WritePropertyName(name);
            if (Mathf.Approximately(value, Mathf.Round(value)) && Math.Abs(value) < 1e9f) json.WriteValue((long)Mathf.Round(value));
            else json.WriteValue(value);
        }
    }
}
