using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using System;
using System.Linq;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    public static class XRayGeometryTests
    {
        public static void RunOrThrow()
        {
            bool enabled = XRayGizmoService.Enabled;
            var mode = XRayGizmoService.TargetMode;
            var pinned = XRayGizmoService.PinnedObject;
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("XRay geometry fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var bone = new GameObject("Bone").transform; bone.SetParent(root.transform, false);
                var leaf = new GameObject("Leaf").transform; leaf.SetParent(bone, false); leaf.localPosition = Vector3.up;
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh; renderer.bones = new[] { bone, leaf }; renderer.rootBone = bone;
                mesh.bindposes = new[] { bone.worldToLocalMatrix, leaf.worldToLocalMatrix };
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
                XRayGizmoService.SetEnabled(false);
                XRayGizmoService.SetPinnedObject(root);
                XRayGizmoService.SetTargetMode(XRayGizmoTargetMode.PinnedObject);
                XRayGizmoService.SetEnabled(true);
                var original = Gizmo(root);
                Check(original != null, "Fixture gizmo was not created.");

                var helper = new GameObject("NonDeformingTail").transform;
                helper.SetParent(leaf, false); helper.localPosition = Vector3.right * 0.12f;
                XRayGizmoService.Refresh();
                var added = Gizmo(root);
                Check(added != original, "Adding a helper reused stale geometry.");
                AssertEndpoint(root, leaf);
                var previousSegment = XRayGizmoService.ActiveBoneSegments.Single(s => s.Bone == leaf && s.IsLeaf);

                helper.localPosition = Vector3.forward * 0.17f;
                // Exercise the Scene-view check too: moving a Transform need not raise hierarchyChanged.
                typeof(XRayGizmoService).GetField("nextGeometryCheck", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 0d);
                typeof(XRayGizmoService).GetMethod("RefreshChangedGeometry", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { null });
                var moved = Gizmo(root);
                Check(moved != added, "Moving a helper reused stale geometry.");
                AssertEndpoint(root, leaf);
                var currentSegment = XRayGizmoService.ActiveBoneSegments.Single(s => s.Bone == leaf && s.IsLeaf);
                var same = typeof(XRayBonePickingService).GetMethod("SameSegment", BindingFlags.NonPublic | BindingFlags.Static);
                Check(!(bool)same.Invoke(null, new object[] { previousSegment, currentSegment }), "Hover ignored the changed endpoint.");

                root.transform.position = new Vector3(4, 2, -3);
                root.transform.rotation = Quaternion.Euler(20, 55, 10);
                bone.localRotation = Quaternion.Euler(0, 20, 0);
                XRayGizmoService.Refresh();
                Check(Gizmo(root) == moved, "Ordinary avatar/bone motion rebuilt geometry.");
                AssertEndpoint(root, leaf);

                Object.DestroyImmediate(helper.gameObject);
                XRayGizmoService.Refresh();
                Check(Gizmo(root) != moved, "Removing a helper reused stale geometry.");
                AssertEndpoint(root, leaf);
                Check(renderer.bones.Length == 2 && !renderer.bones.Any(b => b.name == "NonDeformingTail"), "Helper entered deforming bone list.");
                Debug.Log("[XRay Geometry Tests] PASS: helper add/move/remove, rendered and picked endpoints, hover identity, avatar motion without rebuild.");
            }
            finally
            {
                XRayGizmoService.SetEnabled(false);
                Object.DestroyImmediate(root); Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(scene);
                XRayGizmoService.SetPinnedObject(pinned);
                XRayGizmoService.SetTargetMode(mode);
                XRayGizmoService.SetEnabled(enabled);
            }
        }

        private static SkinnedMeshRenderer Gizmo(GameObject root) => root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(r => r.gameObject.name.StartsWith("__XRayGizmos_", StringComparison.Ordinal));

        private static void AssertEndpoint(GameObject root, Transform leaf)
        {
            var segment = XRayGizmoService.ActiveBoneSegments.Single(s => s.Bone == leaf && s.IsLeaf);
            var renderer = Gizmo(root);
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked);
                float distance = baked.vertices.Min(v => Vector3.Distance(renderer.transform.TransformPoint(v), segment.EndPosition));
                Check(distance < 0.0001f, "Rendered and picked leaf endpoints differ by " + distance);
            }
            finally { Object.DestroyImmediate(baked); }
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
