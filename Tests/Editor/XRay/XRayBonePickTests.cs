using Orbiters.Toolkit.Editor.XRay;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// A bone click of the armature overlay goes to a tool that takes it (Unity's selection stays, the tool's bone is
    /// reported as picked) and selects the bone as before when no tool takes it.
    /// </summary>
    public static class XRayBonePickTests
    {
        public static void RunOrThrow()
        {
            const string id = "orbiters.toolkit.tests.bone-picks";
            var selection = Selection.objects;
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Bone pick fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var bone = new GameObject("Taken bone").transform;
                bone.SetParent(root.transform, false);
                var other = new GameObject("Declined bone").transform;
                other.SetParent(root.transform, false);
                Transform taken = null;
                XRayBonePickHandlers.Register(id, b =>
                {
                    if (b != bone) return false;
                    taken = b;
                    return true;
                }, () => taken);

                Selection.activeGameObject = root;
                XRayBonePickingService.Pick(bone, false, false);
                Check(taken == bone, "The tool did not get the clicked bone.");
                Check(Selection.activeGameObject == root, "A bone click a tool took changed Unity's selection.");
                var picked = new HashSet<Transform>();
                XRayBonePickHandlers.CollectPicked(picked);
                Check(picked.Contains(bone), "The tool's picked bone is not reported for the highlight.");

                XRayBonePickingService.Pick(other, false, false);
                Check(Selection.activeGameObject == other.gameObject, "A bone click no tool took did not select the bone.");

                XRayBonePickHandlers.Unregister(id);
                Selection.activeGameObject = root;
                XRayBonePickingService.Pick(bone, false, false);
                Check(Selection.activeGameObject == bone.gameObject, "After the tool left, a bone click did not select the bone.");
                picked.Clear();
                XRayBonePickHandlers.CollectPicked(picked);
                Check(!picked.Contains(bone), "A tool that left still reports its picked bone.");
            }
            finally
            {
                XRayBonePickHandlers.Unregister(id);
                Selection.objects = selection;
                Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
