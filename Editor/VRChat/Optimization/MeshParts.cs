using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Meshes;
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
            var table = MeshShapes.Of(mesh);
            moved = new bool[table.ShapeCount][];
            for (int shape = 0; shape < moved.Length; shape++) moved[shape] = new bool[mesh.vertexCount];
            return Reach(table, moved);
        }

        /// <summary>
        /// The same from a table (any thread); <paramref name="moved"/>, when given, receives the vertices each shape moves
        /// (one array per shape, as long as the mesh).
        /// </summary>
        public static int[] Reach(BlendShapeTable table, bool[][] moved = null)
        {
            var reach = new int[table.ShapeCount];
            var scratch = moved == null ? new bool[table.VertexCount] : null;
            for (int shape = 0; shape < reach.Length; shape++)
            {
                var marks = moved != null ? moved[shape] : scratch;
                var frames = table.Shapes[shape].Frames;
                foreach (var frame in frames)
                {
                    var indices = frame.Indices;
                    var deltas = frame.Deltas;
                    for (int k = 0; k < indices.Length; k++)
                        if (!marks[indices[k]] && deltas[k].sqrMagnitude > 1e-10f) { marks[indices[k]] = true; reach[shape]++; }
                }
                // The scratch marks go back to false: only what this shape marked.
                if (moved == null)
                    foreach (var frame in frames)
                        foreach (int v in frame.Indices) scratch[v] = false;
            }
            return reach;
        }

        /// <summary>
        /// The triangles of each submesh that <paramref name="keep"/> keeps (by their three vertex indices), with only the
        /// vertices they use. The submeshes stay, empty ones too, so material slots line up; one of lines or points has no
        /// triangles to choose and stays empty. The blendshapes <paramref name="shape"/> picks (by index) come along; none
        /// without it.
        /// </summary>
        public static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape = null) => Subset(mesh, keep, shape, out _);

        /// <summary>The same, with the source vertex of each of the part's vertices.</summary>
        public static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape, out int[] vertices) =>
            Subset(mesh, keep, shape, out vertices, false);

        // With otherTopologies, submeshes of lines or points come whole.
        private static Mesh Subset(Mesh mesh, Func<int, int, int, bool> keep, Func<int, bool> shape, out int[] vertices, bool otherTopologies)
        {
            var map = new int[mesh.vertexCount];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var used = new List<int>();
            var submeshes = new List<(int[] Indices, MeshTopology Topology)>();
            int Map(int v)
            {
                if (map[v] < 0) { map[v] = used.Count; used.Add(v); }
                return map[v];
            }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndices(s);
                var topology = mesh.GetTopology(s);
                var kept = new List<int>();
                if (topology != MeshTopology.Triangles)
                {
                    if (otherTopologies) foreach (int v in indices) kept.Add(Map(v));
                }
                else
                    for (int t = 0; t + 2 < indices.Length; t += 3)
                    {
                        if (!keep(indices[t], indices[t + 1], indices[t + 2])) continue;
                        for (int k = 0; k < 3; k++) kept.Add(Map(indices[t + k]));
                    }
                submeshes.Add((kept.ToArray(), topology));
            }
            vertices = used.ToArray();
            var part = new Mesh { name = mesh.name, indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            Copy(mesh, part, used);
            part.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) part.SetIndices(submeshes[s].Indices, submeshes[s].Topology, s, false);
            // The picked shapes on the part's vertices, written once.
            if (shape != null && mesh.blendShapeCount > 0)
                MeshShapes.Write(part, MeshShapes.Picked(MeshShapes.Of(mesh), shape).Remap(map, used.Count));
            part.bindposes = mesh.bindposes;
            part.RecalculateBounds();
            return part;
        }

        /// <summary>Leaves out the empty submeshes; returns, for each one left, the submesh it was.</summary>
        public static int[] DropEmptySubmeshes(Mesh mesh)
        {
            var slots = Enumerable.Range(0, mesh.subMeshCount).Where(s => mesh.GetIndexCount(s) > 0).ToArray();
            if (slots.Length == mesh.subMeshCount) return slots;
            var indices = slots.Select(s => mesh.GetIndices(s)).ToList();
            var topologies = slots.Select(mesh.GetTopology).ToList();
            mesh.subMeshCount = slots.Length;
            for (int s = 0; s < slots.Length; s++) mesh.SetIndices(indices[s], topologies[s], s, false);
            return slots;
        }

        /// <summary>
        /// <paramref name="first"/> and <paramref name="second"/> as one mesh, with <paramref name="first"/>'s submeshes:
        /// each of <paramref name="second"/>'s goes into the one <paramref name="slots"/> names (the same index without it),
        /// which draws the same topology. Blendshapes join by name, in <paramref name="first"/>'s order: each moves what it
        /// moved in either mesh, at every weight either has a frame at. Both use the same bones and bind poses.
        /// </summary>
        public static Mesh Join(Mesh first, Mesh second, int[] slots = null) => Join(first, second, slots, true);

        private static Mesh Join(Mesh first, Mesh second, int[] slots, bool shapes)
        {
            int a = first.vertexCount, total = a + second.vertexCount;
            var joined = new Mesh { name = first.name, indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            Append(first, second, joined);
            var indices = Enumerable.Range(0, first.subMeshCount).Select(s => first.GetIndices(s).ToList()).ToList();
            var topologies = Enumerable.Range(0, first.subMeshCount).Select(first.GetTopology).ToList();
            for (int s = 0; s < second.subMeshCount; s++)
            {
                int slot = slots != null ? slots[s] : s;
                while (indices.Count <= slot) { indices.Add(new List<int>()); topologies.Add(second.GetTopology(s)); }
                if (topologies[slot] != second.GetTopology(s) && indices[slot].Count > 0 && second.GetIndexCount(s) > 0)
                    throw new ArgumentException("Submesh " + s + " draws " + second.GetTopology(s) + " into a slot of " + topologies[slot] + ".", nameof(slots));
                if (indices[slot].Count == 0) topologies[slot] = second.GetTopology(s);
                indices[slot].AddRange(second.GetIndices(s).Select(i => i + a));
            }
            joined.subMeshCount = indices.Count;
            for (int s = 0; s < indices.Count; s++) joined.SetIndices(indices[s], topologies[s], s, false);
            joined.bindposes = first.bindposes;
            if (!shapes) { joined.RecalculateBounds(); return joined; }

            var names = Enumerable.Range(0, first.blendShapeCount).Select(first.GetBlendShapeName)
                .Concat(Enumerable.Range(0, second.blendShapeCount).Select(second.GetBlendShapeName)).Distinct().ToList();
            if (names.Count > 0)
            {
                var one = MeshShapes.Of(first);
                var two = MeshShapes.Of(second);
                var frame = new MeshShapes.FrameBuilder();
                var joinedShapes = new List<BlendShapeTable.Shape>(names.Count);
                foreach (var name in names)
                {
                    int i1 = one.IndexOf(name), i2 = two.IndexOf(name);
                    // Frames at every weight either mesh has one at; each mesh moves there as Unity would blend it.
                    var weights = new SortedSet<float>();
                    if (i1 >= 0) foreach (var f in one.Shapes[i1].Frames) weights.Add(f.Weight);
                    if (i2 >= 0) foreach (var f in two.Shapes[i2].Frames) weights.Add(f.Weight);
                    var frames = new List<BlendShapeTable.Frame>(weights.Count);
                    foreach (float weight in weights)
                    {
                        frame.Clear();
                        if (i1 >= 0) MeshShapes.AddShapeAt(one, i1, weight, 0, frame);
                        if (i2 >= 0) MeshShapes.AddShapeAt(two, i2, weight, a, frame);
                        frames.Add(frame.Build(weight));
                    }
                    joinedShapes.Add(new BlendShapeTable.Shape(name, frames));
                }
                MeshShapes.Write(joined, new BlendShapeTable(total, joinedShapes));
            }
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
            // Lines and points stay as they are with what is kept.
            var kept = Subset(mesh, keep, null, out var vertices, true);
            try
            {
                var joined = Join(kept, replacement, slots, false);
                if (mesh.blendShapeCount == 0) return joined;
                int offset = kept.vertexCount, total = joined.vertexCount;
                var extra = MeshShapes.Of(replacement);
                // The kept part's shapes on its vertices (vertices[i] is new vertex i's source).
                var oldToNew = new int[mesh.vertexCount];
                for (int v = 0; v < oldToNew.Length; v++) oldToNew[v] = -1;
                for (int i = 0; i < vertices.Length; i++) oldToNew[vertices[i]] = i;
                var keptShapes = MeshShapes.Picked(MeshShapes.Of(mesh), shape).Remap(oldToNew, offset);
                var frame = new MeshShapes.FrameBuilder();
                var shapes = new List<BlendShapeTable.Shape>(keptShapes.ShapeCount);
                foreach (var own in keptShapes.Shapes)
                {
                    int other = extra.IndexOf(own.Name);
                    var frames = new List<BlendShapeTable.Frame>(own.FrameCount);
                    for (int f = 0; f < own.FrameCount; f++)
                    {
                        // The replacement's frame of the same index moves its vertices, at the mesh's frame weight.
                        frame.Clear();
                        frame.AddCopy(own.Frames[f], 0);
                        if (other >= 0 && f < extra.Shapes[other].FrameCount) frame.AddCopy(extra.Shapes[other].Frames[f], offset);
                        frames.Add(frame.Build(own.Frames[f].Weight));
                    }
                    shapes.Add(new BlendShapeTable.Shape(own.Name, frames));
                }
                MeshShapes.Write(joined, new BlendShapeTable(total, shapes));
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
            var nearby = shading ? new List<int>() : null;
            int over = 0, allowed = points.Length / 100, turned = 0, tolerated = points.Length / 500;
            for (int i = 0; i < points.Length; i++)
            {
                var (distance, _) = surface.Nearest(points[i]);
                if (distance > 2f * budget || distance > budget && ++over > allowed) return false;
                if (shading && !surface.Shades(points[i], normals[i], distance, budget / 4f, vertexNormals, minDot, nearby) && ++turned > tolerated) return false;
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
            var source = new ShapeSource(from);
            source.Apply(to, source.Match(to.vertices, to.normals), shape);
        }

        /// <summary>
        /// <see cref="TransferShapes"/> in three steps, for a mesh whose blendshapes go to several meshes made from it (each
        /// try of a simplification): its surface is read once, here (on the editor's thread); <see cref="Match"/> pairs a
        /// made mesh's vertices with it on any thread (the slow part: a body's tens of thousands of searches); then
        /// <see cref="Apply"/> adds the blendshapes (on the editor's thread).
        /// </summary>
        public sealed class ShapeSource
        {
            private readonly Mesh from;
            private readonly int[] triangles;
            private readonly Vector3[] normals;
            private readonly float reach;
            private readonly Lazy<Surface> surface;

            /// <summary>For each made vertex, three of the source's vertices and how much each counts.</summary>
            public sealed class Matches { internal int[] Corners; internal float[] Weights; }

            public ShapeSource(Mesh from)
            {
                this.from = from;
                var vertices = from.vertices;
                triangles = from.triangles;
                var all = from.normals;
                normals = all.Length == vertices.Length ? all : null;
                double edges = 0;
                for (int t = 0; t + 2 < triangles.Length; t += 3) edges += (vertices[triangles[t]] - vertices[triangles[t + 1]]).magnitude;
                // A new vertex lies on the surface it was made from or next to it: a triangle and a half around is far enough.
                reach = triangles.Length > 0 ? 1.5f * (float)(edges / (triangles.Length / 3)) : 1f;
                // Built by the first match, wherever it runs.
                surface = new Lazy<Surface>(() => new Surface(vertices, triangles, reach));
            }

            /// <summary>Pairs the made mesh's vertices (and normals, when it has one per vertex) with the source's surface. Any thread.</summary>
            public Matches Match(Vector3[] targets, Vector3[] targetNormals)
            {
                if (targetNormals != null && targetNormals.Length != targets.Length) targetNormals = null;
                var found = surface.Value;
                var corners = new int[targets.Length * 3];
                var weights = new float[targets.Length * 3];
                // Scratch belongs to this call: a ShapeSource can be matched on several worker threads.
                var nearby = new List<int>();
                for (int i = 0; i < targets.Length; i++)
                {
                    var (t, b) = found.Closest(targets[i], targetNormals != null ? targetNormals[i] : (Vector3?)null, normals, reach / 16f, nearby);
                    if (t < 0) continue;
                    corners[3 * i] = triangles[t]; corners[3 * i + 1] = triangles[t + 1]; corners[3 * i + 2] = triangles[t + 2];
                    weights[3 * i] = b.x; weights[3 * i + 1] = b.y; weights[3 * i + 2] = b.z;
                }
                return new Matches { Corners = corners, Weights = weights };
            }

            /// <summary>Gives <paramref name="to"/> the blendshapes <paramref name="shape"/> picks, as matched (after those it has).</summary>
            public void Apply(Mesh to, Matches matches, Func<int, bool> shape)
            {
                var corners = matches.Corners; var weights = matches.Weights;
                int count = corners.Length / 3;
                var table = MeshShapes.Of(from);
                // Per source vertex, the made vertices it is a corner of: a frame reaches only the made vertices around
                // what it moves (the others come out zero, as from the dense frames).
                var starts = new int[from.vertexCount + 1];
                foreach (int corner in corners) starts[corner + 1]++;
                for (int v = 0; v < from.vertexCount; v++) starts[v + 1] += starts[v];
                var fill = (int[])starts.Clone();
                var users = new int[corners.Length];
                for (int c = 0; c < corners.Length; c++) users[fill[corners[c]]++] = c / 3;
                var dv = new Vector3[from.vertexCount]; var dn = new Vector3[from.vertexCount]; var dt = new Vector3[from.vertexCount];
                var reached = new bool[count];
                var list = new List<int>();
                var frame = new MeshShapes.FrameBuilder();
                var shapes = new List<BlendShapeTable.Shape>();
                if (to.blendShapeCount > 0) shapes.AddRange(MeshShapes.Of(to).Shapes);
                for (int index = 0; index < table.ShapeCount; index++)
                {
                    if (!shape(index)) continue;
                    var source = table.Shapes[index];
                    var frames = new List<BlendShapeTable.Frame>(source.FrameCount);
                    foreach (var f in source.Frames)
                    {
                        f.Densify(dv, dn, dt);
                        list.Clear();
                        foreach (int v in f.Indices)
                            for (int u = starts[v]; u < starts[v + 1]; u++)
                                if (!reached[users[u]]) { reached[users[u]] = true; list.Add(users[u]); }
                        list.Sort();
                        frame.Clear();
                        foreach (int i in list)
                        {
                            reached[i] = false;
                            int a = corners[3 * i], b = corners[3 * i + 1], c = corners[3 * i + 2];
                            float wa = weights[3 * i], wb = weights[3 * i + 1], wc = weights[3 * i + 2];
                            frame.Add(i, dv[a] * wa + dv[b] * wb + dv[c] * wc, dn[a] * wa + dn[b] * wb + dn[c] * wc, dt[a] * wa + dt[b] * wb + dt[c] * wc,
                                f.HasNormals, f.HasTangents);
                        }
                        frames.Add(frame.Build(f.Weight));
                    }
                    shapes.Add(new BlendShapeTable.Shape(source.Name, frames));
                }
                MeshShapes.Write(to, new BlendShapeTable(count, shapes));
            }
        }

        // A uniform grid put thousands of overlapping body triangles in the same cells and visited them repeatedly
        // for every vertex and every pose. Share the mesh BVH used by the other geometry tools: nearest-first bounds
        // prune distant triangles, and a second, small query considers the nearby sides of hard edges and seams.
        private sealed class Surface
        {
            private readonly Vector3[] vertices;
            private readonly int[] triangles;
            private readonly float reach;
            private readonly MeshBvh bvh;

            public Surface(Vector3[] vertices, int[] triangles, float reach)
            {
                this.vertices = vertices;
                this.triangles = triangles;
                this.reach = reach;
                bvh = MeshBvh.Build(vertices, triangles);
            }

            public (float Distance, int Triangle) Nearest(Vector3 p)
            {
                var hit = bvh.ClosestPoint(p, reach + 1f);
                return hit.Found ? (hit.Distance, hit.Triangle * 3) : (reach + 1f, -1);
            }

            // The distance and shading checks share the nearest result. Candidates outside its slack cannot make a
            // flipped surface pass by borrowing the normal of a more distant sheet.
            public bool Shades(Vector3 p, Vector3 n, float nearest, float slack, Vector3[] normals, float minDot, List<int> nearby)
            {
                if (nearest > reach) return false;
                float within = nearest + slack;
                bvh.TrianglesNear(p, within, nearby);
                foreach (int triangle in nearby)
                {
                    int t = triangle * 3;
                    var point = Point(p, t, out var bary);
                    if ((point - p).magnitude <= within && Vector3.Dot(Normal(t, bary, normals), n) >= minDot) return true;
                }
                return false;
            }

            // Among the surfaces about as near, prefer the side whose normal matches. Triangle order breaks equal
            // normal matches deterministically, independent of how the BVH partitions them.
            public (int Triangle, Vector3 Barycentric) Closest(Vector3 p, Vector3? normal, Vector3[] normals, float slack, List<int> nearby)
            {
                var (nearest, triangle) = Nearest(p);
                if (triangle < 0) return (-1, Vector3.zero);
                Point(p, triangle, out var barycentric);
                if (normal != null && normals != null)
                {
                    float within = nearest + slack, best = -2f;
                    bvh.TrianglesNear(p, within, nearby);
                    foreach (int candidate in nearby)
                    {
                        int t = candidate * 3;
                        var point = Point(p, t, out var bary);
                        if ((point - p).magnitude > within) continue;
                        float dot = Vector3.Dot(Normal(t, bary, normals), normal.Value);
                        if (dot > best || dot == best && t < triangle)
                        {
                            best = dot;
                            triangle = t;
                            barycentric = bary;
                        }
                    }
                }
                return (triangle, barycentric);
            }

            private Vector3 Point(Vector3 p, int t, out Vector3 bary) =>
                MeshBvh.ClosestPointOnTriangle(p, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]], out bary);

            private Vector3 Normal(int t, Vector3 bary, Vector3[] normals) =>
                (bary.x * normals[triangles[t]] + bary.y * normals[triangles[t + 1]] + bary.z * normals[triangles[t + 2]]).normalized;
        }

        /// <summary>
        /// A blendshape's offsets at <paramref name="weight"/> as Unity blends them: between the frames around it, from zero
        /// below the first, along the last two beyond the last. The arrays hold at least the mesh's vertices.
        /// </summary>
        public static void ShapeAt(Mesh mesh, int shape, float weight, Vector3[] dv, Vector3[] dn, Vector3[] dt)
        {
            int frames = mesh.GetBlendShapeFrameCount(shape), n = mesh.vertexCount;
            int upper = 0;
            while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
            float upperWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
            float lowerWeight = upper == 0 ? 0 : mesh.GetBlendShapeFrameWeight(shape, upper - 1);
            float t = upperWeight == lowerWeight ? 1 : (weight - lowerWeight) / (upperWeight - lowerWeight);
            Read(mesh, shape, upper, dv, dn, dt);
            for (int v = 0; v < n; v++) { dv[v] *= t; dn[v] *= t; dt[v] *= t; }
            if (upper == 0 || t == 1) return;
            var lv = new Vector3[n]; var ln = new Vector3[n]; var lt = new Vector3[n];
            Read(mesh, shape, upper - 1, lv, ln, lt);
            for (int v = 0; v < n; v++) { dv[v] += lv[v] * (1 - t); dn[v] += ln[v] * (1 - t); dt[v] += lt[v] * (1 - t); }
        }

        // A frame into arrays that may be longer than the mesh's vertices.
        private static void Read(Mesh mesh, int shape, int frame, Vector3[] dv, Vector3[] dn, Vector3[] dt)
        {
            int n = mesh.vertexCount;
            if (dv.Length == n) { mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt); return; }
            var v = new Vector3[n]; var nn = new Vector3[n]; var tt = new Vector3[n];
            mesh.GetBlendShapeFrameVertices(shape, frame, v, nn, tt);
            Array.Copy(v, dv, n); Array.Copy(nn, dn, n); Array.Copy(tt, dt, n);
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
            // A mesh without weights joining one with them keeps its vertices weighted to no bone; the other keeps its own.
            var firstCounts = first.GetBonesPerVertex(); var secondCounts = second.GetBonesPerVertex();
            if (firstCounts.Length == 0 && secondCounts.Length == 0) return;
            var counts = (firstCounts.Length > 0 ? firstCounts.ToArray() : new byte[first.vertexCount])
                .Concat(secondCounts.Length > 0 ? secondCounts.ToArray() : new byte[second.vertexCount]).ToArray();
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
