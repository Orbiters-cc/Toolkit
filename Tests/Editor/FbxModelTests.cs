using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

// FbxModel against Unity's model importer, on a skinned mesh Blender 5.2 exported as the MCB add-on does (armature away
// from the origin and turned, quads, triangles and an n-gon, a hard edge, two materials, two UV maps, colours, a vertex in
// seven groups, shape keys, a prop on a bone). The vertices may be ordered and split differently: they are matched by
// position, normal, UVs and colour, then everything else is compared through that match.
public sealed class FbxModelTests
{
    private const string Fixture = "Packages/orbiters.toolkit/Tests/Editor/Fixtures/BlenderSkinned.fbx";

    [Test]
    public void ABlenderExportIsBuiltAsTheImporterPlacesIt()
    {
        // The package keeps no .meta files: the importer's side of the comparison needs the fixture readable.
        if (AssetImporter.GetAtPath(Fixture) is ModelImporter importer && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
        var imported = AssetDatabase.LoadAssetAtPath<GameObject>(Fixture);
        Assert.That(imported, Is.Not.Null, "The fixture is imported.");
        var loaded = FbxModel.Load(System.IO.Path.GetFullPath(Fixture));
        try
        {
            var ours = loaded.GetComponentsInChildren<Transform>(true).ToDictionary(t => Path(loaded.transform, t));
            foreach (var theirs in imported.GetComponentsInChildren<Transform>(true))
            {
                string path = Path(imported.transform, theirs);
                Assert.That(ours.ContainsKey(path), Is.True, "Has " + path);
                var mine = ours[path];
                Assert.That(Vector3.Distance(mine.localPosition, theirs.localPosition), Is.LessThan(1e-4f), path + " position");
                Assert.That(Quaternion.Angle(mine.localRotation, theirs.localRotation), Is.LessThan(0.01f), path + " rotation");
                Assert.That(Vector3.Distance(mine.localScale, theirs.localScale), Is.LessThan(1e-4f), path + " scale");
            }
            Assert.That(ours.Count, Is.EqualTo(imported.GetComponentsInChildren<Transform>(true).Length), "No other objects.");

            var prop = ours["Armature/Hips/Spine/Prop"].GetComponent<MeshFilter>();
            Assert.That(prop, Is.Not.Null, "A mesh without skin or blendshapes gets a MeshFilter.");

            var theirRenderer = imported.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
            var myRenderer = ours[Path(imported.transform, theirRenderer.transform)].GetComponent<SkinnedMeshRenderer>();
            Assert.That(myRenderer, Is.Not.Null);
            CompareSkinnedMeshes(imported.transform, theirRenderer, loaded.transform, myRenderer);
        }
        finally
        {
            FbxModel.Release(loaded);
        }
    }

    private static void CompareSkinnedMeshes(Transform theirRoot, SkinnedMeshRenderer theirs, Transform myRoot, SkinnedMeshRenderer mine)
    {
        Mesh a = theirs.sharedMesh, b = mine.sharedMesh;
        Assert.That(Path(myRoot, mine.rootBone), Is.EqualTo(Path(theirRoot, theirs.rootBone)), "Root bone");
        Assert.That(b.vertexCount, Is.EqualTo(a.vertexCount), "Vertices");
        Assert.That(b.subMeshCount, Is.EqualTo(a.subMeshCount), "Material slots");
        Assert.That(Enumerable.Range(0, b.blendShapeCount).Select(b.GetBlendShapeName),
            Is.EqualTo(Enumerable.Range(0, a.blendShapeCount).Select(a.GetBlendShapeName)), "Blendshapes");

        Vector3[] av = a.vertices, bv = b.vertices, an = a.normals, bn = b.normals;
        Vector2[] auv = a.uv, buv = b.uv, auv2 = a.uv2, buv2 = b.uv2;
        Color[] ac = a.colors, bc = b.colors;
        // Each of their vertices to ours: same place, normal and UVs.
        var match = new int[av.Length];
        for (int i = 0; i < av.Length; i++)
        {
            match[i] = -1;
            for (int j = 0; j < bv.Length && match[i] < 0; j++)
                if ((av[i] - bv[j]).sqrMagnitude < 1e-10f && Vector3.Angle(an[i], bn[j]) < 0.05f && (auv[i] - buv[j]).sqrMagnitude < 1e-10f &&
                    (auv2[i] - buv2[j]).sqrMagnitude < 1e-10f && ColorDistance(ac[i], bc[j]) < 1.5f / 255f)
                    match[i] = j;
            Assert.That(match[i], Is.GreaterThanOrEqualTo(0), $"Their vertex {i} at {av[i]:F5} normal {an[i]:F4} uv {auv[i]:F4} uv2 {auv2[i]:F4} colour {ac[i]} has no match.");
        }

        // Weights by bone name, bind poses by bone name.
        var aw = a.boneWeights; var bw = b.boneWeights;
        for (int i = 0; i < av.Length; i++)
        {
            var theirWeights = Weights(aw[i], theirs.bones);
            var myWeights = Weights(bw[match[i]], mine.bones);
            Assert.That(myWeights.Keys.OrderBy(k => k), Is.EqualTo(theirWeights.Keys.OrderBy(k => k)), $"Bones of vertex {i}");
            foreach (var pair in theirWeights) Assert.That(myWeights[pair.Key], Is.EqualTo(pair.Value).Within(1e-3f), $"Weight of {pair.Key} on vertex {i}");
        }
        for (int i = 0; i < theirs.bones.Length; i++)
        {
            int j = System.Array.FindIndex(mine.bones, t => t.name == theirs.bones[i].name);
            if (!aw.Any(w => Uses(w, i))) continue;
            Assert.That(j, Is.GreaterThanOrEqualTo(0), "Bone " + theirs.bones[i].name);
            for (int k = 0; k < 16; k++)
                Assert.That(b.bindposes[j][k], Is.EqualTo(a.bindposes[i][k]).Within(1e-4f), $"Bind pose of {theirs.bones[i].name} [{k}]");
        }

        // Triangles: the same count per slot, facing the same way over the same surface (a quad may be split along the other
        // diagonal).
        for (int s = 0; s < a.subMeshCount; s++)
        {
            int[] at = a.GetTriangles(s), bt = b.GetTriangles(s);
            Assert.That(bt.Length, Is.EqualTo(at.Length), "Triangles of slot " + s);
            Vector3 areaA = AreaVector(av, at), areaB = AreaVector(bv, bt);
            Assert.That(Vector3.Distance(areaA, areaB), Is.LessThan(1e-3f * Mathf.Max(1f, areaA.magnitude)), "Surface of slot " + s);
        }

        // Blendshape offsets by matched vertex.
        var ad = new Vector3[av.Length]; var bd = new Vector3[bv.Length];
        for (int shape = 0; shape < a.blendShapeCount; shape++)
        {
            int frame = a.GetBlendShapeFrameCount(shape) - 1;
            Assert.That(b.GetBlendShapeFrameCount(shape) - 1, Is.EqualTo(frame));
            Assert.That(b.GetBlendShapeFrameWeight(shape, frame), Is.EqualTo(a.GetBlendShapeFrameWeight(shape, frame)));
            a.GetBlendShapeFrameVertices(shape, frame, ad, null, null);
            b.GetBlendShapeFrameVertices(shape, frame, bd, null, null);
            for (int i = 0; i < av.Length; i++)
                Assert.That(Vector3.Distance(ad[i], bd[match[i]]), Is.LessThan(1e-5f), $"{a.GetBlendShapeName(shape)} on vertex {i}");
        }
    }

    private static float ColorDistance(Color a, Color b) =>
        Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a));

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

    private static string Path(Transform root, Transform t)
    {
        if (t == null) return null;
        var names = new List<string>();
        for (; t != null && t != root; t = t.parent) names.Insert(0, t.name);
        return string.Join("/", names);
    }
}
