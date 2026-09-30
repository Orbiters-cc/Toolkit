using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>Where each custom blendshape moves the body (world positions of the skin it moves), measured once per body.</summary>
    public sealed class BodyShapeMap
    {
        // Enough points to find a sleeve or a collar near a shape; the rest only costs time.
        private const int MaxPoints = 6000;

        public SkinnedMeshRenderer Body { get; private set; }
        public IReadOnlyList<string> Shapes { get; private set; }
        internal readonly Dictionary<string, Vector3[]> Moved = new Dictionary<string, Vector3[]>(StringComparer.Ordinal);

        /// <summary>Reads the body a couple of shapes per editor update, so a large body never stalls the editor.</summary>
        public static Task<BodyShapeMap> BuildAsync(SkinnedMeshRenderer body, IReadOnlyList<string> shapes, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<BodyShapeMap>();
            BodyShapeMap map = null;
            RefitRunner.Drive(Build(body, shapes, m => map = m, cancellation), error =>
            {
                if (error != null) completion.TrySetException(error);
                else if (cancellation.IsCancellationRequested) completion.TrySetCanceled();
                else completion.TrySetResult(map);
            });
            return completion.Task;
        }

        private static IEnumerator Build(SkinnedMeshRenderer body, IReadOnlyList<string> shapes, Action<BodyShapeMap> done, CancellationToken cancellation)
        {
            var map = new BodyShapeMap { Body = body, Shapes = shapes.ToList() };
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null || shapes.Count == 0) { done(map); yield break; }
            var baked = new Mesh();
            Vector3[] positions;
            try
            {
                body.BakeMesh(baked, true);
                positions = baked.vertices;
                // A renderer without a skeleton bakes nothing: its mesh as it stands.
                if (positions.Length != mesh.vertexCount)
                {
                    positions = mesh.vertices;
                    baked.vertices = positions;
                }
                // BakeMesh leaves the bounds empty.
                baked.RecalculateBounds();
                // Blendshape offsets are in the mesh's own units; the baked mesh is in the avatar's (import scale, bones).
                var raw = mesh.bounds.size;
                float scale = Mathf.Max(baked.bounds.size.x, baked.bounds.size.y, baked.bounds.size.z) /
                              Mathf.Max(1e-6f, Mathf.Max(raw.x, Mathf.Max(raw.y, raw.z)));
                var toWorld = Matrix4x4.TRS(body.transform.position, body.transform.rotation, Vector3.one);
                for (int i = 0; i < positions.Length; i++) positions[i] = toWorld.MultiplyPoint3x4(positions[i]);
                map.scale = scale;
            }
            finally { Object.DestroyImmediate(baked); }
            yield return null;

            var deltas = new Vector3[mesh.vertexCount];
            float minimum = RefitRelevance.MinMove / Mathf.Max(1e-6f, map.scale);
            foreach (string shape in shapes)
            {
                if (cancellation.IsCancellationRequested) yield break;
                int index = mesh.GetBlendShapeIndex(shape);
                if (index < 0) continue;
                mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, deltas, null, null);
                float minimumSq = minimum * minimum;
                var moved = new List<Vector3>();
                for (int v = 0; v < deltas.Length && v < positions.Length; v++)
                    if (deltas[v].sqrMagnitude >= minimumSq) moved.Add(positions[v]);
                map.Moved[shape] = Sample(moved);
                yield return null;
            }
            done(map);
        }

        private float scale = 1f;

        private static Vector3[] Sample(List<Vector3> points)
        {
            if (points.Count <= MaxPoints) return points.ToArray();
            var sampled = new Vector3[MaxPoints];
            float step = (float)points.Count / MaxPoints;
            for (int i = 0; i < MaxPoints; i++) sampled[i] = points[(int)(i * step)];
            return sampled;
        }
    }

    /// <summary>Which custom blendshapes move the body close enough to a mesh that the mesh should follow them.</summary>
    public static class RefitRelevance
    {
        /// <summary>A mesh follows a shape when enough of it lies this close to skin the shape moves.</summary>
        public const float Reach = 0.05f;
        /// <summary>Skin moving less than this is not moved by the shape.</summary>
        public const float MinMove = 0.0015f;
        private const int MaxMeshPoints = 20000;

        public static async Task<Dictionary<SkinnedMeshRenderer, List<string>>> AnalyzeAsync(BodyShapeMap map, IReadOnlyList<SkinnedMeshRenderer> meshes,
            CancellationToken cancellation)
        {
            var result = new Dictionary<SkinnedMeshRenderer, List<string>>();
            if (map == null || meshes == null || meshes.Count == 0) return result;
            // Baking is main-thread work; the search runs on a worker.
            var points = new List<(SkinnedMeshRenderer mesh, Vector3[] points)>();
            foreach (var mesh in meshes)
            {
                if (mesh == null || mesh.sharedMesh == null) continue;
                points.Add((mesh, WorldPoints(mesh)));
                result[mesh] = new List<string>();
            }
            var shapes = map.Shapes.Where(s => map.Moved.ContainsKey(s)).ToList();
            var moved = shapes.Select(s => map.Moved[s]).ToList();
            var near = await Task.Run(() => Near(moved, points.Select(p => p.points).ToList(), cancellation), cancellation);
            for (int m = 0; m < points.Count; m++)
                for (int s = 0; s < shapes.Count; s++)
                    if (near[m][s]) result[points[m].mesh].Add(shapes[s]);
            return result;
        }

        internal static Vector3[] WorldPoints(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh();
            try
            {
                renderer.BakeMesh(baked, true);
                var vertices = baked.vertices;
                if (vertices.Length != renderer.sharedMesh.vertexCount)
                {
                    vertices = renderer.sharedMesh.vertices;
                    var scale = renderer.transform.lossyScale;
                    for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], scale);
                }
                var toWorld = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                int step = Mathf.Max(1, vertices.Length / MaxMeshPoints);
                var points = new Vector3[(vertices.Length + step - 1) / step];
                for (int i = 0, j = 0; i < vertices.Length; i += step, j++) points[j] = toWorld.MultiplyPoint3x4(vertices[i]);
                return points;
            }
            finally { Object.DestroyImmediate(baked); }
        }

        // near[mesh][shape]: at least a few of the mesh's points (0.2% of them, 3 at least) within Reach of skin the shape moves.
        internal static bool[][] Near(List<Vector3[]> moved, List<Vector3[]> meshes, CancellationToken cancellation)
        {
            var near = meshes.Select(_ => new bool[moved.Count]).ToArray();
            float reachSq = Reach * Reach;
            for (int s = 0; s < moved.Count; s++)
            {
                cancellation.ThrowIfCancellationRequested();
                var grid = new Dictionary<Vector3Int, List<Vector3>>();
                foreach (var point in moved[s])
                {
                    var cell = Cell(point);
                    if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<Vector3>();
                    list.Add(point);
                }
                if (grid.Count == 0) continue;
                for (int m = 0; m < meshes.Count; m++)
                {
                    int needed = Mathf.Max(3, meshes[m].Length / 500), found = 0;
                    foreach (var point in meshes[m])
                    {
                        if (!Close(grid, point, reachSq)) continue;
                        if (++found >= needed) { near[m][s] = true; break; }
                    }
                }
            }
            return near;
        }

        private static bool Close(Dictionary<Vector3Int, List<Vector3>> grid, Vector3 point, float reachSq)
        {
            var center = Cell(point);
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
                if (grid.TryGetValue(new Vector3Int(center.x + x, center.y + y, center.z + z), out var list))
                    foreach (var other in list)
                        if ((other - point).sqrMagnitude <= reachSq) return true;
            return false;
        }

        private static Vector3Int Cell(Vector3 point) =>
            new Vector3Int(Mathf.FloorToInt(point.x / Reach), Mathf.FloorToInt(point.y / Reach), Mathf.FloorToInt(point.z / Reach));
    }
}
