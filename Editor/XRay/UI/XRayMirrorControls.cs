using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor.Posing;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.XRay
{
    // Presentation and target selection only; pose matching, math and Undo belong to Toolkit.
    [InitializeOnLoad]
    internal static class XRayMirrorControls
    {
        static XRayMirrorControls()
        {
            // Outside humanoids, mirror mode finds rigs with the same detection as the XRay armatures.
            MirrorPoseService.ResolveOtherRig = (GameObject selected, out Transform root, out SkinnedMeshRenderer renderer) =>
            {
                bool found = XRayGizmoTargetFinder.TryResolveTarget(selected, out var target);
                root = found ? target.Owner.transform : null;
                renderer = found ? target.Renderer : null;
                return found;
            };
        }

        public static string Status
        {
            get
            {
                if (!MirrorPoseService.IsMirroring) return MirrorPoseService.LastStatus;
                if (AnimationMode.InAnimationMode()) return "Mirror paused during animation preview/recording.";
                var selected = Selection.activeTransform;
                var partner = MirrorPoseService.GetPartner(selected);
                return MirrorPoseService.LastStatus + (partner != null
                    ? $"\n{selected.name} ↔ {partner.name}"
                    : "\nSelect one paired bone on this rig. Center bones are not mirrored.");
            }
        }

        public static void SetEnabled(bool enabled)
        {
            // Mirror mode stays on without a rig to mirror; it binds to the next avatar or bone selected.
            if (enabled) MirrorPoseService.Enable();
            else MirrorPoseService.Disable();
        }

        public static void BindImmediateToggle(Toggle toggle, Action<bool> changed)
        {
            bool queued = false;
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (queued) return;
                queued = true;
                bool requested = evt.newValue;
                toggle.schedule.Execute(() => { queued = false; changed(requested); });
            });
            toggle.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || queued) return;
                toggle.value = !toggle.value;
                evt.PreventDefault();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
        }

        private static Texture2D icon;
        public static Texture2D Icon
        {
            get
            {
                if (icon != null) return icon;
                const int resolution = 64;
                icon = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
                { name = "Mirror pose butterfly", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                Color ink = EditorGUIUtility.isProSkin ? new Color(.85f, .85f, .85f) : new Color(.2f, .2f, .2f);
                var outline = new List<Vector2>();
                AddCurve(outline, new Vector2(8, 10), new Vector2(8, 7), new Vector2(2, 1), new Vector2(2, 5));
                AddCurve(outline, new Vector2(2, 5), new Vector2(2, 8), new Vector2(4, 10), new Vector2(8, 10));
                AddCurve(outline, new Vector2(8, 10), new Vector2(4, 10), new Vector2(2, 12), new Vector2(2, 15));
                AddCurve(outline, new Vector2(2, 15), new Vector2(2, 19), new Vector2(8, 14), new Vector2(8, 10));
                for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    var p = new Vector2((x + .5f) * 20 / resolution, (y + .5f) * 20 / resolution);
                    p.x = Mathf.Min(p.x, 20 - p.x);
                    float distance = 20;
                    for (int i = 1; i < outline.Count; i++)
                    {
                        var segment = outline[i] - outline[i - 1];
                        float along = Mathf.Clamp01(Vector2.Dot(p - outline[i - 1], segment) / segment.sqrMagnitude);
                        distance = Mathf.Min(distance, Vector2.Distance(p, outline[i - 1] + along * segment));
                    }
                    if (p.y >= 2 && p.y <= 18 && Mathf.FloorToInt(p.y) % 4 < 2)
                        distance = Mathf.Min(distance, Mathf.Abs(p.x - 10));
                    var pixel = ink;
                    pixel.a = Mathf.Clamp01((.65f - distance) * resolution / 20);
                    icon.SetPixel(x, y, pixel);
                }
                icon.Apply();
                return icon;
            }
        }

        private static void AddCurve(List<Vector2> points, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            for (int i = points.Count == 0 ? 0 : 1; i <= 16; i++)
            {
                float t = i / 16f;
                float u = 1 - t;
                points.Add(u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d);
            }
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    internal sealed class XRayMirrorToolbarToggle : EditorToolbarToggle
    {
        public const string Id = "Orbiters/XRayGizmos/Mirror";
        private bool listening;

        public XRayMirrorToolbarToggle()
        {
            text = string.Empty;
            icon = XRayMirrorControls.Icon;
            XRayMirrorControls.BindImmediateToggle(this, XRayMirrorControls.SetEnabled);
            RegisterCallback<AttachToPanelEvent>(_ => Attach());
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            Attach();
            Sync();
        }

        private void Sync()
        {
            SetValueWithoutNotify(MirrorPoseService.Enabled);
            SetEnabled(!EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying);
            tooltip = XRayMirrorControls.Status + "\nMirror rotation and movement across the avatar's local X axis. Click to toggle.";
        }

        private void Attach()
        {
            if (listening) return;
            listening = true;
            MirrorPoseService.Changed += Sync;
            Selection.selectionChanged += Sync;
        }

        private void Detach()
        {
            if (!listening) return;
            listening = false;
            MirrorPoseService.Changed -= Sync;
            Selection.selectionChanged -= Sync;
        }
    }
}
