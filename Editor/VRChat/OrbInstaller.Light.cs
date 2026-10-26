using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's light (Orbiters contracts' Docs/orb.md, the Light option on N4N0's card): N4N0 becomes a light, for a
    /// dark world or a photo. A point light under N4N0, without shadows, lit in one of eight colours at a brightness
    /// from 0 to 1; N4N0 takes the light's colour. Everyone around sees it (17 synced bits). Orbiters - N4N0 sets it
    /// from N4N0's menu, and N4N0 › Light in the expressions menu does too.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>On (synced, not saved), its colour (an index of <see cref="LightColors"/>) and brightness (synced, saved).</summary>
        public const string Light = "N4N0/Light/On", LightColor = "N4N0/Light/Color", LightLevel = "N4N0/Light/Level";
        public const float LightLevelDefault = .6f;
        internal const string LightObject = Hop + "/Light";
        // How far the light reaches at ReferenceEyes (Bind scales it with the avatar), and its intensity at level 0 and 1.
        internal const float LightRange = 5f, LightDim = .15f, LightBright = 3f;

        /// <summary>The light's colours, as Orbiters - N4N0's menu names them (contracts <c>orb::LIGHT_COLORS</c>, sRGB).</summary>
        public static readonly IReadOnlyList<(string name, Color color)> LightColors = new[]
        {
            ("White", Srgb(255, 255, 255)), ("Warm", Srgb(255, 197, 143)), ("Sunset", Srgb(255, 128, 64)), ("Pink", Srgb(255, 105, 180)),
            ("Violet", Srgb(164, 104, 255)), ("Blue", Srgb(72, 132, 255)), ("Cyan", Srgb(64, 224, 232)), ("Green", Srgb(0, 218, 109)),
        };

        private static Color Srgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        /// <summary>Whether a placed orb has its light.</summary>
        public static bool HasLight(Transform orb)
        {
            var light = orb != null ? orb.Find(LightObject) : null;
            return light != null && light.GetComponent<UnityEngine.Light>() != null;
        }

        /// <summary>Adds the light under a placed orb, or takes it away (with Undo).</summary>
        internal static void SetLightObject(Transform orb, bool on)
        {
            var existing = orb.Find(LightObject);
            if (!on)
            {
                if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);
                return;
            }
            if (existing != null) return;
            var hop = orb.Find(Hop) ?? throw new InvalidOperationException("This N4N0 was modified: add it again.");
            var go = new GameObject("Light");
            Undo.RegisterCreatedObjectUndo(go, "N4N0 light");
            go.transform.SetParent(hop, false);
            var light = go.AddComponent<UnityEngine.Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            // A pixel light: what it lights shows it, not only the vertices nearby.
            light.renderMode = LightRenderMode.ForcePixel;
            light.color = LightColors[0].color;
            light.intensity = Mathf.Lerp(LightDim, LightBright, LightLevelDefault);
            FitLight(light);
            // The animator turns it on.
            go.SetActive(false);
        }

        // As far as N4N0 is large: the light's range never scales with its object.
        private static void FitLight(UnityEngine.Light light) => light.range = LightRange * Mathf.Abs(light.transform.lossyScale.x);

        /// <summary>Fits a placed orb's light to how large Bind made it.</summary>
        private static void FitLight(Transform orb)
        {
            var light = orb.Find(LightObject)?.GetComponent<UnityEngine.Light>();
            if (light == null) return;
            Undo.RecordObject(light, "Fit orb");
            FitLight(light);
        }

        /// <summary>
        /// The light's parameters, its layer and the N4N0 › Light submenu: on, a brightness radial and the colours. One state
        /// per colour, its brightness a blend of a dim and a bright clip; each state sets everything the layer animates (the
        /// light's object, colour and intensity), off included, so any Write Defaults leave nothing behind.
        /// </summary>
        private static void LightController(AnimatorController controller, string folder, List<VRCExpressionParameters.Parameter> parameters, List<VRCExpressionsMenu.Control> controls)
        {
            var machine = Layer(controller, "Light");
            var off = new AnimationClip { name = "Light off" };
            Curve(off, LightObject, typeof(GameObject), "m_IsActive", 0);
            var dark = HeldPropRig.State(controller, machine, "Off", off);
            machine.defaultState = dark;
            Enter(machine, dark, (Light, AnimatorConditionMode.IfNot, 0));
            for (int i = 0; i < LightColors.Count; i++)
            {
                var (name, color) = LightColors[i];
                var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = LightLevel, useAutomaticThresholds = false };
                tree.AddChild(LightClip(name + " dim", color, LightDim), 0);
                tree.AddChild(LightClip(name + " bright", color, LightBright), 1);
                var state = HeldPropRig.State(controller, machine, name, tree);
                Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Equals, i));
                // A colour past the last is the first.
                if (i == 0) Enter(machine, state, (Light, AnimatorConditionMode.If, 0), (LightColor, AnimatorConditionMode.Greater, LightColors.Count - 1));
            }

            parameters.Add(Parameter(Light));
            parameters.Add(Parameter(LightColor, type: VRCExpressionParameters.ValueType.Int, saved: true));
            parameters.Add(Parameter(LightLevel, type: VRCExpressionParameters.ValueType.Float, saved: true, defaultValue: LightLevelDefault));

            var colours = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            colours.controls = LightColors.Select((c, i) =>
            {
                var toggle = Control(c.name, VRCExpressionsMenu.Control.ControlType.Toggle, LightColor);
                toggle.value = i;
                return toggle;
            }).ToList();
            AssetDatabase.CreateAsset(colours, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 light colours.asset"));
            var colourMenu = Control("Colour", VRCExpressionsMenu.Control.ControlType.SubMenu, "");
            colourMenu.subMenu = colours;
            // A radial sets its first sub-parameter; its own parameter (none here) is only on while it is open.
            var brightness = Control("Brightness", VRCExpressionsMenu.Control.ControlType.RadialPuppet, "");
            brightness.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = LightLevel } };
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control> { Control("Light", VRCExpressionsMenu.Control.ControlType.Toggle, Light), brightness, colourMenu };
            AssetDatabase.CreateAsset(menu, AssetDatabase.GenerateUniqueAssetPath(folder + "/N4N0 light menu.asset"));
            var light = Control("Light", VRCExpressionsMenu.Control.ControlType.SubMenu, "");
            light.subMenu = menu;
            controls.Add(light);
        }

        // The light on, in a colour at an intensity (N4N0's patterns take the colour in the Colour layer).
        private static AnimationClip LightClip(string name, Color color, float intensity)
        {
            var clip = new AnimationClip { name = name };
            Curve(clip, LightObject, typeof(GameObject), "m_IsActive", 1);
            Curve(clip, LightObject, typeof(UnityEngine.Light), "m_Color.r", color.r);
            Curve(clip, LightObject, typeof(UnityEngine.Light), "m_Color.g", color.g);
            Curve(clip, LightObject, typeof(UnityEngine.Light), "m_Color.b", color.b);
            Curve(clip, LightObject, typeof(UnityEngine.Light), "m_Color.a", 1);
            Curve(clip, LightObject, typeof(UnityEngine.Light), "m_Intensity", intensity);
            return clip;
        }

    }
}
