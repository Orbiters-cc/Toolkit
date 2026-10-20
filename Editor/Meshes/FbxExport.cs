using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// One mesh for <see cref="FbxExport"/>: where it sits, the mesh itself (a baked or edited copy works as well as the
    /// renderer's own), a material per submesh and, skinned, the bones its bind poses and weights refer to.
    /// </summary>
    public sealed class FbxExportMesh
    {
        /// <summary>Where the mesh sits: its object becomes the file's mesh model.</summary>
        public Transform Transform;
        public Mesh Mesh;
        /// <summary>The model's name; the object's when null.</summary>
        public string Name;
        /// <summary>A material per submesh; a missing one becomes a slot named after the mesh.</summary>
        public Material[] Materials = Array.Empty<Material>();
        /// <summary>Skinned: the bones of the mesh's bind poses and weights, by index. Null or empty: not skinned.</summary>
        public Transform[] Bones;
        /// <summary>The weight each blendshape shows, by index (zero past its end): the file's default weights.</summary>
        public float[] BlendShapeWeights = Array.Empty<float>();
        /// <summary>Blendshapes applied to the mesh at their weight and left out of the file (a fit to keep, say).</summary>
        public readonly HashSet<string> Baked = new HashSet<string>(StringComparer.Ordinal);

        public string DisplayName => Name ?? (Transform != null ? Transform.name : Mesh != null ? Mesh.name : "Mesh");

        public float Weight(int shape) => BlendShapeWeights != null && shape >= 0 && shape < BlendShapeWeights.Length ? BlendShapeWeights[shape] : 0f;

        /// <summary>
        /// A renderer as it shows: its mesh (or <paramref name="mesh"/> in its place, its blendshapes taking the weights of
        /// the renderer's shapes of the same names), materials, bones and blendshape weights.
        /// </summary>
        public static FbxExportMesh From(Renderer renderer, Mesh mesh = null)
        {
            var item = new FbxExportMesh { Transform = renderer != null ? renderer.transform : null };
            if (renderer == null) return item;
            item.Materials = renderer.sharedMaterials ?? Array.Empty<Material>();
            if (renderer is SkinnedMeshRenderer skinned)
            {
                var shown = skinned.sharedMesh;
                item.Mesh = mesh != null ? mesh : shown;
                item.Bones = skinned.bones;
                if (item.Mesh != null)
                {
                    item.BlendShapeWeights = new float[item.Mesh.blendShapeCount];
                    for (int i = 0; i < item.BlendShapeWeights.Length; i++)
                    {
                        int index = item.Mesh == shown ? i : shown != null ? shown.GetBlendShapeIndex(item.Mesh.GetBlendShapeName(i)) : -1;
                        item.BlendShapeWeights[i] = index >= 0 ? skinned.GetBlendShapeWeight(index) : 0f;
                    }
                }
            }
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                item.Mesh = mesh != null ? mesh : filter != null ? filter.sharedMesh : null;
            }
            return item;
        }
    }

    /// <summary>What <see cref="FbxExport.Write"/> did: the file written, or why not.</summary>
    public sealed class FbxExportResult
    {
        public string Path;
        /// <summary>Why nothing was written; null when the file was.</summary>
        public string Error;
        public bool Success => Error == null;
        public int Meshes, Bones, BlendShapes;
        public long Bytes;
        /// <summary>What was left out or changed on the way (weights to missing bones, shared material slots...).</summary>
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Writes Unity meshes to a binary FBX file (version 7400, as Blender writes it) that Unity's model importer and Blender
    /// read back as they were: Y up and metres (unit scale 100, the file's X mirrored from Unity's and faces wound the other
    /// way round, see <see cref="FbxDocument.ToUnity"/>); each mesh with its parents up to the export root (bones are
    /// LimbNodes, the objects above them Nulls), normals, every UV set, colours and a material per submesh (its main texture
    /// linked); skinned ones with a skin cluster per weighted bone and a bind pose that gives back the mesh's bind poses;
    /// blendshapes with their in-between frames and the weights they show. Vertices Unity split (UV seams, hard edges) are
    /// one control point again where their place, weights and blendshape offsets agree. Tangents are left to the importer.
    /// </summary>
    public static class FbxExport
    {
        /// <summary>
        /// Writes <paramref name="meshes"/> to <paramref name="path"/> (through a temporary file, so a failure leaves any
        /// earlier file whole). Their objects are placed in <paramref name="root"/>'s space, which is not written itself; by
        /// default the deepest object above every mesh and bone (<see cref="CommonRoot"/>). Never throws for a mesh or a file
        /// that can't be written: the result says why.
        /// </summary>
        public static FbxExportResult Write(string path, IEnumerable<FbxExportMesh> meshes, Transform root = null)
        {
            var result = new FbxExportResult { Path = path };
            var items = (meshes ?? Enumerable.Empty<FbxExportMesh>()).Where(m => m != null).ToList();
            try
            {
                result.Error = Check(path, items, root);
                if (result.Error != null) return result;
                string full = Path.GetFullPath(path);
                result.Path = full;
                var document = Build(items, root, full, result);
                byte[] bytes = document.Write();
                Save(full, bytes);
                result.Bytes = bytes.LongLength;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)) Debug.LogException(ex);
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>The file's records for <paramref name="meshes"/> (checked already), texture paths relative to <paramref name="path"/>.</summary>
        public static FbxDocument Build(IReadOnlyList<FbxExportMesh> meshes, Transform root, string path, FbxExportResult result)
        {
            var writer = new Writer(result ?? new FbxExportResult(), Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
            return writer.Build(meshes, root != null ? root : CommonRoot(meshes));
        }

        /// <summary>
        /// The deepest object above every mesh and bone of <paramref name="meshes"/> (one above it when it is one of them, as
        /// the root itself is not written); null when they have none in common (the scene's space).
        /// </summary>
        public static Transform CommonRoot(IEnumerable<FbxExportMesh> meshes)
        {
            var all = (meshes ?? Enumerable.Empty<FbxExportMesh>()).Where(m => m != null)
                .SelectMany(m => new[] { m.Transform }.Concat(m.Bones ?? Array.Empty<Transform>())).Where(t => t != null).Distinct().ToList();
            if (all.Count == 0) return null;
            var common = all[0];
            foreach (var t in all)
                while (common != null && !t.IsChildOf(common)) common = common.parent;
            if (common != null && all.Contains(common)) common = common.parent;
            return common;
        }

        private static string Check(string path, List<FbxExportMesh> items, Transform root)
        {
            if (string.IsNullOrWhiteSpace(path)) return "No file to write to.";
            if (items.Count == 0) return "Nothing to export.";
            var seen = new HashSet<Transform>();
            foreach (var item in items)
            {
                if (item.Transform == null) return "A mesh to export has no object.";
                string name = item.DisplayName;
                if (!seen.Add(item.Transform)) return name + " is in the export twice.";
                var mesh = item.Mesh;
                if (mesh == null) return name + " has no mesh.";
                if (!mesh.isReadable && Application.isPlaying) return name + "'s mesh can't be read in Play mode (Read/Write is off).";
                if (mesh.vertexCount == 0) return name + " has no vertices.";
                bool faces = false;
                for (int s = 0; s < mesh.subMeshCount && !faces; s++) faces = mesh.GetTopology(s) == MeshTopology.Triangles || mesh.GetTopology(s) == MeshTopology.Quads;
                if (!faces) return name + " has no triangles.";
                if (root == null) continue;
                foreach (var t in new[] { item.Transform }.Concat(item.Bones ?? Array.Empty<Transform>()))
                    if (t != null && (t == root || !t.IsChildOf(root))) return t.name + " is not under " + root.name + ".";
            }
            return null;
        }

        // Next to the file under a name Unity's asset database skips, then over it.
        private static void Save(string full, byte[] bytes)
        {
            string folder = Path.GetDirectoryName(full) ?? "";
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(full)) File.Replace(temp, full, null);
                else File.Move(temp, full);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        /// <summary>
        /// A blendshape's offsets at a weight, as Unity blends its frames: from nothing to the first frame below it, then
        /// between the two frames around it, past the last one along it.
        /// </summary>
        public static void ShapeAt(Mesh mesh, int shape, float weight, Vector3[] deltas, Vector3[] normals)
        {
            int frames = mesh.GetBlendShapeFrameCount(shape), n = mesh.vertexCount;
            int upper = 0;
            while (upper < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, upper) < weight) upper++;
            float upperWeight = mesh.GetBlendShapeFrameWeight(shape, upper);
            float lowerWeight = upper == 0 ? 0f : mesh.GetBlendShapeFrameWeight(shape, upper - 1);
            float t = upperWeight == lowerWeight ? 1f : (weight - lowerWeight) / (upperWeight - lowerWeight);
            mesh.GetBlendShapeFrameVertices(shape, upper, deltas, normals, null);
            for (int v = 0; v < n; v++)
            {
                deltas[v] *= t;
                if (normals != null) normals[v] *= t;
            }
            if (upper == 0 || t == 1f) return;
            var lowerDeltas = new Vector3[n];
            var lowerNormals = normals != null ? new Vector3[n] : null;
            mesh.GetBlendShapeFrameVertices(shape, upper - 1, lowerDeltas, lowerNormals, null);
            for (int v = 0; v < n; v++)
            {
                deltas[v] += lowerDeltas[v] * (1f - t);
                if (normals != null) normals[v] += lowerNormals[v] * (1f - t);
            }
        }

        private sealed class Writer
        {
            // Unity's space to the file's and back: X mirrored.
            private static readonly Matrix4x4 Mirror = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            private static readonly string[] MainTextures = { "_MainTex", "_BaseMap" };
            private static readonly string[] TypeOrder = { "GlobalSettings", "NodeAttribute", "Geometry", "Model", "Pose", "Deformer", "Material", "Texture", "Video" };

            private sealed class Model
            {
                public long Id;
                public Matrix4x4 Global;
            }

            private sealed class Cluster
            {
                public Transform Bone;
                public Matrix4x4 Bindpose;
                public readonly List<int> Indexes = new List<int>();
                public readonly List<double> Weights = new List<double>();
            }

            private readonly FbxExportResult result;
            private readonly string folder;
            private readonly FbxNode objects = new FbxNode("Objects");
            private readonly FbxNode connections = new FbxNode("Connections");
            private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            private readonly Dictionary<Transform, Model> models = new Dictionary<Transform, Model>();
            private readonly Dictionary<Material, long> materials = new Dictionary<Material, long>();
            private readonly Dictionary<Texture, long> textures = new Dictionary<Texture, long>();
            private readonly HashSet<string> materialNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<Transform> meshObjects = new HashSet<Transform>();
            private readonly HashSet<Transform> limbs = new HashSet<Transform>();
            private Transform root;
            private long nextId = 1000000;

            public Writer(FbxExportResult result, string folder)
            {
                this.result = result;
                this.folder = folder;
            }

            public FbxDocument Build(IReadOnlyList<FbxExportMesh> items, Transform space)
            {
                root = space;
                var bones = new List<Transform>();
                foreach (var item in items)
                {
                    meshObjects.Add(item.Transform);
                    foreach (var bone in item.Bones ?? Array.Empty<Transform>())
                        if (bone != null && !bones.Contains(bone)) bones.Add(bone);
                }
                // Bones are LimbNodes, as is an object between two of them (else an armature would break there).
                var boneSet = new HashSet<Transform>(bones);
                limbs.UnionWith(bones);
                foreach (var bone in bones)
                {
                    var between = new List<Transform>();
                    for (var p = bone.parent; p != null && p != root; p = p.parent)
                    {
                        if (boneSet.Contains(p)) { limbs.UnionWith(between); break; }
                        between.Add(p);
                    }
                }
                foreach (var item in items) AddModel(item.Transform);
                foreach (var bone in bones) AddModel(bone);
                result.Bones = bones.Count;
                foreach (var item in items) AddMesh(item);
                result.Meshes = items.Count;
                return Document();
            }

            // ---- Objects -------------------------------------------------------------------------------------------------

            private long Id() => nextId++;

            private FbxNode Object(string type, long id, string name, string nameClass, string subclass)
            {
                counts[type] = (counts.TryGetValue(type, out int count) ? count : 0) + 1;
                return objects.Add(type, id, (name ?? "") + "\0\u0001" + nameClass, subclass);
            }

            private void Connect(long child, long parent) => connections.Add("C", "OO", child, parent);

            private static void P(FbxNode properties, string name, string type, string label, string flags, params object[] values)
            {
                var p = properties.Add("P", name, type, label, flags);
                p.Properties.AddRange(values);
            }

            private static Matrix4x4 Local(Transform t) => Mirror * Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * Mirror;

            // An object and its parents up to the root, parents first: placed by its local translation, XYZ rotation and scale.
            private Model AddModel(Transform t)
            {
                if (t == null || t == root) return null;
                if (models.TryGetValue(t, out var model)) return model;
                var parent = AddModel(t.parent);
                string kind = meshObjects.Contains(t) ? "Mesh" : limbs.Contains(t) ? "LimbNode" : "Null";
                model = new Model { Id = Id(), Global = (parent != null ? parent.Global : Matrix4x4.identity) * Local(t) };
                models[t] = model;
                var node = Object("Model", model.Id, t.name, "Model", kind);
                node.Add("Version", 232);
                var properties = node.Add("Properties70");
                Vector3 p = t.localPosition, s = t.localScale;
                var r = EulerXyz(t.localRotation);
                P(properties, "Lcl Translation", "Lcl Translation", "", "A", -(double)p.x, (double)p.y, (double)p.z);
                P(properties, "Lcl Rotation", "Lcl Rotation", "", "A", r[0], r[1], r[2]);
                P(properties, "Lcl Scaling", "Lcl Scaling", "", "A", (double)s.x, (double)s.y, (double)s.z);
                P(properties, "DefaultAttributeIndex", "int", "Integer", "", 0);
                P(properties, "InheritType", "enum", "", "", 1);
                node.Add("MultiLayer", 0);
                node.Add("MultiTake", 0);
                node.Add("Shading", true);
                node.Add("Culling", "CullingOff");
                if (kind != "Mesh")
                {
                    long attributeId = Id();
                    Object("NodeAttribute", attributeId, t.name, "NodeAttribute", kind).Add("TypeFlags", kind == "LimbNode" ? "Skeleton" : "Null");
                    Connect(attributeId, model.Id);
                }
                Connect(model.Id, parent != null ? parent.Id : 0L);
                return model;
            }

            // A Unity rotation as the file's XYZ Euler angles in degrees (X turned first), in double precision: mirrored, it
            // turns the other way about X's mirror image.
            private static double[] EulerXyz(Quaternion q)
            {
                double x = q.x, y = -q.y, z = -q.z, w = q.w;
                double length = Math.Sqrt(x * x + y * y + z * z + w * w);
                if (length < 1e-12) return new double[3];
                x /= length; y /= length; z /= length; w /= length;
                double m00 = 1 - 2 * (y * y + z * z), m10 = 2 * (x * y + w * z), m20 = 2 * (x * z - w * y);
                double m11 = 1 - 2 * (x * x + z * z), m12 = 2 * (y * z - w * x), m21 = 2 * (y * z + w * x), m22 = 1 - 2 * (x * x + y * y);
                double beta = Math.Asin(Math.Max(-1d, Math.Min(1d, -m20))), alpha, gamma;
                if (Math.Abs(m20) < 0.9999999d)
                {
                    alpha = Math.Atan2(m21, m22);
                    gamma = Math.Atan2(m10, m00);
                }
                else
                {
                    // Gimbal lock: X and Z turn about the same axis; it all goes to X.
                    gamma = 0d;
                    alpha = Math.Atan2(-m12, m11);
                }
                const double degrees = 180d / Math.PI;
                return new[] { alpha * degrees, beta * degrees, gamma * degrees };
            }

            // ---- Meshes --------------------------------------------------------------------------------------------------

            private void AddMesh(FbxExportMesh item)
            {
                var mesh = item.Mesh;
                var model = models[item.Transform];
                string name = item.DisplayName;
                int n = mesh.vertexCount;

                var positions = mesh.vertices;
                var normals = mesh.normals;
                bool hasNormals = normals.Length == n;
                var colors = mesh.HasVertexAttribute(VertexAttribute.Color) ? mesh.colors : null;
                if (colors != null && colors.Length != n) colors = null;
                var uvs = Uvs(mesh);

                // Blendshapes kept on the mesh at their weight.
                var baked = new bool[mesh.blendShapeCount];
                var dv = new Vector3[n];
                var dn = new Vector3[n];
                foreach (string shapeName in item.Baked)
                {
                    int shape = mesh.GetBlendShapeIndex(shapeName);
                    if (shape < 0) { result.Warnings.Add(name + " has no blendshape " + shapeName + " to apply."); continue; }
                    baked[shape] = true;
                    float weight = item.Weight(shape);
                    if (weight == 0f || mesh.GetBlendShapeFrameCount(shape) == 0) continue;
                    ShapeAt(mesh, shape, weight, dv, hasNormals ? dn : null);
                    for (int v = 0; v < n; v++)
                    {
                        positions[v] += dv[v];
                        if (hasNormals) normals[v] += dn[v];
                    }
                }
                if (hasNormals && baked.Any(b => b))
                    for (int v = 0; v < n; v++)
                        if (normals[v].sqrMagnitude > 0f) normals[v] = normals[v].normalized;

                // Polygons: Unity's vertex at each corner, wound the other way round for the mirrored X; a slot each.
                long cornerCount = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) cornerCount += mesh.GetIndexCount(s);
                var corners = new List<int>((int)Math.Min(cornerCount, int.MaxValue));
                var polygonEnds = new List<int>();
                var polygonSlots = new List<int>();
                var slotMaterials = new List<long>();
                var slotSubmesh = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var topology = mesh.GetTopology(s);
                    int size = topology == MeshTopology.Triangles ? 3 : topology == MeshTopology.Quads ? 4 : 0;
                    if (size == 0) { result.Warnings.Add(name + ": submesh " + s + " is made of " + topology.ToString().ToLowerInvariant() + ", left out."); continue; }
                    var indices = mesh.GetIndices(s);
                    if (indices.Length < size) continue;
                    long material = MaterialId(item, s, name);
                    int slot = slotMaterials.IndexOf(material);
                    if (slot < 0)
                    {
                        slot = slotMaterials.Count;
                        slotMaterials.Add(material);
                        slotSubmesh.Add(s);
                    }
                    else result.Warnings.Add(name + ": submeshes " + slotSubmesh[slot] + " and " + s + " share a material, one slot in the file.");
                    for (int i = 0; i + size <= indices.Length; i += size)
                    {
                        bool valid = true;
                        for (int k = 0; k < size && valid; k++) valid = indices[i + k] >= 0 && indices[i + k] < n;
                        if (!valid) continue;
                        corners.Add(indices[i]);
                        for (int k = size - 1; k >= 1; k--) corners.Add(indices[i + k]);
                        polygonEnds.Add(corners.Count);
                        polygonSlots.Add(slot);
                    }
                }

                // Skin influences by vertex, all of them (not only the four Unity skins with).
                bool skinned = item.Bones != null && item.Bones.Length > 0;
                NativeArray<byte> perVertex = default;
                NativeArray<BoneWeight1> influences = default;
                int[] skinStart = null;
                if (skinned)
                {
                    perVertex = mesh.GetBonesPerVertex();
                    influences = mesh.GetAllBoneWeights();
                    if (perVertex.Length != n)
                    {
                        result.Warnings.Add(name + " has bones but no skin weights: written unskinned.");
                        skinned = false;
                    }
                    else
                    {
                        skinStart = new int[n + 1];
                        for (int v = 0; v < n; v++) skinStart[v + 1] = skinStart[v] + perVertex[v];
                        if (skinStart[n] > influences.Length) { result.Warnings.Add(name + "'s skin weights are incomplete: written unskinned."); skinned = false; }
                    }
                }

                // What the kept blendshapes move each vertex by, as one hash: vertices weld only where every shape moves them alike.
                var kept = Enumerable.Range(0, mesh.blendShapeCount).Where(i => !baked[i] && mesh.GetBlendShapeFrameCount(i) > 0).ToList();
                ulong[] shapeHash = kept.Count > 0 ? new ulong[n] : null;
                foreach (int shape in kept)
                    for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, f, dv, null, null);
                        ulong salt = Mix((ulong)shape << 16 | (uint)f);
                        for (int v = 0; v < n; v++)
                            if (dv[v].x != 0f || dv[v].y != 0f || dv[v].z != 0f) shapeHash[v] = Mix(shapeHash[v] ^ salt ^ Hash(dv[v]));
                    }

                // Control points: Unity's vertices welded where their place, weights and blendshape offsets agree.
                var pointOf = new int[n];
                for (int v = 0; v < n; v++) pointOf[v] = -1;
                var points = new List<int>();
                var chain = new List<int>();
                var buckets = new Dictionary<ulong, int>();
                foreach (int v in corners)
                {
                    if (pointOf[v] >= 0) continue;
                    ulong key = Hash(positions[v]);
                    if (skinned)
                        for (int k = skinStart[v]; k < skinStart[v + 1]; k++)
                            key = Mix(key ^ (uint)influences[k].boneIndex ^ (ulong)(uint)influences[k].weight.GetHashCode() << 32);
                    if (shapeHash != null) key = Mix(key ^ shapeHash[v]);
                    int point = -1;
                    int head = buckets.TryGetValue(key, out int first) ? first : -1;
                    for (int c = head; c >= 0 && point < 0; c = chain[c])
                        if (Same(points[c], v, positions, skinned, skinStart, influences, shapeHash)) point = c;
                    if (point < 0)
                    {
                        point = points.Count;
                        points.Add(v);
                        chain.Add(head);
                        buckets[key] = point;
                    }
                    pointOf[v] = point;
                }

                var geometry = Object("Geometry", Id(), name, "Geometry", "Mesh");
                long geometryId = (long)geometry.Properties[0];
                geometry.Add("Properties70");
                geometry.Add("GeometryVersion", 124);
                var vertices = new double[points.Count * 3];
                for (int c = 0; c < points.Count; c++)
                {
                    var p = positions[points[c]];
                    vertices[c * 3] = -(double)p.x;
                    vertices[c * 3 + 1] = p.y;
                    vertices[c * 3 + 2] = p.z;
                }
                geometry.Add("Vertices", vertices);
                var polygonVertices = new int[corners.Count];
                for (int i = 0, polygon = 0; i < corners.Count; i++)
                {
                    int point = pointOf[corners[i]];
                    bool last = i == polygonEnds[polygon] - 1;
                    polygonVertices[i] = last ? ~point : point;
                    if (last) polygon++;
                }
                geometry.Add("PolygonVertexIndex", polygonVertices);

                // Corner data, each an index into Unity's vertices.
                var cornerIndex = corners.ToArray();
                var layer0 = new List<string>();
                if (hasNormals)
                {
                    var values = new double[n * 3];
                    for (int v = 0; v < n; v++) { values[v * 3] = -(double)normals[v].x; values[v * 3 + 1] = normals[v].y; values[v * 3 + 2] = normals[v].z; }
                    var element = Element(geometry, "LayerElementNormal", 0, "", "ByPolygonVertex", "IndexToDirect");
                    element.Add("Normals", values);
                    element.Add("NormalsIndex", cornerIndex);
                    layer0.Add("LayerElementNormal");
                }
                if (colors != null)
                {
                    var values = new double[n * 4];
                    for (int v = 0; v < n; v++) { values[v * 4] = colors[v].r; values[v * 4 + 1] = colors[v].g; values[v * 4 + 2] = colors[v].b; values[v * 4 + 3] = colors[v].a; }
                    var element = Element(geometry, "LayerElementColor", 0, "Col", "ByPolygonVertex", "IndexToDirect");
                    element.Add("Colors", values);
                    element.Add("ColorIndex", cornerIndex);
                    layer0.Add("LayerElementColor");
                }
                for (int channel = 0; channel < uvs.Count; channel++)
                {
                    var values = new double[n * 2];
                    for (int v = 0; v < n; v++) { values[v * 2] = uvs[channel][v].x; values[v * 2 + 1] = uvs[channel][v].y; }
                    var element = Element(geometry, "LayerElementUV", channel, channel == 0 ? "UVMap" : "UVMap." + channel.ToString("000"), "ByPolygonVertex", "IndexToDirect");
                    element.Add("UV", values);
                    element.Add("UVIndex", cornerIndex);
                    if (channel == 0) layer0.Add("LayerElementUV");
                }
                var materialElement = Element(geometry, "LayerElementMaterial", 0, "", "ByPolygon", "IndexToDirect");
                materialElement.Add("Materials", polygonSlots.ToArray());
                layer0.Add("LayerElementMaterial");
                Layer(geometry, 0, layer0, 0);
                for (int channel = 1; channel < uvs.Count; channel++) Layer(geometry, channel, new List<string> { "LayerElementUV" }, channel);

                Connect(geometryId, model.Id);
                foreach (long material in slotMaterials) Connect(material, model.Id);

                if (skinned) Skin(item, name, model, geometryId, points, skinStart, influences);
                if (kept.Count > 0) BlendShapes(item, name, geometryId, kept, points, hasNormals, dv, dn);
            }

            private static bool Same(int a, int b, Vector3[] positions, bool skinned, int[] skinStart, NativeArray<BoneWeight1> influences, ulong[] shapeHash)
            {
                Vector3 pa = positions[a], pb = positions[b];
                if (pa.x != pb.x || pa.y != pb.y || pa.z != pb.z) return false;
                if (shapeHash != null && shapeHash[a] != shapeHash[b]) return false;
                if (!skinned) return true;
                int count = skinStart[a + 1] - skinStart[a];
                if (count != skinStart[b + 1] - skinStart[b]) return false;
                for (int k = 0; k < count; k++)
                {
                    BoneWeight1 wa = influences[skinStart[a] + k], wb = influences[skinStart[b] + k];
                    if (wa.boneIndex != wb.boneIndex || wa.weight != wb.weight) return false;
                }
                return true;
            }

            private static ulong Mix(ulong x)
            {
                x += 0x9e3779b97f4a7c15UL;
                x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
                x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
                return x ^ (x >> 31);
            }

            private static ulong Hash(Vector3 v) => Mix(Mix(Mix((uint)v.x.GetHashCode()) ^ (uint)v.y.GetHashCode()) ^ (uint)v.z.GetHashCode());

            // Every UV set up to the last one the mesh has (a gap filled with zeros keeps the later sets in their channels).
            private static List<Vector2[]> Uvs(Mesh mesh)
            {
                int last = -1;
                for (int channel = 0; channel < 8; channel++)
                    if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) last = channel;
                var sets = new List<Vector2[]>();
                var values = new List<Vector4>();
                for (int channel = 0; channel <= last; channel++)
                {
                    var set = new Vector2[mesh.vertexCount];
                    if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel))
                    {
                        mesh.GetUVs(channel, values);
                        for (int v = 0; v < set.Length && v < values.Count; v++) set[v] = new Vector2(values[v].x, values[v].y);
                    }
                    sets.Add(set);
                }
                return sets;
            }

            private static FbxNode Element(FbxNode geometry, string type, int index, string name, string mapping, string reference)
            {
                var element = geometry.Add(type, index);
                element.Add("Version", 101);
                element.Add("Name", name);
                element.Add("MappingInformationType", mapping);
                element.Add("ReferenceInformationType", reference);
                return element;
            }

            private static void Layer(FbxNode geometry, int index, List<string> types, int typedIndex)
            {
                var layer = geometry.Add("Layer", index);
                layer.Add("Version", 100);
                foreach (string type in types)
                {
                    var element = layer.Add("LayerElement");
                    element.Add("Type", type);
                    element.Add("TypedIndex", typedIndex);
                }
            }

            // ---- Skin ------------------------------------------------------------------------------------------------------

            // A cluster per bone and bind pose that weights a vertex; its link is where the bone was at bind time, given by
            // the mesh's bind pose from where the mesh is now, as importers derive the bind pose back.
            private void Skin(FbxExportMesh item, string name, Model model, long geometryId, List<int> points, int[] skinStart, NativeArray<BoneWeight1> influences)
            {
                var bindposes = item.Mesh.bindposes;
                var bones = item.Bones;
                if (bindposes.Length != bones.Length)
                    result.Warnings.Add(name + " has " + bones.Length + " bones for " + bindposes.Length + " bind poses: the extra ones are left out.");
                int boneCount = Math.Min(bones.Length, bindposes.Length);
                var clusters = new List<Cluster>();
                var clusterOf = new int[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                {
                    clusterOf[b] = -1;
                    if (b >= boneCount || bones[b] == null) continue;
                    int found = clusters.FindIndex(c => c.Bone == bones[b] && c.Bindpose == bindposes[b]);
                    if (found < 0)
                    {
                        found = clusters.Count;
                        clusters.Add(new Cluster { Bone = bones[b], Bindpose = bindposes[b] });
                    }
                    clusterOf[b] = found;
                }
                int lost = 0;
                for (int point = 0; point < points.Count; point++)
                {
                    int v = points[point];
                    bool lostHere = false;
                    for (int k = skinStart[v]; k < skinStart[v + 1]; k++)
                    {
                        var influence = influences[k];
                        if (influence.weight <= 0f) continue;
                        int c = influence.boneIndex >= 0 && influence.boneIndex < clusterOf.Length ? clusterOf[influence.boneIndex] : -1;
                        if (c < 0) { lostHere = true; continue; }
                        var cluster = clusters[c];
                        int last = cluster.Indexes.Count - 1;
                        if (last >= 0 && cluster.Indexes[last] == point) cluster.Weights[last] += influence.weight;
                        else
                        {
                            cluster.Indexes.Add(point);
                            cluster.Weights.Add(influence.weight);
                        }
                    }
                    if (lostHere) lost++;
                }
                if (lost > 0) result.Warnings.Add(name + ": " + lost + " vertices lose weights to bones that are missing.");
                var used = clusters.Where(c => c.Indexes.Count > 0).ToList();
                if (used.Count == 0)
                {
                    result.Warnings.Add(name + " has bones but no weights to them: written unskinned.");
                    return;
                }

                var skin = Object("Deformer", Id(), name, "Deformer", "Skin");
                long skinId = (long)skin.Properties[0];
                skin.Add("Version", 101);
                skin.Add("Link_DeformAcuracy", 50d);
                Connect(skinId, geometryId);

                var pose = Object("Pose", Id(), name, "Pose", "BindPose");
                pose.Add("Type", "BindPose");
                pose.Add("Version", 100);
                var poseCount = pose.Add("NbPoseNodes", 0);
                var posed = new HashSet<Transform>();
                void PoseNode(long node, Matrix4x4 global)
                {
                    var entry = pose.Add("PoseNode");
                    entry.Add("Node", node);
                    entry.Add("Matrix", FbxTransform.ToDoubles(global));
                }
                PoseNode(model.Id, model.Global);

                foreach (var cluster in used)
                {
                    var bind = Mirror * cluster.Bindpose * Mirror;
                    var link = model.Global * bind.inverse;
                    var node = Object("Deformer", Id(), cluster.Bone.name, "SubDeformer", "Cluster");
                    long clusterId = (long)node.Properties[0];
                    node.Add("Version", 100);
                    node.Add("UserData", "", "");
                    node.Add("Indexes", cluster.Indexes.ToArray());
                    node.Add("Weights", cluster.Weights.ToArray());
                    // In the bone's space, as FBX files keep it (the global transform of the mesh at bind time, seen from the bone).
                    node.Add("Transform", FbxTransform.ToDoubles(bind));
                    node.Add("TransformLink", FbxTransform.ToDoubles(link));
                    Connect(clusterId, skinId);
                    Connect(models[cluster.Bone].Id, clusterId);
                    if (posed.Add(cluster.Bone)) PoseNode(models[cluster.Bone].Id, link);
                }
                poseCount.Properties[0] = posed.Count + 1;
            }

            // ---- Blendshapes -----------------------------------------------------------------------------------------------

            // A channel per blendshape at the weight it shows, a shape per frame: the offsets of the control points it moves.
            private void BlendShapes(FbxExportMesh item, string name, long geometryId, List<int> kept, List<int> points, bool hasNormals, Vector3[] dv, Vector3[] dn)
            {
                var mesh = item.Mesh;
                var blend = Object("Deformer", Id(), name, "Deformer", "BlendShape");
                long blendId = (long)blend.Properties[0];
                blend.Add("Version", 100);
                Connect(blendId, geometryId);
                var indexes = new List<int>();
                var offsets = new List<double>();
                var normalOffsets = new List<double>();
                var inBetweens = new List<string>();
                foreach (int shape in kept)
                {
                    string shapeName = mesh.GetBlendShapeName(shape);
                    int frames = mesh.GetBlendShapeFrameCount(shape);
                    if (frames > 1) inBetweens.Add(shapeName);
                    var channel = Object("Deformer", Id(), shapeName, "SubDeformer", "BlendShapeChannel");
                    long channelId = (long)channel.Properties[0];
                    channel.Add("Version", 100);
                    channel.Add("DeformPercent", (double)item.Weight(shape));
                    var weights = new double[frames];
                    for (int f = 0; f < frames; f++) weights[f] = mesh.GetBlendShapeFrameWeight(shape, f);
                    channel.Add("FullWeights", weights);
                    Connect(channelId, blendId);
                    for (int f = 0; f < frames; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, f, dv, hasNormals ? dn : null, null);
                        indexes.Clear();
                        offsets.Clear();
                        normalOffsets.Clear();
                        for (int point = 0; point < points.Count; point++)
                        {
                            int v = points[point];
                            Vector3 d = dv[v], m = hasNormals ? dn[v] : Vector3.zero;
                            if (d.x == 0f && d.y == 0f && d.z == 0f && m.x == 0f && m.y == 0f && m.z == 0f) continue;
                            indexes.Add(point);
                            offsets.Add(-(double)d.x); offsets.Add(d.y); offsets.Add(d.z);
                            normalOffsets.Add(-(double)m.x); normalOffsets.Add(m.y); normalOffsets.Add(m.z);
                        }
                        // A shape that moves nothing still names one point, so importers keep it.
                        if (indexes.Count == 0)
                        {
                            indexes.Add(0);
                            offsets.AddRange(new[] { 0d, 0d, 0d });
                            normalOffsets.AddRange(new[] { 0d, 0d, 0d });
                        }
                        string frameName = f == frames - 1 ? shapeName : shapeName + " " + weights[f].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        var target = Object("Geometry", Id(), frameName, "Geometry", "Shape");
                        target.Add("Properties70");
                        target.Add("Version", 100);
                        target.Add("Indexes", indexes.ToArray());
                        target.Add("Vertices", offsets.ToArray());
                        if (hasNormals) target.Add("Normals", normalOffsets.ToArray());
                        Connect((long)target.Properties[0], channelId);
                    }
                    result.BlendShapes++;
                }
                if (inBetweens.Count > 0)
                    result.Warnings.Add(name + ": " + string.Join(", ", inBetweens) + " keep their in-between frames, which Unity reads but Blender's FBX importer refuses.");
            }

            // ---- Materials ---------------------------------------------------------------------------------------------------

            private long MaterialId(FbxExportMesh item, int submesh, string meshName)
            {
                var material = item.Materials != null && submesh < item.Materials.Length ? item.Materials[submesh] : null;
                if (material != null && materials.TryGetValue(material, out long known)) return known;
                string name = material != null ? material.name : meshName + "_" + submesh;
                string unique = name;
                for (int i = 2; !materialNames.Add(unique); i++) unique = name + " " + i;
                long id = Id();
                var node = Object("Material", id, unique, "Material", "");
                node.Add("Version", 102);
                node.Add("ShadingModel", "Phong");
                node.Add("MultiLayer", 0);
                var properties = node.Add("Properties70");
                var color = material != null && material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                P(properties, "DiffuseColor", "Color", "", "A", (double)color.r, (double)color.g, (double)color.b);
                P(properties, "DiffuseFactor", "Number", "", "A", 1d);
                P(properties, "Opacity", "Number", "", "A", (double)color.a);
                if (material == null) return id;
                materials[material] = id;
                Texture(material, id);
                return id;
            }

            // The material's main texture file, linked as its diffuse colour: by its full path and from the FBX's folder.
            private void Texture(Material material, long materialId)
            {
                Texture texture = null;
                foreach (string property in MainTextures)
                    if (texture == null && material.HasProperty(property)) texture = material.GetTexture(property);
                if (texture == null) return;
                if (!textures.TryGetValue(texture, out long textureId))
                {
                    string asset = AssetDatabase.GetAssetPath(texture);
                    if (string.IsNullOrEmpty(asset)) return;
                    string file = Path.GetFullPath(FileUtil.GetPhysicalPath(asset));
                    if (!File.Exists(file)) return;
                    string relative = Relative(file);
                    long videoId = Id();
                    var video = Object("Video", videoId, texture.name, "Video", "Clip");
                    video.Add("Type", "Clip");
                    var videoProperties = video.Add("Properties70");
                    P(videoProperties, "Path", "KString", "XRefUrl", "", file);
                    P(videoProperties, "RelPath", "KString", "XRefUrl", "", relative);
                    video.Add("UseMipMap", 0);
                    video.Add("Filename", file);
                    video.Add("RelativeFilename", relative);
                    textureId = Id();
                    var node = Object("Texture", textureId, texture.name, "Texture", "");
                    node.Add("Type", "TextureVideoClip");
                    node.Add("Version", 202);
                    node.Add("TextureName", texture.name + "\0\u0001Texture");
                    var properties = node.Add("Properties70");
                    P(properties, "UseMaterial", "bool", "", "", 1);
                    node.Add("Media", texture.name + "\0\u0001Video");
                    node.Add("FileName", file);
                    node.Add("RelativeFilename", relative);
                    node.Add("ModelUVTranslation", 0d, 0d);
                    node.Add("ModelUVScaling", 1d, 1d);
                    node.Add("Texture_Alpha_Source", "None");
                    node.Add("Cropping", 0, 0, 0, 0);
                    Connect(videoId, textureId);
                    textures[texture] = textureId;
                }
                connections.Add("C", "OP", textureId, materialId, "DiffuseColor");
            }

            private string Relative(string file)
            {
                try
                {
                    string from = folder.EndsWith(Path.DirectorySeparatorChar.ToString()) ? folder : folder + Path.DirectorySeparatorChar;
                    string relative = Uri.UnescapeDataString(new Uri(from).MakeRelativeUri(new Uri(file)).ToString());
                    return relative.Replace('/', Path.DirectorySeparatorChar);
                }
                catch (UriFormatException) { return Path.GetFileName(file); }
                catch (InvalidOperationException) { return Path.GetFileName(file); }
            }

            // ---- The file ----------------------------------------------------------------------------------------------------

            private FbxDocument Document()
            {
                var document = new FbxDocument { Version = 7400 };
                var now = DateTime.Now;
                const string creator = "Orbiters Toolkit FBX export";

                var header = new FbxNode("FBXHeaderExtension");
                header.Add("FBXHeaderVersion", 1003);
                header.Add("FBXVersion", 7400);
                header.Add("EncryptionType", 0);
                var stamp = header.Add("CreationTimeStamp");
                stamp.Add("Version", 1000);
                stamp.Add("Year", now.Year);
                stamp.Add("Month", now.Month);
                stamp.Add("Day", now.Day);
                stamp.Add("Hour", now.Hour);
                stamp.Add("Minute", now.Minute);
                stamp.Add("Second", now.Second);
                stamp.Add("Millisecond", now.Millisecond);
                header.Add("Creator", creator);
                var scene = header.Add("SceneInfo", "GlobalInfo\0\u0001SceneInfo", "UserData");
                scene.Add("Type", "UserData");
                scene.Add("Version", 100);
                var meta = scene.Add("MetaData");
                meta.Add("Version", 100);
                foreach (string field in new[] { "Title", "Subject", "Author", "Keywords", "Revision", "Comment" }) meta.Add(field, "");
                var info = scene.Add("Properties70");
                P(info, "Original", "Compound", "", "");
                P(info, "Original|ApplicationVendor", "KString", "", "", "Orbiters");
                P(info, "Original|ApplicationName", "KString", "", "", creator);
                P(info, "Original|ApplicationVersion", "KString", "", "", Application.unityVersion);
                P(info, "LastSaved", "Compound", "", "");
                P(info, "LastSaved|ApplicationVendor", "KString", "", "", "Orbiters");
                P(info, "LastSaved|ApplicationName", "KString", "", "", creator);
                P(info, "LastSaved|ApplicationVersion", "KString", "", "", Application.unityVersion);
                document.Nodes.Add(header);
                // The file id and creation time Blender writes: they go with the footer FbxDocument writes.
                document.Nodes.Add(new FbxNode("FileId", new byte[] { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 }));
                document.Nodes.Add(new FbxNode("CreationTime", "1970-01-01 10:00:00:000"));
                document.Nodes.Add(new FbxNode("Creator", creator));

                var global = new FbxNode("GlobalSettings");
                global.Add("Version", 1000);
                var settings = global.Add("Properties70");
                P(settings, "UpAxis", "int", "Integer", "", 1);
                P(settings, "UpAxisSign", "int", "Integer", "", 1);
                P(settings, "FrontAxis", "int", "Integer", "", 2);
                P(settings, "FrontAxisSign", "int", "Integer", "", 1);
                P(settings, "CoordAxis", "int", "Integer", "", 0);
                P(settings, "CoordAxisSign", "int", "Integer", "", 1);
                P(settings, "OriginalUpAxis", "int", "Integer", "", 1);
                P(settings, "OriginalUpAxisSign", "int", "Integer", "", 1);
                // Centimetres per unit: the file is in metres.
                P(settings, "UnitScaleFactor", "double", "Number", "", 100d);
                P(settings, "OriginalUnitScaleFactor", "double", "Number", "", 100d);
                P(settings, "AmbientColor", "ColorRGB", "Color", "", 0d, 0d, 0d);
                P(settings, "DefaultCamera", "KString", "", "", "Producer Perspective");
                P(settings, "TimeMode", "enum", "", "", 11);
                P(settings, "TimeSpanStart", "KTime", "Time", "", 0L);
                P(settings, "TimeSpanStop", "KTime", "Time", "", 46186158000L);
                P(settings, "CustomFrameRate", "double", "Number", "", 24d);
                document.Nodes.Add(global);
                counts["GlobalSettings"] = 1;

                var documents = new FbxNode("Documents");
                documents.Add("Count", 1);
                var sceneDocument = documents.Add("Document", Id(), "Scene", "Scene");
                var sceneProperties = sceneDocument.Add("Properties70");
                P(sceneProperties, "SourceObject", "object", "", "");
                P(sceneProperties, "ActiveAnimStackName", "KString", "", "", "");
                sceneDocument.Add("RootNode", 0L);
                document.Nodes.Add(documents);
                document.Nodes.Add(new FbxNode("References"));

                var definitions = new FbxNode("Definitions");
                definitions.Add("Version", 100);
                definitions.Add("Count", counts.Values.Sum());
                foreach (string type in TypeOrder)
                {
                    if (!counts.TryGetValue(type, out int count)) continue;
                    definitions.Add("ObjectType", type).Add("Count", count);
                }
                document.Nodes.Add(definitions);
                document.Nodes.Add(objects);
                document.Nodes.Add(connections);
                var takes = new FbxNode("Takes");
                takes.Add("Current", "");
                document.Nodes.Add(takes);
                return document;
            }
        }
    }
}
