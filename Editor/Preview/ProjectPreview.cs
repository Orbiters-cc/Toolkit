using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Preview
{
    /// <summary>
    /// Keeps a small ready-to-draw copy of the scene last saved, so that Orbiters - N4N0 shows the project in 3D before
    /// it is opened (contracts' Docs/project-preview.md): posed meshes, base-colour pictures and where everything stands,
    /// in &lt;project&gt;/UserSettings/Orbiters/Previews/&lt;scene GUID&gt;.preview. Written a few seconds after a scene is saved,
    /// after an avatar upload, and once after the editor loads when the open scene has none or an older one.
    /// The capture never holds the editor: it reads the scene in steps of a few milliseconds on
    /// <see cref="EditorApplication.update"/> (an editor in the background never repaints, so not delayCall), one mesh
    /// copied or one skinned mesh baked or one picture drawn through the GPU per step, the pictures read back
    /// asynchronously; then a worker thread encodes the pictures and writes the file. It changes nothing in the scene,
    /// its assets or the Undo history.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectPreview
    {
        /// <summary>What the last capture did (diagnostics: status, file, cost).</summary>
        public static string LastResult { get; private set; } = "none";

        /// <summary>A capture is running.</summary>
        public static bool Busy => capture != null;

        internal const int Keep = 4;
        private const double SaveDelay = 3;
        private const double CatchUpDelay = 20;
        private const double StepBudgetMs = 4;

        private static Capture capture;
        private static string pendingScene;
        private static string pendingReason;
        private static double due;
        private static double catchUpAt;

        internal static string Folder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "UserSettings", "Orbiters", "Previews");

        static ProjectPreview()
        {
            if (Application.isBatchMode) return;
            EditorSceneManager.sceneSaved += scene => Request(scene.path, "save");
            EditorApplication.update += Tick;
            catchUpAt = EditorApplication.timeSinceStartup + CatchUpDelay;
        }

        /// <summary>Captures the open scene in a few seconds (an avatar was uploaded from it, say).</summary>
        public static void RequestActiveScene(string reason) => Request(SceneManager.GetActiveScene().path, reason);

        /// <summary>Captures the open scene now (tests, diagnostics); false when one is running or there is none.</summary>
        public static bool CaptureNow(string reason)
        {
            if (capture != null) return false;
            var scene = SceneManager.GetActiveScene();
            if (!Capturable(scene)) return false;
            Start(scene, reason);
            return true;
        }

        /// <summary>The preview file of a scene asset.</summary>
        internal static string FileOf(string scenePath) => Path.Combine(Folder, AssetDatabase.AssetPathToGUID(scenePath) + ".preview");

        private static void Request(string scenePath, string reason)
        {
            if (string.IsNullOrEmpty(scenePath)) return;
            pendingScene = scenePath;
            pendingReason = reason;
            due = EditorApplication.timeSinceStartup + SaveDelay;
        }

        private static bool Capturable(Scene scene) =>
            scene.IsValid() && scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene) &&
            !string.IsNullOrEmpty(scene.path) && scene.path.StartsWith("Assets/", StringComparison.Ordinal);

        private static bool EditorBusy() =>
            EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer;

        private static void Tick()
        {
            if (capture != null)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) { capture.Abort("Play Mode"); capture = null; return; }
                if (!capture.Step()) capture = null;
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (catchUpAt > 0 && now >= catchUpAt && !EditorBusy())
            {
                catchUpAt = 0;
                var active = SceneManager.GetActiveScene();
                if (Capturable(active) && pendingScene == null)
                {
                    string file = FileOf(active.path);
                    string scene = Path.GetFullPath(active.path);
                    if (!File.Exists(file) || File.Exists(scene) && File.GetLastWriteTimeUtc(file) < File.GetLastWriteTimeUtc(scene))
                        Request(active.path, "load");
                }
            }
            if (pendingScene == null || now < due || EditorBusy()) return;
            string path = pendingScene;
            string reason = pendingReason;
            pendingScene = null;
            var target = SceneManager.GetSceneByPath(path);
            if (Capturable(target)) Start(target, reason);
        }

        private static void Start(Scene scene, string reason)
        {
            try { capture = new Capture(scene, reason); }
            catch (Exception ex)
            {
                capture = null;
                LastResult = "failed: " + ex.Message;
                Debug.LogWarning("Orbiters Toolkit could not start the 3D preview Orbiters - N4N0 shows: " + ex.Message);
            }
        }

        internal static void Finished(string result) => LastResult = result;

        /// <summary>One capture: its steps on the main thread, then its worker.</summary>
        private sealed class Capture
        {
            private readonly PreviewData data = new PreviewData();
            private readonly string path;
            private readonly Stopwatch total = Stopwatch.StartNew();
            private readonly List<Renderer> renderers = new List<Renderer>();
            private readonly Dictionary<Transform, int> avatarOf = new Dictionary<Transform, int>();
            private readonly Dictionary<int, int> meshes = new Dictionary<int, int>();
            private readonly Dictionary<int, int> materials = new Dictionary<int, int>();
            private readonly Dictionary<int, int> textures = new Dictionary<int, int>();
            private readonly List<Texture> textureSources = new List<Texture>();
            private readonly List<bool> textureAlpha = new List<bool>();
            private readonly List<Pending> readbacks = new List<Pending>();
            private Mesh baked;
            /// <summary>A skinned renderer baked in the last step, its copy taken in the next.</summary>
            private SkinnedMeshRenderer bakedFor;
            private int next, nextTexture, textureSide;
            private long triangles;
            private Task<string> worker;

            private sealed class Pending
            {
                public int index;
                public RenderTexture target;
                public AsyncGPUReadbackRequest request;
            }

            public Capture(Scene scene, string reason)
            {
                path = FileOf(scene.path);
                data.Reason = reason;
                data.SceneName = scene.name;
                data.ScenePath = scene.path;
                data.SceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
                data.Unity = Application.unityVersion;
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProjectPreview).Assembly);
                data.Generator = "Orbiters Toolkit" + (package != null ? " " + package.version : "");
            }

            /// <summary>Runs steps for a few milliseconds; false once done (or failed).</summary>
            public bool Step()
            {
                try
                {
                    if (worker != null) return Wait();
                    var watch = Stopwatch.StartNew();
                    do
                    {
                        if (!NextStep()) break;
                    } while (watch.Elapsed.TotalMilliseconds < StepBudgetMs && worker == null);
                    double ms = watch.Elapsed.TotalMilliseconds;
                    data.MainThreadMs += ms;
                    data.LongestStepMs = Math.Max(data.LongestStepMs, ms);
                    data.Steps++;
                    return true;
                }
                catch (Exception ex)
                {
                    Abort(ex.Message);
                    Debug.LogWarning("Orbiters Toolkit could not write the 3D preview Orbiters - N4N0 shows: " + ex.Message);
                    return false;
                }
            }

            public void Abort(string why)
            {
                Release();
                Finished("stopped: " + why);
            }

            // One unit of work; false when this frame should end (waiting on the GPU).
            private bool NextStep()
            {
                if (next == 0)
                {
                    Collect();
                    next = 1;
                    return true;
                }
                if (bakedFor != null)
                {
                    var r = bakedFor;
                    bakedFor = null;
                    Record(r, baked, r.transform.localToWorldMatrix, true);
                    return true;
                }
                if (next <= renderers.Count)
                {
                    Visit(renderers[next - 1]);
                    next++;
                    return true;
                }
                if (nextTexture < textureSources.Count)
                {
                    if (textureSide == 0) textureSide = textureSources.Count <= 8 ? 1024 : textureSources.Count <= 32 ? 512 : 256;
                    Draw(nextTexture++);
                    return true;
                }
                // The pictures coming back from the GPU.
                for (int i = readbacks.Count - 1; i >= 0; i--)
                {
                    var r = readbacks[i];
                    r.request.Update();
                    if (!r.request.done) continue;
                    if (!r.request.hasError) data.Textures[r.index].Pixels = r.request.GetData<byte>().ToArray();
                    RenderTexture.ReleaseTemporary(r.target);
                    readbacks.RemoveAt(i);
                }
                if (readbacks.Count > 0) return false;
                Release();
                data.TotalMsBeforeWorker = total.Elapsed.TotalMilliseconds;
                var d = data;
                string file = path;
                worker = Task.Run(() => PreviewFile.Write(d, file));
                return false;
            }

            private bool Wait()
            {
                if (!worker.IsCompleted) return true;
                if (worker.IsFaulted)
                {
                    var ex = worker.Exception?.GetBaseException();
                    Finished("failed: " + ex?.Message);
                    Debug.LogWarning("Orbiters Toolkit could not write the 3D preview Orbiters - N4N0 shows: " + ex?.Message);
                }
                else Finished(worker.Result);
                return false;
            }

            private void Release()
            {
                foreach (var r in readbacks)
                {
                    r.request.WaitForCompletion();
                    RenderTexture.ReleaseTemporary(r.target);
                }
                readbacks.Clear();
                if (baked != null) Object.DestroyImmediate(baked);
                baked = null;
            }

            // What the scene shows: its avatars, its world descriptor, its renderers in the order the budget takes them,
            // and the Scene view's camera.
            private void Collect()
            {
                var loaded = new HashSet<Scene>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && !EditorSceneManager.IsPreviewScene(s)) loaded.Add(s);
                }
                foreach (var avatar in Find("VRC.SDKBase.VRC_AvatarDescriptor").Where(c => loaded.Contains(c.gameObject.scene)))
                {
                    var t = avatar.transform;
                    avatarOf[t] = data.Avatars.Count;
                    data.Avatars.Add(new PreviewData.AvatarPlace { Name = t.name, Id = BlueprintId(avatar.gameObject), Position = t.position, Forward = t.forward });
                }
                data.Kind = data.Avatars.Count > 0 ? "avatars" : Find("VRC.SDKBase.VRC_SceneDescriptor").Any(c => loaded.Contains(c.gameObject.scene)) ? "world" : "scene";
                var editorOnly = new Dictionary<Transform, bool>();
                var all = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                    .Where(r => r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.gameObject.activeInHierarchy &&
                                (r.gameObject.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) == 0 && loaded.Contains(r.gameObject.scene) &&
                                !EditorOnly(r.transform, editorOnly))
                    .ToList();
                data.Renderers = all.Count;
                // Avatars first, then the largest.
                renderers.AddRange(all.OrderBy(r => AvatarOf(r.transform) < 0 ? 1 : 0).ThenByDescending(r => Volume(r.bounds)));
                var view = SceneView.lastActiveSceneView;
                if (view != null && view.camera != null)
                {
                    var t = view.camera.transform;
                    data.Camera = new PreviewData.SceneCamera { Position = t.position, Rotation = t.rotation, Pivot = view.pivot, FovY = view.camera.fieldOfView };
                }
            }

            private static float Volume(Bounds b) => Mathf.Max(b.size.x, 0.01f) * Mathf.Max(b.size.y, 0.01f) * Mathf.Max(b.size.z, 0.01f);

            private static bool EditorOnly(Transform t, Dictionary<Transform, bool> known)
            {
                if (t == null) return false;
                if (known.TryGetValue(t, out bool v)) return v;
                v = t.CompareTag("EditorOnly") || EditorOnly(t.parent, known);
                known[t] = v;
                return v;
            }

            private int AvatarOf(Transform t)
            {
                for (; t != null; t = t.parent)
                    if (avatarOf.TryGetValue(t, out int i)) return i;
                return -1;
            }

            // The components of a VRChat SDK type (by name: this assembly compiles without the SDK).
            private static IEnumerable<Component> Find(string typeName)
            {
                var type = TypeCache.GetTypesDerivedFrom<MonoBehaviour>().FirstOrDefault(t => t.FullName == typeName);
                if (type == null) return Enumerable.Empty<Component>();
                return Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None).OfType<Component>();
            }

            private static string BlueprintId(GameObject go)
            {
                var pipeline = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().FullName == "VRC.Core.PipelineManager");
                if (pipeline == null) return null;
                var field = pipeline.GetType().GetField("blueprintId", BindingFlags.Public | BindingFlags.Instance);
                string id = field?.GetValue(pipeline) as string;
                return string.IsNullOrEmpty(id) ? null : id;
            }

            // A renderer: its mesh (stored once; a skinned one baked as posed, its copy taken in the next step), materials
            // and pictures. One whose materials all draw nothing a person sees (utility shaders) is left out.
            private void Visit(Renderer r)
            {
                if (r == null) return;
                if (r.sharedMaterials.All(m => MaterialIndex(m) < 0)) { data.Skip("shader"); return; }
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null) { data.Skip("missing"); return; }
                    if (triangles + Triangles(smr.sharedMesh) > PreviewData.TriangleBudget) { data.Skip("budget"); return; }
                    if (baked == null) baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    // With its transform's scale, so that its matrix is the transform's (without, a scaled renderer
                    // comes out 1/scale too large).
                    smr.BakeMesh(baked, true);
                    bakedFor = smr;
                    return;
                }
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) { data.Skip("missing"); return; }
                Record(r, mesh, r.transform.localToWorldMatrix, false);
            }

            // The renderer's draw of `mesh` (copied unless a renderer before drew the same static mesh).
            private void Record(Renderer r, Mesh mesh, Matrix4x4 matrix, bool skinned)
            {
                var mats = r.sharedMaterials;
                int meshIndex;
                if (skinned || !meshes.TryGetValue(mesh.GetInstanceID(), out meshIndex))
                {
                    long count = Triangles(mesh);
                    if (count == 0) { data.Skip("topology"); return; }
                    if (triangles + count > PreviewData.TriangleBudget) { data.Skip("budget"); return; }
                    var copy = Read(mesh);
                    if (copy == null) { data.Skip("missing"); return; }
                    triangles += count;
                    meshIndex = data.Meshes.Count;
                    data.Meshes.Add(copy);
                    if (!skinned) meshes[mesh.GetInstanceID()] = meshIndex;
                }
                var stored = data.Meshes[meshIndex];
                var draw = new PreviewData.Draw
                {
                    Instance = data.Matrices.Count,
                    Mesh = meshIndex,
                    Shadows = r.shadowCastingMode != ShadowCastingMode.Off,
                    Avatar = AvatarOf(r.transform),
                    Materials = new int[Math.Min(stored.Submeshes.Count, mats.Length)],
                };
                for (int i = 0; i < draw.Materials.Length; i++) draw.Materials[i] = stored.Submeshes[i].count > 0 ? MaterialIndex(mats[i]) : -1;
                data.Matrices.Add(matrix);
                data.Draws.Add(draw);
            }

            private static long Triangles(Mesh m)
            {
                long n = 0;
                for (int i = 0; i < m.subMeshCount; i++)
                    if (m.GetTopology(i) == MeshTopology.Triangles) n += (long)m.GetIndexCount(i) / 3;
                return n;
            }

            // The mesh's vertices, normals, first UVs and triangle indices (a submesh of other topology stays empty).
            private static PreviewData.Mesh Read(Mesh m)
            {
                try
                {
                    var copy = new PreviewData.Mesh { Positions = m.vertices, Normals = m.normals, Uvs = m.uv };
                    var indices = new List<int>();
                    var sub = new List<int>();
                    for (int i = 0; i < m.subMeshCount; i++)
                    {
                        int start = indices.Count;
                        if (m.GetTopology(i) == MeshTopology.Triangles)
                        {
                            m.GetIndices(sub, i, true);
                            indices.AddRange(sub);
                        }
                        copy.Submeshes.Add((start, indices.Count - start));
                    }
                    copy.Indices = indices.ToArray();
                    return copy.Positions.Length > 0 ? copy : null;
                }
                catch (Exception) { return null; }
            }

            // A material's index in the preview, -1 for none or one that shows no surface: a shader with neither a main
            // colour nor a main texture (N4N0's picture probes, depth and utility shaders). Not by name: Poiyomi's locked
            // shaders are all "Hidden/Locked/...".
            private int MaterialIndex(Material m)
            {
                if (m == null) return -1;
                if (materials.TryGetValue(m.GetInstanceID(), out int index)) return index;
                var shader = m.shader;
                string colorName = Flagged(shader, ShaderPropertyFlags.MainColor) ?? (m.HasProperty("_Color") ? "_Color" : m.HasProperty("_BaseColor") ? "_BaseColor" : null);
                string textureName = Flagged(shader, ShaderPropertyFlags.MainTexture) ?? (m.HasProperty("_MainTex") ? "_MainTex" : m.HasProperty("_BaseMap") ? "_BaseMap" : null);
                if (shader == null || colorName == null && textureName == null)
                {
                    materials[m.GetInstanceID()] = -1;
                    return -1;
                }
                int queue = m.renderQueue;
                var material = new PreviewData.Material
                {
                    Name = m.name,
                    Color = colorName != null ? m.GetColor(colorName) : Color.white,
                    Alpha = queue >= 3000 ? "blend" : queue >= 2450 ? "cutout" : "opaque",
                    Cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f,
                    Cull = !m.HasProperty("_Cull") ? "back" : Mathf.RoundToInt(m.GetFloat("_Cull")) == 0 ? "off" : Mathf.RoundToInt(m.GetFloat("_Cull")) == 1 ? "front" : "back",
                    Texture = -1,
                    Tiling = Vector2.one,
                };
                var texture = textureName != null ? m.GetTexture(textureName) : null;
                if (texture != null && texture.dimension == TextureDimension.Tex2D)
                {
                    material.Tiling = m.GetTextureScale(textureName);
                    material.Offset = m.GetTextureOffset(textureName);
                    bool alpha = material.Alpha != "opaque" && GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat);
                    if (!textures.TryGetValue(texture.GetInstanceID(), out int t))
                    {
                        t = textureSources.Count;
                        textures[texture.GetInstanceID()] = t;
                        textureSources.Add(texture);
                        textureAlpha.Add(alpha);
                        data.Textures.Add(new PreviewData.Picture());
                    }
                    else if (alpha) textureAlpha[t] = true;
                    material.Texture = t;
                }
                index = data.Materials.Count;
                data.Materials.Add(material);
                materials[m.GetInstanceID()] = index;
                return index;
            }

            private static string Flagged(Shader shader, ShaderPropertyFlags flag)
            {
                if (shader == null) return null;
                int n = shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                    if ((shader.GetPropertyFlags(i) & flag) != 0) return shader.GetPropertyName(i);
                return null;
            }

            // A picture drawn through the GPU at the preview's size (its aspect kept, never larger than it is) into an
            // sRGB target: the bytes come back as Unity shows the colours. Read back without waiting.
            private void Draw(int index)
            {
                var source = textureSources[index];
                var picture = data.Textures[index];
                float scale = Mathf.Min(1f, textureSide / (float)Mathf.Max(source.width, source.height));
                int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
                picture.Width = w;
                picture.Height = h;
                picture.Png = textureAlpha[index];
                var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, target);
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    readbacks.Add(new Pending { index = index, target = target, request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32) });
                    return;
                }
                var active = RenderTexture.active;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    copy.Apply();
                    picture.Pixels = copy.GetRawTextureData();
                }
                finally
                {
                    RenderTexture.active = active;
                    RenderTexture.ReleaseTemporary(target);
                    Object.DestroyImmediate(copy);
                }
            }
        }
    }
}
