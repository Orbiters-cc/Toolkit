using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Props that fit themselves to the avatar they are attached to (the drawing pen and the hand screen bind their hands
    /// and spawn point). Tools call <see cref="Prepare"/> on a placed object before analysing it, so the fitted object is
    /// what gets attached.
    /// </summary>
    public static class AttachmentHooks
    {
        private static readonly List<Action<GameObject, Transform>> Hooks = new List<Action<GameObject, Transform>>();
        private static readonly List<Func<GameObject, bool>> Claims = new List<Func<GameObject, bool>>();

        /// <param name="claims">Whether an object holds a prop <paramref name="hook"/> fits.</param>
        public static void Register(Action<GameObject, Transform> hook, Func<GameObject, bool> claims = null)
        {
            if (hook != null && !Hooks.Contains(hook)) Hooks.Add(hook);
            if (claims != null && !Claims.Contains(claims)) Claims.Add(claims);
        }

        /// <summary>
        /// True when <paramref name="root"/> holds a prop that only fits an avatar through this Toolkit: a package
        /// distributing it needs this Toolkit version or a later one, even when it refers to none of its files.
        /// </summary>
        public static bool Fits(GameObject root) => root != null && Claims.Any(claim => claim(root));

        public static void Prepare(GameObject placed, Transform avatar)
        {
            if (placed == null || avatar == null) return;
            foreach (var hook in Hooks) hook(placed, avatar);
        }
    }
}
