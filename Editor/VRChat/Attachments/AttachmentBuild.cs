using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Build;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Applies My Avatar attachments to the avatar being built (VRChat's upload copy, VRCFury's play mode or test copy):
    /// linked bones and props are placed on their avatar bone and follow it. Runs just before VRCFury: when VRCFury builds
    /// the avatar they are moved under their bone, as VRCFury recorded original paths earlier and so keeps their animations
    /// working; otherwise Toolkit copies the animation graphs and remaps paths itself. Neither path adds constraints.
    /// </summary>
    public static class AttachmentBuild
    {
        // Each attachment's skinned meshes, taken before Apply moves bones (and meshes under them) out of it: Finish runs
        // after VRCFury, on the same objects.
        private sealed class Skins : Dictionary<OrbitersAttachment, SkinnedMeshRenderer[]> { }

        /// <summary>The build of <paramref name="avatarRoot"/>'s copy (<see cref="BuildContext.For"/>).</summary>
        public static void Apply(GameObject avatarRoot) => Apply(BuildContext.For(avatarRoot));

        public static void Apply(BuildContext context)
        {
            var avatarRoot = context.Avatar;
            var attachments = avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true);
            if (attachments.Length == 0) return;
            AttachmentInstaller.ConvertAtBuild(avatarRoot, attachments);
            var skins = context.State<Skins>();
            foreach (var attachment in attachments) skins[attachment] = attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var creatorLinks = VrcFury.ArmatureLinks(avatarRoot).ToArray();
            var moves = Moves(avatarRoot, creatorLinks);
            bool reparent = VrcFury.Builds(avatarRoot);
            // A recursive creator link would align the fitted bones again, and only match identical
            // child names. Replace that link on this build copy; keep toggles and unrelated prop links.
            var fitted = new HashSet<Transform>(moves.Where(l => l.Source == "Fitted clothing").Select(l => l.Follower));
            foreach (var link in creatorLinks)
                if (link.Recursive && link.From != null && fitted.Contains(link.From.transform))
                    Object.DestroyImmediate(link.Component);
            var animations = reparent ? null : context.Animations;
            foreach (var move in moves) move.Apply();
            if (reparent)
                foreach (var move in moves) move.Follower.SetParent(move.Target, true);
            else animations.Move(moves);
        }

        // What Apply moves onto its bone, parents first (a child is placed from its parent's new pose, then everything
        // follows its bone). Other configured attachments may belong to constraints or another build tool: only the fitted
        // skeleton whose recursive VRCFury link Apply replaces is taken over.
        private static List<FollowLink> Moves(GameObject avatarRoot, IReadOnlyList<VrcFury.Link> creatorLinks)
        {
            var links = AttachmentFollow.Links(avatarRoot.transform, matchUnlinkedClothing: false);
            var fittedRoots = new HashSet<Transform>(links.Where(l => l.Source == "Fitted clothing")
                .GroupBy(l => l.Accessory).Where(group => creatorLinks.Any(c => c.Recursive && c.From != null &&
                    group.Any(l => l.Follower == c.From.transform))).Select(group => group.Key));
            var moves = links.Where(l => l.Source == "My Avatar" ||
                l.Source == "Fitted clothing" && fittedRoots.Contains(l.Accessory)).ToList();
            moves.Sort((a, b) => Depth(a.Follower).CompareTo(Depth(b.Follower)));
            return moves;
        }

        /// <summary>
        /// The objects the build puts under another parent before VRCFury runs, in that order (each becomes its new
        /// parent's last child), when VRCFury builds the avatar; empty otherwise. VRCFury reads its components in hierarchy
        /// order after that: tools that predict its build (My Avatar's menu preview) read them from there. The avatar
        /// doesn't change.
        /// </summary>
        public static IReadOnlyList<(Transform Follower, Transform Parent)> Reparented(GameObject avatarRoot)
        {
            if (avatarRoot == null || avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true).Length == 0 || !VrcFury.Builds(avatarRoot))
                return System.Array.Empty<(Transform, Transform)>();
            return Moves(avatarRoot, VrcFury.ArmatureLinks(avatarRoot).ToArray()).Where(m => m.Follower != null && m.Target != null)
                .Select(m => (m.Follower, m.Target)).ToList();
        }

        /// <summary>
        /// After VRCFury built its controllers: body blendshape animations also drive the accessories' shapes, except those a
        /// refit already links (<see cref="Refit.RefitBuild"/>).
        /// </summary>
        public static void Finish(GameObject avatarRoot) => Finish(BuildContext.For(avatarRoot));

        public static void Finish(BuildContext context)
        {
            var avatarRoot = context.Avatar;
            var copies = new List<BlendShapeCopy>();
            var skins = context.State<Skins>();
            foreach (var attachment in avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                if (attachment.syncBlendShapes)
                    copies.AddRange(AttachmentInstaller.BlendShapeCopies(attachment, skins.TryGetValue(attachment, out var own) ? own : null)
                        .Where(c => !Refit.RefitBuild.IsLinked(avatarRoot, c.Destination, c.DestinationShape)));
                skins.Remove(attachment);
                Object.DestroyImmediate(attachment);
            }
            if (copies.Count > 0) BlendShapeSync.Apply(avatarRoot, copies, "My Avatar accessories");
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }
    }

    internal sealed class AttachmentBuildHook : BuildStep
    {
        // Just before VRCFury applies its features (-10000).
        public override BuildPhase Phase => BuildPhase.BeforeVrcFury;
        public override bool Run(BuildContext context)
        {
            AttachmentBuild.Apply(context);
            return true;
        }
    }

    internal sealed class AttachmentFinishHook : BuildStep
    {
        // After VRCFury (-10000), MCB's blendshape links (-9000), ReFit's links (-8960) and the accessories following body
        // blendshapes; before MCB's versions (-8800) and the SDK strips editor-only components (-1024).
        public override BuildPhase Phase => BuildPhase.AccessoryShapes;
        public override IReadOnlyList<System.Type> After => new[] { typeof(SurfaceFollow.SurfaceFollowHook) };
        public override bool Run(BuildContext context)
        {
            AttachmentBuild.Finish(context);
            return true;
        }
    }
}
