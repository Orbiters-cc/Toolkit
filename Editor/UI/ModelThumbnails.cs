using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Small pictures of models in the scene (clothing, accessories), framed on the model itself over a dark background.
    /// <see cref="Get"/> never waits: it returns the picture once made and queues it otherwise; one picture is rendered per
    /// editor frame. Pictures are kept for the session and in Library/Orbiters/Thumbnails, by a key the caller changes
    /// when the model does (e.g. its prefab's dependency hash). Keys made of session ids (instance ids) are kept for the
    /// session only (<c>keepOnDisk: false</c>); a caller whose key changed <see cref="Release"/>s the old picture.
    /// </summary>
    public static class ModelThumbnails
    {
        public const int Size = 192;
        /// <summary>The background of every picture (dark grey), for frames that should blend with it.</summary>
        public static readonly Color Background = new Color32(33, 33, 35, 255);

        // Changes when pictures are made differently, so older ones are made again.
        private const string Version = "6";
        private static readonly string Folder = Path.Combine("Library", "Orbiters", "Thumbnails");
        private static readonly Dictionary<string, Texture2D> Made = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, GameObject> Queue = new Dictionary<string, GameObject>();
        // Keys (hashed) of pictures made without a background.
        private static readonly HashSet<string> Clear = new HashSet<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        // Keys (hashed) whose pictures never go to the disk.
        private static readonly HashSet<string> SessionOnly = new HashSet<string>();
        // Pictures on the disk not used for this long are deleted, once per editor session.
        private static readonly TimeSpan KeptOnDisk = TimeSpan.FromDays(30);
        private const string PrunedKey = "Orbiters.Toolkit.Thumbnails.Pruned";
        private static bool pumping, hooked;

        /// <summary>The picture of <paramref name="root"/> for <paramref name="key"/>, or null while it is made.</summary>
        public static Texture2D Get(GameObject root, string key) => Get(root, key, false);

        /// <summary>
        /// The picture of <paramref name="root"/> for <paramref name="key"/>, or null while it is made; with
        /// <paramref name="transparent"/>, without a background (the model alone, its edges blended), for rows and cards
        /// that have their own.
        /// </summary>
        public static Texture2D Get(GameObject root, string key, bool transparent) => Get(root, key, transparent, true);

        /// <summary>
        /// As <see cref="Get(GameObject, string, bool)"/>; with <paramref name="keepOnDisk"/> false the picture is never
        /// saved to or read from the disk (keys that hold instance ids mean nothing after a restart).
        /// </summary>
        public static Texture2D Get(GameObject root, string key, bool transparent, bool keepOnDisk)
        {
            if (root == null || string.IsNullOrEmpty(key)) return null;
            Hook();
            key = KeyOf(key, transparent);
            if (Made.TryGetValue(key, out var texture) && texture != null) return texture;
            if (Failed.Contains(key)) return null;
            if (!keepOnDisk) SessionOnly.Add(key);
            texture = keepOnDisk ? Load(key, transparent) : null;
            if (texture != null) return Made[key] = texture;
            if (transparent) Clear.Add(key);
            Queue[key] = root;
            if (!pumping) { pumping = true; EditorApplication.update += Pump; }
            return null;
        }

        /// <summary>Whether the picture for <paramref name="key"/> could not be made (nothing to show, or an error):
        /// <see cref="Get(GameObject, string, bool)"/> then returns null for good, not while it is made.</summary>
        public static bool HasFailed(string key, bool transparent = false) =>
            !string.IsNullOrEmpty(key) && Failed.Contains(KeyOf(key, transparent));

        /// <summary>Frees the picture made for <paramref name="key"/> (and forgets a request still queued), for a caller
        /// whose key changed: the old picture is no longer shown anywhere.</summary>
        public static void Release(string key, bool transparent = false)
        {
            if (string.IsNullOrEmpty(key)) return;
            key = KeyOf(key, transparent);
            Queue.Remove(key);
            Failed.Remove(key);
            Clear.Remove(key);
            SessionOnly.Remove(key);
            if (Made.TryGetValue(key, out var texture))
            {
                Made.Remove(key);
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        private static string KeyOf(string key, bool transparent) => Hash((transparent ? "clear|" : "") + key);

        // The pictures are native objects a script reload would orphan: freed before it (Load makes them again).
        // Old pictures on the disk are pruned once per editor session.
        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
            if (SessionState.GetBool(PrunedKey, false)) return;
            SessionState.SetBool(PrunedKey, true);
            try
            {
                if (!Directory.Exists(Folder)) return;
                var oldest = DateTime.UtcNow - KeptOnDisk;
                foreach (var file in new DirectoryInfo(Folder).GetFiles("*.png"))
                    if (file.LastWriteTimeUtc < oldest) file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void ReleaseAll()
        {
            foreach (var texture in Made.Values)
                if (texture != null) Object.DestroyImmediate(texture);
            Made.Clear();
            Queue.Clear();
        }

        /// <summary>A key for a model made from an asset: changes when the asset or what it uses changes.</summary>
        public static string KeyFor(string assetPath) =>
            string.IsNullOrEmpty(assetPath) ? null : assetPath + "|" + AssetDatabase.GetAssetDependencyHash(assetPath);

        private static void Pump()
        {
            if (Queue.Count == 0) { EditorApplication.update -= Pump; pumping = false; return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string key = null;
            foreach (var pair in Queue) { key = pair.Key; break; }
            var root = Queue[key];
            Queue.Remove(key);
            try
            {
                var texture = root != null ? Render(root, Clear.Contains(key)) : null;
                if (texture == null) { Failed.Add(key); return; }
                Made[key] = texture;
                if (!SessionOnly.Contains(key)) Save(key, texture);
            }
            catch (Exception ex)
            {
                Failed.Add(key);
                Debug.LogWarning("[Orbiters] Could not make a picture of " + (root != null ? root.name : "a model") + ": " + ex.Message);
            }
        }

        // Baked: a mesh made here (a skinned renderer's pose), destroyed afterwards. Any other mesh is the renderer's own.
        private struct Part { public Mesh Mesh; public Matrix4x4 Matrix; public Material[] Materials; public bool Baked; }

        private static Texture2D Render(GameObject root, bool transparent)
        {
            var parts = new List<Part>();
            var preview = new PreviewRenderUtility();
            try
            {
                var bounds = new Bounds();
                bool any = false;
                // A prop hidden until its menu toggle turns it on (a drawing pen) is pictured as it shows then.
                for (int pass = 0; pass < 2 && !any; pass++)
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(pass == 1))
                {
                    // Only what is worn: not editor helpers (gizmo overlays, previews) hidden in the hierarchy or tagged EditorOnly.
                    if (!renderer.enabled || Helper(renderer.transform, root.transform)) continue;
                    Mesh mesh = null;
                    bool baked = false;
                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        // Baked as posed, with its blendshapes, in the renderer's space.
                        mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        baked = true;
                        skinned.BakeMesh(mesh, true);
                        if (mesh.vertexCount == 0) { Object.DestroyImmediate(mesh); continue; }
                    }
                    else if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null) mesh = filter.sharedMesh;
                    }
                    if (mesh == null || mesh.vertexCount == 0) continue;
                    // A baked mesh already has the renderer's scale.
                    var matrix = renderer is SkinnedMeshRenderer ? Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one) : renderer.localToWorldMatrix;
                    parts.Add(new Part { Mesh = mesh, Matrix = matrix, Materials = renderer.sharedMaterials, Baked = baked });
                    // The renderer's own mesh is read as it is: its bounds may be kept by a tool on purpose (Mixer's
                    // working mesh), and a shared asset must not change.
                    if (baked) mesh.RecalculateBounds();
                    var local = mesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                        if (!any) { bounds = new Bounds(point, Vector3.zero); any = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                if (!any) return null;

                // A three-quarter view from the front of the avatar it is worn on, a little from above.
                var facing = root.transform.root.rotation;
                var direction = facing * new Vector3(0.45f, 0.25f, 1f).normalized;
                float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
                var camera = preview.camera;
                camera.orthographic = true;
                camera.transform.position = bounds.center + direction * (radius * 3f);
                camera.transform.LookAt(bounds.center, facing * Vector3.up);
                float half = 0f;
                foreach (var part in parts)
                foreach (var vertex in part.Mesh.vertices)
                {
                    var point = camera.transform.InverseTransformPoint(part.Matrix.MultiplyPoint3x4(vertex));
                    half = Mathf.Max(half, Mathf.Abs(point.x), Mathf.Abs(point.y));
                }
                camera.orthographicSize = Mathf.Max(half, 0.005f) * 1.12f;
                camera.nearClipPlane = 0.001f;
                camera.farClipPlane = radius * 8f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                preview.ambientColor = new Color(0.95f, 0.95f, 0.97f);
                preview.lights[0].intensity = 1.5f;
                preview.lights[0].transform.rotation = camera.transform.rotation * Quaternion.Euler(30, -35, 0);
                preview.lights[1].intensity = 0.9f;
                preview.lights[1].transform.rotation = camera.transform.rotation * Quaternion.Euler(-10, 150, 0);

                // Without a background: the model over black, then over white. Where both agree it covers the pixel;
                // where they differ by the whole range nothing does. Shaders' own alpha (often odd on avatar shaders)
                // is never read.
                var overBlack = Pass(preview, parts, transparent ? Color.black : Background);
                var texture = new Texture2D(Size, Size, transparent ? TextureFormat.RGBA32 : TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, name = root.name + " picture" };
                if (transparent)
                {
                    var overWhite = Pass(preview, parts, Color.white);
                    for (int i = 0; i < overBlack.Length; i++)
                    {
                        Color b = overBlack[i], w = overWhite[i];
                        float alpha = Mathf.Clamp01(1f - ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f);
                        overBlack[i] = alpha > 0.002f ? new Color(Mathf.Clamp01(b.r / alpha), Mathf.Clamp01(b.g / alpha), Mathf.Clamp01(b.b / alpha), alpha) : new Color(0f, 0f, 0f, 0f);
                    }
                }
                texture.SetPixels(overBlack);
                texture.Apply();
                return texture;
            }
            finally
            {
                preview.Cleanup();
                // Only the meshes baked here: a renderer's own mesh may be another tool's in-memory mesh (Mixer's
                // working mesh has the same flags).
                foreach (var part in parts)
                    if (part.Baked && part.Mesh != null) Object.DestroyImmediate(part.Mesh);
            }
        }

        // One render of the parts over a background, read back in sRGB.
        private static Color[] Pass(PreviewRenderUtility preview, List<Part> parts, Color background)
        {
            var camera = preview.camera;
            preview.BeginPreview(new Rect(0, 0, Size, Size), GUIStyle.none);
            RenderTexture render;
            // EndPreview always runs: it restores the editor's lighting, which Render points at the preview scene, and
            // the preview scene goes with Cleanup.
            try
            {
                // BeginPreview clears the camera to its own colour: ours is set after it.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                foreach (var part in parts)
                    for (int submesh = 0; submesh < part.Mesh.subMeshCount; submesh++)
                    {
                        var material = part.Materials.Length > 0 ? part.Materials[Mathf.Min(submesh, part.Materials.Length - 1)] : null;
                        if (material != null) preview.DrawMesh(part.Mesh, part.Matrix, material, submesh);
                    }
                preview.Render(true);
            }
            finally
            {
                render = (RenderTexture)preview.EndPreview();
            }
            var previous = RenderTexture.active;
            var read = new Texture2D(render.width, render.height, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                RenderTexture.active = render;
                read.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
                var pixels = read.GetPixels();
                // A linear project renders linear values into the preview texture: shown as they are, everything
                // looks far too dark. Converted to sRGB like the Game view does.
                if (QualitySettings.activeColorSpace == ColorSpace.Linear && !render.sRGB)
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
                return pixels;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        private static bool Helper(Transform transform, Transform root)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                if (t.gameObject.hideFlags != HideFlags.None || t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
            }
            return false;
        }

        private static Texture2D Load(string key, bool transparent)
        {
            string path = Path.Combine(Folder, key + ".png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, transparent ? TextureFormat.RGBA32 : TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            if (texture.LoadImage(File.ReadAllBytes(path)))
            {
                // Used: kept past the next prune.
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return texture;
            }
            Object.DestroyImmediate(texture);
            return null;
        }

        private static void Save(string key, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(Path.Combine(Folder, key + ".png"), texture.EncodeToPNG());
            }
            catch (IOException) { }
        }

        private static string Hash(string key)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Version + "|" + key))).Replace("-", "").ToLowerInvariant();
        }
    }
}
