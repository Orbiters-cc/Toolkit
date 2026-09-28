using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Avatars;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    public sealed class AttachmentOptions
    {
        public bool AddToggle = true;
        public string MenuFolder = "Accessories";
        public bool SyncBlendShapes = true;
        /// <summary>The object was created for this install: removing the accessory deletes it.</summary>
        public bool Created;
        public string Source, Variant;
    }

    /// <summary>Applies an <see cref="AttachmentPlan"/> with Undo. The avatar itself is never modified.</summary>
    public static class AttachmentInstaller
    {
        public static OrbitersAttachment Install(AttachmentPlan plan, AttachmentOptions options)
        {
            if (plan == null || plan.Root == null) throw new ArgumentNullException(nameof(plan));
            options ??= new AttachmentOptions();
            var root = plan.Root;
            var attachment = root.GetComponent<OrbitersAttachment>();
            if (attachment == null)
            {
                attachment = Undo.AddComponent<OrbitersAttachment>(root);
                attachment.created = options.Created;
            }
            else Undo.RecordObject(attachment, "Attach accessory");
            attachment.source = options.Source;
            attachment.variant = options.Variant;
            attachment.mode = plan.Mode;
            attachment.body = plan.Body;
            attachment.links.Clear();
            attachment.parent = null;

            WireConstraints(plan, attachment);
            switch (plan.Mode)
            {
                case OrbitersAttachment.AttachMode.VrcFury:
                    bool humanoid = plan.Index.TryGetHumanoid(plan.LinkTo, out var bone);
                    Track(attachment, VrcFury.Writer.ArmatureLink(root, plan.LinkFrom.gameObject, humanoid ? bone : HumanBodyBones.LastBone, humanoid ? null : plan.LinkTo.gameObject));
                    break;
                case OrbitersAttachment.AttachMode.Merge:
                    foreach (var match in plan.Matches.Where(m => m.Matched))
                        attachment.links.Add(new OrbitersAttachment.BoneLink { from = match.Source, to = match.Target });
                    break;
                case OrbitersAttachment.AttachMode.Parent:
                    attachment.parent = plan.Parent;
                    if (plan.Snap && plan.Parent != null) SnapTo(root, plan.Parent);
                    break;
            }

            if (options.AddToggle && !plan.HasToggle && plan.Kind != AttachmentKind.Empty && VrcFury.Writer != null)
                Track(attachment, VrcFury.Writer.Toggle(root, options.MenuFolder + "/" + MenuName(root.name), root, root.activeSelf));

            var copies = options.SyncBlendShapes && !plan.HasBlendShapeLink ? BlendShapeCopies(attachment) : new List<BlendShapeCopy>();
            attachment.syncBlendShapes = copies.Count > 0;
            // Shows the clothing with the body's current shapes right away; the build keeps them linked.
            if (copies.Count > 0) BlendShapeSync.CopyWeights(copies, recordUndo: true);
            Dirty(attachment);
            return attachment;
        }

        /// <summary>Makes a prop follow another bone (the user's or AI's choice), placing it on the bone when asked.</summary>
        public static void Retarget(OrbitersAttachment attachment, Transform bone, bool snap)
        {
            Undo.RecordObject(attachment, "Attach accessory to " + bone.name);
            attachment.mode = OrbitersAttachment.AttachMode.Parent;
            attachment.links.Clear();
            attachment.parent = bone;
            if (snap) SnapTo(attachment.gameObject, bone);
            Dirty(attachment);
        }

        /// <summary>Adds bone links found later (AI answers); links of the same bone are replaced.</summary>
        public static void Link(OrbitersAttachment attachment, IEnumerable<(Transform from, Transform to)> links)
        {
            Undo.RecordObject(attachment, "Link accessory bones");
            foreach (var (from, to) in links)
            {
                if (from == null || to == null || !from.IsChildOf(attachment.transform)) continue;
                attachment.links.RemoveAll(l => l.from == from);
                attachment.links.Add(new OrbitersAttachment.BoneLink { from = from, to = to });
            }
            if (attachment.links.Count > 0 && attachment.mode == OrbitersAttachment.AttachMode.Parent) { attachment.mode = OrbitersAttachment.AttachMode.Merge; attachment.parent = null; }
            Dirty(attachment);
        }

        /// <summary>Deletes an accessory the tool placed, or takes off what the tool added to one the user placed.</summary>
        public static void Remove(OrbitersAttachment attachment)
        {
            if (attachment == null) return;
            if (attachment.created) { Undo.DestroyObjectImmediate(attachment.gameObject); return; }
            foreach (var component in attachment.added.Where(c => c != null).ToList()) Undo.DestroyObjectImmediate(component);
            Undo.DestroyObjectImmediate(attachment);
        }

        public static List<OrbitersAttachment> Installed(Transform avatarRoot) =>
            avatarRoot == null ? new List<OrbitersAttachment>() : avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true).ToList();

        /// <summary>Every skinned mesh of the accessory paired with the body's same-named blendshapes.</summary>
        public static List<BlendShapeCopy> BlendShapeCopies(OrbitersAttachment attachment)
        {
            var copies = new List<BlendShapeCopy>();
            if (attachment == null || attachment.body == null || attachment.body.sharedMesh == null) return copies;
            foreach (var skin in attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin != attachment.body && skin.sharedMesh != null && skin.sharedMesh.blendShapeCount > 0)
                    copies.AddRange(BlendShapeSync.Plan(attachment.body, skin));
            return copies;
        }

        private static void SnapTo(GameObject root, Transform bone)
        {
            var bounds = AttachmentPlanner.Bounds(root);
            Undo.RecordObject(root.transform, "Place accessory");
            root.transform.position += bone.position - bounds.center;
        }

        // Empty constraints named after avatar bones ("Head", "Left wrist") get that bone as source, keeping where the
        // object stands, and become VRChat constraints.
        private static void WireConstraints(AttachmentPlan plan, OrbitersAttachment attachment)
        {
            var descriptor = plan.Avatar.GetComponent<VRCAvatarDescriptor>();
            var unity = new List<IConstraint>();
            foreach (var constraint in plan.EmptyConstraints)
            {
                if (constraint == null) continue;
                var source = AttachmentPlanner.EmptyConstraintTarget(constraint, plan.Index);
                if (source == null) continue;
                var t = constraint.transform;
                Undo.RecordObject(constraint, "Wire constraint");
                switch (constraint)
                {
                    case ParentConstraint parent:
                        Replace(parent, source);
                        parent.SetTranslationOffset(0, source.InverseTransformPoint(t.position));
                        parent.SetRotationOffset(0, (Quaternion.Inverse(source.rotation) * t.rotation).eulerAngles);
                        break;
                    case PositionConstraint position:
                        Replace(position, source);
                        position.translationOffset = t.position - source.position;
                        break;
                    case RotationConstraint rotation:
                        Replace(rotation, source);
                        rotation.rotationOffset = (Quaternion.Inverse(source.rotation) * t.rotation).eulerAngles;
                        break;
                    case VRCParentConstraint vrc:
                        vrc.Sources.Clear();
                        vrc.Sources.Add(new VRCConstraintSource(source, 1, source.InverseTransformPoint(t.position), (Quaternion.Inverse(source.rotation) * t.rotation).eulerAngles));
                        vrc.IsActive = true;
                        vrc.Locked = true;
                        break;
                    default: continue;
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(constraint);
                if (constraint is IConstraint ic) unity.Add(ic);
            }
            if (unity.Count == 0 || descriptor == null) return;
            var before = new HashSet<VRCConstraintBase>(plan.Root.GetComponentsInChildren<VRCConstraintBase>(true));
            AvatarDynamicsSetup.DoConvertUnityConstraints(unity.ToArray(), descriptor, false);
            if (!attachment.created) attachment.added.AddRange(plan.Root.GetComponentsInChildren<VRCConstraintBase>(true).Where(c => !before.Contains(c)));
        }

        private static void Replace(IConstraint constraint, Transform source)
        {
            while (constraint.sourceCount > 0) constraint.RemoveSource(0);
            constraint.AddSource(new ConstraintSource { sourceTransform = source, weight = 1 });
            constraint.weight = 1;
            constraint.locked = true;
            constraint.constraintActive = true;
        }

        private static void Track(OrbitersAttachment attachment, Component component)
        {
            if (component != null && !attachment.created) attachment.added.Add(component);
        }

        private static string MenuName(string name) => name.Replace("/", "-").Trim();

        internal static void Dirty(Component component)
        {
            EditorUtility.SetDirty(component);
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            if (component.gameObject.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }
    }
}
