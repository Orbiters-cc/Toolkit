using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// Coverage is only requested for body clothing with evidence of different rig dimensions. Underwear, swimwear and
    /// one-piece garments count as body clothing.
    /// </summary>
    public static class ClothingCoverage
    {
        private static readonly Regex Clothing = new Regex(@"\b(pants|shorts|trousers|hoodie|jacket|shirt|underwear|briefs|boxers|thong|bra|jock|speedo|trunks)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Distinctive enough to count inside longer names ("classicJockstrap", "WickerOnesie").
        private static readonly Regex Garments = new Regex("jockstrap|underwear|lingerie|panties|bikini|swimsuit|swimwear|leotard|bodysuit|onesie|jumpsuit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Underwear = new Regex(@"\b(underwear|briefs|boxers|thong|bra|jock|speedo|trunks)\b|jockstrap|lingerie|panties|bikini", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OnePiece = new Regex("swimsuit|swimwear|leotard|bodysuit|onesie|jumpsuit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool Eligible(OrbitersAttachment attachment, bool madeForDifferentBase = false)
        {
            if (attachment == null) return false;
            // Use original asset/object names, not an AI display label or unrelated parent folder.
            if (!NamedClothing(attachment.name + " " + Path.GetFileNameWithoutExtension(attachment.variant ?? "")) &&
                !attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(IsBodyClothing)) return false;
            return DifferentBase(attachment, madeForDifferentBase);
        }

        /// <summary>Outfit packs opt in per garment, so a shirt does not make bundled props expand.</summary>
        public static bool Eligible(OrbitersAttachment attachment, SkinnedMeshRenderer renderer, bool madeForDifferentBase = false)
        {
            if (attachment == null || renderer == null || !renderer.transform.IsChildOf(attachment.transform)) return false;
            bool named = IsBodyClothing(renderer);
            // A standalone garment may use generic renderer names. A mixed outfit must identify its garments.
            if (!named && attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 1)
                named = NamedClothing(attachment.name + " " + Path.GetFileNameWithoutExtension(attachment.variant ?? ""));
            return named && DifferentBase(attachment, madeForDifferentBase);
        }

        private static bool IsBodyClothing(SkinnedMeshRenderer renderer) => renderer != null &&
            NamedClothing(renderer.name + " " + (renderer.sharedMesh != null ? renderer.sharedMesh.name : ""));

        /// <summary>Recognized outer garments are fitted after the shirt or underwear worn beneath them.</summary>
        public static int Layer(SkinnedMeshRenderer renderer)
        {
            int layer = NamedLayer(renderer.name);
            return layer >= 0 ? layer : Mathf.Max(0, NamedLayer(renderer.sharedMesh != null ? renderer.sharedMesh.name : ""));
        }

        private static int NamedLayer(string name)
        {
            name = name.Replace('_', ' ').Replace('-', ' ');
            if (Regex.IsMatch(name, @"\b(jacket|hoodie)\b", RegexOptions.IgnoreCase)) return 2;
            if (Regex.IsMatch(name, @"\b(shirt|pants|shorts|trousers)\b", RegexOptions.IgnoreCase) || OnePiece.IsMatch(name)) return 1;
            return Underwear.IsMatch(name) ? 0 : -1;
        }

        private static bool NamedClothing(string name)
        {
            name = name.Replace('_', ' ').Replace('-', ' ');
            return Clothing.IsMatch(name) || Garments.IsMatch(name);
        }

        private static bool DifferentBase(OrbitersAttachment attachment, bool madeForDifferentBase)
        {
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
