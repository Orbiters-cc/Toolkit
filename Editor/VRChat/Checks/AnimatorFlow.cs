using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.Toolkit.Editor.VRChat.Checks
{
    /// <summary>
    /// The playable layers of a built avatar: transitions and blend trees that read a parameter their controller doesn't
    /// have (they never change), and states no transition leads to (their animations never play).
    /// </summary>
    public static class AnimatorFlow
    {
        public static List<AvatarIssue> Find(GameObject root)
        {
            var issues = new List<AvatarIssue>();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return issues;
            var seen = new HashSet<AnimatorController>();
            foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                         .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
            {
                if (layer.isDefault) continue;
                var controller = layer.animatorController is AnimatorOverrideController overrides ? overrides.runtimeAnimatorController as AnimatorController
                    : layer.animatorController as AnimatorController;
                if (controller == null || !seen.Add(controller)) continue;
                var declared = new HashSet<string>(controller.parameters.Select(p => p.name), StringComparer.Ordinal);
                foreach (var animatorLayer in controller.layers)
                {
                    if (animatorLayer.stateMachine == null || animatorLayer.syncedLayerIndex >= 0) continue;
                    string where = layer.type + " / " + animatorLayer.name;
                    var missing = new Dictionary<string, int>(StringComparer.Ordinal);
                    void Need(string parameter) { if (!string.IsNullOrEmpty(parameter) && !declared.Contains(parameter)) missing[parameter] = missing.TryGetValue(parameter, out int n) ? n + 1 : 1; }
                    foreach (var transition in Transitions(animatorLayer.stateMachine))
                        foreach (var condition in transition.conditions) Need(condition.parameter);
                    foreach (var tree in Trees(animatorLayer.stateMachine))
                    {
                        if (tree.blendType == BlendTreeType.Direct) foreach (var child in tree.children) Need(child.directBlendParameter);
                        else
                        {
                            Need(tree.blendParameter);
                            if (tree.blendType != BlendTreeType.Simple1D) Need(tree.blendParameterY);
                        }
                    }
                    foreach (var pair in missing)
                        issues.Add(new AvatarIssue(IssueLevel.Problem, $"“{animatorLayer.name}” reads “{pair.Key}”, which its controller doesn't have",
                            $"{pair.Value} transition{(pair.Value == 1 ? "" : "s")} or blend tree{(pair.Value == 1 ? "" : "s")} wait on it and never change. Add the parameter to the controller.", where));
                    var unreachable = Unreachable(animatorLayer.stateMachine);
                    if (unreachable.Count > 0)
                        issues.Add(new AvatarIssue(IssueLevel.Warning, $"“{animatorLayer.name}”: {unreachable.Count} state{(unreachable.Count == 1 ? "" : "s")} can never play",
                            "No transition leads to " + string.Join(", ", unreachable.Take(5).Select(s => "“" + s.name + "”")) + (unreachable.Count > 5 ? " and others" : "") +
                            ": their animations never show. Add a transition to them, or remove them.", where));
                }
            }
            return issues;
        }

        private static IEnumerable<AnimatorStateMachine> Machines(AnimatorStateMachine machine)
        {
            yield return machine;
            foreach (var child in machine.stateMachines)
                foreach (var nested in Machines(child.stateMachine)) yield return nested;
        }

        private static IEnumerable<AnimatorTransitionBase> Transitions(AnimatorStateMachine root)
        {
            foreach (var machine in Machines(root))
            {
                foreach (var t in machine.anyStateTransitions) yield return t;
                foreach (var t in machine.entryTransitions) yield return t;
                foreach (var state in machine.states) foreach (var t in state.state.transitions) yield return t;
                foreach (var child in machine.stateMachines) foreach (var t in machine.GetStateMachineTransitions(child.stateMachine)) yield return t;
            }
        }

        private static IEnumerable<BlendTree> Trees(AnimatorStateMachine root)
        {
            IEnumerable<BlendTree> Nested(Motion motion)
            {
                if (!(motion is BlendTree tree)) yield break;
                yield return tree;
                foreach (var child in tree.children) foreach (var nested in Nested(child.motion)) yield return nested;
            }
            return Machines(root).SelectMany(m => m.states).SelectMany(s => Nested(s.state.motion)).Distinct();
        }

        /// <summary>
        /// The states of a layer nothing leads to: from the layer's entry (default state and entry transitions), its Any
        /// State transitions and every state reached, through sub-state machines' entries and exits.
        /// </summary>
        public static List<AnimatorState> Unreachable(AnimatorStateMachine root)
        {
            var parents = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var owner = new Dictionary<AnimatorState, AnimatorStateMachine>();
            foreach (var machine in Machines(root))
            {
                foreach (var child in machine.stateMachines) parents[child.stateMachine] = machine;
                foreach (var state in machine.states) owner[state.state] = machine;
            }
            var reached = new HashSet<AnimatorState>();
            var entered = new HashSet<AnimatorStateMachine>();
            var queue = new Queue<AnimatorState>();
            void Reach(AnimatorState state) { if (state != null && reached.Add(state)) queue.Enqueue(state); }
            void Enter(AnimatorStateMachine machine)
            {
                if (machine == null || !entered.Add(machine)) return;
                Reach(machine.defaultState);
                foreach (var t in machine.entryTransitions) Go(t, machine);
            }
            void Exit(AnimatorStateMachine machine)
            {
                // Leaving a sub-state machine follows its parent's transitions from it, else the parent's entry.
                if (!parents.TryGetValue(machine, out var parent)) { Enter(root); return; }
                var outs = parent.GetStateMachineTransitions(machine);
                if (outs.Length == 0) Enter(parent);
                foreach (var t in outs) Go(t, parent);
            }
            void Go(AnimatorTransitionBase transition, AnimatorStateMachine from)
            {
                if (transition.destinationState != null) Reach(transition.destinationState);
                else if (transition.destinationStateMachine != null) Enter(transition.destinationStateMachine);
                else if (transition.isExit) Exit(from);
            }
            Enter(root);
            foreach (var machine in Machines(root)) foreach (var t in machine.anyStateTransitions) Go(t, machine);
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                foreach (var t in state.transitions) Go(t, owner.TryGetValue(state, out var machine) ? machine : root);
            }
            return owner.Keys.Where(s => !reached.Contains(s)).ToList();
        }
    }
}
