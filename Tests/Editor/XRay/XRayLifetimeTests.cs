using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.XRay;
using Orbiters.Toolkit.XRay;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// The gizmos' hidden meshes go with them: when a gizmo is rebuilt, and when its avatar or renderer is destroyed with
    /// the gizmo inside (a hidden mesh nothing unloads would stay otherwise). Pinning the root bone paints that bone's own
    /// weights, not every bone below it; a mesh swapped under the overlay is followed. Fixtures live in a preview scene.
    /// </summary>
    public sealed class XRayLifetimeTests
    {
        private sealed class Fixture : IDisposable
        {
            public readonly UnityEngine.SceneManagement.Scene Scene = EditorSceneManager.NewPreviewScene();
            public readonly GameObject Root;
            public readonly Transform Hips, Spine;
            public readonly SkinnedMeshRenderer Renderer;
            public readonly Mesh Mesh;

            public Fixture()
            {
                Root = new GameObject("XRay lifetime fixture");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Root, Scene);
                Hips = new GameObject("Hips").transform; Hips.SetParent(Root.transform, false);
                Spine = new GameObject("Spine").transform; Spine.SetParent(Hips, false); Spine.localPosition = Vector3.up * 0.3f;
                var body = new GameObject("Body");
                body.transform.SetParent(Root.transform, false);
                Renderer = body.AddComponent<SkinnedMeshRenderer>();
                Mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up * 0.3f, Vector3.right * 0.1f }, triangles = new[] { 0, 1, 2 } };
                Mesh.boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 0.5f, boneIndex1 = 1, weight1 = 0.5f },
                };
                Mesh.bindposes = new[] { Hips.worldToLocalMatrix, Spine.worldToLocalMatrix };
                Renderer.sharedMesh = Mesh;
                Renderer.bones = new[] { Hips, Spine };
                Renderer.rootBone = Hips;
            }

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                if (Mesh != null) Object.DestroyImmediate(Mesh);
                EditorSceneManager.ClosePreviewScene(Scene);
            }
        }

        private static Mesh GizmoMesh(GameObject root) => root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.gameObject.name.StartsWith(XRayArmatureMeshGenerator.MeshObjectNamePrefix, StringComparison.Ordinal))
            .Select(r => r.sharedMesh).FirstOrDefault();

        private static Mesh OverlayMesh(SkinnedMeshRenderer renderer) => renderer.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r != renderer && r.gameObject.name.StartsWith(XRayArmatureMeshGenerator.GizmoObjectNamePrefix + "WeightPaint", StringComparison.Ordinal))
            .Select(r => r.sharedMesh).FirstOrDefault();

        [Test]
        public void BoneGizmoMeshesGoWithTheirGizmo()
        {
            bool enabled = XRayGizmoService.Enabled;
            var mode = XRayGizmoService.TargetMode;
            var pinned = XRayGizmoService.PinnedObject;
            var fixture = new Fixture();
            try
            {
                XRayGizmoService.SetEnabled(false);
                XRayGizmoService.SetPinnedObject(fixture.Root);
                XRayGizmoService.SetTargetMode(XRayGizmoTargetMode.PinnedObject);
                XRayGizmoService.SetEnabled(true);
                var first = GizmoMesh(fixture.Root);
                Assert.That(first != null, Is.True, "the gizmo was made");

                XRayGizmoService.RebuildAll();
                var second = GizmoMesh(fixture.Root);
                Assert.That(second != null && second != first, Is.True, "a rebuild makes a new mesh");
                Assert.That(first == null, Is.True, "and destroys the one it replaces");

                // The avatar goes, its hidden gizmo object with it: the mesh must go too.
                Object.DestroyImmediate(fixture.Root);
                XRayGizmoService.Refresh();
                Assert.That(second == null, Is.True, "the mesh of a gizmo that went with its avatar is destroyed");
                Assert.That(XRayGizmoService.ActiveBoneSegments.Count, Is.EqualTo(0));
            }
            finally
            {
                XRayGizmoService.SetEnabled(false);
                fixture.Dispose();
                XRayGizmoService.SetPinnedObject(pinned);
                XRayGizmoService.SetTargetMode(mode);
                XRayGizmoService.SetEnabled(enabled);
            }
        }

        [Test]
        public void WeightPaintPinsTheRootBoneAloneAndItsMeshGoesWithTheRenderer()
        {
            var fixture = new Fixture();
            Mesh swapped = null;
            try
            {
                XRayWeightPaintService.Pin(fixture.Renderer, new[] { fixture.Hips });
                var overlay = OverlayMesh(fixture.Renderer);
                Assert.That(overlay != null, Is.True, "the pinned overlay was made");
                var colors = overlay.colors32;
                Assert.That(colors[0], Is.EqualTo(new Color32(255, 0, 0, 255)), "Hips' own vertex: full weight");
                Assert.That(colors[1], Is.EqualTo(new Color32(0, 0, 255, 255)), "Spine's vertex: no Hips weight (the root bone is not the whole armature)");
                Assert.That(colors[2], Is.EqualTo(new Color32(0, 255, 0, 255)), "half and half");

                // Another mesh under the renderer (Mixer's working copy): the overlay is made again from it.
                swapped = Object.Instantiate(fixture.Mesh);
                fixture.Renderer.sharedMesh = swapped;
                XRayWeightPaintService.Repaint(fixture.Renderer);
                var again = OverlayMesh(fixture.Renderer);
                Assert.That(again != null && again != overlay, Is.True, "a new overlay for the new mesh");
                Assert.That(overlay == null, Is.True, "the old overlay's mesh is destroyed");

                // The renderer goes with its overlay inside: the overlay's mesh goes, and so does the pin.
                Object.DestroyImmediate(fixture.Renderer.gameObject);
                XRayWeightPaintService.Refresh();
                Assert.That(again == null, Is.True, "the mesh of an overlay that went with its renderer is destroyed");
                Assert.That(XRayWeightPaintService.Pinned, Is.False, "a pin ends with its renderer");
            }
            finally
            {
                XRayWeightPaintService.Unpin();
                if (swapped != null) Object.DestroyImmediate(swapped);
                fixture.Dispose();
            }
        }
    }
}
