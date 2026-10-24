using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// N4N0's screen thumbnails (Orbiters contracts, Docs/orb.md, Thumbnails): the decoder the installer generates for the
// screen's shader, drawn on the GPU, gives what the contracts' Rust reference decodes from the same bytes
// (OrbThumbnailVectors.json is the contracts' TestVectors~/orb_thumbnail.json; OrbThumbnail.json its Docs/orb-thumbnail.json).
public class OrbThumbnailTests
{
    private const string Vectors = "Packages/orbiters.toolkit/Tests/Editor/OrbThumbnailVectors.json";

    private struct Sample
    {
        public int Layers;
        public float X, Y;
        public float[] Rgb;
    }

    private static List<(int size, byte[] bytes, List<Sample> samples)> ReadVectors()
    {
        string text = AssetDatabase.LoadAssetAtPath<TextAsset>(Vectors).text;
        float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        var thumbnails = new List<(int, byte[], List<Sample>)>();
        foreach (var chunk in Regex.Split(text, "\"bytes\"\\s*:").Skip(1))
        {
            var bytes = Regex.Matches(chunk.Substring(0, chunk.IndexOf(']')), @"\d+").Cast<Match>().Select(m => byte.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            int size = int.Parse(Regex.Match(chunk, "\"size\"\\s*:\\s*(\\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var samples = new List<Sample>();
            foreach (Match m in Regex.Matches(chunk, "\"layers\"\\s*:\\s*(\\d+)\\s*,\\s*\"rgb\"\\s*:\\s*(null|\\[[^\\]]*\\])\\s*,\\s*\"x\"\\s*:\\s*([-0-9.eE+]+)\\s*,\\s*\"y\"\\s*:\\s*([-0-9.eE+]+)"))
            {
                var rgb = m.Groups[2].Value == "null" ? null : Regex.Matches(m.Groups[2].Value, @"[-0-9.eE+]+").Cast<Match>().Select(v => F(v.Value)).ToArray();
                samples.Add(new Sample { Layers = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Rgb = rgb, X = F(m.Groups[3].Value), Y = F(m.Groups[4].Value) });
            }
            thumbnails.Add((size, bytes, samples));
        }
        return thumbnails;
    }

    // The generated decoder in a shader of its own: the thumbnail at byte 0 of the memory, at _Point (x, y, layers).
    private static Shader Probe(int thumbnailBytes) => ShaderUtil.CreateShaderAsset(@"Shader ""Hidden/Orbiters/N4N0 thumbnail probe""
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include ""UnityCG.cginc""
            #define SCREEN_WORDS " + OrbInstaller.ScreenWords(thumbnailBytes) + @"
            float _Words[SCREEN_WORDS];
            float4 _Point;
            int Byte(int i)
            {
                float w = _Words[clamp(i >> 1, 0, SCREEN_WORDS - 1)];
                float high = floor((w + .5) / 256);
                return (int)((i & 1) ? high : round(w - high * 256));
            }
" + OrbInstaller.ThumbnailDecoder(thumbnailBytes) + @"
            float4 frag(v2f_img i) : SV_Target { return float4(YccToRgb(Thumbnail(0, _Point.xy, (int)_Point.z)), 1); }
            ENDCG
        }
    }
}", false);

    [Test]
    public void TheShadersDecoderDrawsWhatTheContractsDecode()
    {
        var vectors = ReadVectors();
        CollectionAssert.AreEquivalent(new[] { 72, 72, 128, 128 }, vectors.Select(v => v.size), "Two pictures of each size");
        var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var read = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        var shaders = new List<Object>();
        try
        {
            foreach (var (size, bytes, samples) in vectors)
            {
                Assert.AreEqual(size, bytes.Length);
                Assert.IsFalse(bytes.Contains((byte)128), "No byte is 128");
                var shader = Probe(size);
                shaders.Add(shader);
                Assert.IsTrue(shader.isSupported, string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                shaders.Add(material);
                var words = new float[OrbInstaller.ScreenWords(size)];
                for (int k = 0; 2 * k < bytes.Length; k++) words[k] = bytes[2 * k] + 256f * (2 * k + 1 < bytes.Length ? bytes[2 * k + 1] : 0);
                material.SetFloatArray("_Words", words);
                Assert.AreEqual(OrbInstaller.ThumbnailLayersIn(size), samples.Max(s => s.Layers));
                foreach (var s in samples.Where(s => s.Rgb != null))
                {
                    material.SetVector("_Point", new Vector4(s.X, s.Y, s.Layers, 0));
                    Graphics.Blit(null, target, material);
                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    read.Apply();
                    RenderTexture.active = previous;
                    var got = read.GetPixel(0, 0);
                    for (int c = 0; c < 3; c++)
                        Assert.AreEqual(s.Rgb[c], got[c], 2e-3f, $"{size} B, {s.Layers} layers at ({s.X}, {s.Y}), channel {c}");
                }
            }
        }
        finally
        {
            foreach (var o in shaders) Object.DestroyImmediate(o);
            Object.DestroyImmediate(read);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }

    [Test]
    public void TheDesignFillsEachSizesLayers()
    {
        var fields = OrbInstaller.ThumbnailFields(out _, out _);
        int Bits(int layers) => fields.Where(f => f.Layer <= layers).Sum(f => f.Width);
        Assert.AreEqual((72 - 5) * 8, Bits(3), "72 bytes: three layers, every bit a field's");
        Assert.AreEqual((128 - 5) * 8, Bits(4), "128 bytes: four layers");
        Assert.AreEqual(new[] { 3, 4 }, OrbInstaller.OfferedThumbnailBytes.Select(OrbInstaller.ThumbnailLayersIn).ToArray());
        Assert.AreEqual(new[] { 744, 1024 }, OrbInstaller.OfferedThumbnailBytes.Select(OrbInstaller.ScreenBytes).ToArray());
        Assert.IsFalse(OrbInstaller.ScreenFits(4, 128), "1024 bytes in blocks of 4 would pass pointer 253");
        Assert.IsTrue(OrbInstaller.ScreenFits(4, 72) && OrbInstaller.ScreenFits(8, 128));
    }
}
