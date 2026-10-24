using System;
using System.Collections.Generic;

namespace Orbiters.Toolkit.Editor.VRChat.Parameters
{
    /// <summary>The animator parameters VRChat and VRCFaceTracking set by name: no build step may change or rename them.</summary>
    public static class VrcParameters
    {
        // VRChat's own parameters, and those VRCFaceTracking drives over OSC.
        private static readonly HashSet<string> BuiltIn = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight", "AngularY",
            "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode",
            "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "ScaleModified", "ScaleFactor",
            "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent", "EyeTrackingActive", "LipTrackingActive",
        };

        /// <summary>The parameters a PhysBone sets from its own: its parameter with these after it.</summary>
        public static readonly IReadOnlyList<string> PhysBoneSuffixes = new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        /// <summary>
        /// The expression parameters of an avatar that keeps VRChat's default menu (custom expressions off): its emotes and
        /// face blend, which the default Action and FX layers read and the default menu sets.
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultExpressionParameters = new[] { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV" };

        public static bool Protected(string name) =>
            string.IsNullOrEmpty(name) || BuiltIn.Contains(name) || name.Contains("v2/") || name.StartsWith("OSCm/", StringComparison.Ordinal) || name.StartsWith("FT/", StringComparison.Ordinal) ||
            OscDriven(name);

        // Parameters apps set over OSC from outside the avatar (heart rate, text and chatbox displays, OSCmooth, VRCOSC,
        // N4N0…), by the names those apps use: nothing in the avatar setting them doesn't make them unused.
        private static readonly string[] OscPrefixes = { "OSC", "VRCOSC/", "KAT_", "BFI/", "bHaptics", "Heartrate", "HeartRate", "isHR", "ChatBox", "Chatbox", "MagicChatbox", "N4N0/" };
        private static readonly HashSet<string> OscNames = new HashSet<string>(StringComparer.Ordinal) { "HR", "onesHR", "tensHR", "hundredsHR", "HRPercent", "FullHRPercent" };

        /// <summary>Whether apps commonly set <paramref name="name"/> over OSC (by the names they use).</summary>
        public static bool OscDriven(string name)
        {
            if (string.IsNullOrEmpty(name) || OscNames.Contains(name)) return !string.IsNullOrEmpty(name);
            foreach (string prefix in OscPrefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
