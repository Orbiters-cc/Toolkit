using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
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
    /// Every successful upload, whatever its platform, is also noted in uploaded.json there (<see cref="NoteUpload"/>).
    /// </summary>
    [InitializeOnLoad]
    internal sealed class AvatarManifestExporter : IVRCSDKPreprocessAvatarCallback
    {
        // ActiveBuildType ("Test" or "Publish", "None" outside the SDK's builds) came with VRChat SDK 3.7.5.
        private static readonly PropertyInfo BuildType = typeof(VRC_SdkBuilder).GetProperty("ActiveBuildType", BindingFlags.Public | BindingFlags.Static);
        private static IVRCSdkAvatarBuilderApi builder;
        // What the last build wrote, for its upload.
        private static AvatarManifest built;
        // The avatar the last build started with, for an upload that wrote no manifest (Android, iOS).
        private static string builtName;
        private static string generator;

        /// <summary>The upload record's file in <see cref="AvatarManifest.Folder"/> (contracts' Docs/avatar-upload.md).</summary>
        internal const string UploadFileName = "uploaded.json";

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
            if (manifest.N4N0TextSync.HasValue) manifest.N4N0Version = N4N0Installer.BuiltVersion(avatar);
            manifest.Name = WithoutClone(manifest.Name);
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

        // The SDK builds a copy named after the avatar.
        private static string WithoutClone(string name) =>
            name != null && name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - "(Clone)".Length).TrimEnd() : name;

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

        private static void BuildStart(object sender, object target)
        {
            built = null;
            builtName = WithoutClone((target as GameObject)?.name);
        }

        // The id the upload ended with, when the build had none (or another).
        private static void Uploaded(object sender, string avatarId)
        {
            try
            {
                if (built != null && !string.IsNullOrEmpty(avatarId) && built.AvatarId != avatarId) Write(built, avatarId);
            }
            catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not write the avatar manifest Orbiters - N4N0 reads: " + ex.Message); }
            NoteUpload(avatarId);
            if (!string.IsNullOrEmpty(avatarId)) SaveThumbnail(avatarId);
            // The scene as uploaded, for Orbiters - N4N0's 3D preview of the project.
            Orbiters.Toolkit.Editor.Preview.ProjectPreview.RequestActiveScene("upload");
        }

        /// <summary>
        /// Notes the upload of <paramref name="avatarId"/> in %LOCALAPPDATA%\Orbiters\Avatars\uploaded.json, replaced in one
        /// step, for whichever platform was built and whether or not its manifest changed (contracts' Docs/avatar-upload.md):
        /// Orbiters - N4N0 then makes VRChat wear the new version. Only uploaded avatars' ids (avtr_).
        /// </summary>
        internal static void NoteUpload(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId) || !avatarId.StartsWith("avtr_", StringComparison.Ordinal)) return;
            try
            {
                string project = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
                int pid;
                using (var unity = System.Diagnostics.Process.GetCurrentProcess()) pid = unity.Id;
                string record = UploadRecord(avatarId, Platform(EditorUserBuildSettings.activeBuildTarget), built?.Name ?? builtName, project, DateTime.UtcNow, pid);
                SafePaths.ReplaceFile(Path.Combine(AvatarManifest.Folder, UploadFileName), record);
            }
            catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not tell Orbiters - N4N0 about the upload: " + ex.Message); }
        }

        /// <summary>The upload record as contracts' Docs/avatar-upload.md shows it (schema 1).</summary>
        internal static string UploadRecord(string avatarId, string platform, string name, string project, DateTime uploadedUtc, int unityPid)
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            using (var json = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2 })
            {
                json.WriteStartObject();
                json.WritePropertyName("schema"); json.WriteValue(1);
                json.WritePropertyName("avatarId"); json.WriteValue(avatarId);
                json.WritePropertyName("platform"); json.WriteValue(platform);
                json.WritePropertyName("name"); json.WriteValue(name ?? "");
                json.WritePropertyName("project"); json.WriteValue(project ?? "");
                json.WritePropertyName("uploadedAt"); json.WriteValue(uploadedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                json.WritePropertyName("unityPid"); json.WriteValue(unityPid);
                json.WriteEndObject();
            }
            return text.ToString();
        }

        /// <summary>The record's platform for a build target: the platform VRChat wears the build on.</summary>
        internal static string Platform(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android: return "android";
                case BuildTarget.iOS: return "ios";
                default: return "windows";
            }
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
