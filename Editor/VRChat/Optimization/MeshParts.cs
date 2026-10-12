using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Takes a mesh apart and puts it back together: the triangles chosen, with only the vertices they use (every attribute,
    /// bone weights, bind poses, submeshes, and the blendshapes asked for), and two meshes joined into one. A step that may
    /// only change part of a mesh (simplifying what no expression moves) works on that part alone.
    /// </summary>
    public static class MeshParts
    {
        /// <summary>How many vertices each blendshape moves (in some frame, by more than a hundredth of a millimetre).</summary>
        public static int[] Reach(Mesh mesh, out bool[][] moved)
        {
            var reach = new int[mesh.blendShapeCount];
            moved = new bool[mesh.blendShapeCount][];
            var deltas = new Vector3[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                var by = moved[shape] = new bool[mesh.vertexCount];
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, deltas, null, null);
                    for (int v = 0; v < deltas.Length; v++) if (!by[v] && deltas[v].sqrMagnitude > 1e-10f) { by[v] = true; reach[shape]++; }
                }
            }
            return reach;
        }

        /// <summary>
        /// The triangles of each submesh that <paramref name="keep"/> keeps (by their three vertex indices), with only the
        /// vertices they use. The submeshes stay, empty ones too, so material slots line up. The blendshapes
        /// <paramref name="shape"/> picks (by index) come along; none without it.
        /// </summary>
        public static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape = null) => Subset(mesh, keep, shape, out _);

        /// <summary>The same, with the source vertex of each of the part's vertices.</summary>
        public static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape, out int[] vertices)
        {
            var map = new int[mesh.vertexCount];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var used = new List<int>();
            var submeshes = new List<int[]>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndices(s);
                var kept = new List<int>();
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    if (!keep(indices[t], indices[t + 1], indices[t + 2])) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = indices[t + k];
                        if (map[v] < 0) { map[v] = used.Count; used.Add(v); }
                        kept.Add(map[v]);
                    }
                }
                submeshes.Add(kept.ToArray());
            }
            vertices = used.ToArray();
            var part = new Mesh { name = mesh.name, indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            Copy(mesh, part, used);
            part.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) part.SetIndices(submeshes[s], MeshTopology.Triangles, s, false);
            if (shape != null)
            {
                var picked = vertices;
                var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                var mv = new Vector3[used.Count]; var mn = new Vector3[used.Count]; var mt = new Vector3[used.Count];
                for (int index = 0; index < mesh.blendShapeCount; index++)
                {
                    if (!shape(index)) continue;
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                        for (int i = 0; i < picked.Length; i++) { mv[i] = dv[picked[i]]; mn[i] = dn[picked[i]]; mt[i] = dt[picked[i]]; }
                        part.AddBlendShapeFrame(mesh.GetBlendShapeName(index), mesh.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
                    }
                }
            }
            part.bindposes = mesh.bindposes;
            part.RecalculateBounds();
            return part;
        }

        /// <summary>Leaves out the submeshes without triangles; returns, for each one left, the submesh it was.</summary>
        public static int[] DropEmptySubmeshes(Mesh mesh)
        {
            var slots = Enumerable.Range(0, mesh.subMeshCount).Where(s => mesh.GetIndexCount(s) > 0).ToArray();
            if (slots.Length == mesh.subMeshCount) return slots;
            var indices = slots.Select(s => mesh.GetIndices(s)).ToList();
            mesh.subMeshCount = slots.Length;
            for (int s = 0; s < slots.Length; s++) mesh.SetIndices(indices[s], MeshTopology.Triangles, s, false);
            return slots;
        }

        /// <summary>
        /// <paramref name="first"/> and <paramref name="second"/> as one mesh, with <paramref name="first"/>'s submeshes:
        /// each of <paramref name="second"/>'s goes into the one <paramref name="slots"/> names (the same index without it).
        /// Blendshapes join by name, in <paramref name="first"/>'s order: each moves what it moved in either mesh. Both use
        /// the same bones and bind poses.
        /// </summary>
        public static Mesh Join(Mesh first, Mesh second, int[] slots = null) => Join(first, second, slots, true);

        private static Mesh Join(Mesh first, Mesh second, int[] slots, bool shapes)
        {
            int a = first.vertexCount, total = a + second.vertexCount;
            var joined = new Mesh { name = first.name, indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            Append(first, second, joined);
            var indices = Enumerable.Range(0, first.subMeshCount).Select(s => first.GetIndices(s).ToList()).ToList();
            for (int s = 0; s < second.subMeshCount; s++)
            {
                int slot = slots != null ? slots[s] : s;
                while (indices.Count <= slot) indices.Add(new List<int>());
                indices[slot].AddRange(second.GetIndices(s).Select(i => i + a));
            }
            joined.subMeshCount = indices.Count;
            for (int s = 0; s < indices.Count; s++) joined.SetIndices(indices[s], MeshTopology.Triangles, s, false);
            joined.bindposes = first.bindposes;
            if (!shapes) { joined.RecalculateBounds(); return joined; }

            var names = Enumerable.Range(0, first.blendShapeCount).Select(first.GetBlendShapeName)
                .Concat(Enumerable.Range(0, second.blendShapeCount).Select(second.GetBlendShapeName)).Distinct().ToList();
            var mv = new Vector3[total]; var mn = new Vector3[total]; var mt = new Vector3[total];
            foreach (var name in names)
            {
                int i1 = first.GetBlendShapeIndex(name), i2 = second.GetBlendShapeIndex(name);
                var reference = i1 >= 0 ? first : second;
                int at = i1 >= 0 ? i1 : i2;
                for (int frame = 0; frame < reference.GetBlendShapeFrameCount(at); frame++)
                {
                    Array.Clear(mv, 0, total); Array.Clear(mn, 0, total); Array.Clear(mt, 0, total);
                    if (i1 >= 0) Frame(first, i1, frame, mv, mn, mt, 0);
                    if (i2 >= 0 && frame < second.GetBlendShapeFrameCount(i2)) Frame(second, i2, frame, mv, mn, mt, a);
                    joined.AddBlendShapeFrame(name, reference.GetBlendShapeFrameWeight(at, frame), mv, mn, mt);
                }
            }
            joined.bindposes = first.bindposes;
            joined.RecalculateBounds();
            return joined;
        }

        /// <summary>
        /// <paramref name="mesh"/> with the triangles <paramref name="keep"/> keeps as they are and
        /// <paramref name="replacement"/> in place of the others (each of its submeshes into the one <paramref name="slots"/>
        /// names). The blendshapes <paramref name="shape"/> picks stay, each also moving what the shape of that name moves
        /// in <paramref name="replacement"/>. One pass over the blendshapes: a body has hundreds.
        /// </summary>
        public static Mesh Replace(Mesh mesh, Func<int, int, int, bool> keep, Mesh replacement, int[] slots, Func<int, bool> shape)
        {
            var kept = Subset(mesh, keep, null, out var vertices);
            try
            {
                var joined = Join(kept, replacement, slots, false);
                int offset = kept.vertexCount, total = joined.vertexCount, extra = replacement.vertexCount;
                var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                var rv = new Vector3[extra]; var rn = new Vector3[extra]; var rt = new Vector3[extra];
                var mv = new Vector3[total]; var mn = new Vector3[total]; var mt = new Vector3[total];
                for (int index = 0; index < mesh.blendShapeCount; index++)
                {
                    if (!shape(index)) continue;
                    string name = mesh.GetBlendShapeName(index);
                    int other = replacement.GetBlendShapeIndex(name);
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                        for (int i = 0; i < vertices.Length; i++) { mv[i] = dv[vertices[i]]; mn[i] = dn[vertices[i]]; mt[i] = dt[vertices[i]]; }
                        if (other >= 0 && frame < replacement.GetBlendShapeFrameCount(other))
                        {
                            replacement.GetBlendShapeFrameVertices(other, frame, rv, rn, rt);
                            Array.Copy(rv, 0, mv, offset, extra); Array.Copy(rn, 0, mn, offset, extra); Array.Copy(rt, 0, mt, offset, extra);
                        }
                        else { Array.Clear(mv, offset, extra); Array.Clear(mn, offset, extra); Array.Clear(mt, offset, extra); }
                        joined.AddBlendShapeFrame(name, mesh.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
                    }
                }
                return joined;
            }
            finally { UnityEngine.Object.DestroyImmediate(kept); }
        }

        /// <summary>
        /// Whether <paramref name="points"/> lie on the surface of <paramref name="triangles"/> (indices into
        /// <paramref name="vertices"/>) within <paramref name="budget"/>: 99% of them, and none beyond twice it. With the
        /// points' <paramref name="normals"/> and the surface's <paramref name="vertexNormals"/>, the surface also shades
        /// the same: at each point, the nearest triangle's normal (interpolated where it is closest) within
        /// <paramref name="angle"/> of the point's, but for one point in 500. Folded or flipped triangles turn their
        /// normals far beyond it even where they stay close; a surface only smoothed turns them a little (on hair cards at
        /// 95% of their triangles, one point in 800 beyond 60 degrees, none beyond 90; on a body folded by a too coarse
        /// simplification, one in 120). On a hard edge or a two-sided card, any triangle about as near (within a quarter
        /// of the budget of the nearest) may match. Stops at the first point that settles it.
        /// </summary>
        public static bool Within(Vector3[] points, Vector3[] vertices, int[] triangles, float budget, Vector3[] normals = null, Vector3[] vertexNormals = null,
            float angle = 60f)
        {
            float minDot = Mathf.Cos(angle * Mathf.Deg2Rad);
            var surface = new Surface(vertices, triangles, 2f * budget);
            bool shading = normals != null && vertexNormals != null && vertexNormals.Length == vertices.Length;
            int over = 0, allowed = points.Length / 100, turned = 0, tolerated = points.Length / 500;
            for (int i = 0; i < points.Length; i++)
            {
                var (distance, _) = surface.Nearest(points[i], budget);
                if (distance > 2f * budget || distance > budget && ++over > allowed) return false;
                if (shading && !surface.Shades(points[i], normals[i], budget / 4f, vertexNormals, minDot) && ++turned > tolerated) return false;
            }
            return true;
        }

        /// <summary>
        /// Gives <paramref name="to"/> the blendshapes of <paramref name="from"/> that <paramref name="shape"/> picks (by index),
        /// for a <paramref name="to"/> made from <paramref name="from"/>'s surface (simplified, say): each of its vertices
        /// moves as the point of <paramref name="from"/> nearest to it does, its offsets interpolated across that triangle
        /// (positions, normals and tangents alike, offsets as they are). Where two sides meet at a point (a seam, a hard
        /// edge), the side whose normal matches the vertex's is used.
        /// </summary>
        public static void TransferShapes(Mesh from, Mesh to, Func<int, bool> shape)
        {
            var vertices = from.vertices;
            var triangles = from.triangles;
            var normals = from.normals.Length == vertices.Length ? from.normals : null;
            double edges = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3) edges += (vertices[triangles[t]] - vertices[triangles[t + 1]]).magnitude;
            // A new vertex lies on the surface it was made from or next to it: a triangle and a half around is far enough.
            float reach = triangles.Length > 0 ? 1.5f * (float)(edges / (triangles.Length / 3)) : 1f;
            var surface = new Surface(vertices, triangles, reach);
            var targets = to.vertices;
            var targetNormals = to.normals.Length == targets.Length ? to.normals : null;
            // For each new vertex: three of the source's vertices and how much each counts.
            var corners = new int[targets.Length * 3];
            var weights = new float[targets.Length * 3];
            for (int i = 0; i < targets.Length; i++)
            {
                var (t, b) = surface.Closest(targets[i], targetNormals != null ? targetNormals[i] : (Vector3?)null, normals, reach / 16f);
                if (t < 0) { corners[3 * i] = corners[3 * i + 1] = corners[3 * i + 2] = 0; weights[3 * i] = weights[3 * i + 1] = weights[3 * i + 2] = 0f; continue; }
                corners[3 * i] = triangles[t]; corners[3 * i + 1] = triangles[t + 1]; corners[3 * i + 2] = triangles[t + 2];
                weights[3 * i] = b.x; weights[3 * i + 1] = b.y; weights[3 * i + 2] = b.z;
            }
            var dv = new Vector3[from.vertexCount]; var dn = new Vector3[from.vertexCount]; var dt = new Vector3[from.vertexCount];
            var mv = new Vector3[targets.Length]; var mn = new Vector3[targets.Length]; var mt = new Vector3[targets.Length];
            for (int index = 0; index < from.blendShapeCount; index++)
            {
                if (!shape(index)) continue;
                for (int frame = 0; frame < from.GetBlendShapeFrameCount(index); frame++)
                {
                    from.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                    for (int i = 0; i < targets.Length; i++)
                    {
                        int a = corners[3 * i], b = corners[3 * i + 1], c = corners[3 * i + 2];
                        float wa = weights[3 * i], wb = weights[3 * i + 1], wc = weights[3 * i + 2];
                        mv[i] = dv[a] * wa + dv[b] * wb + dv[c] * wc;
                        mn[i] = dn[a] * wa + dn[b] * wb + dn[c] * wc;
                        mt[i] = dt[a] * wa + dt[b] * wb + dt[c] * wc;
                    }
                    to.AddBlendShapeFrame(from.GetBlendShapeName(index), from.GetBlendShapeFrameWeight(index, frame), mv, mn, mt);
                }
            }
        }

        // Triangles in cells about a triangle wide (at least the reach): each fills a few, each cell holds a few.
        private sealed class Surface
        {
            private readonly Vector3[] vertices;
            private readonly int[] triangles;
            private readonly float cell, reach;
            private readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            // A triangle spanning many cells (a long sliver) is checked by every point instead of filling them all.
            private readonly List<int> large = new List<int>();

            public Surface(Vector3[] vertices, int[] triangles, float reach)
            {
                this.vertices = vertices; this.triangles = triangles; this.reach = reach;
                double edges = 0;
                for (int t = 0; t + 2 < triangles.Length; t += 3) edges += (vertices[triangles[t]] - vertices[triangles[t + 1]]).magnitude;
                cell = Mathf.Max(reach, triangles.Length > 0 ? (float)(edges / (triangles.Length / 3)) : reach);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                    var from = Vector3Int.FloorToInt(Vector3.Min(a, Vector3.Min(b, c)) / cell);
                    var to = Vector3Int.FloorToInt(Vector3.Max(a, Vector3.Max(b, c)) / cell);
                    if ((long)(to.x - from.x + 1) * (to.y - from.y + 1) * (to.z - from.z + 1) > 64) { large.Add(t); continue; }
                    for (int x = from.x; x <= to.x; x++)
                        for (int y = from.y; y <= to.y; y++)
                            for (int z = from.z; z <= to.z; z++)
                            {
                                long key = Cell(x, y, z);
                                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                                list.Add(t);
                            }
                }
            }

            // The distance to the nearest triangle and that triangle, up to the reach (beyond it: more than the reach, no
            // triangle). Stops at the first one within the good enough distance.
            public (float Distance, int Triangle) Nearest(Vector3 p, float good)
            {
                float best = reach + 1f;
                int nearest = -1;
                void Test(int t)
                {
                    float d = PointTriangle(p, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]);
                    if (d < best) { best = d; nearest = t; }
                }
                foreach (int t in large) Test(t);
                var at = Vector3Int.FloorToInt(p / cell);
                for (int x = -1; x <= 1 && best > good; x++)
                    for (int y = -1; y <= 1 && best > good; y++)
                        for (int z = -1; z <= 1 && best > good; z++)
                            if (grid.TryGetValue(Cell(at.x + x, at.y + y, at.z + z), out var list))
                                foreach (int t in list) Test(t);
                return (best, nearest);
            }

            // Whether p is shaded like the normal n there: by one of the triangles about as near as the nearest (within the
            // slack of it: both sides of a hard edge or a two-sided card), not any triangle further around.
            public bool Shades(Vector3 p, Vector3 n, float slack, Vector3[] normals, float minDot)
            {
                var (nearest, _) = Nearest(p, 0f);
                if (nearest > reach) return false;
                float within = nearest + slack;
                bool Test(int t) => PointTriangle(p, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]) <= within &&
                                    Vector3.Dot(Normal(p, t, normals), n) >= minDot;
                foreach (int t in large) if (Test(t)) return true;
                var at = Vector3Int.FloorToInt(p / cell);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (grid.TryGetValue(Cell(at.x + x, at.y + y, at.z + z), out var list))
                                foreach (int t in list) if (Test(t)) return true;
                return false;
            }

            // The triangle nearest to p and the barycentric coordinates of its point closest to p. With a normal, among the
            // triangles about as near (within the slack of the nearest), the one shading most like it.
            public (int Triangle, Vector3 Barycentric) Closest(Vector3 p, Vector3? normal, Vector3[] normals, float slack)
            {
                var (nearest, triangle) = Nearest(p, 0f);
                if (triangle < 0) return (-1, Vector3.zero);
                if (normal != null && normals != null)
                {
                    float best = -2f;
                    void Test(int t)
                    {
                        if (PointTriangle(p, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]) > nearest + slack) return;
                        float dot = Vector3.Dot(Normal(p, t, normals), normal.Value);
                        if (dot > best) { best = dot; triangle = t; }
                    }
                    foreach (int t in large) Test(t);
                    var at = Vector3Int.FloorToInt(p / cell);
                    for (int x = -1; x <= 1; x++)
                        for (int y = -1; y <= 1; y++)
                            for (int z = -1; z <= 1; z++)
                                if (grid.TryGetValue(Cell(at.x + x, at.y + y, at.z + z), out var list))
                                    foreach (int t in list) Test(t);
                }
                return (triangle, Barycentric(p, triangle));
            }

            // The barycentric coordinates of the point of the triangle closest to p (p projected, clamped inside).
            private Vector3 Barycentric(Vector3 p, int t)
            {
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                var n = Vector3.Cross(b - a, c - a);
                var q = n.sqrMagnitude > 0 ? p - Vector3.Dot(p - a, n) / n.sqrMagnitude * n : a;
                Vector3 v0 = b - a, v1 = c - a, v2 = q - a;
                float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                float denominator = d00 * d11 - d01 * d01;
                float v = denominator != 0 ? (d11 * d20 - d01 * d21) / denominator : 0f, w = denominator != 0 ? (d00 * d21 - d01 * d20) / denominator : 0f;
                v = Mathf.Clamp01(v); w = Mathf.Clamp01(w);
                if (v + w > 1f) { float sum = v + w; v /= sum; w /= sum; }
                return new Vector3(1f - v - w, v, w);
            }

            // The vertex normals of the triangle interpolated at its point closest to p.
            private Vector3 Normal(Vector3 p, int t, Vector3[] normals)
            {
                var b = Barycentric(p, t);
                return (b.x * normals[triangles[t]] + b.y * normals[triangles[t + 1]] + b.z * normals[triangles[t + 2]]).normalized;
            }
        }

        private static long Cell(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

        // The distance from p to the triangle abc (closest point by regions, Ericson's Real-Time Collision Detection 5.1.5).
        private static float PointTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.magnitude;
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.magnitude;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).magnitude;
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.magnitude;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).magnitude;
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))))).magnitude;
            float denominator = 1f / (va + vb + vc);
            return (p - (a + ab * (vb * denominator) + ac * (vc * denominator))).magnitude;
        }

        private static void Frame(Mesh mesh, int shape, int frame, Vector3[] mv, Vector3[] mn, Vector3[] mt, int offset)
        {
            var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
            Array.Copy(dv, 0, mv, offset, dv.Length); Array.Copy(dn, 0, mn, offset, dn.Length); Array.Copy(dt, 0, mt, offset, dt.Length);
        }

        // The listed vertices of source, every attribute it has, into target.
        private static void Copy(Mesh source, Mesh target, List<int> vertices)
        {
            target.SetVertices(Pick(source.vertices, vertices));
            if (source.normals.Length > 0) target.SetNormals(Pick(source.normals, vertices));
            if (source.tangents.Length > 0) target.SetTangents(Pick(source.tangents, vertices));
            if (source.colors.Length > 0) target.SetColors(Pick(source.colors, vertices));
            var buffer = new List<Vector4>();
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!source.HasVertexAttribute(attribute)) continue;
                source.GetUVs(channel, buffer);
                SetUVs(target, channel, source.GetVertexAttributeDimension(attribute), Pick(buffer, vertices));
            }
            var counts = source.GetBonesPerVertex();
            if (counts.Length == 0) return;
            var weights = source.GetAllBoneWeights();
            var starts = new int[counts.Length];
            for (int v = 1; v < counts.Length; v++) starts[v] = starts[v - 1] + counts[v - 1];
            var newCounts = new byte[vertices.Count];
            var newWeights = new List<BoneWeight1>();
            for (int i = 0; i < vertices.Count; i++)
            {
                int v = vertices[i];
                newCounts[i] = counts[v];
                for (int w = 0; w < counts[v]; w++) newWeights.Add(weights[starts[v] + w]);
            }
            using (var perVertex = new NativeArray<byte>(newCounts, Allocator.Temp))
            using (var all = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
                target.SetBoneWeights(perVertex, all);
        }

        // Both meshes' vertices one after the other; an attribute only one has gets defaults on the other's.
        private static void Append(Mesh first, Mesh second, Mesh target)
        {
            target.SetVertices(first.vertices.Concat(second.vertices).ToList());
            if (first.normals.Length > 0 || second.normals.Length > 0)
                target.SetNormals(Or(first.normals, first.vertexCount, Vector3.up).Concat(Or(second.normals, second.vertexCount, Vector3.up)).ToList());
            if (first.tangents.Length > 0 || second.tangents.Length > 0)
                target.SetTangents(Or(first.tangents, first.vertexCount, new Vector4(1, 0, 0, 1)).Concat(Or(second.tangents, second.vertexCount, new Vector4(1, 0, 0, 1))).ToList());
            if (first.colors.Length > 0 || second.colors.Length > 0)
                target.SetColors(Or(first.colors, first.vertexCount, Color.white).Concat(Or(second.colors, second.vertexCount, Color.white)).ToList());
            var a = new List<Vector4>(); var b = new List<Vector4>();
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                int dimension = Math.Max(first.HasVertexAttribute(attribute) ? first.GetVertexAttributeDimension(attribute) : 0,
                                         second.HasVertexAttribute(attribute) ? second.GetVertexAttributeDimension(attribute) : 0);
                if (dimension == 0) continue;
                a.Clear(); b.Clear();
                if (first.HasVertexAttribute(attribute)) first.GetUVs(channel, a); else a.AddRange(Enumerable.Repeat(Vector4.zero, first.vertexCount));
                if (second.HasVertexAttribute(attribute)) second.GetUVs(channel, b); else b.AddRange(Enumerable.Repeat(Vector4.zero, second.vertexCount));
                SetUVs(target, channel, dimension, a.Concat(b).ToList());
            }
            var counts = first.GetBonesPerVertex().Concat(second.GetBonesPerVertex()).ToArray();
            if (counts.Length != first.vertexCount + second.vertexCount) return;
            using (var perVertex = new NativeArray<byte>(counts, Allocator.Temp))
            using (var all = new NativeArray<BoneWeight1>(first.GetAllBoneWeights().Concat(second.GetAllBoneWeights()).ToArray(), Allocator.Temp))
                target.SetBoneWeights(perVertex, all);
        }

        private static List<T> Pick<T>(IList<T> values, List<int> vertices)
        {
            var picked = new List<T>(vertices.Count);
            foreach (int v in vertices) picked.Add(values[v]);
            return picked;
        }

        private static IEnumerable<T> Or<T>(T[] values, int count, T fallback) => values.Length > 0 ? values : Enumerable.Repeat(fallback, count);

        private static void SetUVs(Mesh mesh, int channel, int dimension, List<Vector4> uvs)
        {
            if (dimension <= 2) mesh.SetUVs(channel, uvs.Select(u => (Vector2)u).ToList());
            else if (dimension == 3) mesh.SetUVs(channel, uvs.Select(u => (Vector3)u).ToList());
            else mesh.SetUVs(channel, uvs);
        }
    }
}
