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
        public const float MaxTilt = 80f;
        /// <summary>Tilt of the avatar toward (positive) or away from the camera, after its turn.</summary>
        public float TiltDegrees { get => tiltDegrees; set => tiltDegrees = Mathf.Clamp(value, -MaxTilt, MaxTilt); }
        private float tiltDegrees;
        /// <summary>The bone the avatar turns around, chosen by <see cref="BeginTurn"/>.</summary>
        public PhotoshootService.TurnPivot Pivot { get; set; } = PhotoshootService.TurnPivot.Hips;
        /// <summary>The avatar looks at the camera, with its head and eyes balanced by <see cref="LookWithEyes"/>.</summary>
        public bool LookAtCamera { get; set; }
        /// <summary>Who looks at the camera: 0 the head alone, 1 the eyes alone.</summary>
        public float LookWithEyes { get; set; } = .5f;
        public HashSet<string> SelectedFaceBlendshapes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Effects over every shot (and the ref sheet's views): bloom, comic halftone, depth of field…</summary>
        public PhotoshootEffectSettings Effects { get; } = new PhotoshootEffectSettings();
        /// <summary>The environment light over the preset's own: 1 as the preset has it, up to <see cref="MaxAmbientIntensity"/>.</summary>
        public float AmbientIntensity { get => ambientIntensity; set => ambientIntensity = Mathf.Clamp(value, 0f, MaxAmbientIntensity); }
        private float ambientIntensity = 1f;
        public const float MaxAmbientIntensity = 3f;
        private const string FramingOpenPref = "Orbiters.Photoshoot.FramingOpen";
        /// <summary>The framing card is unfolded (the default); remembered by the editor.</summary>
        public bool FramingOpen { get => EditorPrefs.GetBool(FramingOpenPref, true); set => EditorPrefs.SetBool(FramingOpenPref, value); }
        /// <summary>The style picker tab shown in the panel (pose, light, background or expression); survives host rebuilds.</summary>
        public int StyleTab { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool IsGenerating { get; internal set; }
        internal int RefreshTicket;

        /// <summary>The panel shows the ref sheet (front, back and side) instead of the shots; survives host rebuilds.</summary>
        public bool RefSheetOpen { get; set; }
        /// <summary>The ref sheet's own plain background, dark grey until changed; the photoshoot keeps its own.</summary>
        public Color RefSheetBackground { get; set; } = PhotoshootRefSheet.DefaultBackground;
        public bool RefSheetSideFacesRight { get; set; }
        /// <summary>Zoom over the full body fitted in every view (1), and how far the views are moved up (positive).</summary>
        public float RefSheetZoom { get => refSheetZoom; set => refSheetZoom = Mathf.Clamp(value, MinRefSheetZoom, MaxRefSheetZoom); }
        private float refSheetZoom = 1f;
        public float RefSheetLift { get => refSheetLift; set => refSheetLift = Mathf.Clamp(value, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement); }
        private float refSheetLift;
        public const float MinRefSheetZoom = 0.5f, MaxRefSheetZoom = 4f;
        private const float RefSheetMargin = 0.88f;
        private RenderTexture refSheetPreview;
        // The full-body fit of the last pose: one zoom for every view, so all three share a scale, and each view's placement.
        private float refSheetFitZoom;
        private Vector2[] refSheetFitPlacements;

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
            TiltDegrees = 0f;
            Pivot = PhotoshootService.TurnPivot.Hips;
            LookAtCamera = false;
            LookWithEyes = .5f;
            BackgroundColor = PhotoshootService.DefaultBackgroundColor;
            FramingPreset = null;
            RefSheetOpen = false;
            RefSheetBackground = PhotoshootRefSheet.DefaultBackground;
            RefSheetSideFacesRight = false;
            RefSheetZoom = 1f;
            RefSheetLift = 0f;
            refSheetFitPlacements = null;
            SelectedFaceBlendshapes.Clear();
            Effects.Reset();
            AmbientIntensity = 1f;
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
            ReleaseRefSheetPreview();
            refSheetFitPlacements = null;
            // Closing the photoshoot leaves the ref sheet: it opens on its shots next time.
            RefSheetOpen = false;
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

        /// <summary>The backgrounds again after the user added or removed one; <paramref name="select"/> becomes the chosen one.</summary>
        internal void ReloadBackgrounds(string select = null)
        {
            if (Catalog == null) return;
            var chosen = Catalog.backgrounds.Count > 0 ? Catalog.backgrounds[ClampIndex(BackgroundIndex, Catalog.backgrounds.Count)] : null;
            Catalog.backgrounds = PhotoshootService.FindBackgrounds();
            string wanted = select ?? chosen?.assetPath;
            int index = Catalog.backgrounds.FindIndex(option => wanted != null ? option.assetPath == wanted : option.solidColor == (chosen?.solidColor ?? true));
            BackgroundIndex = ClampIndex(index < 0 ? 0 : index, Catalog.backgrounds.Count);
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

        /// <summary>
        /// Before turning or tilting: the avatar turns around whichever of its hips, chest and head is nearest the middle of
        /// the view, so what is framed stays in place. Switching pivots shifts the placement so nothing moves on screen.
        /// </summary>
        internal void BeginTurn()
        {
            if (previewSession == null) return;
            var pivot = previewSession.NearestPivot(out Vector2 shift);
            if (pivot == Pivot) return;
            Pivot = pivot;
            Vector2 placement = Placement + shift;
            Placement = new Vector2(Mathf.Clamp(placement.x, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement),
                Mathf.Clamp(placement.y, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement));
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

        // ---- Ref sheet ------------------------------------------------------------------------------------

        /// <summary>
        /// Renders the live ref sheet at <paramref name="size"/> into <see cref="RefSheetPreview"/>. <paramref name="refit"/>:
        /// the pose, expression or side changed, so the full body is fitted in every view again first.
        /// </summary>
        internal void RenderRefSheet(GameObject avatarRoot, Vector2Int size, bool refit, bool forceFaceBlendshapeApply)
        {
            try
            {
                Error = null;
                if (refSheetPreview == null || refSheetPreview.width != size.x || refSheetPreview.height != size.y || !refSheetPreview.IsCreated())
                {
                    ReleaseRefSheetPreview();
                    refSheetPreview = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "Orbiters Photoshoot Ref Sheet", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false
                    };
                    refSheetPreview.Create();
                }
                DrawRefSheet(avatarRoot, refSheetPreview, refit, forceFaceBlendshapeApply);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Status = null;
            }
        }

        internal Texture RefSheetPreview => refSheetPreview;

        /// <summary>The ref sheet at full size (<see cref="PhotoshootRefSheet.Size"/>), framed as the live one.</summary>
        internal Texture2D CaptureRefSheet(GameObject avatarRoot)
        {
            var size = PhotoshootRefSheet.Size;
            var sheet = RenderTexture.GetTemporary(size.x, size.y, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                DrawRefSheet(avatarRoot, sheet, refSheetFitPlacements == null, false);
                RenderTexture.active = sheet;
                var texture = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false, false) { name = "Orbiters Ref Sheet" };
                texture.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(sheet);
            }
        }

        /// <summary>How far <see cref="RefSheetLift"/> moves for a drag across a whole view's height.</summary>
        internal float RefSheetLiftPerView(Vector2Int sheet)
        {
            var view = PhotoshootRefSheet.ViewSize(sheet);
            float zoom = refSheetFitZoom > 0f ? refSheetFitZoom * RefSheetZoom : DefaultZoom;
            return PhotoshootService.PlacementPerFrame(previewSession != null ? previewSession.LastFrame : default, PhotoshootService.ShotKind.Thumbnail, view, zoom).y;
        }

        private void DrawRefSheet(GameObject avatarRoot, RenderTexture sheet, bool refit, bool forceFaceBlendshapeApply)
        {
            if (previewSession == null)
            {
                previewSession = new PhotoshootService.LivePreviewSession();
            }

            var view = PhotoshootRefSheet.ViewSize(new Vector2Int(sheet.width, sheet.height));
            int views = PhotoshootRefSheet.Views.Length;
            if (refit || refSheetFitPlacements == null)
            {
                // Each view fitted alone, then the smallest zoom for all: the widest outline (arms out, a tail aside) sets
                // the scale. A turn keeps every point at its height, so the views share their height too.
                refSheetFitZoom = MaxZoom;
                var placements = new Vector2[views];
                for (int i = 0; i < views; i++)
                {
                    previewSession.UpdatePreview(RefSheetRequest(avatarRoot, i, view, DefaultZoom, Vector2.zero, forceFaceBlendshapeApply && i == 0));
                    if (!PhotoshootService.TrySuggestFraming(previewSession.LastFrame, PhotoshootService.ShotKind.Thumbnail, view,
                            PhotoshootService.FramingPreset.FullBody, out float zoom, out Vector2 placement))
                    {
                        zoom = DefaultZoom;
                        placement = Vector2.zero;
                    }
                    refSheetFitZoom = Mathf.Min(refSheetFitZoom, zoom);
                    placements[i] = placement;
                }
                // Room between the views: hands held out never touch the next view.
                refSheetFitZoom *= RefSheetMargin;
                refSheetFitPlacements = placements;
                forceFaceBlendshapeApply = false;
            }

            PhotoshootRefSheet.Clear(sheet, RefSheetBackground);
            float scale = Mathf.Clamp(refSheetFitZoom * RefSheetZoom, PhotoshootService.MinCameraZoom, MaxZoom);
            for (int i = 0; i < views; i++)
            {
                var placement = new Vector2(refSheetFitPlacements[i].x, refSheetFitPlacements[0].y + RefSheetLift);
                previewSession.UpdatePreview(RefSheetRequest(avatarRoot, i, view, scale, placement, forceFaceBlendshapeApply && i == 0));
                PhotoshootRefSheet.Place(sheet, previewSession.GetPreviewTexture(PhotoshootService.ShotKind.Thumbnail), i);
            }
            PhotoshootRefSheet.DrawNames(sheet);
        }

        // Every view: same pose, light and expression; no tilt, and no look at the camera (the back view would turn its head).
        private PhotoshootService.RenderRequest RefSheetRequest(GameObject avatarRoot, int view, Vector2Int size, float zoom, Vector2 placement, bool forceFaceBlendshapeApply)
        {
            EnsureCatalog(avatarRoot);
            return new PhotoshootService.RenderRequest
            {
                avatarRoot = avatarRoot,
                bodyPose = Catalog.bodyPoses.Count > 0 ? Catalog.bodyPoses[BodyPoseIndex].clip : null,
                background = null,
                backgroundColor = RefSheetBackground,
                lightPreset = Catalog.lightPresets.Count > 0 ? Catalog.lightPresets[LightPresetIndex] : null,
                selectedFaceBlendshapeNames = SelectedFaceBlendshapes.ToArray(),
                forceFaceBlendshapeApply = forceFaceBlendshapeApply,
                shotKind = PhotoshootService.ShotKind.Thumbnail,
                zoom = zoom,
                placement = placement,
                avatarYawDegrees = PhotoshootRefSheet.Yaw(view, RefSheetSideFacesRight),
                avatarTiltDegrees = 0f,
                pivot = PhotoshootService.TurnPivot.Hips,
                lookAtCamera = false,
                width = size.x,
                height = size.y,
                effects = Effects,
                frameEffects = false,
                ambientIntensity = AmbientIntensity
            };
        }

        private void ReleaseRefSheetPreview()
        {
            if (refSheetPreview == null) return;
            if (RenderTexture.active == refSheetPreview) RenderTexture.active = null;
            refSheetPreview.Release();
            UnityEngine.Object.DestroyImmediate(refSheetPreview);
            refSheetPreview = null;
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
                avatarTiltDegrees = TiltDegrees,
                pivot = Pivot,
                lookAtCamera = LookAtCamera,
                lookWithEyes = LookWithEyes,
                width = size.x,
                height = size.y,
                effects = Effects,
                ambientIntensity = AmbientIntensity
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
