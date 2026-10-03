using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

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
        private static readonly Dictionary<OrbitersAttachment, SkinnedMeshRenderer[]> Skins = new Dictionary<OrbitersAttachment, SkinnedMeshRenderer[]>();

        public static void Apply(GameObject avatarRoot)
        {
            var attachments = avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true);
            if (attachments.Length == 0) return;
            AttachmentInstaller.ConvertAtBuild(avatarRoot, attachments);
            foreach (var gone in Skins.Keys.Where(a => a == null).ToList()) Skins.Remove(gone);
            foreach (var attachment in attachments) Skins[attachment] = attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var links = AttachmentFollow.Links(avatarRoot.transform, matchUnlinkedClothing: false);
            var creatorLinks = VrcFury.ArmatureLinks(avatarRoot).ToArray();
            // Other configured attachments may belong to constraints or another build tool.
            // Only take over the fitted skeleton whose recursive VRCFury link we replace.
            var fittedRoots = new HashSet<Transform>(links.Where(l => l.Source == "Fitted clothing")
                .GroupBy(l => l.Accessory).Where(group => creatorLinks.Any(c => c.Recursive && c.From != null &&
                    group.Any(l => l.Follower == c.From.transform))).Select(group => group.Key));
            var moves = links.Where(l => l.Source == "My Avatar" ||
                l.Source == "Fitted clothing" && fittedRoots.Contains(l.Accessory)).ToList();
            bool reparent = VrcFury.Builds(avatarRoot);
            // A recursive creator link would align the fitted bones again, and only match identical
            // child names. Replace that link on this build copy; keep toggles and unrelated prop links.
            var fitted = new HashSet<Transform>(moves.Where(l => l.Source == "Fitted clothing").Select(l => l.Follower));
            foreach (var link in creatorLinks)
                if (link.Recursive && link.From != null && fitted.Contains(link.From.transform))
                    Object.DestroyImmediate(link.Component);
            // Parents first: a child is placed from its parent's new pose, then everything follows its bone.
            moves.Sort((a, b) => Depth(a.Follower).CompareTo(Depth(b.Follower)));
            var animations = reparent ? null : AttachmentAnimationBuild.Prepare(avatarRoot);
            foreach (var move in moves) move.Apply();
            if (reparent)
                foreach (var move in moves) move.Follower.SetParent(move.Target, true);
            else animations.Move(moves);
        }

        /// <summary>
        /// After VRCFury built its controllers: body blendshape animations also drive the accessories' shapes, except those a
        /// refit already links (<see cref="Refit.RefitBuild"/>).
        /// </summary>
        public static void Finish(GameObject avatarRoot)
        {
            var copies = new List<BlendShapeCopy>();
            foreach (var attachment in avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                if (attachment.syncBlendShapes)
                    copies.AddRange(AttachmentInstaller.BlendShapeCopies(attachment, Skins.TryGetValue(attachment, out var skins) ? skins : null)
                        .Where(c => !Refit.RefitBuild.IsLinked(avatarRoot, c.Destination, c.DestinationShape)));
                Skins.Remove(attachment);
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

    internal sealed class AttachmentBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        // VRCFury applies its features at -10000.
        public int callbackOrder => -10100;
        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            AttachmentBuild.Apply(avatarRoot);
            return true;
        }
    }

    internal sealed class AttachmentFinishHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury (-10000) and MCB's blendshape links (-9000); before the SDK strips editor-only components (-1024).
        public int callbackOrder => -8900;
        public bool OnPreprocessAvatar(GameObject avatarRoot)
        {
            AttachmentBuild.Finish(avatarRoot);
            return true;
        }
    }
}
