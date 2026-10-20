using System;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Textures
{
    /// <summary>
    /// An image another tool painted for one material slot of an avatar's renderer: My Avatar showing what Substance 3D
    /// Painter streams (<see cref="Painted.Final"/> false: a file Painter replaces soon), then applying the maps Painter
    /// saved (true: an image of the project). Tools that also hold the avatar's meshes (MCB's Blender link) can show it too.
    /// </summary>
    public static class AvatarTextureEdits
    {
        public struct Painted
        {
            public Renderer Renderer;
            public int Slot;
            /// <summary>Painter's channel: basecolor, normal (OpenGL), roughness, metallic, emissive, ambientocclusion.</summary>
            public string Channel;
            /// <summary>
            /// The image's absolute path: one the tool that notifies checked is its own (My Avatar's Painter link folder, a
            /// project texture), never a path taken as it came from another application.
            /// </summary>
            public string Path;
            public bool Final;
        }

        /// <summary>Raised on the main thread for each slot an image was painted for.</summary>
        public static event Action<Painted> Changed;

        /// <summary>Tells every listener; one that fails is logged and the others still hear it.</summary>
        public static void Notify(Painted painted)
        {
            if (painted.Renderer == null || Changed == null) return;
            foreach (Action<Painted> listener in Changed.GetInvocationList())
            {
                try { listener(painted); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
