using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Props that fit themselves to the avatar they are attached to (the drawing pen binds its hands and spawn point).
    /// Tools call <see cref="Prepare"/> on a placed object before analysing it, so the fitted object is what gets attached.
    /// </summary>
    public static class AttachmentHooks
    {
        private static readonly List<Action<GameObject, Transform>> Hooks = new List<Action<GameObject, Transform>>();

        public static void Register(Action<GameObject, Transform> hook) { if (hook != null && !Hooks.Contains(hook)) Hooks.Add(hook); }

        public static void Prepare(GameObject placed, Transform avatar)
        {
            if (placed == null || avatar == null) return;
            foreach (var hook in Hooks) hook(placed, avatar);
        }
    }
}
