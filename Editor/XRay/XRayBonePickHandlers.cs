using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.XRay
{
    /// <summary>
    /// Tools that take the armature overlay's bone clicks over for a while, the way Mixer's Weight Paint sub-selects the
    /// clicked bone instead of selecting it. A handler gets the bone of a plain click (Shift and Ctrl clicks still select
    /// in Unity) on mouse down and returns true when it took the click; Unity's selection is then left alone. The bone a
    /// handler reports as picked is highlighted like a selected bone. The most recently registered handler is asked first.
    /// </summary>
    public static class XRayBonePickHandlers
    {
        private sealed class Handler
        {
            public string Id;
            public Func<Transform, bool> TryPick;
            public Func<Transform> Picked;
        }

        private static readonly List<Handler> Handlers = new List<Handler>();

        /// <summary>A handler came or went, or its picked bone changed.</summary>
        public static event Action Changed;

        /// <summary>
        /// Registers (or replaces) the handler <paramref name="id"/>: <paramref name="tryPick"/> takes a clicked bone when
        /// it returns true, <paramref name="picked"/> (optional) names the bone it shows as picked, or null.
        /// </summary>
        public static void Register(string id, Func<Transform, bool> tryPick, Func<Transform> picked = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Bone pick handler id cannot be empty.", nameof(id));
            if (tryPick == null) throw new ArgumentNullException(nameof(tryPick));
            Handlers.RemoveAll(h => h.Id == id);
            Handlers.Add(new Handler { Id = id, TryPick = tryPick, Picked = picked });
            NotifyChanged();
        }

        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (Handlers.RemoveAll(h => h.Id == id) > 0) NotifyChanged();
        }

        /// <summary>Hands a clicked bone to the handlers; true when one of them took it (the selection stays as it is).</summary>
        public static bool TryPick(Transform bone)
        {
            if (bone == null) return false;
            for (int i = Handlers.Count - 1; i >= 0; i--)
            {
                var handler = Handlers[i];
                try
                {
                    if (handler.TryPick(bone)) return true;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            return false;
        }

        /// <summary>Adds the bones the handlers show as picked to <paramref name="into"/>.</summary>
        public static void CollectPicked(HashSet<Transform> into)
        {
            if (into == null) return;
            foreach (var handler in Handlers)
            {
                if (handler.Picked == null) continue;
                Transform bone;
                try { bone = handler.Picked(); }
                catch (Exception e) { Debug.LogException(e); continue; }
                if (bone != null) into.Add(bone);
            }
        }

        /// <summary>A handler's picked bone changed: the overlay redraws its highlight.</summary>
        public static void NotifyChanged()
        {
            Changed?.Invoke();
            SceneView.RepaintAll();
        }
    }
}
