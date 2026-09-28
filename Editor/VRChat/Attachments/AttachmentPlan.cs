using System.Collections.Generic;
using Orbiters.Toolkit.Armature;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    public enum AttachmentKind
    {
        /// <summary>The creator's VRCFury or Modular Avatar setup already attaches everything.</summary>
        Configured,
        /// <summary>Skinned to its own armature, whose bones follow the avatar's.</summary>
        Clothing,
        /// <summary>Follows one avatar bone as a whole (props, hats, unrigged or self-contained rigs like hair).</summary>
        Rigid,
        /// <summary>Nothing visible to attach.</summary>
        Empty,
    }

    /// <summary>Something the tool could not do for the user, with the object to look at.</summary>
    public struct SetupNote
    {
        public Object Target;
        public string Reason;
        public SetupNote(Object target, string reason) { Target = target; Reason = reason; }
    }

    /// <summary>How one accessory will be attached; shared by the installer, the build and the posing preview.</summary>
    public sealed class AttachmentPlan
    {
        public GameObject Root;
        public Transform Avatar;
        public AttachmentKind Kind;
        public OrbitersAttachment.AttachMode Mode;
        /// <summary>"VRCFury", "Modular Avatar" or null: whose setup the creator shipped.</summary>
        public string Setup;
        public bool NeedsModularAvatar;

        // Clothing
        public readonly List<BoneMatch> Matches = new List<BoneMatch>();
        public readonly List<Transform> Unmatched = new List<Transform>();
        /// <summary>VrcFury mode: the bone linked recursively and its avatar bone.</summary>
        public Transform LinkFrom, LinkTo;

        // Rigid
        public Transform Parent;
        /// <summary>The modelled position is far from the bone: place it on the bone.</summary>
        public bool Snap;
        /// <summary>Guessed from proximity alone: worth confirming (AI or the user).</summary>
        public bool ParentGuessed;

        public readonly List<SetupNote> Notes = new List<SetupNote>();
        public readonly List<Component> EmptyConstraints = new List<Component>();
        public bool HasToggle, HasBlendShapeLink;
        public SkinnedMeshRenderer Body;
        public AvatarBoneIndex Index;

        public int MatchedCount { get { int n = 0; foreach (var m in Matches) if (m.Matched) n++; return n; } }
    }
}
