using System;
using System.Text.RegularExpressions;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Links to VRChat's website, e.g. an uploaded avatar's page.</summary>
    public static class VrcLinks
    {
        private const string Home = "https://vrchat.com/home/";
        private static readonly Regex AvatarId = new Regex(
            "^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Whether <paramref name="id"/> is a VRChat avatar ID ("avtr_" and a GUID).</summary>
        public static bool IsAvatarId(string id) => !string.IsNullOrWhiteSpace(id) && AvatarId.IsMatch(id.Trim());

        /// <summary>The avatar's page on vrchat.com, or null when <paramref name="avatarId"/> is not an avatar ID.</summary>
        public static string AvatarPage(string avatarId) =>
            IsAvatarId(avatarId) ? Home + "avatar/" + Uri.EscapeDataString(avatarId.Trim().ToLowerInvariant()) : null;
    }
}
