using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor.Photoshoot
{
    public sealed partial class PhotoshootPanel
    {
        // ---- Framing ----------------------------------------------------------------------------------------

        private const float FramingTweenSeconds = 0.34f;
        private const float TurnDegreesPerPixel = 0.5f;

        private static readonly (PhotoshootService.FramingPreset preset, string label, string tip)[] FramingPresets =
        {
            (PhotoshootService.FramingPreset.Portrait, "Portrait", "Head and shoulders."),
            (PhotoshootService.FramingPreset.HalfBody, "Half body", "Down to the hips."),
            (PhotoshootService.FramingPreset.FullBody, "Full body", "The whole avatar."),
        };

        private void BuildFraming()
        {
            var card = Card("ps-framing");
            var header = Header(card, "Framing");
            var reset = CreateButton("Reset", ResetFraming, "ps-link");
            reset.tooltip = "Default zoom, centred, facing the camera.";
            header.Add(reset);

            presets = new SegmentedControl(FramingPresets.Select(entry => new SegmentedControl.Option(entry.label, null, entry.tip)),
                index => ApplyPreset(FramingPresets[index].preset));
            presets.AddToClassList("ps-framing__presets");
            card.Add(presets);

            // The sphere turns and tilts the avatar; beside it, zoom and where the avatar looks.
            var body = new VisualElement();
            body.AddToClassList("ps-framing__body");
            card.Add(body);
            orbit = new OrbitSphere(PhotoshootState.MaxTilt, (yaw, tilt) =>
            {
                framingTween?.Pause();
                state.RotationDegrees = yaw;
                state.TiltDegrees = tilt;
                RenderKeepingFraming();
            }, state.BeginTurn, () => AnimateFraming(state.Zoom, state.Placement, 0f, state.FramingPreset));
            orbit.tooltip = "Drag to turn the avatar, up and down to tilt it. It turns around whichever of its hips, chest and head is in the middle of the view. Double-click to face the camera again.";
            orbit.AddToClassList("ps-framing__orbit");
            body.Add(orbit);
            var controls = new VisualElement();
            controls.AddToClassList("ps-framing__controls");
            body.Add(controls);

            zoomDial = new ScrubDial("Zoom", Mathf.Log(PhotoshootState.MinZoom, 2f), Mathf.Log(PhotoshootState.MaxZoom, 2f), Mathf.Log(PhotoshootState.DefaultZoom, 2f),
                0.1f, 5, 70f, 0.04f,
                value => Mathf.Pow(2f, value).ToString("0.00") + "×",
                value =>
                {
                    framingTween?.Pause();
                    state.Zoom = Mathf.Pow(2f, value);
                    ManualFraming();
                });
            controls.Add(zoomDial);

            var look = new VisualElement();
            look.AddToClassList("ps-look");
            var lookLabel = new Label("Look at the camera");
            lookLabel.AddToClassList("ps-look__label");
            look.Add(lookLabel);
            lookSwitch = new ToggleSwitch(state.LookAtCamera, on =>
            {
                state.LookAtCamera = on;
                SyncLook();
                RenderKeepingFraming();
            }) { tooltip = "The avatar looks straight at the camera." };
            look.Add(lookSwitch);
            controls.Add(look);
            lookDial = new ScrubDial("Look", 0f, 100f, 50f, 10f, 5, 2.4f, 3f,
                value => value < .5f ? "Head" : value > 99.5f ? "Eyes" : $"Eyes {value:0}%",
                value =>
                {
                    state.LookWithEyes = value / 100f;
                    RenderKeepingFraming();
                });
            lookDial.tooltip = "The avatar always looks straight at the camera: this splits the turn between the head (left) and the eyes (right).";
            controls.Add(lookDial);

            var tip = new Label("On the preview: drag to move, scroll to zoom, Shift-drag to turn and tilt");
            tip.AddToClassList("ps-caption");
            tip.AddToClassList("ps-framing__tip");
            card.Add(tip);
            Foldable(card, header);
            SyncFraming();
        }

        // The framing card folds under its header (a press anywhere on it but its links); it stays as left, open at first.
        private void Foldable(VisualElement card, VisualElement header)
        {
            var body = new VisualElement();
            body.AddToClassList("ps-card__body");
            foreach (var child in card.Children().Where(child => child != header).ToList()) body.Add(child);
            card.Add(body);
            card.AddToClassList("ps-card--foldable");
            var chevron = new VectorIcon(IconGlyph.Chevron) { pickingMode = PickingMode.Ignore };
            chevron.AddToClassList("ps-card__chevron");
            header.Insert(0, chevron);
            header.tooltip = "Fold or unfold";
            void Show()
            {
                card.EnableInClassList("ps-card--folded", !state.FramingOpen);
                body.style.display = state.FramingOpen ? DisplayStyle.Flex : DisplayStyle.None;
            }
            // The links in the header (Reset, Fit) act on press and stop the event there.
            header.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                state.FramingOpen = !state.FramingOpen;
                Show();
            });
            Show();
        }

        private void SyncFraming()
        {
            if (state.RefSheetOpen)
            {
                zoomDial?.SetValueWithoutNotify(Mathf.Log(state.RefSheetZoom, 2f));
                sideControl?.SetIndex(state.RefSheetSideFacesRight ? 1 : 0);
                return;
            }
            orbit?.SetValueWithoutNotify(state.RotationDegrees, state.TiltDegrees);
            zoomDial?.SetValueWithoutNotify(Mathf.Log(state.Zoom, 2f));
            MovePresetIndicator();
            SyncLook();
        }

        private void SyncLook()
        {
            lookSwitch?.SetValueWithoutNotify(state.LookAtCamera);
            if (lookDial == null) return;
            lookDial.SetValueWithoutNotify(state.LookWithEyes * 100f);
            lookDial.style.display = state.LookAtCamera ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // With no preset the highlight fades out where it was; it slides to a preset when one is chosen.
        private void MovePresetIndicator() => presets?.SetIndex(Array.FindIndex(FramingPresets, entry => entry.preset == state.FramingPreset));

        // Zoom or placement changed by hand: the framing no longer follows a preset.
        private void ManualFraming()
        {
            state.FramingPreset = null;
            SyncFraming();
            RenderNow();
        }

        // Framing input renders in the same event, so the preview moves with the pointer; a framing render reuses the posed
        // avatar and costs a couple of milliseconds. Any render still queued for this tick is dropped.
        private void RenderNow()
        {
            ++state.RefreshTicket;
            if (CanGenerate()) RenderPreviews(false);
        }

        // Turning, tilting and where the avatar looks keep the zoom and placement: a framing preset is not fitted again to
        // the changed outline.
        private void RenderKeepingFraming()
        {
            ++state.RefreshTicket;
            if (CanGenerate()) RenderPreviews(false, false);
        }

        private void ApplyPreset(PhotoshootService.FramingPreset preset)
        {
            var kind = PhotoshootService.ShotKind.Thumbnail;
            if (!state.TrySuggestFraming(kind, PreviewSize(kind), preset, out float zoom, out Vector2 placement)) return;
            AnimateFraming(zoom, placement, null, preset);
        }

        private void ResetFraming() => AnimateFraming(PhotoshootState.DefaultZoom, Vector2.zero, 0f, null);

        // Glides zoom, placement and optionally rotation to a new framing with a light overshoot, rendering every frame.
        private void AnimateFraming(float toZoom, Vector2 toPlacement, float? toRotation, PhotoshootService.FramingPreset? preset)
        {
            framingTween?.Pause();
            state.FramingPreset = preset;
            MovePresetIndicator();
            float fromZoom = Mathf.Log(state.Zoom), targetZoom = Mathf.Log(Mathf.Clamp(toZoom, PhotoshootState.MinZoom, PhotoshootState.MaxZoom));
            Vector2 fromPlacement = state.Placement;
            float fromRotation = state.RotationDegrees, fromTilt = state.TiltDegrees;
            double start = EditorApplication.timeSinceStartup;
            framingTween = schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - start) / FramingTweenSeconds));
                float eased = EaseOutBack(t);
                state.Zoom = Mathf.Clamp(Mathf.Exp(Mathf.LerpUnclamped(fromZoom, targetZoom, eased)), PhotoshootState.MinZoom, PhotoshootState.MaxZoom);
                Vector2 placement = Vector2.LerpUnclamped(fromPlacement, toPlacement, eased);
                state.Placement = new Vector2(Mathf.Clamp(placement.x, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement), Mathf.Clamp(placement.y, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement));
                if (toRotation.HasValue)
                {
                    // The short way round.
                    state.RotationDegrees = fromRotation + Mathf.DeltaAngle(fromRotation, toRotation.Value) * eased;
                    state.TiltDegrees = Mathf.LerpUnclamped(fromTilt, 0f, eased);
                }
                SyncFraming();
                RenderPreviews(false, false);
                if (t >= 1f) framingTween?.Pause();
            }).Every(16);
        }

        private static float EaseOutBack(float t)
        {
            const float overshoot = 0.9f;
            float u = t - 1f;
            return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
        }

        private void MoveBy(Vector2 delta, Vector2 frame, PhotoshootService.ShotKind kind)
        {
            if (frame.x <= 0f || frame.y <= 0f) return;
            framingTween?.Pause();
            // Placement is scaled so the avatar follows the pointer across the frame it is shown in.
            Vector2 perFrame = state.PlacementPerFrame(kind, PreviewSize(kind));
            Vector2 placement = state.Placement + new Vector2(delta.x / frame.x * perFrame.x, -delta.y / frame.y * perFrame.y);
            state.Placement = new Vector2(Mathf.Clamp(placement.x, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement), Mathf.Clamp(placement.y, -PhotoshootService.MaxPlacement, PhotoshootService.MaxPlacement));
            ManualFraming();
        }

        private void TurnBy(Vector2 delta)
        {
            framingTween?.Pause();
            state.RotationDegrees -= delta.x * TurnDegreesPerPixel;
            state.TiltDegrees += delta.y * TurnDegreesPerPixel;
            SyncFraming();
            RenderKeepingFraming();
        }

        private void ZoomBy(float wheel)
        {
            framingTween?.Pause();
            state.Zoom = Mathf.Clamp(state.Zoom * Mathf.Exp(-wheel * 0.05f), PhotoshootState.MinZoom, PhotoshootState.MaxZoom);
            ManualFraming();
        }

        /// <summary>
        /// Lets the avatar be framed directly on a host element that shows the live thumbnail (see
        /// <see cref="PhotoshootOptions.ThumbnailPreview"/>): drag to move, Shift-drag to turn, scroll to zoom, double-click
        /// to reset. <paramref name="frameSize"/> is the size of the whole thumbnail on that element, also where it is cropped.
        /// </summary>
        public void AttachFraming(VisualElement surface, Func<Vector2> frameSize) =>
            AttachFraming(surface, PhotoshootService.ShotKind.Thumbnail, frameSize);

        public void DetachFraming(VisualElement surface)
        {
            if (surface == null || !framingSurfaces.TryGetValue(surface, out var drag)) return;
            surface.RemoveManipulator(drag);
            framingSurfaces.Remove(surface);
            if (textLayers.TryGetValue(surface, out var layer)) { layer.RemoveFromHierarchy(); textLayers.Remove(surface); }
        }

        private void AttachFraming(VisualElement surface, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
        {
            if (surface == null || frameSize == null || framingSurfaces.ContainsKey(surface)) return;
            var drag = new FramingDrag(this, kind, frameSize);
            framingSurfaces[surface] = drag;
            surface.AddManipulator(drag);
            // The thumbnail's text sits on the same surface: pressing it moves the text, pressing elsewhere the avatar.
            if (kind == PhotoshootService.ShotKind.Thumbnail && options.TextFonts != null)
            {
                var layer = new PhotoshootTextLayer(() => state.Text, frameSize, TextChanged);
                layer.style.display = TextOn ? DisplayStyle.Flex : DisplayStyle.None;
                surface.Add(layer);
                textLayers[surface] = layer;
            }
        }

        private sealed class FramingDrag : PointerManipulator
        {
            private readonly PhotoshootPanel owner;
            private readonly PhotoshootService.ShotKind kind;
            private readonly Func<Vector2> frameSize;
            private StyleSheet addedSheet;
            private Label hint;
            private PointerDragCapture drag;
            private Vector2 last;
            private bool turning;

            public FramingDrag(PhotoshootPanel owner, PhotoshootService.ShotKind kind, Func<Vector2> frameSize)
            {
                this.owner = owner; this.kind = kind; this.frameSize = frameSize;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
                if (sheet && !target.styleSheets.Contains(sheet)) { target.styleSheets.Add(sheet); addedSheet = sheet; }
                target.AddToClassList("ps-surface");
                hint = new Label("Drag to move · Scroll to zoom · Shift-drag to turn and tilt") { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ps-surface__hint");
                target.Add(hint);
                target.RegisterCallback<PointerDownEvent>(OnDown);
                target.RegisterCallback<PointerMoveEvent>(OnMove);
                drag = new PointerDragCapture(target, End);
                target.RegisterCallback<WheelEvent>(OnWheel);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                drag.Dispose();
                target.UnregisterCallback<PointerDownEvent>(OnDown);
                target.UnregisterCallback<PointerMoveEvent>(OnMove);
                target.UnregisterCallback<WheelEvent>(OnWheel);
                hint?.RemoveFromHierarchy();
                target.RemoveFromClassList("ps-surface");
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
                if (addedSheet) target.styleSheets.Remove(addedSheet);
            }

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0 || owner.state.RefSheetOpen) return;
                if (evt.clickCount == 2) { owner.ResetFraming(); evt.StopPropagation(); return; }
                last = evt.position;
                drag.Begin(evt.pointerId);
                target.AddToClassList("ps-surface--dragging");
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                evt.StopPropagation();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (!drag.Owns(evt.pointerId)) return;
                Vector2 delta = (Vector2)evt.position - last;
                last = evt.position;
                target.EnableInClassList("ps-surface--turning", evt.shiftKey);
                try
                {
                    if (evt.shiftKey)
                    {
                        // A turn starts around the bone nearest the middle of the view.
                        if (!turning) { turning = true; owner.state.BeginTurn(); }
                        owner.TurnBy(delta);
                    }
                    else
                    {
                        turning = false;
                        owner.MoveBy(delta, frameSize(), kind);
                    }
                }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            }

            private void End()
            {
                turning = false;
                target.RemoveFromClassList("ps-surface--dragging");
                target.RemoveFromClassList("ps-surface--turning");
            }

            // Scrolling over the preview zooms instead of scrolling the window.
            private void OnWheel(WheelEvent evt)
            {
                if (owner.state.RefSheetOpen) return;
                owner.ZoomBy(evt.delta.y);
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }
    }
}
