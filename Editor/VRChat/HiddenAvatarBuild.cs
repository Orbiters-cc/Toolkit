using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// The avatar as an upload builds it, without uploading: a copy in its own preview scene put through the SDK's own list of
    /// build steps (VRCFury, Modular Avatar, MCB, My Avatar...) in its order, all in one editor frame, as the SDK runs them.
    /// VRCFury opens its build at the first order and closes it at the last, and keeps what its steps share (the objects'
    /// original paths, its build context, the objects it made) for one frame only: run across frames, its later steps saw a
    /// hierarchy earlier steps had already moved and built another avatar. The copy is made and released in other frames;
    /// while the steps run, a progress bar can cancel them.
    /// Left out: the steps that lock shaders or write the build cache into the project, belong to the upload itself or time
    /// real builds (<see cref="LeftOut"/>), and the caller's skips. The steps build for the editor's current build target.
    /// VRCFury is kept from asking about the copy's Write Defaults (its answer would be saved on the copy) and its error
    /// dialog is left out: its errors stop the build. VRCFury still opens a dialog in a few cases a hidden build can't
    /// answer; those it sees coming (<see cref="Blockers"/>: too many contacts or parameters, desktop shaders on an
    /// Android or iOS build) refuse the build first. What the steps still write, as at upload (VRCFury's temporary build
    /// folder, Orbiters' build cache), is deleted on <see cref="Release"/>, or once the editor can when Play Mode starts
    /// or scripts reload, which release every build too. As at upload, VRCFury and MCB still save the project's unsaved
    /// assets first, and VRCFury deletes its earlier build folders no loaded avatar uses.
    /// </summary>
    public sealed class HiddenAvatarBuild
    {
        /// <summary>One build step: the SDK's callback (or a test's), its order and how to run it on the copy.</summary>
        internal readonly struct Step
        {
            public readonly Type Type;
            public readonly int Order;
            public readonly Func<GameObject, bool> Run;

            public Step(Type type, int order, Func<GameObject, bool> run)
            {
                Type = type; Order = order; Run = run;
            }
        }

        // By full type name: the build cache makes in-memory objects assets for the SDK's prefab, VRCFury's blueprint hook
        // clears the callbacks a pending upload runs once it succeeds, and the Logger's probes time real builds.
        private static readonly string[] Left =
        {
            typeof(BuildCopyAssetsHook).FullName,
            "VF.Hooks.WhenBlueprintIdReadyHook+VrcPreuploadHook",
            "Orbiters.Logger.Editor.VRChat.LoggerBuildStepProbe",
        };
        // By type name, as VRCFury blocks them outside uploads: shader locks that write shaders and materials into the
        // project, Poiyomi's (in Thry.ThryEditor.ShaderOptimizer, Thry.ShaderOptimizer in older versions) and lilToon's.
        private static readonly string[] Locks = { "LockMaterialsOnUpload", "VRChatModule" };

        // VRChat's limits that VRCFury checks with a dialog, outside uploads, instead of failing.
        private const int MaxContacts = 256, MaxParameters = 8192;

        private const string VrcFuryBuilds = "Packages/com.vrcfury.temp/Builds";
        private const string LeftoversKey = "Orbiters.HiddenAvatarBuild.Leftovers";

        // Every build not released yet, running or finished.
        private static readonly List<HiddenAvatarBuild> Open = new List<HiddenAvatarBuild>();
        // While scripts reload or Play Mode starts: nothing is deleted from the project then, only queued.
        private static bool deferring;

        public GameObject Source { get; }
        /// <summary>The built copy, once <see cref="Finished"/> without <see cref="Error"/>.</summary>
        public GameObject Copy { get; private set; }
        /// <summary>Who builds now ("VRCFury", "MCB"...), for progress and errors.</summary>
        public string Building { get; private set; }
        public int Done { get; private set; }
        public int Total => steps.Count;
        public bool Finished { get; private set; }
        public string Error { get; private set; }
        /// <summary>Whether the build stopped because someone asked (the progress bar's Cancel, <see cref="Release"/> before the end).</summary>
        public bool Cancelled { get; private set; }
        /// <summary>Runs once the build finished, failed or was stopped.</summary>
        public event Action Completed;

        private readonly List<Step> steps;
        // In-memory objects the copy shared with the avatar before the build: never freed with the copy.
        private readonly HashSet<Object> before = new HashSet<Object>();
        // VRCFury's temporary build folders this build made.
        private readonly List<string> folders = new List<string>();
        // The copy, kept once closed: the build cache finds its data by it.
        private GameObject made;
        private Scene scene;
        private bool started, notified;

        private HiddenAvatarBuild(GameObject source, List<Step> steps)
        {
            Source = source;
            this.steps = steps;
        }

        /// <summary>
        /// Starts building a copy of <paramref name="source"/>; <paramref name="skip"/> leaves build steps out by type, and
        /// <paramref name="prepare"/> changes the copy before the first step (a setting the build reads). The steps run on a
        /// later editor frame; <see cref="Completed"/> tells when they are done.
        /// </summary>
        public static HiddenAvatarBuild Start(GameObject source, Func<Type, bool> skip = null, Action<GameObject> prepare = null)
        {
            List<Step> steps;
            try { steps = SdkSteps(skip); }
            catch (Exception ex)
            {
                var failed = new HiddenAvatarBuild(source, new List<Step>());
                failed.Fail(ex);
                return failed;
            }
            return Start(source, steps, prepare);
        }

        internal static HiddenAvatarBuild Start(GameObject source, List<Step> steps, Action<GameObject> prepare = null)
        {
            var build = new HiddenAvatarBuild(source, steps);
            string refused = source == null ? "the avatar is gone"
                : EditorApplication.isPlayingOrWillChangePlaymode ? "leave Play Mode first"
                // VRCFury refuses to build its test copies again, with a dialog.
                : VrcFury.IsTestCopy(source) ? "“" + source.name + "” is a VRCFury test copy, built already: check the avatar it was made from"
                : Blockers(source).FirstOrDefault()?.Title;
            if (refused != null) { build.End(refused, false); return build; }
            try
            {
                build.Begin();
                prepare?.Invoke(build.Copy);
            }
            catch (Exception ex) { build.Fail(ex); return build; }
            Open.Add(build);
            if (Open.Count == 1)
            {
                EditorApplication.update += TickAll;
                AssemblyReloadEvents.beforeAssemblyReload += ReleaseOnReload;
                EditorApplication.playModeStateChanged += ReleaseOnPlay;
            }
            return build;
        }

        /// <summary>
        /// The SDK's own build steps as an upload runs them: its list (which tools change: VRCFury moves the SDK's
        /// EditorOnly removal to the end), sorted by order as the SDK sorts it, ties in list order, less
        /// <see cref="LeftOut"/> and <paramref name="skip"/>.
        /// </summary>
        internal static List<Step> SdkSteps(Func<Type, bool> skip = null)
        {
            var field = typeof(VRCBuildPipelineCallbacks).GetField("_preprocessAvatarCallbacks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (!(field?.GetValue(null) is List<IVRCSDKPreprocessAvatarCallback> callbacks))
                throw new InvalidOperationException("this VRChat SDK doesn't list its build steps where Orbiters looks for them");
            var steps = new List<Step>();
            foreach (var callback in callbacks)
            {
                if (callback == null) continue;
                var type = callback.GetType();
                if (LeftOut(type) || skip != null && skip(type)) continue;
                int order;
                try { order = callback.callbackOrder; }
                catch (Exception) { continue; }
                steps.Add(new Step(type, order, Runner(callback)));
            }
            return steps.OrderBy(s => s.Order).ToList();
        }

        /// <summary>Whether a hidden build always leaves this build step out (see the lists above).</summary>
        internal static bool LeftOut(Type type) => Left.Contains(type.FullName) || Locks.Contains(type.Name);

        /// <summary>
        /// What makes VRCFury stop a build of <paramref name="source"/> with a dialog when it isn't an upload, seen before
        /// the build: more contacts or expression parameters than VRChat allows (an upload fails on them), and, with
        /// Android or iOS as the build target, desktop shaders on the avatar's own renderers (VRCFury removes them and asks
        /// whether the target was switched by mistake). Empty when VRCFury doesn't build the avatar. A hidden build of it
        /// is refused; the issues say why.
        /// </summary>
        public static List<AvatarIssue> Blockers(GameObject source)
        {
            var issues = new List<AvatarIssue>();
            if (source == null || !VrcFury.Builds(source)) return issues;
            int contacts = source.GetComponentsInChildren<ContactBase>(true).Length;
            if (contacts > MaxContacts)
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{source.name}” has {contacts} contacts, over VRChat's {MaxContacts}",
                    "VRChat refuses the upload. Remove contacts (or the DPS/SPS items that add them).", source.name));
            var descriptor = source.GetComponent<VRCAvatarDescriptor>();
            int parameters = descriptor != null && descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null
                ? descriptor.expressionParameters.parameters.Length : 0;
            if (parameters > MaxParameters)
                issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{source.name}” has {parameters} expression parameters, over VRChat's {MaxParameters}",
                    "VRChat refuses the upload, synced or not. Remove parameters nothing uses.", "Parameters"));
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS)
            {
                var desktop = source.transform.Cast<Transform>().Where(t => t.gameObject.activeSelf).SelectMany(t => t.GetComponents<Renderer>())
                    .SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader != null && !m.shader.name.StartsWith("VRChat/Mobile/", StringComparison.Ordinal))
                    .Select(m => m.name).Distinct().ToList();
                if (desktop.Count > 0)
                    issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{source.name}” uses desktop shaders, and the build target is {EditorUserBuildSettings.activeBuildTarget}",
                        "An upload now removes these materials. Switch the build target to PC in the VRChat SDK panel, or give the Quest version VRChat's mobile " +
                        "shaders (" + string.Join(", ", desktop.Take(3)) + (desktop.Count > 3 ? "..." : "") + ").", source.name));
            }
            return issues;
        }

        // VRCFury's steps run without the dialog its error boundary opens: their errors stop the build like any other's.
        private static Func<GameObject, bool> Runner(IVRCSDKPreprocessAvatarCallback callback)
        {
            var preprocessor = VrcFury.Find("VF.Hooks.VrcfAvatarPreprocessor");
            var wrapper = VrcFury.Find("VF.Utils.VFGameObject");
            if (preprocessor == null || wrapper == null || !preprocessor.IsInstanceOfType(callback)) return callback.OnPreprocessAvatar;
            var process = callback.GetType().GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { wrapper }, null);
            var convert = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == wrapper && m.GetParameters()[0].ParameterType == typeof(GameObject));
            if (process == null || convert == null) return callback.OnPreprocessAvatar;
            return avatar =>
            {
                process.Invoke(callback, new[] { convert.Invoke(null, new object[] { avatar }) });
                return true;
            };
        }

        private void Begin()
        {
            scene = EditorSceneManager.NewPreviewScene();
            // Made hidden and moved at once: an object made in the open scene would mark it changed.
            var holder = EditorUtility.CreateGameObjectWithHideFlags("Hidden build", HideFlags.HideAndDontSave);
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(Source, holder.transform);
            copy.name = Source.name;
            copy.transform.SetParent(null, false);
            Object.DestroyImmediate(holder);
            // The SDK panel lists every avatar whose object isn't flagged so: an upload must never pick the copy.
            copy.hideFlags = HideFlags.NotEditable;
            copy.SetActive(true);
            made = Copy = copy;
            before.UnionWith(InMemory(copy));
            VrcFury.LeaveWriteDefaults(copy);
        }

        private static void TickAll()
        {
            // Not while scripts compile or assets import: a reload may follow, and the steps read the project.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            foreach (var build in Open.ToArray()) build.Run();
        }

        /// <summary>Runs every step now, in this one call (the editor does on its next frame after <see cref="Start"/>).</summary>
        internal void Run()
        {
            if (Finished || started) return;
            started = true;
            Exception error = null;
            bool cancelled = false;
            var existing = new HashSet<string>(Folders());
            try
            {
                for (; Done < steps.Count; Done++)
                {
                    if (Copy == null) throw new InvalidOperationException("a build step removed the copy");
                    var step = steps[Done];
                    Building = Owner(step.Type);
                    if (EditorUtility.DisplayCancelableProgressBar("Building “" + Copy.name + "” as an upload would",
                            Building + " (" + (Done + 1) + " of " + steps.Count + ")", (float)Done / Math.Max(1, steps.Count)))
                    {
                        cancelled = true;
                        break;
                    }
                    if (!step.Run(Copy)) throw new InvalidOperationException(Building + " stopped the build (" + step.Type.Name + ")");
                }
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                EditorUtility.ClearProgressBar();
                // Nothing else ran meanwhile: the new folders are this build's.
                folders.AddRange(Folders().Where(f => !existing.Contains(f)));
            }
            if (error != null) Fail(error);
            else if (cancelled) { Cancelled = true; End("it was cancelled", false); }
            else
            {
                Finished = true;
                Building = null;
                Notify();
            }
        }

        private static string[] Folders() => AssetDatabase.IsValidFolder(VrcFuryBuilds) ? AssetDatabase.GetSubFolders(VrcFuryBuilds) : Array.Empty<string>();

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

        private void Fail(Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Debug.LogException(inner);
            End(Building != null && !Finished ? Building + ": " + inner.Message : inner.Message, false);
        }

        private void Notify()
        {
            if (notified) return;
            notified = true;
            try { Completed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>
        /// Closes the copy and frees what the build steps made for it: in memory (controllers, clips, menus, meshes, materials,
        /// textures) and in the project (VRCFury's build folder, the build cache). A build still running stops:
        /// <see cref="Completed"/> runs with <see cref="Cancelled"/> set.
        /// </summary>
        public void Release()
        {
            if (!Finished) Cancelled = true;
            End("it was stopped", false);
        }

        // Stops a running build with this error (or keeps a finished one's), closes the copy, then tells the caller once.
        private void End(string error, bool deferAssets)
        {
            bool running = !Finished;
            if (running)
            {
                Finished = true;
                Error ??= error;
            }
            Building = null;
            Forget(this);
            Close(deferAssets || deferring);
            if (running) Notify();
        }

        private void Close(bool deferAssets)
        {
            var copy = Copy;
            Copy = null;
            if (copy != null)
            {
                // Objects kept alive on purpose (caches flag theirs not to unload) and those the avatar already used stay.
                foreach (var value in InMemory(copy))
                    if (!before.Contains(value) && !(value is Shader) && (value.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0) Object.DestroyImmediate(value);
            }
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            scene = default;
            before.Clear();
            // The build cache's assets: deleted now, or by the cache itself once it sees the copy gone (after a reload, its
            // leftovers at the next start).
            if (!ReferenceEquals(made, null))
            {
                if (!deferAssets) AttachmentAnimationBuild.Release(made);
                made = null;
            }
            if (folders.Count > 0)
            {
                DeleteFolders(folders, deferAssets);
                folders.Clear();
            }
        }

        // What the copy uses that lives in memory only, not its own objects: read from its components' references, then from
        // what those reference. EditorUtility.CollectDependencies leaves out every object that isn't an asset.
        private static HashSet<Object> InMemory(GameObject copy)
        {
            var found = new HashSet<Object>();
            var queue = new Queue<Object>();
            void Add(Object value)
            {
                if (value != null && !(value is GameObject) && !(value is Component) && !EditorUtility.IsPersistent(value) && found.Add(value))
                    queue.Enqueue(value);
            }
            foreach (var component in copy.GetComponentsInChildren<Component>(true)) if (component != null) queue.Enqueue(component);
            while (queue.Count > 0)
            {
                var owner = queue.Dequeue();
                // Leaves: nothing to follow, and reading their data (vertices, pixels, samples) would cost the most.
                if (owner is Mesh || owner is Texture || owner is Shader || owner is AudioClip) continue;
                if (owner is AnimationClip clip)
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding)) Add(key.value);
                    continue;
                }
                using (var serialized = new SerializedObject(owner))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference) Add(property.objectReferenceValue);
                }
            }
            return found;
        }

        private static void Forget(HiddenAvatarBuild build)
        {
            if (!Open.Remove(build) || Open.Count > 0) return;
            EditorApplication.update -= TickAll;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseOnReload;
            EditorApplication.playModeStateChanged -= ReleaseOnPlay;
        }

        // A build left alive across a script reload hung Unity once: every build goes first. Deleting assets now is not
        // safe: that waits for the reload to end.
        private static void ReleaseOnReload() => ReleaseAll("scripts reloaded");

        private static void ReleaseOnPlay(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) ReleaseAll("Play Mode started");
        }

        internal static void ReleaseAll(string why)
        {
            deferring = true;
            try { foreach (var build in Open.ToArray()) build.End(why, true); }
            finally { deferring = false; }
        }

        // ---- VRCFury's build folders, deleted once the editor can ----

        private static void DeleteFolders(IEnumerable<string> paths, bool later)
        {
            var pending = Leftovers().Concat(paths).Distinct().ToList();
            if (later || !CanDelete())
            {
                SessionState.SetString(LeftoversKey, string.Join("\n", pending));
                EditorApplication.update -= DeleteLeftovers;
                EditorApplication.update += DeleteLeftovers;
                return;
            }
            SessionState.EraseString(LeftoversKey);
            foreach (var path in pending) DeleteFolder(path);
        }

        [InitializeOnLoadMethod]
        private static void ResumeLeftovers()
        {
            if (Leftovers().Count > 0) EditorApplication.update += DeleteLeftovers;
        }

        private static void DeleteLeftovers()
        {
            if (!CanDelete()) return;
            EditorApplication.update -= DeleteLeftovers;
            var paths = Leftovers();
            SessionState.EraseString(LeftoversKey);
            foreach (var path in paths) DeleteFolder(path);
        }

        private static List<string> Leftovers() =>
            SessionState.GetString(LeftoversKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static bool CanDelete() =>
            !deferring && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        // Only VRCFury's own build folders, while they exist.
        private static void DeleteFolder(string path)
        {
            if (path.StartsWith(VrcFuryBuilds + "/", StringComparison.Ordinal) && path.IndexOf("..", StringComparison.Ordinal) < 0 &&
                AssetDatabase.IsValidFolder(path))
                AssetDatabase.DeleteAsset(path);
        }
    }
}
