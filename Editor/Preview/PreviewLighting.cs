using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.Preview
{
    /// <summary>
    /// The light the loaded scenes give shaders in the built-in pipeline, as the preview records it (contracts'
    /// Docs/project-preview.md, <c>lighting</c>): the directional lights (the sun first, at most four) with the colour
    /// shaders receive, whether they cast shadows; the ambient light facing up, round the horizon and down; the
    /// reflections' intensity. Orbiters - N4N0 draws the preview in it by default. A few property reads on the main
    /// thread, in the capture's first step.
    /// </summary>
    internal static class PreviewLighting
    {
        private const int MaxLights = 4;

        internal static PreviewData.SceneLighting Read(HashSet<Scene> loaded, Func<Transform, bool> editorOnly)
        {
            var lighting = new PreviewData.SceneLighting();
            var sun = RenderSettings.sun;
            // The loaded scenes' own active objects (the same as FindObjectsByType's in the editor, and a test's preview
            // scene too, which FindObjectsByType does not search).
            var lights = loaded.SelectMany(s => s.GetRootGameObjects()).SelectMany(r => r.GetComponentsInChildren<Light>(false))
                .Where(l => l.type == LightType.Directional && l.isActiveAndEnabled &&
                            (l.gameObject.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) == 0 && !editorOnly(l.transform))
                .OrderBy(l => l == sun ? 0 : 1)
                .ThenByDescending(l => l.intensity * l.color.maxColorComponent)
                .Take(MaxLights);
            foreach (var l in lights)
            {
                var c = ShaderColor(l);
                lighting.Lights.Add(new PreviewData.SceneLighting.Directional
                {
                    Direction = l.transform.forward.normalized,
                    Color = new Vector3(c.r, c.g, c.b),
                    Intensity = l.intensity,
                    Shadows = l.shadows != LightShadows.None,
                    ShadowStrength = l.shadowStrength,
                });
            }
            Ambient(lighting);
            lighting.ReflectionIntensity = RenderSettings.reflectionIntensity;
            return lighting;
        }

        // The colour shaders get as _LightColor0: the colour (times its temperature's when the project uses them) and
        // the intensity, multiplied in linear or gamma space as the project says.
        internal static Color ShaderColor(Light l)
        {
            var c = l.color;
            if (GraphicsSettings.lightsUseColorTemperature && l.useColorTemperature) c *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature);
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return c * l.intensity;
            return GraphicsSettings.lightsUseLinearIntensity ? c.linear * l.intensity : (c * l.intensity).linear;
        }

        private static void Ambient(PreviewData.SceneLighting lighting)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Vector3 V(Color c)
            {
                if (linear) c = c.linear;
                return new Vector3(c.r, c.g, c.b);
            }
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Trilight:
                    lighting.AmbientMode = "trilight";
                    lighting.Sky = V(RenderSettings.ambientSkyColor);
                    lighting.Equator = V(RenderSettings.ambientEquatorColor);
                    lighting.Ground = V(RenderSettings.ambientGroundColor);
                    return;
                case AmbientMode.Flat:
                    lighting.AmbientMode = "flat";
                    lighting.Sky = lighting.Equator = lighting.Ground = V(RenderSettings.ambientLight);
                    return;
                default:
                    lighting.AmbientMode = "skybox";
                    SkyboxAmbient(lighting, RenderSettings.ambientIntensity, () => RenderSettings.ambientProbe, DynamicGI.UpdateEnvironment);
                    return;
            }
        }

        /// <summary>
        /// The skybox's ambient light, as the editor computes it into the ambient probe (intensity included; already
        /// linear). Measured in Unity 2022.3.22f1 (3 November 2026): the probe is black (every coefficient 0) until the
        /// editor computes it, which it does in an update after it opened the scene, not in the one that opened it (the
        /// capture once after the editor loads can run before), and <c>DynamicGI.UpdateEnvironment</c> computes it at
        /// once (a few milliseconds; nothing in the scene changes). A scene never baked gets it from its skybox (the
        /// default sky without a sun: about 0.17 facing up), one without a skybox from the Lighting window's Ambient
        /// Color. So a black probe is computed first (<paramref name="compute"/>), and stays unknown (no ambient light
        /// written, <c>ambient: null</c>) only when it is still black. An intensity of 0 makes it black for real: black,
        /// known. Orbiters - N4N0 draws a scene whose light shows nothing (no sun, an ambient light black or unknown)
        /// in its studio light.
        /// </summary>
        internal static void SkyboxAmbient(PreviewData.SceneLighting lighting, float intensity, Func<SphericalHarmonicsL2> probe, Action compute)
        {
            if (intensity <= 0)
            {
                lighting.Sky = lighting.Equator = lighting.Ground = Vector3.zero;
                return;
            }
            var sh = probe();
            if (Black(sh))
            {
                compute();
                sh = probe();
                if (Black(sh)) return;
            }
            var directions = new[] { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.down };
            var results = new Color[directions.Length];
            sh.Evaluate(directions, results);
            lighting.Sky = Positive(results[0]);
            lighting.Equator = Positive((results[1] + results[2] + results[3] + results[4]) * 0.25f);
            lighting.Ground = Positive(results[5]);
        }

        // Never computed: every coefficient 0 (a computed probe of a black sky is too, which reads the same).
        private static bool Black(SphericalHarmonicsL2 sh)
        {
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    if (Mathf.Abs(sh[c, k]) > 1e-6f) return false;
            return true;
        }

        // Spherical harmonics can ring below zero.
        private static Vector3 Positive(Color c) => new Vector3(Mathf.Max(0, c.r), Mathf.Max(0, c.g), Mathf.Max(0, c.b));
    }
}
