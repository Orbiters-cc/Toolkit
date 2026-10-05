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
        /// <summary>A short clean name to show for it (AI suggested, e.g. "Glowsticks for Ultipaw"); empty: its object's name.</summary>
        [HideInInspector] public string displayName;
        /// <summary>The object name <see cref="displayName"/> was made for: a renamed object gets a new one.</summary>
        [HideInInspector] public string displayNameFor;

        public string DisplayName => !string.IsNullOrEmpty(displayName) && displayNameFor == name ? displayName : name;
        /// <summary>True when the tool created this object: removing the accessory deletes it.</summary>
        [HideInInspector] public bool created;
        /// <summary>Components the tool added to an object it did not create, removed with the accessory.</summary>
        [HideInInspector] public List<Component> added = new List<Component>();
        /// <summary>Unity constraints of an object the tool did not create: they become VRChat constraints on the build copy only.</summary>
        [HideInInspector] public List<Component> convertAtBuild = new List<Component>();
        /// <summary>What installing changed on an object the tool did not create, put back when the accessory is removed.</summary>
        [HideInInspector] public InstallChanges changes = new InstallChanges();
        /// <summary>
        /// The transforms moved or resized to fit the avatar's armature (clothing made for another avatar), as they were and as
        /// the fit left them: cancelling the fit puts back those still as it left them.
        /// </summary>
        [HideInInspector] public List<FittedPose> fitted = new List<FittedPose>();

        [HideInInspector] public List<FittedMesh> fittedMeshes = new List<FittedMesh>();

        /// <summary>
        /// Set when it was installed from My Avatar's asset gallery: the asset, release and package it came from, so the
        /// gallery can show it as installed, offer its update and remove it.
        /// </summary>
        [HideInInspector] public GalleryReceipt gallery = new GalleryReceipt();

        [Serializable]
        public sealed class GalleryReceipt
        {
            public int assetId, releaseId, variantId;
            public string assetName, version, setup, sha256, creatorName;
            /// <summary>The installation journal entry that placed it (retries find it instead of adding a copy).</summary>
            public string installId;
            public long installedAtTicks;
            public bool Installed => assetId > 0;
        }

        [Serializable]
        public sealed class FittedMesh
        {
            public SkinnedMeshRenderer renderer;
            public Mesh before, after;
            public Bounds beforeBounds, afterBounds;
        }

        [Serializable]
        public struct FittedPose
        {
            public Transform transform;
            public LocalPose before, after;
        }

        /// <summary>Each value as it was before the tool changed it and as the tool left it: removal puts back only what is still as the tool left it.</summary>
        [Serializable]
        public sealed class InstallChanges
        {
            public bool moved;
            public LocalPose poseBefore, poseAfter;
            public List<ConstraintChange> constraints = new List<ConstraintChange>();
            public List<WeightChange> weights = new List<WeightChange>();
        }

        [Serializable]
        public struct LocalPose
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
        }

        [Serializable]
        public sealed class ConstraintChange
        {
            public Component constraint;
            public ConstraintState before, after;
        }

        /// <summary>The part of a Unity or VRChat constraint the tool sets: its sources, offsets, weight and switches.</summary>
        [Serializable]
        public sealed class ConstraintState
        {
            public float weight;
            public bool active, locked;
            public Vector3 positionOffset, rotationOffset;
            public List<ConstraintSourceState> sources = new List<ConstraintSourceState>();
        }

        [Serializable]
        public struct ConstraintSourceState
        {
            public Transform transform;
            public float weight;
            public Vector3 positionOffset, rotationOffset;
        }

        [Serializable]
        public struct WeightChange
        {
            public SkinnedMeshRenderer renderer;
            public string shape;
            public float before, after;
        }
    }
}
