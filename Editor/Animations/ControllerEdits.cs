using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// Many edits to big animator controllers at once. Unity rebuilds a whole controller each time one of its states,
    /// transitions, blend trees or layer masks, or a clip it plays, changes: each edit costs as much as the controller is
    /// big, and renaming the parameters of a controller of thousands of states took ten minutes of an avatar's build.
    /// Inside <see cref="Batch"/> the controllers' layers hold an empty state machine and no mask, so each edit costs
    /// nothing, and get theirs back when the batch is disposed: one rebuild per controller. Inside a batch, reach states,
    /// transitions and masks through the objects already in hand (a controller's layers show the empty stand-in), and add
    /// or remove no layers. Nothing is saved meanwhile: a controller saved inside a batch would be written without its
    /// state machines.
    /// </summary>
    public static class ControllerEdits
    {
        public static IDisposable Batch(IEnumerable<AnimatorController> controllers)
        {
            var detached = new List<(AnimatorController Controller, AnimatorStateMachine[] Machines, AvatarMask[] Masks)>();
            AnimatorStateMachine standIn = null;
            try
            {
                foreach (var controller in (controllers ?? Enumerable.Empty<AnimatorController>()).Where(c => c != null).Distinct())
                {
                    var layers = controller.layers;
                    if (layers.Length == 0) continue;
                    if (standIn == null) standIn = new AnimatorStateMachine { name = "Orbiters edits", hideFlags = HideFlags.HideAndDontSave };
                    var machines = layers.Select(l => l.stateMachine).ToArray();
                    var masks = layers.Select(l => l.avatarMask).ToArray();
                    foreach (var layer in layers)
                    {
                        if (layer.stateMachine != null) layer.stateMachine = standIn;
                        layer.avatarMask = null;
                    }
                    controller.layers = layers;
                    detached.Add((controller, machines, masks));
                }
            }
            catch
            {
                Restore(detached, standIn);
                throw;
            }
            return new Scope(() => Restore(detached, standIn));
        }

        private static void Restore(List<(AnimatorController Controller, AnimatorStateMachine[] Machines, AvatarMask[] Masks)> detached, AnimatorStateMachine standIn)
        {
            foreach (var (controller, machines, masks) in detached)
            {
                if (controller == null) continue;
                var layers = controller.layers;
                for (int i = 0; i < layers.Length && i < machines.Length; i++)
                {
                    layers[i].stateMachine = machines[i];
                    layers[i].avatarMask = masks[i];
                }
                controller.layers = layers;
            }
            detached.Clear();
            if (standIn != null) Object.DestroyImmediate(standIn);
        }

        private sealed class Scope : IDisposable
        {
            private Action end;
            public Scope(Action end) => this.end = end;
            public void Dispose()
            {
                var run = end;
                end = null;
                run?.Invoke();
            }
        }
    }
}
