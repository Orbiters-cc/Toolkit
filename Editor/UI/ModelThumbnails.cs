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
    /// when the model does (e.g. its prefab's dependency hash).
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
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static bool pumping;

        /// <summary>The picture of <paramref name="root"/> for <paramref name="key"/>, or null while it is made.</summary>
        public static Texture2D Get(GameObject root, string key)
        {
            if (root == null || string.IsNullOrEmpty(key)) return null;
            key = Hash(key);
            if (Made.TryGetValue(key, out var texture) && texture != null) return texture;
            if (Failed.Contains(key)) return null;
            texture = Load(key);
            if (texture != null) return Made[key] = texture;
            Queue[key] = root;
            if (!pumping) { pumping = true; EditorApplication.update += Pump; }
            return null;
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
                var texture = root != null ? Render(root) : null;
                if (texture == null) { Failed.Add(key); return; }
                Made[key] = texture;
                Save(key, texture);
            }
            catch (Exception ex)
            {
                Failed.Add(key);
                Debug.LogWarning("[Orbiters] Could not make a picture of " + (root != null ? root.name : "a model") + ": " + ex.Message);
            }
        }

        private struct Part { public Mesh Mesh; public Matrix4x4 Matrix; public Material[] Materials; }

        private static Texture2D Render(GameObject root)
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
                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        // Baked as posed, with its blendshapes, in the renderer's space.
                        mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        skinned.BakeMesh(mesh, true);
                    }
                    else if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null) mesh = filter.sharedMesh;
                    }
                    if (mesh == null || mesh.vertexCount == 0) continue;
                    // A baked mesh already has the renderer's scale.
                    var matrix = renderer is SkinnedMeshRenderer ? Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one) : renderer.localToWorldMatrix;
                    parts.Add(new Part { Mesh = mesh, Matrix = matrix, Materials = renderer.sharedMaterials });
                    mesh.RecalculateBounds();
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

                preview.BeginPreview(new Rect(0, 0, Size, Size), GUIStyle.none);
                // BeginPreview clears the camera to its own colour: ours is set after it.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                foreach (var part in parts)
                    for (int submesh = 0; submesh < part.Mesh.subMeshCount; submesh++)
                    {
                        var material = part.Materials.Length > 0 ? part.Materials[Mathf.Min(submesh, part.Materials.Length - 1)] : null;
                        if (material != null) preview.DrawMesh(part.Mesh, part.Matrix, material, submesh);
                    }
                preview.Render(true);
                var render = (RenderTexture)preview.EndPreview();
                var previous = RenderTexture.active;
                var texture = new Texture2D(render.width, render.height, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, name = root.name + " picture" };
                try
                {
                    RenderTexture.active = render;
                    texture.ReadPixels(new Rect(0, 0, render.width, render.height), 0, 0);
                    // A linear project renders linear values into the preview texture: shown as they are, everything
                    // looks far too dark. Converted to sRGB like the Game view does.
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear && !render.sRGB)
                    {
                        var pixels = texture.GetPixels();
                        for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
                        texture.SetPixels(pixels);
                    }
                    texture.Apply();
                }
                finally { RenderTexture.active = previous; }
                return texture;
            }
            finally
            {
                preview.Cleanup();
                // Only the meshes baked here: built-in ones (primitives) share the flags but are assets.
                foreach (var part in parts)
                    if (part.Mesh != null && part.Mesh.hideFlags == HideFlags.HideAndDontSave && !EditorUtility.IsPersistent(part.Mesh)) Object.DestroyImmediate(part.Mesh);
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

        private static Texture2D Load(string key)
        {
            string path = Path.Combine(Folder, key + ".png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            if (texture.LoadImage(File.ReadAllBytes(path))) return texture;
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
