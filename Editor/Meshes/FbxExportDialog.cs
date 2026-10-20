using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Meshes
{
    /// <summary>
    /// Saving an FBX export where the user picks: the save panel opens in the folder used last (the project's at first);
    /// a file inside Assets/ is imported and pinged, one elsewhere shown in the file browser; a failure is said in a dialog,
    /// warnings go to the Console.
    /// </summary>
    public static class FbxExportDialog
    {
        private const string FolderKey = "Orbiters.Toolkit.FbxExport.Folder";
        private const string Title = "Export FBX";

        /// <summary>An FBX file name for an object: its name, without what a file name can't hold.</summary>
        public static string FileName(string name) => MeshAssets.Clean(name) + ".fbx";

        /// <summary>Asks where to save <paramref name="fileName"/>; null when cancelled. Remembers the folder.</summary>
        public static string AskPath(string fileName)
        {
            string folder = EditorPrefs.GetString(FolderKey, "");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = ProjectFolder;
            string path = EditorUtility.SaveFilePanel(Title, folder, fileName, "fbx");
            if (string.IsNullOrEmpty(path)) return null;
            EditorPrefs.SetString(FolderKey, Path.GetDirectoryName(path));
            return path;
        }

        /// <summary>
        /// On the next editor update (the press that asked paints first): asks where to save, writes the file through
        /// <paramref name="write"/> and shows it (<see cref="Show"/>).
        /// </summary>
        public static void Save(string fileName, Func<string, FbxExportResult> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            EditorApplication.CallbackFunction once = null;
            once = () =>
            {
                EditorApplication.update -= once;
                string path = AskPath(fileName);
                if (path == null) return;
                FbxExportResult result;
                try
                {
                    EditorUtility.DisplayProgressBar(Title, "Writing " + Path.GetFileName(path) + "…", 0.5f);
                    result = write(path);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                Show(result);
            };
            EditorApplication.update += once;
        }

        /// <summary>Imports and pings a file written inside Assets/, reveals one elsewhere; a failure in a dialog.</summary>
        public static void Show(FbxExportResult result)
        {
            if (result == null) return;
            if (!result.Success)
            {
                EditorUtility.DisplayDialog(Title, "The FBX file was not written: " + result.Error, "OK");
                return;
            }
            if (result.Warnings.Count > 0) Debug.LogWarning(Title + " (" + Path.GetFileName(result.Path) + "):\n" + string.Join("\n", result.Warnings));
            string asset = AssetPath(result.Path);
            if (asset == null)
            {
                EditorUtility.RevealInFinder(result.Path);
                return;
            }
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate);
            var imported = AssetDatabase.LoadMainAssetAtPath(asset);
            if (imported != null) EditorGUIUtility.PingObject(imported);
        }

        /// <summary>The project path ("Assets/...") of a file inside the project's Assets folder, else null.</summary>
        public static string AssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full = Path.GetFullPath(path);
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(assets, StringComparison.OrdinalIgnoreCase) ? "Assets/" + full.Substring(assets.Length).Replace('\\', '/') : null;
        }

        private static string ProjectFolder => Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
    }

    /// <summary>"Export FBX…" on a renderer's context menu: the mesh as it shows, with its bones and blendshapes.</summary>
    internal static class FbxExportMenu
    {
        [MenuItem("CONTEXT/SkinnedMeshRenderer/Export FBX…")]
        private static void ExportSkinned(MenuCommand command) => Export(command.context as Renderer);

        [MenuItem("CONTEXT/SkinnedMeshRenderer/Export FBX…", true)]
        private static bool CanExportSkinned(MenuCommand command) => command.context is SkinnedMeshRenderer renderer && renderer.sharedMesh != null;

        [MenuItem("CONTEXT/MeshRenderer/Export FBX…")]
        private static void ExportStatic(MenuCommand command) => Export(command.context as Renderer);

        [MenuItem("CONTEXT/MeshRenderer/Export FBX…", true)]
        private static bool CanExportStatic(MenuCommand command)
        {
            var filter = command.context is MeshRenderer renderer ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null && filter.sharedMesh != null;
        }

        private static void Export(Renderer renderer)
        {
            if (renderer == null) return;
            FbxExportDialog.Save(FbxExportDialog.FileName(renderer.name), path => FbxExport.Write(path, new[] { FbxExportMesh.From(renderer) }));
        }
    }
}
