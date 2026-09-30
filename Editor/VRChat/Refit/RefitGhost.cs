using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// A see-through preview of an avatar's original base, placed over the avatar's body, to line up clothing made for the
    /// original before refitting it. Never saved, not in the hierarchy; removed on Dispose and before scripts reload.
    /// </summary>
    public sealed class RefitGhost : IDisposable
    {
        private static readonly Color Tint = new Color(0.25f, 0.62f, 1f, 0.3f);
        private GameObject root;
        private Material material;

        /// <summary>The original base's hips, to line clothing up with; null for a rig without a humanoid avatar.</summary>
        public Transform Hips { get; private set; }

        public bool Visible => root != null;

        /// <summary>Shows <paramref name="original"/> (a model or prefab) so that its body lies where <paramref name="avatarBody"/> is.</summary>
        public static RefitGhost Show(GameObject original, SkinnedMeshRenderer originalBody, SkinnedMeshRenderer avatarBody)
        {
            if (original == null || originalBody == null || avatarBody == null) throw new ArgumentNullException(nameof(original));
            var ghost = new RefitGhost();
            ghost.root = Object.Instantiate(original);
            ghost.root.name = original.name + " (original base)";
            foreach (var t in ghost.root.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.HideAndDontSave;

            // The copy of the original body, found by its path in the original.
            string bodyPath = AnimationUtility.CalculateTransformPath(originalBody.transform, original.transform);
            var body = bodyPath.Length == 0 ? ghost.root.transform : ghost.root.transform.Find(bodyPath);
            var animator = ghost.root.GetComponentInChildren<Animator>(true);
            ghost.Hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;

            // Only renderers stay: nothing of the original runs (animators, physics, scripts).
            foreach (var component in ghost.root.GetComponentsInChildren<Component>(true).Reverse())
                if (!(component is Transform) && !(component is Renderer) && !(component is MeshFilter)) Object.DestroyImmediate(component);

            // Its body where the avatar's body is: same world matrix.
            if (body != null)
            {
                ghost.root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                ghost.root.transform.localScale = Vector3.one;
                var target = avatarBody.transform.localToWorldMatrix * body.localToWorldMatrix.inverse;
                ghost.root.transform.SetPositionAndRotation(target.GetColumn(3), target.rotation);
                ghost.root.transform.localScale = target.lossyScale;
            }

            ghost.material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave, name = "Original base preview" };
            ghost.material.SetColor("_Color", Tint);
            ghost.material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            ghost.material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            ghost.material.SetInt("_Cull", (int)CullMode.Off);
            ghost.material.SetInt("_ZWrite", 0);
            ghost.material.renderQueue = (int)RenderQueue.Transparent;
            foreach (var renderer in ghost.root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterials = Enumerable.Repeat(ghost.material, Mathf.Max(1, renderer.sharedMaterials.Length)).ToArray();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            AssemblyReloadEvents.beforeAssemblyReload += ghost.Dispose;
            EditorApplication.playModeStateChanged += ghost.PlayModeChanged;
            SceneView.RepaintAll();
            return ghost;
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
