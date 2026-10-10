using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The orbit orb: a small glowing orb circling the tip of the right index finger. It hangs on a short PhysBone chain, so
    /// it lags and springs behind the hand, and anyone can grab it and let it bounce back. The orbit stays upright with the
    /// avatar however the hand turns. An optional light (off by default, no shadows) lets it light its surroundings. Native
    /// avatar components only, a shader of its own in its folder (like the hand screen's) and no Orbiters script:
    /// <see cref="Bind"/> ties it to the avatar.
    /// </summary>
    public static class OrbitOrbInstaller
    {
        public const string MenuPath = "Orbit orb";
        public const string PrefabName = "Orbit orb.prefab";
        internal const string Enabled = "OrbitOrb/Enabled", Lit = "OrbitOrb/Light", Orb = "OrbitOrb/Orb";
        internal const string Fingertip = "Fingertip", Upright = Fingertip + "/Upright", Orbit = Upright + "/Orbit", Tether = Orbit + "/Tether";
        internal static readonly string Ball = Tether + "/1/2/Orb", Lamp = Ball + "/Light";
        internal static readonly Color Colour = new Color(.2f, .75f, 1f);
        private const string ShaderTemplate = "Packages/orbiters.toolkit/Editor/VRChat/OrbitOrb.shader.txt";
        // One turn around the fingertip.
        internal const float OrbitSeconds = 4;
        // The tether's three links lean out from the orbit's axis, so the orb circles about 4.5 cm from it, 8 cm up.
        private const float Link = .03f, Lean = 30;

        /// <summary>The orbit orbs below <paramref name="root"/>, known by their tether.</summary>
        internal static IEnumerable<Transform> FindAll(GameObject root) => root == null ? Enumerable.Empty<Transform>() :
            root.GetComponentsInChildren<VRCPhysBone>(true).Where(p => p.parameter == Orb).Select(p => RootOf(p.transform)).Where(r => r != null).ToList();

        private static Transform RootOf(Transform tether)
        {
            for (var root = tether.parent; root != null; root = root.parent)
                if (root.Find(Tether) == tether) return root;
            return null;
        }

        [InitializeOnLoadMethod]
        private static void RegisterAttachHook() => AttachmentHooks.Register((root, avatar) =>
        {
            foreach (var orb in FindAll(root)) Bind(orb, avatar);
        }, root => FindAll(root).Any());

        /// <summary>Creates the orbit orb prefab with its controller, menu, parameters, shader and materials in <paramref name="folder"/>.</summary>
        public static string CreatePrefab(string folder) => SavePrefab(folder, PrefabName, "Orbit orb", "orbit orb", root => Build(root, folder));

        private static void Build(GameObject root, string folder)
        {
            var tip = Child(root.transform, Fingertip);
            tip.localPosition = new Vector3(.25f, 1.2f, .25f);
            var follow = tip.gameObject.AddComponent<VRCParentConstraint>();
            follow.Sources.Add(new VRCConstraintSource(null, 1));
            follow.IsActive = true; follow.Locked = true;
            // Turned like the avatar (source 0, set by Bind), not like the finger.
            var upright = Child(tip, "Upright");
            var level = upright.gameObject.AddComponent<VRCRotationConstraint>();
            level.Sources.Add(new VRCConstraintSource(null, 1));
            level.IsActive = true; level.Locked = true;
            var orbit = Child(upright, "Orbit");

            // The chain's root stays on the orbit; the links and the orb lag behind it.
            var tether = Child(orbit, "Tether");
            var lean = Quaternion.Euler(0, 0, -Lean) * Vector3.up * Link;
            var link = Child(tether, "1"); link.localPosition = lean;
            link = Child(link, "2"); link.localPosition = lean;
            var ball = Child(link, "Orb"); ball.localPosition = lean;
            var phys = tether.gameObject.AddComponent<VRCPhysBone>();
            phys.rootTransform = tether; phys.integrationType = VRCPhysBoneBase.IntegrationType.Advanced;
            phys.pull = .15f; phys.spring = .7f; phys.stiffness = .1f; phys.gravity = -.05f; phys.gravityFalloff = 1;
            phys.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion; phys.immobile = 0;
            phys.radius = .02f; phys.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            phys.allowGrabbing = VRCPhysBoneBase.AdvancedBool.True; phys.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            phys.grabMovement = .6f; phys.snapToHand = false; phys.maxStretch = .5f;
            phys.resetWhenDisabled = true; phys.parameter = Orb;

            var shader = CopyShader(ShaderTemplate, folder + "/Orbit orb.shader");
            var core = OrbMaterial(folder, shader, "Orb core", false);
            var halo = OrbMaterial(folder, shader, "Orb glow", true);
            var shown = new List<Transform>
            {
                Primitive(ball, "Core", PrimitiveType.Sphere, Vector3.one * .03f, Vector3.zero, core),
                Primitive(ball, "Glow", PrimitiveType.Sphere, Vector3.one * .085f, Vector3.zero, halo),
            };
            foreach (var shape in shown) Quiet(shape.GetComponent<Renderer>());

            var sparkles = Child(ball, "Sparkles").gameObject.AddComponent<ParticleSystem>();
            var main = sparkles.main;
            main.loop = true; main.playOnAwake = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.5f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0, .02f);
            main.startSize = new ParticleSystem.MinMaxCurve(.006f, .016f);
            main.startColor = new ParticleSystem.MinMaxGradient(Colour, Color.white);
            main.maxParticles = 40;
            var emission = sparkles.emission;
            emission.rateOverTime = 14; emission.rateOverDistance = 20;
            var cloud = sparkles.shape;
            cloud.shapeType = ParticleSystemShapeType.Sphere; cloud.radius = .02f;
            var size = sparkles.sizeOverLifetime;
            size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.EaseInOut(0, 1, 1, 0));
            var renderer = sparkles.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Additive(folder, "Orb sparkles", Sparkle(folder));
            Quiet(renderer);

            var light = Child(ball, "Light").gameObject.AddComponent<Light>();
            light.type = LightType.Point; light.color = Colour; light.range = .8f; light.intensity = 1;
            light.shadows = LightShadows.None; light.renderMode = LightRenderMode.Auto;
            light.gameObject.SetActive(false);
            // What the orb shows is no part of the chain.
            shown.Add(sparkles.transform); shown.Add(light.transform);
            phys.ignoreTransforms = shown;

            BuildController(root, folder);
            orbit.gameObject.SetActive(false);
        }

        private static Material OrbMaterial(string folder, Shader shader, string name, bool halo)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", Colour);
            material.SetFloat("_Halo", halo ? 1 : 0);
            material.SetFloat("_Intensity", halo ? .9f : 1.4f);
            material.SetFloat("_Falloff", halo ? 2.5f : 2);
            if (halo)
            {
                material.SetFloat("_DstBlend", (float)BlendMode.One); material.SetFloat("_ZWrite", 0);
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetOverrideTag("RenderType", "Transparent");
                // Viewers who hide custom shaders would see a solid ball where the halo is.
                material.SetOverrideTag("VRCFallback", "Hidden");
            }
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static void Quiet(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        internal static void BuildController(GameObject root, string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Orbit orb.controller");
            controller.AddParameter(Enabled, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Lit, AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var hidden = new AnimationClip();
            Curve(hidden, Orbit, typeof(GameObject), "m_IsActive", 0);
            var off = State(controller, machine, "Off", hidden);
            var on = State(controller, machine, "Orbit", Spin());
            machine.defaultState = off;
            Transition(off, on, (Enabled, AnimatorConditionMode.If, 0));
            Transition(on, off, (Enabled, AnimatorConditionMode.IfNot, 0));

            var lighting = Layer(controller, "Light");
            var dark = State(controller, lighting, "Dark", Shine(false));
            var shine = State(controller, lighting, "Shine", Shine(true));
            lighting.defaultState = dark;
            Transition(dark, shine, (Lit, AnimatorConditionMode.If, 0));
            Transition(shine, dark, (Lit, AnimatorConditionMode.IfNot, 0));

            Install(root, folder, controller, MenuPath, "Orb menu", new List<VRCExpressionsMenu.Control>
            {
                Control("Orb", VRCExpressionsMenu.Control.ControlType.Toggle, Enabled),
                Control("Light", VRCExpressionsMenu.Control.ControlType.Toggle, Lit),
            }, new[] { Parameter(Enabled), Parameter(Lit) });
        }

        // Shown, turning once every OrbitSeconds: the only looping clip, every other one holds still for 0.2 s.
        private static AnimationClip Spin()
        {
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Orbit, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, OrbitSeconds, 1));
            foreach (char axis in "xz") AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Orbit, typeof(Transform), "localEulerAnglesRaw." + axis), AnimationCurve.Constant(0, OrbitSeconds, 0));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(Orbit, typeof(Transform), "localEulerAnglesRaw.y"), AnimationCurve.Linear(0, 0, OrbitSeconds, 360));
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        private static AnimationClip Shine(bool on)
        {
            var clip = new AnimationClip();
            Curve(clip, Lamp, typeof(GameObject), "m_IsActive", on ? 1 : 0);
            return clip;
        }

        /// <summary>
        /// Fits a placed orbit orb to <paramref name="avatar"/>: it circles the tip of the right index finger, its orbit upright
        /// with the avatar. Recorded with Undo; binding again (another avatar, a moved armature) replaces the earlier fit.
        /// </summary>
        public static void Bind(Transform orb, Transform avatar)
        {
            if (orb == null) throw new ArgumentNullException(nameof(orb));
            var animator = Humanoid(avatar, "orbit orb");
            var upright = orb.Find(Upright);
            var level = upright != null ? upright.GetComponent<VRCRotationConstraint>() : null;
            if (level == null || level.Sources.Count < 1) throw new InvalidOperationException("This orbit orb was modified: add it again.");
            FollowFingertip(orb.Find(Fingertip), animator, avatar, "orbit orb");
            Undo.RecordObjects(new Object[] { upright, level }, "Fit orbit orb");
            upright.rotation = avatar.rotation;
            level.Sources[0] = new VRCConstraintSource(avatar, 1, Vector3.zero, Vector3.zero);
            EditorUtility.SetDirty(level);
        }
    }
}
