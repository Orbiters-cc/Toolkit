using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>
    /// Keeps rigid accessories on the body when body blendshapes change (beta): a piercing on a chest that grows with a
    /// "Muscles" shape moves and tilts with the skin under it. Applied to the upload or play copy only: each accessory gets
    /// a blendshape per body blendshape that moves the skin under it, driven by the body's weights and animations.
    /// </summary>
    [AddComponentMenu("Orbiters/Follow Body Blendshapes (beta)"), DisallowMultipleComponent]
    public sealed class OrbitersSurfaceFollow : MonoBehaviour, IEditorOnly
    {
        public enum Scope
        {
            /// <summary>The meshes of this object and its children.</summary>
            ThisObject,
            /// <summary>Every small, rigid accessory of the avatar close to the skin (on the avatar root).</summary>
            SmallAccessories,
        }

        [Tooltip("This object's meshes, or every small accessory of the avatar that sits on the skin (put it on the avatar root).")]
        public Scope scope = Scope.ThisObject;
        [Tooltip("The body mesh to follow. Empty: the largest skinned mesh with blendshapes of the avatar.")]
        public SkinnedMeshRenderer body;
        [Tooltip("Also tilt with the skin, not only move with it.")]
        public bool rotate = true;
        [Tooltip("Small accessories: the largest size (metres) of what counts as an accessory to follow.")]
        [Range(0.01f, 0.5f)] public float maximumSize = 0.15f;
        [Tooltip("Small accessories: the farthest (metres) an accessory can sit from the skin.")]
        [Range(0.002f, 0.1f)] public float maximumGap = 0.03f;
    }
}
