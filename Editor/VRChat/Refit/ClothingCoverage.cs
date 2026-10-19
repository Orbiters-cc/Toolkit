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
        private static readonly Regex Clothing = new Regex(@"\b(pants|shorts|trousers|hoodie|jacket|vest|waistcoat|shirt|underwear|briefs|boxers|thong|bra|jock|speedo|trunks)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Distinctive enough to count inside longer names ("classicJockstrap", "WickerOnesie").
        private static readonly Regex Garments = new Regex("jockstrap|underwear|lingerie|panties|bikini|swimsuit|swimwear|leotard|bodysuit|onesie|jumpsuit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Underwear = new Regex(@"\b(underwear|briefs|boxers|thong|bra|jock|speedo|trunks)\b|jockstrap|lingerie|panties|bikini", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OnePiece = new Regex("swimsuit|swimwear|leotard|bodysuit|onesie|jumpsuit", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Where a word of a joined name ("FashionSuitJacket") starts.
        private static readonly Regex WordStart = new Regex(@"(?<=\p{Ll})(?=\p{Lu})", RegexOptions.CultureInvariant);

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

        /// <summary>
        /// Resized or reproportioned onto the avatar (recorded by <see cref="AttachmentFit"/>): made for another body than the
        /// one it was put on, unless it was made for the avatar's original base.
        /// </summary>
        public static bool Resized(OrbitersAttachment attachment) => attachment != null && DifferentBase(attachment, false);

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
            name = Words(name);
            if (Named(name, @"\b(jacket|hoodie)\b")) return 3;
            if (Named(name, @"\b(vest|waistcoat)\b")) return 2;
            if (Named(name, @"\b(shirt|pants|shorts|trousers)\b") || OnePiece.IsMatch(name)) return 1;
            return Underwear.IsMatch(name) ? 0 : -1;
        }

        private static bool Named(string words, string pattern) =>
            Regex.IsMatch(words, pattern, RegexOptions.IgnoreCase) || Regex.IsMatch(Heads(words), pattern, RegexOptions.IgnoreCase);

        private static bool NamedClothing(string name)
        {
            name = Words(name);
            return Clothing.IsMatch(name) || Garments.IsMatch(name) || Clothing.IsMatch(Heads(name));
        }

        private static string Words(string name) => name.Replace('_', ' ').Replace('-', ' ');

        // A joined name is the thing its last word names: "FashionSuitJacket" a jacket, "ShirtButton" a button.
        private static string Heads(string words) =>
            string.Join(" ", words.Split(' ').Select(word => WordStart.Split(word).Last()));

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
