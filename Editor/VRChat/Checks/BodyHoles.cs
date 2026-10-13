using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// "Black holes": a body shape that shrinks the skin under clothing (Shrink_Chest, Hide_Arms...) left on at rest while
    /// the clothing over that skin can be toggled off (its object or its renderer), with no toggle setting the shape back
    /// and nothing always worn there instead. With the clothing off, the shrunk skin shows a hole. Clothing elsewhere (a
    /// hat over a shrunk chest) doesn't count: the clothing must lie over the skin the shape moves.
    /// </summary>
    public static class BodyHoles
    {
        // A piece of clothing lies over a shape when this share of the skin the shape moves is near it.
        private const float Covered = .25f;
        // Skin checked per shape, at most: evenly spread over what it moves.
        private const int Samples = 2000;

        public static List<AvatarIssue> Find(GameObject root, AvatarUsage usage = null)
        {
            var issues = new List<AvatarIssue>();
            var body = AttachmentPlanner.Body(root.transform);
            if (body == null || body.sharedMesh == null) return issues;
            usage ??= new AvatarUsage(root);
            var animated = usage.AnimatedAt(body.transform);
            var mesh = body.sharedMesh;
            var shapes = Enumerable.Range(0, mesh.blendShapeCount)
                .Where(i => body.GetBlendShapeWeight(i) > 0f && Shrinks(mesh.GetBlendShapeName(i)) && !animated.Contains("blendShape." + mesh.GetBlendShapeName(i))).ToList();
            if (shapes.Count == 0) return issues;
            var clothing = root.GetComponentsInChildren<Renderer>(true).Where(r => r != body && (r is SkinnedMeshRenderer || r is MeshRenderer)).ToList();
            var toggled = clothing.Where(r => usage.Toggled(r.transform) || usage.Switched(r)).ToList();
            if (toggled.Count == 0) return issues;
            var worn = clothing.Where(r => !toggled.Contains(r) && r.enabled && r.gameObject.activeInHierarchy).ToList();

            // The skin where it is without the shapes: a deep Hide_* moves it far beneath the clothing over it.
            var weights = shapes.Select(body.GetBlendShapeWeight).ToList();
            Vector3[] skin;
            try
            {
                foreach (int shape in shapes) body.SetBlendShapeWeight(shape, 0f);
                skin = Points(body, false);
            }
            finally
            {
                for (int i = 0; i < shapes.Count; i++) body.SetBlendShapeWeight(shapes[i], weights[i]);
            }
            if (skin == null) return issues;
            var size = new Bounds(skin[0], Vector3.zero);
            foreach (var point in skin) size.Encapsulate(point);
            // Clothing sits a few millimetres to centimetres off the skin: cells of 2% of the body's height.
            float cell = Mathf.Max(.005f, size.size.y * .02f);
            var grids = new Dictionary<Renderer, Grid>();
            Grid GridOf(Renderer renderer)
            {
                if (!grids.TryGetValue(renderer, out var grid)) grids[renderer] = grid = Points(renderer, true) is Vector3[] points ? new Grid(points, cell) : null;
                return grid;
            }
            bool Over(Renderer renderer, List<int> moved)
            {
                var grid = GridOf(renderer);
                return grid != null && moved.Count(v => grid.Near(skin[v])) >= Covered * moved.Count;
            }

            foreach (int shape in shapes)
            {
                var moved = Moved(mesh, shape);
                if (moved == null || moved.Count == 0) continue;
                var over = toggled.Where(r => Over(r, moved)).ToList();
                if (over.Count == 0 || worn.Any(r => Over(r, moved))) continue;
                string name = mesh.GetBlendShapeName(shape), weight = body.GetBlendShapeWeight(shape).ToString("0");
                var names = over.Select(r => r.name).Distinct().ToList();
                string pieces = string.Join(", ", names.Take(3).Select(n => "“" + n + "”")) + (names.Count > 3 ? " or others" : "");
                issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{name}” stays at {weight} while clothing over it can come off",
                    $"It hides the body under {pieces}, and no toggle sets it back: turning that clothing off shows a hole there. " +
                    $"Add “{name}” to the clothing's toggle, at {weight} with it on and 0 with it off.", body.name));
            }
            return issues;
        }

        /// <summary>Whether a body shape hides skin for clothing: Shrink_*, Hide_* and the like (not an iris or a pupil).</summary>
        public static bool Shrinks(string shape)
        {
            string name = (shape ?? "").ToLowerInvariant();
            if (name.Contains("iris") || name.Contains("pupil")) return false;
            return name.Contains("shrink") || name.StartsWith("hide", StringComparison.Ordinal) || name.Contains("_hide") || name.Contains(" hide");
        }

        // The vertices a shape moves (in its last frame), spread evenly down to the samples checked; null when unreadable.
        private static List<int> Moved(Mesh mesh, int shape)
        {
            try
            {
                var deltas = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(shape, mesh.GetBlendShapeFrameCount(shape) - 1, deltas, null, null);
                var moved = new List<int>();
                for (int v = 0; v < deltas.Length; v++) if (deltas[v].sqrMagnitude > 1e-10f) moved.Add(v);
                if (moved.Count <= Samples) return moved;
                int step = (moved.Count + Samples - 1) / Samples;
                return moved.Where((_, i) => i % step == 0).ToList();
            }
            catch (Exception) { return null; }
        }

        // A renderer's vertices in the world as it stands (skinned meshes posed), with its triangles' centres when asked
        // (sparse clothing); null when its mesh can't be read.
        private static Vector3[] Points(Renderer renderer, bool centres)
        {
            Mesh baked = null;
            try
            {
                Mesh mesh;
                Matrix4x4 toWorld;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) return null;
                    baked = new Mesh();
                    skinned.BakeMesh(baked);
                    mesh = baked;
                    toWorld = SkinnedMeshBounds.BakedToWorld(skinned);
                }
                else
                {
                    mesh = renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                    if (mesh == null) return null;
                    toWorld = renderer.transform.localToWorldMatrix;
                }
                var vertices = mesh.vertices;
                if (vertices.Length == 0) return null;
                var points = new List<Vector3>(vertices.Length);
                foreach (var v in vertices) points.Add(toWorld.MultiplyPoint3x4(v));
                if (centres)
                {
                    var triangles = mesh.triangles;
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                        points.Add(toWorld.MultiplyPoint3x4((vertices[triangles[t]] + vertices[triangles[t + 1]] + vertices[triangles[t + 2]]) / 3f));
                }
                return points.ToArray();
            }
            catch (Exception) { return null; }
            finally { if (baked != null) Object.DestroyImmediate(baked); }
        }

        // The cells a surface's points fall in: a point is near the surface when a cell around its own holds one.
        private sealed class Grid
        {
            private readonly float cell;
            private readonly HashSet<Vector3Int> cells = new HashSet<Vector3Int>();

            public Grid(Vector3[] points, float cell)
            {
                this.cell = cell;
                foreach (var point in points) cells.Add(Cell(point));
            }

            private Vector3Int Cell(Vector3 point) =>
                new Vector3Int(Mathf.FloorToInt(point.x / cell), Mathf.FloorToInt(point.y / cell), Mathf.FloorToInt(point.z / cell));

            public bool Near(Vector3 point)
            {
                var at = Cell(point);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (cells.Contains(new Vector3Int(at.x + x, at.y + y, at.z + z))) return true;
                return false;
            }
        }
    }
}
