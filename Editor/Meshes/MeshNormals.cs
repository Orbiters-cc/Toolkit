using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>Vertex normals computed again from a mesh's own faces.</summary>
    public static class MeshNormals
    {
        /// <summary>
        /// Gives the chosen vertices smooth normals from the faces around them as the mesh is now (area-weighted, smoothed
        /// across the vertices at the same place: UV seams don't show), and leaves every other normal as it is. Blendshape
        /// normals computed where surfaces touch (inner thighs pressed against the crotch at rest) come out wrong; computed
        /// again on a pose that parts them, they are right.
        /// </summary>
        public static void Recalculate(Mesh mesh, IReadOnlyCollection<int> chosen)
        {
            if (chosen == null || chosen.Count == 0) return;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            if (normals == null || normals.Length != vertices.Length) return;
            var atPlace = new Dictionary<Vector3, Vector3>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var triangles = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                    // The cross product's length is twice the triangle's area: bigger faces weigh more.
                    var face = Vector3.Cross(b - a, c - a);
                    foreach (var corner in new[] { a, b, c })
                        atPlace[corner] = atPlace.TryGetValue(corner, out var sum) ? sum + face : face;
                }
            }
            foreach (int i in chosen)
            {
                if (i < 0 || i >= vertices.Length || !atPlace.TryGetValue(vertices[i], out var sum)) continue;
                // By hand: small faces (millimetres in metres) sum below Vector3.Normalize's threshold, which would give zero.
                float length = Mathf.Sqrt(sum.sqrMagnitude);
                if (length > 0f) normals[i] = sum / length;
            }
            mesh.normals = normals;
        }

        /// <summary>The vertices a renderer's blendshapes move now (those with weight), by more than a hundredth of a millimetre.</summary>
        public static List<int> MovedByBlendShapes(SkinnedMeshRenderer renderer)
        {
            var moved = new List<int>();
            var mesh = renderer.sharedMesh;
            if (!mesh || mesh.blendShapeCount == 0) return moved;
            var total = new Vector3[mesh.vertexCount];
            var deltas = new Vector3[mesh.vertexCount];
            var deltaNormals = new Vector3[mesh.vertexCount];
            var deltaTangents = new Vector3[mesh.vertexCount];
            bool any = false;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float weight = renderer.GetBlendShapeWeight(s);
                if (Mathf.Abs(weight) < 1e-4f) continue;
                int frame = mesh.GetBlendShapeFrameCount(s) - 1;
                float full = mesh.GetBlendShapeFrameWeight(s, frame);
                mesh.GetBlendShapeFrameVertices(s, frame, deltas, deltaNormals, deltaTangents);
                float scale = full != 0f ? weight / full : 0f;
                for (int v = 0; v < deltas.Length; v++) total[v] += deltas[v] * scale;
                any = true;
            }
            if (!any) return moved;
            for (int v = 0; v < total.Length; v++)
                if (total[v].sqrMagnitude > 1e-10f) moved.Add(v);
            return moved;
        }
    }
}
