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

        /// <summary>The last build that succeeded in this editor session, or null.</summary>
        public static Build Last { get; private set; }

        private static IVRCSdkAvatarBuilderApi attached;
        private static GameObject started;

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

        private static void OnPanelDisable(object sender, EventArgs args) => Detach();

        private static void TryAttach()
        {
            try
            {
                if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null ||
                    ReferenceEquals(builder, attached))
                    return;
                Detach();
                attached = builder;
                attached.OnSdkBuildStart += OnBuildStart;
                attached.OnSdkBuildSuccess += OnBuildSuccess;
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
            attached = null;
        }

        private static void OnBuildStart(object sender, object target)
        {
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
