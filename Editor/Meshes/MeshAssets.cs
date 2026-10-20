using System.Collections.Generic;
using System.IO;
using Orbiters.Toolkit.Storage;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// Saves meshes as native <c>.asset</c> files: the only kind of import that costs milliseconds, since no model
    /// importer runs. A tool that edits meshes commits here instead of rewriting an FBX, which would re-import for
    /// tens of seconds on a body with many blendshapes. A new mesh never takes the place of another asset that has
    /// its name (two avatars both have a "Body"); a mesh made again from one of the tool's own assets replaces that
    /// asset in place, so every renderer and prefab that points at it keeps pointing at it.
    /// </summary>
    public static class MeshAssets
    {
        /// <summary>Where a product keeps the meshes it made: <c>Assets/Orbiters/&lt;product&gt;/Meshes/&lt;owner&gt;</c>.</summary>
        public static string Folder(string product, string owner) =>
            "Assets/Orbiters/" + Clean(product) + "/Meshes/" + Clean(owner);

        /// <summary>
        /// Writes <paramref name="mesh"/> as a new asset: <c>folder/name.asset</c>, or <c>name 1.asset</c>,
        /// <c>name 2.asset</c>… when that file exists already. Returns the asset: <paramref name="mesh"/> itself, or a copy
        /// of it when it is an asset already (a mesh of a model, of another file), which stays as it is.
        /// </summary>
        public static Mesh Save(Mesh mesh, string folder, string name)
        {
            if (mesh == null) throw new System.ArgumentNullException(nameof(mesh));
            if (AssetDatabase.Contains(mesh)) mesh = Object.Instantiate(mesh);
            EnsureFolder(folder);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Clean(name) + ".asset");
            mesh.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssetIfDirty(mesh);
            return mesh;
        }

        /// <summary>
        /// Copies <paramref name="mesh"/> into the asset <paramref name="existing"/> (its file, name and GUID stay, so
        /// references to it hold) and returns <paramref name="existing"/>. <paramref name="mesh"/> is destroyed unless it
        /// is an asset itself: the caller must not keep using it. <paramref name="existing"/> must be a mesh of the project's
        /// own files (<c>.asset</c>...): a mesh a model importer made (an FBX's) would lose the change at its next import.
        /// </summary>
        public static Mesh Replace(Mesh mesh, Mesh existing)
        {
            if (mesh == null) throw new System.ArgumentNullException(nameof(mesh));
            if (!IsNativeAsset(existing)) throw new System.ArgumentException("Not a mesh asset of the project's own (a model's meshes are made again at each import).", nameof(existing));
            if (mesh == existing) return existing;
            string name = existing.name;
            // Every renderer and prefab points at this asset: a copy that fails half way puts the old mesh back instead of
            // leaving it cleared for the next save to write out.
            var backup = Object.Instantiate(existing);
            try
            {
                CopyInto(mesh, existing);
            }
            catch
            {
                CopyInto(backup, existing);
                existing.name = name;
                throw;
            }
            finally
            {
                Object.DestroyImmediate(backup);
            }
            existing.name = name;
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            if (!AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            return existing;
        }

        // Every part of a mesh, written through the mesh API. EditorUtility.CopySerialized leaves a skinned mesh's vertices as
        // they were (Unity 2022.3; with Clear first it leaves none): Mixer's in-place commits of skinned meshes kept the old
        // shape.
        private static void CopyInto(Mesh from, Mesh to)
        {
            to.Clear();
            to.indexFormat = from.indexFormat;
            to.SetVertices(from.vertices);
            if (from.HasVertexAttribute(VertexAttribute.Normal)) to.SetNormals(from.normals);
            if (from.HasVertexAttribute(VertexAttribute.Tangent)) to.SetTangents(from.tangents);
            if (from.HasVertexAttribute(VertexAttribute.Color)) to.SetColors(from.colors);
            var buffer = new List<Vector4>();
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!from.HasVertexAttribute(attribute)) continue;
                int dimension = from.GetVertexAttributeDimension(attribute);
                from.GetUVs(channel, buffer);
                if (dimension <= 2) to.SetUVs(channel, buffer.ConvertAll(uv => (Vector2)uv));
                else if (dimension == 3) to.SetUVs(channel, buffer.ConvertAll(uv => (Vector3)uv));
                else to.SetUVs(channel, buffer);
            }
            var counts = from.GetBonesPerVertex();
            if (counts.Length > 0)
                using (var perVertex = new NativeArray<byte>(counts, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(from.GetAllBoneWeights(), Allocator.Temp))
                    to.SetBoneWeights(perVertex, weights);
            to.bindposes = from.bindposes;
            to.subMeshCount = from.subMeshCount;
            // Indices as stored, with their base vertex: a 16-bit mesh reaches vertices past 65535 only through it.
            for (int s = 0; s < from.subMeshCount; s++)
                to.SetIndices(from.GetIndices(s, false), from.GetTopology(s), s, false, (int)from.GetBaseVertex(s));
            int n = from.vertexCount;
            var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
            for (int shape = 0; shape < from.blendShapeCount; shape++)
                for (int frame = 0; frame < from.GetBlendShapeFrameCount(shape); frame++)
                {
                    from.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                    to.AddBlendShapeFrame(from.GetBlendShapeName(shape), from.GetBlendShapeFrameWeight(shape, frame), dv, dn, dt);
                }
            to.bounds = from.bounds;
        }

        /// <summary>Whether <paramref name="mesh"/> is an asset file of the project's own (not a model's) under a product's folder.</summary>
        public static bool IsOwnedBy(Mesh mesh, string product)
        {
            if (!IsNativeAsset(mesh)) return false;
            string path = AssetDatabase.GetAssetPath(mesh);
            return path.StartsWith("Assets/Orbiters/" + Clean(product) + "/", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates the folder (and its parents) through the asset database without a full refresh.</summary>
        public static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent)) return;
            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, leaf);
        }

        private static bool IsNativeAsset(Mesh mesh) => mesh != null && AssetDatabase.Contains(mesh) && AssetDatabase.IsNativeAsset(mesh);

        /// <summary>
        /// <paramref name="name"/> as a file or folder name (<see cref="SafePaths.FileName"/>): separators and reserved characters
        /// become "_", never "." or "..", never a device name (CON, NUL...), at most 64 characters; "Mesh" when nothing is left.
        /// </summary>
        public static string Clean(string name) => SafePaths.FileName(name, "Mesh");
    }
}
