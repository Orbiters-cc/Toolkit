using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Storage;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Orbiters.Toolkit.Editor.Preview
{
    /// <summary>What a capture copied out of the scene on the main thread, for the worker to write.</summary>
    internal sealed class PreviewData
    {
        public const long TriangleBudget = 2_000_000;

        public sealed class AvatarPlace
        {
            public string Name, Id;
            public Vector3 Position, Forward;
        }

        public sealed class SceneCamera
        {
            public Vector3 Position, Pivot;
            public Quaternion Rotation;
            public float FovY;
        }

        public sealed class Picture
        {
            public int Width, Height;
            public bool Png;
            /// <summary>RGBA, sRGB, rows from the bottom (as Unity reads textures back).</summary>
            public byte[] Pixels;
        }

        public sealed class Material
        {
            public string Name, Alpha, Cull;
            public Color Color;
            public int Texture;
            public Vector2 Tiling, Offset;
            public float Cutoff;
        }

        public sealed class Mesh
        {
            public Vector3[] Positions, Normals;
            public Vector2[] Uvs;
            public int[] Indices;
            public List<(int start, int count)> Submeshes = new List<(int start, int count)>();
        }

        public sealed class Draw
        {
            public int Instance, Mesh, Avatar;
            public int[] Materials;
            public bool Shadows;
        }

        public string Reason, SceneName, ScenePath, SceneGuid, Unity, Generator, Kind;
        public SceneCamera Camera;
        public readonly List<AvatarPlace> Avatars = new List<AvatarPlace>();
        public readonly List<Picture> Textures = new List<Picture>();
        public readonly List<Material> Materials = new List<Material>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
        public readonly List<Draw> Draws = new List<Draw>();
        public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>();
        public int Renderers, Steps;
        public double MainThreadMs, LongestStepMs, TotalMsBeforeWorker;

        public void Skip(string why) => Skipped[why] = Skipped.TryGetValue(why, out int n) ? n + 1 : 1;
    }

    /// <summary>
    /// Writes a <see cref="PreviewData"/> as a preview file (contracts' Docs/project-preview.md): "ORBPREV\0", schema 1, the
    /// header's length, the JSON header, then the blobs, each at a multiple of 16 from the data section's start.
    /// Runs on a worker thread: only thread-safe Unity calls (ImageConversion's array encoders).
    /// </summary>
    internal static class PreviewFile
    {
        private const int Schema = 1;
        private const int VertexSize = 20;

        public static string Write(PreviewData d, string path)
        {
            var watch = Stopwatch.StartNew();
            var blobs = new MemoryStream();
            JObject Blob(byte[] bytes)
            {
                long offset = blobs.Length;
                blobs.Write(bytes, 0, bytes.Length);
                int pad = (int)((16 - blobs.Length % 16) % 16);
                for (int i = 0; i < pad; i++) blobs.WriteByte(0);
                return new JObject { ["offset"] = offset, ["length"] = bytes.Length };
            }

            // Pictures on every core.
            var encoded = new byte[d.Textures.Count][];
            Parallel.For(0, d.Textures.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
            {
                var t = d.Textures[i];
                if (t.Pixels == null || t.Pixels.Length < t.Width * t.Height * 4) return;
                encoded[i] = t.Png
                    ? ImageConversion.EncodeArrayToPNG(t.Pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)t.Width, (uint)t.Height)
                    : ImageConversion.EncodeArrayToJPG(t.Pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)t.Width, (uint)t.Height, 0, 90);
            });
            var textures = new JArray();
            var textureIndex = new int[d.Textures.Count];
            for (int i = 0; i < d.Textures.Count; i++)
            {
                if (encoded[i] == null) { textureIndex[i] = -1; continue; }
                textureIndex[i] = textures.Count;
                var t = d.Textures[i];
                textures.Add(new JObject { ["width"] = t.Width, ["height"] = t.Height, ["format"] = t.Png ? "png" : "jpeg", ["data"] = Blob(encoded[i]) });
            }

            var materials = new JArray();
            foreach (var m in d.Materials)
            {
                materials.Add(new JObject
                {
                    ["name"] = m.Name,
                    ["color"] = Array(m.Color.r, m.Color.g, m.Color.b, m.Color.a),
                    ["texture"] = m.Texture >= 0 ? textureIndex[m.Texture] : -1,
                    ["tiling"] = Array(m.Tiling.x, m.Tiling.y),
                    ["offset"] = Array(m.Offset.x, m.Offset.y),
                    ["alpha"] = m.Alpha,
                    ["cutoff"] = m.Cutoff,
                    ["cull"] = m.Cull,
                });
            }

            var meshes = new JArray();
            var meshBounds = new List<Bounds>();
            long triangles = 0, vertices = 0;
            foreach (var m in d.Meshes)
            {
                var (bytes, bounds) = Vertices(m);
                meshBounds.Add(bounds);
                int n = m.Positions.Length;
                bool small = n <= 65535;
                var indexBytes = new byte[m.Indices.Length * (small ? 2 : 4)];
                if (small)
                    for (int i = 0; i < m.Indices.Length; i++)
                    {
                        indexBytes[i * 2] = (byte)m.Indices[i];
                        indexBytes[i * 2 + 1] = (byte)(m.Indices[i] >> 8);
                    }
                else Buffer.BlockCopy(m.Indices, 0, indexBytes, 0, indexBytes.Length);
                triangles += m.Indices.Length / 3;
                vertices += n;
                meshes.Add(new JObject
                {
                    ["vertices"] = n,
                    ["data"] = Blob(bytes),
                    ["indexFormat"] = small ? "u16" : "u32",
                    ["indices"] = Blob(indexBytes),
                    ["submeshes"] = new JArray(m.Submeshes.Select(s => new JObject { ["start"] = s.start, ["count"] = s.count })),
                    ["bounds"] = Box(bounds),
                });
            }

            var matrices = new byte[d.Matrices.Count * 64];
            var floats = new float[16];
            for (int i = 0; i < d.Matrices.Count; i++)
            {
                for (int k = 0; k < 16; k++) floats[k] = d.Matrices[i][k % 4, k / 4];
                Buffer.BlockCopy(floats, 0, matrices, i * 64, 64);
            }
            var draws = new JArray();
            Bounds? all = null, avatars = null;
            foreach (var draw in d.Draws)
            {
                var o = new JObject
                {
                    ["instance"] = draw.Instance,
                    ["mesh"] = draw.Mesh,
                    ["materials"] = new JArray(draw.Materials),
                    ["shadows"] = draw.Shadows,
                };
                if (draw.Avatar >= 0) o["avatar"] = draw.Avatar;
                draws.Add(o);
                var world = Transform(meshBounds[draw.Mesh], d.Matrices[draw.Instance]);
                all = Grow(all, world);
                if (draw.Avatar >= 0) avatars = Grow(avatars, world);
            }
            var bounds0 = all ?? new Bounds();
            float? ground = d.Avatars.Count > 0 ? d.Avatars.Min(a => a.Position.y) : (float?)null;

            var header = new JObject
            {
                ["writtenUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["generator"] = d.Generator,
                ["unity"] = d.Unity,
                ["scene"] = new JObject { ["name"] = d.SceneName, ["path"] = d.ScenePath, ["guid"] = d.SceneGuid },
                ["kind"] = d.Kind,
                ["bounds"] = Box(bounds0),
                ["focus"] = Box(avatars ?? bounds0),
                ["ground"] = ground.HasValue ? new JValue(Round(ground.Value)) : JValue.CreateNull(),
                ["camera"] = d.Camera == null
                    ? (JToken)JValue.CreateNull()
                    : new JObject
                    {
                        ["position"] = Vec(d.Camera.Position),
                        ["rotation"] = Array(d.Camera.Rotation.x, d.Camera.Rotation.y, d.Camera.Rotation.z, d.Camera.Rotation.w),
                        ["pivot"] = Vec(d.Camera.Pivot),
                        ["fovY"] = d.Camera.FovY,
                    },
                ["avatars"] = new JArray(d.Avatars.Select(a => new JObject
                {
                    ["name"] = a.Name,
                    ["id"] = a.Id,
                    ["position"] = Vec(a.Position),
                    ["forward"] = Vec(a.Forward),
                })),
                ["textures"] = textures,
                ["materials"] = materials,
                ["meshes"] = meshes,
                ["instances"] = new JObject { ["count"] = d.Matrices.Count, ["matrices"] = Blob(matrices) },
                ["draws"] = draws,
                ["stats"] = new JObject
                {
                    ["renderers"] = d.Renderers,
                    ["triangles"] = triangles,
                    ["vertices"] = vertices,
                    ["skipped"] = new JObject(d.Skipped.Select(s => new JProperty(s.Key, s.Value))),
                },
            };
            double encodeMs = watch.Elapsed.TotalMilliseconds;
            header["cost"] = new JObject
            {
                ["mainThreadMs"] = Math.Round(d.MainThreadMs, 2),
                ["steps"] = d.Steps,
                ["longestStepMs"] = Math.Round(d.LongestStepMs, 2),
                ["encodeMs"] = Math.Round(encodeMs, 1),
                ["totalMs"] = Math.Round(d.TotalMsBeforeWorker + encodeMs, 1),
            };

            byte[] json = Encoding.UTF8.GetBytes(header.ToString(Newtonsoft.Json.Formatting.None));
            int start = 16 + json.Length;
            int dataStart = (start + 15) / 16 * 16;
            var file = new byte[dataStart + blobs.Length];
            Encoding.ASCII.GetBytes("ORBPREV\0").CopyTo(file, 0);
            BitConverter.GetBytes(Schema).CopyTo(file, 8);
            BitConverter.GetBytes(json.Length).CopyTo(file, 12);
            json.CopyTo(file, 16);
            blobs.Position = 0;
            blobs.Read(file, dataStart, (int)blobs.Length);
            SafePaths.ReplaceFile(path, file);
            Prune(Path.GetDirectoryName(path), path);
            return string.Format(CultureInfo.InvariantCulture, "written {0} ({1:0.0} MB, {2} triangles, {3} pictures; main thread {4:0.0} ms over {5} steps, longest {6:0.0} ms; worker {7:0} ms; {8:0} ms in all; {9})",
                Path.GetFileName(path), file.Length / 1e6, triangles, textures.Count, d.MainThreadMs, d.Steps, d.LongestStepMs, encodeMs, d.TotalMsBeforeWorker + encodeMs, d.Reason);
        }

        // Keeps the newest previews of the project (one per scene).
        private static void Prune(string folder, string keep)
        {
            try
            {
                var old = new DirectoryInfo(folder).GetFiles("*.preview").OrderByDescending(f => f.LastWriteTimeUtc).Skip(ProjectPreview.Keep);
                foreach (var f in old)
                    if (!string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) f.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // 20 bytes a vertex: position (3 × f32), octahedral normal (2 × snorm16), UV (2 × f16); and the mesh's bounds.
        private static (byte[], Bounds) Vertices(PreviewData.Mesh m)
        {
            int n = m.Positions.Length;
            var bytes = new byte[n * VertexSize];
            bool normals = m.Normals != null && m.Normals.Length == n, uvs = m.Uvs != null && m.Uvs.Length == n;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            for (int i = 0; i < n; i++)
            {
                var p = m.Positions[i];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                int o = i * VertexSize;
                Single(bytes, o, p.x);
                Single(bytes, o + 4, p.y);
                Single(bytes, o + 8, p.z);
                var (nx, ny) = Octahedral(normals ? m.Normals[i] : Vector3.up);
                Short(bytes, o + 12, nx);
                Short(bytes, o + 14, ny);
                var uv = uvs ? m.Uvs[i] : Vector2.zero;
                Short(bytes, o + 16, (short)Half(uv.x));
                Short(bytes, o + 18, (short)Half(uv.y));
            }
            var b = new Bounds();
            if (n > 0) b.SetMinMax(min, max);
            return (bytes, b);
        }

        private static void Single(byte[] b, int at, float v)
        {
            uint u = (uint)BitConverter.SingleToInt32Bits(v);
            b[at] = (byte)u;
            b[at + 1] = (byte)(u >> 8);
            b[at + 2] = (byte)(u >> 16);
            b[at + 3] = (byte)(u >> 24);
        }

        private static void Short(byte[] b, int at, short v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }

        internal static (short, short) Octahedral(Vector3 n)
        {
            float l1 = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            float x = l1 > 0 ? n.x / l1 : 0, y = l1 > 0 ? n.y / l1 : 0;
            if (n.z < 0)
            {
                float ox = x, oy = y;
                x = (1 - Mathf.Abs(oy)) * (ox >= 0 ? 1 : -1);
                y = (1 - Mathf.Abs(ox)) * (oy >= 0 ? 1 : -1);
            }
            return ((short)Mathf.RoundToInt(Mathf.Clamp(x, -1, 1) * 32767), (short)Mathf.RoundToInt(Mathf.Clamp(y, -1, 1) * 32767));
        }

        /// <summary>IEEE half-float bits, rounded to nearest (Mathf.FloatToHalf is not for worker threads).</summary>
        internal static ushort Half(float v)
        {
            uint b = (uint)BitConverter.SingleToInt32Bits(v);
            uint sign = (b >> 16) & 0x8000;
            int exp = (int)((b >> 23) & 0xff);
            uint mant = b & 0x7fffff;
            if (exp == 0xff) return (ushort)(sign | 0x7c00 | (mant != 0 ? 0x200u : 0));
            int e = exp - 127 + 15;
            if (e >= 0x1f) return (ushort)(sign | 0x7c00);
            if (e <= 0)
            {
                if (e < -10) return (ushort)sign;
                uint m = (mant | 0x800000) >> (1 - e);
                return (ushort)(sign | ((m + 0x1000) >> 13));
            }
            uint h = sign | ((uint)e << 10) | (mant >> 13);
            return (ushort)(h + ((mant >> 12) & 1));
        }

        private static Bounds Transform(Bounds local, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(local.center);
            var e = local.extents;
            var x = m.MultiplyVector(new Vector3(e.x, 0, 0));
            var y = m.MultiplyVector(new Vector3(0, e.y, 0));
            var z = m.MultiplyVector(new Vector3(0, 0, e.z));
            var ext = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(c, ext * 2);
        }

        private static Bounds Grow(Bounds? b, Bounds add)
        {
            if (!b.HasValue) return add;
            var g = b.Value;
            g.Encapsulate(add);
            return g;
        }

        private static JArray Array(params float[] v) => new JArray(v.Select(f => (object)Round(f)));
        private static JArray Vec(Vector3 v) => Array(v.x, v.y, v.z);
        private static JObject Box(Bounds b) => new JObject { ["min"] = Vec(b.min), ["max"] = Vec(b.max) };
        private static double Round(float f) => float.IsFinite(f) ? Math.Round(f, 5) : 0;
    }
}
