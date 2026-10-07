using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// Whether a material shows the back of its faces, and whether the mesh part it covers needs that: fur cards,
    /// feathers and planes are open surfaces seen from both sides, while a closed body never shows its inside. A part is
    /// open when many of its edges belong to a single triangle (Rexouium: body 0.01 %, eyes 2 %, feathers 24–34 %, tail
    /// fur cards 100 %). Unity's Standard shader always hides back faces.
    /// </summary>
    public static class MaterialSides
    {
        public const float OpenThreshold = .15f;
        private static readonly Dictionary<(int mesh, int submesh, int vertices, int indices), float> ratios = new Dictionary<(int, int, int, int), float>();

        public static bool IsStandard(Material material) =>
            material && material.shader && (material.shader.name == "Standard" || material.shader.name == "Standard (Specular setup)");

        /// <summary>True or false when the material's culling is known (Toon Standard's _Culling, _Cull of Poiyomi and most shaders, Standard), null otherwise.</summary>
        public static bool? ShowsBackFaces(Material material)
        {
            if (!material || !material.shader) return null;
            if (IsStandard(material)) return false;
            if (material.HasProperty("_Culling")) return material.GetFloat("_Culling") == 0;
            if (material.HasProperty("_Cull")) return material.GetFloat("_Cull") == 0;
            return null;
        }

        /// <summary>Whether the material's culling can be changed: a culling property, outside a locked (baked) shader.</summary>
        public static bool CanShowBackFaces(Material material) =>
            material && material.shader && !material.shader.name.StartsWith("Hidden/Locked/", System.StringComparison.Ordinal) &&
            (material.HasProperty("_Culling") || material.HasProperty("_Cull"));

        /// <summary>Turns culling off. Record the material for Undo first.</summary>
        public static void ShowBackFaces(Material material)
        {
            if (material.HasProperty("_Culling")) material.SetFloat("_Culling", 0);
            else if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0);
        }

        /// <summary>The share of a mesh part's edges used by a single triangle, with vertices welded by position (UV seams stay closed).</summary>
        public static float OpenRatio(Mesh mesh, int submesh)
        {
            if (!mesh || submesh < 0 || submesh >= mesh.subMeshCount || !mesh.isReadable) return 0f;
            var key = (mesh.GetInstanceID(), submesh, mesh.vertexCount, (int)mesh.GetIndexCount(submesh));
            if (ratios.TryGetValue(key, out float known)) return known;
            var vertices = mesh.vertices;
            var triangles = mesh.GetTriangles(submesh);
            var weld = new int[vertices.Length];
            var positions = new Dictionary<Vector3Int, int>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                var cell = Vector3Int.RoundToInt(vertices[i] * 10000f);
                if (!positions.TryGetValue(cell, out int id)) { id = positions.Count; positions.Add(cell, id); }
                weld[i] = id;
            }
            var edges = new Dictionary<long, int>(triangles.Length);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[triangles[t + e]], b = weld[triangles[t + (e + 1) % 3]];
                    if (a == b) continue;
                    long edge = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    edges.TryGetValue(edge, out int count);
                    edges[edge] = count + 1;
                }
            int open = 0;
            foreach (int count in edges.Values) if (count == 1) open++;
            float ratio = edges.Count > 0 ? (float)open / edges.Count : 0f;
            ratios[key] = ratio;
            return ratio;
        }

        public static bool IsOpen(Mesh mesh, int submesh) => OpenRatio(mesh, submesh) >= OpenThreshold;
    }
}
