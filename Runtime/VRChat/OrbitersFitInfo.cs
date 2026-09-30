using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>
    /// Added by a clothing or accessory creator to their prefab: the body this item was made for. Orbiters tools read it when
    /// the item is put on an avatar with a custom base, and offer the right refit instead of guessing.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("Orbiters/Fit Info")]
    public sealed class OrbitersFitInfo : MonoBehaviour, IEditorOnly
    {
        [Tooltip("The avatar base the item was modelled on, as named on its store page.")]
        public string baseName;
        [Tooltip("The Orbiters id of the custom base it was made for. 0: made for the original base.")]
        public int customBaseAssetId;
        [Tooltip("The custom base version it was made for. Empty: any version.")]
        public string customBaseVersion;
        [Tooltip("Body blendshapes this item must never get (a shape the creator decided it should ignore).")]
        public List<string> excludedShapes = new List<string>();

        public bool MadeForCustomBase => customBaseAssetId > 0;
    }
}
