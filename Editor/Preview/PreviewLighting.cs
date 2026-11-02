using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

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
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(l => l.type == LightType.Directional && l.isActiveAndEnabled && loaded.Contains(l.gameObject.scene) &&
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
                    // The skybox's, as the editor computed it into the ambient probe (intensity included; already linear).
                    lighting.AmbientMode = "skybox";
                    var directions = new[] { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.down };
                    var results = new Color[directions.Length];
                    RenderSettings.ambientProbe.Evaluate(directions, results);
                    var equator = (results[1] + results[2] + results[3] + results[4]) * 0.25f;
                    var all = results.Aggregate(0f, (m, c) => Mathf.Max(m, c.maxColorComponent));
                    // A probe never computed is black: unknown, not a dark scene.
                    if (all <= 1e-5f) return;
                    lighting.Sky = Positive(results[0]);
                    lighting.Equator = Positive(equator);
                    lighting.Ground = Positive(results[5]);
                    return;
            }
        }

        // Spherical harmonics can ring below zero.
        private static Vector3 Positive(Color c) => new Vector3(Mathf.Max(0, c.r), Mathf.Max(0, c.g), Mathf.Max(0, c.b));
    }
}
