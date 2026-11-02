using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

// Meshes toggled on their own, merged into the mesh always shown (MeshMerge.MergeToggled): three toggled props, a rigid
// prop (a mesh renderer on a bone), a rigid skinned ring and a sleeve skinned to two bones. Fewer renderers, each toggle
// still shows and hides its part only, the rest state is the toggles' (Write Defaults on and off), and what it adds to
// the download is what the policy weighs.
public sealed class MeshMergeToggledTests
{
    private readonly List<Object> owned = new List<Object>();
    private Scene scene;
    private GameObject root;
    private AnimatorController fx;
    private Transform[] bones;
    private SkinnedMeshRenderer body, ring, sleeve;
    private MeshRenderer hat;
    private const string HatPath = "Bone0/Bone1/Bone2/Bone3/Hat";
    private Material hatMaterial, ringMaterial, sleeveMaterial;

    // PC's skinned mesh limits; meshes rated as if one were too many, so the hat's merge is worth it.
    private static MeshMerge.ToggledPolicy Policy(int[] skinned = null, int[] meshes = null, int[] bones = null) => new MeshMerge.ToggledPolicy
    {
        SkinnedLimits = skinned ?? new[] { 1, 2, 8, 16 }, MeshLimits = meshes ?? new[] { 0, 4, 8, 16 }, BoneLimits = bones ?? new[] { 75, 150, 256, 400 },
    };

    [SetUp] public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        root = New("Avatar");
        var descriptor = root.AddComponent<VRCAvatarDescriptor>();
        fx = Own(new AnimatorController());
        descriptor.customizeAnimationLayers = true;
        descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
        bones = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            bones[i] = New("Bone" + i).transform;
            bones[i].SetParent(i == 0 ? root.transform : bones[i - 1], false);
            bones[i].localPosition = i == 0 ? Vector3.zero : Vector3.up;
        }
        // The body, always shown: a quad on each bone.
        body = Skinned("Body", root.transform, Enumerable.Range(0, 4).SelectMany(b => Quad(new Vector3(0, b, 0), new Vector3(1, 0, 0), new Vector3(0, .5f, 0))
            .Select(p => (p, new[] { (b, 1f) }))).ToList(), bones, out _);
        // A hat on the last bone, its own object toggled, shown at rest.
        var hatObject = New("Hat");
        hatObject.transform.SetParent(bones[3], false);
        hatObject.transform.localPosition = new Vector3(.3f, .2f, 0);
        var hatMesh = Own(new Mesh { name = "Hat" });
        hatMesh.SetVertices(Quad(Vector3.zero, new Vector3(.2f, 0, 0), new Vector3(0, .2f, 0)).Concat(Quad(new Vector3(0, 0, .2f), new Vector3(.2f, 0, 0), new Vector3(0, .2f, 0))).ToList());
        hatMesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 }, 0);
        hatMesh.RecalculateNormals();
        hatObject.AddComponent<MeshFilter>().sharedMesh = hatMesh;
        hat = hatObject.AddComponent<MeshRenderer>();
        hat.sharedMaterial = hatMaterial = Material("Hat");
        // A ring skinned to the third bone only, its renderer switched on by its toggle (off at rest).
        ring = Skinned("Ring", root.transform, Quad(new Vector3(.2f, 2.1f, .1f), new Vector3(.3f, 0, 0), new Vector3(0, .1f, 0)).Select(p => (p, new[] { (0, 1f) })).ToList(),
            new[] { bones[2] }, out ringMaterial);
        ring.enabled = false;
        // A sleeve on the second and third bones under an outfit toggled on and off, with an animated blendshape: its bottom
        // follows the second bone, its top both (where they meet).
        var outfit = New("Outfit");
        sleeve = Skinned("Sleeve", outfit.transform, new List<(Vector3, (int, float)[])>
        {
            (new Vector3(0, 1.2f, .3f), new[] { (0, 1f) }), (new Vector3(.4f, 1.2f, .3f), new[] { (0, 1f) }),
            (new Vector3(0, 1.9f, .3f), new[] { (0, .5f), (1, .5f) }), (new Vector3(.4f, 1.9f, .3f), new[] { (0, .5f), (1, .5f) }),
        }, new[] { bones[1], bones[2] }, out sleeveMaterial, shape: "Loose");

        Toggle("Hat", HatPath, typeof(GameObject), "m_IsActive");
        Toggle("Ring", "Ring", typeof(SkinnedMeshRenderer), "m_Enabled");
        Toggle("Outfit", "Outfit", typeof(GameObject), "m_IsActive");
        var loose = Own(new AnimationClip { name = "Loose" });
        AnimationUtility.SetEditorCurve(loose, EditorCurveBinding.FloatCurve("Outfit/Sleeve", typeof(SkinnedMeshRenderer), "blendShape.Loose"), AnimationCurve.Constant(0, 1, 100));
        fx.AddLayer("Loose");
        fx.layers.Last().stateMachine.AddState("Loose").motion = loose;
    }

    [TearDown] public void TearDown()
    {
        AttachmentAnimationBuild.Release(root);
        EditorSceneManager.ClosePreviewScene(scene);
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
    }

    [Test] public void ThreeToggledPropsJoinTheBodyAndEachToggleStillShowsAndHidesItsPart()
    {
        Pose();
        var hatShown = World(hat);
        var ringShown = World(ring);
        var sleeveShown = World(sleeve);
        sleeve.SetBlendShapeWeight(0, 100);
        var sleeveLoose = World(sleeve);
        sleeve.SetBlendShapeWeight(0, 0);
        int sleeveVertices = sleeve.sharedMesh.vertexCount;
        var policy = Policy();
        var build = AttachmentAnimationBuild.Prepare(root);
        var usage = new AvatarUsage(root);

        var result = MeshMerge.MergeToggled(usage, build, policy);
        owned.AddRange(result.Meshes);

        Assert.That(result.Skipped, Is.Empty, string.Join("\n", result.Skipped));
        Assert.That(result.Merged, Is.EqualTo(3)); Assert.That(result.Into, Is.EqualTo(1));
        Assert.That(root.GetComponentsInChildren<SkinnedMeshRenderer>(true), Is.EqualTo(new[] { body }), "One skinned mesh left: the body.");
        Assert.That(root.GetComponentsInChildren<MeshRenderer>(true), Is.Empty);
        Assert.That(result.HideBones, Is.EqualTo(2), "The hat and the ring follow one bone each: a hide bone each.");
        Assert.That(result.CollapseShapes, Is.EqualTo(1), "The sleeve follows two bones: a blendshape hides it.");
        Assert.That(result.AddedBytes, Is.EqualTo(2 * 64 + policy.CollapseBytes(sleeveVertices)));
        var hatBone = bones[3].Find(MeshMerge.HideBonePrefix + "Hat");
        var ringBone = bones[2].Find(MeshMerge.HideBonePrefix + "Ring");
        Assert.That(hatBone, Is.Not.Null, "Above the hat's own object, which its toggle hides: under the bone it hangs from.");
        Assert.That(ringBone, Is.Not.Null);
        int hidden = body.sharedMesh.GetBlendShapeIndex(MeshMerge.HiddenShapePrefix + "Outfit");
        Assert.That(hidden, Is.GreaterThanOrEqualTo(0));
        Assert.That(body.sharedMesh.GetBlendShapeIndex("Loose"), Is.GreaterThanOrEqualTo(0), "The sleeve's animated shape joined the body.");

        // At rest, as the toggles have it: the hat and the sleeve shown, the ring hidden (Write Defaults on returns here).
        Assert.That(hatBone.localScale, Is.EqualTo(Vector3.one)); Assert.That(ringBone.localScale, Is.EqualTo(Vector3.zero));
        Assert.That(body.GetBlendShapeWeight(hidden), Is.EqualTo(0f));
        AssertShown(hatMaterial, hatShown); AssertHidden(ringMaterial); AssertShown(sleeveMaterial, sleeveShown);

        // Each toggle, from rest (Write Defaults on: what the clip doesn't set is at rest).
        var rest = Rest();
        void Play(string clip) { Restore(rest); Clip(build, clip).SampleAnimation(root, 0); }
        Play("Hat off"); AssertHidden(hatMaterial); AssertHidden(ringMaterial); AssertShown(sleeveMaterial, sleeveShown);
        Play("Hat on"); AssertShown(hatMaterial, hatShown);
        Play("Ring on"); AssertShown(ringMaterial, ringShown); AssertShown(hatMaterial, hatShown);
        Play("Ring off"); AssertHidden(ringMaterial);
        Play("Outfit off"); AssertHidden(sleeveMaterial); AssertShown(hatMaterial, hatShown);
        Play("Outfit on"); AssertShown(sleeveMaterial, sleeveShown);
        Play("Loose"); AssertShown(sleeveMaterial, sleeveLoose);

        // Write Defaults off: each clip leaves what it doesn't set as the last one did.
        Restore(rest);
        foreach (var clip in new[] { "Outfit off", "Ring on", "Hat off" }) Clip(build, clip).SampleAnimation(root, 0);
        AssertHidden(sleeveMaterial); AssertShown(ringMaterial, ringShown); AssertHidden(hatMaterial);
        foreach (var clip in new[] { "Hat on", "Outfit on" }) Clip(build, clip).SampleAnimation(root, 0);
        AssertShown(sleeveMaterial, sleeveShown); AssertShown(ringMaterial, ringShown); AssertShown(hatMaterial, hatShown);
        // And every clip that sets a toggle sets its part too, with the same keys.
        foreach (var (clip, _) in build.Clips)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            bool hatToggle = bindings.Any(b => b.path == HatPath && b.propertyName == "m_IsActive"), outfitToggle = bindings.Any(b => b.path == "Outfit" && b.propertyName == "m_IsActive");
            Assert.That(bindings.Any(b => b.propertyName == "m_LocalScale.x" && b.path.EndsWith(MeshMerge.HideBonePrefix + "Hat")), Is.EqualTo(hatToggle), clip.name);
            Assert.That(bindings.Any(b => b.propertyName == "blendShape." + MeshMerge.HiddenShapePrefix + "Outfit"), Is.EqualTo(outfitToggle), clip.name);
            Assert.That(bindings.Any(b => b.path == "Ring" && b.propertyName == "m_Enabled"), Is.False, "The ring's switch left with its renderer.");
        }
        Assert.That(BindingAudit.Of(build.Clips).Missing, Is.Empty, "Nothing points at what left.");
        Assert.That(AnimationUtility.GetCurveBindings(OwnClip("Ring on")).Any(b => b.path == "Ring" && b.propertyName == "m_Enabled"), "The avatar's own clips are untouched.");
    }

    // The sleeve folds onto one point, the origin of the bone it follows most (Bone1): a vertex on that bone lands on it, one
    // on two bones on the line between where each carries it, so the hidden sleeve is flat in any pose.
    [Test] public void APartHiddenByItsBlendshapeIsFlatInAnyPose()
    {
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), build, Policy());
        owned.AddRange(result.Meshes);
        Clip(build, "Outfit off").SampleAnimation(root, 0);
        foreach (var angle in new[] { 0f, 35f, 90f })
        {
            bones[1].localRotation = Quaternion.Euler(0, 0, angle);
            bones[2].localRotation = Quaternion.Euler(angle, 0, -angle);
            var baked = Baked();
            var indices = Indices(sleeveMaterial);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = baked[indices[t]]; var b = baked[indices[t + 1]]; var c = baked[indices[t + 2]];
                Assert.That(Vector3.Cross(b - a, c - a).magnitude, Is.LessThan(1e-6f), "A face of the hidden sleeve has an area at " + angle + "°.");
            }
            int onBone = indices.Distinct().Count(i => Vector3.Distance(baked[i], bones[1].position) < 1e-4f);
            Assert.That(onBone, Is.GreaterThanOrEqualTo(2), "Its two vertices on Bone1 alone land on Bone1's origin (all four at rest).");
        }
    }

    // The steps after the merge: bones that never move are folded, then objects get short names. The hide bones stay, and the
    // toggles still find them.
    [Test] public void HideBonesSurviveTheStepsAfterTheMerge()
    {
        var build = AttachmentAnimationBuild.Prepare(root);
        var usage = new AvatarUsage(root);
        var result = MeshMerge.MergeToggled(usage, build, Policy());
        owned.AddRange(result.Meshes);
        var hatBone = bones[3].Find(MeshMerge.HideBonePrefix + "Hat");
        owned.AddRange(StaticBones.Fold(usage).Meshes);
        Assert.That(body.bones, Does.Contain(hatBone), "Animated, it is never folded into its parent.");
        ShortNames.Rename(usage, build);
        Assert.That(BindingAudit.Of(build.Clips).Missing, Is.Empty);
        Clip(build, "Hat off").SampleAnimation(root, 0);
        Assert.That(hatBone.localScale, Is.EqualTo(Vector3.zero), "Renamed, it is still what hides the hat.");
    }

    // Nothing is merged when no rating gets better: three skinned meshes are Medium, and one would still be.
    [Test] public void NothingMergesWhenNoRatingGetsBetter()
    {
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), build, Policy(skinned: new[] { 0, 0, 8, 16 }, meshes: new[] { 1, 4, 8, 16 }));
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(0));
        Assert.That(ring != null && sleeve != null && hat != null);
        Assert.That(result.Skipped.Count(s => s.Contains("no better rating")), Is.EqualTo(3), string.Join("\n", result.Skipped));
        Assert.That(MeshMerge.MergeToggled(new AvatarUsage(root), build, null).Merged, Is.EqualTo(0), "Without a policy (the SDK's limits not loaded) nothing merges.");
    }

    // A bone more that would rate the bones worse: the rigid parts are hidden by blendshapes instead.
    [Test] public void RigidPartsTakeABlendshapeWhenABoneMoreWouldRateTheBonesWorse()
    {
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), build, Policy(bones: new[] { 4, 150, 256, 400 }));
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(3));
        Assert.That(result.HideBones, Is.EqualTo(0));
        Assert.That(result.CollapseShapes, Is.EqualTo(3));
        var rest = Rest();
        Restore(rest); Clip(build, "Hat off").SampleAnimation(root, 0); AssertHidden(hatMaterial);
        Restore(rest); Clip(build, "Ring on").SampleAnimation(root, 0); Assert.That(Spread(ringMaterial), Is.GreaterThan(.05f));
    }

    // What stays apart, and says why: a toggle that also changes its material, a second toggle above it, a prop a constraint
    // moves (it can be dropped in the world), what a component points at.
    [Test] public void WhatTheMergeWouldChangeStaysApart()
    {
        Toggle("Sleeve material", "Outfit/Sleeve", typeof(SkinnedMeshRenderer), "material._Color.a");
        Toggle("Sleeve", "Outfit/Sleeve", typeof(GameObject), "m_IsActive");
        hat.gameObject.AddComponent<VRCParentConstraint>();
        root.GetComponent<VRCAvatarDescriptor>().VisemeSkinnedMesh = ring;
        var build = AttachmentAnimationBuild.Prepare(root);
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), build, Policy(skinned: new[] { 1, 1, 1, 1 }, meshes: new[] { 0, 0, 0, 0 }));
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(0), string.Join("\n", result.Skipped));
        Assert.That(result.Skipped.Single(s => s.StartsWith("Outfit/Sleeve")), Does.Contain("material._Color.a"));
        Assert.That(result.Skipped.Single(s => s.StartsWith(HatPath)), Does.Contain("constraint"));
        Assert.That(result.Skipped.Single(s => s.StartsWith("Ring")), Does.Contain("points at it"));
    }

    // An estimate merges on a copy nothing plays: the same renderers join, the animations stay as they are.
    [Test] public void AnEstimateMergesTheSameWithoutAnimations()
    {
        // The sleeve's shape animation follows it only at upload: without it, an estimate takes the sleeve in too.
        fx.RemoveLayer(fx.layers.Length - 1);
        var result = MeshMerge.MergeToggled(new AvatarUsage(root), null, Policy());
        owned.AddRange(result.Meshes);
        Assert.That(result.Merged, Is.EqualTo(3), string.Join("\n", result.Skipped));
        Assert.That(root.GetComponentsInChildren<SkinnedMeshRenderer>(true), Is.EqualTo(new[] { body }));
        Assert.That(AnimationUtility.GetCurveBindings(OwnClip("Ring on")).Any(b => b.path == "Ring" && b.propertyName == "m_Enabled"));
    }

    // What a hiding blendshape may add, by platform (ToggledPolicy.For with the SDK's limits).
    [Test] public void MobileCapsAreAQuarterOfPcs()
    {
        var pc = MeshMerge.ToggledPolicy.For(false);
        var mobile = MeshMerge.ToggledPolicy.For(true);
        if (pc == null || mobile == null) Assert.Inconclusive("The VRChat SDK has not loaded its performance levels yet.");
        Assert.That((pc.MaxBytes, pc.MaxTotalBytes), Is.EqualTo((256 * 1024L, 1024 * 1024L)));
        Assert.That((mobile.MaxBytes, mobile.MaxTotalBytes), Is.EqualTo((64 * 1024L, 256 * 1024L)));
        Assert.That(mobile.SkinnedLimits[2], Is.LessThan(pc.SkinnedLimits[2]), "Mobile's own limits.");
    }

    // ---- Helpers ----

    private T Own<T>(T o) where T : Object { owned.Add(o); return o; }

    private GameObject New(string name)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, scene);
        if (root != null) go.transform.SetParent(root.transform, false);
        return go;
    }

    private Material Material(string name) => Own(new Material(Shader.Find("Standard")) { name = name });

    private static IEnumerable<Vector3> Quad(Vector3 at, Vector3 u, Vector3 v) => new[] { at, at + u, at + v, at + u + v };

    // A skinned mesh in the avatar's space (bound at rest), quads of four corners each.
    private SkinnedMeshRenderer Skinned(string name, Transform parent, List<(Vector3 Position, (int Bone, float Weight)[] Weights)> corners, Transform[] to,
        out Material material, string shape = null)
    {
        var go = New(name);
        go.transform.SetParent(parent, false);
        var mesh = Own(new Mesh { name = name });
        mesh.SetVertices(corners.Select(c => c.Position).ToList());
        var counts = corners.Select(c => (byte)c.Weights.Length).ToArray();
        var weights = corners.SelectMany(c => c.Weights.OrderByDescending(w => w.Weight).Select(w => new BoneWeight1 { boneIndex = w.Bone, weight = w.Weight })).ToArray();
        using (var perVertex = new Unity.Collections.NativeArray<byte>(counts, Unity.Collections.Allocator.Temp))
        using (var all = new Unity.Collections.NativeArray<BoneWeight1>(weights, Unity.Collections.Allocator.Temp))
            mesh.SetBoneWeights(perVertex, all);
        var indices = new List<int>();
        for (int q = 0; q + 3 < corners.Count; q += 4) indices.AddRange(new[] { q, q + 2, q + 1, q + 1, q + 2, q + 3 });
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.bindposes = to.Select(b => b.worldToLocalMatrix).ToArray();
        if (shape != null) mesh.AddBlendShapeFrame(shape, 100, corners.Select(_ => Vector3.forward * .1f).ToArray(), null, null);
        mesh.RecalculateBounds();
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh; renderer.bones = to; renderer.rootBone = to[0];
        renderer.sharedMaterial = material = Material(name);
        return renderer;
    }

    // A layer with an "on" and an "off" state for one toggle.
    private void Toggle(string name, string path, System.Type type, string property)
    {
        fx.AddLayer(name);
        var machine = fx.layers.Last().stateMachine;
        foreach (var on in new[] { true, false })
        {
            var clip = Own(new AnimationClip { name = name + (on ? " on" : " off") });
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, on ? 1 : 0));
            machine.AddState(clip.name).motion = clip;
        }
    }

    private AnimationClip OwnClip(string name) => owned.OfType<AnimationClip>().First(c => c.name == name);
    private static AnimationClip Clip(AttachmentAnimationBuild build, string name) => build.Clips.Select(c => c.Clip).First(c => c.name == name);

    private void Pose()
    {
        bones[1].localRotation = Quaternion.Euler(0, 0, 30);
        bones[2].localRotation = Quaternion.Euler(20, 0, 0);
        bones[3].localRotation = Quaternion.Euler(0, 40, 10);
    }

    // What the animations here set, as it is now: put back for each "frame" of Write Defaults on.
    private (Dictionary<Transform, Vector3> Scales, float[] Weights, Dictionary<GameObject, bool> Active, Dictionary<Renderer, bool> Enabled) Rest() => (
        root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => t.localScale),
        Enumerable.Range(0, body.sharedMesh.blendShapeCount).Select(body.GetBlendShapeWeight).ToArray(),
        root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.gameObject, t => t.gameObject.activeSelf),
        root.GetComponentsInChildren<Renderer>(true).ToDictionary(r => r, r => r.enabled));

    private void Restore((Dictionary<Transform, Vector3> Scales, float[] Weights, Dictionary<GameObject, bool> Active, Dictionary<Renderer, bool> Enabled) rest)
    {
        foreach (var p in rest.Scales) p.Key.localScale = p.Value;
        for (int i = 0; i < rest.Weights.Length; i++) body.SetBlendShapeWeight(i, rest.Weights[i]);
        foreach (var p in rest.Active) p.Key.SetActive(p.Value);
        foreach (var p in rest.Enabled) p.Key.enabled = p.Value;
    }

    private List<Vector3> World(Renderer renderer)
    {
        if (renderer is MeshRenderer) return renderer.GetComponent<MeshFilter>().sharedMesh.vertices.Select(renderer.transform.TransformPoint).ToList();
        var skinned = (SkinnedMeshRenderer)renderer;
        var baked = Own(new Mesh());
        skinned.BakeMesh(baked, true);
        return baked.vertices.Select(skinned.transform.TransformPoint).ToList();
    }

    private Vector3[] Baked()
    {
        var baked = Own(new Mesh());
        body.BakeMesh(baked, true);
        return baked.vertices.Select(body.transform.TransformPoint).ToArray();
    }

    // The merged mesh's vertices drawn with a part's material (each part has its own).
    private int[] Indices(Material material)
    {
        int slot = System.Array.IndexOf(body.sharedMaterials, material);
        Assert.That(slot, Is.GreaterThanOrEqualTo(0), material.name + " is drawn by the merged mesh");
        return body.sharedMesh.GetIndices(slot);
    }

    private float Spread(Material material)
    {
        var baked = Baked();
        var points = Indices(material).Select(i => baked[i]).ToList();
        return points.Max(p => points.Max(q => Vector3.Distance(p, q)));
    }

    // Shown: every vertex of the part where it was before the merge (in the order of its own mesh).
    private void AssertShown(Material material, List<Vector3> expected)
    {
        var baked = Baked();
        var drawn = new HashSet<int>(Indices(material)).OrderBy(i => i).Select(i => baked[i]).ToList();
        Assert.That(drawn.Count, Is.EqualTo(expected.Count), material.name);
        foreach (var e in expected)
            Assert.That(drawn.Min(d => Vector3.Distance(d, e)), Is.LessThan(1e-4f), material.name + " shown: a vertex is missing at " + e);
    }

    // Hidden: every face of the part has no area.
    private void AssertHidden(Material material)
    {
        var baked = Baked();
        var indices = Indices(material);
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = baked[indices[t]]; var b = baked[indices[t + 1]]; var c = baked[indices[t + 2]];
            Assert.That(Vector3.Cross(b - a, c - a).magnitude, Is.LessThan(1e-6f), material.name + " hidden: a face still has an area");
        }
    }
}
