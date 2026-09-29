using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Works out how an accessory placed under the avatar root attaches to it: keeps a creator's VRCFury or Modular Avatar
    /// setup, links a clothing armature to the avatar's bones, or makes a prop follow one bone.
    /// </summary>
    public static class AttachmentPlanner
    {
        // Where a prop goes when its name says what it is. Order matters: the first word found wins.
        private static readonly (string[] words, HumanBodyBones bone, bool sided)[] Categories =
        {
            (new[] { "glove", "bracelet", "wristband", "watch", "bangle", "cuff" }, HumanBodyBones.LeftHand, true),
            (new[] { "ring" }, HumanBodyBones.LeftRingProximal, true),
            (new[] { "shoe", "boot", "sock", "sneaker", "anklet" }, HumanBodyBones.LeftFoot, true),
            (new[] { "hair", "hat", "cap", "beanie", "snapback", "ear", "ears", "horn", "horns", "glasses", "goggles", "sunglasses", "halo", "crown",
                     "helmet", "mask", "headphone", "headphones", "headband", "bow", "ribbon", "piercing", "earring", "visor", "hood" }, HumanBodyBones.Head, false),
            (new[] { "necklace", "collar", "choker", "scarf", "scalf", "pin", "badge", "tie", "pendant", "chain", "backpack", "bag", "wing", "wings" }, HumanBodyBones.Chest, false),
            (new[] { "tail", "belt", "skirt", "holster" }, HumanBodyBones.Hips, false),
        };

        // Object names that carry instructions for a manual step ("Chest Pin (Put me in armature)", "Armature (open me)").
        private static readonly Regex Instructions = new Regex(@"open me|put me|place (me |it )?(on|in)|drag|drop (me|it|this)|move (me|this)|unpack|attach to|delete me",
            RegexOptions.IgnoreCase);
        private static readonly Regex Words = new Regex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])");
        private static readonly string[] KnownAssemblies = { "UnityEngine", "Unity.", "VRC", "VRCFury", "nadena.dev", "Orbiters", "d4rkpl4y3r", "Poiyomi", "Thry", "lilToon" };

        public static AttachmentPlan Analyze(GameObject root, Transform avatarRoot)
        {
            if (root == null || avatarRoot == null) throw new ArgumentNullException(root == null ? nameof(root) : nameof(avatarRoot));
            var plan = new AttachmentPlan { Root = root, Avatar = avatarRoot, Body = Body(avatarRoot, root.transform) };
            var skeleton = AvatarSkeleton.Bones(avatarRoot, plan.Body);
            skeleton.RemoveWhere(b => b.IsChildOf(root.transform));
            var index = plan.Index = AvatarBoneIndex.Build(avatarRoot, skeleton);

            // The creator's setup, and everything its links already attach.
            var features = VrcFury.Features(root).ToList();
            var modular = root.GetComponentsInChildren<Component>(true).Where(c => c != null && (c.GetType().Namespace ?? "").StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal)).ToList();
            if (features.Count > 0) plan.Setup = "VRCFury";
            if (modular.Count > 0) plan.Setup = plan.Setup == null ? "Modular Avatar" : plan.Setup + " and Modular Avatar";
            plan.NeedsModularAvatar = modular.Count == 0 && UsesMissingModularAvatar(root);
            plan.HasToggle = features.Any(f => f.kind == "Toggle" || f.kind == "FullController") ||
                             modular.Any(c => c.GetType().Name == "ModularAvatarMenuInstaller" || c.GetType().Name == "ModularAvatarMenuItem");
            plan.HasBlendShapeLink = features.Any(f => f.kind == "BlendShapeLink") || modular.Any(c => c.GetType().Name == "ModularAvatarBlendshapeSync");
            var covered = new HashSet<Transform>();
            foreach (var link in VrcFury.ArmatureLinks(root)) if (link.From != null) covered.UnionWith(link.From.GetComponentsInChildren<Transform>(true));
            foreach (var c in modular)
                if (c.GetType().Name == "ModularAvatarMergeArmature" || c.GetType().Name == "ModularAvatarBoneProxy")
                    covered.UnionWith(c.GetComponentsInChildren<Transform>(true));
            // Skins with bones of their own.
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.bones.Length > 0).ToList();
            var skinBones = new HashSet<Transform>(skins.SelectMany(r => r.bones).Where(b => b != null && b.IsChildOf(root.transform)));
            // A constraint attaches what it holds when it is on and its source follows the avatar: an avatar object, something
            // already attached, or one of the accessory's skin bones (linked below, or following a linked parent). One held
            // by the accessory alone (a locator, a disabled drop setup) leaves it to the usual attachment.
            bool Follows(Transform source) => source != null && (!source.IsChildOf(root.transform)
                ? source.IsChildOf(avatarRoot)
                : covered.Contains(source) || source.GetComponentsInParent<Transform>(true).Any(skinBones.Contains));
            var sourced = new List<Component>();
            foreach (var constraint in Constraints(root))
            {
                if (Sources(constraint).Any(s => s != null)) { sourced.Add(constraint); continue; }
                if (!Wirable(constraint))
                {
                    plan.Notes.Add(new SetupNote(constraint, "“" + constraint.name + "” has an empty " + ObjectNames.NicifyVariableName(constraint.GetType().Name) + ": give it a source by hand."));
                    continue;
                }
                if (!plan.EmptyConstraints.Contains(constraint)) plan.EmptyConstraints.Add(constraint);
                // Wired to the bone it is named after at install: it follows through the constraint, not a bone link.
                if (EmptyConstraintTarget(constraint, index) != null && Enabled(constraint)) covered.UnionWith(constraint.GetComponentsInChildren<Transform>(true));
            }
            // Constraints can hold one another: until nothing more attaches.
            for (bool more = true; more;)
            {
                more = false;
                foreach (var constraint in sourced.Where(c => Active(c) && Sources(c).Any(Follows)).ToList())
                {
                    covered.UnionWith(constraint.GetComponentsInChildren<Transform>(true));
                    sourced.Remove(constraint);
                    more = true;
                }
            }

            // Skin bones that nothing attaches yet.
            var bones = skins.SelectMany(r => r.bones).Where(b => b != null && !skeleton.Contains(b) && !covered.Contains(b) && b.IsChildOf(root.transform))
                .Distinct().ToList();
            var loose = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer && ((SkinnedMeshRenderer)r).bones.Length == 0) && !covered.Contains(r.transform) &&
                            !bones.Any(b => r.transform.IsChildOf(b)))
                .ToList();

            if (bones.Count > 0)
            {
                var options = BoneMatcher.DetectAffixes(bones, index);
                // Several avatar bones fit a bone equally: the matcher's first pick is only a guess, left for AI or the user.
                // So are matches below it that were chosen next to that pick while another candidate has a bone of that name.
                // Parents first, so that each match knows its undecided ancestors.
                var undecided = new Dictionary<Transform, List<Transform>>();
                foreach (var match in BoneMatcher.MatchHierarchy(bones, index, options).OrderBy(m => Ancestors(m.Source, null).Count()))
                {
                    var others = match.Matched ? OtherCandidates(match, undecided, index) : null;
                    if (others != null && others.Count > 0)
                    {
                        undecided[match.Source] = others;
                        plan.Ambiguous.Add(match);
                        plan.Matches.Add(new BoneMatch(match.Source, null, BoneMatchKind.None, 0f));
                    }
                    else plan.Matches.Add(match);
                }
                var matched = new HashSet<Transform>(plan.Matches.Where(m => m.Matched).Select(m => m.Source));
                foreach (var bone in bones)
                    if (!matched.Contains(bone) && !undecided.ContainsKey(bone) && !Ancestors(bone, root.transform).Any(a => matched.Contains(a) || undecided.ContainsKey(a)))
                        plan.Unmatched.Add(bone);
            }

            if (plan.MatchedCount > 0)
            {
                plan.Kind = AttachmentKind.Clothing;
                plan.Mode = VrcFuryCanLink(plan, out plan.LinkFrom, out plan.LinkTo) ? OrbitersAttachment.AttachMode.VrcFury : OrbitersAttachment.AttachMode.Merge;
            }
            else if (bones.Count > 0 || loose.Count > 0)
            {
                // A VRCFury or Modular Avatar prop that links nothing is deliberate (dropped in the world, driven by its own
                // constraints or controller): its creator's setup is kept as is.
                if (plan.Setup != null && bones.Count == 0) plan.Kind = AttachmentKind.Configured;
                else
                {
                    plan.Kind = AttachmentKind.Rigid;
                    plan.Mode = OrbitersAttachment.AttachMode.Parent;
                    ChooseParent(plan, root, index, avatarRoot);
                }
            }
            else plan.Kind = plan.Setup != null || root.GetComponentInChildren<Renderer>(true) != null ? AttachmentKind.Configured : AttachmentKind.Empty;
            if (plan.Kind == AttachmentKind.Configured) plan.Mode = OrbitersAttachment.AttachMode.Configured;
            if (plan.Kind == AttachmentKind.Clothing)
                foreach (var renderer in loose.Take(4))
                    plan.Notes.Add(new SetupNote(renderer.gameObject, "“" + renderer.name + "” is not on a clothing bone and will not follow the avatar."));

            Notes(plan, root);
            return plan;
        }

        /// <summary>
        /// True when one recursive VRCFury Armature Link reproduces the matcher's result exactly: VRCFury merges children by
        /// exact name below the linked bone (after removing the suffix it derives from the link root), nothing more.
        /// </summary>
        private static bool VrcFuryCanLink(AttachmentPlan plan, out Transform from, out Transform to)
        {
            from = to = null;
            // Undecided bones are linked later (AI or the user), with the tool's own links.
            if (VrcFury.Writer == null || plan.Ambiguous.Count > 0) return false;
            var matches = plan.Matches.Where(m => m.Matched).ToDictionary(m => m.Source, m => m.Target);
            var tops = matches.Keys.Where(b => !Ancestors(b, plan.Root.transform).Any(matches.ContainsKey)).ToList();
            if (tops.Count != 1) return false;
            var top = tops[0];
            var target = matches[top];
            string suffix = "";
            if (top.name != target.name)
            {
                if (!top.name.Contains(target.name)) return false;
                suffix = top.name.Replace(target.name, "");
            }
            var simulated = new Dictionary<Transform, Transform> { [top] = target };
            var queue = new Queue<Transform>();
            queue.Enqueue(top);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                simulated.TryGetValue(parent, out var avatarParent);
                foreach (Transform child in parent)
                {
                    var name = suffix.Length > 0 ? child.name.Replace(suffix, "") : child.name;
                    simulated[child] = avatarParent != null ? avatarParent.Find(name) : null;
                    queue.Enqueue(child);
                }
            }
            foreach (var pair in matches)
                if (!simulated.TryGetValue(pair.Key, out var linked) || linked != pair.Value) return false;
            from = top;
            to = target;
            return true;
        }

        private static void ChooseParent(AttachmentPlan plan, GameObject root, AvatarBoneIndex index, Transform avatarRoot)
        {
            var bounds = Bounds(root);
            var head = index.Humanoid(HumanBodyBones.Head);
            float height = head != null ? Mathf.Max(.3f, head.position.y - avatarRoot.position.y) : 1.5f;
            float near = Mathf.Max(bounds.extents.magnitude * 1.2f, .12f * height);
            bool Near(Transform bone) => bone != null && Vector3.Distance(bounds.center, bone.position) <= near;

            var names = new[] { root.name }.Concat(root.GetComponentsInChildren<Renderer>(true).Select(r => r.name))
                .Concat(root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).Select(f => f.sharedMesh.name));
            var words = new HashSet<string>(names.SelectMany(n => Words.Matches(n).Cast<Match>().Select(m => m.Value.ToLowerInvariant())));
            foreach (var (categoryWords, bone, sided) in Categories)
            {
                if (!categoryWords.Any(words.Contains)) continue;
                var target = sided ? Sided(bone, root.name, bounds.center, index) : index.Humanoid(bone) ?? (bone == HumanBodyBones.Chest ? index.Humanoid(HumanBodyBones.Spine) : null);
                if (target == null) continue;
                plan.Parent = target;
                plan.Snap = !Near(target);
                return;
            }
            // No word to go by: the closest humanoid bone, a guess worth confirming.
            Transform closest = null;
            float best = float.MaxValue;
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                var bone = index.Humanoid(id);
                if (bone == null) continue;
                float d = Vector3.Distance(bounds.center, bone.position);
                if (d < best) { best = d; closest = bone; }
            }
            plan.Parent = closest ?? avatarRoot;
            plan.ParentGuessed = true;
        }

        // The side the name says, else the closer one.
        private static Transform Sided(HumanBodyBones leftBone, string name, Vector3 center, AvatarBoneIndex index)
        {
            var rightBone = Mirror(leftBone);
            var side = BoneNames.Side(name);
            if (side == BodySide.Left) return index.Humanoid(leftBone);
            if (side == BodySide.Right) return index.Humanoid(rightBone);
            var left = index.Humanoid(leftBone);
            var right = index.Humanoid(rightBone);
            if (left == null || right == null) return left ?? right;
            return Vector3.Distance(center, left.position) <= Vector3.Distance(center, right.position) ? left : right;
        }

        private static HumanBodyBones Mirror(HumanBodyBones bone)
        {
            var name = bone.ToString();
            return name.StartsWith("Left", StringComparison.Ordinal) && Enum.TryParse("Right" + name.Substring(4), out HumanBodyBones right) ? right : bone;
        }

        private static void Notes(AttachmentPlan plan, GameObject root)
        {
            if (plan.NeedsModularAvatar) plan.Notes.Add(new SetupNote(root, "Made for Modular Avatar: install it to attach this accessory."));
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (Instructions.IsMatch(t.name)) plan.Notes.Add(new SetupNote(t.gameObject, "Its name asks for a manual step: “" + t.name + "”."));
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                if (missing > 0 && !plan.NeedsModularAvatar) plan.Notes.Add(new SetupNote(t.gameObject, "Has a missing script: a package it needs is not installed."));
            }
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var assembly = behaviour.GetType().Assembly.GetName().Name;
                if (KnownAssemblies.Any(k => assembly.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
                plan.Notes.Add(new SetupNote(behaviour, "Uses " + ObjectNames.NicifyVariableName(behaviour.GetType().Name) + ", which may need its own setup."));
            }
            foreach (var bone in plan.Unmatched.Where(b => !plan.Unmatched.Contains(b.parent)).Take(8))
                plan.Notes.Add(new SetupNote(bone.gameObject, "“" + bone.name + "” matches no avatar bone and will not follow the avatar."));
            var undecided = new HashSet<Transform>(plan.Ambiguous.Select(m => m.Source));
            foreach (var match in plan.Ambiguous.Where(m => !Ancestors(m.Source, root.transform).Any(undecided.Contains)).Take(8))
            {
                var candidates = new[] { match.Target }.Concat(match.Alternatives ?? Array.Empty<Transform>())
                    .Select(t => t.parent != null && t.parent != plan.Avatar ? t.parent.name + "/" + t.name : t.name).ToList();
                plan.Notes.Add(new SetupNote(match.Source.gameObject, "“" + match.Source.name + "” fits several avatar bones (" + string.Join(", ", candidates.Take(4)) +
                                                                      (candidates.Count > 4 ? "…" : "") + "): not linked until AI or you choose one."));
            }
        }

        /// <summary>
        /// The avatar bone an empty constraint's object is named after ("Head", "Left wrist"); otherwise the hips, which
        /// hold everything else of a full-body accessory (its root object usually carries such a constraint).
        /// </summary>
        public static Transform EmptyConstraintTarget(Component constraint, AvatarBoneIndex index)
        {
            if (constraint == null || index == null) return null;
            var exact = index.WithName(BoneNames.Normalize(constraint.name));
            if (exact.Count == 1) return exact[0];
            if (BoneNames.TryInferHumanoid(constraint.name, out var bone) && index.Humanoid(bone) != null) return index.Humanoid(bone);
            return index.Humanoid(HumanBodyBones.Hips);
        }

        internal static IEnumerable<Component> Constraints(GameObject root) =>
            root.GetComponentsInChildren<Component>(true).Where(c => c is IConstraint || c is VRCConstraintBase);

        internal static IEnumerable<Transform> Sources(Component constraint)
        {
            if (constraint is IConstraint unity)
                for (int i = 0; i < unity.sourceCount; i++) yield return unity.GetSource(i).sourceTransform;
            else if (constraint is VRCConstraintBase vrc)
                foreach (var source in vrc.Sources) yield return source.SourceTransform;
        }

        /// <summary>The empty constraints the installer gives a bone as source: those that make an object follow it.</summary>
        internal static bool Wirable(Component constraint) =>
            constraint is ParentConstraint || constraint is PositionConstraint || constraint is RotationConstraint ||
            constraint is VRCParentConstraint || constraint is VRCPositionConstraint || constraint is VRCRotationConstraint;

        private static bool Enabled(Component constraint) => constraint is Behaviour behaviour && behaviour.enabled;

        private static bool Active(Component constraint) => Enabled(constraint) &&
            (constraint is IConstraint unity ? unity.constraintActive && unity.weight > 0 : constraint is VRCConstraintBase vrc && vrc.IsActive && vrc.GlobalWeight > 0);

        // The avatar bones that fit as well as the match's target: its alternatives or, below an undecided bone, the bones
        // of the target's name under that bone's other candidates.
        private static List<Transform> OtherCandidates(BoneMatch match, Dictionary<Transform, List<Transform>> undecided, AvatarBoneIndex index)
        {
            if (match.Ambiguous) return match.Alternatives.ToList();
            var ancestor = Ancestors(match.Source, null).FirstOrDefault(undecided.ContainsKey);
            if (ancestor == null) return null;
            var others = undecided[ancestor];
            return index.WithName(BoneNames.Normalize(match.Target.name)).Where(t => t != match.Target && others.Any(t.IsChildOf)).ToList();
        }

        private static bool UsesMissingModularAvatar(GameObject root)
        {
            if (!root.GetComponentsInChildren<Transform>(true).Any(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) > 0)) return false;
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            // Serialized field names of Modular Avatar's merge, bone proxy and menu components.
            return text.Contains("mergeTarget:") || text.Contains("boneReference:") || text.Contains("menuToAppend:") || text.Contains("installTargetMenu:");
        }

        /// <summary>The avatar's main body mesh: skinned to the avatar's armature, with the most blendshapes.</summary>
        public static SkinnedMeshRenderer Body(Transform avatarRoot, Transform exclude = null)
        {
            // Without a humanoid Animator the skeleton is unknown: any mesh that is not an attached accessory.
            var skeleton = AvatarSkeleton.Bones(avatarRoot);
            return avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && (exclude == null || !r.transform.IsChildOf(exclude)) &&
                            (skeleton.Count > 0 ? r.bones.Any(b => b != null && skeleton.Contains(b)) : r.GetComponentInParent<OrbitersAttachment>(true) == null))
                .OrderByDescending(r => r.name.Equals("Body", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.sharedMesh.blendShapeCount)
                .ThenByDescending(r => r.sharedMesh.vertexCount)
                .FirstOrDefault();
        }

        /// <summary>World bounds of every renderer, from their meshes so that inactive objects count too.</summary>
        public static Bounds Bounds(GameObject root)
        {
            bool any = false;
            var result = new Bounds(root.transform.position, Vector3.zero);
            void Add(Bounds local, Matrix4x4 toWorld)
            {
                var c = local.center; var e = local.extents;
                for (int i = 0; i < 8; i++)
                {
                    var p = toWorld.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; } else result.Encapsulate(p);
                }
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) Add(filter.sharedMesh.bounds, filter.transform.localToWorldMatrix);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh != null) Add(skin.sharedMesh.bounds, skin.transform.localToWorldMatrix);
            return result;
        }

        internal static IEnumerable<Transform> Ancestors(Transform t, Transform stop)
        {
            for (var p = t.parent; p != null && p != stop; p = p.parent) yield return p;
        }
    }
}
