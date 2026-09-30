using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>A refit kept for reuse: the generated mesh and its shapes, found again by what went into it.</summary>
    public sealed class RefitCacheEntry : ScriptableObject
    {
        public Mesh mesh;
        public string primaryShape;
        public List<RefitShape> shapes = new List<RefitShape>();
        /// <summary>The engine's binding data, for later blendshape passes.</summary>
        public string metadata;
        /// <summary>The result's warnings and errors (a rough fit stays rough when reused).</summary>
        public List<RefitMessage> messages = new List<RefitMessage>();
        public string engine;
    }
}
