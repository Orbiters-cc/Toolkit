using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// The body under clothing worn all the time: its triangles that opaque clothing hides from every side a player could
    /// look from go, which VRChat counts as polygons and draws for nothing. Each body vertex, posed as now, casts rays
    /// outward (along its normal and eight directions around it); a vertex is hidden when every ray meets clothing within
    /// reach, and a triangle whose three vertices are hidden goes. Skin near a collar, a cuff or a hem keeps some way out,
    /// so it stays. Clothing counts when it is always shown (never hidden or switched off), its opaque materials only,
    /// and when its animations change nothing but blendshapes the body has too (it follows the body's shape).
    /// </summary>
    public static class BodyCover
    {
        public sealed class Result
        {
            public Mesh Mesh;
            public int Triangles;
        }

        // Reach and offsets in avatar heights; the side rays lean this far from the normal.
        private const float Reach = .08f, Lift = .0008f, Lean = 55f;

        /// <summary>
        /// Gives <paramref name="body"/> a copy of its mesh without the hidden triangles; null when none are hidden or no
        /// clothing counts. The copy keeps every vertex and blendshape.
        /// </summary>
        public static Result HideCovered(AvatarUsage usage, SkinnedMeshRenderer body)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null || !Shown(body, usage) || usage.Pending(body.transform)) return null;
            var clothing = Clothing(usage, body);
            if (clothing.Count == 0) return null;

            var baked = new Mesh();
            try
            {
                body.BakeMesh(baked, false);
                var vertices = baked.vertices; var normals = baked.normals;
                if (vertices.Length != mesh.vertexCount || normals.Length != vertices.Length) return null;
                var toWorld = body.transform.localToWorldMatrix;
                for (int i = 0; i < vertices.Length; i++) { vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]); normals[i] = toWorld.MultiplyVector(normals[i]).normalized; }
                float height = Mathf.Max(.2f, Bounds(vertices).size.y);
                var grid = new Grid(clothing, height / 120f);
                var hidden = Hidden(vertices, normals, grid, height);
                return Remove(body, mesh, hidden);
            }
            finally { Object.DestroyImmediate(baked); }
        }

        // ---- What counts as clothing ----

        private static bool Shown(Renderer renderer, AvatarUsage usage)
        {
            if (!renderer.enabled || usage.Toggled(renderer.transform) || usage.Switched(renderer)) return false;
            for (var t = renderer.transform; t != null; t = t == usage.Root.transform ? null : t.parent)
                if (!t.gameObject.activeSelf || t.CompareTag("EditorOnly")) return false;
            return true;
        }

        // Opaque triangles of the clothing, in world space, as it is posed now.
        private static List<Vector3> Clothing(AvatarUsage usage, SkinnedMeshRenderer body)
        {
            var bodyShapes = new HashSet<string>(Enumerable.Range(0, body.sharedMesh.blendShapeCount).Select(body.sharedMesh.GetBlendShapeName));
            var triangles = new List<Vector3>();
            foreach (var renderer in usage.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == body || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (!Shown(renderer, usage) || usage.Pending(renderer.transform) || usage.SwapsMaterials(renderer)) continue;
                // Its own animations may only move blendshapes the body moves too.
                if (usage.AnimatedAt(renderer.transform).Any(p => !p.StartsWith("blendShape.", StringComparison.Ordinal) || !bodyShapes.Contains(p.Substring(11)))) continue;
                var source = MeshOptimization.MeshOf(renderer);
                if (source == null) continue;
                var materials = renderer.sharedMaterials;
                var opaque = Enumerable.Range(0, source.subMeshCount)
                    .Where(i => i < materials.Length && materials[i] != null && materials[i].renderQueue < (int)RenderQueue.AlphaTest && source.GetTopology(i) == MeshTopology.Triangles).ToList();
                if (opaque.Count == 0) continue;
                Vector3[] points;
                Matrix4x4 toWorld = renderer.transform.localToWorldMatrix;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    var baked = new Mesh();
                    skinned.BakeMesh(baked, false);
                    points = baked.vertices;
                    Object.DestroyImmediate(baked);
                }
                else points = source.vertices;
                for (int i = 0; i < points.Length; i++) points[i] = toWorld.MultiplyPoint3x4(points[i]);
                foreach (int sub in opaque)
                    foreach (int index in source.GetIndices(sub))
                        if (index < points.Length) triangles.Add(points[index]);
            }
            return triangles;
        }

        // ---- Which vertices are hidden ----

        private static bool[] Hidden(Vector3[] vertices, Vector3[] normals, Grid grid, float height)
        {
            float reach = Reach * height, lift = Lift * height;
            var hidden = new bool[vertices.Length];
            var directions = new Vector3[9];
            for (int v = 0; v < vertices.Length; v++)
            {
                var n = normals[v];
                if (n.sqrMagnitude < .5f || !grid.Near(vertices[v], reach)) continue;
                var origin = vertices[v] + n * lift;
                // The normal, then eight directions leaning around it.
                var side = Vector3.Cross(n, Mathf.Abs(n.y) < .9f ? Vector3.up : Vector3.right).normalized;
                directions[0] = n;
                for (int k = 0; k < 8; k++)
                {
                    var around = Quaternion.AngleAxis(k * 45f, n) * side;
                    directions[k + 1] = Quaternion.AngleAxis(Lean, Vector3.Cross(n, around)) * n;
                }
                bool all = true;
                for (int k = 0; k < 9 && all; k++) all = grid.Hit(origin, directions[k], reach);
                hidden[v] = all;
            }
            return hidden;
        }

        private static Result Remove(SkinnedMeshRenderer body, Mesh mesh, bool[] hidden)
        {
            int removed = 0;
            var kept = new List<int[]>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var indices = mesh.GetIndices(sub, true);
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) { kept.Add(indices); continue; }
                var list = new List<int>(indices.Length);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    if (hidden[indices[i]] && hidden[indices[i + 1]] && hidden[indices[i + 2]]) { removed++; continue; }
                    list.Add(indices[i]); list.Add(indices[i + 1]); list.Add(indices[i + 2]);
                }
                kept.Add(list.ToArray());
            }
            if (removed == 0) return null;
            var copy = MeshOptimization.Copy(mesh);
            for (int sub = 0; sub < mesh.subMeshCount; sub++) copy.SetIndices(kept[sub], mesh.GetTopology(sub), sub, false, 0);
            copy.bounds = mesh.bounds;
            MeshOptimization.SetMesh(body, copy);
            return new Result { Mesh = copy, Triangles = removed };
        }

        private static Bounds Bounds(Vector3[] points)
        {
            var bounds = new Bounds(points.Length > 0 ? points[0] : Vector3.zero, Vector3.zero);
            foreach (var p in points) bounds.Encapsulate(p);
            return bounds;
        }

        // ---- A grid of triangles to cast rays through ----

        private sealed class Grid
        {
            private readonly Vector3[] corners;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            private readonly float cell;
            private readonly Bounds bounds;
            private readonly int[] seen;
            private int stamp;

            public Grid(List<Vector3> triangles, float cell)
            {
                corners = triangles.ToArray();
                this.cell = Mathf.Max(.002f, cell);
                bounds = BodyCover.Bounds(corners);
                seen = new int[corners.Length / 3];
                for (int t = 0; t < seen.Length; t++)
                {
                    var a = corners[3 * t]; var b = corners[3 * t + 1]; var c = corners[3 * t + 2];
                    var min = Vector3.Min(a, Vector3.Min(b, c)); var max = Vector3.Max(a, Vector3.Max(b, c));
                    var lo = Cell(min); var hi = Cell(max);
                    for (int x = lo.x; x <= hi.x; x++)
                    for (int y = lo.y; y <= hi.y; y++)
                    for (int z = lo.z; z <= hi.z; z++)
                    {
                        long key = Key(x, y, z);
                        if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                        list.Add(t);
                    }
                }
            }

            private Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

            private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

            public bool Near(Vector3 point, float reach)
            {
                var near = bounds; near.Expand(reach * 2f);
                return near.Contains(point);
            }

            // Walks the cells along the ray (Amanatides and Woo) and tests their triangles, from both sides.
            public bool Hit(Vector3 origin, Vector3 direction, float reach)
            {
                stamp++;
                var at = Cell(origin);
                int stepX = direction.x >= 0 ? 1 : -1, stepY = direction.y >= 0 ? 1 : -1, stepZ = direction.z >= 0 ? 1 : -1;
                float Next(float o, float d, int c, int step) => Mathf.Abs(d) < 1e-9f ? float.MaxValue : ((c + (step > 0 ? 1 : 0)) * cell - o) / d;
                float tMaxX = Next(origin.x, direction.x, at.x, stepX), tMaxY = Next(origin.y, direction.y, at.y, stepY), tMaxZ = Next(origin.z, direction.z, at.z, stepZ);
                float tDeltaX = Mathf.Abs(direction.x) < 1e-9f ? float.MaxValue : cell / Mathf.Abs(direction.x);
                float tDeltaY = Mathf.Abs(direction.y) < 1e-9f ? float.MaxValue : cell / Mathf.Abs(direction.y);
                float tDeltaZ = Mathf.Abs(direction.z) < 1e-9f ? float.MaxValue : cell / Mathf.Abs(direction.z);
                float travelled = 0f;
                while (travelled <= reach)
                {
                    if (cells.TryGetValue(Key(at.x, at.y, at.z), out var list))
                        foreach (int t in list)
                        {
                            if (seen[t] == stamp) continue;
                            seen[t] = stamp;
                            if (Intersects(origin, direction, corners[3 * t], corners[3 * t + 1], corners[3 * t + 2], reach)) return true;
                        }
                    if (tMaxX < tMaxY && tMaxX < tMaxZ) { travelled = tMaxX; tMaxX += tDeltaX; at.x += stepX; }
                    else if (tMaxY < tMaxZ) { travelled = tMaxY; tMaxY += tDeltaY; at.y += stepY; }
                    else { travelled = tMaxZ; tMaxZ += tDeltaZ; at.z += stepZ; }
                }
                return false;
            }

            // Möller–Trumbore, either side of the triangle.
            private static bool Intersects(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, float reach)
            {
                var e1 = b - a; var e2 = c - a;
                var p = Vector3.Cross(direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) return false;
                float inv = 1f / det;
                var s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) return false;
                var q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(direction, q) * inv;
                if (v < 0f || u + v > 1f) return false;
                float t = Vector3.Dot(e2, q) * inv;
                return t > 0f && t <= reach;
            }
        }
    }
}
