using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.VRChat.Attachments
{
    /// <summary>
    /// Puts back the meshes the drop of an attachment once pushed out of the body (a coarse clearance by the bones' volume,
    /// no longer applied: clipping is left for ReFit, which fits the clothing to the body). An attachment fitted that way keeps
    /// its record until its fit is cancelled.
    /// </summary>
    public static class AttachmentVolumeFit
    {
        internal static void Cancel(OrbitersAttachment attachment)
        {
            foreach (var fit in attachment.fittedMeshes)
            {
                var refit = RefitRecords.Find(fit.renderer);
                if (refit && refit.Applied && refit.original.mesh == fit.after) RefitRecords.Discard(refit);
                if (!fit.renderer || fit.renderer.sharedMesh != fit.after) continue;
                Undo.RecordObject(fit.renderer, "Cancel clothing volume fit");
                fit.renderer.sharedMesh = fit.before;
                fit.renderer.localBounds = fit.beforeBounds;
                PrefabUtility.RecordPrefabInstancePropertyModifications(fit.renderer);
            }
            Undo.RecordObject(attachment, "Cancel clothing volume fit");
            attachment.fittedMeshes.Clear();
        }
    }
}
