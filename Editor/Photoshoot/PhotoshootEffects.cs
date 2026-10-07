using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>A look the photoshoot can add over its shots, alone or with any other.</summary>
    public enum PhotoshootEffect
    {
        Bloom,
        Halftone,
        AmbientOcclusion,
        DepthOfField,
        ChromaticAberration,
        Grain,
        LensDistortion,
        Vignette
    }

    /// <summary>
    /// Which effects are on and how strong each one is (1 is the usual strength; some go further, the vignette below
    /// zero), and the comic's tones and dots.
    /// </summary>
    public sealed class PhotoshootEffectSettings
    {
        public const float DefaultAmount = 0.5f;
        public const int MinComicColors = 2, MaxComicColors = 12, DefaultComicColors = 4;
        public const float DefaultComicDotSize = 0.3f;
        public static readonly PhotoshootEffect[] All = (PhotoshootEffect[])Enum.GetValues(typeof(PhotoshootEffect));
        private readonly bool[] on = new bool[All.Length];
        private readonly float[] amounts = Enumerable.Repeat(DefaultAmount, All.Length).ToArray();
        private int comicColors = DefaultComicColors;
        private float comicDotSize = DefaultComicDotSize;

        public bool IsOn(PhotoshootEffect effect) => on[(int)effect];
        public float Amount(PhotoshootEffect effect) => amounts[(int)effect];
        public void SetOn(PhotoshootEffect effect, bool value) => on[(int)effect] = value;
        public void SetAmount(PhotoshootEffect effect, float value) => amounts[(int)effect] = Mathf.Clamp(value, Min(effect), Max(effect));

        /// <summary>The weakest an effect goes: the vignette brightens the edges below zero.</summary>
        public static float Min(PhotoshootEffect effect) => effect == PhotoshootEffect.Vignette ? -1f : 0f;

        public static float Max(PhotoshootEffect effect) =>
            effect == PhotoshootEffect.Bloom || effect == PhotoshootEffect.AmbientOcclusion || effect == PhotoshootEffect.DepthOfField ||
            effect == PhotoshootEffect.ChromaticAberration ? 3f : 1f;

        /// <summary>How many tones the comic's flat colours have.</summary>
        public int ComicColors { get => comicColors; set => comicColors = Mathf.Clamp(value, MinComicColors, MaxComicColors); }

        /// <summary>The comic's print screen of ink dots, sized by <see cref="ComicDotSize"/> (0 to 1).</summary>
        public bool ComicDots { get; set; }
        public float ComicDotSize { get => comicDotSize; set => comicDotSize = Mathf.Clamp01(value); }

        /// <summary>The distance between dots, in pixels of an image <paramref name="height"/> high.</summary>
        public float ComicDotCell(float height) => height * Mathf.Lerp(1f / 260f, 1f / 36f, comicDotSize);

        /// <summary>How strong the effect is in the shot: 0 while it is off. The comic is all or nothing.</summary>
        public float Strength(PhotoshootEffect effect) => !on[(int)effect] ? 0f : effect == PhotoshootEffect.Halftone ? 1f : amounts[(int)effect];

        public int Count => on.Count(value => value);

        /// <summary>Occlusion, depth of field and the comic's outlines read the scene's depth.</summary>
        internal bool NeedsDepth => Strength(PhotoshootEffect.AmbientOcclusion) > 0f || Strength(PhotoshootEffect.DepthOfField) > 0f ||
                                    Strength(PhotoshootEffect.Halftone) > 0f;

        public void Reset()
        {
            for (int i = 0; i < on.Length; i++)
            {
                on[i] = false;
                amounts[i] = DefaultAmount;
            }
            comicColors = DefaultComicColors;
            ComicDots = false;
            comicDotSize = DefaultComicDotSize;
        }

        public static string Label(PhotoshootEffect effect)
        {
            switch (effect)
            {
                case PhotoshootEffect.Halftone: return "Comic halftone";
                case PhotoshootEffect.AmbientOcclusion: return "Ambient occlusion";
                case PhotoshootEffect.DepthOfField: return "Depth of field";
                case PhotoshootEffect.ChromaticAberration: return "Chromatic aberration";
                case PhotoshootEffect.LensDistortion: return "Lens distortion";
                default: return effect.ToString();
            }
        }

        public static string Description(PhotoshootEffect effect)
        {
            switch (effect)
            {
                case PhotoshootEffect.Bloom: return "Bright parts glow.";
                case PhotoshootEffect.Halftone: return "Flat colours in a few tones and ink outlines, like a comic page. Dots add a print screen.";
                case PhotoshootEffect.AmbientOcclusion: return "Soft shadows where surfaces meet: folds, creases, under the arms.";
                case PhotoshootEffect.DepthOfField: return "Sharp at the avatar's view point (its VRChat avatar descriptor), blurred before and behind it.";
                case PhotoshootEffect.ChromaticAberration: return "Colour fringes toward the edges, like a cheap lens.";
                case PhotoshootEffect.Grain: return "Film grain.";
                case PhotoshootEffect.LensDistortion: return "Bulges the picture like a wide lens.";
                default: return "Darkens the edges of the picture; below zero, brightens them.";
            }
        }
    }

    /// <summary>
    /// The photoshoot's effects over a rendered shot, with its own shaders (built-in render pipeline). Occlusion, depth of
    /// field and comic outlines read a depth pass: the stage drawn again with <c>PhotoshootDepthNormals</c>.
    /// </summary>
    internal static class PhotoshootEffects
    {
        private const string ShaderName = "Hidden/Orbiters/PhotoshootEffects";
        private const string ShaderPath = "Packages/orbiters.toolkit/Editor/Photoshoot/PhotoshootEffects.shader";
        private const string DepthShaderName = "Hidden/Orbiters/PhotoshootDepthNormals";
        private const string DepthShaderPath = "Packages/orbiters.toolkit/Editor/Photoshoot/PhotoshootDepthNormals.shader";
        private const int OcclusionPass = 0, ApplyOcclusionPass = 1, DepthOfFieldPass = 2, PrefilterPass = 3, DownPass = 4, UpPass = 5, FinalPass = 6;
        // Avatar-scale distances, in metres.
        private const float OcclusionRadius = 0.11f, OcclusionBias = 0.012f;
        private static Material material;
        private static Shader depthShader;

        /// <summary>The stage again, as view normals and distances, for the effects that need depth. Null when none does.</summary>
        internal static RenderTexture RenderDepth(Camera camera, PhotoshootEffectSettings settings, ref RenderTexture target, int width, int height)
        {
            if (settings == null || !settings.NeedsDepth || !DepthShader())
            {
                return null;
            }

            if (target == null || target.width != width || target.height != height || !target.IsCreated())
            {
                Release(ref target);
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    name = "Orbiters Photoshoot Depth", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point,
                    useMipMap = false, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp
                };
                target.Create();
            }

            var previousTarget = camera.targetTexture;
            var previousColor = camera.backgroundColor;
            var previousClear = camera.clearFlags;
            bool previousHdr = camera.allowHDR;
            try
            {
                // Nothing drawn reads as 0: far away for the effects.
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.allowHDR = false;
                camera.RenderWithShader(depthShader, string.Empty);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.backgroundColor = previousColor;
                camera.clearFlags = previousClear;
                camera.allowHDR = previousHdr;
            }

            return target;
        }

        /// <param name="source">The shot as rendered, resolved (not multisampled).</param>
        /// <param name="focus">How far in front of the camera the depth of field is sharp, in metres.</param>
        /// <param name="frame">Effects of the whole picture (lens distortion, chromatic aberration, vignette): off on a ref sheet's views.</param>
        internal static void Apply(Texture source, RenderTexture destination, RenderTexture depth, Camera camera, float focus,
            PhotoshootEffectSettings settings, bool frame)
        {
            var effects = Material();
            if (effects == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            int width = destination.width, height = destination.height;
            var temporaries = new List<RenderTexture>();
            RenderTexture Temporary(int w, int h, RenderTextureFormat format = RenderTextureFormat.ARGB32)
            {
                var texture = RenderTexture.GetTemporary(Mathf.Max(1, w), Mathf.Max(1, h), 0, format,
                    format == RenderTextureFormat.ARGB32 ? RenderTextureReadWrite.Default : RenderTextureReadWrite.Linear);
                texture.filterMode = FilterMode.Bilinear;
                temporaries.Add(texture);
                return texture;
            }

            try
            {
                float tangent = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
                effects.SetVector("_Frustum", new Vector4(tangent * camera.aspect, tangent, 0f, 0f));
                effects.SetVector("_Screen", new Vector4(width, height, 1f / width, 1f / height));
                effects.SetTexture("_DepthNormals", depth != null ? (Texture)depth : Texture2D.blackTexture);
                effects.SetFloat("_HasDepth", depth != null ? 1f : 0f);
                Texture current = source;

                float occlusion = settings.Strength(PhotoshootEffect.AmbientOcclusion);
                if (occlusion > 0f && depth != null)
                {
                    var occluded = Temporary(width / 2, height / 2, RenderTextureFormat.R8);
                    // Past the usual strength the shadows also reach further.
                    effects.SetVector("_Ao", new Vector4(occlusion, OcclusionRadius * (1f + 0.35f * Mathf.Max(0f, occlusion - 1f)), OcclusionBias, 0f));
                    Graphics.Blit(current, occluded, effects, OcclusionPass);
                    effects.SetTexture("_AoTex", occluded);
                    effects.SetVector("_AoTex_TexelSize", new Vector4(1f / occluded.width, 1f / occluded.height, occluded.width, occluded.height));
                    var next = Temporary(width, height);
                    Graphics.Blit(current, next, effects, ApplyOcclusionPass);
                    current = next;
                }

                float blur = settings.Strength(PhotoshootEffect.DepthOfField);
                if (blur > 0f && depth != null)
                {
                    // The sharp band widens with the distance, like a lens: a full-body shot keeps the whole avatar sharp.
                    effects.SetVector("_Dof", new Vector4(focus, 0.12f + focus * 0.06f, 0.03f * blur, 0f));
                    var next = Temporary(width, height);
                    Graphics.Blit(current, next, effects, DepthOfFieldPass);
                    current = next;
                }

                // The threshold first: the chain's first pass reads it.
                float bloom = settings.Strength(PhotoshootEffect.Bloom);
                effects.SetVector("_Bloom", new Vector4(0.78f, 0.3f, 0f, 0f));
                int levels = 0;
                effects.SetTexture("_BloomTex", bloom > 0f ? (Texture)Bloom(effects, current, width, height, Temporary, out levels) : Texture2D.blackTexture);
                // The chain adds every level up: averaged, then scaled by the strength.
                effects.SetVector("_Bloom", new Vector4(0.78f, 0.3f, levels > 0 ? bloom * 3.4f / levels : 0f, 0f));
                // The comic draws the avatar (up to a little behind its view point) and only screens what lies behind.
                effects.SetFloat("_Focus", focus);
                effects.SetVector("_Lens", new Vector4(
                    frame ? settings.Strength(PhotoshootEffect.LensDistortion) * 0.4f : 0f,
                    frame ? settings.Strength(PhotoshootEffect.ChromaticAberration) : 0f,
                    frame ? settings.Strength(PhotoshootEffect.Vignette) : 0f,
                    settings.Strength(PhotoshootEffect.Grain)));
                effects.SetVector("_Halftone", new Vector4(settings.Strength(PhotoshootEffect.Halftone),
                    Mathf.Max(3f, settings.ComicDotCell(height)), Mathf.Max(1f, height / 520f), 0.37f));
                effects.SetVector("_Comic", new Vector4(settings.ComicColors, settings.ComicDots ? 1f : 0f, 0f, 0f));
                Graphics.Blit(current, destination, effects, FinalPass);
            }
            finally
            {
                foreach (var texture in temporaries) RenderTexture.ReleaseTemporary(texture);
            }
        }

        // Bright parts, then a chain of half-size copies blurred down and added back up: a wide, soft glow.
        private static RenderTexture Bloom(Material effects, Texture source, int width, int height, Func<int, int, RenderTextureFormat, RenderTexture> temporary, out int count)
        {
            var levels = new List<RenderTexture>();
            int w = width / 2, h = height / 2;
            var level = temporary(w, h, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, level, effects, PrefilterPass);
            levels.Add(level);
            while (levels.Count < 6 && w > 8 && h > 8)
            {
                w /= 2;
                h /= 2;
                var smaller = temporary(w, h, RenderTextureFormat.ARGB32);
                Graphics.Blit(levels[levels.Count - 1], smaller, effects, DownPass);
                levels.Add(smaller);
            }

            for (int i = levels.Count - 1; i > 0; i--) Graphics.Blit(levels[i], levels[i - 1], effects, UpPass);
            count = levels.Count;
            return levels[0];
        }

        internal static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }

        private static Material Material()
        {
            if (material != null) return material;
            var shader = Shader.Find(ShaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null) return null;
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return material;
        }

        private static bool DepthShader()
        {
            if (depthShader == null) depthShader = Shader.Find(DepthShaderName) ?? AssetDatabase.LoadAssetAtPath<Shader>(DepthShaderPath);
            return depthShader != null;
        }
    }
}
