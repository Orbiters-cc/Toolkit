using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.Toolkit.Editor.Animations
{
    /// <summary>
    /// A deep copy of an animator controller in memory: its layers, state machines, states, transitions, state behaviours,
    /// blend trees and clips are all new objects, so the copy can be edited and played without touching (or saving) an
    /// asset. <see cref="Destroy"/> frees everything the copy made.
    /// </summary>
    public sealed class AnimatorControllerCopy
    {
        public AnimatorController Controller { get; private set; }
        private readonly List<Object> made = new List<Object>();
        private readonly Dictionary<Motion, Motion> motions = new Dictionary<Motion, Motion>();
        private readonly Dictionary<AnimatorState, AnimatorState> states = new Dictionary<AnimatorState, AnimatorState>();
        private readonly Dictionary<AnimatorStateMachine, AnimatorStateMachine> machines = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
        private readonly Func<AnimationClip, AnimationClip> copyClip;

        private AnimatorControllerCopy(Func<AnimationClip, AnimationClip> copyClip) => this.copyClip = copyClip;

        /// <summary>
        /// Copies <paramref name="source"/>. Each clip is copied with <paramref name="copyClip"/> (by default a plain copy),
        /// which may also change the copy, e.g. rewrite its paths.
        /// </summary>
        public static AnimatorControllerCopy Of(AnimatorController source, Func<AnimationClip, AnimationClip> copyClip = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var copy = new AnimatorControllerCopy(copyClip);
            var controller = copy.Make(new AnimatorController { name = source.name });
            controller.parameters = source.parameters;
            var layers = source.layers;
            foreach (var layer in layers) if (layer.stateMachine != null) layer.stateMachine = copy.Machine(layer.stateMachine);
            foreach (var layer in source.layers) if (layer.stateMachine != null) copy.Transitions(layer.stateMachine);
            controller.layers = layers;
            copy.Controller = controller;
            return copy;
        }

        /// <summary>
        /// A new blend tree with <paramref name="source"/>'s settings and children (the same motions). Unity asserts when a
        /// blend tree holding other trees is instantiated, so trees are copied this way.
        /// </summary>
        public static BlendTree Tree(BlendTree source) => new BlendTree
        {
            name = source.name, blendType = source.blendType, blendParameter = source.blendParameter, blendParameterY = source.blendParameterY,
            minThreshold = source.minThreshold, maxThreshold = source.maxThreshold, useAutomaticThresholds = source.useAutomaticThresholds,
            children = source.children,
        };

        /// <summary>Everything the copy made, its clips too.</summary>
        public IReadOnlyList<Object> Made => made;

        public void Destroy()
        {
            foreach (var value in made) if (value != null) Object.DestroyImmediate(value);
            made.Clear();
            Controller = null;
        }

        private T Make<T>(T value) where T : Object
        {
            value.hideFlags = HideFlags.HideAndDontSave;
            made.Add(value);
            return value;
        }

        private StateMachineBehaviour[] Behaviours(StateMachineBehaviour[] source) =>
            source.Where(b => b != null).Select(b => Make(Object.Instantiate(b))).ToArray();

        private AnimatorStateMachine Machine(AnimatorStateMachine source)
        {
            var machine = Make(new AnimatorStateMachine
            {
                name = source.name, anyStatePosition = source.anyStatePosition, entryPosition = source.entryPosition,
                exitPosition = source.exitPosition, parentStateMachinePosition = source.parentStateMachinePosition,
            });
            machines[source] = machine;
            machine.states = source.states.Select(c => new ChildAnimatorState { state = State(c.state), position = c.position }).ToArray();
            machine.stateMachines = source.stateMachines.Select(c => new ChildAnimatorStateMachine { stateMachine = Machine(c.stateMachine), position = c.position }).ToArray();
            machine.behaviours = Behaviours(source.behaviours);
            if (source.defaultState != null && states.TryGetValue(source.defaultState, out var start)) machine.defaultState = start;
            return machine;
        }

        private AnimatorState State(AnimatorState source)
        {
            var state = Make(new AnimatorState
            {
                name = source.name, tag = source.tag, speed = source.speed, speedParameter = source.speedParameter, speedParameterActive = source.speedParameterActive,
                cycleOffset = source.cycleOffset, cycleOffsetParameter = source.cycleOffsetParameter, cycleOffsetParameterActive = source.cycleOffsetParameterActive,
                mirror = source.mirror, mirrorParameter = source.mirrorParameter, mirrorParameterActive = source.mirrorParameterActive,
                timeParameter = source.timeParameter, timeParameterActive = source.timeParameterActive, iKOnFeet = source.iKOnFeet,
                writeDefaultValues = source.writeDefaultValues, motion = CopyMotion(source.motion),
            });
            states[source] = state;
            state.behaviours = Behaviours(source.behaviours);
            return state;
        }

        private Motion CopyMotion(Motion source)
        {
            if (source == null) return null;
            if (motions.TryGetValue(source, out var known)) return known;
            if (source is BlendTree tree)
            {
                var copied = Make(Tree(tree));
                motions[source] = copied;
                copied.children = tree.children.Select(c => { c.motion = CopyMotion(c.motion); return c; }).ToArray();
                return copied;
            }
            Motion result = source;
            if (source is AnimationClip clip)
            {
                var copiedClip = copyClip != null ? copyClip(clip) : Object.Instantiate(clip);
                if (copiedClip != null && copiedClip != clip) { copiedClip.name = clip.name; Make(copiedClip); }
                result = copiedClip != null ? copiedClip : clip;
            }
            motions[source] = result;
            return result;
        }

        // Transitions point at states and machines anywhere in the layer: copied once every state is.
        private void Transitions(AnimatorStateMachine source)
        {
            var machine = machines[source];
            machine.anyStateTransitions = source.anyStateTransitions.Select(StateTransition).ToArray();
            machine.entryTransitions = source.entryTransitions.Select(Transition).ToArray();
            foreach (var child in source.states) states[child.state].transitions = child.state.transitions.Select(StateTransition).ToArray();
            foreach (var child in source.stateMachines)
            {
                machine.SetStateMachineTransitions(machines[child.stateMachine], source.GetStateMachineTransitions(child.stateMachine).Select(Transition).ToArray());
                Transitions(child.stateMachine);
            }
        }

        private AnimatorStateTransition StateTransition(AnimatorStateTransition source)
        {
            var transition = Make(new AnimatorStateTransition
            {
                name = source.name, hasExitTime = source.hasExitTime, exitTime = source.exitTime, hasFixedDuration = source.hasFixedDuration,
                duration = source.duration, offset = source.offset, interruptionSource = source.interruptionSource,
                orderedInterruption = source.orderedInterruption, canTransitionToSelf = source.canTransitionToSelf,
            });
            Point(source, transition);
            return transition;
        }

        private AnimatorTransition Transition(AnimatorTransition source)
        {
            var transition = Make(new AnimatorTransition { name = source.name });
            Point(source, transition);
            return transition;
        }

        private void Point(AnimatorTransitionBase source, AnimatorTransitionBase transition)
        {
            transition.conditions = source.conditions;
            transition.mute = source.mute; transition.solo = source.solo;
            transition.isExit = source.isExit;
            if (source.destinationState != null && states.TryGetValue(source.destinationState, out var state)) transition.destinationState = state;
            if (source.destinationStateMachine != null && machines.TryGetValue(source.destinationStateMachine, out var machine)) transition.destinationStateMachine = machine;
        }
    }
}
