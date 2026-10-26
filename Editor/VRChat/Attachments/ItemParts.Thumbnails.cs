using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    public static partial class ItemParts
    {
        /// <summary>The background of a variant's thumbnail (My Avatar's cards' dark grey).</summary>
        public static readonly Color ThumbnailBackground = new Color32(33, 33, 35, 255);

        /// <summary>
        /// A variant's 512 × 512 thumbnail as a PNG: for a part with a renderer, the picture it makes (N4N0's menu page in
        /// that theme and language); else the item's renderers showing the part, as they are now (the + form applies the
        /// variant to the item first; a prefab shows its own model). Null when nothing can be pictured.
        /// </summary>
        public static async Task<byte[]> ThumbnailAsync(Transform item, Definition definition, string prefabPath, string partsPath, Part part, PartVariant variant)
        {
            if (!string.IsNullOrEmpty(part.renderer) && Renderers.TryGetValue(part.renderer, out var renderer) && renderer.Render != null)
            {
                var job = Job(item, definition, prefabPath, partsPath, part.renderer, new List<(Part, PartVariant)> { (part, variant) }, null, default);
                job.ThumbnailFor = part.key;
                await renderer.Render(job);
                if (job.Thumbnail != null) return job.Thumbnail;
            }
            if (item == null) return null;
            // A model once (the first renderer of each slot: N4N0's phone body is the same model), else the renderers its values go on.
            var renderers = part.kind == Model
                ? (part.slots ?? new List<Slot>()).Where(s => s != null).Select(s => Find(item, (s.paths ?? new List<string>()).FirstOrDefault())?.GetComponent<Renderer>())
                : Anchors(item, part);
            var picture = RenderPicture(renderers.Where(r => r != null).Distinct().ToList(), ThumbnailSize, 0, part.kind == Model);
            if (picture == null) return null;
            try { return picture.EncodeToPNG(); }
            finally { Object.DestroyImmediate(picture); }
        }

        /// <summary>
        /// A picture of <paramref name="renderers"/> as they show (skinned ones baked with their blendshapes), seen three
        /// quarters from the front and a little from above, turned <paramref name="turn"/> degrees around them (a model turning
        /// in the + form). The front is the avatar's, or with <paramref name="meshFront"/> the first renderer's own (+Z: a
        /// model's face, whichever way its object is turned). The caller destroys it. Null when nothing shows.
        /// </summary>
        public static Texture2D RenderPicture(IReadOnlyList<Renderer> renderers, int size, float turn = 0, bool meshFront = false)
        {
            var parts = new List<(Mesh mesh, Matrix4x4 matrix, Material[] materials, bool baked)>();
            var preview = new PreviewRenderUtility();
            try
            {
                var bounds = new Bounds();
                bool any = false;
                foreach (var renderer in renderers ?? new List<Renderer>())
                {
                    if (renderer == null) continue;
                    Mesh mesh = null;
                    bool baked = false;
                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        skinned.BakeMesh(mesh, true);
                        baked = true;
                        mesh.RecalculateBounds();
                    }
                    else if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) mesh = filter.sharedMesh;
                    if (mesh == null || mesh.vertexCount == 0) { if (baked) Object.DestroyImmediate(mesh); continue; }
                    // A baked mesh already has the renderer's scale.
                    var matrix = baked ? Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one) : renderer.localToWorldMatrix;
                    parts.Add((mesh, matrix, renderer.sharedMaterials, baked));
                    var local = mesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                        if (!any) { bounds = new Bounds(point, Vector3.zero); any = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                if (!any) return null;
                var first = renderers.First(r => r != null).transform;
                var facing = meshFront ? first.rotation : first.root.rotation;
                var direction = facing * Quaternion.Euler(0, turn, 0) * new Vector3(0.45f, 0.25f, 1f).normalized;
                float radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
                var camera = preview.camera;
                camera.orthographic = true;
                camera.transform.position = bounds.center + direction * (radius * 3f);
                camera.transform.LookAt(bounds.center, facing * Vector3.up);
                camera.orthographicSize = radius * 1.05f;
                camera.nearClipPlane = radius * 0.01f;
                camera.farClipPlane = radius * 8f;
                preview.ambientColor = new Color(0.95f, 0.95f, 0.97f);
                preview.lights[0].intensity = 1.4f;
                preview.lights[0].transform.rotation = camera.transform.rotation * Quaternion.Euler(30, -35, 0);
                preview.lights[1].intensity = 0.8f;
                preview.lights[1].transform.rotation = camera.transform.rotation * Quaternion.Euler(-10, 150, 0);
                preview.BeginPreview(new Rect(0, 0, size, size), GUIStyle.none);
                RenderTexture render;
                // EndPreview always runs: it restores the editor's lighting, which Render points at the preview scene.
                try
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = ThumbnailBackground;
                    foreach (var part in parts)
                        for (int submesh = 0; submesh < part.mesh.subMeshCount; submesh++)
                        {
                            var material = part.materials.Length > 0 ? part.materials[Mathf.Min(submesh, part.materials.Length - 1)] : null;
                            if (material != null) preview.DrawMesh(part.mesh, part.matrix, material, submesh);
                        }
                    preview.Render(true);
                }
                finally { render = (RenderTexture)preview.EndPreview(); }
                var previous = RenderTexture.active;
                var picture = new Texture2D(size, size, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, name = "Variant picture" };
                try
                {
                    RenderTexture.active = render;
                    picture.ReadPixels(new Rect(0, 0, Mathf.Min(size, render.width), Mathf.Min(size, render.height)), 0, 0);
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear && !render.sRGB)
                    {
                        var pixels = picture.GetPixels();
                        for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
                        picture.SetPixels(pixels);
                    }
                    picture.Apply();
                }
                finally { RenderTexture.active = previous; }
                return picture;
            }
            finally
            {
                preview.Cleanup();
                foreach (var part in parts) if (part.baked && part.mesh != null) Object.DestroyImmediate(part.mesh);
            }
        }

        /// <summary>A picture (a PNG or JPEG) fitted into a <paramref name="size"/> square over <paramref name="background"/>, as a PNG.</summary>
        public static byte[] Fit(byte[] image, int size, Color background)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var target = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture render = null;
            var previous = RenderTexture.active;
            try
            {
                if (image == null || !source.LoadImage(image)) return null;
                float scale = Mathf.Min((float)size / source.width, (float)size / source.height);
                int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
                render = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, render);
                RenderTexture.active = render;
                var scaled = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var fill = Enumerable.Repeat((Color32)background, size * size).ToArray();
                var pixels = scaled.GetPixels32();
                int x0 = (size - w) / 2, y0 = (size - h) / 2;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = pixels[y * w + x];
                    var b = fill[(y + y0) * size + x + x0];
                    float a = p.a / 255f;
                    fill[(y + y0) * size + x + x0] = new Color32((byte)(p.r * a + b.r * (1 - a)), (byte)(p.g * a + b.g * (1 - a)), (byte)(p.b * a + b.b * (1 - a)), 255);
                }
                Object.DestroyImmediate(scaled);
                target.SetPixels32(fill);
                target.Apply();
                return target.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                if (render != null) RenderTexture.ReleaseTemporary(render);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
            }
        }
    }
}
