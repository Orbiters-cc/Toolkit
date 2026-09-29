using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.SurfaceFollow
{
    /// <summary>How one body blendshape moves the skin under an accessory.</summary>
    public struct FollowShape
    {
        public string Name;
        public int Frame;
        public float FrameWeight;
        public Vector3 Move;
        public Quaternion Turn;
        public float TurnDegrees;
    }

    /// <summary>An accessory and where it sits on the body: the closest skin triangle and what each blendshape does there.</summary>
    public sealed class FollowTarget
    {
        public Renderer Renderer;
        public Vector3 Anchor;
        public float Gap;
        public int Triangle = -1;
        public Vector3 Barycentric;
        public readonly List<FollowShape> Shapes = new List<FollowShape>();
        /// <summary>Why the accessory is left as it is, when it is.</summary>
        public string Skipped;
    }

    /// <summary>
    /// Keeps rigid accessories on the skin when body blendshapes change (beta). Each accessory is anchored to the closest
    /// triangle of the body; for each body blendshape, the movement and tilt of that triangle become a blendshape of the
    /// accessory with the same name, which <see cref="BlendShapeSync"/> then drives with the body's weights and animations.
    /// Works on the build copy only; plain meshes become single-bone skinned meshes there.
    /// </summary>
    public static class SurfaceFollow
    {
        private const float MinimumMove = 0.0002f, MinimumTurn = 0.3f;

        /// <summary>The body: the skinned mesh with the most blendshape data (vertices times shapes) under the root.</summary>
        public static SkinnedMeshRenderer FindBody(Transform root)
        {
            return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0)
                .OrderByDescending(r => (long)r.sharedMesh.vertexCount * r.sharedMesh.blendShapeCount)
                .FirstOrDefault();
        }

        public static SkinnedMeshRenderer BodyOf(OrbitersSurfaceFollow follow)
        {
            if (follow.body != null) return follow.body;
            var descriptor = follow.GetComponentInParent<VRCAvatarDescriptor>(true);
            return FindBody(descriptor != null ? descriptor.transform : follow.transform.root);
        }

        /// <summary>What would follow the body, and how, without changing anything (the inspector's check, and the build).</summary>
        public static List<FollowTarget> Plan(OrbitersSurfaceFollow follow)
        {
            var result = new List<FollowTarget>();
            var body = BodyOf(follow);
            if (body == null || body.sharedMesh == null) return result;
            var surface = new BodySurface(body);
            foreach (var renderer in Candidates(follow, body))
            {
                var target = new FollowTarget { Renderer = renderer, Anchor = renderer.bounds.center };
                surface.Closest(target.Anchor, out target.Triangle, out target.Barycentric, out target.Gap);
                if (follow.scope == OrbitersSurfaceFollow.Scope.SmallAccessories && target.Gap - renderer.bounds.extents.magnitude > follow.maximumGap) continue;
                if (target.Triangle < 0) { target.Skipped = "no body surface found"; result.Add(target); continue; }
                target.Shapes.AddRange(surface.Shapes(target.Triangle, target.Barycentric, follow.rotate));
                if (target.Shapes.Count == 0) target.Skipped = "no body blendshape moves the skin here";
                result.Add(target);
            }
            return result;
        }

        private static IEnumerable<Renderer> Candidates(OrbitersSurfaceFollow follow, SkinnedMeshRenderer body)
        {
            foreach (var renderer in follow.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == body || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                var mesh = MeshOf(renderer);
                if (mesh == null || mesh.vertexCount == 0) continue;
                // Rigid pieces only: a mesh skinned to many bones is clothing, which bends with the body on its own.
                if (renderer is SkinnedMeshRenderer skinned && skinned.bones.Where(b => b != null).Distinct().Count() > 2) continue;
                if (follow.scope == OrbitersSurfaceFollow.Scope.SmallAccessories && renderer.bounds.size.magnitude > follow.maximumSize) continue;
                yield return renderer;
            }
        }

        private static Mesh MeshOf(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
        }

        // ---- Build ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Build copy, after VRCFury built its controllers: adds the follow blendshapes and links them to the body.
        /// Returns a message for the log, or null when the avatar has nothing to follow.
        /// </summary>
        public static string Apply(GameObject avatarRoot)
        {
            var follows = avatarRoot.GetComponentsInChildren<OrbitersSurfaceFollow>(true);
            if (follows.Length == 0) return null;
            var animated = AnimatedRenderers(avatarRoot);
            var copies = new List<BlendShapeCopy>();
            int followed = 0, skipped = 0;
            var done = new HashSet<Renderer>();
            foreach (var follow in follows)
            {
                var body = BodyOf(follow);
                foreach (var target in Plan(follow))
                {
                    if (!done.Add(target.Renderer)) continue;
                    if (target.Skipped != null) { skipped++; continue; }
                    string path = AnimationUtility.CalculateTransformPath(target.Renderer.transform, avatarRoot.transform);
                    if (target.Renderer is MeshRenderer && animated.Contains(path))
                    {
                        // Replacing an animated renderer would break its animations (material swaps, enabled...).
                        skipped++;
                        Debug.LogWarning("[Orbiters] " + target.Renderer.name + " does not follow the body: its renderer is animated.", target.Renderer);
                        continue;
                    }
                    var skinned = Bake(target, body);
                    foreach (var shape in target.Shapes.Select(s => s.Name).Distinct())
                        if (skinned.sharedMesh.GetBlendShapeIndex(shape) >= 0)
                            copies.Add(new BlendShapeCopy { Source = body, SourceShape = shape, Destination = skinned, DestinationShape = shape });
                    followed++;
                }
            }
            foreach (var follow in follows) Object.DestroyImmediate(follow);
            if (followed == 0) return "Follow body blendshapes: nothing to follow (" + skipped + " left as is).";
            var sync = BlendShapeSync.Apply(avatarRoot, copies, "Follow body blendshapes");
            return "Follow body blendshapes: " + followed + " accessor" + (followed == 1 ? "y" : "ies") + " follow the body" + (skipped > 0 ? ", " + skipped + " left as is" : "") + ". " + sync.Message;
        }

        // Renderer paths some animation of the avatar drives (enabled, materials...): those keep their component.
        private static HashSet<string> AnimatedRenderers(GameObject avatarRoot)
        {
            var paths = new HashSet<string>();
            var controllers = new HashSet<RuntimeAnimatorController>(BlendShapeLinkEngine.CollectBuiltControllers(avatarRoot));
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
                foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
                    if (layer.animatorController != null) controllers.Add(layer.animatorController);
            foreach (var clip in controllers.SelectMany(c => c.animationClips).Where(c => c != null).Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    if (binding.type == typeof(MeshRenderer) || binding.type == typeof(Renderer)) paths.Add(binding.path);
            }
            return paths;
        }

        /// <summary>The accessory as a skinned mesh with one blendshape per body shape that moves it (build copy).</summary>
        public static SkinnedMeshRenderer Bake(FollowTarget target, SkinnedMeshRenderer body)
        {
            var renderer = target.Renderer;
            float size = renderer.bounds.extents.magnitude;
            var skinned = renderer as SkinnedMeshRenderer ?? Convert((MeshRenderer)renderer);
            var mesh = Object.Instantiate(skinned.sharedMesh);
            mesh.name = skinned.sharedMesh.name + " (follows body)";
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            var toWorld = SkinMatrices(skinned, mesh);
            var fromWorld = toWorld.Select(m => m.inverse).ToArray();
            var world = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) world[i] = toWorld[i].MultiplyPoint3x4(vertices[i]);

            var bodyMesh = body.sharedMesh;
            var compensation = new Vector3[vertices.Length];
            foreach (var group in target.Shapes.GroupBy(s => s.Name))
            {
                if (mesh.GetBlendShapeIndex(group.Key) >= 0) continue;
                int bodyIndex = bodyMesh.GetBlendShapeIndex(group.Key);
                float current = bodyIndex >= 0 ? body.GetBlendShapeWeight(bodyIndex) : 0f;
                var frames = group.OrderBy(s => s.FrameWeight).ToList();
                foreach (var shape in frames)
                {
                    var deltas = new Vector3[vertices.Length];
                    var normalDeltas = hasNormals ? new Vector3[vertices.Length] : null;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        // The accessory moves rigidly with the skin: turned around the anchor, then carried along.
                        Vector3 moved = target.Anchor + shape.Move + shape.Turn * (world[i] - target.Anchor);
                        deltas[i] = fromWorld[i].MultiplyVector(moved - world[i]);
                        if (hasNormals)
                        {
                            var n = toWorld[i].MultiplyVector(normals[i]).normalized;
                            normalDeltas[i] = fromWorld[i].MultiplyVector(shape.Turn * n - n);
                        }
                    }
                    mesh.AddBlendShapeFrame(group.Key, shape.FrameWeight, deltas, normalDeltas, null);
                    // The accessory sits where the body's current weights put it: its rest shape is that place minus them.
                    if (shape.FrameWeight == frames[frames.Count - 1].FrameWeight && current != 0f)
                        for (int i = 0; i < vertices.Length; i++) compensation[i] += deltas[i] * (current / shape.FrameWeight);
                }
            }
            for (int i = 0; i < vertices.Length; i++) vertices[i] -= compensation[i];
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
            skinned.sharedMesh = mesh;
            // Room for the largest movement, so the accessory is never culled while the body changes.
            float reach = target.Shapes.Count == 0 ? 0f : target.Shapes.Max(s => s.Move.magnitude) + size * 0.2f;
            var bounds = skinned.localBounds;
            bounds.Expand(reach * 2f / Mathf.Max(0.0001f, skinned.transform.lossyScale.x));
            skinned.localBounds = bounds;
            return skinned;
        }

        // Each vertex's mesh-to-world matrix, as skinning computes it at the current pose.
        private static Matrix4x4[] SkinMatrices(SkinnedMeshRenderer skinned, Mesh mesh)
        {
            var result = new Matrix4x4[mesh.vertexCount];
            var bones = skinned.bones;
            var bindposes = mesh.bindposes;
            var weights = mesh.boneWeights;
            if (bones.Length == 0 || bindposes.Length == 0 || weights.Length != mesh.vertexCount)
            {
                for (int i = 0; i < result.Length; i++) result[i] = skinned.transform.localToWorldMatrix;
                return result;
            }
            var bone = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++) bone[b] = bones[b] != null && b < bindposes.Length ? bones[b].localToWorldMatrix * bindposes[b] : skinned.transform.localToWorldMatrix;
            for (int i = 0; i < result.Length; i++)
            {
                var w = weights[i];
                result[i] = Blend(bone, w);
            }
            return result;
        }

        internal static Matrix4x4 Blend(Matrix4x4[] bone, BoneWeight w)
        {
            var m = new Matrix4x4();
            void Add(int index, float weight)
            {
                if (weight <= 0f || index < 0 || index >= bone.Length) return;
                for (int k = 0; k < 16; k++) m[k] += bone[index][k] * weight;
            }
            Add(w.boneIndex0, w.weight0);
            Add(w.boneIndex1, w.weight1);
            Add(w.boneIndex2, w.weight2);
            Add(w.boneIndex3, w.weight3);
            return m;
        }

        // A plain mesh becomes a skinned mesh bound to its own object: it draws exactly as before, and can take blendshapes.
        private static SkinnedMeshRenderer Convert(MeshRenderer renderer)
        {
            var go = renderer.gameObject;
            var filter = go.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            mesh.name = filter.sharedMesh.name;
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
            var materials = renderer.sharedMaterials;
            var shadows = renderer.shadowCastingMode;
            bool receive = renderer.receiveShadows;
            var probes = renderer.lightProbeUsage;
            var reflections = renderer.reflectionProbeUsage;
            var anchor = renderer.probeAnchor;
            bool enabled = renderer.enabled;
            Object.DestroyImmediate(renderer);
            Object.DestroyImmediate(filter);
            var skinned = go.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = mesh;
            skinned.bones = new[] { go.transform };
            skinned.rootBone = go.transform;
            skinned.sharedMaterials = materials;
            skinned.shadowCastingMode = shadows;
            skinned.receiveShadows = receive;
            skinned.lightProbeUsage = probes;
            skinned.reflectionProbeUsage = reflections;
            skinned.probeAnchor = anchor;
            skinned.enabled = enabled;
            skinned.localBounds = mesh.bounds;
            return skinned;
        }

        // ---- The body surface -----------------------------------------------------------------------------------------------

        /// <summary>The body at its current pose and weights: closest triangles, and what each blendshape does to them.</summary>
        private sealed class BodySurface
        {
            private readonly SkinnedMeshRenderer body;
            private readonly Mesh mesh;
            private readonly Vector3[] world;
            private readonly int[] triangles;
            private Matrix4x4[] bone;
            private BoneWeight[] weights;

            public BodySurface(SkinnedMeshRenderer body)
            {
                this.body = body;
                mesh = body.sharedMesh;
                var baked = new Mesh();
                body.BakeMesh(baked, true);
                var local = baked.vertices;
                Object.DestroyImmediate(baked);
                world = new Vector3[local.Length];
                var toWorld = body.transform.localToWorldMatrix;
                for (int i = 0; i < local.Length; i++) world[i] = toWorld.MultiplyPoint3x4(local[i]);
                triangles = mesh.triangles;
            }

            public void Closest(Vector3 point, out int triangle, out Vector3 barycentric, out float distance)
            {
                triangle = -1;
                barycentric = Vector3.zero;
                float best = float.MaxValue;
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    var closest = ClosestOnTriangle(point, world[triangles[t]], world[triangles[t + 1]], world[triangles[t + 2]], out var bary);
                    float d = (closest - point).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    triangle = t;
                    barycentric = bary;
                }
                distance = triangle < 0 ? float.MaxValue : Mathf.Sqrt(best);
            }

            public IEnumerable<FollowShape> Shapes(int triangle, Vector3 bary, bool rotate)
            {
                int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
                var skin = new[] { SkinAt(a), SkinAt(b), SkinAt(c) };
                Vector3 p0 = world[a], p1 = world[b], p2 = world[c];
                var before = Frame(p0, p1, p2);
                var deltas = new Vector3[mesh.vertexCount];
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    for (int f = 0; f < frames; f++)
                    {
                        mesh.GetBlendShapeFrameVertices(s, f, deltas, null, null);
                        // Skinning is linear: an offset in the mesh moves by the same blend of bone matrices as the point.
                        Vector3 d0 = skin[0].MultiplyVector(deltas[a]), d1 = skin[1].MultiplyVector(deltas[b]), d2 = skin[2].MultiplyVector(deltas[c]);
                        var move = d0 * bary.x + d1 * bary.y + d2 * bary.z;
                        var turn = rotate ? Frame(p0 + d0, p1 + d1, p2 + d2) * Quaternion.Inverse(before) : Quaternion.identity;
                        float degrees = Quaternion.Angle(Quaternion.identity, turn);
                        if (move.magnitude < MinimumMove && degrees < MinimumTurn) continue;
                        yield return new FollowShape { Name = mesh.GetBlendShapeName(s), Frame = f, FrameWeight = mesh.GetBlendShapeFrameWeight(s, f), Move = move, Turn = turn, TurnDegrees = degrees };
                    }
                }
            }

            private Matrix4x4 SkinAt(int vertex)
            {
                if (bone == null)
                {
                    var bones = body.bones;
                    var bindposes = mesh.bindposes;
                    bone = new Matrix4x4[bones.Length];
                    for (int i = 0; i < bones.Length; i++) bone[i] = bones[i] != null && i < bindposes.Length ? bones[i].localToWorldMatrix * bindposes[i] : body.transform.localToWorldMatrix;
                    weights = mesh.boneWeights;
                }
                return bone.Length == 0 || weights.Length <= vertex ? body.transform.localToWorldMatrix : Blend(bone, weights[vertex]);
            }

            private static Quaternion Frame(Vector3 a, Vector3 b, Vector3 c)
            {
                var normal = Vector3.Cross(b - a, c - a);
                var edge = b - a;
                if (normal.sqrMagnitude < 1e-16f || edge.sqrMagnitude < 1e-16f) return Quaternion.identity;
                return Quaternion.LookRotation(normal.normalized, edge.normalized);
            }
        }

        // The closest point of a triangle, with its barycentric coordinates (Ericson, Real-Time Collision Detection 5.1.5).
        internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float v = d1 / (d1 - d3); bary = new Vector3(1 - v, v, 0); return a + ab * v; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); bary = new Vector3(1 - w, 0, w); return a + ac * w; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bary = new Vector3(0, 1 - w, w); return b + (c - b) * w; }
            float denominator = 1f / (va + vb + vc);
            float bv = vb * denominator, cw = vc * denominator;
            bary = new Vector3(1 - bv - cw, bv, cw);
            return a + ab * bv + ac * cw;
        }
    }

    internal sealed class SurfaceFollowHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000) and MCB's blendshape links (-9000), before My Avatar's attachments finish (-8900).
        public int callbackOrder => -8950;
        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            string message = SurfaceFollow.Apply(avatarRoot);
            if (message != null) Debug.Log("[Orbiters] " + message);
            return true;
        }
    }
}
