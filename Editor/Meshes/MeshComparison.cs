using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    public enum PartChange { Same, Moved, Reshaped, Materials, Shapes, Added, Removed }

    /// <summary>A renderer of a model or prefab version: its mesh and where it sits under the root.</summary>
    public sealed class ModelPart
    {
        public string Path = string.Empty;
        public Mesh Mesh;
        public Matrix4x4 Placement = Matrix4x4.identity;
        public string[] Materials = new string[0];
        // The materials to draw it with when changes are not highlighted (prefabs); null draws it plain.
        public Material[] Rendered;
        public string[] BlendShapes = new string[0];
        // For meshes read from a file: blendshape name → signature of its offsets.
        public Dictionary<string, long> ShapeSignatures;
        public int Bones;
        // Created for the comparison (read from a file), destroyed with it.
        public bool Owned;
        // The geometry the comparison reads, so it can run off the main thread (Unity's meshes cannot be read there).
        public Vector3[] Points;
        public int[] Triangles;

        public int VertexCount => Points?.Length ?? (Mesh != null ? Mesh.vertexCount : 0);
    }

    public sealed class ModelInfo
    {
        public readonly List<ModelPart> Parts = new List<ModelPart>();
        public readonly HashSet<string> Transforms = new HashSet<string>();
        public Bounds Bounds;
        public long FileBytes;
    }

    /// <summary>How one part of a version differs from the same part of the version it is compared to.</summary>
    public sealed class PartDiff
    {
        public string Path = string.Empty;
        public PartChange Change;
        public float MaxDistance;
        public int MovedVertices;
        public int VerticesBefore, VerticesAfter;
        public Color[] Colors;
        public readonly List<string> ShapesAdded = new List<string>(), ShapesRemoved = new List<string>(), ShapesChanged = new List<string>();
        public bool BonesChanged;
    }

    /// <summary>
    /// Compares versions of a model part by part: vertices moved (a heat map from yellow to red), parts placed elsewhere,
    /// other materials, blendshapes or bones, parts added or removed. Models are read from their file (<see cref="FbxReader"/>),
    /// so nothing is imported. Shared by Unit Git's 3D view and MCB's version comparison.
    /// </summary>
    public static class MeshComparison
    {
        public static readonly Color Unchanged = new Color(0.6f, 0.62f, 0.66f), Added = new Color(0.1f, 0.85f, 0.45f),
            Moved = new Color(1f, 0.6f, 0.2f), Materials = new Color(0.62f, 0.5f, 1f), Shapes = new Color(0.3f, 0.78f, 1f);

        /// <summary>
        /// The parts of a model file, read without Unity's importer. Any thread: the parts have no Unity mesh yet,
        /// <see cref="CreateMesh"/> makes them on the main thread.
        /// </summary>
        public static ModelInfo FromFile(long fileBytes, List<FbxMesh> parsed)
        {
            var info = new ModelInfo { FileBytes = fileBytes };
            bool hasBounds = false;
            foreach (var source in parsed)
            {
                info.Parts.Add(new ModelPart
                {
                    Path = source.Path,
                    Points = source.Points,
                    Triangles = source.Triangles,
                    Materials = source.Materials,
                    BlendShapes = source.Shapes.Keys.ToArray(),
                    ShapeSignatures = source.Shapes,
                    Bones = source.Bones,
                    Owned = true
                });
                if (source.Points.Length == 0) continue;
                var bounds = BoundsOf(source.Points);
                if (hasBounds) info.Bounds.Encapsulate(bounds);
                else { info.Bounds = bounds; hasBounds = true; }
            }
            if (!hasBounds) info.Bounds = new Bounds(Vector3.zero, Vector3.one);
            return info;
        }

        /// <summary>A Unity mesh of a part read from a file (main thread), with these vertex colours when given.</summary>
        public static Mesh CreateMesh(ModelPart part, Color[] colors = null)
        {
            var mesh = new Mesh { name = part.Path, hideFlags = HideFlags.HideAndDontSave, indexFormat = part.Points.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.vertices = part.Points;
            mesh.triangles = part.Triangles;
            if (colors != null && colors.Length == part.Points.Length) mesh.colors = colors;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Copies what the comparison reads out of the Unity meshes of a described prefab (main thread), so that
        /// <see cref="Compare"/> can then run on any thread.
        /// </summary>
        public static void Snapshot(ModelInfo info)
        {
            foreach (var part in info.Parts)
            {
                if (part.Points != null || part.Mesh == null) continue;
                part.Points = part.Mesh.vertices;
                part.Triangles = part.Mesh.triangles;
                part.ShapeSignatures = ShapeSignaturesOf(part.Mesh);
            }
        }

        // Blendshape name → a signature of its last frame's offsets: equal signatures, same shape.
        private static Dictionary<string, long> ShapeSignaturesOf(Mesh mesh)
        {
            var result = new Dictionary<string, long>();
            if (mesh.blendShapeCount == 0) return result;
            var deltas = new Vector3[mesh.vertexCount];
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                int last = mesh.GetBlendShapeFrameCount(i) - 1;
                if (last < 0) continue;
                mesh.GetBlendShapeFrameVertices(i, last, deltas, null, null);
                unchecked
                {
                    long hash = 1469598103934665603L;
                    foreach (var d in deltas)
                    {
                        hash = (hash ^ Mathf.RoundToInt(d.x * 1e5f)) * 1099511628211L;
                        hash = (hash ^ Mathf.RoundToInt(d.y * 1e5f)) * 1099511628211L;
                        hash = (hash ^ Mathf.RoundToInt(d.z * 1e5f)) * 1099511628211L;
                    }
                    result[mesh.GetBlendShapeName(i)] = hash;
                }
            }
            return result;
        }

        private static Bounds BoundsOf(Vector3[] points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++) bounds.Encapsulate(points[i]);
            return bounds;
        }

        /// <summary>Reads a model file off the main thread: binary FBX or OBJ.</summary>
        public static List<FbxMesh> Parse(string path, byte[] bytes, FbxReadOptions options = FbxReadOptions.Geometry)
        {
            return string.Equals(System.IO.Path.GetExtension(path), ".obj", StringComparison.OrdinalIgnoreCase) ? ReadObj(bytes) : FbxReader.Read(bytes, options);
        }

        // OBJ: "v x y z" points and "f a/b/c ..." polygons, one mesh per "o"/"g" group (X mirrored like Unity's importer).
        private static List<FbxMesh> ReadObj(byte[] bytes)
        {
            var points = new List<Vector3>();
            var meshes = new List<FbxMesh>();
            var triangles = new List<int>();
            string name = "Mesh";
            void Flush()
            {
                if (triangles.Count == 0) return;
                meshes.Add(new FbxMesh { Path = name, Points = points.ToArray(), Triangles = triangles.ToArray() });
                triangles = new List<int>();
            }
            foreach (string raw in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    var p = line.Substring(2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    float F(int i) => float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
                    points.Add(new Vector3(-F(0), F(1), F(2)));
                }
                else if (line.StartsWith("o ", StringComparison.Ordinal) || line.StartsWith("g ", StringComparison.Ordinal))
                {
                    Flush();
                    name = line.Substring(2).Trim();
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    var corners = line.Substring(2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => int.Parse(c.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture))
                        .Select(i => i < 0 ? points.Count + i : i - 1).ToList();
                    for (int k = 1; k + 1 < corners.Count; k++) { triangles.Add(corners[0]); triangles.Add(corners[k + 1]); triangles.Add(corners[k]); }
                }
            }
            Flush();
            return meshes;
        }

        public static ModelInfo Describe(GameObject root, long fileBytes)
        {
            var info = new ModelInfo { FileBytes = fileBytes };
            if (root == null) return info;
            var rootInverse = root.transform.worldToLocalMatrix;
            bool hasBounds = false;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) info.Transforms.Add(PathOf(root.transform, transform));
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var part = new ModelPart
                {
                    Path = PathOf(root.transform, renderer.transform),
                    Mesh = mesh,
                    Placement = rootInverse * renderer.transform.localToWorldMatrix,
                    Materials = renderer.sharedMaterials.Select(m => m != null ? m.name : "None").ToArray(),
                    Rendered = renderer.sharedMaterials,
                    BlendShapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray(),
                    Bones = renderer is SkinnedMeshRenderer s ? s.bones.Length : 0
                };
                info.Parts.Add(part);
                var bounds = GeometryUtility.CalculateBounds(new[] { mesh.bounds.min, mesh.bounds.max }, part.Placement);
                if (hasBounds) info.Bounds.Encapsulate(bounds);
                else { info.Bounds = bounds; hasBounds = true; }
            }
            if (!hasBounds) info.Bounds = new Bounds(Vector3.zero, Vector3.one);
            return info;
        }

        private static string PathOf(Transform root, Transform transform)
        {
            var names = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent) names.Add(current.name);
            names.Reverse();
            return names.Count == 0 ? root.name : string.Join("/", names);
        }

        /// <summary>
        /// What each part of <paramref name="side"/> changed compared to <paramref name="reference"/>: moved vertices as a
        /// heat map (grey unchanged, yellow to red by distance), whole parts moved, re-materialed, added or removed.
        /// Any thread once every part has its geometry (read from a file, or <see cref="Snapshot"/>).
        /// </summary>
        public static List<PartDiff> Compare(ModelInfo reference, ModelInfo side)
        {
            var result = new List<PartDiff>();
            var before = reference.Parts.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.First());
            float size = Mathf.Max(side.Bounds.size.magnitude, reference.Bounds.size.magnitude, 1e-4f);
            float threshold = Mathf.Max(size * 2e-5f, 1e-6f);
            foreach (var part in side.Parts)
            {
                var diff = new PartDiff { Path = part.Path, VerticesAfter = part.VertexCount };
                result.Add(diff);
                if (!before.TryGetValue(part.Path, out var old))
                {
                    diff.Change = PartChange.Added;
                    diff.Colors = Fill(part.VertexCount, Added);
                    continue;
                }
                diff.VerticesBefore = old.VertexCount;
                diff.ShapesAdded.AddRange(part.BlendShapes.Except(old.BlendShapes));
                diff.ShapesRemoved.AddRange(old.BlendShapes.Except(part.BlendShapes));
                if (old.ShapeSignatures != null && part.ShapeSignatures != null)
                    diff.ShapesChanged.AddRange(part.ShapeSignatures.Where(pair => old.ShapeSignatures.TryGetValue(pair.Key, out long before) && before != pair.Value).Select(pair => pair.Key));
                diff.BonesChanged = old.Bones != part.Bones;

                var oldPoints = old.Points ?? old.Mesh.vertices;
                var points = part.Points ?? part.Mesh.vertices;
                bool sameTopology = oldPoints.Length == points.Length;
                var distances = sameTopology
                    ? Distances(oldPoints, points, size)
                    : SurfaceDistances(oldPoints, old.Triangles ?? old.Mesh.triangles, points, size);
                // With new topology, points are measured to the old surface: allow for the smoothing of a re-export.
                float moved = sameTopology ? threshold : Mathf.Max(threshold, size * 5e-4f);
                diff.MaxDistance = distances.Length == 0 ? 0f : distances.Max();
                diff.MovedVertices = distances.Count(d => d > moved);
                if (diff.MovedVertices > 0 || !sameTopology)
                {
                    diff.Change = PartChange.Reshaped;
                    diff.Colors = Heat(distances, moved, Mathf.Max(diff.MaxDistance, moved * 10f));
                }
                else if (!Same(old.Placement, part.Placement, threshold))
                {
                    diff.Change = PartChange.Moved;
                    diff.Colors = Fill(part.VertexCount, Moved);
                }
                else if (!old.Materials.SequenceEqual(part.Materials))
                {
                    diff.Change = PartChange.Materials;
                    diff.Colors = Fill(part.VertexCount, Materials);
                }
                else
                {
                    // Same surface, but its blendshapes or bones changed.
                    diff.Change = diff.ShapesAdded.Count + diff.ShapesRemoved.Count + diff.ShapesChanged.Count > 0 || diff.BonesChanged ? PartChange.Shapes : PartChange.Same;
                    diff.Colors = Fill(part.VertexCount, diff.Change == PartChange.Same ? Unchanged : Shapes);
                }
            }
            var now = new HashSet<string>(side.Parts.Select(p => p.Path));
            foreach (var old in reference.Parts.Where(p => !now.Contains(p.Path)))
                result.Add(new PartDiff { Path = old.Path, Change = PartChange.Removed, VerticesBefore = old.VertexCount });
            return result;
        }

        /// <summary>
        /// How far each vertex moved: index by index when the vertex count is the same, otherwise the distance to the
        /// closest vertex of the other version (a grid keeps it fast on avatar meshes).
        /// </summary>
        public static float[] Distances(Vector3[] before, Vector3[] after, float size)
        {
            var result = new float[after.Length];
            if (before.Length == after.Length)
            {
                for (int i = 0; i < after.Length; i++) result[i] = Vector3.Distance(before[i], after[i]);
                return result;
            }
            if (before.Length == 0)
            {
                for (int i = 0; i < result.Length; i++) result[i] = size;
                return result;
            }
            float cell = Mathf.Max(size / 48f, 1e-5f);
            var grid = new Dictionary<Vector3Int, List<int>>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            for (int i = 0; i < before.Length; i++)
            {
                var key = Key(before[i]);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                list.Add(i);
            }
            for (int i = 0; i < after.Length; i++)
            {
                var key = Key(after[i]);
                float best = float.MaxValue;
                for (int ring = 1; ring <= 3 && best == float.MaxValue; ring++)
                    for (int x = -ring; x <= ring; x++)
                    for (int y = -ring; y <= ring; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        if (!grid.TryGetValue(new Vector3Int(key.x + x, key.y + y, key.z + z), out var list)) continue;
                        foreach (int j in list) best = Mathf.Min(best, (before[j] - after[i]).sqrMagnitude);
                    }
                result[i] = best == float.MaxValue ? cell * 3f : Mathf.Sqrt(best);
            }
            return result;
        }

        /// <summary>
        /// How far each point is from the other version's surface (its closest triangle): a mesh subdivided or re-exported
        /// with other vertices shows only where its shape really changed.
        /// </summary>
        public static float[] SurfaceDistances(Vector3[] points, int[] triangles, Vector3[] query, float size)
        {
            var result = new float[query.Length];
            if (triangles.Length < 3) return Distances(points, query, size);
            // Cells about the size of a triangle: each triangle lands in a few of them.
            double edges = 0;
            int sampled = 0;
            for (int t = 0; t + 2 < triangles.Length; t += Math.Max(3, triangles.Length / 3000 * 3), sampled++)
                edges += (points[triangles[t]] - points[triangles[t + 1]]).magnitude;
            float cell = Mathf.Max(size / 256f, (float)(edges / Math.Max(1, sampled)) * 1.5f, 1e-5f);
            var grid = new Dictionary<Vector3Int, List<int>>();
            var large = new List<int>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = points[triangles[t]], b = points[triangles[t + 1]], c = points[triangles[t + 2]];
                var min = Key(Vector3.Min(a, Vector3.Min(b, c)));
                var max = Key(Vector3.Max(a, Vector3.Max(b, c)));
                // A triangle far larger than a cell is checked against every point instead of filling the grid.
                if ((long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1) > 64) { large.Add(t); continue; }
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    var key = new Vector3Int(x, y, z);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(t);
                }
            }
            // Points are independent: searched in parallel over the read-only grid, leaving the editor half the cores.
            System.Threading.Tasks.Parallel.For(0, query.Length, HalfTheCores, i =>
            {
                var p = query[i];
                var key = Key(p);
                float best = float.MaxValue;
                foreach (int t in large)
                    best = Mathf.Min(best, (Closest(p, points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]) - p).sqrMagnitude);
                // Rings of cells around the point, one shell at a time. Once rings up to r are searched, any other triangle is
                // at least r cells away: a point on the other surface stops after the first ring.
                for (int ring = 0; ring <= 4; ring++)
                {
                    float reach = (ring - 1) * cell;
                    if (ring > 1 && best <= reach * reach) break;
                    for (int x = -ring; x <= ring; x++)
                    for (int y = -ring; y <= ring; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Max(Mathf.Abs(y), Mathf.Abs(z))) != ring) continue;
                        if (!grid.TryGetValue(new Vector3Int(key.x + x, key.y + y, key.z + z), out var list)) continue;
                        foreach (int t in list)
                            best = Mathf.Min(best, (Closest(p, points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]) - p).sqrMagnitude);
                    }
                }
                result[i] = best == float.MaxValue ? cell * 4f : Mathf.Sqrt(best);
            });
            return result;
        }

        private static readonly System.Threading.Tasks.ParallelOptions HalfTheCores =
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

        // The closest point of a triangle (Ericson, Real-Time Collision Detection 5.1.5).
        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denominator = 1f / (va + vb + vc);
            return a + ab * (vb * denominator) + ac * (vc * denominator);
        }

        private static Color[] Heat(float[] distances, float threshold, float max)
        {
            var colors = new Color[distances.Length];
            var yellow = new Color(1f, 0.86f, 0.2f);
            var red = new Color(1f, 0.22f, 0.2f);
            for (int i = 0; i < distances.Length; i++)
            {
                float d = distances[i];
                if (d <= threshold) { colors[i] = Unchanged; continue; }
                float t = Mathf.Clamp01((d - threshold) / (max - threshold));
                colors[i] = t < 0.5f ? Color.Lerp(yellow, new Color(1f, 0.55f, 0.15f), t * 2f) : Color.Lerp(new Color(1f, 0.55f, 0.15f), red, (t - 0.5f) * 2f);
            }
            return colors;
        }

        private static Color[] Fill(int count, Color color)
        {
            var colors = new Color[count];
            for (int i = 0; i < count; i++) colors[i] = color;
            return colors;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b, float threshold)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > Mathf.Max(threshold, 1e-5f)) return false;
            return true;
        }

        /// <summary>"2.4 mm", "1.2 cm": distances as a creator reads them (one unit is one metre).</summary>
        public static string Length(float metres)
        {
            if (metres < 0.01f) return (metres * 1000f).ToString("0.#") + " mm";
            if (metres < 1f) return (metres * 100f).ToString("0.#") + " cm";
            return metres.ToString("0.##") + " m";
        }
    }
}
