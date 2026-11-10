using System;
using System.Collections.Generic;
using System.Threading;
using Orbiters.Toolkit.Meshes;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.AlterMesh
{
    /// <summary>What a <see cref="SimplificationRun"/> may change.</summary>
    public struct SimplificationOptions
    {
        public static SimplificationOptions Default => new SimplificationOptions
        {
            MinNormalDot = 0.2f,
            NormalWeight = 0.5f,
            UvWeight = 1f,
            MaxDeviation = float.PositiveInfinity,
        };

        /// <summary>Open edges (holes, and the cut around a part taken out of a mesh) never move.</summary>
        public bool LockBorders;
        /// <summary>A collapse may turn no remaining face further than this (cosine between its normals before and after).</summary>
        public float MinNormalDot;
        /// <summary>How much shading (normals) and texture placement (UV0) weigh against shape in a collapse's cost.</summary>
        public float NormalWeight, UvWeight;
        /// <summary>In the mesh's units: a collapse whose estimated surface deviation is larger is never made.</summary>
        public float MaxDeviation;
        /// <summary>The run stops at this many triangles.</summary>
        public int MinTriangles;
        /// <summary>Per mesh vertex, or null: vertices that never move (all the vertices at a locked vertex's place stay).</summary>
        public bool[] LockedVertices;
    }

    /// <summary>
    /// One simplification of a mesh, run once to the end and kept as its collapse log, so that any smaller amount of
    /// simplification is a prefix of the same run: <see cref="Write"/> any number of collapses, ask how many fit a
    /// deviation (<see cref="CollapsesWithin"/>) or a triangle count (<see cref="CollapsesFor(int)"/>), without running again.
    /// <para>
    /// The topology is the mesh's welded one: vertices at one place (Unity splits them at UV seams and hard edges) are one
    /// point, each split copy a wedge. A point with one wedge and no open edge collapses anywhere; one on a border slides
    /// along its border; one on a seam (exactly two wedges) slides along its seam and both wedges go together, so UV
    /// islands never mix; anything else stays. A collapse moves a point onto a neighbour (no new positions), so every kept
    /// vertex keeps its own attributes, skin weights and blendshape offsets exactly: <see cref="Shapes"/> only drops the
    /// removed vertices. Costs are quadrics of the surface (with border and seam edges) plus attribute quadrics of normals
    /// and UV0, collapsed in passes of the cheapest edges (after meshoptimizer's simplifier).
    /// </para>
    /// <see cref="Prepare"/> reads the mesh (main thread), <see cref="Run"/> works on any thread, <see cref="Write"/> is
    /// the main thread's again. Nothing native is held between calls.
    /// </summary>
    public sealed class SimplificationRun
    {
        private enum Kind : byte { Manifold, Border, Seam, Locked }

        private struct Quadric
        {
            public float A00, A11, A22, A10, A20, A21, B0, B1, B2, C, W;

            public static Quadric FromPlane(float3 n, float d, float w) => new Quadric
            {
                A00 = w * n.x * n.x, A11 = w * n.y * n.y, A22 = w * n.z * n.z,
                A10 = w * n.y * n.x, A20 = w * n.z * n.x, A21 = w * n.z * n.y,
                B0 = w * n.x * d, B1 = w * n.y * d, B2 = w * n.z * d, C = w * d * d, W = w
            };

            public void Add(in Quadric q)
            {
                A00 += q.A00; A11 += q.A11; A22 += q.A22; A10 += q.A10; A20 += q.A20; A21 += q.A21;
                B0 += q.B0; B1 += q.B1; B2 += q.B2; C += q.C; W += q.W;
            }

            // v'Av + 2b'v + c, not yet divided by the weight.
            public float Evaluate(float3 v)
            {
                float rx = B0 + A10 * v.y, ry = B1 + A21 * v.z, rz = B2 + A20 * v.x;
                rx *= 2f; ry *= 2f; rz *= 2f;
                rx += A00 * v.x; ry += A11 * v.y; rz += A22 * v.z;
                return C + rx * v.x + ry * v.y + rz * v.z;
            }

            // The mean squared distance to the planes it holds.
            public float Error(float3 v) => W > 0f ? math.abs(Evaluate(v)) / W : 0f;
        }

        private static readonly ProfilerMarker RunMarker = new ProfilerMarker("SimplificationRun.Run");
        private static readonly ProfilerMarker WriteMarker = new ProfilerMarker("SimplificationRun.Write");

        // What the result is written from: the mesh's vertex streams as bytes, its index lists and skin.
        private VertexAttributeDescriptor[] layout;
        private byte[][] streams;
        private int[] strides;
        private int[][] submeshIndices;
        private MeshTopology[] topologies;
        private byte[] bonesPerVertex;
        private BoneWeight1[] boneWeights;
        private int[] boneStart;
        private Matrix4x4[] bindposes;
        private string name;

        // What the run works on.
        private readonly SimplificationOptions options;
        private int vertexCount;
        private float3[] positions;   // in a unit box, so costs compare across meshes
        private float extent;
        private int attributeCount;
        private float[] attributes;   // per wedge, weighted
        private int[] triangles;      // every triangle submesh's, without the collapsed ones
        private int[] remap, wedge;   // a wedge's point (its lowest wedge) and the next wedge at its place
        private bool ran;

        // The log: per collapse, the wedge removed and the one it became (and the seam's second pair), what it cost.
        private int count;
        private int[] removed, kept, removedSeam, keptSeam;
        private float[] deviation;    // running maximum, in mesh units
        private int[] trianglesAfter;
        private int startTriangles;

        private SimplificationRun(SimplificationOptions options) => this.options = options;

        /// <summary>Reads what the run needs from <paramref name="mesh"/> (main thread; the mesh must be readable).</summary>
        public static SimplificationRun Prepare(Mesh mesh, SimplificationOptions options)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var run = new SimplificationRun(options) { name = mesh.name };
            int n = run.vertexCount = mesh.vertexCount;
            if (options.LockedVertices != null && options.LockedVertices.Length != n)
                throw new ArgumentException("One lock per vertex: " + options.LockedVertices.Length + " for " + n + ".", nameof(options));
            run.layout = mesh.GetVertexAttributes();
            int streamCount = 0;
            foreach (var attribute in run.layout) streamCount = Math.Max(streamCount, attribute.stream + 1);
            run.streams = new byte[streamCount][];
            run.strides = new int[streamCount];
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                for (int s = 0; s < streamCount; s++)
                {
                    run.strides[s] = data[0].GetVertexBufferStride(s);
                    run.streams[s] = run.strides[s] > 0 ? data[0].GetVertexData<byte>(s).ToArray() : Array.Empty<byte>();
                }
            }
            run.submeshIndices = new int[mesh.subMeshCount][];
            run.topologies = new MeshTopology[mesh.subMeshCount];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                run.topologies[s] = mesh.GetTopology(s);
                run.submeshIndices[s] = mesh.GetIndices(s, true);
            }
            var perVertex = mesh.GetBonesPerVertex();
            if (perVertex.Length == n && n > 0)
            {
                run.bonesPerVertex = perVertex.ToArray();
                run.boneWeights = mesh.GetAllBoneWeights().ToArray();
                run.boneStart = new int[n + 1];
                for (int v = 0; v < n; v++) run.boneStart[v + 1] = run.boneStart[v] + run.bonesPerVertex[v];
            }
            run.bindposes = mesh.bindposes;

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            bool withNormals = normals.Length == n && options.NormalWeight > 0f;
            bool withUvs = uvs.Length == n && options.UvWeight > 0f;
            run.attributeCount = (withNormals ? 3 : 0) + (withUvs ? 2 : 0);
            var bounds = n > 0 ? new Bounds(vertices[0], Vector3.zero) : new Bounds();
            for (int v = 1; v < n; v++) bounds.Encapsulate(vertices[v]);
            run.extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (!(run.extent > 0f)) run.extent = 1f;
            float scale = 1f / run.extent;
            run.positions = new float3[n];
            for (int v = 0; v < n; v++) run.positions[v] = (float3)(vertices[v] - bounds.min) * scale;
            run.attributes = new float[n * run.attributeCount];
            for (int v = 0; v < n; v++)
            {
                int o = v * run.attributeCount;
                if (withNormals) { run.attributes[o] = normals[v].x * options.NormalWeight; run.attributes[o + 1] = normals[v].y * options.NormalWeight; run.attributes[o + 2] = normals[v].z * options.NormalWeight; o += 3; }
                if (withUvs) { run.attributes[o] = uvs[v].x * options.UvWeight; run.attributes[o + 1] = uvs[v].y * options.UvWeight; }
            }

            // Points: wedges at exactly one place (-0 and 0 alike).
            run.remap = new int[n];
            run.wedge = new int[n];
            var points = new Dictionary<Vector3, int>(n);
            for (int v = 0; v < n; v++)
            {
                var p = vertices[v];
                var key = new Vector3(p.x + 0f, p.y + 0f, p.z + 0f);
                if (points.TryGetValue(key, out int first))
                {
                    run.remap[v] = first;
                    // Into the circle of the first's wedges, right after it.
                    run.wedge[v] = run.wedge[first];
                    run.wedge[first] = v;
                }
                else
                {
                    points.Add(key, v);
                    run.remap[v] = v;
                    run.wedge[v] = v;
                }
            }

            var all = new List<int>();
            for (int s = 0; s < run.submeshIndices.Length; s++)
            {
                if (run.topologies[s] != MeshTopology.Triangles) continue;
                var indices = run.submeshIndices[s];
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
                    if (run.remap[a] == run.remap[b] || run.remap[b] == run.remap[c] || run.remap[a] == run.remap[c]) continue;
                    all.Add(a); all.Add(b); all.Add(c);
                }
            }
            run.triangles = all.ToArray();
            run.startTriangles = run.triangles.Length / 3;
            return run;
        }

        /// <summary>The triangles at the start (those of triangle submeshes that have an area: three distinct places).</summary>
        public int TriangleCount => startTriangles;
        /// <summary>How many collapses the run made.</summary>
        public int Count => count;

        /// <summary>Triangles left after the first <paramref name="collapses"/> collapses.</summary>
        public int TrianglesAt(int collapses) => collapses <= 0 ? startTriangles : trianglesAfter[Math.Min(collapses, count) - 1];

        /// <summary>
        /// The surface deviation the first <paramref name="collapses"/> collapses are estimated to cause, in the mesh's units
        /// (the largest of their quadric errors as a distance: never decreasing with more collapses).
        /// </summary>
        public float DeviationAt(int collapses) => collapses <= 0 ? 0f : deviation[Math.Min(collapses, count) - 1];

        /// <summary>The most collapses whose estimated deviation stays within <paramref name="distance"/>.</summary>
        public int CollapsesWithin(float distance)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (deviation[mid - 1] <= distance) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The fewest collapses that leave at most <paramref name="triangleCount"/> triangles (all of them when none does).</summary>
        public int CollapsesFor(int triangleCount)
        {
            if (startTriangles <= triangleCount) return 0;
            int lo = 1, hi = count;
            if (count == 0 || trianglesAfter[count - 1] > triangleCount) return count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (trianglesAfter[mid - 1] <= triangleCount) hi = mid; else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>The fewest collapses that keep at most <paramref name="keep"/> (0..1) of the triangles.</summary>
        public int CollapsesFor(float keep) => CollapsesFor((int)Math.Floor(startTriangles * (double)Mathf.Clamp01(keep)));

        // ---- The run ------------------------------------------------------------------------------------------------------

        /// <summary>Collapses until nothing more may go (any thread; once).</summary>
        public void Run(CancellationToken token = default)
        {
            if (ran) throw new InvalidOperationException("A simplification runs once.");
            ran = true;
            using (RunMarker.Auto()) Collapse(token);
        }

        // Which collapses each kind allows (from, to), and whether its edges show up from both sides.
        private static readonly bool[,] CanCollapse =
        {
            { true, true, true, true },
            { false, true, false, false },
            { false, false, true, false },
            { false, false, false, false },
        };
        private static readonly bool[,] HasOpposite =
        {
            { true, true, true, true },
            { true, false, true, false },
            { true, true, true, true },
            { true, false, true, false },
        };

        private const float BorderEdgeWeight = 10f, SeamEdgeWeight = 1f;
        // Each pass takes about this share of the triangles left, so a prefix of the log ranks almost as finely as a run
        // that stopped there.
        private const int PassShare = 5;

        private Kind[] kinds;
        private int[] loop, loopback;
        private Quadric[] pointQuadrics, wedgeQuadrics;
        private float4[] gradients;

        private void Collapse(CancellationToken token)
        {
            int n = vertexCount;
            removed = new int[Math.Max(1, n)]; kept = new int[removed.Length]; removedSeam = new int[removed.Length]; keptSeam = new int[removed.Length];
            deviation = new float[removed.Length]; trianglesAfter = new int[removed.Length];
            count = 0;
            if (startTriangles == 0) return;
            Classify();
            BuildQuadrics();
            float limit = options.MaxDeviation >= float.MaxValue ? float.PositiveInfinity : options.MaxDeviation / extent;
            float limit2 = limit * limit;
            float runningMax = 0f;

            var current = (int[])triangles.Clone();
            int indexCount = current.Length;
            var offsets = new int[n + 1];
            var nexts = new int[indexCount];
            var prevs = new int[indexCount];
            var fill = new int[n];
            var from = new int[2 * indexCount];
            var to = new int[from.Length];
            var costs = new float[from.Length];
            var sortedCosts = new float[from.Length];
            var order = new int[from.Length];
            var pointErrors = new float[from.Length];
            var collapseRemap = new int[n];
            var lockedThisPass = new bool[n];

            while (indexCount / 3 > options.MinTriangles)
            {
                token.ThrowIfCancellationRequested();
                int alive = indexCount / 3;
                // Point adjacency of the triangles as they are: each point's corners, with the next and previous point.
                Array.Clear(offsets, 0, offsets.Length);
                for (int i = 0; i < indexCount; i++) offsets[remap[current[i]] + 1]++;
                for (int v = 0; v < n; v++) offsets[v + 1] += offsets[v];
                Array.Copy(offsets, fill, n);
                for (int t = 0; t < indexCount; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int r = remap[current[t + e]];
                        int slot = fill[r]++;
                        nexts[slot] = remap[current[t + (e + 1) % 3]];
                        prevs[slot] = remap[current[t + (e + 2) % 3]];
                    }

                // Candidates, each in its cheaper direction.
                int candidates = 0;
                for (int t = 0; t < indexCount; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int i0 = current[t + e], i1 = current[t + (e + 1) % 3];
                        int r0 = remap[i0], r1 = remap[i1];
                        if (r0 == r1) continue;
                        var k0 = kinds[i0]; var k1 = kinds[i1];
                        bool forward = CanCollapse[(int)k0, (int)k1], backward = CanCollapse[(int)k1, (int)k0];
                        if (!forward && !backward) continue;
                        if (HasOpposite[(int)k0, (int)k1] && r1 > r0) continue;
                        // Two border or seam points not joined along their loop belong to two loops: never.
                        if (k0 == k1 && (k0 == Kind.Border || k0 == Kind.Seam) && loop[i0] != i1) continue;
                        float ef = float.PositiveInfinity, eb = float.PositiveInfinity, pf = 0f, pb = 0f;
                        if (forward) ef = Cost(i0, i1, out pf);
                        if (backward) eb = Cost(i1, i0, out pb);
                        if (float.IsPositiveInfinity(ef) && float.IsPositiveInfinity(eb)) continue;
                        bool f = ef <= eb;
                        from[candidates] = f ? i0 : i1;
                        to[candidates] = f ? i1 : i0;
                        costs[candidates] = f ? ef : eb;
                        pointErrors[candidates] = f ? pf : pb;
                        candidates++;
                    }
                if (candidates == 0) break;
                for (int c = 0; c < candidates; c++) { order[c] = c; sortedCosts[c] = costs[c]; }
                Array.Sort(sortedCosts, order, 0, candidates);

                for (int v = 0; v < n; v++) collapseRemap[v] = v;
                Array.Clear(lockedThisPass, 0, n);
                int triangleGoal = Math.Min(alive - options.MinTriangles, Math.Max(1, alive / PassShare));
                int edgeGoal = triangleGoal / 2;
                int removedThisPass = 0, collapsedThisPass = 0;
                for (int k = 0; k < candidates; k++)
                {
                    int c = order[k];
                    if (removedThisPass >= triangleGoal) break;
                    // The pass stops past the cost of about its goal's collapse (half again: many are locked by a neighbour's).
                    float errorGoal = edgeGoal < candidates ? 1.5f * sortedCosts[edgeGoal] : float.PositiveInfinity;
                    if (costs[c] > errorGoal && removedThisPass > triangleGoal / 6) break;
                    if (pointErrors[c] > limit2) continue;
                    int i0 = from[c], i1 = to[c];
                    int r0 = remap[i0], r1 = remap[i1];
                    if (lockedThisPass[r0] || lockedThisPass[r1]) continue;
                    if (Flips(r0, r1, offsets, nexts, prevs, collapseRemap)) { edgeGoal++; continue; }
                    int s0 = -1, s1 = -1;
                    if (kinds[i0] == Kind.Seam)
                    {
                        s0 = wedge[i0];
                        s1 = loop[i0] == i1 ? loopback[s0] : loop[s0];
                        if (s1 < 0 || remap[s1] != r1 || s0 == i0) continue;
                    }
                    pointQuadrics[r1].Add(pointQuadrics[r0]);
                    if (attributeCount > 0)
                    {
                        AddWedge(i1, i0);
                        if (s0 >= 0) AddWedge(s1, s0);
                    }
                    collapseRemap[i0] = i1;
                    if (s0 >= 0) collapseRemap[s0] = s1;
                    lockedThisPass[r0] = lockedThisPass[r1] = true;
                    // The triangles at both points: those go.
                    int gone = 0;
                    for (int slot = offsets[r0]; slot < offsets[r0 + 1]; slot++)
                        if (nexts[slot] == r1 || prevs[slot] == r1) gone++;
                    removedThisPass += gone;
                    alive -= gone;
                    runningMax = Math.Max(runningMax, math.sqrt(pointErrors[c]) * extent);
                    removed[count] = i0; kept[count] = i1; removedSeam[count] = s0; keptSeam[count] = s1;
                    deviation[count] = runningMax;
                    trianglesAfter[count] = alive;
                    count++;
                    collapsedThisPass++;
                    if (alive <= options.MinTriangles) break;
                }
                if (collapsedThisPass == 0) break;

                RemapLoops(loop, collapseRemap);
                RemapLoops(loopback, collapseRemap);
                int written = 0;
                for (int t = 0; t < indexCount; t += 3)
                {
                    int a = collapseRemap[current[t]], b = collapseRemap[current[t + 1]], c = collapseRemap[current[t + 2]];
                    if (remap[a] == remap[b] || remap[b] == remap[c] || remap[a] == remap[c]) continue;
                    current[written++] = a; current[written++] = b; current[written++] = c;
                }
                indexCount = written;
            }
            // The run's bookkeeping is not needed for replays.
            kinds = null; loop = loopback = null; pointQuadrics = wedgeQuadrics = null; gradients = gradientScratch = null;
        }

        private void AddWedge(int into, int from)
        {
            wedgeQuadrics[into].Add(wedgeQuadrics[from]);
            for (int k = 0; k < attributeCount; k++) gradients[into * attributeCount + k] += gradients[from * attributeCount + k];
        }

        // What collapsing wedge i0 onto i1 costs (the point's quadric at i1's place, the wedges' attribute quadrics with
        // i1's attributes, and a seam's second wedge pair), and the point quadric's share (the shape's deviation, squared).
        private float Cost(int i0, int i1, out float pointError)
        {
            var p1 = positions[i1];
            pointError = pointQuadrics[remap[i0]].Error(p1);
            float cost = pointError;
            if (attributeCount == 0) return cost;
            cost += AttributeError(i0, i1);
            if (kinds[i0] == Kind.Seam)
            {
                int s0 = wedge[i0];
                int s1 = loop[i0] == i1 ? loopback[s0] : loop[s0];
                if (s1 < 0 || remap[s1] != remap[i1]) return float.PositiveInfinity;
                cost += AttributeError(s0, s1);
            }
            return cost;
        }

        // The attribute quadric of wedge w at wedge into's place and attributes: how far its interpolated attributes land.
        private float AttributeError(int w, int into)
        {
            ref var q = ref wedgeQuadrics[w];
            if (!(q.W > 0f)) return 0f;
            var p = positions[into];
            float r = q.Evaluate(p);
            int o = into * attributeCount, g = w * attributeCount;
            for (int k = 0; k < attributeCount; k++)
            {
                float a = attributes[o + k];
                var gradient = gradients[g + k];
                r += a * a * q.W - 2f * a * (gradient.x * p.x + gradient.y * p.y + gradient.z * p.z + gradient.w);
            }
            return math.abs(r) / q.W;
        }

        // Whether moving point r0 onto r1 turns a remaining face of r0 too far (or makes it collapse to no area).
        private bool Flips(int r0, int r1, int[] offsets, int[] nexts, int[] prevs, int[] collapseRemap)
        {
            float3 p0 = positions[r0], p1 = positions[r1];
            for (int slot = offsets[r0]; slot < offsets[r0 + 1]; slot++)
            {
                int a = remap[collapseRemap[nexts[slot]]], b = remap[collapseRemap[prevs[slot]]];
                if (a == r1 || b == r1) continue;
                float3 pa = positions[a], pb = positions[b];
                float3 before = math.cross(pa - p0, pb - p0), after = math.cross(pa - p1, pb - p1);
                float lengths = math.sqrt(math.lengthsq(before) * math.lengthsq(after));
                if (!(math.dot(before, after) > options.MinNormalDot * lengths)) return true;
            }
            return false;
        }

        private static void RemapLoops(int[] loops, int[] collapseRemap)
        {
            for (int i = 0; i < loops.Length; i++)
            {
                int l = loops[i];
                if (l < 0) continue;
                int r = collapseRemap[l];
                // A seam collapsed against the loop's direction: the loop goes on past it.
                if (i == r) loops[i] = loops[l] >= 0 ? collapseRemap[loops[l]] : -1;
                else loops[i] = r;
            }
        }

        // Each wedge's kind from its open half-edges (no half-edge back between the same two wedges): borders have one in
        // and one out, seams one per wedge that meet at the same points.
        private void Classify()
        {
            int n = vertexCount;
            int indexCount = triangles.Length;
            var offsets = new int[n + 1];
            for (int i = 0; i < indexCount; i++) offsets[triangles[i] + 1]++;
            for (int v = 0; v < n; v++) offsets[v + 1] += offsets[v];
            var fill = new int[n];
            Array.Copy(offsets, fill, n);
            var nexts = new int[indexCount];
            for (int t = 0; t < indexCount; t += 3)
                for (int e = 0; e < 3; e++) nexts[fill[triangles[t + e]]++] = triangles[t + (e + 1) % 3];
            bool HasEdge(int a, int b)
            {
                for (int slot = offsets[a]; slot < offsets[a + 1]; slot++) if (nexts[slot] == b) return true;
                return false;
            }
            var openIn = loopback = new int[n];
            var openOut = loop = new int[n];
            for (int v = 0; v < n; v++) openIn[v] = openOut[v] = -1;
            for (int v = 0; v < n; v++)
                for (int slot = offsets[v]; slot < offsets[v + 1]; slot++)
                {
                    int target = nexts[slot];
                    if (target == v) { openIn[v] = openOut[v] = v; continue; }
                    if (HasEdge(target, v)) continue;
                    // More than one open edge in (or out) marks the vertex itself: it is locked below.
                    openIn[target] = openIn[target] == -1 ? v : target;
                    openOut[v] = openOut[v] == -1 ? target : v;
                }
            kinds = new Kind[n];
            for (int v = 0; v < n; v++)
            {
                if (remap[v] != v) continue;
                Kind kind;
                if (wedge[v] == v)
                {
                    int i = openIn[v], o = openOut[v];
                    kind = i == -1 && o == -1 ? Kind.Manifold : i != v && o != v && i != -1 && o != -1 ? Kind.Border : Kind.Locked;
                    if (kind == Kind.Border && options.LockBorders) kind = Kind.Locked;
                }
                else if (wedge[wedge[v]] == v)
                {
                    int w = wedge[v];
                    int iv = openIn[v], ov = openOut[v], iw = openIn[w], ow = openOut[w];
                    bool open = iv >= 0 && iv != v && ov >= 0 && ov != v && iw >= 0 && iw != w && ow >= 0 && ow != w;
                    kind = open && remap[iv] == remap[ow] && remap[ov] == remap[iw] && remap[iv] != remap[ov] ? Kind.Seam : Kind.Locked;
                }
                else kind = Kind.Locked;
                kinds[v] = kind;
            }
            if (options.LockedVertices != null)
                for (int v = 0; v < n; v++)
                    if (options.LockedVertices[v]) kinds[remap[v]] = Kind.Locked;
            for (int v = 0; v < n; v++) kinds[v] = kinds[remap[v]];
        }

        private void BuildQuadrics()
        {
            int n = vertexCount;
            pointQuadrics = new Quadric[n];
            wedgeQuadrics = new Quadric[n];
            gradients = new float4[n * attributeCount];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                float3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                float3 normal = math.cross(p1 - p0, p2 - p0);
                float area = math.length(normal);
                // Weighed by the square root of the area, so that edge quadrics (by length) weigh alike at any size.
                float weight = math.sqrt(area);
                if (area > 0f)
                {
                    normal /= area;
                    var q = Quadric.FromPlane(normal, -math.dot(normal, p0), weight);
                    pointQuadrics[remap[i0]].Add(q);
                    pointQuadrics[remap[i1]].Add(q);
                    pointQuadrics[remap[i2]].Add(q);
                }
                if (attributeCount > 0) AddAttributeQuadrics(i0, i1, i2, p0, p1, p2, weight);

                // Border and seam edges hold their line: a plane through the edge, across the face.
                for (int e = 0; e < 3; e++)
                {
                    int a = triangles[t + e], b = triangles[t + (e + 1) % 3], c = triangles[t + (e + 2) % 3];
                    var ka = kinds[a]; var kb = kinds[b];
                    bool aOpen = ka == Kind.Border || ka == Kind.Seam, bOpen = kb == Kind.Border || kb == Kind.Seam;
                    if (!aOpen && !bOpen) continue;
                    // On the same loop: the open half-edge from a is this one (and into b), also where the other end is a
                    // corner that stays.
                    if (aOpen && loop[a] != b) continue;
                    if (bOpen && loopback[b] != a) continue;
                    // A seam's edge shows up on both of its sides: once is enough.
                    if (HasOpposite[(int)ka, (int)kb] && remap[b] > remap[a]) continue;
                    float edgeWeight = ka == Kind.Border || kb == Kind.Border ? BorderEdgeWeight : SeamEdgeWeight;
                    var q = EdgeQuadric(positions[a], positions[b], positions[c], edgeWeight);
                    pointQuadrics[remap[a]].Add(q);
                    pointQuadrics[remap[b]].Add(q);
                }
            }
        }

        private static Quadric EdgeQuadric(float3 p0, float3 p1, float3 p2, float weight)
        {
            float3 p10 = p1 - p0, p20 = p2 - p0;
            float lengthSq = math.lengthsq(p10);
            float length = math.sqrt(lengthSq);
            // The direction from the edge's line to the third corner (unnormalized projection corrected by the scale).
            float3 perpendicular = p20 * lengthSq - p10 * math.dot(p20, p10);
            float size = math.length(perpendicular);
            if (!(size > 0f) || !(length > 0f)) return default;
            perpendicular /= size;
            return Quadric.FromPlane(perpendicular, -math.dot(perpendicular, p0), length * weight);
        }

        // Each attribute's gradient over the triangle (Hoppe): the attribute quadric measures how far an attribute moves
        // from what the face interpolated there.
        private void AddAttributeQuadrics(int i0, int i1, int i2, float3 p0, float3 p1, float3 p2, float weight)
        {
            float3 e1 = p1 - p0, e2 = p2 - p0;
            float d00 = math.dot(e1, e1), d01 = math.dot(e1, e2), d11 = math.dot(e2, e2);
            float denominator = d00 * d11 - d01 * d01;
            float inverse = denominator != 0f ? 1f / denominator : 0f;
            float3 gu = (d11 * e1 - d01 * e2) * inverse, gv = (d00 * e2 - d01 * e1) * inverse;
            var q = new Quadric { W = weight };
            var g = gradientScratch ?? (gradientScratch = new float4[attributeCount]);
            for (int k = 0; k < attributeCount; k++)
            {
                float a0 = attributes[i0 * attributeCount + k], a1 = attributes[i1 * attributeCount + k], a2 = attributes[i2 * attributeCount + k];
                float3 gradient = gu * (a1 - a0) + gv * (a2 - a0);
                float offset = a0 - math.dot(gradient, p0);
                q.A00 += weight * gradient.x * gradient.x; q.A11 += weight * gradient.y * gradient.y; q.A22 += weight * gradient.z * gradient.z;
                q.A10 += weight * gradient.y * gradient.x; q.A20 += weight * gradient.z * gradient.x; q.A21 += weight * gradient.z * gradient.y;
                q.B0 += weight * gradient.x * offset; q.B1 += weight * gradient.y * offset; q.B2 += weight * gradient.z * offset;
                q.C += weight * offset * offset;
                g[k] = new float4(gradient * weight, offset * weight);
            }
            AddToWedge(i0, q, g);
            AddToWedge(i1, q, g);
            AddToWedge(i2, q, g);
        }

        private float4[] gradientScratch;

        private void AddToWedge(int w, in Quadric q, float4[] g)
        {
            wedgeQuadrics[w].Add(q);
            for (int k = 0; k < attributeCount; k++) gradients[w * attributeCount + k] += g[k];
        }

        // ---- Replays ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Per mesh vertex, the vertex it is after the first <paramref name="collapses"/> collapses (itself when it stays).
        /// </summary>
        public int[] WedgeMap(int collapses)
        {
            if (!ran) throw new InvalidOperationException("Run first.");
            collapses = Math.Max(0, Math.Min(collapses, count));
            var map = new int[vertexCount];
            for (int v = 0; v < map.Length; v++) map[v] = v;
            for (int k = 0; k < collapses; k++)
            {
                map[removed[k]] = kept[k];
                if (removedSeam[k] >= 0) map[removedSeam[k]] = keptSeam[k];
            }
            // Each removed vertex pointed at one alive at its collapse: chains end, and are shortened as they are followed.
            for (int v = 0; v < map.Length; v++)
            {
                int r = v;
                while (map[r] != r) r = map[r];
                for (int c = v; map[c] != r && c != r;) { int next = map[c]; map[c] = r; c = next; }
            }
            return map;
        }

        /// <summary>
        /// The vertices the mesh keeps after the first <paramref name="collapses"/> collapses, in their order: entry j is the
        /// source vertex that new vertex j is, unchanged.
        /// </summary>
        public int[] KeptVertices(int collapses) => Result(collapses, out _, out _);

        // The kept vertices and each submesh's index list (of new vertices) after a prefix.
        private int[] Result(int collapses, out int[][] indices, out int[] oldToNew)
        {
            var map = WedgeMap(collapses);
            indices = new int[submeshIndices.Length][];
            var used = new bool[vertexCount];
            for (int s = 0; s < submeshIndices.Length; s++)
            {
                var source = submeshIndices[s];
                var list = new List<int>(source.Length);
                if (topologies[s] == MeshTopology.Triangles)
                {
                    for (int i = 0; i + 2 < source.Length; i += 3)
                    {
                        int a = source[i], b = source[i + 1], c = source[i + 2];
                        if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount) continue;
                        a = map[a]; b = map[b]; c = map[c];
                        if (remap[a] == remap[b] || remap[b] == remap[c] || remap[a] == remap[c]) continue;
                        list.Add(a); list.Add(b); list.Add(c);
                    }
                }
                else
                {
                    foreach (int v in source) if ((uint)v < (uint)vertexCount) list.Add(map[v]);
                }
                foreach (int v in list) used[v] = true;
                indices[s] = list.ToArray();
            }
            oldToNew = new int[vertexCount];
            int next = 0;
            for (int v = 0; v < vertexCount; v++) oldToNew[v] = used[v] ? next++ : -1;
            var newToOld = new int[next];
            for (int v = 0; v < vertexCount; v++) if (used[v]) newToOld[oldToNew[v]] = v;
            for (int s = 0; s < indices.Length; s++)
            {
                var list = indices[s];
                for (int i = 0; i < list.Length; i++) list[i] = oldToNew[list[i]];
            }
            return newToOld;
        }

        /// <summary>
        /// <paramref name="shapes"/> (the source mesh's blendshapes) on the mesh after the first <paramref name="collapses"/>
        /// collapses: each kept vertex keeps its own offsets, removed ones are dropped. Exact, at the cost of the table.
        /// </summary>
        public BlendShapeTable Shapes(int collapses, BlendShapeTable shapes)
        {
            if (shapes == null) throw new ArgumentNullException(nameof(shapes));
            if (shapes.VertexCount != vertexCount) throw new ArgumentException("The source mesh's blendshapes.", nameof(shapes));
            var newToOld = Result(collapses, out _, out var oldToNew);
            return shapes.Remap(oldToNew, newToOld.Length);
        }

        /// <summary>
        /// Writes the mesh after the first <paramref name="collapses"/> collapses into <paramref name="destination"/> (main
        /// thread): every vertex stream byte for byte, skin weights, bind poses, submeshes in their order, and
        /// <paramref name="shapes"/> when given (the source's, see <see cref="Shapes"/>). Returns <paramref name="destination"/>.
        /// </summary>
        public Mesh Write(int collapses, Mesh destination, BlendShapeTable shapes = null)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (shapes != null && shapes.VertexCount != vertexCount) throw new ArgumentException("The source mesh's blendshapes.", nameof(shapes));
            using (WriteMarker.Auto())
            {
                var newToOld = Result(collapses, out var indices, out var oldToNew);
                int n = newToOld.Length;
                destination.Clear();
                var writable = Mesh.AllocateWritableMeshData(1);
                try
                {
                    var data = writable[0];
                    data.SetVertexBufferParams(n, layout);
                    for (int s = 0; s < streams.Length; s++)
                    {
                        int stride = strides[s];
                        if (stride <= 0) continue;
                        var bytes = new byte[n * stride];
                        for (int j = 0; j < n; j++) Buffer.BlockCopy(streams[s], newToOld[j] * stride, bytes, j * stride, stride);
                        data.GetVertexData<byte>(s).CopyFrom(bytes);
                    }
                    int total = 0;
                    foreach (var list in indices) total += list.Length;
                    bool wide = n > 65535;
                    data.SetIndexBufferParams(total, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
                    if (wide)
                    {
                        var target = data.GetIndexData<int>();
                        int o = 0;
                        foreach (var list in indices) { NativeArray<int>.Copy(list, 0, target, o, list.Length); o += list.Length; }
                    }
                    else
                    {
                        var target = data.GetIndexData<ushort>();
                        int o = 0;
                        foreach (var list in indices) foreach (int v in list) target[o++] = (ushort)v;
                    }
                    data.subMeshCount = indices.Length;
                    for (int s = 0, start = 0; s < indices.Length; s++)
                    {
                        data.SetSubMesh(s, new SubMeshDescriptor(start, indices[s].Length, topologies[s]), MeshUpdateFlags.DontRecalculateBounds);
                        start += indices[s].Length;
                    }
                }
                catch
                {
                    writable.Dispose();
                    throw;
                }
                Mesh.ApplyAndDisposeWritableMeshData(writable, destination);
                if (bonesPerVertex != null)
                {
                    var counts = new NativeArray<byte>(n, Allocator.Temp);
                    int weightCount = 0;
                    for (int j = 0; j < n; j++) { counts[j] = bonesPerVertex[newToOld[j]]; weightCount += counts[j]; }
                    var weights = new NativeArray<BoneWeight1>(weightCount, Allocator.Temp);
                    try
                    {
                        for (int j = 0, o = 0; j < n; j++)
                        {
                            int v = newToOld[j];
                            for (int k = boneStart[v]; k < boneStart[v + 1]; k++) weights[o++] = boneWeights[k];
                        }
                        destination.SetBoneWeights(counts, weights);
                    }
                    finally
                    {
                        counts.Dispose();
                        weights.Dispose();
                    }
                }
                destination.bindposes = bindposes;
                destination.RecalculateBounds();
                if (shapes != null && shapes.ShapeCount > 0) shapes.Remap(oldToNew, n).WriteTo(destination);
                if (string.IsNullOrEmpty(destination.name)) destination.name = name;
                return destination;
            }
        }
    }
}
