using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// What an avatar tool (My Avatar) does for an avatar's objects in views that are not its own, such as Mixer's
    /// Hierarchy: drops on the avatar root, as on the tool's drop field, and the ReFit question of an item it added.
    /// </summary>
    public interface IAvatarObjectTool
    {
        /// <summary>The avatar root takes drops (the tool is on it).</summary>
        bool TakesDrops(GameObject avatarRoot);
        /// <summary>A drop on the avatar root is being applied.</summary>
        bool Busy(GameObject avatarRoot);
        /// <summary>Drops project or file paths on the avatar root, as on the tool's own drop field.</summary>
        void Drop(GameObject avatarRoot, string[] paths);
        /// <summary>The item, added to an avatar by the tool, seems not to fit it: a ReFit question waits.</summary>
        bool AsksForRefit(GameObject item);
        /// <summary>Answers that question with ReFit, as the tool's own fit card does.</summary>
        void Refit(GameObject item);
    }

    /// <summary>
    /// A newer version waiting for one of an avatar's objects: an item a tool added (My Avatar's gallery assets) or the
    /// custom base the avatar uses (MCB), as the tool's own Update button offers it.
    /// </summary>
    public sealed class AvatarObjectUpdate
    {
        /// <summary>What it updates, as people know it ("N4N0", "Ultipaw 2").</summary>
        public string Name;
        /// <summary>The version on the avatar and the newer one.</summary>
        public string From, To;
        /// <summary>The update is under way; <see cref="Status"/> says how far.</summary>
        public bool Running;
        /// <summary>The last try failed; <see cref="Status"/> says why, and starting again retries it.</summary>
        public bool Failed;
        public string Status;
        /// <summary>Starts it as the tool's own Update button does, with its questions and progress (or tries again).</summary>
        public Action Start;
    }

    /// <summary>A tool that knows when one of an avatar's objects has a newer version.</summary>
    public interface IAvatarObjectUpdates
    {
        /// <summary>
        /// Main thread and cheap, from what the tool knows now: it may look further in the background, then raises
        /// <see cref="AvatarObjectTools.NotifyChanged"/>. Null when nothing newer waits for the object.
        /// </summary>
        AvatarObjectUpdate UpdateOf(GameObject go);
    }

    /// <summary>
    /// The avatar tools registered at load (<see cref="IAvatarObjectTool"/>, <see cref="IAvatarObjectUpdates"/>), for views
    /// that show an avatar's objects. Views refresh on <see cref="Changed"/>, which tools raise when what they answer
    /// changes (a drop, a fit question, an update found or under way).
    /// </summary>
    public static class AvatarObjectTools
    {
        private static readonly List<IAvatarObjectTool> Tools = new List<IAvatarObjectTool>();
        private static readonly List<IAvatarObjectUpdates> UpdateSources = new List<IAvatarObjectUpdates>();

        /// <summary>Raised when a tool's answers changed.</summary>
        public static event Action Changed;

        public static void Register(IAvatarObjectTool tool)
        {
            if (tool == null || Tools.Contains(tool)) return;
            Tools.Add(tool);
            NotifyChanged();
        }

        public static void RegisterUpdates(IAvatarObjectUpdates source)
        {
            if (source == null || UpdateSources.Contains(source)) return;
            UpdateSources.Add(source);
            NotifyChanged();
        }

        public static void UnregisterUpdates(IAvatarObjectUpdates source)
        {
            if (UpdateSources.Remove(source)) NotifyChanged();
        }

        public static void NotifyChanged() => Changed?.Invoke();

        /// <summary>The newer version waiting for <paramref name="go"/> (an item, or an avatar root's custom base), or null.</summary>
        public static AvatarObjectUpdate UpdateOf(GameObject go)
        {
            if (go == null) return null;
            foreach (var source in UpdateSources)
            {
                try
                {
                    var update = source.UpdateOf(go);
                    if (update != null) return update;
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            return null;
        }

        /// <summary>The tool that takes drops on <paramref name="avatarRoot"/>, or null.</summary>
        public static IAvatarObjectTool DropTarget(GameObject avatarRoot) => First(avatarRoot, (tool, go) => tool.TakesDrops(go));

        /// <summary>The tool asking whether <paramref name="item"/> should be refitted, or null.</summary>
        public static IAvatarObjectTool RefitQuestion(GameObject item) => First(item, (tool, go) => tool.AsksForRefit(go));

        // A tool that throws answers no: a view must keep drawing whatever one tool does.
        private static IAvatarObjectTool First(GameObject go, Func<IAvatarObjectTool, GameObject, bool> answers)
        {
            if (go == null) return null;
            foreach (var tool in Tools)
            {
                try { if (answers(tool, go)) return tool; }
                catch (Exception e) { Debug.LogException(e); }
            }
            return null;
        }
    }
}
