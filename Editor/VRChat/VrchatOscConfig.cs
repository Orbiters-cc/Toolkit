using System;
using System.Collections.Generic;
using System.IO;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// VRChat's OSC config files. The first time VRChat loads an avatar it writes the avatar's parameters (their OSC
    /// addresses and types) to LocalLow\VRChat\VRChat\OSC\&lt;account id&gt;\Avatars\&lt;avatar id&gt;.json, one per
    /// account, and from then on reads that file rather than the avatar, even after the avatar is uploaded again with other
    /// parameters: OSC apps (face tracking, Orbiters - N4N0) then send to parameters VRChat no longer maps, or never reach new
    /// ones. Deleting the file after an upload makes VRChat write it again from the new avatar the next time it loads it.
    /// The idea is Haï's Auto-reset OSC config (dev.hai-vr.fuck-osc-config, MIT), which deletes the signed-in account's file;
    /// this deletes every account's on this computer, as an avatar can be worn by any of them (with Haï's installed too: it
    /// leaves the other accounts' files).
    /// </summary>
    public static class VrchatOscConfig
    {
        /// <summary>
        /// The switch My Avatar shows in the Orbiters settings window (Features, My Avatar), on by default. Where nothing
        /// registers it (Toolkit without My Avatar) the reset stays on.
        /// </summary>
        public const string Feature = "toolkit.reset-osc-config";

        /// <summary>Whether an upload resets the avatar's OSC config (<see cref="Feature"/>).</summary>
        public static bool Enabled => OrbitersFeatures.Find(Feature) == null || OrbitersFeatures.IsEnabled(Feature);

        /// <summary>What a reset did: the accounts whose file went, and the files that could not be deleted (with why).</summary>
        public sealed class Result
        {
            public readonly List<string> Accounts = new List<string>();
            public readonly List<string> Failures = new List<string>();
        }

        /// <summary>%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\OSC (VRChat's own, on Windows), or null where there is no user profile.</summary>
        public static string Folder
        {
            get
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow", "VRChat", "VRChat", "OSC");
            }
        }

        /// <summary>
        /// Deletes <paramref name="avatarId"/>'s OSC config in each account's folder (usr_…) under <paramref name="oscFolder"/>.
        /// Nothing for an id that is not an uploaded avatar's (avtr_ and a UUID's characters), or a folder that does not exist.
        /// </summary>
        public static Result Reset(string oscFolder, string avatarId)
        {
            var result = new Result();
            if (!IsAvatarId(avatarId) || string.IsNullOrEmpty(oscFolder) || !Directory.Exists(oscFolder)) return result;
            foreach (string account in Directory.GetDirectories(oscFolder, "usr_*"))
            {
                string file = Path.Combine(account, "Avatars", avatarId + ".json");
                // False for a folder of that name too.
                if (!File.Exists(file)) continue;
                try
                {
                    File.Delete(file);
                    result.Accounts.Add(Path.GetFileName(account));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failures.Add(ex.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// The reset an upload of <paramref name="avatarId"/> does: <see cref="Reset"/> in every account's folder, or null
        /// when the switch is off (nothing is deleted).
        /// </summary>
        public static Result AfterUpload(string oscFolder, string avatarId) => Enabled ? Reset(oscFolder, avatarId) : null;

        /// <summary>avtr_ followed by letters, digits and dashes (no path separator or dot can reach the file name).</summary>
        public static bool IsAvatarId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length <= 5 || id.Length > 64 || !id.StartsWith("avtr_", StringComparison.Ordinal)) return false;
            for (int i = 5; i < id.Length; i++)
            {
                char c = id[i];
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
            }
            return true;
        }
    }
}
