using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.XRay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// "Does this renderer use the selection" (the edge overlay's scene search when a bone or an armature is selected)
    /// answers as the former check did: a bone selected, or any ancestor of a bone selected (Transform.IsChildOf).
    /// </summary>
    public sealed class XRayEdgeTargetTests
    {
        [Test]
        public void RendererUsesSelectionAsIsChildOfSays()
        {
            var root = new GameObject("Edge target fixture");
            var mesh = new Mesh();
            try
            {
                var armature = new GameObject("Armature").transform; armature.SetParent(root.transform, false);
                var hips = new GameObject("Hips").transform; hips.SetParent(armature, false);
                var spine = new GameObject("Spine").transform; spine.SetParent(hips, false);
                var hand = new GameObject("Hand").transform; hand.SetParent(spine, false);
                var prop = new GameObject("Prop").transform; prop.SetParent(hand, false);
                var other = new GameObject("Other").transform; other.SetParent(root.transform, false);
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.bones = new[] { hips, null, spine };

                var all = new[] { root.transform, armature, hips, spine, hand, prop, other };
                // Every selection of up to two of the transforms (and none).
                var selections = new List<Transform[]> { new Transform[0] };
                selections.AddRange(all.Select(t => new[] { t }));
                for (int a = 0; a < all.Length; a++)
                    for (int b = a + 1; b < all.Length; b++)
                        selections.Add(new[] { all[a], all[b] });

                foreach (var selection in selections)
                {
                    var set = new HashSet<Transform>(selection);
                    bool expected = renderer.bones.Any(bone => bone != null && (set.Contains(bone) || set.Any(s => bone.IsChildOf(s))));
                    Assert.That(XRayMeshEdgeService.RendererUsesSelection(renderer, set), Is.EqualTo(expected),
                        "selection: " + string.Join(", ", selection.Select(t => t.name)));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
