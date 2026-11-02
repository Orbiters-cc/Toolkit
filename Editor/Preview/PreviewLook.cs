using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.Preview
{
    /// <summary>
    /// How a material's shader takes the light, as the preview carries it (contracts' Docs/project-preview.md,
    /// Materials, schema 2): <c>lit</c> (Unity's Standard, by metallic and smoothness), <c>toon</c> (two bands, the
    /// shadowed one tinted), <c>flat</c> or <c>unlit</c>; the normal map, the metallic-smoothness map and emission.
    /// Read from the material's properties by the shader family (lilToon, Poiyomi, MToon, Unity Toon Shader, Standard
    /// and its look-alikes), never by asking the shader to draw: cheap enough for the capture's steps.
    /// </summary>
    internal static class PreviewLook
    {
        internal enum Role
        {
            Color = 0,
            Normal = 1,
            Surface = 2,
        }

        /// <summary>
        /// A picture a material needs: the texture and what it holds. A surface picture takes metallic and smoothness
        /// from the channels named (0–3: R, G, B, A; -1: none, white), inverted where the shader reads them inverted.
        /// </summary>
        internal struct Source
        {
            public Texture Texture;
            public Role Role;
            public int Metallic, Smoothness;
            public bool InvertMetallic, InvertSmoothness;
        }

        /// <summary>
        /// Fills <paramref name="into"/>'s look. <paramref name="picture"/> stores a picture (once per texture and
        /// purpose) and returns its index, -1 when none. <paramref name="mainTexture"/> is the material's main texture.
        /// </summary>
        internal static void Read(Material m, PreviewData.Material into, Texture mainTexture, Func<Source, int> picture)
        {
            var shader = m.shader;
            string name = shader != null ? shader.name.ToLowerInvariant() : "";
            into.Shading = "lit";
            into.Smoothness = 0.2f;
            if (m.HasProperty("_lilToonVersion")) LilToon(m, into, picture);
            else if (name.Contains("poiyomi") || m.HasProperty("_LightingMode") && m.HasProperty("_LightingShadowColor")) Poiyomi(m, into, picture);
            else if (m.HasProperty("_ShadeToony") && m.HasProperty("_ShadeColor")) MToon(m, into, picture);
            else if (m.HasProperty("_1st_ShadeColor")) UnityToon(m, into, picture);
            else if (name.Contains("unlit") || name.Contains("particle") || name.Contains("skybox"))
            {
                into.Shading = "unlit";
                into.Smoothness = 0;
            }
            else if (name.Contains("toon"))
            {
                // VRChat/Mobile/Toon Lit lights evenly; other toon shaders in two bands.
                into.Shading = name.Contains("toon lit") ? "flat" : "toon";
                into.Smoothness = 0;
                Normal(m, into, picture, "_BumpMap", "_BumpScale");
                Emission(m, into, picture, m.IsKeywordEnabled("_EMISSION"));
            }
            else Standard(m, into, mainTexture, name, picture);
        }

        // Standard, Standard Lite, URP Lit, Autodesk Interactive, the legacy and mobile shaders.
        private static void Standard(Material m, PreviewData.Material into, Texture mainTexture, string name, Func<Source, int> picture)
        {
            bool urp = m.HasProperty("_Smoothness") && !m.HasProperty("_Glossiness");
            float metallic = Value(m, "_Metallic", 0);
            float smoothness = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness")
                : urp ? m.GetFloat("_Smoothness")
                : name.Contains("diffuse") || name.Contains("lightmapped") || name.Contains("matcap") ? 0
                : name.Contains("specular") && m.HasProperty("_Shininess") ? Mathf.Clamp01(m.GetFloat("_Shininess")) * 0.6f
                : 0.2f;
            // Standard (and URP Lit) read metallic from the map alone, smoothness scaled by _GlossMapScale (URP: _Smoothness);
            // Standard Lite multiplies both sliders by the map.
            bool scaleOnly = m.HasProperty("_GlossMapScale") || urp;
            float glossScale = urp ? smoothness : Value(m, "_GlossMapScale", 1);
            bool fromAlbedo = m.HasProperty("_SmoothnessTextureChannel") && Mathf.RoundToInt(m.GetFloat("_SmoothnessTextureChannel")) == 1;
            var map = Picture(m, "_MetallicGlossMap");
            if (map != null)
            {
                into.Metallic = scaleOnly ? 1 : metallic;
                into.Smoothness = scaleOnly ? glossScale : smoothness;
                into.SurfaceTexture = fromAlbedo && mainTexture != null
                    ? picture(new Source { Texture = mainTexture, Role = Role.Surface, Metallic = -1, Smoothness = 3 })
                    : picture(new Source { Texture = map, Role = Role.Surface, Metallic = 0, Smoothness = 3 });
                // Metallic from the map but smoothness from the albedo cannot share one picture: the map's metallic is dropped.
                if (fromAlbedo && mainTexture != null) into.Metallic = scaleOnly ? 0 : metallic;
            }
            else
            {
                into.Metallic = metallic;
                into.Smoothness = smoothness;
                if (fromAlbedo && mainTexture != null)
                {
                    into.Smoothness = scaleOnly ? glossScale : smoothness;
                    into.SurfaceTexture = picture(new Source { Texture = mainTexture, Role = Role.Surface, Metallic = -1, Smoothness = 3 });
                }
            }
            Normal(m, into, picture, m.HasProperty("_BumpMap") ? "_BumpMap" : "_NormalMap", "_BumpScale");
            Emission(m, into, picture, m.IsKeywordEnabled("_EMISSION"));
        }

        private static void LilToon(Material m, PreviewData.Material into, Func<Source, int> picture)
        {
            if (Value(m, "_AsUnlit", 0) >= 0.99f) into.Shading = "unlit";
            else if (On(m, "_UseShadow"))
            {
                into.Shading = "toon";
                var c = Linear(m, "_ShadowColor", new Color(0.82f, 0.76f, 0.85f));
                float strength = Value(m, "_ShadowStrength", 1) * (m.HasProperty("_ShadowColor") ? m.GetColor("_ShadowColor").a : 1);
                into.Shade = Vector3.Lerp(Vector3.one, new Vector3(c.r, c.g, c.b), strength);
                into.ShadeBorder = Value(m, "_ShadowBorder", 0.5f);
                into.ShadeBlur = Value(m, "_ShadowBlur", 0.1f);
            }
            else into.Shading = "flat";
            into.Smoothness = 0;
            if (On(m, "_UseReflection"))
            {
                into.Metallic = Value(m, "_Metallic", 0);
                into.Smoothness = Value(m, "_Smoothness", 1);
                var smooth = Picture(m, "_SmoothnessTex");
                var metal = Picture(m, "_MetallicGlossMap");
                if (smooth != null) into.SurfaceTexture = picture(new Source { Texture = smooth, Role = Role.Surface, Metallic = metal == smooth ? 0 : -1, Smoothness = 0 });
                else if (metal != null) into.SurfaceTexture = picture(new Source { Texture = metal, Role = Role.Surface, Metallic = 0, Smoothness = -1 });
            }
            if (On(m, "_UseBumpMap")) Normal(m, into, picture, "_BumpMap", "_BumpScale");
            Emission(m, into, picture, On(m, "_UseEmission"), Value(m, "_EmissionBlend", 1));
        }

        private static void Poiyomi(Material m, PreviewData.Material into, Func<Source, int> picture)
        {
            int mode = Mathf.RoundToInt(Value(m, "_LightingMode", 5));
            float strength = Value(m, "_ShadowStrength", 1);
            Vector3 Tint(string property, float scale)
            {
                var c = Linear(m, property, Color.white);
                return Vector3.Lerp(Vector3.one, new Vector3(c.r, c.g, c.b) * scale, strength);
            }
            switch (mode)
            {
                case 0: // Texture ramp: a soft step, tinted.
                    Toon(into, Tint("_LightingShadowColor", 0.55f), 0.5f, 0.4f);
                    break;
                case 1: // Multilayer math.
                    Toon(into, Tint("_ShadowColor", 1), Value(m, "_ShadowBorder", 0.5f), Value(m, "_ShadowBlur", 0.1f));
                    break;
                case 2: // Wrapped: a wide, soft fall-off.
                    Toon(into, Tint("_LightingShadowColor", 0.4f), 0.45f, 0.9f);
                    break;
                case 4: // Shade map.
                    Toon(into, Tint("_1st_ShadeColor", 1), Value(m, "_BaseColor_Step", 0.5f), Value(m, "_BaseShade_Feather", 0.0001f));
                    break;
                case 3: // Skin, realistic, cloth.
                case 6:
                case 7:
                    into.Shading = "lit";
                    break;
                default: // Flat, SDF (faces).
                    into.Shading = "flat";
                    break;
            }
            into.Metallic = 0;
            into.Smoothness = 0;
            if (On(m, "_MochieBRDF"))
            {
                into.Metallic = Value(m, "_MochieMetallicMultiplier", 0);
                into.Smoothness = Value(m, "_MochieRoughnessMultiplier", 1);
                var maps = Picture(m, "_MochieMetallicMaps");
                if (maps != null)
                {
                    int Channel(string p, int d) { int c = Mathf.RoundToInt(Value(m, p, d)); return c >= 0 && c <= 3 ? c : -1; }
                    into.SurfaceTexture = picture(new Source
                    {
                        Texture = maps,
                        Role = Role.Surface,
                        Metallic = Channel("_MochieMetallicMapsMetallicChannel", 0),
                        Smoothness = Channel("_MochieMetallicMapsRoughnessChannel", 1),
                        InvertMetallic = On(m, "_MochieMetallicMapInvert"),
                        InvertSmoothness = On(m, "_MochieRoughnessMapInvert"),
                    });
                }
            }
            Normal(m, into, picture, "_BumpMap", "_BumpScale");
            Emission(m, into, picture, On(m, "_EnableEmission"), Value(m, "_EmissionStrength", 1));
        }

        private static void MToon(Material m, PreviewData.Material into, Func<Source, int> picture)
        {
            // MToon draws the shade colour where unlit: as a tint of the lit colour.
            var lit = Linear(m, "_Color", Color.white);
            var shade = Linear(m, "_ShadeColor", new Color(0.97f, 0.81f, 0.86f));
            Vector3 tint = new Vector3(Ratio(shade.r, lit.r), Ratio(shade.g, lit.g), Ratio(shade.b, lit.b));
            float toony = Value(m, "_ShadeToony", 0.9f), shift = Value(m, "_ShadeShift", 0);
            Toon(into, tint, Mathf.Clamp01(0.5f - shift * 0.5f), Mathf.Clamp01(1 - toony));
            into.Smoothness = 0;
            Normal(m, into, picture, "_BumpMap", "_BumpScale");
            Emission(m, into, picture, m.HasProperty("_EmissionColor"));
        }

        private static void UnityToon(Material m, PreviewData.Material into, Func<Source, int> picture)
        {
            var lit = Linear(m, "_BaseColor", Color.white);
            var shade = Linear(m, "_1st_ShadeColor", Color.white);
            Toon(into, new Vector3(Ratio(shade.r, lit.r), Ratio(shade.g, lit.g), Ratio(shade.b, lit.b)),
                Value(m, "_BaseColor_Step", 0.5f), Value(m, "_BaseShade_Feather", 0.0001f));
            into.Smoothness = 0;
            Normal(m, into, picture, "_NormalMap", "_BumpScale");
            Emission(m, into, picture, m.HasProperty("_Emissive_Color"), 1, "_Emissive_Color", "_Emissive_Tex");
        }

        private static void Toon(PreviewData.Material into, Vector3 shade, float border, float blur)
        {
            into.Shading = "toon";
            into.Shade = new Vector3(Mathf.Clamp01(shade.x), Mathf.Clamp01(shade.y), Mathf.Clamp01(shade.z));
            into.ShadeBorder = Mathf.Clamp01(border);
            into.ShadeBlur = Mathf.Clamp01(blur);
        }

        private static void Normal(Material m, PreviewData.Material into, Func<Source, int> picture, string texture, string scale)
        {
            var t = Picture(m, texture);
            if (t == null) return;
            into.NormalTexture = picture(new Source { Texture = t, Role = Role.Normal });
            into.NormalScale = Value(m, scale, 1);
        }

        private static void Emission(Material m, PreviewData.Material into, Func<Source, int> picture, bool on, float strength = 1,
            string color = "_EmissionColor", string texture = "_EmissionMap")
        {
            if (!on || !m.HasProperty(color)) return;
            var c = Linear(m, color, Color.black) * strength;
            if (c.r <= 0.001f && c.g <= 0.001f && c.b <= 0.001f) return;
            into.Emission = new Vector3(c.r, c.g, c.b);
            var t = Picture(m, texture);
            if (t != null) into.EmissionTexture = picture(new Source { Texture = t, Role = Role.Color });
        }

        private static float Ratio(float shade, float lit) => lit > 0.001f ? Mathf.Clamp01(shade / lit) : 1;

        private static bool On(Material m, string property) => m.HasProperty(property) && m.GetFloat(property) > 0.5f;

        // A float as the shader computes with it: a [Gamma] slider (Standard's Metallic) is converted in a linear project.
        private static float Value(Material m, string property, float otherwise)
        {
            if (!m.HasProperty(property)) return otherwise;
            float v = m.GetFloat(property);
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || m.shader == null) return v;
            int i = m.shader.FindPropertyIndex(property);
            return i >= 0 && (m.shader.GetPropertyFlags(i) & ShaderPropertyFlags.Gamma) != 0 ? Mathf.GammaToLinearSpace(v) : v;
        }

        // A colour as the shader computes with it (linear in a linear project; HDR kept).
        private static Color Linear(Material m, string property, Color otherwise)
        {
            var c = m.HasProperty(property) ? m.GetColor(property) : otherwise;
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
        }

        private static Texture Picture(Material m, string property)
        {
            if (!m.HasProperty(property)) return null;
            var t = m.GetTexture(property);
            return t != null && t.dimension == TextureDimension.Tex2D ? t : null;
        }
    }
}
