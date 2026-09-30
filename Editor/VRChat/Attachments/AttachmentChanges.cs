using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using ConstraintState = Orbiters.Toolkit.VRChat.OrbitersAttachment.ConstraintState;
using LocalPose = Orbiters.Toolkit.VRChat.OrbitersAttachment.LocalPose;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// What installing changes on an object the tool did not create (wired constraints, the place it was put, the body's
    /// blendshape weights copied onto it), kept on its <see cref="OrbitersAttachment"/> and put back on removal. A value the
    /// user changed since is theirs: only values still as the tool left them are put back.
    /// </summary>
    internal static class AttachmentChanges
    {
        private const float Tolerance = 1e-4f;

        // ---- Constraints ----------------------------------------------------------------------------------------------

        /// <summary>Records <paramref name="constraint"/> around <paramref name="change"/>; the first state recorded is kept on a reinstall.</summary>
        internal static void Constraint(OrbitersAttachment attachment, Component constraint, System.Action change)
        {
            if (attachment.created) { change(); return; }
            var before = Capture(constraint);
            change();
            var record = attachment.changes.constraints.FirstOrDefault(c => c.constraint == constraint);
            if (record == null) attachment.changes.constraints.Add(record = new OrbitersAttachment.ConstraintChange { constraint = constraint, before = before });
            record.after = Capture(constraint);
        }

        internal static ConstraintState Capture(Component constraint)
        {
            var state = new ConstraintState();
            switch (constraint)
            {
                case IConstraint unity:
                    state.weight = unity.weight;
                    state.active = unity.constraintActive;
                    state.locked = unity.locked;
                    for (int i = 0; i < unity.sourceCount; i++)
                    {
                        var source = unity.GetSource(i);
                        var captured = new OrbitersAttachment.ConstraintSourceState { transform = source.sourceTransform, weight = source.weight };
                        if (unity is ParentConstraint parent)
                        {
                            captured.positionOffset = parent.GetTranslationOffset(i);
                            captured.rotationOffset = parent.GetRotationOffset(i);
                        }
                        state.sources.Add(captured);
                    }
                    if (unity is PositionConstraint position) state.positionOffset = position.translationOffset;
                    if (unity is RotationConstraint rotation) state.rotationOffset = rotation.rotationOffset;
                    break;
                case VRCConstraintBase vrc:
                    state.weight = vrc.GlobalWeight;
                    state.active = vrc.IsActive;
                    state.locked = vrc.Locked;
                    foreach (var source in vrc.Sources)
                        state.sources.Add(new OrbitersAttachment.ConstraintSourceState
                        {
                            transform = source.SourceTransform, weight = source.Weight,
                            positionOffset = source.ParentPositionOffset, rotationOffset = source.ParentRotationOffset,
                        });
                    if (vrc is VRCPositionConstraint vrcPosition) state.positionOffset = vrcPosition.PositionOffset;
                    if (vrc is VRCRotationConstraint vrcRotation) state.rotationOffset = vrcRotation.RotationOffset;
                    break;
            }
            return state;
        }

        internal static void Apply(Component constraint, ConstraintState state)
        {
            switch (constraint)
            {
                case IConstraint unity:
                    unity.SetSources(state.sources.Select(s => new ConstraintSource { sourceTransform = s.transform, weight = s.weight }).ToList());
                    if (unity is ParentConstraint parent)
                    {
                        parent.translationOffsets = state.sources.Select(s => s.positionOffset).ToArray();
                        parent.rotationOffsets = state.sources.Select(s => s.rotationOffset).ToArray();
                    }
                    if (unity is PositionConstraint position) position.translationOffset = state.positionOffset;
                    if (unity is RotationConstraint rotation) rotation.rotationOffset = state.rotationOffset;
                    unity.weight = state.weight;
                    unity.locked = state.locked;
                    unity.constraintActive = state.active;
                    break;
                case VRCConstraintBase vrc:
                    vrc.Sources.Clear();
                    foreach (var s in state.sources) vrc.Sources.Add(new VRCConstraintSource(s.transform, s.weight, s.positionOffset, s.rotationOffset));
                    if (vrc is VRCPositionConstraint vrcPosition) vrcPosition.PositionOffset = state.positionOffset;
                    if (vrc is VRCRotationConstraint vrcRotation) vrcRotation.RotationOffset = state.rotationOffset;
                    vrc.GlobalWeight = state.weight;
                    vrc.Locked = state.locked;
                    vrc.IsActive = state.active;
                    break;
            }
        }

        internal static bool Same(ConstraintState a, ConstraintState b)
        {
            if (a == null || b == null) return a == b;
            if (!Near(a.weight, b.weight) || a.active != b.active || a.locked != b.locked) return false;
            if (!Near(a.positionOffset, b.positionOffset) || !Near(a.rotationOffset, b.rotationOffset)) return false;
            if (a.sources.Count != b.sources.Count) return false;
            for (int i = 0; i < a.sources.Count; i++)
            {
                var x = a.sources[i];
                var y = b.sources[i];
                if (x.transform != y.transform || !Near(x.weight, y.weight) || !Near(x.positionOffset, y.positionOffset) || !Near(x.rotationOffset, y.rotationOffset))
                    return false;
            }
            return true;
        }

        // ---- Place ----------------------------------------------------------------------------------------------------

        /// <summary>Records the object's local pose around <paramref name="move"/>; the pose from before the first move is kept.</summary>
        internal static void Move(OrbitersAttachment attachment, System.Action move)
        {
            if (attachment.created) { move(); return; }
            var t = attachment.transform;
            if (!attachment.changes.moved)
            {
                attachment.changes.moved = true;
                attachment.changes.poseBefore = Pose(t);
            }
            move();
            attachment.changes.poseAfter = Pose(t);
        }

        internal static LocalPose Pose(Transform t) => new LocalPose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };

        private static bool Same(LocalPose a, LocalPose b) =>
            Near(a.position, b.position) && Near(a.scale, b.scale) && Quaternion.Angle(a.rotation, b.rotation) < 0.01f;

        // ---- Blendshape weights ---------------------------------------------------------------------------------------

        /// <summary>Copies the body's weights onto the accessory, recording each weight it changes on an object the tool did not create.</summary>
        internal static void CopyWeights(OrbitersAttachment attachment, IReadOnlyList<BlendShapeCopy> copies)
        {
            if (attachment.created) { BlendShapeSync.CopyWeights(copies, recordUndo: true); return; }
            var before = new Dictionary<(SkinnedMeshRenderer, string), float>();
            foreach (var copy in copies)
            {
                int index = Index(copy.Destination, copy.DestinationShape);
                if (index >= 0 && !before.ContainsKey((copy.Destination, copy.DestinationShape)))
                    before[(copy.Destination, copy.DestinationShape)] = copy.Destination.GetBlendShapeWeight(index);
            }
            BlendShapeSync.CopyWeights(copies, recordUndo: true);
            foreach (var pair in before)
            {
                var (renderer, shape) = pair.Key;
                float after = renderer.GetBlendShapeWeight(Index(renderer, shape));
                if (Near(after, pair.Value)) continue;
                var weights = attachment.changes.weights;
                int existing = weights.FindIndex(w => w.renderer == renderer && w.shape == shape);
                var change = new OrbitersAttachment.WeightChange { renderer = renderer, shape = shape, before = existing >= 0 ? weights[existing].before : pair.Value, after = after };
                if (existing >= 0) weights[existing] = change;
                else weights.Add(change);
            }
        }

        private static int Index(SkinnedMeshRenderer renderer, string shape) =>
            renderer != null && renderer.sharedMesh != null && !string.IsNullOrEmpty(shape) ? renderer.sharedMesh.GetBlendShapeIndex(shape) : -1;

        // ---- Removal --------------------------------------------------------------------------------------------------

        /// <summary>Puts back what installing changed, with Undo, where it is still as the tool left it.</summary>
        internal static void Restore(OrbitersAttachment attachment)
        {
            var changes = attachment.changes;
            foreach (var change in changes.constraints)
            {
                if (change.constraint == null || !Same(Capture(change.constraint), change.after)) continue;
                Undo.RecordObject(change.constraint, "Put back " + change.constraint.name);
                Apply(change.constraint, change.before);
                AttachmentInstaller.Dirty(change.constraint);
            }
            var t = attachment.transform;
            if (changes.moved && Same(Pose(t), changes.poseAfter))
            {
                Undo.RecordObject(t, "Put back " + t.name);
                t.localPosition = changes.poseBefore.position;
                t.localRotation = changes.poseBefore.rotation;
                t.localScale = changes.poseBefore.scale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            }
            foreach (var weight in changes.weights)
            {
                int index = Index(weight.renderer, weight.shape);
                if (index < 0 || !Near(weight.renderer.GetBlendShapeWeight(index), weight.after)) continue;
                Undo.RecordObject(weight.renderer, "Put back " + weight.renderer.name);
                weight.renderer.SetBlendShapeWeight(index, weight.before);
                AttachmentInstaller.Dirty(weight.renderer);
            }
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) <= Tolerance;
        private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude <= Tolerance * Tolerance;
    }
}
