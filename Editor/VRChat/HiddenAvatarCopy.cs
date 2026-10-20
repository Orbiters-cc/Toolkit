using System;
using System.Linq;
using Orbiters.Toolkit.Editor.XRay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// A copy of an avatar as it is in the scene, in a preview scene of its own: out of the hierarchy, never saved, the open
    /// scene left unchanged, and never listed by the SDK's upload panel. The avatar gizmos' helpers (bones, weight paint) are
    /// not part of it: never baked, exported or built. Disposing closes it.
    /// </summary>
    public sealed class HiddenAvatarCopy : IDisposable
    {
        public GameObject Copy { get; private set; }
        public Scene Scene { get; private set; }

        public HiddenAvatarCopy(GameObject source)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                // Made hidden and moved at once: an object made in the open scene would mark it changed.
                var holder = EditorUtility.CreateGameObjectWithHideFlags("Hidden copy", HideFlags.HideAndDontSave);
                holder.SetActive(false);
                SceneManager.MoveGameObjectToScene(holder, Scene);
                var copy = Object.Instantiate(source, holder.transform);
                copy.name = source.name;
                copy.transform.SetParent(null, false);
                // A copy of an object never saved (a tool's own hidden copy) lands in the open scene when unparented.
                if (copy.scene != Scene) SceneManager.MoveGameObjectToScene(copy, Scene);
                Object.DestroyImmediate(holder);
                // Their meshes stay the source's gizmos': only the copied objects go.
                foreach (var helper in copy.GetComponentsInChildren<Transform>(true).Where(t => t != copy.transform && XRayArmatureMeshGenerator.IsPackageGizmoObject(t.gameObject)).ToList())
                    if (helper != null) Object.DestroyImmediate(helper.gameObject);
                // The SDK panel lists every avatar whose object isn't flagged so: an upload must never pick the copy.
                copy.hideFlags = HideFlags.NotEditable;
                copy.SetActive(true);
                Copy = copy;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Copy != null && Copy.scene != Scene) Object.DestroyImmediate(Copy);
            Copy = null;
            if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene);
            Scene = default;
        }
    }
}
