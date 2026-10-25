using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Manifest
{
    /// <summary>
    /// Writes the <see cref="AvatarManifest"/> of an avatar the VRChat SDK builds for Windows (Build &amp; Test or an
    /// upload), after every other build step: VRCFury builds at −10000 and compresses parameters at int.MaxValue − 100,
    /// Modular Avatar (NDMF) and the optimizers that run in it finish by −1025, MCB, My Avatar and the Toolkit before 10000,
    /// so the menu and parameters read here are the ones the avatar is uploaded with. The file is
    /// %LOCALAPPDATA%\Orbiters\Avatars\&lt;blueprint id&gt;.json, replaced in one step; a Build &amp; Test build also writes it
    /// under the id the SDK lists such a test avatar by (local:sdk_&lt;avatar name&gt;). An avatar uploaded for the first time
    /// by an SDK that only gives it its id once uploaded gets its file when the upload succeeds. Anything that goes wrong is
    /// said in the Console: the build goes on. Android and iOS builds, Play Mode and Orbiters' hidden builds write nothing.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class AvatarManifestExporter : IVRCSDKPreprocessAvatarCallback
    {
        // ActiveBuildType ("Test" or "Publish", "None" outside the SDK's builds) came with VRChat SDK 3.7.5.
        private static readonly PropertyInfo BuildType = typeof(VRC_SdkBuilder).GetProperty("ActiveBuildType", BindingFlags.Public | BindingFlags.Static);
        private static IVRCSdkAvatarBuilderApi builder;
        // What the last build wrote, for its upload.
        private static AvatarManifest built;
        private static string generator;

        static AvatarManifestExporter()
        {
            VRCSdkControlPanel.OnSdkPanelEnable += (_, __) => HookBuilder();
            HookBuilder();
        }

        public int callbackOrder => int.MaxValue;

        public bool OnPreprocessAvatar(GameObject avatar)
        {
            try { Export(avatar); }
            catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not write the avatar manifest Orbiters - N4N0 reads: " + ex.Message); }
            return true;
        }

        private static void Export(GameObject avatar)
        {
            string build = BuildType != null ? BuildType.GetValue(null)?.ToString() : "Unknown";
            if (avatar == null || build == "None" || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows) return;
            string id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            var manifest = AvatarManifest.Read(avatar, id);
            // N4N0's own version, noted as the build started (its gallery folder is no longer known by now).
            if (manifest.OrbTextSync.HasValue) manifest.OrbVersion = OrbInstaller.BuiltVersion(avatar);
            // The SDK builds a copy named after the avatar.
            if (manifest.Name.EndsWith("(Clone)", StringComparison.Ordinal)) manifest.Name = manifest.Name.Substring(0, manifest.Name.Length - "(Clone)".Length).TrimEnd();
            manifest.WrittenUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            manifest.Generator = Generator();
            built = manifest;
            if (!string.IsNullOrEmpty(id)) Write(manifest, id);
            // Build & Test copies the bundle to VRChat's test avatars as "<avatar name>.vrca", which the SDK lists as
            // local:sdk_<avatar name>.
            if (build == "Test" && !string.IsNullOrEmpty(manifest.Name)) Write(manifest, "local:sdk_" + manifest.Name);
        }

        /// <summary>Writes <paramref name="manifest"/> as the manifest of <paramref name="avatarId"/> (its id in the file too).</summary>
        internal static string Write(AvatarManifest manifest, string avatarId)
        {
            manifest.AvatarId = avatarId;
            string path = Path.Combine(AvatarManifest.Folder, AvatarManifest.FileName(avatarId));
            SafePaths.ReplaceFile(path, manifest.ToJson());
            return path;
        }

        private static string Generator()
        {
            if (generator == null)
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AvatarManifestExporter).Assembly);
                generator = "Orbiters Toolkit" + (package != null ? " " + package.version : "");
            }
            return generator;
        }

        private static void HookBuilder()
        {
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var next) || next == builder) return;
            if (builder != null)
            {
                builder.OnSdkBuildStart -= BuildStart;
                builder.OnSdkUploadSuccess -= Uploaded;
            }
            builder = next;
            builder.OnSdkBuildStart += BuildStart;
            builder.OnSdkUploadSuccess += Uploaded;
        }

        private static void BuildStart(object sender, object target) => built = null;

        // The id the upload ended with, when the build had none (or another).
        private static void Uploaded(object sender, string avatarId)
        {
            try
            {
                if (built != null && !string.IsNullOrEmpty(avatarId) && built.AvatarId != avatarId) Write(built, avatarId);
            }
            catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not write the avatar manifest Orbiters - N4N0 reads: " + ex.Message); }
        }
    }
}
