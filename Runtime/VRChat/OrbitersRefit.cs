using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>One transform as it was before a refit, so restoring puts its pose back.</summary>
    [Serializable]
    public sealed class RefitTransformState
    {
        public Transform transform;
        /// <summary>Path under the avatar root, used when the reference is gone (a saved copy, a reloaded scene).</summary>
        public string path;
        /// <summary>
        /// For each segment of <see cref="path"/>, its position among same-named siblings: tells apart objects that share a
        /// name. Empty when each is the first of its name.
        /// </summary>
        public List<int> siblingOrdinals = new List<int>();
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
    }

    /// <summary>The sibling ordinals of one saved path (see <see cref="RefitTransformState.siblingOrdinals"/>).</summary>
    [Serializable]
    public sealed class RefitSiblingOrdinals
    {
        public List<int> ordinals = new List<int>();
    }

    /// <summary>A skinned renderer's mesh, skinning, pose and blendshape weights, as captured before or after a refit.</summary>
    [Serializable]
    public sealed class RefitRendererState
    {
        public Mesh mesh;
        public List<Transform> bones = new List<Transform>();
        /// <summary>Bone paths under the avatar root; null for a bone outside it.</summary>
        public List<string> bonePaths = new List<string>();
        /// <summary>The sibling ordinals of each bone path; empty when no bone path has a duplicate name in it.</summary>
        public List<RefitSiblingOrdinals> boneSiblingOrdinals = new List<RefitSiblingOrdinals>();
        public bool rootBoneCaptured;
        public Transform rootBone;
        public string rootBonePath;
        public List<int> rootBoneSiblingOrdinals = new List<int>();
        public List<RefitTransformState> transforms = new List<RefitTransformState>();
        public List<string> blendShapeNames = new List<string>();
        public List<float> blendShapeWeights = new List<float>();
        public bool rendererCaptured;
        public bool updateWhenOffscreen;
        public Bounds localBounds;
    }

    /// <summary>A generated blendshape of a refitted mesh and the body blendshape it follows.</summary>
    [Serializable]
    public struct RefitShape
    {
        public string source;
        public string generated;

        public RefitShape(string source, string generated)
        {
            this.source = source;
            this.generated = generated;
        }
    }

    /// <summary>
    /// A fit of a refitted renderer kept to choose from (looser, tighter...): the renderer as that fit left it, and its
    /// generated blendshapes. In VRChat the avatar's menu picks one of the kept fits.
    /// </summary>
    [Serializable]
    public sealed class RefitKeptFit
    {
        public string name;
        /// <summary>The renderer as this fit left it (mesh, skinning, pose): what using it again puts back.</summary>
        public RefitRendererState state = new RefitRendererState();
        public string meshPath;
        public List<RefitShape> shapes = new List<RefitShape>();

        public Mesh Mesh => state != null ? state.mesh : null;
    }

    /// <summary>
    /// A clothing or accessory mesh refitted by an Orbiters tool: how it was before (Restore puts it back) and which of its
    /// blendshapes follow which body blendshapes. When the avatar is built, every animation of those body blendshapes also
    /// drives them. Applies only while the renderer still uses <see cref="mesh"/>.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class OrbitersRefit : MonoBehaviour, IEditorOnly
    {
        public enum FitKind
        {
            /// <summary>Fitted from the original base body to this avatar's body, with the body's blendshapes.</summary>
            Fitted,
            /// <summary>Already fitted this body: only the body's blendshapes were added.</summary>
            Shapes,
        }

        [Tooltip("The body whose blendshapes this mesh follows.")]
        public SkinnedMeshRenderer body;
        public FitKind kind;
        /// <summary>The mesh the refit produced.</summary>
        [HideInInspector] public Mesh mesh;
        [HideInInspector] public string meshPath;
        [HideInInspector] public List<RefitShape> shapes = new List<RefitShape>();
        [HideInInspector] public RefitRendererState original = new RefitRendererState();
        /// <summary>The custom base and version it was fitted for (the tool's key), or empty.</summary>
        [HideInInspector] public string baseKey;
        /// <summary>Display name of that custom base.</summary>
        [HideInInspector] public string baseName;
        /// <summary>The tool that made it ("MCB", "My Avatar", "ReFit").</summary>
        [HideInInspector] public string tool;

        /// <summary>
        /// Skin of <see cref="body"/> moved inwards while this clothing is worn (where ReFit's motion check found it coming
        /// through in motion): body vertices and their offsets in the body mesh's space, before skinning. Empty for none. The
        /// build adds it to the body as a blendshape that follows whatever shows the clothing.
        /// </summary>
        [HideInInspector] public List<int> bodyShrinkVertices = new List<int>();
        [HideInInspector] public List<Vector3> bodyShrinkOffsets = new List<Vector3>();
        /// <summary>The body mesh the shrink was planned on: on another mesh (a version switched since) it is left out.</summary>
        [HideInInspector] public Mesh bodyShrinkMesh;

        /// <summary>
        /// Fits of this renderer kept to choose from, the current one among them when it was kept. When the avatar is built,
        /// each other kept fit becomes a blendshape of the current mesh, and the avatar's menu ("Fits") picks one of them.
        /// </summary>
        [HideInInspector] public List<RefitKeptFit> keptFits = new List<RefitKeptFit>();

        public bool HasBodyShrink => bodyShrinkVertices != null && bodyShrinkVertices.Count > 0 && bodyShrinkOffsets != null &&
                                     bodyShrinkOffsets.Count == bodyShrinkVertices.Count;

        public bool Applied => mesh != null && TryGetComponent<SkinnedMeshRenderer>(out var renderer) && renderer.sharedMesh == mesh;
    }
}
