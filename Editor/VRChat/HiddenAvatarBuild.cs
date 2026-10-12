using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar as an upload builds it, without uploading: a copy in its own preview scene put through the SDK's build
    /// steps (VRCFury, Modular Avatar, MCB, My Avatar...) in their order, one step per editor frame. Steps that write to
    /// the project (Poiyomi's material lock, the build cache) or wait for an upload (VRCFury's blueprint hook) are left
    /// out, and so are the steps the caller skips. <see cref="Release"/> closes the copy; scripts reloading release it too.
    /// </summary>
    public sealed class HiddenAvatarBuild
    {
        // By type name: they write shaders or assets into the project, or need the upload's blueprint.
        private static readonly string[] ProjectWriters = { "LockMaterialsOnUpload", "BuildCopyAssetsHook", "WhenBlueprintIdReadyHook" };
        // Every build not released yet, running or finished.
        private static readonly List<HiddenAvatarBuild> Open = new List<HiddenAvatarBuild>();

        public GameObject Source { get; }
        /// <summary>The built copy, once <see cref="Finished"/> without <see cref="Error"/>.</summary>
        public GameObject Copy { get; private set; }
        /// <summary>Who builds now ("VRCFury", "MCB"...), for progress.</summary>
        public string Building { get; private set; }
        public int Done { get; private set; }
        public int Total => steps.Count;
        public bool Finished { get; private set; }
        public string Error { get; private set; }
        /// <summary>Runs once the build finished or failed.</summary>
        public event Action Completed;

        private readonly List<IVRCSDKPreprocessAvatarCallback> steps;
        private readonly HashSet<Object> before = new HashSet<Object>();
        private Scene scene;

        private HiddenAvatarBuild(GameObject source, List<IVRCSDKPreprocessAvatarCallback> steps)
        {
            Source = source;
            this.steps = steps;
        }

        /// <summary>
        /// Starts building a copy of <paramref name="source"/>; <paramref name="skip"/> leaves build steps out by type, and
        /// <paramref name="prepare"/> changes the copy before the first step (a setting the build reads).
        /// </summary>
        public static HiddenAvatarBuild Start(GameObject source, Func<Type, bool> skip = null, Action<GameObject> prepare = null)
        {
            var steps = new List<IVRCSDKPreprocessAvatarCallback>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IVRCSDKPreprocessAvatarCallback>())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null || ProjectWriters.Contains(type.Name) || skip != null && skip(type)) continue;
                try { steps.Add((IVRCSDKPreprocessAvatarCallback)Activator.CreateInstance(type)); }
                catch (Exception ex) { Debug.LogWarning("[Orbiters] Hidden build: " + type.FullName + " could not start (" + ex.Message + ")."); }
            }
            steps.Sort((a, b) => a.callbackOrder.CompareTo(b.callbackOrder));
            var build = new HiddenAvatarBuild(source, steps);
            try { build.Begin(); prepare?.Invoke(build.Copy); }
            catch (Exception ex) { build.Fail(ex); return build; }
            Open.Add(build);
            if (Open.Count == 1)
            {
                EditorApplication.update += TickAll;
                AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
                EditorApplication.playModeStateChanged += ReleaseOnPlay;
            }
            return build;
        }

        private void Begin()
        {
            scene = EditorSceneManager.NewPreviewScene();
            var holder = new GameObject("Hidden build");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(Source, holder.transform);
            copy.name = Source.name;
            copy.transform.SetParent(null, false);
            Object.DestroyImmediate(holder);
            copy.SetActive(true);
            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                before.UnionWith(renderer.sharedMaterials.Where(m => m != null));
                var mesh = Optimization.MeshOptimization.MeshOf(renderer);
                if (mesh != null) before.Add(mesh);
            }
            Copy = copy;
        }

        private static void TickAll()
        {
            foreach (var build in Open.ToArray()) build.Tick();
        }

        // One build step per editor frame.
        private void Tick()
        {
            if (Finished) return;
            if (Copy == null) { Fail(new InvalidOperationException("the copy was closed")); return; }
            if (Done >= steps.Count) { Finish(); return; }
            var step = steps[Done];
            Building = Owner(step.GetType());
            try
            {
                if (!step.OnPreprocessAvatar(Copy)) { Fail(new InvalidOperationException(Building + " stopped the build (" + step.GetType().Name + ")")); return; }
            }
            catch (Exception ex) { Fail(ex); return; }
            Done++;
        }

        // Who a build step belongs to, from its assembly.
        private static string Owner(Type type)
        {
            string assembly = type.Assembly.GetName().Name;
            if (assembly.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0) return "VRCFury";
            if (assembly.StartsWith("nadena.dev", StringComparison.Ordinal)) return "Modular Avatar";
            if (assembly.IndexOf("MCB", StringComparison.OrdinalIgnoreCase) >= 0) return "MCB";
            if (assembly.StartsWith("Orbiters.MyAvatar", StringComparison.Ordinal)) return "My Avatar";
            if (assembly.StartsWith("Orbiters", StringComparison.Ordinal)) return "Orbiters Toolkit";
            if (assembly.StartsWith("VRC", StringComparison.Ordinal)) return "VRChat SDK";
            return assembly;
        }

        private void Finish()
        {
            Finished = true;
            Building = null;
            Notify();
        }

        private void Fail(Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Error = inner.Message;
            Debug.LogException(inner);
            Finished = true;
            Release();
            Notify();
        }

        private void Notify()
        {
            try { Completed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        private static void Forget(HiddenAvatarBuild build)
        {
            if (!Open.Remove(build) || Open.Count > 0) return;
            EditorApplication.update -= TickAll;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
            EditorApplication.playModeStateChanged -= ReleaseOnPlay;
        }

        /// <summary>Closes the copy and frees the meshes and materials the build steps made in memory.</summary>
        public void Release()
        {
            if (!Finished) { Finished = true; Error ??= "stopped"; }
            Forget(this);
            var copy = Copy;
            Copy = null;
            if (copy != null)
            {
                AttachmentAnimationBuild.Release(copy);
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null && !before.Contains(material) && !EditorUtility.IsPersistent(material)) Object.DestroyImmediate(material);
                    var mesh = Optimization.MeshOptimization.MeshOf(renderer);
                    if (mesh != null && !before.Contains(mesh) && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
                }
            }
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            before.Clear();
        }

        // A build left alive across a script reload hung Unity once: every build goes first.
        private static void ReleaseAll()
        {
            foreach (var build in Open.ToArray()) build.Release();
        }

        private static void ReleaseOnPlay(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) ReleaseAll();
        }
    }
}
