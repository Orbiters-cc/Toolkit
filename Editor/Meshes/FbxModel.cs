using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// A binary FBX's scene built straight from its records, placed as Unity's model importer places it, without importing
    /// an asset (seconds where the import of a body with hundreds of blendshapes takes half a minute). Under a hidden root
    /// that is never saved: a GameObject per model; a SkinnedMeshRenderer per skinned or blendshaped mesh (a MeshFilter and
    /// MeshRenderer otherwise) with the file's normals, UVs, colours, skin weights (the four strongest per vertex, as the
    /// importer keeps by default) and bind poses; blendshapes with their position offsets only (no normal offsets).
    /// Tangents are calculated. Material slots follow the polygons' first use of each material, as the importer orders them.
    /// Made for what Blender's FBX exporter writes: polygons are split in fans.
    /// <see cref="Release"/> destroys it, with its meshes that were not saved into an asset since.
    /// </summary>
    public static class FbxModel
    {
        private const float MinBoneWeight = 0.001f;

        public sealed class Options
        {
            /// <summary>Whether a mesh (by its model's name) keeps a blendshape; every one when null.</summary>
            public Func<string, string, bool> IncludeShape;
            /// <summary>Normal and tangent offsets of a blendshape frame (mesh, shape, frame index), by vertex, or null.</summary>
            public Func<string, string, int, FrameNormals> Normals;
        }

        public sealed class FrameNormals
        {
            public Vector3[] Normals;
            public Vector3[] Tangents;
        }

        public static GameObject Load(string path, Options options = null) =>
            Load(FbxDocument.Read(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path), options);

        /// <summary>
        /// Builds the document's scene. Loading the same document again gives the same vertices in the same order (a first
        /// load with a few blendshapes can measure what a second one with all of them takes in: <see cref="Options.Normals"/>).
        /// </summary>
        public static GameObject Load(FbxDocument document, string name, Options options = null)
        {
            var scene = new Scene(document, options ?? new Options());
            var root = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
            try
            {
                scene.Build(root.transform);
                return root;
            }
            catch
            {
                Release(root);
                throw;
            }
        }

        /// <summary>Destroys a loaded model and its meshes that no asset holds.</summary>
        public static void Release(GameObject root)
        {
            if (root == null) return;
            var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh)
                .Concat(root.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh));
            foreach (var mesh in meshes.Where(m => m != null && !EditorUtility.IsPersistent(m)).Distinct().ToList()) Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(root);
        }

        private sealed class Scene
        {
            private readonly Dictionary<long, FbxNode> objects;
            private readonly Dictionary<long, List<long>> children = new Dictionary<long, List<long>>();
            private readonly Matrix4x4 toUnity, toFbx;
            private readonly Dictionary<long, Transform> transforms = new Dictionary<long, Transform>();
            private readonly Dictionary<long, Matrix4x4> globals = new Dictionary<long, Matrix4x4>();
            private readonly Dictionary<long, long> parents = new Dictionary<long, long>();
            private readonly Options options;

            public Scene(FbxDocument document, Options options)
            {
                this.options = options;
                objects = document.Objects();
                foreach (var (child, parent, _) in document.Connections())
                {
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<long>();
                    list.Add(child);
                }
                toUnity = document.ToUnity();
                toFbx = toUnity.inverse;
            }

            // The records connected under a parent, in the file's order.
            private IEnumerable<FbxNode> Of(long parent, string type, string kind = null) =>
                children.TryGetValue(parent, out var list)
                    ? list.Where(objects.ContainsKey).Select(id => objects[id]).Where(n => n.Name == type && (kind == null || n.Class == kind))
                    : Enumerable.Empty<FbxNode>();

            public void Build(Transform root)
            {
                AddModels(0, root, Matrix4x4.identity);
                foreach (var pair in transforms.ToList())
                {
                    var model = objects[pair.Key];
                    if (model.Class != "Mesh") continue;
                    var geometry = Of(model.Id, "Geometry", "Mesh").FirstOrDefault();
                    if (geometry != null) AddMesh(model, geometry, pair.Value.gameObject);
                }
            }

            // The models below a parent, by name as the importer sorts them, each placed in its parent's space.
            private void AddModels(long parentId, Transform parent, Matrix4x4 parentGlobal)
            {
                foreach (var model in Of(parentId, "Model").OrderBy(m => m.ObjectName, StringComparer.Ordinal))
                {
                    var local = FbxTransform.Local(model);
                    var go = EditorUtility.CreateGameObjectWithHideFlags(model.ObjectName, HideFlags.HideAndDontSave);
                    go.transform.SetParent(parent, false);
                    SetLocal(go.transform, toUnity * local * toFbx);
                    transforms[model.Id] = go.transform;
                    globals[model.Id] = parentGlobal * local;
                    parents[model.Id] = parentId;
                    AddModels(model.Id, go.transform, globals[model.Id]);
                }
            }

            private static void SetLocal(Transform transform, Matrix4x4 m)
            {
                Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
                var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
                if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) scale.x = -scale.x;
                transform.localPosition = m.GetColumn(3);
                transform.localRotation = scale.y > 0f && scale.z > 0f ? Quaternion.LookRotation(z / scale.z, y / scale.y) : Quaternion.identity;
                transform.localScale = scale;
            }

            private void AddMesh(FbxNode model, FbxNode geometry, GameObject owner)
            {
                // Points from the geometry's space into the model's, in Unity's axes; a mirror between them turns windings.
                var points = toUnity * FbxTransform.Geometric(model);
                var normalMatrix = points.inverse.transpose;
                bool mirrored = points.determinant < 0f;

                double[] controlPoints = Doubles(geometry, "Vertices") ?? throw new InvalidDataException($"{model.ObjectName} has no vertices.");
                int[] polygonVertices = geometry.Value<int[]>("PolygonVertexIndex") ?? throw new InvalidDataException($"{model.ObjectName} has no polygons.");
                int pointCount = controlPoints.Length / 3;
                var normals = Element.Read(geometry.All("LayerElementNormal").FirstOrDefault(), "Normals", "NormalsIndex", 3);
                var uvLayers = geometry.All("LayerElementUV").OrderBy(e => e.Properties.Count > 0 ? Convert.ToInt32(e.Properties[0]) : 0)
                    .Select(e => Element.Read(e, "UV", "UVIndex", 2)).Where(e => e != null).Take(8).ToList();
                var colors = Element.Read(geometry.All("LayerElementColor").FirstOrDefault(), "Colors", "ColorIndex", 4);
                var materialElement = geometry.All("LayerElementMaterial").FirstOrDefault();
                int[] materialIndexes = materialElement?.Value<int[]>("Materials");
                bool materialPerPolygon = materialElement?.Value<string>("MappingInformationType") == "ByPolygon" && materialIndexes != null;

                // Unity's vertices: a control point split where its corners' normal, UVs or colour differ.
                var vertexPoint = new List<int>();
                var vertexNormals = new List<Vector3>();
                var vertexUvs = uvLayers.Select(_ => new List<Vector2>()).ToList();
                var vertexColors = new List<Color>();
                var pointVertices = new List<int>[pointCount];
                // Material slots in the order the polygons first use them, as the importer orders them (not the file's).
                var slotOf = new Dictionary<int, int>();
                var slotTriangles = new List<List<int>>();
                var polygon = new List<int>();
                int polygonIndex = 0, cornerStart = 0;
                for (int corner = 0; corner < polygonVertices.Length; corner++)
                {
                    int raw = polygonVertices[corner];
                    int point = raw < 0 ? ~raw : raw;
                    if (point >= pointCount) throw new InvalidDataException($"{model.ObjectName} has a polygon corner past its vertices.");
                    var normal = normals != null ? normalMatrix.MultiplyVector(normals.Vec3(corner, point, polygonIndex)).normalized : Vector3.zero;
                    var color = colors?.Rgba(corner, point, polygonIndex) ?? Color.white;
                    int vertex = -1;
                    var candidates = pointVertices[point] ?? (pointVertices[point] = new List<int>(2));
                    foreach (int candidate in candidates)
                    {
                        if (vertexNormals[candidate] != normal || (colors != null && vertexColors[candidate] != color)) continue;
                        bool same = true;
                        for (int layer = 0; layer < uvLayers.Count && same; layer++)
                            same = vertexUvs[layer][candidate] == uvLayers[layer].Vec2(corner, point, polygonIndex);
                        if (same) { vertex = candidate; break; }
                    }
                    if (vertex < 0)
                    {
                        vertex = vertexPoint.Count;
                        vertexPoint.Add(point);
                        vertexNormals.Add(normal);
                        vertexColors.Add(color);
                        for (int layer = 0; layer < uvLayers.Count; layer++) vertexUvs[layer].Add(uvLayers[layer].Vec2(corner, point, polygonIndex));
                        candidates.Add(vertex);
                    }
                    polygon.Add(vertex);
                    if (raw >= 0) continue;

                    int material = materialIndexes == null || materialIndexes.Length == 0 ? 0
                        : materialIndexes[materialPerPolygon && polygonIndex < materialIndexes.Length ? polygonIndex : 0];
                    if (!slotOf.TryGetValue(material, out int slot))
                    {
                        slotOf[material] = slot = slotTriangles.Count;
                        slotTriangles.Add(new List<int>());
                    }
                    var triangles = slotTriangles[slot];
                    for (int i = 1; i + 1 < polygon.Count; i++)
                    {
                        triangles.Add(polygon[0]);
                        triangles.Add(polygon[mirrored ? i + 1 : i]);
                        triangles.Add(polygon[mirrored ? i : i + 1]);
                    }
                    polygon.Clear();
                    polygonIndex++;
                    cornerStart = corner + 1;
                }
                if (cornerStart != polygonVertices.Length) throw new InvalidDataException($"{model.ObjectName} ends in an unfinished polygon.");

                var mesh = new Mesh { name = model.ObjectName };
                int vertexCount = vertexPoint.Count;
                if (vertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;
                var positions = new Vector3[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    int p = vertexPoint[v] * 3;
                    positions[v] = points.MultiplyPoint3x4(new Vector3((float)controlPoints[p], (float)controlPoints[p + 1], (float)controlPoints[p + 2]));
                }
                mesh.vertices = positions;
                if (normals != null) mesh.SetNormals(vertexNormals);
                for (int layer = 0; layer < uvLayers.Count; layer++) mesh.SetUVs(layer, vertexUvs[layer]);
                if (colors != null) mesh.SetColors(vertexColors);
                int slots = Math.Max(1, slotTriangles.Count);
                mesh.subMeshCount = slots;
                for (int slot = 0; slot < slots; slot++) mesh.SetTriangles(slot < slotTriangles.Count ? slotTriangles[slot] : new List<int>(), slot, false);
                if (normals == null) mesh.RecalculateNormals();
                if (uvLayers.Count > 0) mesh.RecalculateTangents();

                var skin = Of(geometry.Id, "Deformer", "Skin").FirstOrDefault();
                Transform[] bones = skin != null ? Skin(mesh, model, skin, vertexPoint, pointCount) : null;
                var blend = Of(geometry.Id, "Deformer", "BlendShape").FirstOrDefault();
                if (blend != null) BlendShapes(mesh, model.ObjectName, blend, points, vertexPoint, pointVertices);
                mesh.RecalculateBounds();

                if (bones != null || mesh.blendShapeCount > 0)
                {
                    var renderer = owner.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.sharedMaterials = new Material[slots];
                    if (bones != null)
                    {
                        renderer.bones = bones;
                        renderer.rootBone = RootBone(bones);
                    }
                    renderer.localBounds = mesh.bounds;
                }
                else
                {
                    owner.AddComponent<MeshFilter>().sharedMesh = mesh;
                    owner.AddComponent<MeshRenderer>().sharedMaterials = new Material[slots];
                }
            }

            // A bone per cluster (in the file's order), bound where its link says; each vertex keeps its point's four
            // strongest weights over the importer's minimum, normalised.
            private Transform[] Skin(Mesh mesh, FbxNode model, FbxNode skin, List<int> vertexPoint, int pointCount)
            {
                var clusters = Of(skin.Id, "Deformer", "Cluster").ToList();
                var bones = new List<Transform>();
                var bindposes = new List<Matrix4x4>();
                var influences = new List<(int bone, float weight)>[pointCount];
                var meshGlobal = globals[model.Id];
                foreach (var cluster in clusters)
                {
                    var boneModel = Of(cluster.Id, "Model").FirstOrDefault();
                    if (boneModel == null || !transforms.TryGetValue(boneModel.Id, out var bone)) continue;
                    var link = cluster.Child("TransformLink") != null ? FbxTransform.FromDoubles(Doubles(cluster, "TransformLink")) : globals[boneModel.Id];
                    int index = bones.Count;
                    bones.Add(bone);
                    bindposes.Add(toUnity * link.inverse * meshGlobal * toFbx);
                    int[] indexes = cluster.Value<int[]>("Indexes");
                    double[] weights = Doubles(cluster, "Weights");
                    if (indexes == null || weights == null) continue;
                    for (int i = 0; i < indexes.Length && i < weights.Length; i++)
                    {
                        int point = indexes[i];
                        if (point < 0 || point >= pointCount || weights[i] <= 0d) continue;
                        (influences[point] ?? (influences[point] = new List<(int, float)>(4))).Add((index, (float)weights[i]));
                    }
                }

                var boneWeights = new BoneWeight[vertexPoint.Count];
                var perPoint = new BoneWeight[pointCount];
                for (int p = 0; p < pointCount; p++)
                {
                    var list = influences[p];
                    if (list == null) continue;
                    var strongest = list.OrderByDescending(w => w.weight).Where(w => w.weight >= MinBoneWeight).Take(4).ToList();
                    float sum = strongest.Sum(w => w.weight);
                    if (sum <= 0f) continue;
                    var weight = new BoneWeight();
                    for (int i = 0; i < strongest.Count; i++)
                    {
                        float value = strongest[i].weight / sum;
                        switch (i)
                        {
                            case 0: weight.boneIndex0 = strongest[i].bone; weight.weight0 = value; break;
                            case 1: weight.boneIndex1 = strongest[i].bone; weight.weight1 = value; break;
                            case 2: weight.boneIndex2 = strongest[i].bone; weight.weight2 = value; break;
                            default: weight.boneIndex3 = strongest[i].bone; weight.weight3 = value; break;
                        }
                    }
                    perPoint[p] = weight;
                }
                for (int v = 0; v < boneWeights.Length; v++) boneWeights[v] = perPoint[vertexPoint[v]];
                mesh.boneWeights = boneWeights;
                mesh.bindposes = bindposes.ToArray();
                return bones.ToArray();
            }

            // A blendshape per channel (in the file's order), a frame per shape at its full weight; offsets by point, given
            // to every vertex of the point. Each frame is added once: every addition copies all the frames added before.
            private void BlendShapes(Mesh mesh, string meshName, FbxNode blend, Matrix4x4 points, List<int> vertexPoint, List<int>[] pointVertices)
            {
                var deltas = new Vector3[vertexPoint.Count];
                var touched = new List<int>();
                foreach (var channel in Of(blend.Id, "Deformer", "BlendShapeChannel"))
                {
                    string name = channel.ObjectName;
                    if (mesh.GetBlendShapeIndex(name) >= 0 || options.IncludeShape != null && !options.IncludeShape(meshName, name)) continue;
                    var shapes = Of(channel.Id, "Geometry", "Shape").ToList();
                    double[] fullWeights = Doubles(channel, "FullWeights");
                    var frames = shapes.Select((shape, i) => (shape, weight: fullWeights != null && fullWeights.Length == shapes.Count ? (float)fullWeights[i] : 100f * (i + 1) / shapes.Count))
                        .OrderBy(f => f.weight).ToList();
                    if (frames.Count == 0) frames.Add((null, 100f));
                    float last = float.NegativeInfinity;
                    int frameIndex = 0;
                    foreach (var (shape, weight) in frames)
                    {
                        if (weight <= last) continue;
                        last = weight;
                        var normals = options.Normals?.Invoke(meshName, name, frameIndex++);
                        foreach (int v in touched) deltas[v] = Vector3.zero;
                        touched.Clear();
                        int[] indexes = shape?.Value<int[]>("Indexes");
                        double[] offsets = shape != null ? Doubles(shape, "Vertices") : null;
                        if (indexes != null && offsets != null)
                        {
                            for (int i = 0; i < indexes.Length && i * 3 + 2 < offsets.Length; i++)
                            {
                                int point = indexes[i];
                                if (point < 0 || point >= pointVertices.Length || pointVertices[point] == null) continue;
                                var delta = points.MultiplyVector(new Vector3((float)offsets[i * 3], (float)offsets[i * 3 + 1], (float)offsets[i * 3 + 2]));
                                foreach (int v in pointVertices[point]) { deltas[v] = delta; touched.Add(v); }
                            }
                        }
                        bool fits = normals?.Normals != null && normals.Normals.Length == deltas.Length && normals.Tangents?.Length == deltas.Length;
                        mesh.AddBlendShapeFrame(name, weight, deltas, fits ? normals.Normals : null, fits ? normals.Tangents : null);
                    }
                }
            }

            // The bone every other one is below (or the deepest model above them all).
            private Transform RootBone(Transform[] bones)
            {
                var chains = bones.Select(b => Ancestors(b).Reverse().ToList()).ToList();
                Transform common = null;
                for (int depth = 0; ; depth++)
                {
                    if (chains.Any(c => c.Count <= depth)) break;
                    var at = chains[0][depth];
                    if (chains.Any(c => c[depth] != at)) break;
                    common = at;
                }
                return common;
            }

            private static IEnumerable<Transform> Ancestors(Transform t)
            {
                for (; t != null; t = t.parent) yield return t;
            }

            private static double[] Doubles(FbxNode node, string child)
            {
                var value = node.Child(child)?.Properties.FirstOrDefault();
                switch (value)
                {
                    case double[] doubles: return doubles;
                    case float[] floats: return floats.Select(f => (double)f).ToArray();
                    case int[] ints: return ints.Select(i => (double)i).ToArray();
                    case long[] longs: return longs.Select(l => (double)l).ToArray();
                    default: return null;
                }
            }
        }

        // A layer element (normals, UVs, colours): its values by corner, control point or polygon, direct or indexed.
        private sealed class Element
        {
            private double[] values;
            private int[] index;
            private string mapping;
            private int width;

            public static Element Read(FbxNode node, string valuesName, string indexName, int width)
            {
                if (node == null) return null;
                var values = node.Child(valuesName)?.Properties.FirstOrDefault();
                double[] doubles = values as double[] ?? (values as float[])?.Select(f => (double)f).ToArray();
                if (doubles == null) return null;
                bool indexed = node.Value<string>("ReferenceInformationType") == "IndexToDirect" || node.Value<string>("ReferenceInformationType") == "Index";
                return new Element
                {
                    values = doubles, width = width, mapping = node.Value<string>("MappingInformationType") ?? "ByPolygonVertex",
                    index = indexed ? node.Value<int[]>(indexName) : null,
                };
            }

            private int Offset(int corner, int point, int polygon)
            {
                int i;
                switch (mapping)
                {
                    case "ByPolygonVertex": i = corner; break;
                    case "ByPolygon": i = polygon; break;
                    case "AllSame": i = 0; break;
                    default: i = point; break;
                }
                if (index != null) i = i < index.Length ? index[i] : -1;
                return i >= 0 && (i + 1) * width <= values.Length ? i * width : -1;
            }

            public Vector3 Vec3(int corner, int point, int polygon)
            {
                int o = Offset(corner, point, polygon);
                return o < 0 ? Vector3.zero : new Vector3((float)values[o], (float)values[o + 1], (float)values[o + 2]);
            }

            public Vector2 Vec2(int corner, int point, int polygon)
            {
                int o = Offset(corner, point, polygon);
                return o < 0 ? Vector2.zero : new Vector2((float)values[o], (float)values[o + 1]);
            }

            public Color Rgba(int corner, int point, int polygon)
            {
                int o = Offset(corner, point, polygon);
                return o < 0 ? Color.white : new Color((float)values[o], (float)values[o + 1], (float)values[o + 2], (float)values[o + 3]);
            }
        }
    }
}
