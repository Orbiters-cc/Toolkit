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
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
    }

    /// <summary>A skinned renderer's mesh, skinning, pose and blendshape weights, as captured before or after a refit.</summary>
    [Serializable]
    public sealed class RefitRendererState
    {
        public Mesh mesh;
        public List<Transform> bones = new List<Transform>();
        /// <summary>Bone paths under the avatar root; null for a bone outside it.</summary>
        public List<string> bonePaths = new List<string>();
        public bool rootBoneCaptured;
        public Transform rootBone;
        public string rootBonePath;
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

        public bool Applied => mesh != null && TryGetComponent<SkinnedMeshRenderer>(out var renderer) && renderer.sharedMesh == mesh;
    }
}
