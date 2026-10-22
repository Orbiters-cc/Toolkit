using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The thumbnails on Orb's projected screen (Orbiters contracts, Docs/orb.md, Thumbnails): 64 × 36 colour pictures as a
    /// sum of cosines, kept in 72 or 128 bytes (the orb card's "Thumbnail size"), in layers that sharpen them as they
    /// arrive. Which fields, with how many bits and which weight, is the contracts' design table, of which
    /// <c>OrbThumbnail.json</c> is a copy; the screen's shader gets its decoder from it, as straight-line code (a loop
    /// over a table cost about twenty times as much).
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>
        /// Local, never changed by a menu: the bytes of each thumbnail the screen keeps (its default value is the orb card's
        /// choice). Orbiters desktop reads it to write the screen's memory, which is <see cref="ScreenBytes"/> long.
        /// </summary>
        public const string ScreenThumbnailBytes = "Orb/Screen/ThumbnailBytes";
        /// <summary>The thumbnail sizes an orb's card offers: 72 bytes (three layers) or 128 (four; 8 text slots or more).</summary>
        public static readonly IReadOnlyList<int> OfferedThumbnailBytes = new[] { 72, 128 };
        public const int DefaultThumbnailBytes = 72;
        /// <summary>Where each layer ends, in bytes from a thumbnail's first.</summary>
        internal static readonly int[] ThumbnailLayers = { 16, 40, 72, 128 };
        // The gains, then the means, then the fields.
        private const int ThumbnailFieldsAt = 5;
        private const string ThumbnailDesign = "Packages/orbiters.toolkit/Editor/VRChat/OrbThumbnail.json";

        /// <summary>The screen's memory with thumbnails of <paramref name="thumbnailBytes"/>: 744 bytes with 72, 1024 with 128.</summary>
        public static int ScreenBytes(int thumbnailBytes) => 384 + 5 * thumbnailBytes;
        public static int ScreenWords(int thumbnailBytes) => ScreenBytes(thumbnailBytes) / 2;
        /// <summary>The layers a thumbnail of <paramref name="thumbnailBytes"/> has.</summary>
        public static int ThumbnailLayersIn(int thumbnailBytes) => ThumbnailLayers.Count(end => end <= thumbnailBytes);

        internal struct ThumbnailField
        {
            public int Channel, U, V, Width, Bit, Layer;
            public float Weight;
        }

        /// <summary>The design table's fields, with their first bit from the thumbnail's first byte and their layer.</summary>
        internal static List<ThumbnailField> ThumbnailFields(out float gainLow, out float gainStep)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ThumbnailDesign);
            if (asset == null) throw new InvalidOperationException("Missing " + ThumbnailDesign + ": reinstall Orbiters Toolkit.");
            string json = asset.text;
            float Number(string key) => float.Parse(Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            if ((int)Number("version") != 1) throw new InvalidOperationException(ThumbnailDesign + " is not version 1 of the design.");
            gainLow = Number("gain_low");
            gainStep = Number("gain_step");
            var fields = new List<ThumbnailField>();
            int bit = ThumbnailFieldsAt * 8;
            var body = json.Substring(json.IndexOf("\"fields\"", StringComparison.Ordinal));
            foreach (Match m in Regex.Matches(body, @"\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([-+0-9.eE]+)\s*\]"))
            {
                int width = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                int end = bit + width, layer = 1;
                while (layer <= ThumbnailLayers.Length && end > ThumbnailLayers[layer - 1] * 8) layer++;
                fields.Add(new ThumbnailField
                {
                    Channel = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), U = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    V = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), Width = width, Bit = bit, Layer = layer,
                    Weight = float.Parse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture),
                });
                bit = end;
            }
            if (fields.Count == 0 || bit > ThumbnailLayers[ThumbnailLayers.Length - 1] * 8) throw new InvalidOperationException(ThumbnailDesign + " does not hold a design.");
            return fields;
        }

        /// <summary>
        /// The screen shader's thumbnail decoder for thumbnails of <paramref name="thumbnailBytes"/>: <c>Thumbnail(at, t,
        /// layers)</c>, the YCbCr (sRGB values) at <c>t</c> (0 to 1 across and down) of the thumbnail at byte <c>at</c>
        /// (even) holding <c>layers</c> layers, and <c>YccToRgb</c>. Each layer's fields are summed row by row
        /// (one cosine of the row and one per coefficient, from a recurrence), their offsets, widths and weights constants.
        /// </summary>
        internal static string ThumbnailDecoder(int thumbnailBytes)
        {
            var fields = ThumbnailFields(out float gainLow, out float gainStep).Where(f => f.Layer <= ThumbnailLayersIn(thumbnailBytes)).ToList();
            int uMax = Math.Max(1, fields.Max(f => f.U)), vMax = Math.Max(1, fields.Max(f => f.V));
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            const string I = "            ";
            var code = new StringBuilder();
            code.AppendLine(I + $"// Thumbnails of {thumbnailBytes} bytes: {fields.Count} fields in {ThumbnailLayersIn(thumbnailBytes)} layers (OrbThumbnail.json).");
            code.AppendLine(I + "uint ThumbnailWord(int k) { return (uint)(_Words[clamp(k, 0, SCREEN_WORDS - 1)] + .5); }");
            code.AppendLine(I + "float3 YccToRgb(float3 ycc)");
            code.AppendLine(I + "{");
            code.AppendLine(I + "    float r = ycc.x + 1.402 * ycc.z, b = ycc.x + 1.772 * ycc.y;");
            code.AppendLine(I + "    return saturate(float3(r, (ycc.x - .299 * r - .114 * b) / .587, b));");
            code.AppendLine(I + "}");
            code.AppendLine(I + "float3 Thumbnail(int at, float2 t, int layers)");
            code.AppendLine(I + "{");
            code.AppendLine(I + "    int w0 = at >> 1;");
            code.AppendLine(I + "    float3 ycc = float3((Byte(at + 2) - 1) / 254.0, ((Byte(at + 3) ^ 128) - 128) / 254.0, ((Byte(at + 4) ^ 128) - 128) / 254.0);");
            code.AppendLine(I + $"    float2 gain = exp2({F(gainLow)} + float2(Byte(at), Byte(at + 1)) / {F(gainStep)});");
            code.AppendLine(I + "    float ax = cos(UNITY_PI * t.x), ay = cos(UNITY_PI * t.y);");
            code.AppendLine(I + $"    float cx[{uMax + 1}], cy[{vMax + 1}];");
            code.AppendLine(I + "    cx[0] = 1; cx[1] = ax; cy[0] = 1; cy[1] = ay;");
            code.AppendLine(I + $"    [unroll] for (int n = 2; n <= {uMax}; n++) cx[n] = 2 * ax * cx[n - 1] - cx[n - 2];");
            code.AppendLine(I + $"    [unroll] for (int m = 2; m <= {vMax}; m++) cy[m] = 2 * ay * cy[m - 1] - cy[m - 2];");
            code.AppendLine(I + "    float3 total = 0, row = 0;");
            foreach (var layer in fields.GroupBy(f => f.Layer).OrderBy(g => g.Key))
            {
                string inner = layer.Key == 1 ? I + "    " : I + "        ";
                if (layer.Key > 1) code.AppendLine(I + $"    if (layers >= {layer.Key})").AppendLine(I + "    {");
                foreach (var row in layer.GroupBy(f => f.V).OrderBy(g => g.Key))
                {
                    code.AppendLine(inner + "row = 0;");
                    foreach (var f in row.OrderBy(f => f.U).ThenBy(f => f.Channel).ThenBy(f => f.Bit))
                    {
                        int k = f.Bit >> 4, shift = f.Bit & 15;
                        string window = shift + f.Width <= 16 ? $"ThumbnailWord(w0 + {k})" : $"(ThumbnailWord(w0 + {k}) | (ThumbnailWord(w0 + {k + 1}) << 16))";
                        code.AppendLine(inner + $"row.{"xyz"[f.Channel]} += ((int)({window} << {32 - shift - f.Width}) >> {32 - f.Width}) * ({F(f.Weight)} * cx[{f.U}]);");
                    }
                    code.AppendLine(inner + $"total += row * cy[{row.Key}];");
                }
                if (layer.Key > 1) code.AppendLine(I + "    }");
            }
            code.AppendLine(I + "    return ycc + total * float3(gain.x, gain.y, gain.y);");
            code.AppendLine(I + "}");
            return code.ToString().TrimEnd();
        }
    }
}
