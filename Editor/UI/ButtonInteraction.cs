using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>Buttons that act on press (pointer, mouse or keyboard) and keep Unity's click as a fallback.</summary>
    public static class ButtonInteraction
    {
        public static void RegisterImmediateClick(Button button, Action onClick)
        {
            if (button == null || onClick == null)
            {
                return;
            }

            bool suppressNextClicked = false;
            long lastImmediateTicks = 0L;

            void activateImmediate(EventBase evt)
            {
                long now = DateTime.UtcNow.Ticks;
                if (!button.enabledInHierarchy ||
                    now - lastImmediateTicks < TimeSpan.TicksPerMillisecond * 25L)
                {
                    evt.StopImmediatePropagation();
                    evt.PreventDefault();
                    return;
                }

                lastImmediateTicks = now;
                suppressNextClicked = true;
                button.schedule.Execute(() => suppressNextClicked = false).StartingIn(1000);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                onClick();
            }

            button.clicked += () =>
            {
                if (suppressNextClicked)
                {
                    suppressNextClicked = false;
                    return;
                }

                onClick();
            };

            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                activateImmediate(evt);
            }, TrickleDown.TrickleDown);
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                activateImmediate(evt);
            }, TrickleDown.TrickleDown);
            button.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return &&
                    evt.keyCode != KeyCode.KeypadEnter &&
                    evt.keyCode != KeyCode.Space)
                {
                    return;
                }

                activateImmediate(evt);
            }, TrickleDown.TrickleDown);
        }
    }
}
