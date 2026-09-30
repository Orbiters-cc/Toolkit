using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// A small picture that never makes the UI wait: Unity renders asset previews in the background, so the picture is
    /// asked for a few times a second and shown (with the <see cref="ShownClass"/> class, for a fade) once it exists.
    /// Nothing comes after a while (no preview for that asset): the picture is left hidden.
    /// </summary>
    public sealed class AssetThumbnail : Image
    {
        public const string ShownClass = "orb-thumbnail--shown";
        private const float Wait = 6f;
        private IVisualElementScheduledItem poll;

        /// <summary>The preview of an asset (a prefab, model or texture).</summary>
        public static AssetThumbnail Of(string assetPath) => new AssetThumbnail(() =>
        {
            var asset = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset != null ? AssetPreview.GetAssetPreview(asset) : null;
        }, !string.IsNullOrEmpty(assetPath));

        /// <param name="source">The picture, or null while it loads.</param>
        public AssetThumbnail(Func<Texture2D> source, bool expected = true)
        {
            scaleMode = ScaleMode.ScaleAndCrop;
            pickingMode = PickingMode.Ignore;
            AddToClassList("orb-thumbnail");
            if (source == null || !expected) return;
            double until = EditorApplication.timeSinceStartup + Wait;
            if (TryShow(source)) return;
            poll = schedule.Execute(() =>
            {
                if (TryShow(source) || EditorApplication.timeSinceStartup > until) poll.Pause();
            }).Every(150);
        }

        public bool Shown => image != null;

        private bool TryShow(Func<Texture2D> source)
        {
            Texture2D texture;
            try { texture = source(); }
            catch (Exception) { texture = null; }
            if (texture == null) return false;
            image = texture;
            AddToClassList(ShownClass);
            return true;
        }
    }
}
