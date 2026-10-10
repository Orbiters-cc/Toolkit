using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Rainbowo: pointing the right index finger draws a rainbow in the air, with sparkles. The rainbow stays where it was
    /// drawn; the next one replaces it, or with "Leave in world" they all stay until Rainbowo is turned off. A native trail
    /// on the fingertip (no world constraint needed: a trail's points are in the world already) and VRChat's mobile
    /// shaders, so every platform shows it. No Orbiters script: <see cref="Bind"/> ties the fingertip to the avatar.
    /// </summary>
    public static class RainbowoInstaller
    {
        public const string MenuPath = "Rainbowo";
        public const string PrefabName = "Rainbowo.prefab";
        internal const string Enabled = "Rainbowo/Enabled", Keep = "Rainbowo/LeaveInWorld";
        internal const string Fingertip = "Fingertip", Rainbow = "Fingertip/Rainbow", Sparkles = "Fingertip/Sparkles";
        // Pointing with the index finger.
        internal const int Point = 3;
        // How long a rainbow lasts while Rainbowo is on: until it is cleared, in practice.
        internal const float Lasting = 10800;
        internal const float Width = .09f;
        // The pride flag's six colours, from the top.
        internal static readonly Color[] Colours =
        {
            new Color32(228, 3, 3, 255), new Color32(255, 140, 0, 255), new Color32(255, 237, 0, 255),
            new Color32(0, 128, 38, 255), new Color32(0, 77, 255, 255), new Color32(117, 7, 135, 255),
        };

        /// <summary>The Rainbowos below <paramref name="root"/>, known by their trail on the fingertip.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<TrailRenderer>(true).Where(t => t.name == "Rainbow").Select(t => RootOf(t.transform)).Where(r => r != null).ToList();

        private static Transform RootOf(Transform trail)
        {
            var root = trail.parent != null ? trail.parent.parent : null;
            return root != null && root.Find(Rainbow) == trail && root.Find(Fingertip).GetComponent<VRCParentConstraint>() != null ? root : null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var rainbowo in FindAll(root)) Bind(rainbowo, avatar);
        }, root => FindAll(root).Any());

        /// <summary>Creates the Rainbowo prefab with its controller, menu, parameters, textures and materials in <paramref name="folder"/>.</summary>
        public static string CreatePrefab(string folder) => SavePrefab(folder, PrefabName, "Rainbowo", "Rainbowo", root => Build(root, folder));

        private static void Build(GameObject root, string folder)
        {
            var tip = Child(root.transform, Fingertip);
            tip.localPosition = new Vector3(.25f, 1.2f, .25f);
            var follow = tip.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRC.Dynamics.VRCConstraintSource(null, 1));
            follow.IsActive = true; follow.Locked = true;

            // Six stripes across the trail's width, from the top.
            var stripes = Png(folder, "Rainbow", 4, 96, (x, y) => Colours[Mathf.Min(5, (int)((1 - y) * 6))]);
            var trail = Child(tip, "Rainbow").gameObject.AddComponent<TrailRenderer>();
            trail.sharedMaterial = Material(folder, "Rainbow", Color.white, "VRChat/Mobile/Particles/Alpha Blended");
            trail.sharedMaterial.mainTexture = stripes; EditorUtility.SetDirty(trail.sharedMaterial);
            trail.time = 0; trail.emitting = false; trail.autodestruct = false;
            trail.widthMultiplier = Width; trail.minVertexDistance = .02f;
            trail.numCornerVertices = 4; trail.numCapVertices = 4;
            trail.alignment = LineAlignment.View; trail.textureMode = LineTextureMode.Stretch;
            trail.startColor = trail.endColor = Color.white;
            Quiet(trail);

            var sparkles = Child(tip, "Sparkles").gameObject.AddComponent<ParticleSystem>();
            var main = sparkles.main;
            main.loop = true; main.playOnAwake = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.6f, 1.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.01f, .06f);
            main.startSize = new ParticleSystem.MinMaxCurve(.01f, .025f);
            main.startColor = new ParticleSystem.MinMaxGradient(RainbowGradient()) { mode = ParticleSystemGradientMode.RandomColor };
            main.gravityModifier = -.02f; main.maxParticles = 120;
            var emission = sparkles.emission;
            emission.enabled = false; emission.rateOverTime = 12; emission.rateOverDistance = 40;
            var shape = sparkles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = .03f;
            var size = sparkles.sizeOverLifetime;
            size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 1, 1, 0));
            var renderer = sparkles.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Additive(folder, "Sparkles", Sparkle(folder));
            Quiet(renderer);

            BuildController(root, folder);
        }

        private static Gradient RainbowGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(Colours.Select((c, i) => new GradientColorKey(c, i / (Colours.Length - 1f))).ToArray(),
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) });
            gradient.mode = GradientMode.Fixed;
            return gradient;
        }

        private static void Quiet(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Rainbowo.controller");
            controller.AddParameter(Enabled, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Keep, AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var off = State(controller, machine, "Off", Clip(false, 0));
            var ready = State(controller, machine, "Ready", Clip(false, Lasting));
            // One update with no time left wipes the last rainbow before the next one starts.
            var wipe = State(controller, machine, "Wipe", Clip(false, 0));
            var draw = State(controller, machine, "Draw", Clip(true, Lasting));
            machine.defaultState = off;
            Transition(off, ready, (Enabled, AnimatorConditionMode.If, 0));
            foreach (var state in new[] { ready, wipe, draw }) Transition(state, off, (Enabled, AnimatorConditionMode.IfNot, 0));
            Transition(ready, draw, ("GestureRight", AnimatorConditionMode.Equals, Point), (Keep, AnimatorConditionMode.If, 0));
            Transition(ready, wipe, ("GestureRight", AnimatorConditionMode.Equals, Point), (Keep, AnimatorConditionMode.IfNot, 0));
            Transition(wipe, draw, ("GestureRight", AnimatorConditionMode.Equals, Point));
            Transition(wipe, ready, ("GestureRight", AnimatorConditionMode.NotEqual, Point));
            Transition(draw, ready, ("GestureRight", AnimatorConditionMode.NotEqual, Point));
            Install(root, folder, controller, MenuPath, "Rainbowo menu", new List<VRCExpressionsMenu.Control>
            {
                Control("Rainbowo", VRCExpressionsMenu.Control.ControlType.Toggle, Enabled),
                Control("Leave in world", VRCExpressionsMenu.Control.ControlType.Toggle, Keep),
            }, new[] { Parameter(Enabled), Parameter(Keep) });
        }

        private static AnimationClip Clip(bool draw, float lifetime)
        {
            var clip = new AnimationClip();
            Curve(clip, Rainbow, typeof(TrailRenderer), "m_Emitting", draw ? 1 : 0);
            Curve(clip, Rainbow, typeof(TrailRenderer), "m_Time", lifetime);
            Curve(clip, Sparkles, typeof(ParticleSystem), "EmissionModule.enabled", draw ? 1 : 0);
            return clip;
        }

        /// <summary>
        /// Fits a placed Rainbowo to <paramref name="avatar"/>: the rainbow comes from the tip of its right index finger.
        /// Recorded with Undo; binding again (another avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform rainbowo, Transform avatar)
        {
            if (rainbowo == null) throw new ArgumentNullException(nameof(rainbowo));
            FollowFingertip(rainbowo.Find(Fingertip), Humanoid(avatar, "Rainbowo"), avatar, "Rainbowo");
        }
    }
}
