using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>
    /// An accessory or piece of clothing attached to the avatar by an Orbiters tool, and how it follows the avatar once built.
    /// Nothing moves in the scene: Merge and Parent are applied to the build copy only, VRCFury links by VRCFury.
    /// </summary>
    [AddComponentMenu("Orbiters/Attachment"), DisallowMultipleComponent]
    public sealed class OrbitersAttachment : MonoBehaviour, IEditorOnly
    {
        public enum AttachMode
        {
            /// <summary>The creator's own VRCFury or Modular Avatar setup attaches it.</summary>
            Configured,
            /// <summary>A VRCFury Armature Link added by the tool: bone names match the avatar's exactly.</summary>
            VrcFury,
            /// <summary>Each linked bone follows its avatar bone, keeping its rest offset; other bones follow their parent.</summary>
            Merge,
            /// <summary>The whole object follows one avatar bone, where it stands.</summary>
            Parent,
        }

        [Serializable]
        public struct BoneLink
        {
            public Transform from, to;
        }

        public AttachMode mode;
        public List<BoneLink> links = new List<BoneLink>();
        public Transform parent;
        [Tooltip("Body blendshapes also drive the same-named shapes of this accessory's meshes.")]
        public bool syncBlendShapes = true;
        public SkinnedMeshRenderer body;
        [HideInInspector] public string source, variant;
        /// <summary>True when the tool created this object: removing the accessory deletes it.</summary>
        [HideInInspector] public bool created;
        /// <summary>Components the tool added to an object it did not create, removed with the accessory.</summary>
        [HideInInspector] public List<Component> added = new List<Component>();
    }
}
