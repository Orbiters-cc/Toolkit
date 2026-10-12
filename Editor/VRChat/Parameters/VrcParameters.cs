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

        public static bool Protected(string name) =>
            string.IsNullOrEmpty(name) || BuiltIn.Contains(name) || name.Contains("v2/") || name.StartsWith("OSCm/", StringComparison.Ordinal) || name.StartsWith("FT/", StringComparison.Ordinal);
    }
}
