using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.Dynamics;
using VRC.SDKBase.Editor.BuildPipeline;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's screen mirror (Orbiters contracts' Docs/orb.md, Screen mirror, screen version 14): avatars cannot read a
    /// world's textures, so N4N0 carries a small camera of its own. Its owner aims it with their eyes at the world's video
    /// screen, from N4N0's menu, and it stays there in the world, rendering a 384 × 216 render texture that N4N0's
    /// projected screen shows on its Screen mirror page, wherever its owner walks. The camera renders on the wearer's client
    /// only (other players see the page without the picture), every other frame while the page is up, players and menus
    /// left out. Orbiters - N4N0 drives it through three local parameters: <see cref="MirrorOn"/> (the camera renders),
    /// <see cref="MirrorPlaced"/> (it stays where it is in the world, else it follows the eyes) and
    /// <see cref="MirrorZoom"/>. PC only: phones have no avatar cameras.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Local: the camera renders (on the wearer's client) and the screen shows its picture.</summary>
        public const string MirrorOn = "N4N0/Mirror/On";
        /// <summary>Local: the camera stays where it is in the world; off, it looks where its owner looks.</summary>
        public const string MirrorPlaced = "N4N0/Mirror/Placed";
        /// <summary>Local, 0 to 1: the camera's zoom, from <see cref="MirrorWideFov"/> to <see cref="MirrorNarrowFov"/>.</summary>
        public const string MirrorZoom = "N4N0/Mirror/Zoom";
        internal const string Mirror = Shown + "/Mirror", MirrorCamera = Mirror + "/Camera";
        /// <summary>The picture's size, 16:9 like the screen's picture place.</summary>
        internal const int MirrorWidth = 384, MirrorHeight = 216;
        /// <summary>The camera's vertical field of view at zoom 0 and 1 (degrees); the zoom between is even in the picture's scale.</summary>
        internal const float MirrorWideFov = 40f, MirrorNarrowFov = 4f;
        /// <summary>The screen material's mirror texture and the animated switch that shows it.</summary>
        internal const string MirrorTextureProperty = "_Mirror", MirrorLiveProperty = "_MirrorLive";
        // What the camera leaves out: UI, players (everyone else and the wearer, N4N0 with them), VRChat's menus, mirrors'
        // reflections and VRChat's internal UI.
        private const int MirrorHiddenLayers = (1 << 5) | (1 << 9) | (1 << 10) | (1 << 12) | (1 << 18) | (1 << 19);
        private static readonly float[] MirrorZoomSteps = { 0, .25f, .5f, .75f, 1 };

        /// <summary>The vertical field of view at <paramref name="zoom"/> (0 to 1).</summary>
        internal static float MirrorFov(float zoom)
        {
            float wide = Mathf.Tan(MirrorWideFov * Mathf.Deg2Rad / 2), narrow = Mathf.Tan(MirrorNarrowFov * Mathf.Deg2Rad / 2);
            return 2 * Mathf.Atan(wide * Mathf.Pow(narrow / wide, Mathf.Clamp01(zoom))) * Mathf.Rad2Deg;
        }

        /// <summary>Whether a placed orb has the screen mirror's camera.</summary>
        public static bool HasMirror(Transform orb) => orb != null && orb.Find(MirrorCamera) != null;

        /// <summary>
        /// The mirror's camera below <paramref name="orb"/>'s shown part: held to the owner's eyes and line of sight (the
        /// <see cref="Gaze"/> frame, by a VRChat Parent Constraint the animator freezes in the world), off; its render
        /// texture in <paramref name="folder"/>, which the screen's material shows (<see cref="SetMirrorTexture"/>).
        /// </summary>
        internal static Transform AddMirror(Transform orb, string folder)
        {
            var existing = orb.Find(Mirror);
            if (existing != null && HasMirror(orb)) return existing;
            if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);
            var gaze = orb.Find(Gaze);
            var shown = orb.Find(Shown);
            if (gaze == null || shown == null) throw new System.InvalidOperationException("This N4N0 was modified: add it again.");
            var mirror = new GameObject("Mirror").transform;
            Undo.RegisterCreatedObjectUndo(mirror.gameObject, "N4N0 screen mirror");
            mirror.SetParent(shown, false);
            var hold = mirror.gameObject.AddComponent<VRCParentConstraint>();
            hold.Sources.Add(new VRCConstraintSource(gaze, 1f, Vector3.zero, Vector3.zero));
            hold.IsActive = true; hold.Locked = true;
            hold.FreezeToWorld = false;
            hold.RebakeOffsetsWhenUnfrozen = false;
            var holder = Child(mirror, "Camera");
            var camera = holder.gameObject.AddComponent<Camera>();
            camera.fieldOfView = MirrorFov(0);
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 300f;
            // Before VRChat's camera, which draws the screen showing its picture in the same frame.
            camera.depth = -90f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.cullingMask = ~MirrorHiddenLayers;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.allowDynamicResolution = false;
            camera.useOcclusionCulling = true;
            camera.targetTexture = MirrorTexture(folder);
            camera.enabled = false;
            return mirror;
        }

        /// <summary>The mirror's render texture in <paramref name="folder"/> (made once): sRGB, no mipmaps, bilinear.</summary>
        internal static RenderTexture MirrorTexture(string folder)
        {
            string path = folder + "/N4N0 mirror.renderTexture";
            var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (texture != null) return texture;
            texture = new RenderTexture(MirrorWidth, MirrorHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "N4N0 mirror", antiAliasing = 1, useMipMap = false, autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        /// <summary>Gives a screen material the mirror's render texture of its folder (a shader without the property takes none).</summary>
        internal static void SetMirrorTexture(Material material, string folder)
        {
            if (material.HasProperty(MirrorTextureProperty)) material.SetTexture(MirrorTextureProperty, MirrorTexture(folder));
        }

        /// <summary>
        /// The mirror's layers: its camera renders every other frame on the wearer's client while <see cref="MirrorOn"/> (the
        /// screen's picture switched on with it), its field of view following <see cref="MirrorZoom"/>; the camera follows the
        /// owner's eyes, or stays frozen in the world while <see cref="MirrorPlaced"/>. Every state sets all that its layer
        /// animates, so any Write Defaults leave nothing behind.
        /// </summary>
        private static void MirrorController(AnimatorController controller, List<VRCExpressionParameters.Parameter> parameters)
        {
            controller.AddParameter(MirrorOn, AnimatorControllerParameterType.Bool);
            controller.AddParameter(MirrorPlaced, AnimatorControllerParameterType.Bool);
            controller.AddParameter(MirrorZoom, AnimatorControllerParameterType.Float);
            if (!controller.parameters.Any(p => p.name == "IsLocal")) controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);

            var cameraLayer = Layer(controller, "Mirror camera");
            AnimationClip Shot(string name, bool enabled, bool live, float fov)
            {
                var clip = Hidden(new AnimationClip { name = "Mirror " + name });
                Curve(clip, MirrorCamera, typeof(Camera), "m_Enabled", enabled ? 1 : 0);
                Curve(clip, MirrorCamera, typeof(Camera), "field of view", fov);
                Curve(clip, ScreenObject, typeof(SkinnedMeshRenderer), "material." + MirrorLiveProperty, live ? 1 : 0);
                return clip;
            }
            Motion Zoomed(string name, bool enabled)
            {
                var tree = Hidden(new BlendTree { name = "Mirror " + name, blendType = BlendTreeType.Simple1D, blendParameter = MirrorZoom, useAutomaticThresholds = false });
                foreach (float zoom in MirrorZoomSteps) tree.AddChild(Shot(name + " " + zoom.ToString("0.00"), enabled, true, MirrorFov(zoom)), zoom);
                return tree;
            }
            var off = HeldPropRig.State(controller, cameraLayer, "Off", Shot("off", false, false, MirrorFov(0)));
            // Rendering one frame, resting the next: half the frames, whatever the frame rate.
            var render = HeldPropRig.State(controller, cameraLayer, "Render", Zoomed("render", true));
            var rest = HeldPropRig.State(controller, cameraLayer, "Rest", Zoomed("rest", false));
            cameraLayer.defaultState = off;
            Transition(off, render, (MirrorOn, AnimatorConditionMode.If, 0), ("IsLocal", AnimatorConditionMode.If, 0));
            foreach (var on in new[] { render, rest })
            {
                Transition(on, off, (MirrorOn, AnimatorConditionMode.IfNot, 0));
                Transition(on, off, ("IsLocal", AnimatorConditionMode.IfNot, 0));
            }
            // A condition true in the state: the next update leaves (an exit time would wait for the clip's loop).
            Transition(render, rest, ("IsLocal", AnimatorConditionMode.If, 0));
            Transition(rest, render, ("IsLocal", AnimatorConditionMode.If, 0));

            var placeLayer = Layer(controller, "Mirror place");
            AnimatorState Place(string name, bool frozen)
            {
                var clip = new AnimationClip { name = "Mirror " + name.ToLowerInvariant() };
                Curve(clip, Mirror, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1 : 0);
                return HeldPropRig.State(controller, placeLayer, name, clip);
            }
            var following = Place("Following", false);
            var placed = Place("Placed", true);
            placeLayer.defaultState = following;
            Transition(following, placed, (MirrorPlaced, AnimatorConditionMode.If, 0));
            Transition(placed, following, (MirrorPlaced, AnimatorConditionMode.IfNot, 0));

            // Local, never saved, never synced: Orbiters - N4N0 sets them from N4N0's menu over OSC.
            parameters.Add(Parameter(MirrorOn, synced: false));
            parameters.Add(Parameter(MirrorPlaced, synced: false));
            parameters.Add(Parameter(MirrorZoom, synced: false, type: VRCExpressionParameters.ValueType.Float));
        }

        /// <summary>
        /// Points each N4N0's mirror camera under <paramref name="avatarRoot"/> (a build copy) at the render texture its
        /// screen's material shows: N4N0's options may have given the screen the shared folder's material. A screen whose
        /// shader has no mirror leaves the camera out.
        /// </summary>
        internal static void BindMirrors(GameObject avatarRoot)
        {
            foreach (var orb in FindAll(avatarRoot).ToList())
            {
                if (PrefabUtility.IsPartOfAnyPrefab(orb)) continue;
                var camera = orb.Find(MirrorCamera)?.GetComponent<Camera>();
                if (camera == null) continue;
                var material = ScreenRenderer(orb)?.sharedMaterial;
                var texture = material != null && material.HasProperty(MirrorTextureProperty) ? material.GetTexture(MirrorTextureProperty) as RenderTexture : null;
                if (texture != null) camera.targetTexture = texture;
                else Object.DestroyImmediate(orb.Find(Mirror).gameObject);
            }
        }
    }

    /// <summary>Points the mirror cameras of the avatar being built at their screens' pictures (<see cref="OrbInstaller.BindMirrors"/>).</summary>
    internal sealed class OrbMirrorHook : IVRCSDKPreprocessAvatarCallback
    {
        // After the platform's N4N0 is kept (-10200; phones lose the camera with the PC N4N0) and the capture kits (-10150),
        // before VRCFury (-10000).
        public int callbackOrder => -10140;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            OrbInstaller.BindMirrors(avatarRoot);
            return true;
        }
    }
}
