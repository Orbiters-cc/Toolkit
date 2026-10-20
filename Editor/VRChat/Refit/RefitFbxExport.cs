using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.VRChat.Posing;
using Orbiters.Toolkit.VRChat;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// Refitted meshes to FBX as they look fitted, for every tool that refits (MCB, My Avatar, ReFit): the blendshape that
    /// carries the fit is applied to the mesh at the weight the renderer shows it (the tools keep it at 100) and left out,
    /// the body's blendshapes the mesh was given stay blendshapes at their weights, bones and bind poses as on the renderer.
    /// The clothing is written where it rests: a posing preview is paused meanwhile.
    /// </summary>
    public static class RefitFbxExport
    {
        /// <summary>The name the refit engine gives the shape carrying the fit, when nothing else tells it apart.</summary>
        public const string DefaultFitShape = RefitRecords.DefaultFitShape;

        /// <summary>
        /// The blendshape carrying the fit of an applied refit (the first one its mesh has that the original mesh had not and
        /// that is not one of the body's shapes it was given), or null: a refit that only added the body's shapes has none.
        /// </summary>
        public static string FitShape(OrbitersRefit record) => record != null && record.Applied ? RefitRecords.FitShape(record) : null;

        /// <summary>
        /// A renderer to export as it looks: <paramref name="fitShape"/> (by default the fit of its refit record, see
        /// <see cref="FitShape"/>) applied and left out.
        /// </summary>
        public static FbxExportMesh Item(SkinnedMeshRenderer renderer, string fitShape = null)
        {
            var item = FbxExportMesh.From(renderer);
            string shape = !string.IsNullOrEmpty(fitShape) ? fitShape : FitShape(RefitRecords.Find(renderer));
            if (!string.IsNullOrEmpty(shape) && item.Mesh != null && item.Mesh.GetBlendShapeIndex(shape) >= 0) item.Baked.Add(shape);
            return item;
        }

        /// <summary>The renderers under <paramref name="root"/> (itself included) that wear their refit.</summary>
        public static List<SkinnedMeshRenderer> Applied(Transform root) =>
            root == null ? new List<SkinnedMeshRenderer>() :
            root.GetComponentsInChildren<OrbitersRefit>(true).Where(r => r.Applied).Select(r => r.GetComponent<SkinnedMeshRenderer>())
                .Where(r => r != null).Distinct().ToList();

        /// <summary>
        /// Writes <paramref name="renderers"/> to one FBX file, with the bones they share once (<see cref="FbxExport.Write"/>);
        /// <paramref name="fitShapes"/> names the fit shape of a renderer that has no refit record to tell it.
        /// </summary>
        public static FbxExportResult Write(string path, IEnumerable<SkinnedMeshRenderer> renderers,
            IReadOnlyDictionary<SkinnedMeshRenderer, string> fitShapes = null, Transform root = null)
        {
            using (AccessoryPoseSync.Pause())
            {
                var items = (renderers ?? Enumerable.Empty<SkinnedMeshRenderer>()).Where(r => r != null).Distinct()
                    .Select(r => Item(r, fitShapes != null && fitShapes.TryGetValue(r, out string shape) ? shape : null)).ToList();
                if (items.Count == 0) return new FbxExportResult { Path = path, Error = "The refitted meshes are gone." };
                return FbxExport.Write(path, items, root);
            }
        }

        /// <summary>The file name offered for an object's refit: "&lt;object&gt;_ReFit.fbx".</summary>
        public static string FileName(string objectName) => FbxExportDialog.FileName((string.IsNullOrEmpty(objectName) ? "Mesh" : objectName) + "_ReFit");

        /// <summary>
        /// Asks where to save, then writes the renderers (<see cref="Write"/>) and shows the file (<see cref="FbxExportDialog"/>);
        /// the save panel opens on the next editor update, once the press that asked has painted.
        /// </summary>
        public static void Save(IEnumerable<SkinnedMeshRenderer> renderers, string objectName, IReadOnlyDictionary<SkinnedMeshRenderer, string> fitShapes = null)
        {
            var list = (renderers ?? Enumerable.Empty<SkinnedMeshRenderer>()).Where(r => r != null).Distinct().ToList();
            if (list.Count == 0) return;
            FbxExportDialog.Save(FileName(objectName), path => Write(path, list, fitShapes));
        }
    }
}
