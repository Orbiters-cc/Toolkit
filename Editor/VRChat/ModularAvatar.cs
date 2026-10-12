using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>Recognizes Modular Avatar's components without referencing Modular Avatar.</summary>
    public static class ModularAvatar
    {
        /// <summary>Whether a component is one of Modular Avatar's (what it holds builds at upload).</summary>
        public static bool IsComponent(Component value) =>
            value != null && (value.GetType().Namespace ?? "").StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal);
    }
}
