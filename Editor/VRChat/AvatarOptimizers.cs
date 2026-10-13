using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// Recognizes, without referencing them, the components of the optimizers that build after Orbiters' build-copy steps:
    /// Avatar Optimizer and d4rk Avatar Optimizer (both at -1025). They are editor-only, but what they name (renderers to
    /// merge, blendshapes to remove by, objects to leave alone) must still be there when they run.
    /// </summary>
    public static class AvatarOptimizers
    {
        public static bool IsComponent(Component value)
        {
            if (value == null) return false;
            var type = value.GetType();
            return (type.Namespace ?? "").StartsWith("Anatawa12.AvatarOptimizer", StringComparison.Ordinal) || type.Name == "d4rkAvatarOptimizer";
        }
    }
}
