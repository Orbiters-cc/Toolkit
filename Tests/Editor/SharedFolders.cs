using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Tests
{
    /// <summary>
    /// The project's shared folders a test may make (Orbiters' menu icons, written the first time a prop, a toggle or a
    /// menu needs them): <see cref="End"/> deletes those that were not there at <see cref="Begin"/>, a parent made for them
    /// once it is empty.
    /// </summary>
    internal sealed class SharedFolders
    {
        // Outermost first.
        private static readonly string[] Folders = { "Assets/Orbiters", VRChat.OrbitersMenuIcons.Folder };
        private readonly HashSet<string> existing = new HashSet<string>();

        public static SharedFolders Begin()
        {
            var folders = new SharedFolders();
            foreach (var folder in Folders) if (AssetDatabase.IsValidFolder(folder)) folders.existing.Add(folder);
            return folders;
        }

        public void End()
        {
            foreach (var folder in Folders.Reverse())
                if (!existing.Contains(folder) && AssetDatabase.IsValidFolder(folder) &&
                    (folder == VRChat.OrbitersMenuIcons.Folder || AssetDatabase.FindAssets("", new[] { folder }).Length == 0))
                    AssetDatabase.DeleteAsset(folder);
        }
    }
}
