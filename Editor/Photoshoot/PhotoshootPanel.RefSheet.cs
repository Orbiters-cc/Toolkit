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
        // ---- Ref sheet ----------------------------------------------------------------------------------------

        /// <summary>Shows the ref sheet (front, back and side) instead of the shots, or the shots again.</summary>
        public void ShowRefSheet(bool show)
        {
            if (state.RefSheetOpen == show || (show && options.RefSheet == null)) return;
            framingTween?.Pause();
            state.RefSheetOpen = show;
            state.Status = null;
            Build();
            // Both use the thumbnail's render target: whichever shows now renders again at its own size.
            if (show) RenderRefSheet(true);
            else RenderPreviews(false);
            options.Changed?.Invoke();
        }

        private void BuildRefSheetStage()
        {
            var stage = new VisualElement();
            stage.AddToClassList("ps-stage");
            stage.AddToClassList("ps-stage--refsheet");
            column.Add(stage);
            stage.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float width = stage.resolvedStyle.width;
                if (width > 0f && !float.IsNaN(width)) stage.style.height = Mathf.Min(width * PhotoshootRefSheet.Size.y / PhotoshootRefSheet.Size.x, 440f);
            });
            refSheetImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            refSheetImage.AddToClassList("ps-stage__image");
            stage.Add(refSheetImage);
            flash = new VisualElement { pickingMode = PickingMode.Ignore };
            flash.AddToClassList("ps-stage__flash");
            stage.Add(flash);
            stage.AddManipulator(new RefSheetDrag(this));
        }

        private void BuildRefSheetShot()
        {
            var card = Card("ps-shots");
            var row = new VisualElement();
            row.AddToClassList("ps-shot");
            card.Add(row);
            var icon = new VectorIcon(IconGlyph.RefSheet);
            icon.AddToClassList("ps-shot__icon");
            row.Add(icon);
            var text = new VisualElement();
            text.AddToClassList("ps-shot__text");
            row.Add(text);
            var title = new Label("Ref sheet");
            title.AddToClassList("ps-shot__title");
            text.Add(title);
            var size = PhotoshootRefSheet.Size;
            var detail = new Label($"Live · {size.x}×{size.y}");
            detail.AddToClassList("ps-shot__detail");
            text.Add(detail);
            refSheetCapture = CreateButton("Capture", CaptureRefSheet, "ps-pill");
            refSheetCapture.AddToClassList("ps-pill--accent");
            refSheetCapture.tooltip = $"Capture the sheet as shown, at {size.x}×{size.y}.";
            row.Add(refSheetCapture);
            message = new Label();
            message.AddToClassList("ps-message");
            card.Add(message);
        }

        private void BuildRefSheetFraming()
        {
            var card = Card("ps-framing");
            var header = Header(card, "Framing");
            var fit = CreateButton("Fit", FitRefSheet, "ps-link");
            fit.tooltip = "The whole body in every view, at one scale.";
            header.Add(fit);

            zoomDial = new ScrubDial("Zoom", Mathf.Log(PhotoshootState.MinRefSheetZoom, 2f), Mathf.Log(PhotoshootState.MaxRefSheetZoom, 2f), 0f,
                0.1f, 5, 70f, 0.04f,
                value => Mathf.Pow(2f, value).ToString("0.00") + "×",
                value =>
                {
                    framingTween?.Pause();
                    state.RefSheetZoom = Mathf.Pow(2f, value);
                    RenderRefSheet(false);
                });
            zoomDial.tooltip = "Every view zooms together, so they keep one scale.";
            card.Add(zoomDial);

            var side = new VisualElement();
            side.AddToClassList("ps-look");
            var sideLabel = new Label("Side view");
            sideLabel.AddToClassList("ps-look__label");
            side.Add(sideLabel);
            sideControl = new SegmentedControl(new[] { "Faces left", "Faces right" }, index =>
            {
                if (state.RefSheetSideFacesRight == (index == 1)) return;
                state.RefSheetSideFacesRight = index == 1;
                RenderRefSheet(true);
            });
            sideControl.AddToClassList("ps-refsheet__side");
            side.Add(sideControl);
            card.Add(side);

            var tip = new Label("On the sheet: drag up or down to move, scroll to zoom, double-click to fit");
            tip.AddToClassList("ps-caption");
            tip.AddToClassList("ps-framing__tip");
            card.Add(tip);
            Foldable(card, header);
            SyncFraming();
        }

        // The three views render in the same event as the input, like framing: each reuses the posed avatar copy.
        private void RenderRefSheet(bool refit, bool forceFaceBlendshapeApply = false)
        {
            ++state.RefreshTicket;
            var avatarRoot = AvatarRoot;
            if (avatarRoot != null && CanGenerate()) state.RenderRefSheet(avatarRoot, RefSheetPreviewSize, refit, forceFaceBlendshapeApply);
            RefreshImages();
            UpdateMessage();
            RepaintPreview();
            if (state.TakeUnsettled()) Settle();
        }

        private void CaptureRefSheet()
        {
            if (state.IsGenerating || !CanGenerate() || options.RefSheet == null) return;
            state.IsGenerating = true;
            state.Error = null;
            bool captured = false;
            try
            {
                var sheet = state.CaptureRefSheet(AvatarRoot);
                state.Status = "Ref sheet captured";
                options.RefSheet(sheet);
                captured = true;
            }
            catch (Exception ex)
            {
                state.Error = ex.Message;
                state.Status = null;
            }
            finally
            {
                state.IsGenerating = false;
            }

            if (captured) Flash();
            // The capture rendered the views at full size: the live sheet renders again at its own.
            RenderRefSheet(false);
            Refresh();
            options.Changed?.Invoke();
        }

        private void Flash()
        {
            if (flash == null) return;
            flash.AddToClassList("ps-stage__flash--on");
            flash.schedule.Execute(() => flash.RemoveFromClassList("ps-stage__flash--on")).StartingIn(40);
        }

        // Back to the whole body in every view, gliding like the framing presets.
        private void FitRefSheet()
        {
            framingTween?.Pause();
            float fromZoom = Mathf.Log(state.RefSheetZoom), fromLift = state.RefSheetLift;
            double start = EditorApplication.timeSinceStartup;
            framingTween = schedule.Execute(() =>
            {
                float t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - start) / FramingTweenSeconds));
                float eased = EaseOutBack(t);
                state.RefSheetZoom = Mathf.Exp(Mathf.LerpUnclamped(fromZoom, 0f, eased));
                state.RefSheetLift = Mathf.LerpUnclamped(fromLift, 0f, eased);
                SyncFraming();
                RenderRefSheet(false);
                if (t >= 1f) framingTween?.Pause();
            }).Every(16);
        }

        private void LiftRefSheet(float pixels, float sheetHeight)
        {
            float viewHeight = sheetHeight * (1f - PhotoshootRefSheet.LabelBand - PhotoshootRefSheet.Floor);
            if (viewHeight <= 0f) return;
            framingTween?.Pause();
            // Dragging up moves the views up: the avatar follows the pointer.
            state.RefSheetLift -= pixels / viewHeight * state.RefSheetLiftPerView(RefSheetPreviewSize);
            RenderRefSheet(false);
        }

        private void ZoomRefSheet(float wheel)
        {
            framingTween?.Pause();
            state.RefSheetZoom *= Mathf.Exp(-wheel * 0.05f);
            SyncFraming();
            RenderRefSheet(false);
        }

        /// <summary>On the ref sheet: drag up and down to move every view, scroll to zoom them, double-click to fit.</summary>
        private sealed class RefSheetDrag : PointerManipulator
        {
            private readonly PhotoshootPanel owner;
            private Label hint;
            private PointerDragCapture drag;
            private float last;

            public RefSheetDrag(PhotoshootPanel owner) => this.owner = owner;

            protected override void RegisterCallbacksOnTarget()
            {
                target.AddToClassList("ps-surface");
                target.AddToClassList("ps-surface--vertical");
                hint = new Label("Drag to move · Scroll to zoom · Double-click to fit") { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("ps-surface__hint");
                target.Add(hint);
                target.RegisterCallback<PointerDownEvent>(OnDown);
                target.RegisterCallback<PointerMoveEvent>(OnMove);
                drag = new PointerDragCapture(target, () => target.RemoveFromClassList("ps-surface--dragging"));
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
                target.RemoveFromClassList("ps-surface--vertical");
                target.RemoveFromClassList("ps-surface--dragging");
            }

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                if (evt.clickCount == 2) { owner.FitRefSheet(); evt.StopPropagation(); return; }
                last = evt.position.y;
                drag.Begin(evt.pointerId);
                target.AddToClassList("ps-surface--dragging");
                evt.StopPropagation();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (!drag.Owns(evt.pointerId)) return;
                float delta = evt.position.y - last;
                last = evt.position.y;
                // The sheet fills the stage's width or its height, whichever it reaches first.
                var rect = target.contentRect;
                float sheetHeight = Mathf.Min(rect.height, rect.width * PhotoshootRefSheet.Size.y / PhotoshootRefSheet.Size.x);
                try { owner.LiftRefSheet(delta, sheetHeight); }
                catch { drag.End(); throw; }
                evt.StopPropagation();
            }

            private void OnWheel(WheelEvent evt)
            {
                owner.ZoomRefSheet(evt.delta.y);
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }
    }
}
