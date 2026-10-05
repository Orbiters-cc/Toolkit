using UnityEditor;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>The VRChat platform the project builds for, as Orbiters names it: "pc", "android" (Quest) or "ios".</summary>
    public static class AvatarPlatform
    {
        public const string Pc = "pc", Android = "android", Ios = "ios";

        public static string Current
        {
            get
            {
                switch (EditorUserBuildSettings.activeBuildTarget)
                {
                    case BuildTarget.Android: return Android;
                    case BuildTarget.iOS: return Ios;
                    default: return Pc;
                }
            }
        }

        public static bool Mobile => Current != Pc;

        public static string Label(string platform)
        {
            switch (platform)
            {
                case Android: return "Android (Quest)";
                case Ios: return "iOS";
                case Pc: return "PC";
                default: return platform ?? "";
            }
        }

        public static string ShortLabel(string platform) => platform == Android ? "Quest" : Label(platform);
    }
}
