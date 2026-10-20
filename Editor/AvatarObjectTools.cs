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
    /// The avatar tools registered at load (<see cref="IAvatarObjectTool"/>), for views that show an avatar's objects.
    /// Views refresh on <see cref="Changed"/>, which tools raise when what they answer changes (a drop, a fit question).
    /// </summary>
    public static class AvatarObjectTools
    {
        private static readonly List<IAvatarObjectTool> Tools = new List<IAvatarObjectTool>();

        /// <summary>Raised when a tool's answers changed.</summary>
        public static event Action Changed;

        public static void Register(IAvatarObjectTool tool)
        {
            if (tool == null || Tools.Contains(tool)) return;
            Tools.Add(tool);
            NotifyChanged();
        }

        public static void NotifyChanged() => Changed?.Invoke();

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
