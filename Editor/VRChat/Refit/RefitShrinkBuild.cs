using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// At build, the body shrink a refit recorded (<see cref="OrbitersRefit.bodyShrinkVertices"/>) becomes a blendshape of the
    /// body (on a copy of its mesh) that is on while the clothing shows: every animation turning the clothing (or an object
    /// above it) on or off sets it too, and it starts as the clothing starts. Clothing always worn keeps it on. Runs after
    /// VRCFury and Modular Avatar built their toggles. Never stops an upload: what cannot be done is reported.
    /// </summary>
    public static class RefitShrinkBuild
    {
        /// <summary>The shrink's blendshape name on the body, for a refitted renderer.</summary>
        public static string ShapeName(Renderer clothing) => "ReFit shrink (" + clothing.name + ")";

        public static int Apply(GameObject avatarRoot, List<string> warnings)
        {
            if (avatarRoot == null) return 0;
            var records = avatarRoot.GetComponentsInChildren<OrbitersRefit>(true).Where(r => r.Applied && r.HasBodyShrink).ToList();
            if (records.Count == 0) return 0;
            var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            var animatorRoot = descriptor != null ? descriptor.transform : avatarRoot.transform;
            var controllers = BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot);
            var copies = new Dictionary<SkinnedMeshRenderer, Mesh>();
            int applied = 0;
            foreach (var record in records)
            {
                var body = record.body;
                var clothing = record.GetComponent<SkinnedMeshRenderer>();
                if (body == null || body.sharedMesh == null || !body.transform.IsChildOf(avatarRoot.transform))
                {
                    warnings.Add($"the body {record.name}'s shrink was made for is not part of the avatar: no shrink.");
                    continue;
                }
                // The shrink was planned on this mesh, or on one the build copied it from (same vertices).
                if (record.bodyShrinkMesh != null && record.bodyShrinkMesh.vertexCount != body.sharedMesh.vertexCount)
                {
                    warnings.Add($"{record.name}'s body shrink was made for another body mesh ({record.bodyShrinkMesh.name}): left out.");
                    continue;
                }
                var mesh = RefitBuildMeshes.Own(body, copies, records.Count, record.name + "'s body shrink is", warnings);
                if (mesh == null) continue;
                string shape = ShapeName(record.GetComponent<Renderer>());
                if (mesh.GetBlendShapeIndex(shape) >= 0) continue;
                var deltas = new Vector3[mesh.vertexCount];
                for (int i = 0; i < record.bodyShrinkVertices.Count; i++)
                {
                    int v = record.bodyShrinkVertices[i];
                    if (v >= 0 && v < deltas.Length) deltas[v] = record.bodyShrinkOffsets[i];
                }
                mesh.AddBlendShapeFrame(shape, 100f, deltas, null, null);
                int index = mesh.GetBlendShapeIndex(shape);
                // On as the clothing starts, and along every toggle of it.
                bool shown = clothing != null && clothing.enabled && clothing.gameObject.activeInHierarchy;
                body.SetBlendShapeWeight(index, shown ? 100f : 0f);
                int curves = Follow(controllers, animatorRoot, body, shape, clothing != null ? clothing.transform : record.transform, avatarRoot.transform);
                applied++;
                Debug.Log($"[Orbiters] ReFit: {body.name} shrinks under {record.name} ({record.bodyShrinkVertices.Count} vertices, " +
                          (curves > 0 ? $"following {curves} toggle animations" : shown ? "always on" : "off: the clothing starts hidden and nothing shows it") + ").");
            }
            return applied;
        }

        // Every clip animating the clothing renderer's enabled state, or the active state of its object or one above it, also
        // sets the shrink: 100 where the clothing would show. Returns the clips changed.
        private static int Follow(List<UnityEditor.Animations.AnimatorController> controllers, Transform animatorRoot, SkinnedMeshRenderer body,
            string shape, Transform clothing, Transform avatarRoot)
        {
            string bodyPath = AnimationUtility.CalculateTransformPath(body.transform, animatorRoot);
            var watched = new HashSet<string>();
            for (var t = clothing; t != null && t != avatarRoot.parent; t = t.parent)
            {
                watched.Add(AnimationUtility.CalculateTransformPath(t, animatorRoot));
                if (t == animatorRoot) break;
            }
            string rendererPath = AnimationUtility.CalculateTransformPath(clothing, animatorRoot);
            var binding = EditorCurveBinding.FloatCurve(bodyPath, typeof(SkinnedMeshRenderer), "blendShape." + shape);
            int changed = 0;
            var seen = new HashSet<AnimationClip>();
            foreach (var clip in controllers.SelectMany(c => c.animationClips))
            {
                if (clip == null || !seen.Add(clip)) continue;
                AnimationCurve source = null;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    bool active = b.type == typeof(GameObject) && b.propertyName == "m_IsActive" && watched.Contains(b.path);
                    bool enabled = b.type == typeof(SkinnedMeshRenderer) && b.propertyName == "m_Enabled" && b.path == rendererPath;
                    if (!active && !enabled) continue;
                    source = AnimationUtility.GetEditorCurve(clip, b);
                    // The object closest to the clothing decides when several are animated.
                    if (b.path == rendererPath) break;
                }
                if (source == null) continue;
                var keys = source.keys.Select(k => new Keyframe(k.time, k.value > .5f ? 100f : 0f) { inTangent = 0f, outTangent = 0f }).ToArray();
                var curve = new AnimationCurve(keys);
                for (int k = 0; k < keys.Length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Constant);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Constant);
                }
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                changed++;
            }
            return changed;
        }

        /// <summary>Whether any applied refit under the avatar has a body shrink (the build then needs private controllers).</summary>
        public static bool Any(GameObject avatarRoot) =>
            avatarRoot != null && avatarRoot.GetComponentsInChildren<OrbitersRefit>(true).Any(r => r.Applied && r.HasBodyShrink);
    }

    internal sealed class RefitShrinkHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000), Modular Avatar and the refit links (-8960): the toggles exist; before Follow Body
        // Blendshapes (-8950) and mesh optimizers.
        public int callbackOrder => -8956;

        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            var warnings = new List<string>();
            try { RefitShrinkBuild.Apply(avatarRoot, warnings); }
            catch (Exception ex) { warnings.Add(ex.Message); }
            foreach (var warning in warnings.Distinct()) Debug.LogWarning("[Orbiters] ReFit: " + warning, avatarRoot);
            return true;
        }
    }
}
