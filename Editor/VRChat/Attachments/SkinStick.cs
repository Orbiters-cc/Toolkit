using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.Meshes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>The spot of the avatar's skin a small accessory sits on, and the bones that move the skin there.</summary>
    public sealed class SkinSpot
    {
        public SkinnedMeshRenderer Skin;
        public int Triangle;
        public Vector3 Barycentric, Point;
        /// <summary>From the accessory's closest vertex to the skin, in metres.</summary>
        public float Gap;
        /// <summary>The bones moving the skin there, heaviest first; their weights add up to 1.</summary>
        public Transform[] Bones;
        public float[] Weights;
    }

    /// <summary>
    /// Stick to the skin: a small accessory on the skin (a piercing, a stud, a gem) moves with the skin under it instead of
    /// one bone, the way Haï's Skinned Mesh Constraint makes it: the bones of the closest skin triangle, blended by their
    /// weights there, hold it through a VRChat Parent Constraint on the build copy, and the body's blendshapes move it too
    /// (<see cref="SurfaceFollow.SurfaceFollow"/>). Nothing changes in the scene; the posing preview blends the same bones.
    /// </summary>
    public static class SkinStick
    {
        public const string Label = "Stick to the skin";
        // Of the avatar's height: the largest accessory offered (16 cm at 1.6 m) and how far its closest part may be from
        // the skin (2.4 cm).
        private const float SizeOfHeight = .1f, GapOfHeight = .015f, FallbackHeight = 1.6f;
        // The accessory's points that count as touching the skin: up to this much farther than the closest one.
        private const float ContactBand = .003f;
        private const int MaximumBones = 8, MaximumSamples = 4000;
        private const float MinimumWeight = .01f;

        private sealed class Surface
        {
            public long Signature;
            public MeshBvh Bvh;
            public Bounds Bounds;
            public BoneWeight[] Weights;
        }

        private sealed class CachedSpot
        {
            public long Signature;
            public SkinSpot Spot;
            public string Reason;
        }

        // The skins at their last pose and the accessories' spots, measured again when anything they depend on moves.
        private static readonly Dictionary<SkinnedMeshRenderer, Surface> Surfaces = new Dictionary<SkinnedMeshRenderer, Surface>();
        private static readonly Dictionary<OrbitersAttachment, CachedSpot> Spots = new Dictionary<OrbitersAttachment, CachedSpot>();

        [InitializeOnLoadMethod]
        private static void Register() => AttachmentHooks.RegisterOptions(root =>
        {
            var attachment = root != null ? root.GetComponent<OrbitersAttachment>() : null;
            if (attachment == null) return null;
            var spot = Find(attachment, out string reason);
            // Once on, the switch stays to turn it off, also when the accessory was moved off the skin since.
            if (spot == null && !attachment.stickToSkin) return null;
            string bone = attachment.mode == OrbitersAttachment.AttachMode.Parent && attachment.parent != null ? attachment.parent.name : "its bone";
            return new[]
            {
                new AttachmentOption
                {
                    Label = Label,
                    Tooltip = spot != null
                        ? "Moves with the skin under it instead of " + bone + " alone: blended between the bones moving the skin there (" + Describe(spot) +
                          "), and with the body's blendshapes there. VRChat counts one constraint."
                        : "It is no longer on the skin (" + reason + "): it follows " + bone + " as a whole until it is put back on the skin.",
                    IsToggle = true,
                    Choices = new[] { "Off", "On" },
                    ChoiceTooltips = new[] { "Follows " + bone + " as a whole", "Moves with the skin under it" },
                    Current = attachment.stickToSkin ? 1 : 0,
                    Choose = index => Set(attachment, index == 1),
                },
            };
        });

        public static void Set(OrbitersAttachment attachment, bool on)
        {
            if (attachment == null || attachment.stickToSkin == on) return;
            Undo.RecordObject(attachment, on ? "Stick to the skin" : "Unstick from the skin");
            attachment.stickToSkin = on;
            EditorUtility.SetDirty(attachment);
            PrefabUtility.RecordPrefabInstancePropertyModifications(attachment);
        }

        /// <summary>The bones holding the spot with their share, heaviest first ("Head 64 % · Neck 36 %").</summary>
        public static string Describe(SkinSpot spot) =>
            spot == null ? null : string.Join(" · ", spot.Bones.Select((b, i) => b.name + " " + Mathf.RoundToInt(spot.Weights[i] * 100f) + " %"));

        /// <summary>
        /// The spot of skin the accessory sits on, or null with why when it is not a small accessory on the skin. Measured at
        /// the current pose, kept until the accessory, a skin or a bone moves.
        /// </summary>
        public static SkinSpot Find(OrbitersAttachment attachment, out string reason)
        {
            reason = null;
            if (attachment == null) { reason = "it was removed"; return null; }
            var avatarRoot = AvatarRoot(attachment.transform);
            var holder = Holder(attachment, out reason);
            if (holder == null) return null;
            var skins = Skins(avatarRoot);
            long signature = Signature(attachment.transform);
            foreach (var skin in skins) signature = signature * 1099511628211L ^ Signature(skin);
            if (Spots.TryGetValue(attachment, out var cached) && cached.Signature == signature) { reason = cached.Reason; return cached.Spot; }
            foreach (var gone in Spots.Keys.Where(a => a == null).ToList()) Spots.Remove(gone);
            var spot = Measure(attachment, avatarRoot, skins, out reason);
            Spots[attachment] = new CachedSpot { Signature = signature, Spot = spot, Reason = reason };
            return spot;
        }

        /// <summary>
        /// The object a stuck accessory's constraint goes on: the accessory itself when My Avatar puts it on a bone, or the
        /// object its creator's VRCFury Armature Link carries where it stands. Null with why for clothing and other setups.
        /// </summary>
        public static Transform Holder(OrbitersAttachment attachment, out string reason)
        {
            reason = null;
            Transform holder = null;
            switch (attachment.mode)
            {
                case OrbitersAttachment.AttachMode.Parent:
                    holder = attachment.parent != null ? attachment.transform : null;
                    if (holder == null) reason = "it needs a bone";
                    break;
                case OrbitersAttachment.AttachMode.Configured:
                    var links = VrcFury.ArmatureLinks(attachment.gameObject).ToList();
                    if (links.Count != 1 || links[0].From == null || links[0].Recursive) { reason = "its creator's setup links it to several bones"; return null; }
                    if (links[0].AlignPosition || links[0].AlignRotation) { reason = "its creator's link snaps it onto a bone"; return null; }
                    if (attachment.GetComponentsInChildren<Component>(true).Any(ModularAvatar.IsComponent)) { reason = "Modular Avatar places it"; return null; }
                    holder = links[0].From.transform;
                    break;
                default:
                    reason = "it is clothing, following several bones already";
                    return null;
            }
            if (holder == null) return null;
            if (holder.GetComponents<Component>().Any(c => c is VRCConstraintBase || c is UnityEngine.Animations.IConstraint))
            {
                reason = "a constraint already holds it";
                return null;
            }
            // Skinned to bones outside it (the avatar's): it bends with the body by itself.
            if (attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.bones.Any(b => b != null && !b.IsChildOf(attachment.transform))))
            {
                reason = "it is skinned to the avatar's bones";
                return null;
            }
            return holder;
        }

        private static SkinSpot Measure(OrbitersAttachment attachment, Transform avatarRoot, List<SkinnedMeshRenderer> skins, out string reason)
        {
            reason = null;
            var points = Points(attachment);
            if (points.Count == 0) { reason = "it has no mesh"; return null; }
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var p in points) bounds.Encapsulate(p);
            float height = Height(avatarRoot);
            if (bounds.size.magnitude > SizeOfHeight * height) { reason = "it is larger than a small accessory (" + Centimetres(bounds.size.magnitude) + ")"; return null; }
            float reach = GapOfHeight * height;
            // Each point's closest skin, to find the part touching it.
            var closest = new (float distance, SkinnedMeshRenderer skin, MeshBvh.Hit hit)[points.Count];
            float best = float.MaxValue;
            for (int i = 0; i < points.Count; i++) closest[i].distance = float.MaxValue;
            foreach (var skin in skins)
            {
                var surface = SurfaceOf(skin);
                if (surface == null) continue;
                var near = surface.Bounds; near.Expand(reach * 2f);
                if (!near.Intersects(bounds)) continue;
                for (int i = 0; i < points.Count; i++)
                {
                    var hit = surface.Bvh.ClosestPoint(points[i], Mathf.Min(closest[i].distance, reach + ContactBand));
                    if (!hit.Found || hit.Distance >= closest[i].distance) continue;
                    closest[i] = (hit.Distance, skin, hit);
                    best = Mathf.Min(best, hit.Distance);
                }
            }
            if (best > reach) { reason = "it is " + Centimetres(reach) + " or more from the skin"; return null; }
            // The skin under the touching part: the mean of its points' closest skin points, on the skin closest overall.
            var contact = closest.Where(c => c.skin != null && c.distance <= best + ContactBand).ToList();
            var main = contact.GroupBy(c => c.skin).OrderByDescending(g => g.Count()).First().Key;
            var centre = Vector3.zero;
            int count = 0;
            foreach (var c in contact.Where(c => c.skin == main)) { centre += c.hit.Position; count++; }
            centre /= count;
            var mainSurface = SurfaceOf(main);
            var at = mainSurface.Bvh.ClosestPoint(centre, float.MaxValue);
            if (!at.Found) { reason = "no skin found under it"; return null; }
            var spot = new SkinSpot { Skin = main, Triangle = at.Triangle, Barycentric = at.Bary, Point = at.Position, Gap = best };
            if (!Blend(main, mainSurface, at.Triangle, at.Bary, out spot.Bones, out spot.Weights)) { reason = "the skin there follows no bone"; return null; }
            return spot;
        }

        // The skin triangle's bone weights, blended at the spot: the bones and shares a point of skin there moves by.
        private static bool Blend(SkinnedMeshRenderer skin, Surface surface, int triangle, Vector3 bary, out Transform[] bones, out float[] weights)
        {
            var shares = new Dictionary<Transform, float>();
            var skinBones = skin.bones;
            var corners = surface.Bvh.Triangles;
            for (int k = 0; k < 3; k++)
            {
                int vertex = corners[triangle * 3 + k];
                if (vertex >= surface.Weights.Length) continue;
                var w = surface.Weights[vertex];
                Add(w.boneIndex0, w.weight0 * bary[k]);
                Add(w.boneIndex1, w.weight1 * bary[k]);
                Add(w.boneIndex2, w.weight2 * bary[k]);
                Add(w.boneIndex3, w.weight3 * bary[k]);
            }
            void Add(int index, float weight)
            {
                if (weight <= 0f || index < 0 || index >= skinBones.Length || skinBones[index] == null) return;
                shares.TryGetValue(skinBones[index], out float sum);
                shares[skinBones[index]] = sum + weight;
            }
            var kept = shares.Where(s => s.Value >= MinimumWeight).OrderByDescending(s => s.Value).Take(MaximumBones).ToList();
            float total = kept.Sum(s => s.Value);
            bones = kept.Select(s => s.Key).ToArray();
            weights = kept.Select(s => s.Value / total).ToArray();
            return bones.Length > 0 && total > 0f;
        }

        /// <summary>
        /// The posing preview's blend for a stuck accessory placed at <paramref name="placed"/>: each bone of its skin spot,
        /// its share and the offset that keeps it there. Null when it is not on the skin.
        /// </summary>
        public static FollowBlend[] Blend(OrbitersAttachment attachment, Matrix4x4 placed)
        {
            if (attachment == null || !attachment.stickToSkin) return null;
            var spot = Find(attachment, out _);
            if (spot == null) return null;
            return spot.Bones.Select((bone, i) => new FollowBlend { Target = bone, Weight = spot.Weights[i], Offset = Frame(bone).inverse * placed }).ToArray();
        }

        // ---- Build ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Build copy, once VRCFury and My Avatar placed the accessories: each stuck accessory gets a VRChat Parent Constraint
        /// on its skin spot's bones that keeps it where it stands, and follows the body's blendshapes there. Returns a message
        /// for the log, or null when nothing sticks.
        /// </summary>
        public static string Apply(GameObject avatarRoot)
        {
            var stuck = avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true).Where(a => a.stickToSkin).ToList();
            if (stuck.Count == 0) return null;
            Surfaces.Clear();
            Spots.Clear();
            int done = 0;
            foreach (var attachment in stuck)
            {
                var spot = Find(attachment, out string reason);
                var holder = spot != null ? Holder(attachment, out reason) : null;
                if (holder == null)
                {
                    Debug.LogWarning("[Orbiters] " + attachment.DisplayName + " does not stick to the skin: " + reason + ". It follows its bone.", attachment);
                    continue;
                }
                Constrain(holder, spot);
                if (attachment.GetComponent<OrbitersSurfaceFollow>() == null)
                {
                    var follow = attachment.gameObject.AddComponent<OrbitersSurfaceFollow>();
                    follow.scope = OrbitersSurfaceFollow.Scope.ThisObject;
                    follow.body = spot.Skin;
                    follow.rotate = true;
                }
                done++;
            }
            Surfaces.Clear();
            Spots.Clear();
            return done == 0 ? null : "Stick to the skin: " + done + " accessor" + (done == 1 ? "y follows" : "ies follow") + " the skin under " + (done == 1 ? "it" : "them") + ".";
        }

        /// <summary>A VRChat Parent Constraint on the spot's bones, by their shares, holding <paramref name="holder"/> where it stands.</summary>
        public static VRCParentConstraint Constrain(Transform holder, SkinSpot spot)
        {
            var constraint = holder.gameObject.AddComponent<VRCParentConstraint>();
            for (int i = 0; i < spot.Bones.Length; i++)
            {
                var bone = spot.Bones[i];
                // From the source's position and rotation, without its scale, as the constraint applies offsets; set here
                // rather than by Activate, so the accessory sits where the skin's blend of these bones puts it.
                var position = Frame(bone).inverse.MultiplyPoint3x4(holder.position);
                var rotation = (Quaternion.Inverse(bone.rotation) * holder.rotation).eulerAngles;
                constraint.Sources.Add(new VRCConstraintSource(bone, spot.Weights[i], position, rotation));
            }
            constraint.PositionAtRest = holder.localPosition;
            constraint.RotationAtRest = holder.localRotation.eulerAngles;
            constraint.IsActive = true;
            constraint.Locked = true;
            return constraint;
        }

        /// <summary>A bone's position and rotation without its scale: what a parent constraint's offsets are measured from.</summary>
        public static Matrix4x4 Frame(Transform bone) => Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one);

        // ---- What it measures --------------------------------------------------------------------------------------------

        private static Transform AvatarRoot(Transform t)
        {
            var descriptor = t.GetComponentInParent<VRCAvatarDescriptor>(true);
            return descriptor != null ? descriptor.transform : t.root;
        }

        // The avatar's own skins: meshes skinned to its skeleton, outside every accessory.
        private static List<SkinnedMeshRenderer> Skins(Transform avatarRoot)
        {
            var skeleton = AvatarSkeleton.Bones(avatarRoot, AttachmentPlanner.Body(avatarRoot));
            return avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.GetComponentInParent<OrbitersAttachment>(true) == null &&
                            r.bones.Any(b => b != null && skeleton.Contains(b)))
                .ToList();
        }

        // From the eyes when the descriptor says where they are (about 93 % of the height), else the body's bounds.
        private static float Height(Transform avatarRoot)
        {
            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null && descriptor.ViewPosition.y > .1f) return descriptor.ViewPosition.y * avatarRoot.lossyScale.y / .93f;
            var body = AttachmentPlanner.Body(avatarRoot);
            float height = body != null ? body.bounds.size.y : 0f;
            return height > .05f ? height : FallbackHeight;
        }

        // The accessory's mesh vertices in the world, at most a few thousand of them evenly picked.
        private static List<Vector3> Points(OrbitersAttachment attachment)
        {
            var result = new List<Vector3>();
            var meshes = new List<(Vector3[] vertices, Matrix4x4 toWorld)>();
            foreach (var renderer in attachment.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    var baked = new Mesh();
                    skinned.BakeMesh(baked, true);
                    meshes.Add((baked.vertices, skinned.transform.localToWorldMatrix));
                    Object.DestroyImmediate(baked);
                }
                else if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    meshes.Add((filter.sharedMesh.vertices, renderer.transform.localToWorldMatrix));
            }
            int total = meshes.Sum(m => m.vertices.Length);
            int step = Mathf.Max(1, Mathf.CeilToInt(total / (float)MaximumSamples));
            foreach (var (vertices, toWorld) in meshes)
                for (int i = 0; i < vertices.Length; i += step) result.Add(toWorld.MultiplyPoint3x4(vertices[i]));
            return result;
        }

        // A skin at its current pose and blendshape weights, with a hierarchy of its triangles in the world.
        private static Surface SurfaceOf(SkinnedMeshRenderer skin)
        {
            long signature = Signature(skin);
            if (Surfaces.TryGetValue(skin, out var surface) && surface.Signature == signature) return surface;
            foreach (var gone in Surfaces.Keys.Where(s => s == null).ToList()) Surfaces.Remove(gone);
            var mesh = skin.sharedMesh;
            var triangles = mesh.triangles;
            if (triangles.Length < 3) return null;
            var baked = new Mesh();
            skin.BakeMesh(baked, true);
            var world = baked.vertices;
            Object.DestroyImmediate(baked);
            var toWorld = skin.transform.localToWorldMatrix;
            var bounds = new Bounds(world.Length > 0 ? toWorld.MultiplyPoint3x4(world[0]) : Vector3.zero, Vector3.zero);
            for (int i = 0; i < world.Length; i++) { world[i] = toWorld.MultiplyPoint3x4(world[i]); bounds.Encapsulate(world[i]); }
            surface = new Surface { Signature = signature, Bvh = MeshBvh.Build(world, triangles), Bounds = bounds, Weights = mesh.boneWeights };
            Surfaces[skin] = surface;
            return surface;
        }

        // Changes when the skin's mesh, pose (its bones) or blendshape weights do.
        private static long Signature(SkinnedMeshRenderer skin)
        {
            long hash = Signature(skin.transform) ^ (skin.sharedMesh != null ? skin.sharedMesh.GetInstanceID() : 0);
            foreach (var bone in skin.bones) hash = Mix(hash, bone != null ? bone.localToWorldMatrix : Matrix4x4.zero);
            var mesh = skin.sharedMesh;
            for (int i = 0; mesh != null && i < mesh.blendShapeCount; i++) hash = hash * 1099511628211L ^ BitConverter.SingleToInt32Bits(skin.GetBlendShapeWeight(i));
            return hash;
        }

        // Changes when the object or anything below it moves, or its meshes change.
        private static long Signature(Transform root)
        {
            long hash = -3750763034362895579L;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) hash = Mix(hash, t.localToWorldMatrix);
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) hash = hash * 1099511628211L ^ (filter.sharedMesh != null ? filter.sharedMesh.GetInstanceID() : 0);
            return hash;
        }

        private static long Mix(long hash, Matrix4x4 m)
        {
            for (int k = 0; k < 16; k++) hash = hash * 1099511628211L ^ BitConverter.SingleToInt32Bits(m[k]);
            return hash;
        }

        private static string Centimetres(float metres) => (metres * 100f).ToString(metres < .1f ? "0.0" : "0") + " cm";
    }

    internal sealed class SkinStickHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000) placed the accessories and MCB's mode locks (-8970); before body blendshapes are followed
        // (-8950), which it asks for, and before My Avatar's attachments finish (-8900).
        public int callbackOrder => -8960;
        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            string message = SkinStick.Apply(avatarRoot);
            if (message != null) Debug.Log("[Orbiters] " + message);
            return true;
        }
    }
}
