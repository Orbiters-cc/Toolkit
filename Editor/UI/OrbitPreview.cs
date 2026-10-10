using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The camera of the Orbiters tools' 3D previews (MCB's version comparison, ReFit's stage): it turns around a pivot,
    /// moves with it and zooms, and the shown values glide to their targets instead of jumping. Feed it pointer deltas
    /// and wheel steps, call <see cref="Glide"/> every frame, then <see cref="Apply"/> to place a preview camera.
    /// </summary>
    public sealed class OrbitCamera
    {
        public float FieldOfView = 28f;
        public float MinPitch = -75f, MaxPitch = 75f;
        /// <summary>Degrees per pointer pixel, across and up.</summary>
        public float TurnSpeed = 0.45f, TiltSpeed = 0.3f;
        /// <summary>How quickly the shown values reach their targets (per second).</summary>
        public float GlideRate = 9f;

        public float Yaw = 180f, Pitch = 6f, Distance = 3f;
        public float TargetYaw = 180f, TargetPitch = 6f, TargetDistance = 3f;
        public Vector3 Pivot, TargetPivot;

        public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        /// <summary>The distance at which <paramref name="bounds"/> fills the view.</summary>
        public float Fit(Bounds bounds)
        {
            float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            return radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 0.9f;
        }

        /// <summary>Turns with a pointer drag.</summary>
        public void Turn(Vector2 pointerDelta)
        {
            TargetYaw += pointerDelta.x * TurnSpeed;
            TargetPitch = Mathf.Clamp(TargetPitch + pointerDelta.y * TiltSpeed, MinPitch, MaxPitch);
        }

        /// <summary>Moves the pivot with a pointer drag, at once, in a view <paramref name="viewHeight"/> pixels high.</summary>
        public void Pan(Vector2 pointerDelta, float viewHeight)
        {
            float perPixel = 2f * Distance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, viewHeight);
            var rotation = Rotation;
            TargetPivot += (rotation * Vector3.left * pointerDelta.x + rotation * Vector3.up * pointerDelta.y) * perPixel;
            Pivot = TargetPivot;
        }

        /// <summary>Zooms with a wheel step, between <paramref name="nearest"/> and <paramref name="farthest"/>.</summary>
        public void Zoom(float wheelDelta, float nearest, float farthest) =>
            TargetDistance = Mathf.Clamp(TargetDistance * Mathf.Exp(wheelDelta * 0.06f), nearest, farthest);

        /// <summary>Sends the camera to look from <paramref name="direction"/> (from the pivot toward the camera), the short way round.</summary>
        public void LookFrom(Vector3 direction)
        {
            if (direction.sqrMagnitude < 1e-8f) return;
            direction.Normalize();
            float yaw = Mathf.Atan2(-direction.x, -direction.z) * Mathf.Rad2Deg;
            TargetYaw = yaw + 360f * Mathf.Round((TargetYaw - yaw) / 360f);
            TargetPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg, MinPitch, MaxPitch);
        }

        /// <summary>Glides the shown values toward the targets; false once everything rests.</summary>
        /// <param name="pivotEpsilon">How close the pivot counts as arrived (scene units).</param>
        public bool Glide(float deltaTime, float pivotEpsilon)
        {
            float k = 1f - Mathf.Exp(-deltaTime * GlideRate);
            bool moving = false;
            Yaw = Step(Yaw, TargetYaw, k, 0.01f, ref moving);
            Pitch = Step(Pitch, TargetPitch, k, 0.01f, ref moving);
            Distance = Step(Distance, TargetDistance, k, TargetDistance * 0.0005f, ref moving);
            if ((Pivot - TargetPivot).sqrMagnitude > pivotEpsilon * pivotEpsilon)
            {
                Pivot = Vector3.Lerp(Pivot, TargetPivot, k);
                moving = true;
            }
            else Pivot = TargetPivot;
            return moving;
        }

        /// <summary>Jumps to the targets.</summary>
        public void Snap()
        {
            Yaw = TargetYaw;
            Pitch = TargetPitch;
            Distance = TargetDistance;
            Pivot = TargetPivot;
        }

        /// <summary>Places <paramref name="camera"/>: in perspective, looking at the pivot, its clip planes covering <paramref name="reach"/>.</summary>
        public void Apply(Camera camera, float aspect, float reach)
        {
            camera.orthographic = false;
            camera.aspect = aspect;
            camera.fieldOfView = FieldOfView;
            var rotation = Rotation;
            camera.transform.rotation = rotation;
            camera.transform.position = Pivot - rotation * Vector3.forward * Distance;
            camera.nearClipPlane = Mathf.Max(0.001f, Distance * 0.02f);
            camera.farClipPlane = Distance + reach * 4f;
        }

        private static float Step(float value, float target, float k, float epsilon, ref bool moving)
        {
            if (Mathf.Abs(target - value) <= epsilon) return target;
            moving = true;
            return Mathf.Lerp(value, target, k);
        }
    }

    /// <summary>
    /// Renders a <see cref="PreviewRenderUtility"/> into textures that UI Toolkit shows, and always gives the editor its
    /// lighting back: <see cref="PreviewRenderUtility.Render(bool, bool)"/> switches the editor's lighting settings to the
    /// preview scene and only <c>EndPreview</c> switches them back, which a texture shown by UI Toolkit never calls; left
    /// switched, the Scene view reads settings that are gone and Unity can fail fatally.
    /// </summary>
    public static class OffscreenPreview
    {
        /// <summary>A colour texture of the given size (sRGB, multisampled), made again only when the size changes.</summary>
        public static void EnsureTexture(ref RenderTexture texture, int width, int height, string name)
        {
            if (texture != null && texture.width == width && texture.height == height && texture.IsCreated()) return;
            Release(ref texture);
            texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = name,
                antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing > 1 ? QualitySettings.antiAliasing : 4),
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
        }

        public static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            Object.DestroyImmediate(texture);
            texture = null;
        }

        /// <summary>Renders what was drawn into <paramref name="preview"/> into <paramref name="texture"/>.</summary>
        public static void Render(PreviewRenderUtility preview, RenderTexture texture)
        {
            var camera = preview.camera;
            camera.targetTexture = texture;
            try
            {
                preview.Render(true, false);
            }
            finally
            {
                Unsupported.RestoreOverrideLightingSettings();
                camera.targetTexture = null;
            }
        }
    }
}
