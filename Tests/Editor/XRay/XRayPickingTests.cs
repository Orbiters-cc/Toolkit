using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    public static class XRayPickingTests
    {
        public static void RunOrThrow()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("XRay picking fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var camera = root.AddComponent<Camera>();
                camera.nearClipPlane = 0.1f;
                Vector3 head = new Vector3(0.25f, 0f, 1f), tail = new Vector3(0.25f, 0f, -1f);
                Vector2 a, b;
                using (new Handles.DrawingScope(Matrix4x4.identity))
                {
                    Vector2 oldHead = HandleUtility.WorldToGUIPointWithDepth(camera, head);
                    Vector2 oldTail = HandleUtility.WorldToGUIPointWithDepth(camera, tail);
                    Vector2 falseHit = (oldHead + oldTail) * 0.5f;
                    Check(XRayBonePickingService.DistanceToSegment(falseHit, oldHead, oldTail) < 0.001f, "False-hit fixture did not hit the old projection.");
                    Check(XRayBonePickingService.TryProjectSegment(camera, head, tail, out a, out b), "Crossing bone lost its visible portion.");
                    Check(XRayBonePickingService.DistanceToSegment(falseHit, a, b) > 12f, "Behind-camera endpoint still creates a false click target.");
                    Check(Vector2.Distance(b, HandleUtility.WorldToGUIPointWithDepth(camera, new Vector3(0.25f, 0f, 0.1f))) < 0.01f, "Tail was not clipped to the near plane.");
                    Check(XRayBonePickingService.TryProjectSegment(camera, tail, head, out a, out b), "Reversed crossing segment was rejected.");
                    Check(Vector2.Distance(a, HandleUtility.WorldToGUIPointWithDepth(camera, new Vector3(0.25f, 0f, 0.1f))) < 0.01f, "Head was not clipped to the near plane.");
                    Check(!XRayBonePickingService.TryProjectSegment(camera, tail, tail + Vector3.up, out a, out b), "Hidden bone was pickable.");
                    Check(!XRayBonePickingService.TryProjectSegment(camera, new Vector3(float.NaN, 0, 1), head, out a, out b), "Non-finite bone was pickable.");
                    Check(!XRayBonePickingService.TryProjectSegment(camera, head, head, out a, out b), "Zero-length bone was pickable.");
                    camera.orthographic = true;
                    Check(XRayBonePickingService.TryProjectSegment(camera, head, tail + Vector3.up, out a, out b), "Orthographic crossing bone was rejected.");
                }
                CheckHandleOwnership();
                Debug.Log("[XRay Picking Tests] PASS: camera clipping, reversed/hidden/invalid segments, orthographic projection, native handle arbitration, navigation and active drags.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void CheckHandleOwnership()
        {
            // Use Unity's actual AddControl implementation and restore its state before returning to the editor.
            const int boneControl = 913001, transformControl = 913002;
            var distanceField = typeof(HandleUtility).GetField("s_NearestDistance", BindingFlags.Static | BindingFlags.NonPublic);
            float savedDistance = (float)distanceField.GetValue(null);
            int savedNearest = HandleUtility.nearestControl, savedHot = GUIUtility.hotControl;
            var savedTool = Tools.current;
            var click = new Event { type = EventType.MouseDown, button = 0 };
            try
            {
                Tools.current = Tool.Move;
                GUIUtility.hotControl = 0;
                distanceField.SetValue(null, 5f);
                HandleUtility.nearestControl = 0;
                HandleUtility.AddControl(boneControl, 4f);
                HandleUtility.AddControl(transformControl, 1f);
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone stole a closer transform handle's click.");
                distanceField.SetValue(null, 5f);
                HandleUtility.nearestControl = 0;
                HandleUtility.AddControl(boneControl, 1f);
                HandleUtility.AddDefaultControl(transformControl);
                Check(XRayBonePickingService.CanPickBone(click, boneControl), "Visible bone did not win against empty-space selection.");
                GUIUtility.hotControl = transformControl;
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone stole an active drag.");
                GUIUtility.hotControl = 0;
                click.alt = true;
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone stole Alt navigation.");
                click.alt = false;
                Tools.current = Tool.View;
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone stole the View tool.");
                Tools.current = Tool.Move;
                click.button = 1;
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone stole right-button navigation.");
                click.button = 0; click.type = EventType.MouseDrag;
                Check(!XRayBonePickingService.CanPickBone(click, boneControl), "Bone selected during a drag.");
            }
            finally
            {
                Tools.current = savedTool;
                GUIUtility.hotControl = savedHot;
                HandleUtility.nearestControl = savedNearest;
                distanceField.SetValue(null, savedDistance);
            }
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
