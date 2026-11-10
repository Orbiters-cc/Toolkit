using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    // Framing: visible bounds, camera frames, suggested framings and placement.
    public static partial class PhotoshootService
    {
        private static Bounds CalculateVisibleBounds(GameObject avatarRoot)
        {
            bool hasBounds = false;
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                // Frame the avatar's surfaces only: particle, trail and line renderers (often empty, with zero-size
                // bounds at the world origin) would stretch the framing far away from the avatar.
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer ||
                    renderer.bounds.size.sqrMagnitude <= 0f)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds || bounds.size.sqrMagnitude <= 0.0001f)
            {
                throw new InvalidOperationException("The avatar has no visible renderers to frame.");
            }

            return bounds;
        }

        private struct CameraFrame
        {
            public Vector3 target;
            public float verticalSpan;
            public float horizontalSpan;
            // Spans before zoom: placement is measured in these, so a placement keeps pointing at the same body part
            // at every zoom.
            public float baseVerticalSpan;
            public float baseHorizontalSpan;
            public float distance;
            // Part of the distance that keeps the camera in front of the avatar; the spans apply at that front plane.
            public float clearance;
            public float aspect;
        }

        private static CameraFrame FrameFor(Bounds bounds, ShotKind shotKind, int width, int height, float zoom)
        {
            zoom = Mathf.Clamp(zoom > 0f ? zoom : DefaultCameraZoom, MinCameraZoom, MaxCameraZoom);
            float aspect = Mathf.Max(0.1f, width / (float)Mathf.Max(1, height));
            float verticalSpan = shotKind == ShotKind.Thumbnail
                ? Mathf.Max(bounds.size.y * 0.58f, bounds.size.x * 0.82f)
                : bounds.size.y * 0.82f;
            float horizontalSpan = shotKind == ShotKind.Thumbnail
                ? Mathf.Max(bounds.size.x * 0.78f, verticalSpan * aspect * 0.52f)
                : Mathf.Max(bounds.size.x * 1.02f, verticalSpan * aspect * 0.82f);
            float baseVerticalSpan = verticalSpan, baseHorizontalSpan = horizontalSpan;
            verticalSpan /= zoom;
            horizontalSpan /= zoom;

            float verticalFovRadians = ReferenceCameraFieldOfView * Mathf.Deg2Rad;
            float horizontalFovRadians = 2f * Mathf.Atan(Mathf.Tan(verticalFovRadians * 0.5f) * aspect);
            float distanceForHeight = verticalSpan / (2f * Mathf.Tan(verticalFovRadians * 0.5f));
            float distanceForWidth = horizontalSpan / (2f * Mathf.Tan(horizontalFovRadians * 0.5f));
            return new CameraFrame
            {
                target = bounds.center + Vector3.up * (shotKind == ShotKind.Thumbnail ? bounds.size.y * 0.22f : bounds.size.y * 0.16f),
                verticalSpan = verticalSpan,
                horizontalSpan = horizontalSpan,
                baseVerticalSpan = baseVerticalSpan,
                baseHorizontalSpan = baseHorizontalSpan,
                distance = Mathf.Max(distanceForHeight, distanceForWidth) + Mathf.Max(0.2f, bounds.extents.z),
                clearance = Mathf.Max(0.2f, bounds.extents.z),
                aspect = aspect
            };
        }

        /// <summary>
        /// Zoom and placement that show one part of the posed avatar, whatever the pose: the head down to the chest, the
        /// torso and head down to the hips, or the whole body. Regions come from the vertices the head and spine move, so
        /// ears and hair count and a raised tail does not; the frame fits both their height and width, with headroom.
        /// </summary>
        public static bool TrySuggestFraming(FrameInfo frame, ShotKind shotKind, Vector2Int size, FramingPreset preset, out float zoom, out Vector2 placement)
        {
            zoom = DefaultCameraZoom;
            placement = Vector2.zero;
            if (!frame.valid || frame.bounds.size.y <= 0f)
            {
                return false;
            }

            Bounds region;
            if (preset == FramingPreset.Portrait && frame.hasHead)
            {
                region = frame.head;
                region.Encapsulate(new Vector3(region.center.x, frame.chest.y, region.center.z));
            }
            else if (preset == FramingPreset.HalfBody && frame.hasTorso)
            {
                region = frame.torso;
                region.Encapsulate(frame.hips);
            }
            else if (preset == FramingPreset.FullBody)
            {
                region = frame.bounds;
            }
            else
            {
                // Not humanoid: take the top of the outline.
                float cut = preset == FramingPreset.Portrait ? frame.chest.y : frame.hips.y;
                region = frame.bounds;
                region.SetMinMax(new Vector3(region.min.x, cut, region.min.z), region.max);
            }

            float aspect = Mathf.Max(0.1f, size.x / (float)Mathf.Max(1, size.y));
            float top = region.max.y + region.size.y * (preset == FramingPreset.FullBody ? 0.05f : 0.1f);
            float bottom = region.min.y - region.size.y * (preset == FramingPreset.FullBody ? 0.04f : 0.02f);
            float side = region.size.x * 0.06f;
            float wanted = Mathf.Max(top - bottom, (region.size.x + side * 2f) / aspect, frame.bounds.size.y * 0.05f);
            float tangent = Mathf.Tan(ReferenceCameraFieldOfView * Mathf.Deg2Rad * 0.5f);

            // The visible height is measured where the framed part's outline is, at its middle depth: from the camera,
            // that is the avatar's front plus however far that middle sits behind it.
            float front = frame.bounds.center.z + Mathf.Max(0.2f, frame.bounds.extents.z);
            float behindFront = Mathf.Max(0f, front - region.center.z);
            // The visible height shrinks as zoom grows; search the zoom that shows exactly the wanted span.
            float low = MinCameraZoom, high = MaxCameraZoom;
            for (int i = 0; i < 32; i++)
            {
                float middle = Mathf.Sqrt(low * high);
                var candidate = FrameFor(frame.stage, shotKind, size.x, size.y, middle);
                float visible = 2f * (candidate.distance - candidate.clearance + behindFront) * tangent;
                if (visible > wanted) low = middle;
                else high = middle;
            }

            zoom = Mathf.Sqrt(low * high);
            var cameraFrame = FrameFor(frame.stage, shotKind, size.x, size.y, zoom);
            // The camera looks along -Z, so screen right is world -X: placement.x moves the look point towards +X.
            float centreX = region.center.x, centreY = (top + bottom) * 0.5f;
            placement = new Vector2(
                Mathf.Clamp((centreX - cameraFrame.target.x) / (cameraFrame.baseHorizontalSpan * PlacementFrameStrength), -MaxPlacement, MaxPlacement),
                Mathf.Clamp((cameraFrame.target.y - centreY) / (cameraFrame.baseVerticalSpan * PlacementFrameStrength), -MaxPlacement, MaxPlacement));
            return true;
        }

        /// <summary>Placement change that moves the avatar by one whole frame width and height.</summary>
        public static Vector2 PlacementPerFrame(FrameInfo frame, ShotKind shotKind, Vector2Int size, float zoom)
        {
            if (!frame.valid) return new Vector2(1.25f, 1.25f);
            var cameraFrame = FrameFor(frame.stage, shotKind, size.x, size.y, zoom);
            // Measured at the avatar's front, the surface the pointer drags.
            float visibleHeight = 2f * (cameraFrame.distance - cameraFrame.clearance) * Mathf.Tan(ReferenceCameraFieldOfView * Mathf.Deg2Rad * 0.5f);
            return new Vector2(
                visibleHeight * cameraFrame.aspect / (PlacementFrameStrength * cameraFrame.baseHorizontalSpan),
                visibleHeight / (PlacementFrameStrength * cameraFrame.baseVerticalSpan));
        }


        private static void ConfigureCamera(Camera camera, Bounds bounds, ShotKind shotKind, int width, int height, float zoom, Vector2 placement)
        {
            placement = new Vector2(Mathf.Clamp(placement.x, -MaxPlacement, MaxPlacement), Mathf.Clamp(placement.y, -MaxPlacement, MaxPlacement));
            var frame = FrameFor(bounds, shotKind, width, height, zoom);
            float distance = frame.distance, aspect = frame.aspect;

            // The camera looks along -Z, so screen right is world -X. Placement pans the camera rather than tilting it,
            // so an off-centre framing keeps the avatar undistorted.
            Vector3 cameraRight = Vector3.left;
            Vector3 framedTarget = frame.target
                - cameraRight * (frame.baseHorizontalSpan * PlacementFrameStrength * placement.x)
                - Vector3.up * (frame.baseVerticalSpan * PlacementFrameStrength * placement.y);
            camera.transform.position = framedTarget + Vector3.forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            camera.fieldOfView = ReferenceCameraFieldOfView;
            camera.aspect = aspect;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = Mathf.Max(50f, distance * 6f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = PreviewClearColor;
            camera.allowHDR = true;
            camera.allowMSAA = true;
        }
    }
}
