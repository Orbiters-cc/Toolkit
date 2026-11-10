using NUnit.Framework;
using Orbiters.Toolkit.Editor.XRay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// The bone gizmo's change check (polled ten times a second while it shows) answers as a state taken from scratch
    /// would: unchanged after a pose, changed after a child is added, removed, reordered, reparented or moved from its bone.
    /// </summary>
    public sealed class XRayGeometryStateTests
    {
        [Test]
        public void MatchesOnlyWhatAFreshStateWouldMatch()
        {
            var root = new GameObject("Geometry state fixture");
            var mesh = new Mesh();
            try
            {
                var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
                var spine = new GameObject("Spine").transform; spine.SetParent(hips, false); spine.localPosition = Vector3.up;
                var leg = new GameObject("Leg").transform; leg.SetParent(hips, false); leg.localPosition = Vector3.down;
                var helper = new GameObject("Helper").transform; helper.SetParent(hips, false); helper.localPosition = Vector3.right;
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.bones = new[] { hips, spine, leg };
                renderer.rootBone = hips;

                var state = new XRayArmatureGeometryState(renderer);
                Assert.That(state.Matches(renderer), Is.True, "nothing changed");

                hips.localRotation = Quaternion.Euler(10, 20, 30);
                root.transform.position = new Vector3(1, 2, 3);
                Assert.That(state.Matches(renderer), Is.True, "a pose is skinned, not rebuilt");

                leg.SetSiblingIndex(0);
                Assert.That(state.Matches(renderer), Is.False, "children reordered");
                state = new XRayArmatureGeometryState(renderer);
                Assert.That(state.Matches(renderer), Is.True);

                helper.localPosition = Vector3.left;
                Assert.That(state.Matches(renderer), Is.False, "a helper moved from its bone");
                state = new XRayArmatureGeometryState(renderer);

                var extra = new GameObject("Extra").transform; extra.SetParent(spine, false);
                Assert.That(state.Matches(renderer), Is.False, "a child added");
                state = new XRayArmatureGeometryState(renderer);

                extra.SetParent(leg, false);
                Assert.That(state.Matches(renderer), Is.False, "a child moved to another bone");
                state = new XRayArmatureGeometryState(renderer);

                Object.DestroyImmediate(extra.gameObject);
                Assert.That(state.Matches(renderer), Is.False, "a child removed");
                state = new XRayArmatureGeometryState(renderer);

                renderer.bones = new[] { hips, spine };
                Assert.That(state.Matches(renderer), Is.False, "another bone list");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
