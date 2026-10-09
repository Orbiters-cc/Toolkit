using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>
    /// Installation identity of pens made before Toolkit 0.3.19, kept so they load without a missing script. New pens do
    /// not carry it (Toolkit finds pens by their grab). Removed at build; the pen runs on native avatar components.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class OrbitersDrawingPen : MonoBehaviour, IEditorOnly
    {
        public string generatedFolder;
        public TrailRenderer ink;
        public Color color = new Color(.1f, .85f, 1f);
    }
}
