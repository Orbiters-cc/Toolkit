using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    /// <summary>
    /// Photoshoot selections, live preview scene and icon caches. Owned by the host tool so it survives Inspector rebuilds;
    /// <see cref="PhotoshootPanel"/> is a view over it.
    /// </summary>
    public sealed class PhotoshootState : IDisposable
    {
        public const float MinZoom = 0.45f;
        public const float MaxZoom = 20f;
        public const float DefaultZoom = 1.35f;

        private readonly Dictionary<string, Texture2D> poseIconCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> lightIconCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private PhotoshootService.LivePreviewSession previewSession;

        public PhotoshootService.Catalog Catalog { get; private set; }
        public int BodyPoseIndex { get; set; }
        public int BackgroundIndex { get; set; }
        /// <summary>Colour of the solid background option.</summary>
        public Color BackgroundColor { get; set; } = PhotoshootService.DefaultBackgroundColor;
        /// <summary>The framing preset the current zoom and placement came from, or null once framing was adjusted by hand.</summary>
        public PhotoshootService.FramingPreset? FramingPreset { get; set; }
        public int LightPresetIndex { get; set; }
        public float Zoom { get; set; } = DefaultZoom;
        public Vector2 Placement { get; set; } = Vector2.zero;
        /// <summary>Turn of the avatar around itself, all the way round, kept between -180 and 180.</summary>
        public float RotationDegrees { get => rotationDegrees; set => rotationDegrees = Mathf.DeltaAngle(0f, value); }
        private float rotationDegrees;
        public HashSet<string> SelectedFaceBlendshapes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The style picker tab shown in the panel (pose, light, background or expression); survives host rebuilds.</summary>
        public int StyleTab { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool IsGenerating { get; internal set; }
        internal int RefreshTicket;

        public void Reset(bool destroyPreview)
        {
            RefreshTicket++;
            Catalog = null;
            BodyPoseIndex = 0;
            BackgroundIndex = 0;
            LightPresetIndex = 0;
            Zoom = DefaultZoom;
            Placement = Vector2.zero;
            RotationDegrees = 0f;
            BackgroundColor = PhotoshootService.DefaultBackgroundColor;
            FramingPreset = null;
            SelectedFaceBlendshapes.Clear();
            Status = null;
            Error = null;
            IsGenerating = false;
            ReleaseIcons();
            if (destroyPreview)
            {
                ClosePreview();
            }
        }

        public void Dispose()
        {
            ReleaseIcons();
            ClosePreview();
        }

        public void ClosePreview()
        {
            if (previewSession == null)
            {
                return;
            }

            previewSession.Dispose();
            previewSession = null;
        }

        internal void EnsureCatalog(GameObject avatarRoot)
        {
            if (Catalog == null)
            {
                Catalog = PhotoshootService.BuildCatalog(avatarRoot);
            }

            BodyPoseIndex = ClampIndex(BodyPoseIndex, Catalog.bodyPoses.Count);
            BackgroundIndex = ClampIndex(BackgroundIndex, Catalog.backgrounds.Count);
            LightPresetIndex = ClampIndex(LightPresetIndex, Catalog.lightPresets.Count);
            if (SelectedFaceBlendshapes.Count > 0)
            {
                var available = new HashSet<string>(Catalog.faceBlendshapes.Select(option => option.name), StringComparer.OrdinalIgnoreCase);
                SelectedFaceBlendshapes.RemoveWhere(name => !available.Contains(name));
            }
        }

        internal bool HasPreviewTexture(PhotoshootService.ShotKind shotKind) => previewSession?.GetPreviewTexture(shotKind) != null;

        internal Texture GetPreviewTexture(PhotoshootService.ShotKind shotKind) => previewSession?.GetPreviewTexture(shotKind);

        internal void RenderPreview(GameObject avatarRoot, PhotoshootService.ShotKind shotKind, Vector2Int size, bool forceFaceBlendshapeApply)
        {
            try
            {
                Error = null;
                var request = BuildRequest(avatarRoot, shotKind, size, forceFaceBlendshapeApply);
                if (previewSession == null)
                {
                    previewSession = new PhotoshootService.LivePreviewSession();
                }

                previewSession.UpdatePreview(request);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Status = null;
            }
        }

        /// <summary>Zoom and placement for a framing preset, measured on the avatar as posed in the live preview.</summary>
        /// <summary>
        /// True once after a render that posed the avatar copy or changed its expression; the host renders again over the
        /// next editor frames so every skinned mesh shows the new pose.
        /// </summary>
        internal bool TakeUnsettled()
        {
            if (previewSession == null || !previewSession.Unsettled) return false;
            previewSession.MarkSettled();
            return true;
        }

        internal bool TrySuggestFraming(PhotoshootService.ShotKind shotKind, Vector2Int size, PhotoshootService.FramingPreset preset, out float zoom, out Vector2 placement)
        {
            zoom = Zoom;
            placement = Placement;
            return previewSession != null && PhotoshootService.TrySuggestFraming(previewSession.LastFrame, shotKind, size, preset, out zoom, out placement);
        }

        /// <summary>How far placement moves for a drag across the whole frame, so a dragged avatar follows the pointer.</summary>
        internal Vector2 PlacementPerFrame(PhotoshootService.ShotKind shotKind, Vector2Int size) =>
            PhotoshootService.PlacementPerFrame(previewSession != null ? previewSession.LastFrame : default, shotKind, size, Zoom);

        internal Texture2D Capture(GameObject avatarRoot, PhotoshootService.ShotKind shotKind, Vector2Int size, bool withShotEffect = true)
        {
            var request = BuildRequest(avatarRoot, shotKind, size, false);
            if (previewSession == null)
            {
                previewSession = new PhotoshootService.LivePreviewSession();
            }

            var texture = previewSession.Capture(request, withShotEffect);
            if (texture == null)
            {
                throw new InvalidOperationException("Photoshoot image was not captured.");
            }

            texture.name = $"Orbiters Photoshoot {PhotoshootPanel.DisplayName(shotKind)}";
            return texture;
        }

        internal Texture2D GetLightIcon(PhotoshootService.LightPresetOption option)
        {
            string key = option.displayName ?? "light";
            if (lightIconCache.TryGetValue(key, out Texture2D texture) && texture != null)
            {
                return texture;
            }

            texture = PhotoshootIcons.CreateLightIcon(option, 64);
            lightIconCache[key] = texture;
            return texture;
        }

        internal Texture2D GetPoseIcon(GameObject avatarRoot, PhotoshootService.BodyPoseOption option)
        {
            string key = !string.IsNullOrWhiteSpace(option.assetPath) ? option.assetPath : "__default_pose";
            if (poseIconCache.TryGetValue(key, out Texture2D texture) && texture != null)
            {
                return texture;
            }

            texture = PhotoshootIcons.CreatePoseIcon(avatarRoot, option.clip, 64);
            poseIconCache[key] = texture;
            return texture;
        }

        private PhotoshootService.RenderRequest BuildRequest(GameObject avatarRoot, PhotoshootService.ShotKind shotKind, Vector2Int size, bool forceFaceBlendshapeApply)
        {
            EnsureCatalog(avatarRoot);
            return new PhotoshootService.RenderRequest
            {
                avatarRoot = avatarRoot,
                bodyPose = Catalog.bodyPoses.Count > 0 ? Catalog.bodyPoses[BodyPoseIndex].clip : null,
                background = Catalog.backgrounds.Count > 0 ? Catalog.backgrounds[BackgroundIndex].texture : null,
                backgroundColor = BackgroundColor,
                lightPreset = Catalog.lightPresets.Count > 0 ? Catalog.lightPresets[LightPresetIndex] : null,
                selectedFaceBlendshapeNames = SelectedFaceBlendshapes.ToArray(),
                forceFaceBlendshapeApply = forceFaceBlendshapeApply,
                shotKind = shotKind,
                zoom = Zoom,
                placement = Placement,
                avatarYawDegrees = RotationDegrees,
                width = size.x,
                height = size.y
            };
        }

        private void ReleaseIcons()
        {
            foreach (var texture in poseIconCache.Values.Concat(lightIconCache.Values))
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            poseIconCache.Clear();
            lightIconCache.Clear();
        }

        private static int ClampIndex(int value, int count) => count <= 0 ? 0 : Mathf.Clamp(value, 0, count - 1);
    }
}
