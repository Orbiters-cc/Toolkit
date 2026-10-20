using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>Vertex normals computed again from a mesh's own faces.</summary>
    public static class MeshNormals
    {
        /// <summary>
        /// Vertices at one place share their faces only when their normals agree this much (60°): the copies Unity makes at
        /// a UV seam do, the sides of a hard edge and the two sheets of double-sided cloth do not.
        /// </summary>
        public const float SeamNormalDot = 0.5f;

        /// <summary>
        /// Gives the chosen vertices smooth normals from the faces around them as the mesh is now (area-weighted, smoothed
        /// across the vertices at the same place whose normals agree: UV seams don't show, hard edges and touching sheets
        /// stay apart), turns their tangents to the new normals, and leaves every other vertex as it is. Blendshape normals
        /// computed where surfaces touch (inner thighs pressed against the crotch at rest) come out wrong; computed again on
        /// a pose that parts them, they are right.
        /// </summary>
        public static void Recalculate(Mesh mesh, IReadOnlyCollection<int> chosen)
        {
            if (chosen == null || chosen.Count == 0) return;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            if (normals == null || normals.Length != vertices.Length) return;
            var original = (Vector3[])normals.Clone();

            // The places of the chosen vertices, and every vertex at one of them.
            var atPlace = new Dictionary<Vector3, List<int>>();
            foreach (int i in chosen)
                if (i >= 0 && i < vertices.Length && !atPlace.ContainsKey(vertices[i])) atPlace[vertices[i]] = new List<int>(2);
            for (int v = 0; v < vertices.Length; v++)
                if (atPlace.TryGetValue(vertices[v], out var here)) here.Add(v);

            // Each of those vertices' faces. The cross product's length is twice the triangle's area: bigger faces weigh more.
            var faces = new Dictionary<int, Vector3>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var triangles = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                    Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                    if (!atPlace.ContainsKey(a) && !atPlace.ContainsKey(b) && !atPlace.ContainsKey(c)) continue;
                    var face = Vector3.Cross(b - a, c - a);
                    foreach (int corner in new[] { ia, ib, ic })
                        faces[corner] = faces.TryGetValue(corner, out var sum) ? sum + face : face;
                }
            }

            var tangents = mesh.tangents;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            foreach (int i in chosen)
            {
                if (i < 0 || i >= vertices.Length || !atPlace.TryGetValue(vertices[i], out var here)) continue;
                var sum = Vector3.zero;
                foreach (int j in here)
                    if (j == i || Agree(original[i], original[j])) sum += faces.TryGetValue(j, out var face) ? face : Vector3.zero;
                // By hand: small faces (millimetres in metres) sum below Vector3.Normalize's threshold, which would give zero.
                float length = Mathf.Sqrt(sum.sqrMagnitude);
                if (length <= 0f) continue;
                var normal = sum / length;
                normals[i] = normal;
                if (!hasTangents) continue;
                // The tangent keeps its direction along the surface, square to the new normal (Gram-Schmidt), and its sign.
                var tangent = (Vector3)tangents[i];
                var along = tangent - normal * Vector3.Dot(normal, tangent);
                float alongLength = Mathf.Sqrt(along.sqrMagnitude);
                if (alongLength > 0f) tangents[i] = new Vector4(along.x / alongLength, along.y / alongLength, along.z / alongLength, tangents[i].w < 0f ? -1f : 1f);
            }
            mesh.normals = normals;
            if (hasTangents) mesh.tangents = tangents;
        }

        // A missing (zero) normal agrees with any.
        private static bool Agree(Vector3 a, Vector3 b)
        {
            float la = a.sqrMagnitude, lb = b.sqrMagnitude;
            return la < 1e-12f || lb < 1e-12f || Vector3.Dot(a, b) >= SeamNormalDot * Mathf.Sqrt(la * lb);
        }

        /// <summary>
        /// The vertices a renderer's blendshapes move now (those with weight, in-between frames included, as Unity's
        /// skinning applies them), by more than a hundredth of a millimetre.
        /// </summary>
        public static List<int> MovedByBlendShapes(SkinnedMeshRenderer renderer)
        {
            var moved = new List<int>();
            var mesh = renderer.sharedMesh;
            if (!mesh || mesh.blendShapeCount == 0) return moved;
            var total = new Vector3[mesh.vertexCount];
            var scratch = new BlendShapeEvaluation.Scratch(mesh.vertexCount);
            bool clamp = BlendShapeEvaluation.ClampWeights;
            bool any = false;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float weight = renderer.GetBlendShapeWeight(s);
                if (Mathf.Abs(weight) < 1e-4f && mesh.GetBlendShapeFrameWeight(s, 0) >= 0f) continue;
                BlendShapeEvaluation.Add(mesh, s, weight, clamp, total, null, scratch);
                any = true;
            }
            if (!any) return moved;
            for (int v = 0; v < total.Length; v++)
                if (total[v].sqrMagnitude > 1e-10f) moved.Add(v);
            return moved;
        }
    }
}
