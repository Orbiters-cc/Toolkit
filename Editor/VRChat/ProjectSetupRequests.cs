using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Applies the setup requests Orbiters - N4N0 leaves in this project (contracts' Docs/project-setup.md): "add N4N0 to
    /// avtr_… in Canine.unity". It asks once (<see cref="SetupRequestWindow"/>, a small window that blocks nothing: the
    /// request waits until it is answered), opens the scene, finds the avatar by its blueprint id, has My Avatar install
    /// each gallery item (<see cref="Gallery"/>; My Avatar's installer may ask its own questions in its window and survives
    /// domain reloads), then saves the scene and opens the VRChat SDK's builder on the avatar, and writes the result for
    /// the desktop. A request being worked on keeps its progress in &lt;id&gt;.state.json so a reload picks it up again.
    /// The folder is read every 2 s from an update timer, as an editor in the background never repaints.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetupRequests
    {
        /// <summary>What My Avatar does for a request's gallery items (it registers itself on load).</summary>
        public interface IGalleryItems
        {
            /// <summary>Starts installing the asset (null version: its latest release) on the avatar; returns a job id or throws with a reason a person reads.</summary>
            string Start(GameObject avatar, int assetId, string version);
            /// <summary>"running", "done" or "failed", what to tell (the failure, or "N4N0 1.0.14") and the version installed.</summary>
            (string state, string message, string version) Status(string jobId);
            /// <summary>Shows the job in My Avatar's window (its questions are answered there).</summary>
            void Show(string jobId);
        }

        /// <summary>Set by My Avatar; null when My Avatar is not in the project.</summary>
        public static IGalleryItems Gallery { get; set; }

        /// <summary>
        /// Asks the person whether to apply the request at a path (with its id): <see cref="SetupRequestWindow"/>, which
        /// answers through <see cref="Answer"/>; tests answer without one.
        /// </summary>
        internal static Action<string, string> Ask = SetupRequestWindow.Ask;

        internal const int Schema = 1;
        private const double Interval = 2;
        private static readonly TimeSpan KeepResults = TimeSpan.FromDays(7);
        private static double next;

        internal static string Folder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "UserSettings", "Orbiters", "SetupRequests");

        static ProjectSetupRequests()
        {
            if (Application.isBatchMode) return;
            EditorApplication.update += Tick;
        }

        private sealed class State
        {
            public int item;
            public string job;
            public List<JObject> installed = new List<JObject>();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + Interval;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            string folder = Folder;
            if (!Directory.Exists(folder)) return;
            try
            {
                foreach (string path in Directory.GetFiles(folder, "*.json").OrderBy(File.GetLastWriteTimeUtc))
                {
                    string name = Path.GetFileName(path);
                    if (name.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > KeepResults) File.Delete(path);
                        continue;
                    }
                    if (name.EndsWith(".state.json", StringComparison.OrdinalIgnoreCase)) continue;
                    Handle(path);
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Handle(string path)
        {
            JObject request;
            try { request = JObject.Parse(File.ReadAllText(path)); }
            catch (JsonException) { return; }
            catch (IOException) { return; }
            string id = (string)request["id"] ?? Path.GetFileNameWithoutExtension(path);
            if ((int?)request["schema"] != Schema) return;
            string statePath = Path.Combine(Folder, id + ".state.json");
            var state = File.Exists(statePath) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(statePath)) : null;
            try
            {
                // Not answered yet: asked (once a window), the request waits; Answer starts it or declines it.
                if (state == null)
                {
                    Ask(path, id);
                    return;
                }
                Continue(path, statePath, id, request, state);
            }
            catch (Exception ex)
            {
                Finish(path, statePath, id, "failed", ex is SetupException ? ex.Message : "Unity could not apply the request: " + ex.Message, state?.installed);
                if (!(ex is SetupException)) Debug.LogException(ex);
            }
        }

        /// <summary>
        /// The person's answer to the request at <paramref name="path"/>: Add starts it at the next tick (its state file),
        /// Not now writes the result "declined". A request no longer waiting (answered, withdrawn, not in the folder) is left alone.
        /// </summary>
        internal static void Answer(string path, string id, bool add)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(id) || !File.Exists(path)) return;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) return;
            string statePath = Path.Combine(Folder, id + ".state.json");
            if (File.Exists(statePath)) return;
            try
            {
                if (add) Save(statePath, new State());
                else Finish(path, statePath, id, "declined", "Not added: you chose Not now in Unity.", null);
            }
            catch (IOException e) { Debug.LogWarning("Orbiters Toolkit could not answer Orbiters - N4N0's request: " + e.Message); }
            catch (UnauthorizedAccessException e) { Debug.LogWarning("Orbiters Toolkit could not answer Orbiters - N4N0's request: " + e.Message); }
            next = 0;
        }

        private static void Continue(string path, string statePath, string id, JObject request, State state)
        {
            var items = ((JArray)request["items"] ?? new JArray()).Cast<JObject>().ToList();
            // The items' asset ids are that server's: a development id installed from production is another item.
            string server = (string)request["server"];
            if (!string.IsNullOrEmpty(server) && !SameServer(server, OrbitersEnvironment.ApiUrl()))
                throw new SetupException(OrbitersEnvironment.IsDevelopment
                    ? "Orbiters - N4N0 uses the production server but Unity's Orbiters tools use the development one: switch them to production (Orbiters settings) and ask again."
                    : "Orbiters - N4N0 uses the development server but Unity's Orbiters tools use production: switch them to development (Orbiters settings) and ask again.");
            string avatarId = (string)request["avatar"]?["id"];
            if (string.IsNullOrEmpty(avatarId)) throw new SetupException("The request names no avatar.");
            var avatar = FindAvatar(avatarId, (string)request["avatar"]?["scene"]);
            if (avatar == null) throw new SetupException($"{(string)request["avatar"]?["name"] ?? avatarId} is not in this project any more.");

            while (state.item < items.Count)
            {
                var item = items[state.item];
                if ((string)item["kind"] != "gallery") throw new SetupException($"Unity does not know how to add \"{(string)item["kind"]}\": update Orbiters Toolkit.");
                if (Gallery == null) throw new SetupException("My Avatar is not installed in this project: add it from Orbiters - N4N0 first.");
                if (state.job == null)
                {
                    state.job = Gallery.Start(avatar, (int)item["assetId"], (string)item["version"]);
                    Save(statePath, state);
                    Gallery.Show(state.job);
                    return;
                }
                var (status, message, version) = Gallery.Status(state.job);
                if (status == "running") return;
                if (status != "done") throw new SetupException(message ?? "My Avatar could not add " + ((string)item["name"] ?? "the item") + ".");
                state.installed.Add(new JObject { ["assetId"] = (int)item["assetId"], ["version"] = version });
                state.job = null;
                state.item++;
                Save(statePath, state);
            }

            foreach (var step in ((JArray)request["then"] ?? new JArray()).Select(t => (string)t))
            {
                if (step == "save-scene") EditorSceneManager.SaveScene(avatar.scene);
                else if (step == "open-build-panel") OpenBuilder(avatar);
            }
            string names = string.Join(", ", items.Select((item, i) => ((string)item["name"] ?? "Item") + (i < state.installed.Count ? " " + (string)state.installed[i]["version"] : "")));
            Finish(path, statePath, id, "done", $"{names} added to {avatar.name}.", state.installed);
        }

        // The avatar root whose PipelineManager has this blueprint id: in the open scenes, then the request's scene, then
        // every scene of the project (each opened in turn, the person asked to save changes first, as Unity does).
        internal static GameObject FindAvatar(string blueprintId, string scenePath)
        {
            var found = InOpenScenes(blueprintId);
            if (found != null) return found;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(scenePath) && File.Exists(scenePath)) candidates.Add(scenePath);
            candidates.AddRange(AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p != scenePath && SceneMentions(p, blueprintId)));
            foreach (string candidate in candidates)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) throw new SetupException("Not added: the open scene was not saved.");
                EditorSceneManager.OpenScene(candidate, OpenSceneMode.Single);
                found = InOpenScenes(blueprintId);
                if (found != null) return found;
            }
            return null;
        }

        private static bool SameServer(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        private static GameObject InOpenScenes(string blueprintId)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var pipeline in root.GetComponentsInChildren<PipelineManager>(true))
                    if (pipeline.blueprintId == blueprintId) return pipeline.gameObject;
            }
            return null;
        }

        private static bool SceneMentions(string path, string blueprintId)
        {
            try { return File.ReadAllText(path).Contains(blueprintId); }
            catch (IOException) { return false; }
        }

        private static void OpenBuilder(GameObject avatar)
        {
            Selection.activeGameObject = avatar;
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            // The builder exists once the panel has drawn: select the avatar on a later tick (at most ~10 s of ticks).
            int tries = 0;
            void Select()
            {
                if (avatar == null || ++tries > 600) { EditorApplication.update -= Select; return; }
                if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) return;
                EditorApplication.update -= Select;
                try { builder.SelectAvatar(avatar); }
                catch (Exception ex) { Debug.LogWarning("Orbiters Toolkit could not select the avatar in the VRChat SDK panel: " + ex.Message); }
            }
            EditorApplication.update += Select;
        }

        private static void Save(string statePath, State state) => SafePaths.ReplaceFile(statePath, JsonConvert.SerializeObject(state));

        private static void Finish(string path, string statePath, string id, string status, string message, List<JObject> installed)
        {
            var result = new JObject
            {
                ["schema"] = Schema, ["id"] = id, ["status"] = status, ["message"] = message,
                ["finishedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["installed"] = new JArray(installed ?? new List<JObject>()),
            };
            SafePaths.ReplaceFile(Path.Combine(Folder, id + ".result.json"), result.ToString(Formatting.Indented));
            File.Delete(path);
            if (File.Exists(statePath)) File.Delete(statePath);
        }

        private sealed class SetupException : Exception
        {
            public SetupException(string message) : base(message) { }
        }
    }
}
