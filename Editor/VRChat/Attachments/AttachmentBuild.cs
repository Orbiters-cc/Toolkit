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
    /// linked bones and props are placed on their avatar bone and moved under it. Runs just before VRCFury, which records
    /// original paths earlier and so keeps the accessory's animations working after the move.
    /// </summary>
    public static class AttachmentBuild
    {
        public static void Apply(GameObject avatarRoot)
        {
            var attachments = avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true);
            if (attachments.Length == 0) return;
            var moves = new List<FollowLink>();
            foreach (var link in AttachmentFollow.Links(avatarRoot.transform, matchUnlinkedClothing: false))
                if (link.Source == "My Avatar") moves.Add(link);
            // Parents first: a child is placed from its parent's new pose, then everything is moved under its bone.
            moves.Sort((a, b) => Depth(a.Follower).CompareTo(Depth(b.Follower)));
            foreach (var move in moves) move.Apply();
            foreach (var move in moves) move.Follower.SetParent(move.Target, true);
        }

        /// <summary>After VRCFury built its controllers: body blendshape animations also drive the accessories' shapes.</summary>
        public static void Finish(GameObject avatarRoot)
        {
            var copies = new List<BlendShapeCopy>();
            foreach (var attachment in avatarRoot.GetComponentsInChildren<OrbitersAttachment>(true))
            {
                if (attachment.syncBlendShapes) copies.AddRange(AttachmentInstaller.BlendShapeCopies(attachment));
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
