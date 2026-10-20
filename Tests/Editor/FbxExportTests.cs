using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// FbxExport on a skinned box: an armature away from the origin and turned, a pose that is not the bind pose, an unweighted
// end bone, two materials, two UV sets, colours, a hard-edged box split at every corner, a blendshape with normal offsets
// and one with an in-between frame. Read back by FbxModel and by Unity's model importer, vertices matched by position,
// normal and UVs, then everything else compared through that match.
public sealed class FbxExportTests
{
    private sealed class Rig : IDisposable
    {
        public GameObject Root;
        public SkinnedMeshRenderer Renderer;
        public readonly List<Object> Owned = new List<Object>();

        public Transform Find(string path) => Root.transform.Find(path);

        public void Dispose()
        {
            if (Root != null) Object.DestroyImmediate(Root);
            foreach (var owned in Owned) if (owned != null) Object.DestroyImmediate(owned);
        }
    }

    [Test]
    public void AWrittenSkinnedMeshReadsBackAsItWas()
    {
        string path = TempFile();
        using (var rig = MakeRig())
        {
            GameObject loaded = null;
            try
            {
                var result = FbxExport.Write(path, new[] { FbxExportMesh.From(rig.Renderer) });
                Assert.That(result.Error, Is.Null, "Written.");
                Assert.That(result.Warnings.Count, Is.EqualTo(1), "Only the in-between frame is worth a word: " + string.Join(" / ", result.Warnings));
                StringAssert.Contains("Wide", result.Warnings[0]);
                Assert.That(result.Meshes, Is.EqualTo(1));
                Assert.That(result.BlendShapes, Is.EqualTo(2));

                var document = FbxDocument.Read(File.ReadAllBytes(path));
                Assert.That(Setting(document, "UnitScaleFactor"), Is.EqualTo(100d), "Metres.");
                Assert.That(Setting(document, "UpAxis"), Is.EqualTo(1d), "Y up.");
                var objects = document.Objects().Values.ToList();
                Assert.That(objects.Where(o => o.Name == "Model").ToDictionary(o => o.ObjectName, o => o.Class),
                    Is.EquivalentTo(new Dictionary<string, string>
                    {
                        { "Armature", "Null" }, { "Hips", "LimbNode" }, { "Spine", "LimbNode" }, { "LeftUpperLeg", "LimbNode" },
                        { "Spine_end", "LimbNode" }, { "Cloth", "Mesh" },
                    }), "The renderer, its bones and the objects above them, below the export root.");
                Assert.That(objects.Where(o => o.Name == "Material").Select(o => o.ObjectName), Is.EquivalentTo(new[] { "Skin", "Cloth" }));
                Assert.That(objects.Count(o => o.Name == "Deformer" && o.Class == "Cluster"), Is.EqualTo(3), "A cluster per weighted bone.");
                var geometry = objects.Single(o => o.Name == "Geometry" && o.Class == "Mesh");
                Assert.That(geometry.Value<double[]>("Vertices").Length, Is.EqualTo(8 * 3), "The box's split corners are welded again.");

                loaded = FbxModel.Load(path);
                var mine = loaded.GetComponentsInChildren<Transform>(true).ToDictionary(t => PathOf(loaded.transform, t));
                foreach (string name in new[] { "Armature", "Armature/Hips", "Armature/Hips/Spine", "Armature/Hips/Spine/Spine_end", "Armature/Hips/LeftUpperLeg", "Cloth" })
                {
                    Assert.That(mine.ContainsKey(name), Is.True, "Has " + name);
                    var theirs = rig.Find(name);
                    Assert.That(Vector3.Distance(mine[name].localPosition, theirs.localPosition), Is.LessThan(1e-5f), name + " position");
                    Assert.That(Quaternion.Angle(mine[name].localRotation, theirs.localRotation), Is.LessThan(0.01f), name + " rotation");
                    Assert.That(Vector3.Distance(mine[name].localScale, theirs.localScale), Is.LessThan(1e-5f), name + " scale");
                }
                var renderer = mine["Cloth"].GetComponent<SkinnedMeshRenderer>();
                Assert.That(renderer, Is.Not.Null, "Skinned.");
                Compare(rig, renderer, loaded.transform, blendShapeNormals: false, weightTolerance: 1e-5f);
            }
            finally
            {
                FbxModel.Release(loaded);
                File.Delete(path);
            }
        }
    }

    [Test]
    public void UnitysImporterReadsAWrittenSkinnedMesh()
    {
        string folder = "Assets/__OrbitersFbxExportTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        try
        {
            using (var rig = MakeRig())
            {
                string asset = folder + "/Cloth.fbx";
                var result = FbxExport.Write(Path.GetFullPath(asset), new[] { FbxExportMesh.From(rig.Renderer) });
                Assert.That(result.Error, Is.Null, "Written.");
                AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(asset) as ModelImporter;
                Assert.That(importer, Is.Not.Null, "Imported as a model.");
                importer.isReadable = true;
                importer.SaveAndReimport();
                var imported = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                Assert.That(imported, Is.Not.Null);

                var theirs = imported.GetComponentsInChildren<Transform>(true).ToDictionary(t => PathOf(imported.transform, t));
                foreach (string name in new[] { "Armature", "Armature/Hips", "Armature/Hips/Spine", "Armature/Hips/LeftUpperLeg", "Cloth" })
                {
                    Assert.That(theirs.ContainsKey(name), Is.True, "Has " + name);
                    var original = rig.Find(name);
                    Assert.That(Vector3.Distance(theirs[name].localPosition, original.localPosition), Is.LessThan(1e-4f), name + " position");
                    Assert.That(Quaternion.Angle(theirs[name].localRotation, original.localRotation), Is.LessThan(0.01f), name + " rotation");
                    Assert.That(Vector3.Distance(theirs[name].localScale, original.localScale), Is.LessThan(1e-4f), name + " scale");
                }
                var renderer = theirs["Cloth"].GetComponent<SkinnedMeshRenderer>();
                Assert.That(renderer, Is.Not.Null, "Skinned.");
                var mesh = renderer.sharedMesh;
                var source = rig.Renderer.sharedMesh;
                // Unity's importer grows a skinned mesh's bounds to hold its blendshapes: compare where the vertices are.
                var extents = Extents(source.vertices);
                var importedExtents = Extents(mesh.vertices);
                Assert.That(Vector3.Distance(importedExtents.min, extents.min), Is.LessThan(1e-4f), "Lowest corner");
                Assert.That(Vector3.Distance(importedExtents.max, extents.max), Is.LessThan(1e-4f), "Highest corner");
                if (renderer.sharedMaterials.All(m => m != null))
                    Assert.That(renderer.sharedMaterials.Select(m => m.name), Is.EqualTo(new[] { "Skin", "Cloth" }), "Materials by slot");
                Compare(rig, renderer, imported.transform, blendShapeNormals: false, weightTolerance: 2e-3f);
            }
        }
        finally
        {
            AssetDatabase.DeleteAsset(folder);
        }
    }

    [Test]
    public void ARefitIsExportedAsItLooksFitted()
    {
        string path = TempFile();
        using (var rig = MakeRig())
        {
            GameObject loaded = null;
            try
            {
                var original = rig.Renderer.sharedMesh;
                var fitted = Object.Instantiate(original);
                fitted.name = "Cloth_ReFit";
                rig.Owned.Add(fitted);
                int n = fitted.vertexCount;
                var vertices = original.vertices;
                var fit = vertices.Select(v => new Vector3(v.x * 0.05f, 0.002f, v.z * 0.05f)).ToArray();
                var follow = vertices.Select(v => new Vector3(0f, v.y * 0.1f, 0f)).ToArray();
                fitted.AddBlendShapeFrame("refit", 100f, fit, null, null);
                fitted.AddBlendShapeFrame("refit_Smile", 100f, follow, null, null);
                rig.Renderer.sharedMesh = fitted;
                rig.Renderer.SetBlendShapeWeight(fitted.GetBlendShapeIndex("refit"), 100f);
                rig.Renderer.SetBlendShapeWeight(fitted.GetBlendShapeIndex("refit_Smile"), 40f);
                var record = rig.Renderer.gameObject.AddComponent<OrbitersRefit>();
                record.mesh = fitted;
                record.kind = OrbitersRefit.FitKind.Fitted;
                record.original = new RefitRendererState { mesh = original };
                record.shapes = new List<RefitShape> { new RefitShape("Smile", "refit_Smile") };

                Assert.That(RefitFbxExport.FitShape(record), Is.EqualTo("refit"), "The shape new to the mesh that follows no body shape.");
                var result = RefitFbxExport.Write(path, new[] { rig.Renderer });
                Assert.That(result.Error, Is.Null, "Written.");
                Assert.That(rig.Renderer.sharedMesh, Is.SameAs(fitted), "The renderer is left as it was.");

                loaded = FbxModel.Load(path);
                var renderer = loaded.transform.Find("Cloth").GetComponent<SkinnedMeshRenderer>();
                var mesh = renderer.sharedMesh;
                Assert.That(Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName), Is.EqualTo(new[] { "Smile", "Wide", "refit_Smile" }),
                    "The fit is no blendshape any more; the others stay.");
                var mine = mesh.vertices;
                var normals = original.normals;
                var myNormals = mesh.normals;
                var uv = original.uv;
                var myUv = mesh.uv;
                int follows = mesh.GetBlendShapeIndex("refit_Smile");
                var myFollow = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(follows, 0, myFollow, null, null);
                for (int i = 0; i < n; i++)
                {
                    var expected = vertices[i] + fit[i];
                    int j = Enumerable.Range(0, mine.Length).Where(k => (mine[k] - expected).sqrMagnitude < 1e-10f &&
                        Vector3.Angle(myNormals[k], normals[i]) < 0.05f && (myUv[k] - uv[i]).sqrMagnitude < 1e-10f).DefaultIfEmpty(-1).First();
                    Assert.That(j, Is.GreaterThanOrEqualTo(0), $"Vertex {i} sits fitted at {expected:F5}.");
                    Assert.That(Vector3.Distance(myFollow[j], follow[i]), Is.LessThan(1e-5f), $"refit_Smile on vertex {i}");
                }
            }
            finally
            {
                FbxModel.Release(loaded);
                File.Delete(path);
            }
        }
    }

    // ---- The rig ------------------------------------------------------------------------------------------------------------

    private static Rig MakeRig()
    {
        var rig = new Rig { Root = EditorUtility.CreateGameObjectWithHideFlags("Rig", HideFlags.HideAndDontSave) };
        rig.Root.transform.SetPositionAndRotation(new Vector3(5f, 0f, 2f), Quaternion.Euler(0f, 20f, 0f));
        Transform Child(string name, Transform parent, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            return go.transform;
        }
        var armature = Child("Armature", rig.Root.transform, new Vector3(0.1f, 0.05f, -0.2f), new Vector3(0f, 30f, 0f), Vector3.one);
        var hips = Child("Hips", armature, new Vector3(0f, 0.9f, 0f), new Vector3(10f, 0f, 5f), Vector3.one);
        var spine = Child("Spine", hips, new Vector3(0f, 0.2f, 0.01f), new Vector3(-5f, 12f, 3f), Vector3.one);
        var end = Child("Spine_end", spine, new Vector3(0f, 0.15f, 0f), Vector3.zero, Vector3.one);
        var leg = Child("LeftUpperLeg", hips, new Vector3(-0.1f, -0.05f, 0f), new Vector3(170f, 4f, -3f), Vector3.one);
        var cloth = Child("Cloth", rig.Root.transform, new Vector3(0.02f, 0.9f, 0.03f), new Vector3(0f, 0f, 8f), new Vector3(1.1f, 0.9f, 1f));

        var mesh = MakeBox();
        rig.Owned.Add(mesh);
        var bones = new[] { hips, spine, leg, end };
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * cloth.localToWorldMatrix).ToArray();
        mesh.boneWeights = mesh.vertices.Select(v => v.y < 0.2f
            ? new BoneWeight { boneIndex0 = 2, weight0 = 0.6f, boneIndex1 = 0, weight1 = 0.4f }
            : new BoneWeight { boneIndex0 = 1, weight0 = 0.7f, boneIndex1 = 0, weight1 = 0.2f, boneIndex2 = 2, weight2 = 0.1f }).ToArray();
        var vertices = mesh.vertices;
        mesh.AddBlendShapeFrame("Smile", 100f,
            vertices.Select(v => v.y > 0.2f ? new Vector3(v.x * 0.1f, 0.02f, 0f) : Vector3.zero).ToArray(),
            vertices.Select(v => v.y > 0.2f ? new Vector3(0f, 0f, 0.05f) : Vector3.zero).ToArray(), null);
        mesh.AddBlendShapeFrame("Wide", 50f, vertices.Select(v => new Vector3(v.x * 0.5f, 0f, 0f)).ToArray(), null, null);
        mesh.AddBlendShapeFrame("Wide", 100f, vertices.Select(v => new Vector3(v.x * 1.2f, 0f, 0f)).ToArray(), null, null);

        var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        var materials = new[] { new Material(shader) { name = "Skin" }, new Material(shader) { name = "Cloth" } };
        rig.Owned.AddRange(materials);
        rig.Renderer = cloth.gameObject.AddComponent<SkinnedMeshRenderer>();
        rig.Renderer.sharedMesh = mesh;
        rig.Renderer.bones = bones;
        rig.Renderer.rootBone = hips;
        rig.Renderer.sharedMaterials = materials;
        rig.Renderer.SetBlendShapeWeight(0, 30f);
        // The pose shown is not the bind pose.
        spine.localRotation *= Quaternion.Euler(0f, 0f, 25f);
        return rig;
    }

    // A box with a hard edge at every corner: four vertices per side, the first three sides in one submesh, the others in a second.
    private static Mesh MakeBox()
    {
        var sides = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        var half = new Vector3(0.1f, 0.2f, 0.1f);
        var center = new Vector3(0f, 0.2f, 0f);
        var square = new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f) };
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var colors = new List<Color>();
        var submeshes = new[] { new List<int>(), new List<int>() };
        for (int side = 0; side < sides.Length; side++)
        {
            var normal = sides[side];
            var a = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
            var b = Vector3.Cross(normal, a);
            int start = vertices.Count;
            for (int k = 0; k < 4; k++)
            {
                var p = center + Vector3.Scale(normal + a * square[k].x + b * square[k].y, half);
                vertices.Add(p);
                normals.Add(normal);
                uv0.Add((square[k] + Vector2.one) * 0.5f);
                uv1.Add(new Vector2(p.x + p.z, p.y));
                colors.Add(new Color(side / 6f, k / 4f, 0.5f, 1f));
            }
            var triangles = submeshes[side < 3 ? 0 : 1];
            void Triangle(int i, int j, int k)
            {
                Vector3 p0 = vertices[start + i], p1 = vertices[start + j], p2 = vertices[start + k];
                // Unity's front faces turn clockwise: their cross product points out.
                bool outwards = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) > 0f;
                triangles.AddRange(outwards ? new[] { start + i, start + j, start + k } : new[] { start + i, start + k, start + j });
            }
            Triangle(0, 1, 2);
            Triangle(0, 2, 3);
        }
        var mesh = new Mesh { name = "Cloth" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetColors(colors);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(submeshes[0], 0);
        mesh.SetTriangles(submeshes[1], 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---- Comparing ----------------------------------------------------------------------------------------------------------

    private static void Compare(Rig rig, SkinnedMeshRenderer mine, Transform myRoot, bool blendShapeNormals, float weightTolerance)
    {
        var theirs = rig.Renderer;
        Mesh a = theirs.sharedMesh, b = mine.sharedMesh;
        Assert.That(b.subMeshCount, Is.EqualTo(a.subMeshCount), "Material slots");
        Assert.That(Enumerable.Range(0, b.blendShapeCount).Select(b.GetBlendShapeName),
            Is.EqualTo(Enumerable.Range(0, a.blendShapeCount).Select(a.GetBlendShapeName)), "Blendshapes");

        Vector3[] av = a.vertices, bv = b.vertices, an = a.normals, bn = b.normals;
        Vector2[] auv = a.uv, buv = b.uv, auv2 = a.uv2, buv2 = b.uv2;
        var match = new int[av.Length];
        for (int i = 0; i < av.Length; i++)
        {
            match[i] = -1;
            for (int j = 0; j < bv.Length && match[i] < 0; j++)
                if ((av[i] - bv[j]).sqrMagnitude < 1e-10f && Vector3.Angle(an[i], bn[j]) < 0.05f && (auv[i] - buv[j]).sqrMagnitude < 1e-10f &&
                    (auv2[i] - buv2[j]).sqrMagnitude < 1e-10f)
                    match[i] = j;
            Assert.That(match[i], Is.GreaterThanOrEqualTo(0), $"Vertex {i} at {av[i]:F5} normal {an[i]:F3} uv {auv[i]:F3} uv2 {auv2[i]:F3} has no match.");
        }

        // Where the skin puts each vertex in the pose shown, in the export root's space.
        var aSkinned = Skinned(theirs, rig.Root.transform);
        var bSkinned = Skinned(mine, myRoot);
        for (int i = 0; i < av.Length; i++)
            Assert.That(Vector3.Distance(aSkinned[i], bSkinned[match[i]]), Is.LessThan(1e-4f), $"Skinned vertex {i}");

        var aw = a.boneWeights;
        var bw = b.boneWeights;
        for (int i = 0; i < av.Length; i++)
        {
            var theirWeights = Weights(aw[i], theirs.bones);
            var myWeights = Weights(bw[match[i]], mine.bones);
            Assert.That(myWeights.Keys.OrderBy(k => k), Is.EqualTo(theirWeights.Keys.OrderBy(k => k)), $"Bones of vertex {i}");
            foreach (var pair in theirWeights) Assert.That(myWeights[pair.Key], Is.EqualTo(pair.Value).Within(weightTolerance), $"Weight of {pair.Key} on vertex {i}");
        }
        for (int i = 0; i < theirs.bones.Length; i++)
        {
            if (!aw.Any(w => Uses(w, i))) continue;
            int j = Array.FindIndex(mine.bones, t => t.name == theirs.bones[i].name);
            Assert.That(j, Is.GreaterThanOrEqualTo(0), "Bone " + theirs.bones[i].name);
            for (int k = 0; k < 16; k++)
                Assert.That(b.bindposes[j][k], Is.EqualTo(a.bindposes[i][k]).Within(1e-4f), $"Bind pose of {theirs.bones[i].name} [{k}]");
        }

        // The same faces per slot, facing the same way.
        for (int s = 0; s < a.subMeshCount; s++)
        {
            int[] at = a.GetTriangles(s), bt = b.GetTriangles(s);
            Assert.That(bt.Length, Is.EqualTo(at.Length), "Triangles of slot " + s);
            Vector3 areaA = AreaVector(av, at), areaB = AreaVector(bv, bt);
            Assert.That(Vector3.Distance(areaA, areaB), Is.LessThan(1e-4f), "Surface of slot " + s);
            Assert.That(Vector3.Dot(Vector3.Cross(av[at[1]] - av[at[0]], av[at[2]] - av[at[0]]), an[at[0]]), Is.GreaterThan(0f), "The source faces out");
            var faceNormal = Vector3.Cross(bv[bt[1]] - bv[bt[0]], bv[bt[2]] - bv[bt[0]]);
            Assert.That(Vector3.Dot(faceNormal, bn[bt[0]]), Is.GreaterThan(0f), "Faces turn out on slot " + s);
        }

        // Blendshape frames and offsets by matched vertex.
        var ad = new Vector3[av.Length]; var bd = new Vector3[bv.Length];
        var adn = new Vector3[av.Length]; var bdn = new Vector3[bv.Length];
        for (int shape = 0; shape < a.blendShapeCount; shape++)
        {
            Assert.That(b.GetBlendShapeFrameCount(shape), Is.EqualTo(a.GetBlendShapeFrameCount(shape)), "Frames of " + a.GetBlendShapeName(shape));
            for (int frame = 0; frame < a.GetBlendShapeFrameCount(shape); frame++)
            {
                Assert.That(b.GetBlendShapeFrameWeight(shape, frame), Is.EqualTo(a.GetBlendShapeFrameWeight(shape, frame)).Within(1e-4f));
                a.GetBlendShapeFrameVertices(shape, frame, ad, adn, null);
                b.GetBlendShapeFrameVertices(shape, frame, bd, bdn, null);
                for (int i = 0; i < av.Length; i++)
                {
                    Assert.That(Vector3.Distance(ad[i], bd[match[i]]), Is.LessThan(1e-5f), $"{a.GetBlendShapeName(shape)} frame {frame} on vertex {i}");
                    if (blendShapeNormals) Assert.That(Vector3.Distance(adn[i], bdn[match[i]]), Is.LessThan(1e-4f), $"{a.GetBlendShapeName(shape)} normals on vertex {i}");
                }
            }
        }
    }

    private static Vector3[] Skinned(SkinnedMeshRenderer renderer, Transform space)
    {
        var mesh = renderer.sharedMesh;
        var bones = renderer.bones;
        var bindposes = mesh.bindposes;
        var skin = bones.Select((bone, i) => space.worldToLocalMatrix * bone.localToWorldMatrix * bindposes[i]).ToArray();
        var vertices = mesh.vertices;
        var weights = mesh.boneWeights;
        var result = new Vector3[vertices.Length];
        for (int v = 0; v < vertices.Length; v++)
        {
            var w = weights[v];
            result[v] = skin[w.boneIndex0].MultiplyPoint3x4(vertices[v]) * w.weight0 + skin[w.boneIndex1].MultiplyPoint3x4(vertices[v]) * w.weight1 +
                        skin[w.boneIndex2].MultiplyPoint3x4(vertices[v]) * w.weight2 + skin[w.boneIndex3].MultiplyPoint3x4(vertices[v]) * w.weight3;
        }
        return result;
    }

    private static double Setting(FbxDocument document, string name)
    {
        var p = document.Top("GlobalSettings")?.Property70(name);
        return p == null ? double.NaN : p.Properties[4] is double d ? d : p.Properties[4] is int i ? i : double.NaN;
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "OrbitersFbxExport-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".fbx");

    private static Vector3 AreaVector(Vector3[] v, int[] t)
    {
        var sum = Vector3.zero;
        for (int i = 0; i + 2 < t.Length; i += 3) sum += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
        return sum;
    }

    private static Dictionary<string, float> Weights(BoneWeight w, Transform[] bones)
    {
        var result = new Dictionary<string, float>();
        void Add(int index, float weight) { if (weight > 0f) result[bones[index].name] = (result.TryGetValue(bones[index].name, out float known) ? known : 0f) + weight; }
        Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
        return result;
    }

    private static bool Uses(BoneWeight w, int bone) =>
        w.boneIndex0 == bone && w.weight0 > 0f || w.boneIndex1 == bone && w.weight1 > 0f || w.boneIndex2 == bone && w.weight2 > 0f || w.boneIndex3 == bone && w.weight3 > 0f;

    private static string PathOf(Transform root, Transform t)
    {
        var names = new List<string>();
        for (; t != null && t != root; t = t.parent) names.Insert(0, t.name);
        return string.Join("/", names);
    }

    private static (Vector3 min, Vector3 max) Extents(Vector3[] vertices)
    {
        Vector3 min = vertices[0], max = vertices[0];
        foreach (var v in vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }
}
