using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>
    /// More posing controls for the XRay Gizmos window, from assemblies that need what this one does not (the VRChat
    /// clothing preview). The Scene toolbar lists their elements by id; an id nothing registers shows nothing.
    /// </summary>
    public static class XRayPosingExtensions
    {
        public const string ClothingToolbarId = "Orbiters/XRayGizmos/Clothing";
        private static readonly List<Func<VisualElement>> Controls = new List<Func<VisualElement>>();

        public static void Register(Func<VisualElement> build)
        {
            if (build != null && !Controls.Contains(build)) Controls.Add(build);
        }

        internal static IEnumerable<VisualElement> Build()
        {
            foreach (var build in Controls)
            {
                var element = build();
                if (element != null) yield return element;
            }
        }
    }
}
