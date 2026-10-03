using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

/// <summary>Which items on a custom base need its blendshapes: only meshes close to skin a custom shape moves.</summary>
public sealed class FitCheckTests
{
    private readonly List<Object> owned = new List<Object>();
    private GameObject avatar;
    private SkinnedMeshRenderer body;

    [SetUp]
    public void SetUp()
    {
        avatar = Own(new GameObject("Fit check avatar"));
        // Arm skin around x = 1 (moved by the arm shapes), leg skin around y = -1 (moved by the leg shape).
        var arm = Cluster(new Vector3(1, 0, 0));
        var leg = Cluster(new Vector3(0, -1, 0));
        var vertices = arm.Concat(leg).ToArray();
        var mesh = Own(new Mesh { name = "Body", vertices = vertices, triangles = Enumerable.Range(0, vertices.Length - vertices.Length % 3).ToArray() });
        mesh.AddBlendShapeFrame("Flex arms", 100, vertices.Select((v, i) => i < arm.Length ? new Vector3(0.01f, 0, 0) : Vector3.zero).ToArray(), null, null);
        mesh.AddBlendShapeFrame("Muscle arms", 100, vertices.Select((v, i) => i < arm.Length ? new Vector3(0, 0.01f, 0) : Vector3.zero).ToArray(), null, null);
        mesh.AddBlendShapeFrame("Flex legs", 100, vertices.Select((v, i) => i >= arm.Length ? new Vector3(0.01f, 0, 0) : Vector3.zero).ToArray(), null, null);
        mesh.AddBlendShapeFrame("Tiny", 100, vertices.Select(_ => new Vector3(0.0001f, 0, 0)).ToArray(), null, null);
        body = Skin("Body", avatar.transform, mesh);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
        owned.Clear();
    }

    [Test]
    public void NearFindsMeshesCloseToMovedSkinOnly()
    {
        var moved = new List<Vector3[]> { Cluster(new Vector3(1, 0, 0)), Cluster(new Vector3(0, -1, 0)) };
        var sleeve = Cluster(new Vector3(1.02f, 0, 0));
        var hat = Cluster(new Vector3(0, 2, 0));
        var near = RefitRelevance.Near(moved, new List<Vector3[]> { sleeve, hat }, CancellationToken.None);
        Assert.That(near[0], Is.EqualTo(new[] { true, false }));
        Assert.That(near[1], Is.EqualTo(new[] { false, false }));
    }

    [UnityTest]
    public IEnumerator ClothingNearTheArmsIsAskedAboutTheArmShapesOnly()
    {
        var jacket = Item("Jacket", new Vector3(1.02f, 0, 0));
        var suggestion = Check(jacket, State(canFit: true));
        yield return Wait(suggestion);
        var result = suggestion.Result;
        Assert.That(result.Advice, Is.EqualTo(FitAdvice.AskFit));
        Assert.That(result.Missing.Values.Single(), Is.EqualTo(new[] { "Flex arms", "Muscle arms" }), "The leg and too small shapes are not near it.");
        Assert.That(result.ShapeCount, Is.EqualTo(2));
    }

    [UnityTest]
    public IEnumerator FarAwayItemsAreLeftAlone()
    {
        var hat = Item("Hat", new Vector3(0, 2, 0));
        var suggestion = Check(hat, State(canFit: true));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.None));
        Assert.That(suggestion.Result.Missing, Is.Empty);
    }

    [UnityTest]
    public IEnumerator AnItemWithSomeOfTheShapesOnlyGetsTheOthers()
    {
        var jacket = Item("Jacket", new Vector3(1.02f, 0, 0), "flex_arms");
        var suggestion = Check(jacket, State(canFit: true));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.AddShapes));
        Assert.That(suggestion.Result.CreatorAdapted, Is.True);
        Assert.That(suggestion.Result.Missing.Values.Single(), Is.EqualTo(new[] { "Muscle arms" }));
    }

    [UnityTest]
    public IEnumerator AnArmatureFittedItemStillNeedsASurfaceFitQuestionWhenNoShapeIsNear()
    {
        var jacket = Item("Jacket", new Vector3(0, 2, 0));
        var attachment = jacket.AddComponent<OrbitersAttachment>();
        attachment.fitted.Add(new OrbitersAttachment.FittedPose { transform = jacket.transform });
        var suggestion = Check(jacket, State(canFit: true));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.AskFit));
        Assert.That(suggestion.Result.ShapeCount, Is.Zero);

        jacket.AddComponent<OrbitersFitInfo>().customBaseAssetId = 14;
        suggestion = Check(jacket, State(canFit: true));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.None), "An explicit creator fit for this custom base remains authoritative.");
    }

    [UnityTest]
    public IEnumerator ArmatureFitWithoutAnOriginalBaseDoesNotOfferAnUnavailableRefit()
    {
        var jacket = Item("Jacket", new Vector3(0, 2, 0));
        jacket.AddComponent<OrbitersAttachment>().fitted.Add(new OrbitersAttachment.FittedPose { transform = jacket.transform });
        var suggestion = Check(jacket, State(canFit: false));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.None));
    }

    [Test]
    public void SkinnedRelevancePointsApplyImportAndRendererScaleOnce()
    {
        var jacket = Item("Scaled jacket", new Vector3(1, 0, 0));
        var renderer = jacket.GetComponentInChildren<SkinnedMeshRenderer>();
        var bone = Own(new GameObject("Scale test bone")).transform;
        bone.SetParent(avatar.transform, false);
        renderer.bones = new[] { bone }; renderer.rootBone = bone;
        renderer.sharedMesh.bindposes = new[] { bone.worldToLocalMatrix * renderer.localToWorldMatrix };
        renderer.sharedMesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, renderer.sharedMesh.vertexCount).ToArray();
        renderer.transform.localScale = new Vector3(0.88f, 0.88f, 0.88f);
        avatar.transform.localScale = Vector3.one * 1.3f;
        avatar.transform.SetPositionAndRotation(new Vector3(2, 3, -1), Quaternion.Euler(10, 30, 5));
        var points = RefitRelevance.WorldPoints(renderer);
        var expected = bone.localToWorldMatrix * renderer.sharedMesh.bindposes[0];
        for (int i = 0; i < points.Length; i++)
            Assert.That(Vector3.Distance(points[i], expected.MultiplyPoint3x4(renderer.sharedMesh.vertices[i])), Is.LessThan(0.0001f));
    }

    [UnityTest]
    public IEnumerator WithoutTheOriginalBaseOnlyShapesAreOffered()
    {
        var jacket = Item("Jacket", new Vector3(1.02f, 0, 0));
        var suggestion = Check(jacket, State(canFit: false));
        yield return Wait(suggestion);
        Assert.That(suggestion.Result.Advice, Is.EqualTo(FitAdvice.AddShapes));
    }

    [UnityTest]
    public IEnumerator TheCreatorsFitInfoDecides()
    {
        var madeForBase = Item("Jacket", new Vector3(1.02f, 0, 0));
        madeForBase.AddComponent<OrbitersFitInfo>().excludedShapes.Add("Muscle arms");
        var original = Check(madeForBase, State(canFit: true));
        yield return Wait(original);
        Assert.That(original.Result.Advice, Is.EqualTo(FitAdvice.Refit));
        Assert.That(original.Result.MadeForOriginal, Is.True);
        Assert.That(original.Result.Missing.Values.Single(), Is.EqualTo(new[] { "Flex arms" }), "Excluded shapes are never offered.");

        var madeForCustom = Item("Shirt", new Vector3(1.02f, 0, 0));
        madeForCustom.AddComponent<OrbitersFitInfo>().customBaseAssetId = 14;
        var custom = Check(madeForCustom, State(canFit: true));
        yield return Wait(custom);
        Assert.That(custom.Result.Advice, Is.EqualTo(FitAdvice.AddShapes));
    }

    private CustomBaseState State(bool canFit)
    {
        var info = new CustomBaseInfo
        {
            Key = "test:14", Name = "Muscle base 1.0", AssetId = 14, Body = body, Source = "Test",
            Shapes = new List<string> { "Flex arms", "Muscle arms", "Flex legs", "Tiny" },
        };
        if (canFit) info.ResolveOriginal = () => null;
        return new CustomBaseState { Info = info };
    }

    // The body map is measured over editor updates, like in the editor.
    private static Task<FitSuggestion> Check(GameObject item, CustomBaseState state)
    {
        async Task<FitSuggestion> Run()
        {
            state.Map = await BodyShapeMap.BuildAsync(state.Info.Body, state.Info.Shapes, CancellationToken.None);
            return await FitCheck.CheckAsync(item, state, CancellationToken.None);
        }
        return Run();
    }

    private static IEnumerator Wait(Task task)
    {
        for (int frames = 0; !task.IsCompleted && frames < 600; frames++) yield return null;
        Assert.That(task.IsCompleted, Is.True, "The check did not finish.");
        if (task.IsFaulted) throw task.Exception.InnerException;
    }

    private GameObject Item(string name, Vector3 center, params string[] shapes)
    {
        var item = Own(new GameObject(name));
        item.transform.SetParent(avatar.transform, false);
        var points = Cluster(center);
        var mesh = Own(new Mesh { name = name, vertices = points, triangles = Enumerable.Range(0, points.Length - points.Length % 3).ToArray() });
        foreach (string shape in shapes) mesh.AddBlendShapeFrame(shape, 100, new Vector3[points.Length], null, null);
        Skin(name + " mesh", item.transform, mesh);
        return item;
    }

    // Twelve points within 1.5 cm of a center.
    private static Vector3[] Cluster(Vector3 center) =>
        Enumerable.Range(0, 12).Select(i => center + new Vector3((i % 3) * 0.005f, (i / 3 % 2) * 0.005f, (i / 6) * 0.005f)).ToArray();

    private SkinnedMeshRenderer Skin(string name, Transform parent, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        return renderer;
    }

    private T Own<T>(T item) where T : Object
    {
        owned.Add(item);
        return item;
    }
}
