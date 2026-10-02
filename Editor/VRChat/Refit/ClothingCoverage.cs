using System;
using System.IO;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>Coverage is only requested for body clothing with evidence of different rig dimensions.</summary>
    public static class ClothingCoverage
    {
        private static readonly Regex Clothing = new Regex(@"\b(pants|hoodie|shirt|underwear)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool Eligible(OrbitersAttachment attachment, bool madeForDifferentBase = false)
        {
            if (attachment == null) return false;
            // Use original asset/object names, not an AI display label or unrelated parent folder.
            string name = attachment.name + " " + Path.GetFileNameWithoutExtension(attachment.variant ?? "");
            if (!Clothing.IsMatch(name.Replace('_', ' ').Replace('-', ' '))) return false;
            if (madeForDifferentBase) return true;
            foreach (var pose in attachment.fitted)
            {
                if (pose.transform == null) continue;
                // Recorded resize/proportion changes are evidence; placement and a T/A-pose rotation alone aren't.
                for (int axis = 0; axis < 3; axis++)
                    if (Different(pose.before.scale[axis], pose.after.scale[axis])) return true;
                if (pose.transform != attachment.transform && Different(pose.before.position.magnitude, pose.after.position.magnitude)) return true;
            }
            return false;
        }

        private static bool Different(float before, float after) => Mathf.Abs(before) > .0001f &&
            Mathf.Abs(after / before - 1) > AttachmentFit.ScaleThreshold;
    }
}
