using Orbiters.Toolkit.Editor.XRay;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// The mesh edge overlay is a command buffer on the Scene view cameras that draws the selected renderers again: it
    /// adds no object or mesh, never reaches another camera, and follows a sculpt, a pose or a blendshape without being
    /// recorded again (only a renderer swapping its mesh, going away or hiding re-records it).
    /// </summary>
    public static class XRayMeshEdgeTests
    {
        public static void DrawsTheRendererWithoutCopiesOrThrow()
        {
            using (var fixture = new Fixture())
            {
                int objectsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                int childrenBefore = fixture.Root.GetComponentsInChildren<Transform>(true).Length;
                XRayMeshEdgeService.SetEnabled(true);
                XRayMeshEdgeService.Refresh();
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                XRayMeshEdgeService.BeforeCameraRender(fixture.GameCamera);

                Check(XRayMeshEdgeService.ActiveRenderers.Contains(fixture.Source), "The selected renderer is not among the edge overlay's renderers.");
                Check(fixture.Root.GetComponentsInChildren<Transform>(true).Length == childrenBefore, "Showing edges added an object under the avatar.");
                Check(Resources.FindObjectsOfTypeAll<GameObject>().Length == objectsBefore, "Showing edges created a GameObject.");
                Check(Resources.FindObjectsOfTypeAll<Mesh>().Length == meshesBefore, "Showing edges created a mesh.");
                if (!XRayMeshEdgeService.UsesGeometryShader)
                {
                    Debug.Log("[XRay Mesh Edge Tests] This GPU has no geometry shaders: the edges use the wireframe fallback, the command buffer checks are skipped.");
                    return;
                }

                var drawn = XRayMeshEdgeService.DrawnRenderers.Where(d => d.Renderer == fixture.Source).ToList();
                Check(drawn.Count == 1 && drawn[0].Mesh == fixture.Mesh && drawn[0].Shown, "The command buffer does not draw the selected renderer's mesh.");
                Check(XRayMeshEdgeService.Buffer != null && XRayMeshEdgeService.Buffer.sizeInBytes > 0, "The command buffer is empty.");
                Check(CountOurs(fixture.SceneCamera) == 1, "A Scene view camera does not run the edge buffer exactly once.");
                Check(CountOurs(fixture.GameCamera) == 0, "A Game camera runs the edge buffer.");
                foreach (SceneView view in SceneView.sceneViews)
                    if (view != null && view.camera != null)
                        Check(CountOurs(view.camera) == 1, "Scene view '" + view.titleContent.text + "' does not run the edge buffer exactly once.");

                XRayMeshEdgeService.SetEnabled(false);
                Check(CountOurs(fixture.SceneCamera) == 0, "Hiding the edges left the buffer on a Scene view camera.");
                Check(XRayMeshEdgeService.ActiveRenderers.Count == 0 && XRayMeshEdgeService.DrawnRenderers.Count == 0, "Hiding the edges kept renderers.");
            }
        }

        public static void FollowsEditsWithoutRecordingOrThrow()
        {
            if (!XRayMeshEdgeService.UsesGeometryShader)
            {
                Debug.Log("[XRay Mesh Edge Tests] This GPU has no geometry shaders: the edges use the wireframe fallback, nothing to record.");
                return;
            }

            using (var fixture = new Fixture())
            {
                XRayMeshEdgeService.SetEnabled(true);
                XRayMeshEdgeService.Refresh();
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                int recordings = XRayMeshEdgeService.BufferRecordings;

                // A sculpt moves the vertices in place, a pose moves a bone, a shape key changes its weight: the
                // buffer draws the renderer as it is, so none of them records it again.
                var vertices = fixture.Mesh.vertices;
                vertices[1] += new Vector3(0f, 0.5f, 0.25f);
                fixture.Mesh.vertices = vertices;
                fixture.BoneA.localRotation = Quaternion.Euler(0f, 30f, 0f);
                fixture.Source.SetBlendShapeWeight(0, 60f);
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(XRayMeshEdgeService.BufferRecordings == recordings, "A mesh edit, a pose or a blendshape re-recorded the edge buffer.");

                // Mixer shows its working copy in the renderer's place: the buffer follows the new mesh.
                fixture.Source.sharedMesh = fixture.WorkingCopy;
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(XRayMeshEdgeService.BufferRecordings == recordings + 1, "Swapping the renderer's mesh did not re-record the edge buffer.");
                var drawn = XRayMeshEdgeService.DrawnRenderers.Single(d => d.Renderer == fixture.Source);
                Check(drawn.Mesh == fixture.WorkingCopy, "The edge buffer still names the replaced mesh.");
                Check(CountOurs(fixture.SceneCamera) == 1, "Re-recording attached the edge buffer twice.");

                // Another tool clears the camera's buffers: the edges come back at the next render.
                fixture.SceneCamera.RemoveAllCommandBuffers();
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(CountOurs(fixture.SceneCamera) == 1, "The edge buffer was not added again after the camera's buffers were cleared.");

                // A hidden renderer is not skinned by Unity: its edges go away until it shows again.
                fixture.Source.enabled = false;
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(!XRayMeshEdgeService.DrawnRenderers.Single(d => d.Renderer == fixture.Source).Shown, "A disabled renderer still has edges drawn.");
                fixture.Source.enabled = true;
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(XRayMeshEdgeService.DrawnRenderers.Single(d => d.Renderer == fixture.Source).Shown, "A renderer enabled again has no edges.");

                // A deleted renderer leaves the buffer before the next render.
                Object.DestroyImmediate(fixture.Source);
                XRayMeshEdgeService.BeforeCameraRender(fixture.SceneCamera);
                Check(XRayMeshEdgeService.DrawnRenderers.Count == 0 && XRayMeshEdgeService.ActiveRenderers.Count == 0, "A deleted renderer is still drawn.");
            }
        }

        private static int CountOurs(Camera camera)
        {
            return camera.GetCommandBuffers(XRayMeshEdgeService.BufferEvent).Count(b => b != null && b.name == XRayMeshEdgeService.BufferName);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>A two-bone skinned triangle with a shape key, selected, and two cameras (a Scene view stand-in and a Game camera).</summary>
        private sealed class Fixture : IDisposable
        {
            public readonly GameObject Root;
            public readonly Transform BoneA;
            public readonly SkinnedMeshRenderer Source;
            public readonly Mesh Mesh, WorkingCopy;
            public readonly Camera SceneCamera, GameCamera;
            private readonly UnityEngine.SceneManagement.Scene scene;
            private readonly Object[] selection;
            private readonly bool enabled;

            public Fixture()
            {
                selection = Selection.objects;
                enabled = XRayMeshEdgeService.Enabled;
                XRayMeshEdgeService.SetEnabled(false);
                scene = EditorSceneManager.NewPreviewScene();
                Root = new GameObject("Edge overlay fixture");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Root, scene);
                BoneA = new GameObject("A").transform;
                BoneA.SetParent(Root.transform, false);
                var boneB = new GameObject("B").transform;
                boneB.SetParent(Root.transform, false);
                boneB.localPosition = Vector3.right * 2f;
                Mesh = Triangle();
                WorkingCopy = Object.Instantiate(Mesh);
                Source = Root.AddComponent<SkinnedMeshRenderer>();
                Source.sharedMesh = Mesh;
                Source.bones = new[] { BoneA, boneB };
                Source.rootBone = BoneA;
                SceneCamera = NewCamera("Scene view camera stand-in", CameraType.SceneView);
                GameCamera = NewCamera("Game camera", CameraType.Game);
                Selection.activeGameObject = Root;
            }

            private Camera NewCamera(string name, CameraType type)
            {
                var go = new GameObject(name);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                var camera = go.AddComponent<Camera>();
                camera.enabled = false;
                camera.cameraType = type;
                return camera;
            }

            private static Mesh Triangle()
            {
                var mesh = new Mesh { name = "Edge overlay triangle" };
                mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 3).ToArray();
                mesh.AddBlendShapeFrame("Lift", 100f, new[] { Vector3.zero, Vector3.forward, Vector3.zero }, null, null);
                mesh.RecalculateBounds();
                return mesh;
            }

            public void Dispose()
            {
                XRayMeshEdgeService.SetEnabled(false);
                Selection.objects = selection;
                if (SceneCamera != null)
                    foreach (var buffer in SceneCamera.GetCommandBuffers(XRayMeshEdgeService.BufferEvent))
                        SceneCamera.RemoveCommandBuffer(XRayMeshEdgeService.BufferEvent, buffer);
                Object.DestroyImmediate(Root);
                Object.DestroyImmediate(Mesh);
                Object.DestroyImmediate(WorkingCopy);
                EditorSceneManager.ClosePreviewScene(scene);
                XRayMeshEdgeService.SetEnabled(enabled);
            }
        }
    }
}
