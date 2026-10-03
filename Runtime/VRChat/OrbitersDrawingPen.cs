using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.Toolkit.VRChat
{
    /// <summary>Installation identity only. Removed at build; the pen runs on native avatar components.</summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class OrbitersDrawingPen : MonoBehaviour, IEditorOnly
    {
        public string generatedFolder;
        public TrailRenderer ink;
        public Color color = new Color(.1f, .85f, 1f);
    }
}
