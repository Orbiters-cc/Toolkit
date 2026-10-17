using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// Writes meshes to a Wavefront OBJ file (with its MTL), each submesh under the material name it is given: painting and
    /// sculpting applications (Substance 3D Painter, Blender...) make one material, or texture set, per name. Positions and
    /// normals are written in the right-handed, Y-up space OBJ files use (X mirrored from Unity's, faces turned back), the
    /// first UV set as it is.
    /// </summary>
    public sealed class ObjWriter
    {
        private readonly StringBuilder obj = new StringBuilder(1 << 20);
        private readonly List<string> materials = new List<string>();
        private int vertexBase = 1;

        /// <summary>The material names used, in the order they first appeared.</summary>
        public IReadOnlyList<string> Materials => materials;

        /// <summary>
        /// Adds an object: <paramref name="mesh"/> (baked: what it shows now) moved by <paramref name="toWorld"/>, each submesh
        /// under <paramref name="materialNames"/>[i] (a submesh without a name is left out). <paramref name="mirrored"/>: the
        /// mesh shows mirrored (a renderer under a negative scale baked without it); a mirroring matrix is seen on its own.
        /// </summary>
        public void Add(string objectName, Mesh mesh, Matrix4x4 toWorld, IList<string> materialNames, bool mirrored = false)
        {
            bool flip = mirrored ^ (toWorld.determinant < 0f);
            if (mesh == null || mesh.vertexCount == 0) return;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uvs = mesh.uv;
            bool hasNormals = normals.Length == vertices.Length, hasUvs = uvs.Length == vertices.Length;
            var normalMatrix = toWorld.inverse.transpose;
            obj.Append("o ").Append(Clean(objectName)).Append('\n');
            foreach (var v in vertices)
            {
                var p = toWorld.MultiplyPoint3x4(v);
                obj.Append("v ").Append(F(-p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            }
            if (hasUvs) foreach (var uv in uvs) obj.Append("vt ").Append(F(uv.x)).Append(' ').Append(F(uv.y)).Append('\n');
            if (hasNormals)
                foreach (var n in normals)
                {
                    var w = normalMatrix.MultiplyVector(n).normalized;
                    obj.Append("vn ").Append(F(-w.x)).Append(' ').Append(F(w.y)).Append(' ').Append(F(w.z)).Append('\n');
                }
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                string material = s < materialNames.Count ? materialNames[s] : null;
                if (string.IsNullOrEmpty(material) || mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                material = Clean(material);
                if (!materials.Contains(material)) materials.Add(material);
                obj.Append("usemtl ").Append(material).Append('\n');
                var triangles = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    // OBJ's X is Unity's mirrored: the same face seen from outside is listed the other way round (unless the
                    // mesh was mirrored already).
                    obj.Append('f');
                    Corner(triangles[t], hasUvs, hasNormals);
                    Corner(triangles[flip ? t + 1 : t + 2], hasUvs, hasNormals);
                    Corner(triangles[flip ? t + 2 : t + 1], hasUvs, hasNormals);
                    obj.Append('\n');
                }
            }
            vertexBase += vertices.Length;
        }

        private void Corner(int index, bool uv, bool normal)
        {
            string i = (vertexBase + index).ToString(CultureInfo.InvariantCulture);
            obj.Append(' ').Append(i);
            if (uv || normal) obj.Append('/').Append(uv ? i : "");
            if (normal) obj.Append('/').Append(i);
        }

        /// <summary>Writes <paramref name="path"/> and its MTL next to it (same name).</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string mtl = Path.ChangeExtension(path, ".mtl");
            var header = new StringBuilder();
            header.Append("# Written by Orbiters Toolkit\n").Append("mtllib ").Append(Path.GetFileName(mtl)).Append('\n');
            File.WriteAllText(path, header.ToString() + obj, new UTF8Encoding(false));
            var library = new StringBuilder();
            foreach (var material in materials) library.Append("newmtl ").Append(material).Append("\nKd 1 1 1\nd 1\nillum 2\n\n");
            File.WriteAllText(mtl, library.ToString(), new UTF8Encoding(false));
        }

        /// <summary>A name OBJ readers take whole: ASCII letters, digits, '_' and '-' (spaces and dots would split or cut it).</summary>
        public static string Clean(string name)
        {
            var clean = new string((name ?? "").Select(c => c < 128 && char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray()).Trim('_');
            return string.IsNullOrEmpty(clean) ? "unnamed" : clean;
        }

        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
