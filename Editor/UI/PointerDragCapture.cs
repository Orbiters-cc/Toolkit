using System;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    // A drag owns its capture only until release, cancellation, detachment or disposal.
    internal sealed class PointerDragCapture : IDisposable
    {
        private readonly VisualElement target;
        private readonly Action ended;
        private int pointer = -1;

        internal PointerDragCapture(VisualElement target, Action ended = null)
        {
            this.target = target;
            this.ended = ended;
            target.RegisterCallback<PointerUpEvent>(OnUp);
            target.RegisterCallback<PointerCancelEvent>(OnCancel);
            target.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            target.RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        internal void Begin(int pointerId)
        {
            End();
            pointer = pointerId;
            target.CapturePointer(pointerId);
        }

        internal bool Owns(int pointerId) => pointer == pointerId && target.HasPointerCapture(pointerId);

        internal void End()
        {
            int previous = pointer;
            if (previous < 0) return;
            // Clear first: releasing can synchronously send PointerCaptureOutEvent.
            pointer = -1;
            try { if (target.HasPointerCapture(previous)) target.ReleasePointer(previous); }
            finally { ended?.Invoke(); }
        }

        private void OnUp(PointerUpEvent evt) { if (evt.pointerId == pointer) End(); }
        private void OnCancel(PointerCancelEvent evt) { if (evt.pointerId == pointer) End(); }
        private void OnCaptureOut(PointerCaptureOutEvent evt) { if (evt.pointerId == pointer) End(); }
        private void OnDetach(DetachFromPanelEvent evt) => End();

        public void Dispose()
        {
            End();
            target.UnregisterCallback<PointerUpEvent>(OnUp);
            target.UnregisterCallback<PointerCancelEvent>(OnCancel);
            target.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            target.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
        }
    }
}
