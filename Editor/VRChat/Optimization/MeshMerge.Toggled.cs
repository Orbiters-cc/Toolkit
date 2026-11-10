using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    public static partial class MeshMerge
    {
        /// <summary>The name of a blendshape that hides a toggled part of a merged mesh starts with this.</summary>
        public const string HiddenShapePrefix = "Orbiters hidden ";
        /// <summary>The name of a bone that hides a toggled part of a merged mesh starts with this.</summary>
        public const string HideBonePrefix = "Orbiters hide ";

        /// <summary>
        /// When merging meshes toggled on their own is worth what it costs (<see cref="MergeToggled"/>). A mesh fewer only
        /// counts when it gives a better rating in a category VRChat ranks (skinned meshes, meshes). A part hidden by a
        /// blendshape adds that blendshape to the download, and Unity evaluates it while the part is hidden. A part hidden by
        /// a bone adds that bone, which must not give the bones a worse rating.
        /// </summary>
        public sealed class ToggledPolicy
        {
            /// <summary>Each rating's limit, Excellent to Poor: skinned meshes, meshes and bones (the SDK's, for the platform built).</summary>
            public int[] SkinnedLimits, MeshLimits, BoneLimits;
            /// <summary>What a blendshape moving this many vertices adds to the download (My Avatar's download estimate).</summary>
            public Func<int, long> CollapseBytes = DefaultCollapseBytes;
            /// <summary>
            /// The most one toggle's blendshape may add to the download, and all of them together: on PC 256 KB and 1 MB
            /// (about 32,000 and 130,000 vertices, 1.3 and 5 MB of video memory, half a percent of PC's 200 MB download limit at
            /// most); on mobile 64 KB and 256 KB (2.5% of its 10 MB limit). A hidden part's blendshape costs about 40 bytes of
            /// video memory and, on the CPU path, 2 to 11 ns per vertex each frame (measured 2026-11-02).
            /// </summary>
            public long MaxBytes = PcMaxBytes, MaxTotalBytes = PcMaxTotalBytes;

            public const long PcMaxBytes = 256 * 1024, PcMaxTotalBytes = 1024 * 1024, MobileMaxBytes = 64 * 1024, MobileMaxTotalBytes = 256 * 1024;

            /// <summary>A blendshape's stored offsets (40 bytes per vertex it moves and 64 per frame) as compressed in a bundle.</summary>
            public static long DefaultCollapseBytes(int moved) => (long)((moved * 40L + 64) * .2f);

            /// <summary>The SDK's limits for PC or mobile; null until the SDK has loaded them (<see cref="AvatarBudget.Ready"/>).</summary>
            public static ToggledPolicy For(bool mobile, Func<int, long> collapseBytes = null)
            {
                if (!AvatarBudget.Ready) return null;
                int[] Limits(Func<AvatarPerformanceStatsLevel, int> stat) => AvatarBudget.Limits(stat, mobile).Select(l => l.limit).ToArray();
                return new ToggledPolicy
                {
                    SkinnedLimits = Limits(l => l.skinnedMeshCount), MeshLimits = Limits(l => l.meshCount), BoneLimits = Limits(l => l.boneCount),
                    CollapseBytes = collapseBytes ?? DefaultCollapseBytes,
                    MaxBytes = mobile ? MobileMaxBytes : PcMaxBytes, MaxTotalBytes = mobile ? MobileMaxTotalBytes : PcMaxTotalBytes,
                };
            }

            // The rating's index a count gets (0 Excellent ... 3 Poor, 4 Very Poor).
            internal static int Rate(int count, int[] limits)
            {
                if (limits == null) return 0;
                for (int i = 0; i < limits.Length; i++) if (count <= limits[i]) return i;
                return limits.Length;
            }
        }

        // What shows and hides a renderer: an object's m_IsActive, or the renderer's own m_Enabled.
        private sealed class Toggle
        {
            public Transform Holder;
            public Renderer Switch;
            public string Path;
            public bool ShownAtRest;
            /// <summary>The objects the animations setting it play from.</summary>
            public readonly HashSet<Transform> Roots = new HashSet<Transform>();
            public string Name => Switch != null ? Switch.name : Holder.name;

            public bool Is(EditorCurveBinding binding, string pathFromRoot) =>
                pathFromRoot == Path && (Switch == null
                    ? binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive"
                    : binding.propertyName == "m_Enabled" && binding.type != null && binding.type.IsAssignableFrom(Switch.GetType()));
        }

        private sealed class Member
        {
            public Renderer Renderer;
            public Toggle Toggle;
            /// <summary>Where a hide bone can follow the part's one bone from (and nothing hides the bone itself), else null.</summary>
            public Transform Anchor;
            public int Vertices;
            public bool Skinned => Renderer is SkinnedMeshRenderer;
        }

        // The members of one toggle that one merged renderer takes in, hidden one way.
        private sealed class Unit
        {
            public Toggle Toggle;
            public SkinnedMeshRenderer Target;
            public readonly List<Member> Members = new List<Member>();
            public bool Bones;
            public int AddedBones;
            public long Bytes;
            public string Shape;
            public int Skinned => Members.Count(m => m.Skinned);
            public int Props => Members.Count - Skinned;
            public int Vertices => Members.Sum(m => m.Vertices);

            public void Price(ToggledPolicy policy)
            {
                AddedBones = Bones ? Members.Select(m => m.Anchor).Distinct().Count() : 0;
                // A bone's bind pose; a blendshape its offsets.
                Bytes = Bones ? AddedBones * 64L : policy.CollapseBytes(Vertices);
            }
        }

        /// <summary>
        /// Merges skinned meshes and props shown and hidden by toggles of their own into a skinned mesh that is always shown
        /// and drawn the same way, when that gives the skinned meshes or the meshes a better rating (<paramref name="policy"/>;
        /// nothing merges without one). Each toggle keeps working: where it hid the mesh, it now hides the mesh's part of the
        /// merged one.
        /// <list type="bullet">
        /// <item>A part whose vertices all follow one bone (a rigid accessory, a prop) follows a new bone under it instead,
        /// scaled to nothing while hidden: exact in every pose, one bone more.</item>
        /// <item>Any other part gets a blendshape that folds it onto the origin of the bone it follows most: faces on one or
        /// two bones go flat in every pose, faces between three keep a sliver there. It adds that blendshape to the download,
        /// and Unity evaluates it while the part is hidden.</item>
        /// </list>
        /// The animations of the toggle keep their curves (the object may hold more than the mesh: PhysBones and contacts
        /// under it still stop while it is hidden) and get the same keys on the bone's scale or the blendshape, so the merged
        /// renderer is as before in every state, with Write Defaults on or off: at rest the part is hidden or shown as the
        /// toggle has it. A renderer's own switch (<c>m_Enabled</c>) leaves with the renderer. What keeps a mesh apart: what
        /// keeps <see cref="MergeTogether"/>'s apart (something pointing at it, materials swapped, other properties animated,
        /// a property the merged renderer's animations set, other draw or skinning settings, a build tool still to run), a
        /// second toggle above it, a constraint moving it, and on an estimate (<paramref name="build"/> null) animated
        /// blendshapes. <paramref name="build"/> holds the build's copies of the animations.
        /// </summary>
        public static Result MergeToggled(AvatarUsage usage, AttachmentAnimationBuild build, ToggledPolicy policy)
        {
            var result = new Result();
            if (policy == null) return result;
            var root = usage.Root.transform;
            var clips = (build != null ? build.Clips : AvatarUsage.Clips(usage.Root)).Where(c => c.Clip != null && c.AnimatorRoot != null).ToList();
            var constrained = AvatarUsage.ConstraintTargets(usage.Root);
            bool Constrained(Transform t) => constrained.Any(c => c != null && (t == c || t.IsChildOf(c)));
            var targets = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => usage.AlwaysShown(r) && Mergeable(r) && !Emits(r, usage) && !usage.Pending(r.transform) && !r.bones.Any(b => b != null && Constrained(b)))
                // The fewest blendshapes first: the merged mesh is built again with all of them.
                .OrderBy(r => r.sharedMesh.blendShapeCount).ThenBy(r => r.sharedMesh.vertexCount).ToList();
            if (targets.Count == 0) return result;

            // Every toggle's curves, by the path from the avatar's root of what they show.
            var toggled = new Dictionary<string, List<(Transform AnimatorRoot, EditorCurveBinding Binding)>>(StringComparer.Ordinal);
            var index = usage.Animations;
            foreach (var (clip, animatorRoot) in clips)
                foreach (var binding in index.Bindings(clip).Floats)
                {
                    if (binding.propertyName != "m_IsActive" && binding.propertyName != "m_Enabled") continue;
                    string path = usage.PathOf(animatorRoot, binding.path);
                    if (!toggled.TryGetValue(path, out var list)) toggled[path] = list = new List<(Transform, EditorCurveBinding)>();
                    list.Add((animatorRoot, binding));
                }

            var driven = usage.Driven();
            var toggles = new Dictionary<(Transform, Renderer), Toggle>();
            var units = new List<Unit>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool skinned = renderer is SkinnedMeshRenderer skin && Mergeable(skin);
                if (!skinned && !(renderer is MeshRenderer prop && Prop(prop)) || usage.AlwaysShown(renderer)) continue;
                var member = new Member { Renderer = renderer, Vertices = MeshOptimization.MeshOf(renderer).vertexCount };
                string why = Joinable(member, usage, build, toggled, toggles, driven, Constrained);
                if (why == null)
                {
                    var toggle = member.Toggle;
                    var target = targets.FirstOrDefault(t => Fits(t, renderer, usage) && toggle.Roots.All(t.transform.IsChildOf));
                    if (target == null) why = "no mesh that is always shown is drawn like it";
                    else
                    {
                        if (member.Anchor != null && !toggle.Roots.All(member.Anchor.IsChildOf)) member.Anchor = null;
                        var unit = units.FirstOrDefault(u => u.Toggle == toggle && u.Target == target);
                        if (unit == null) units.Add(unit = new Unit { Toggle = toggle, Target = target });
                        unit.Members.Add(member);
                    }
                }
                if (why != null) result.Skipped.Add(usage.PathOf(renderer.transform) + ": " + why);
            }
            foreach (var unit in units)
            {
                unit.Bones = unit.Members.All(m => m.Anchor != null);
                unit.Price(policy);
            }

            foreach (var unit in Choose(usage, units, policy, result)) Join(usage, build, unit.Key, unit.ToList(), result);
            return result;
        }

        // Whether a toggled renderer can join, and how it would be hidden: null, or why it stays apart.
        private static string Joinable(Member member, AvatarUsage usage, AttachmentAnimationBuild build,
            Dictionary<string, List<(Transform AnimatorRoot, EditorCurveBinding Binding)>> toggled, Dictionary<(Transform, Renderer), Toggle> toggles,
            HashSet<Transform> driven, Func<Transform, bool> constrained)
        {
            var renderer = member.Renderer;
            var root = usage.Root.transform;
            var at = renderer.transform;
            if (Emits(renderer, usage)) return "a particle system emits from it";
            if (usage.Pending(at)) return "a build tool still works on it";
            if (usage.Used(renderer) || usage.Used(renderer.gameObject) || renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && usage.Used(filter))
                return "a component points at it";
            if (usage.SwapsMaterials(renderer)) return "an animation swaps its materials";
            var own = usage.AnimatedAt(at, renderer.GetType()).ToList();
            var other = own.FirstOrDefault(p => p != "m_Enabled" && !(renderer is SkinnedMeshRenderer && p.StartsWith("blendShape.", StringComparison.Ordinal)));
            if (other != null) return "an animation sets its " + other;
            if (build == null && own.Any(p => p.StartsWith("blendShape.", StringComparison.Ordinal))) return "its blendshapes are animated (they follow it at upload)";

            // One toggle shows it: every other object above it is on, and nothing else switches it.
            (Transform Holder, Renderer Switch) key = default;
            for (var t = at; t != null && t != root; t = t.parent)
            {
                if (t.CompareTag("EditorOnly")) return "it is EditorOnly";
                if (usage.AnimatedAt(t, typeof(GameObject)).Contains("m_IsActive"))
                {
                    if (key.Holder != null) return "more than one toggle shows it";
                    key = (t, null);
                }
                else if (!t.gameObject.activeSelf) return "it stays hidden";
            }
            if (usage.Switched(renderer))
            {
                if (key.Holder != null) return "more than one toggle shows it";
                key = (at, renderer);
            }
            else if (!renderer.enabled) return "it stays off";
            if (key.Holder == null) return "nothing toggles it";
            if (!toggles.TryGetValue(key, out var toggle))
            {
                toggle = new Toggle
                {
                    Holder = key.Holder, Switch = key.Switch, Path = usage.PathOf(key.Holder),
                    ShownAtRest = key.Switch != null ? key.Switch.enabled : key.Holder.gameObject.activeSelf,
                };
                if (toggled.TryGetValue(toggle.Path, out var curves))
                    foreach (var (animatorRoot, binding) in curves)
                        if (toggle.Is(binding, toggle.Path)) toggle.Roots.Add(animatorRoot);
                toggles[key] = toggle;
            }
            if (toggle.Roots.Count == 0) return "no animation the avatar plays toggles it";
            member.Toggle = toggle;

            // What it follows: a constraint can take it away from the avatar (a dropped prop), out of the merged mesh's bounds.
            var bones = renderer is SkinnedMeshRenderer skinned ? WeightedBones(skinned) : new List<Transform> { at };
            if (bones.Any(b => b == null)) return "a bone it follows is missing";
            if (bones.Any(constrained)) return "a constraint moves it";
            if (bones.Count != 1) return null;
            // Rigid: a hide bone follows its bone from where nothing hides it (the animator sets nothing on an object that is
            // off): under the bone, or above the toggled object when nothing moves the bone against it.
            var bone = bones[0];
            Transform anchor = bone;
            if (toggle.Switch == null && (bone == toggle.Holder || bone.IsChildOf(toggle.Holder)))
            {
                anchor = toggle.Holder.parent;
                for (var t = bone; anchor != null && t != toggle.Holder.parent; t = t.parent)
                    if (usage.Transformed(t) || usage.Humanoid(t) || driven.Contains(t) || usage.Pending(t)) anchor = null;
            }
            if (anchor != null && !AlwaysActive(anchor, usage)) anchor = null;
            member.Anchor = anchor;
            return null;
        }

        private static bool Fits(SkinnedMeshRenderer target, Renderer source, AvatarUsage usage) =>
            Drawn(target).Equals(Drawn(source)) && (!(source is SkinnedMeshRenderer skinned) || Skinning(target).Equals(Skinning(skinned))) &&
            !Crosses(target, source, usage);

        private static bool AlwaysActive(Transform transform, AvatarUsage usage)
        {
            for (var t = transform; t != null; t = t == usage.Root.transform ? null : t.parent)
                if (!t.gameObject.activeSelf || t != usage.Root.transform && usage.AnimatedAt(t, typeof(GameObject)).Contains("m_IsActive")) return false;
            return true;
        }

        // The bones a skinned mesh's vertices follow (weighted above zero).
        private static List<Transform> WeightedBones(SkinnedMeshRenderer renderer)
        {
            var bones = renderer.bones;
            var used = new HashSet<int>();
            foreach (var weight in renderer.sharedMesh.GetAllBoneWeights()) if (weight.weight > 0f) used.Add(weight.boneIndex);
            return used.Select(i => i < bones.Length ? bones[i] : null).Distinct().ToList();
        }

        // The units worth merging, by the renderer taking them in. Only a better rating counts: for skinned meshes and for
        // meshes, the best one the units can reach, with the cheapest units that reach it. A unit hidden by bones that would
        // give the bones a worse rating is hidden by a blendshape instead.
        private static IEnumerable<IGrouping<SkinnedMeshRenderer, Unit>> Choose(AvatarUsage usage, List<Unit> units, ToggledPolicy policy, Result result)
        {
            var root = usage.Root.transform;
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int skinnedNow = skins.Length, meshesNow = root.GetComponentsInChildren<MeshRenderer>(true).Length;
            int bonesNow = skins.SelectMany(r => r.bones).Where(b => b != null).Distinct().Count();
            string Names(Unit u) => string.Join(", ", u.Members.Select(m => usage.PathOf(m.Renderer.transform)));
            foreach (var unit in units.Where(u => u.Bytes > policy.MaxBytes))
                result.Skipped.Add(Names(unit) + ": hiding it would add " + unit.Bytes / 1024 + " KB to the download");
            var open = units.Where(u => u.Bytes <= policy.MaxBytes).OrderBy(u => (double)u.Bytes / u.Members.Count).ThenBy(u => u.AddedBones).ToList();
            int Goal(int count, int most, int[] limits)
            {
                int now = ToggledPolicy.Rate(count, limits), best = ToggledPolicy.Rate(count - most, limits);
                return best < now ? limits[best] : count;
            }
            int goalSkinned = Goal(skinnedNow, open.Sum(u => u.Skinned), policy.SkinnedLimits), goalMeshes = Goal(meshesNow, open.Sum(u => u.Props), policy.MeshLimits);
            var chosen = new List<Unit>();
            int skinnedLeft = skinnedNow, meshesLeft = meshesNow, bones = bonesNow;
            long bytes = 0;
            foreach (var unit in open)
            {
                if (!(unit.Skinned > 0 && skinnedLeft > goalSkinned || unit.Props > 0 && meshesLeft > goalMeshes)) continue;
                if (unit.Bones && ToggledPolicy.Rate(bones + unit.AddedBones, policy.BoneLimits) > ToggledPolicy.Rate(bones, policy.BoneLimits))
                {
                    unit.Bones = false;
                    unit.Price(policy);
                    if (unit.Bytes > policy.MaxBytes) { result.Skipped.Add(Names(unit) + ": a bone more would rate the bones worse, a blendshape adds too much"); continue; }
                }
                if (bytes + unit.Bytes > policy.MaxTotalBytes) { result.Skipped.Add(Names(unit) + ": the blendshapes hiding the others already add enough to the download"); continue; }
                chosen.Add(unit);
                skinnedLeft -= unit.Skinned; meshesLeft -= unit.Props; bones += unit.AddedBones; bytes += unit.Bytes;
            }
            // A category whose rating is no better got nothing: what was chosen only for it stays as it was.
            bool skinnedBetter = ToggledPolicy.Rate(skinnedLeft, policy.SkinnedLimits) < ToggledPolicy.Rate(skinnedNow, policy.SkinnedLimits);
            bool meshesBetter = ToggledPolicy.Rate(meshesLeft, policy.MeshLimits) < ToggledPolicy.Rate(meshesNow, policy.MeshLimits);
            chosen.RemoveAll(u => !(u.Skinned > 0 && skinnedBetter || u.Props > 0 && meshesBetter));
            foreach (var unit in open.Where(u => !chosen.Contains(u)))
                result.Skipped.Add(Names(unit) + ": merging it gives no better rating (" + skinnedNow + " skinned meshes, " + meshesNow + " meshes)");
            return chosen.GroupBy(u => u.Target).ToList();
        }

        private static void Join(AvatarUsage usage, AttachmentAnimationBuild build, SkinnedMeshRenderer target, List<Unit> units, Result result)
        {
            // Hide bones first: the merge binds the parts' vertices to them.
            var hides = new Dictionary<Renderer, Transform>();
            var made = new List<(Unit Unit, Transform Bone)>();
            foreach (var unit in units.Where(u => u.Bones))
                foreach (var anchor in unit.Members.Select(m => m.Anchor).Distinct())
                {
                    var bone = new GameObject(UniqueChild(anchor, HideBonePrefix + unit.Toggle.Name)).transform;
                    bone.SetParent(anchor, false);
                    made.Add((unit, bone));
                    foreach (var member in unit.Members.Where(m => m.Anchor == anchor)) hides[member.Renderer] = bone;
                }
            var sources = units.SelectMany(u => u.Members).Select(m => m.Renderer).ToList();
            Merged merged;
            try
            {
                // Their unused blendshapes go first (baked); the ones animations use join the merged mesh.
                foreach (var source in sources.OfType<SkinnedMeshRenderer>().Where(s => s.sharedMesh.blendShapeCount > 0))
                {
                    var copy = BlendShapePruning.Prune(usage, source);
                    if (copy != null) result.Meshes.Add(copy);
                }
                merged = Merge(target, sources, build != null, hides);
            }
            catch
            {
                foreach (var (_, bone) in made) if (bone != null) Object.DestroyImmediate(bone.gameObject);
                throw;
            }
            result.Meshes.Add(merged.Mesh);
            result.Merged += sources.Count;
            result.Into++;
            foreach (var unit in units.Where(u => !u.Bones))
            {
                unit.Shape = Collapse(target, merged, unit);
                result.CollapseShapes++;
            }
            foreach (var (unit, bone) in made)
            {
                bone.localScale = unit.Toggle.ShownAtRest ? Vector3.one : Vector3.zero;
                result.HideBones++;
            }
            result.AddedBytes += units.Sum(u => u.Bytes);
            // The renderers go even when their animations could not follow: never drawn twice.
            try
            {
                // The steps after this one see the new animations (a hide bone is never folded into its parent).
                string targetPath = usage.PathOf(target.transform);
                foreach (var unit in units)
                    if (unit.Bones)
                        foreach (var (_, bone) in made.Where(x => x.Unit == unit))
                            foreach (var axis in Axes) usage.Animates(usage.PathOf(bone), EditorCurveBinding.FloatCurve(usage.PathOf(bone), typeof(Transform), "m_LocalScale." + axis));
                    else usage.Animates(targetPath, EditorCurveBinding.FloatCurve(targetPath, typeof(SkinnedMeshRenderer), "blendShape." + unit.Shape));
                if (build != null)
                {
                    Mirror(build, usage, target, units, made);
                    MoveShapeAnimations(build, usage, target, merged.Shapes);
                }
            }
            finally { foreach (var source in sources) Remove(source, target, usage); }
        }

        private static readonly string[] Axes = { "x", "y", "z" };

        // A blendshape folding the unit's vertices onto one point: the origin, at rest, of the bone they follow most. Skinned,
        // a vertex on one bone lands where that bone carries the point, one on two bones on the line between where each
        // carries it: faces on one or two bones go flat in every pose, and only faces between three bones keep a sliver near
        // the point (MasculineCanine's body: 0.15-0.17% of its area in any pose, the largest face under 1 cm2; folding each
        // vertex onto a joint of its own bones instead kept 12-18%, faces up to 137 cm2 between joints).
        private static string Collapse(SkinnedMeshRenderer target, Merged merged, Unit unit)
        {
            var mesh = target.sharedMesh;
            var vertices = mesh.vertices;
            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var poses = mesh.bindposes;
            var starts = new int[counts.Length];
            for (int v = 0, k = 0; v < counts.Length; k += counts[v], v++) starts[v] = k;
            var total = new Dictionary<int, float>();
            foreach (var member in unit.Members)
            {
                var (offset, count) = merged.Ranges[member.Renderer];
                for (int v = offset; v < offset + count; v++)
                    for (int i = starts[v]; i < starts[v] + counts[v]; i++)
                        total[weights[i].boneIndex] = (total.TryGetValue(weights[i].boneIndex, out float sum) ? sum : 0f) + weights[i].weight;
            }
            var point = total.Count > 0 ? poses[total.OrderByDescending(p => p.Value).First().Key].inverse.MultiplyPoint3x4(Vector3.zero) : Vector3.zero;
            var deltas = new Vector3[vertices.Length];
            foreach (var member in unit.Members)
            {
                var (offset, count) = merged.Ranges[member.Renderer];
                for (int v = offset; v < offset + count; v++) deltas[v] = point - vertices[v];
            }
            string name = HiddenShapePrefix + unit.Toggle.Name, unique = name;
            for (int n = 2; mesh.GetBlendShapeIndex(unique) >= 0; n++) unique = name + " " + n;
            mesh.AddBlendShapeFrame(unique, 100f, deltas, null, null);
            target.SetBlendShapeWeight(mesh.blendShapeCount - 1, unit.Toggle.ShownAtRest ? 0f : 100f);
            return unique;
        }

        // The toggles' curves, also on the hide bones' scale (1 shown, 0 hidden) or the blendshape (0 shown, 100 hidden), in
        // every clip that sets them; a renderer's own switch goes with it.
        private static void Mirror(AttachmentAnimationBuild build, AvatarUsage usage, SkinnedMeshRenderer target, List<Unit> units, List<(Unit Unit, Transform Bone)> made)
        {
            var index = usage.Animations;
            using var edits = build.Edits();
            foreach (var (clip, animatorRoot) in build.Clips)
            {
                if (clip == null || animatorRoot == null) continue;
                var removed = new List<EditorCurveBinding>();
                var added = new List<EditorCurveBinding>();
                var curves = new List<AnimationCurve>();
                foreach (var binding in index.Bindings(clip).Floats)
                {
                    if (binding.propertyName != "m_IsActive" && binding.propertyName != "m_Enabled") continue;
                    string path = usage.PathOf(animatorRoot, binding.path);
                    foreach (var unit in units)
                    {
                        if (!unit.Toggle.Is(binding, path)) continue;
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        if (curve == null) continue;
                        if (unit.Bones)
                            foreach (var (_, bone) in made.Where(x => x.Unit == unit))
                            {
                                string bonePath = AnimationUtility.CalculateTransformPath(bone, animatorRoot);
                                foreach (var axis in Axes)
                                {
                                    added.Add(EditorCurveBinding.FloatCurve(bonePath, typeof(Transform), "m_LocalScale." + axis));
                                    curves.Add(Step(curve, 1f, 0f));
                                }
                            }
                        else
                        {
                            added.Add(EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(target.transform, animatorRoot), typeof(SkinnedMeshRenderer), "blendShape." + unit.Shape));
                            curves.Add(Step(curve, 0f, 100f));
                        }
                        if (unit.Toggle.Switch != null) removed.Add(binding);
                    }
                }
                if (removed.Count == 0 && added.Count == 0) continue;
                if (removed.Count > 0) index.SetCurves(clip, removed.ToArray(), new AnimationCurve[removed.Count]);
                if (added.Count > 0) index.SetCurves(clip, added.ToArray(), curves.ToArray());
                EditorUtility.SetDirty(clip);
            }
        }

        // The same keys, shown or hidden as the toggle is (above one half: on), held until the next key.
        private static AnimationCurve Step(AnimationCurve toggle, float shown, float hidden)
        {
            var keys = toggle.keys.Select(k => new Keyframe(k.time, k.value > .5f ? shown : hidden, float.PositiveInfinity, float.PositiveInfinity)).ToArray();
            return new AnimationCurve(keys) { preWrapMode = toggle.preWrapMode, postWrapMode = toggle.postWrapMode };
        }

        private static string UniqueChild(Transform parent, string name)
        {
            var candidate = name;
            for (int n = 2; parent.Find(candidate) != null; n++) candidate = name + " " + n;
            return candidate;
        }

        // A part's one bone (a prop's own transform) replaced by its hide bone, bound so that it is where it was.
        private static void Rebone(Part part, Transform hide)
        {
            part.Bones = (Transform[])part.Bones.Clone();
            part.Poses = (Matrix4x4[])part.Poses.Clone();
            int bone = 0;
            if (part.Renderer is SkinnedMeshRenderer)
                foreach (var weight in part.Mesh.GetAllBoneWeights())
                    if (weight.weight > 0f) { bone = weight.boneIndex; break; }
            part.Poses[bone] = hide.worldToLocalMatrix * part.Bones[bone].localToWorldMatrix * part.Poses[bone];
            part.Bones[bone] = hide;
        }
    }
}
