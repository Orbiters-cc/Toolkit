using System;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>
    /// Recognises a custom base by its model file when no tool on the avatar knows it: <see cref="BaseFingerprint"/> looks
    /// the body's model file up on the Orbiters server; this keeps the answers that are a released custom base version.
    /// </summary>
    public static class CustomBaseFingerprint
    {
        /// <summary>
        /// The custom base the body's model file belongs to, or null (an original base, an unknown or edited model, no
        /// connection). Call on the main thread; the work runs in the background.
        /// </summary>
        public static async Task<CustomBaseInfo> IdentifyAsync(SkinnedMeshRenderer body, CancellationToken cancellation)
        {
            var identity = await BaseFingerprint.IdentifyAsync(body, cancellation);
            if (identity == null || !identity.Custom || body == null || body.sharedMesh == null) return null;
            string meshPath = AssetDatabase.GetAssetPath(body.sharedMesh);
            return new CustomBaseInfo
            {
                Key = "orbiters:" + identity.AssetId + ":" + identity.Version,
                Name = string.IsNullOrEmpty(identity.Version) ? identity.AssetName : identity.AssetName + " " + identity.Version,
                BaseName = identity.BaseName,
                AssetId = identity.AssetId,
                Version = identity.Version,
                Body = body,
                Shapes = CustomBases.Shapes(identity.Shapes, body.sharedMesh),
                Source = "Orbiters",
                // Unity's preview of the body's model, loaded in the background by the editor.
                Thumbnail = () => AssetPreview.GetAssetPreview(AssetDatabase.LoadMainAssetAtPath(meshPath)),
            };
        }
    }
}
