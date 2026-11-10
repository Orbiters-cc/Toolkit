using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    // The light presets.
    public static partial class PhotoshootService
    {
        public static List<LightPresetOption> CreateLightPresets()
        {
            return new List<LightPresetOption>
            {
                new LightPresetOption
                {
                    displayName = "Studio Soft",
                    ambientColor = new Color(0.34f, 0.34f, 0.36f),
                    keyColor = Color.white,
                    keyIntensity = 1.15f,
                    keyRotation = new Vector3(42f, -34f, 0f),
                    fillColor = new Color(0.78f, 0.86f, 1f),
                    fillIntensity = 0.55f,
                    fillPosition = new Vector3(-1.9f, 1.35f, 2.2f),
                    rimColor = new Color(0.82f, 0.92f, 1f),
                    rimIntensity = 0.55f,
                    rimRotation = new Vector3(145f, 28f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Product Bright",
                    ambientColor = new Color(0.45f, 0.45f, 0.45f),
                    keyColor = Color.white,
                    keyIntensity = 1.35f,
                    keyRotation = new Vector3(38f, -12f, 0f),
                    fillColor = Color.white,
                    fillIntensity = 0.75f,
                    fillPosition = new Vector3(-1.6f, 1.1f, 2.0f),
                    rimColor = new Color(0.76f, 0.84f, 1f),
                    rimIntensity = 0.35f,
                    rimRotation = new Vector3(150f, 42f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Dramatic Rim",
                    ambientColor = new Color(0.18f, 0.18f, 0.20f),
                    keyColor = new Color(1f, 0.92f, 0.82f),
                    keyIntensity = 0.95f,
                    keyRotation = new Vector3(48f, -48f, 0f),
                    fillColor = new Color(0.58f, 0.66f, 1f),
                    fillIntensity = 0.25f,
                    fillPosition = new Vector3(-2.2f, 1.0f, 2.4f),
                    rimColor = new Color(0.62f, 0.86f, 1f),
                    rimIntensity = 1.15f,
                    rimRotation = new Vector3(140f, 34f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Warm Sunset",
                    ambientColor = new Color(0.30f, 0.24f, 0.22f),
                    keyColor = new Color(1f, 0.70f, 0.46f),
                    keyIntensity = 1.2f,
                    keyRotation = new Vector3(34f, -62f, 0f),
                    fillColor = new Color(0.62f, 0.74f, 1f),
                    fillIntensity = 0.35f,
                    fillPosition = new Vector3(-1.9f, 1.25f, 2.2f),
                    rimColor = new Color(1f, 0.82f, 0.58f),
                    rimIntensity = 0.8f,
                    rimRotation = new Vector3(136f, 48f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Cool Outdoor",
                    ambientColor = new Color(0.30f, 0.34f, 0.40f),
                    keyColor = new Color(0.86f, 0.94f, 1f),
                    keyIntensity = 1.05f,
                    keyRotation = new Vector3(52f, -26f, 0f),
                    fillColor = new Color(0.72f, 0.82f, 1f),
                    fillIntensity = 0.5f,
                    fillPosition = new Vector3(-1.7f, 1.2f, 2.3f),
                    rimColor = new Color(0.78f, 0.94f, 1f),
                    rimIntensity = 0.65f,
                    rimRotation = new Vector3(150f, 24f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "High Key",
                    ambientColor = new Color(0.62f, 0.62f, 0.64f),
                    keyColor = Color.white,
                    keyIntensity = 1.1f,
                    keyRotation = new Vector3(30f, -8f, 0f),
                    fillColor = Color.white,
                    fillIntensity = 0.9f,
                    fillPosition = new Vector3(-1.4f, 1.2f, 2.0f),
                    rimColor = Color.white,
                    rimIntensity = 0.45f,
                    rimRotation = new Vector3(150f, 30f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Low Key",
                    ambientColor = new Color(0.07f, 0.07f, 0.08f),
                    keyColor = new Color(1f, 0.95f, 0.88f),
                    keyIntensity = 1.25f,
                    keyRotation = new Vector3(28f, -78f, 0f),
                    fillColor = new Color(0.6f, 0.65f, 0.8f),
                    fillIntensity = 0.06f,
                    fillPosition = new Vector3(-2.2f, 1.0f, 2.4f),
                    rimColor = new Color(0.85f, 0.9f, 1f),
                    rimIntensity = 0.35f,
                    rimRotation = new Vector3(150f, 60f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Cinematic Rim",
                    // Avatar shaders are mostly toon shaders that light a surface by the strongest light whatever its side,
                    // so a dark, rim-lit look comes from a low side key with softer coloured rims, not strong back lights.
                    ambientColor = new Color(0.04f, 0.045f, 0.06f),
                    keyColor = new Color(1f, 0.93f, 0.85f),
                    keyIntensity = 0.85f,
                    keyRotation = new Vector3(26f, -80f, 0f),
                    fillColor = new Color(0.4f, 0.5f, 0.8f),
                    fillIntensity = 0.03f,
                    fillPosition = new Vector3(-2.0f, 1.2f, 2.4f),
                    rimColor = new Color(0.35f, 0.85f, 1f),
                    rimIntensity = 0.55f,
                    rimRotation = new Vector3(165f, 55f, 0f),
                    rim2Color = new Color(1f, 0.6f, 0.3f),
                    rim2Intensity = 0.45f,
                    rim2Rotation = new Vector3(165f, -55f, 0f)
                },
                new LightPresetOption
                {
                    displayName = "Neon Night",
                    ambientColor = new Color(0.07f, 0.05f, 0.12f),
                    keyColor = new Color(0.85f, 0.5f, 1f),
                    keyIntensity = 0.7f,
                    keyRotation = new Vector3(30f, -70f, 0f),
                    fillColor = new Color(0.4f, 0.3f, 0.9f),
                    fillIntensity = 0.08f,
                    fillPosition = new Vector3(-2.0f, 1.2f, 2.4f),
                    rimColor = new Color(0.2f, 0.9f, 1f),
                    rimIntensity = 0.6f,
                    rimRotation = new Vector3(160f, 55f, 0f),
                    rim2Color = new Color(1f, 0.3f, 0.7f),
                    rim2Intensity = 0.5f,
                    rim2Rotation = new Vector3(160f, -55f, 0f)
                }
            };
        }
    }
}
