using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar builds the VRChat SDK's panel finishes (Build &amp; Test, Build &amp; Publish, its Build API): which avatar,
    /// the bundle it made and how big that file is. The one listener the Orbiters tools share: My Avatar keeps the real size
    /// beside its estimate, Unit Git the bundle for its upload release. (Mixer's console times builds on its own: it has no
    /// package dependency, so that it works when others don't compile.) Reading the file's length is all it costs.
    /// </summary>
    [InitializeOnLoad]
    public static class VrcSdkBuilds
    {
        /// <summary>A finished build.</summary>
        public sealed class Build
        {
            /// <summary>The avatar the build started from (null once it is gone), its name and blueprint ID then.</summary>
            public GameObject Avatar;
            public string AvatarName, BlueprintId;
            public string BundlePath;
            /// <summary>The bundle's size on disk: what the SDK uploads, compressed.</summary>
            public long Bytes;
            /// <summary>The platform it was built for (<see cref="AvatarPlatform"/>: "pc", "android", "ios").</summary>
            public string Platform;
            public DateTime When;
        }

        /// <summary>Raised as a build starts, with the avatar it starts from (before the SDK copies it).</summary>
        public static event Action<GameObject> Starting;

        /// <summary>Raised on the editor's thread as each build succeeds.</summary>
        public static event Action<Build> Succeeded;

        /// <summary>Raised as each build ends, whether it succeeded or not, with the SDK's message.</summary>
        public static event Action<string> Finished;

        /// <summary>Raised as an upload succeeds, with the id the avatar was uploaded under.</summary>
        public static event Action<string> Uploaded;

        /// <summary>Whether the SDK's panel is building an avatar now (between its build's start and end).</summary>
        public static bool Building { get; private set; }

        /// <summary>The last build that succeeded in this editor session, or null.</summary>
        public static Build Last { get; private set; }

        private static IVRCSdkAvatarBuilderApi attached;
        private static GameObject started;
        private static readonly Operation operation = new Operation();
        // ActiveBuildType ("Test" or "Publish", "None" outside the SDK's builds) came with VRChat SDK 3.7.5.
        private static readonly System.Reflection.PropertyInfo BuildType =
            typeof(VRC_SdkBuilder).GetProperty("ActiveBuildType", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        /// <summary>
        /// The builder's operation under way: from a build's start to its end, or for a publish to its upload's end. The
        /// panel may close meanwhile (an upload goes on without it): the builder is let go, or another one followed, only
        /// once the operation is over, so its end (the upload's id) still reaches the listeners.
        /// </summary>
        internal sealed class Operation
        {
            private bool building, publishing, uploading, detach, attach;

            public bool Busy => building || publishing || uploading;

            public void BuildStarted(bool publish) { building = true; publishing = publish; uploading = false; }
            public void BuildEnded(bool failed) { building = false; if (failed) publishing = false; }
            public void UploadStarted() { uploading = true; }
            public void UploadEnded() { uploading = false; publishing = false; }

            /// <summary>The panel closed: true to let the builder go now, else once the operation is over.</summary>
            public bool PanelClosed()
            {
                if (!Busy) return true;
                detach = true;
                return false;
            }

            /// <summary>
            /// Another builder is there: true to follow it now, else once the operation is over. <paramref name="idle"/>: the
            /// builder followed has nothing under way by its own account (a publish build that never uploaded).
            /// </summary>
            public bool NewBuilder(bool idle)
            {
                if (!Busy || idle)
                {
                    building = publishing = uploading = false;
                    detach = attach = false;
                    return true;
                }
                attach = true;
                return false;
            }

            /// <summary>What to do now the operation may be over: let the builder go, look for another one.</summary>
            public (bool Detach, bool Attach) Settle()
            {
                if (Busy) return (false, false);
                var result = (detach, attach);
                detach = attach = false;
                return result;
            }
        }

        static VrcSdkBuilds()
        {
            VRCSdkControlPanel.OnSdkPanelEnable -= OnPanelEnable;
            VRCSdkControlPanel.OnSdkPanelEnable += OnPanelEnable;
            VRCSdkControlPanel.OnSdkPanelDisable -= OnPanelDisable;
            VRCSdkControlPanel.OnSdkPanelDisable += OnPanelDisable;
            AssemblyReloadEvents.beforeAssemblyReload -= Detach;
            AssemblyReloadEvents.beforeAssemblyReload += Detach;
            EditorApplication.delayCall += TryAttach;
        }

        private static void OnPanelEnable(object sender, EventArgs args)
        {
            EditorApplication.delayCall -= TryAttach;
            EditorApplication.delayCall += TryAttach;
        }

        private static void OnPanelDisable(object sender, EventArgs args)
        {
            if (operation.PanelClosed()) Detach();
        }

        private static void TryAttach()
        {
            try
            {
                if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null ||
                    ReferenceEquals(builder, attached))
                    return;
                if (attached != null && !operation.NewBuilder(attached.BuildState != SdkBuildState.Building && attached.UploadState != SdkUploadState.Uploading)) return;
                Detach();
                attached = builder;
                attached.OnSdkBuildStart += OnBuildStart;
                attached.OnSdkBuildSuccess += OnBuildSuccess;
                attached.OnSdkBuildFinish += OnBuildFinish;
                attached.OnSdkBuildError += OnBuildError;
                attached.OnSdkUploadStart += OnUploadStart;
                attached.OnSdkUploadSuccess += OnUploadSuccess;
                attached.OnSdkUploadFinish += OnUploadEnd;
                attached.OnSdkUploadError += OnUploadEnd;
            }
            catch (Exception ex)
            {
                // The SDK panel changed: builds then go unrecorded, nothing else is affected.
                Debug.LogWarning("[Orbiters] The VRChat SDK's builds can't be followed: " + ex.Message);
            }
        }

        private static void Detach()
        {
            if (attached == null) return;
            attached.OnSdkBuildStart -= OnBuildStart;
            attached.OnSdkBuildSuccess -= OnBuildSuccess;
            attached.OnSdkBuildFinish -= OnBuildFinish;
            attached.OnSdkBuildError -= OnBuildError;
            attached.OnSdkUploadStart -= OnUploadStart;
            attached.OnSdkUploadSuccess -= OnUploadSuccess;
            attached.OnSdkUploadFinish -= OnUploadEnd;
            attached.OnSdkUploadError -= OnUploadEnd;
            attached = null;
            Building = false;
        }

        // The operation may be over: the builder is let go, or another one followed, as the panel asked meanwhile.
        private static void Settle()
        {
            var (detach, attach) = operation.Settle();
            if (detach) Detach();
            if (attach) TryAttach();
        }

        private static void OnBuildError(object sender, string message)
        {
            operation.BuildEnded(true);
            Settle();
        }

        private static void OnUploadStart(object sender, EventArgs args) => operation.UploadStarted();

        private static void OnUploadEnd(object sender, string message)
        {
            operation.UploadEnded();
            Settle();
        }

        private static void Raise<T>(Action<T> listeners, T value)
        {
            foreach (Action<T> listener in listeners?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(value); }
                catch (Exception ex) { Debug.LogException(ex); }
        }

        internal static void OnBuildFinish(object sender, string message)
        {
            Building = false;
            operation.BuildEnded(false);
            Raise(Finished, message);
            Settle();
        }

        private static void OnUploadSuccess(object sender, string avatarId) => Raise(Uploaded, avatarId);

        internal static void OnBuildStart(object sender, object target)
        {
            Building = true;
            // A publish uploads once built: the operation lasts until the upload ends.
            operation.BuildStarted(BuildType == null || BuildType.GetValue(null)?.ToString() == "Publish");
            started = target as GameObject;
            foreach (Action<GameObject> listener in Starting?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(started); }
                catch (Exception ex) { Debug.LogException(ex); }
        }

        private static void OnBuildSuccess(object sender, string bundlePath)
        {
            var avatar = started != null ? started : VrcSdkAvatarBuilder.Selected(attached);
            started = null;
            long bytes = 0;
            try { if (!string.IsNullOrEmpty(bundlePath) && File.Exists(bundlePath)) bytes = new FileInfo(bundlePath).Length; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            Report(new Build
            {
                Avatar = avatar, AvatarName = avatar != null ? avatar.name : "", BlueprintId = BlueprintOf(avatar), BundlePath = bundlePath, Bytes = bytes,
                Platform = AvatarPlatform.Current, When = DateTime.UtcNow,
            });
        }

        /// <summary>Records <paramref name="build"/> and tells the listeners (the SDK's callback, and tests).</summary>
        public static void Report(Build build)
        {
            Last = build;
            foreach (Action<Build> listener in Succeeded?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { listener(build); }
                catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>The blueprint ID the avatar uploads under, or "" before its first upload.</summary>
        public static string BlueprintOf(GameObject avatar)
        {
            var pipeline = avatar != null ? avatar.GetComponent<PipelineManager>() : null;
            return pipeline != null && !string.IsNullOrEmpty(pipeline.blueprintId) ? pipeline.blueprintId : "";
        }
    }
}
