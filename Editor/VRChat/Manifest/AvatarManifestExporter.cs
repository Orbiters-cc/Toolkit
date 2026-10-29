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
            manifest.Project = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
            if (!string.IsNullOrEmpty(avatar.scene.path)) manifest.Scene = avatar.scene.path;
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
            if (!string.IsNullOrEmpty(avatarId)) SaveThumbnail(avatarId);
        }

        /// <summary>
        /// Saves the thumbnail VRChat now shows for <paramref name="avatarId"/> beside its manifest (&lt;avatar file name&gt;.png,
        /// at most 1200 × 900; contracts' Docs/project-setup.md, Avatar thumbnails), so Orbiters - N4N0 pictures the avatar
        /// without asking VRChat. Read back through the SDK's own signed-in API once the upload is done.
        /// </summary>
        internal static async void SaveThumbnail(string avatarId)
        {
            Texture2D image = null, scaled = null;
            try
            {
                var avatar = await VRC.SDKBase.Editor.Api.VRCApi.GetAvatar(avatarId, true);
                if (string.IsNullOrEmpty(avatar.ThumbnailImageUrl)) return;
                image = await VRC.SDKBase.Editor.Api.VRCApi.GetImage(avatar.ThumbnailImageUrl, true);
                if (image == null) return;
                scaled = Fit(image, 1200, 900);
                string path = Path.Combine(AvatarManifest.Folder, Path.ChangeExtension(AvatarManifest.FileName(avatarId), ".png"));
                SafePaths.ReplaceFile(path, scaled.EncodeToPNG());
            }
            catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not save the avatar's thumbnail for Orbiters - N4N0: " + ex.Message); }
            finally
            {
                if (scaled != null && scaled != image) UnityEngine.Object.DestroyImmediate(scaled);
            }
        }

        // A readable copy of `image` no larger than width × height, its aspect kept (the SDK's images may not be readable).
        private static Texture2D Fit(Texture2D image, int width, int height)
        {
            float scale = Mathf.Min(1f, Mathf.Min(width / (float)image.width, height / (float)image.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(image.width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(image.height * scale));
            var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var active = RenderTexture.active;
            try
            {
                Graphics.Blit(image, target);
                RenderTexture.active = target;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                return copy;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
