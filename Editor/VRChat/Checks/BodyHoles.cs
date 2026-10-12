using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// "Black holes": a body shape that shrinks the skin under clothing (Shrink_Chest, Hide_Arms...) left on at rest while
    /// the clothing can be toggled off, with no toggle setting it back. With the clothing off, the shrunk skin shows a hole.
    /// </summary>
    public static class BodyHoles
    {
        public static List<AvatarIssue> Find(GameObject root)
        {
            var issues = new List<AvatarIssue>();
            var body = AttachmentPlanner.Body(root.transform);
            if (body == null || body.sharedMesh == null) return issues;
            var usage = new AvatarUsage(root);
            var toggled = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != body && !(r is ParticleSystemRenderer) && usage.Toggled(r.transform)).Select(r => r.name).Distinct().ToList();
            if (toggled.Count == 0) return issues;
            var animated = usage.AnimatedAt(body.transform);
            var mesh = body.sharedMesh;
            string clothing = string.Join(", ", toggled.Take(3).Select(n => "“" + n + "”")) + (toggled.Count > 3 ? " or others" : "");
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string shape = mesh.GetBlendShapeName(i);
                float weight = body.GetBlendShapeWeight(i);
                if (weight <= 0f || !Shrinks(shape) || animated.Contains("blendShape." + shape)) continue;
                issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{shape}” stays at {weight:0} while clothing can come off",
                    $"It hides the body under clothing, and no toggle sets it back: turning off {clothing} shows a hole there. " +
                    $"Add “{shape}” to that clothing's toggle, at {weight:0} with it on and 0 with it off.", body.name));
            }
            return issues;
        }

        /// <summary>Whether a body shape hides skin for clothing: Shrink_*, Hide_* and the like (not an iris or a pupil).</summary>
        public static bool Shrinks(string shape)
        {
            string name = (shape ?? "").ToLowerInvariant();
            if (name.Contains("iris") || name.Contains("pupil")) return false;
            return name.Contains("shrink") || name.StartsWith("hide", StringComparison.Ordinal) || name.Contains("_hide") || name.Contains(" hide");
        }
    }
}
