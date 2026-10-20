using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// A see-through preview of an avatar's original base, placed over the avatar's body, to line up clothing made for the
    /// original before refitting it. Never saved, not in the hierarchy; removed on Dispose and before scripts reload. The
    /// original is not instantiated: its objects and renderers are made again hidden from the start, so the open scene is
    /// never marked changed and none of the original's scripts runs.
    /// </summary>
    public sealed class RefitGhost : IDisposable
    {
        private static readonly Color Tint = new Color(0.25f, 0.62f, 1f, 0.3f);
        private const HideFlags Hidden = HideFlags.HideAndDontSave;
        private GameObject root;
        private Material material;

        /// <summary>The original base's hips, to line clothing up with: its humanoid avatar's, or the bone named so; null without.</summary>
        public Transform Hips { get; private set; }

        /// <summary>The original base shown, to fit clothing onto its armature; null once disposed.</summary>
        public Transform Root => root != null ? root.transform : null;

        public bool Visible => root != null;

        /// <summary>Shows <paramref name="original"/> (a model or prefab) so that its body lies where <paramref name="avatarBody"/> is.</summary>
        public static RefitGhost Show(GameObject original, SkinnedMeshRenderer originalBody, SkinnedMeshRenderer avatarBody)
        {
            if (original == null || originalBody == null || avatarBody == null) throw new ArgumentNullException(nameof(original));
            var ghost = new RefitGhost();
            try
            {
                var made = new Dictionary<Transform, Transform>();
                ghost.root = Rebuild(original.transform, null, made);
                ghost.root.name = original.name + " (original base)";

                // The copy of the original body: the one made from it, else found by its path in the original.
                Transform body = null;
                if (!made.TryGetValue(originalBody.transform, out body))
                {
                    string bodyPath = originalBody.transform.IsChildOf(original.transform)
                        ? AnimationUtility.CalculateTransformPath(originalBody.transform, original.transform) : null;
                    body = bodyPath == null ? null : bodyPath.Length == 0 ? ghost.root.transform : ghost.root.transform.Find(bodyPath);
                }
                var renderers = CopyRenderers(original.transform, made);
                var bodyRenderer = body != null ? body.GetComponent<SkinnedMeshRenderer>() : null;
                // Its humanoid avatar, given for a moment to the copy (an original imported Generic still names its hips).
                var sourceAnimator = original.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isValid && a.avatar.isHuman);
                var bones = bodyRenderer != null ? bodyRenderer.bones : ghost.root.GetComponentsInChildren<Transform>(true);
                if (sourceAnimator != null && made.TryGetValue(sourceAnimator.transform, out var animated))
                {
                    var animator = animated.gameObject.AddComponent<Animator>();
                    animator.avatar = sourceAnimator.avatar;
                    animator.Rebind();
                    ghost.Hips = AvatarBoneIndex.Build(ghost.root.transform, bones, animator).Humanoid(HumanBodyBones.Hips);
                    // Only renderers stay.
                    Object.DestroyImmediate(animator);
                }
                if (ghost.Hips == null) ghost.Hips = AvatarBoneIndex.Build(ghost.root.transform, bones).Humanoid(HumanBodyBones.Hips);

                // Its body where the avatar's body is: same world matrix.
                if (body != null)
                {
                    ghost.root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    ghost.root.transform.localScale = Vector3.one;
                    var target = avatarBody.transform.localToWorldMatrix * body.localToWorldMatrix.inverse;
                    ghost.root.transform.SetPositionAndRotation(target.GetColumn(3), target.rotation);
                    ghost.root.transform.localScale = target.lossyScale;
                }

                ghost.material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = Hidden, name = "Original base preview" };
                ghost.material.SetColor("_Color", Tint);
                ghost.material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                ghost.material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                ghost.material.SetInt("_Cull", (int)CullMode.Off);
                ghost.material.SetInt("_ZWrite", 0);
                ghost.material.renderQueue = (int)RenderQueue.Transparent;
                foreach (var (renderer, slots) in renderers)
                {
                    renderer.sharedMaterials = Enumerable.Repeat(ghost.material, Mathf.Max(1, slots)).ToArray();
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }
            catch
            {
                ghost.Dispose();
                throw;
            }
            AssemblyReloadEvents.beforeAssemblyReload += ghost.Dispose;
            EditorApplication.playModeStateChanged += ghost.PlayModeChanged;
            SceneView.RepaintAll();
            return ghost;
        }

        // The objects of the original, made again hidden from their creation (their names, places and active states).
        private static GameObject Rebuild(Transform source, Transform parent, Dictionary<Transform, Transform> made)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(source.name, Hidden);
            var t = go.transform;
            if (parent != null) t.SetParent(parent, false);
            t.localPosition = source.localPosition;
            t.localRotation = source.localRotation;
            t.localScale = source.localScale;
            made[source] = t;
            foreach (Transform child in source) Rebuild(child, t, made);
            go.SetActive(source.gameObject.activeSelf);
            return go;
        }

        // The original's mesh renderers on the objects made from theirs, skinned to the bones made from theirs, with as many
        // material slots each.
        private static List<(Renderer renderer, int slots)> CopyRenderers(Transform source, Dictionary<Transform, Transform> made)
        {
            var copies = new List<(Renderer, int)>();
            Transform Made(Transform t) => t != null && made.TryGetValue(t, out var copy) ? copy : null;
            foreach (var skinned in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var at = Made(skinned.transform);
                if (at == null) continue;
                var copy = at.gameObject.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = skinned.sharedMesh;
                copy.bones = (skinned.bones ?? Array.Empty<Transform>()).Select(Made).ToArray();
                copy.rootBone = Made(skinned.rootBone);
                copy.updateWhenOffscreen = true;
                copy.enabled = skinned.enabled;
                var mesh = skinned.sharedMesh;
                if (mesh != null)
                    for (int i = 0; i < mesh.blendShapeCount; i++) copy.SetBlendShapeWeight(i, skinned.GetBlendShapeWeight(i));
                copies.Add((copy, skinned.sharedMaterials.Length));
            }
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                var meshRenderer = filter.GetComponent<MeshRenderer>();
                var at = Made(filter.transform);
                if (meshRenderer == null || at == null) continue;
                at.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var copy = at.gameObject.AddComponent<MeshRenderer>();
                copy.enabled = meshRenderer.enabled;
                copies.Add((copy, meshRenderer.sharedMaterials.Length));
            }
            return copies;
        }

        private void PlayModeChanged(PlayModeStateChange change) => Dispose();

        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            if (root != null) Object.DestroyImmediate(root);
            if (material != null) Object.DestroyImmediate(material);
            root = null;
            material = null;
            SceneView.RepaintAll();
        }
    }
}
