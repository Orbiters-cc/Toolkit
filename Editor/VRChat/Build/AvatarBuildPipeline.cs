using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.Toolkit.Editor.VRChat.Build
{
    /// <summary>
    /// Orbiters' avatar build: every <see cref="BuildStep"/> of every package, run by one VRChat SDK callback per
    /// <see cref="BuildPhase"/>. Within a phase the steps run in the order their <see cref="BuildStep.After"/> and
    /// <see cref="BuildStep.Before"/> give, steps nothing orders by type name; a cycle, or a step asking to run after one of
    /// a later phase, is an error naming the steps. Orbiters' hidden builds run the same steps
    /// (<see cref="HiddenAvatarBuild"/>).
    /// </summary>
    public static class AvatarBuildPipeline
    {
        private static List<BuildStep> steps;

        /// <summary>Every step found, in the order they run (phase by phase).</summary>
        public static IReadOnlyList<BuildStep> Steps => steps ??= Sort(Discover());

        /// <summary>The steps of one phase, in the order they run.</summary>
        public static IEnumerable<BuildStep> StepsIn(BuildPhase phase) => Steps.Where(s => s.Phase == phase);

        // Every non-abstract step type with a parameterless constructor, as the SDK finds its callbacks.
        private static IEnumerable<BuildStep> Discover()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<BuildStep>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || type.GetConstructor(Type.EmptyTypes) == null) continue;
                BuildStep step;
                try { step = (BuildStep)Activator.CreateInstance(type); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    continue;
                }
                yield return step;
            }
        }

        /// <summary>
        /// <paramref name="found"/> in the order they run: by phase, then as their After and Before say, then by type name.
        /// Throws when two steps must come before each other (the message names the cycle) or a step's order points at a
        /// step of a phase on the wrong side of its own.
        /// </summary>
        internal static List<BuildStep> Sort(IEnumerable<BuildStep> found)
        {
            var all = found.GroupBy(s => s.GetType()).Select(g => g.First()).ToList();
            var byType = all.ToDictionary(s => s.GetType());
            // Edges: before → after, within a phase; across phases only checked.
            var next = all.ToDictionary(s => s, _ => new List<BuildStep>());
            var waits = all.ToDictionary(s => s, _ => 0);
            void Edge(BuildStep first, BuildStep then)
            {
                if (first.Phase != then.Phase)
                {
                    if (first.Phase > then.Phase)
                        throw new InvalidOperationException("Orbiters build steps: " + Name(first) + " must run before " + Name(then) +
                            ", but its phase (" + first.Phase + ") comes after " + then.Phase + ".");
                    return;
                }
                if (next[first].Contains(then)) return;
                next[first].Add(then);
                waits[then]++;
            }
            foreach (var step in all)
            {
                foreach (var type in step.After ?? Array.Empty<Type>())
                    if (type != null && byType.TryGetValue(type, out var before)) Edge(before, step);
                foreach (var type in step.Before ?? Array.Empty<Type>())
                    if (type != null && byType.TryGetValue(type, out var after)) Edge(step, after);
            }
            var sorted = new List<BuildStep>(all.Count);
            foreach (var phase in all.Select(s => s.Phase).Distinct().OrderBy(p => p))
            {
                var ready = new SortedSet<BuildStep>(all.Where(s => s.Phase == phase && waits[s] == 0), ByName.Instance);
                int count = all.Count(s => s.Phase == phase), done = 0;
                while (ready.Count > 0)
                {
                    var step = ready.Min;
                    ready.Remove(step);
                    sorted.Add(step);
                    done++;
                    foreach (var then in next[step])
                        if (--waits[then] == 0) ready.Add(then);
                }
                if (done < count)
                    throw new InvalidOperationException("Orbiters build steps of phase " + phase + " wait on each other: " +
                        string.Join(" → ", Cycle(all.Where(s => s.Phase == phase && waits[s] > 0).ToList(), next).Select(Name)) + ".");
            }
            return sorted;
        }

        // One cycle among the steps left waiting, for the error.
        private static List<BuildStep> Cycle(List<BuildStep> waiting, Dictionary<BuildStep, List<BuildStep>> next)
        {
            var left = new HashSet<BuildStep>(waiting);
            var path = new List<BuildStep>();
            var step = waiting.OrderBy(s => s.GetType().FullName, StringComparer.Ordinal).First();
            while (!path.Contains(step))
            {
                path.Add(step);
                step = next[step].Where(left.Contains).OrderBy(s => s.GetType().FullName, StringComparer.Ordinal).FirstOrDefault();
                if (step == null) return path;
            }
            var cycle = path.Skip(path.IndexOf(step)).ToList();
            cycle.Add(step);
            return cycle;
        }

        private static string Name(BuildStep step) => step.GetType().FullName;

        private sealed class ByName : IComparer<BuildStep>
        {
            public static readonly ByName Instance = new ByName();
            public int Compare(BuildStep a, BuildStep b) => string.CompareOrdinal(a?.GetType().FullName, b?.GetType().FullName);
        }

        /// <summary>Runs one phase's steps on <paramref name="avatar"/>; false when one stopped the build.</summary>
        internal static bool RunPhase(BuildPhase phase, GameObject avatar) => RunPhase(phase, avatar, StepsIn(phase));

        // A step that stops the build (false, or an exception) ends it: what the steps shared is forgotten at once. A build
        // another tool's callback stops ends with the SDK's build (BuildContext follows VrcSdkBuilds.Finished).
        internal static bool RunPhase(BuildPhase phase, GameObject avatar, IEnumerable<BuildStep> steps)
        {
            var context = BuildContext.Enter(avatar, phase, true);
            bool done = false;
            try
            {
                foreach (var step in steps)
                    if (!step.Run(context)) return false;
                done = true;
                return true;
            }
            finally
            {
                if (!done || phase == BuildPhase.Last) BuildContext.End(avatar);
            }
        }

        /// <summary>Runs one step on <paramref name="avatar"/> (a hidden build's list); <paramref name="first"/>: the first of its phase.</summary>
        internal static bool RunStep(BuildStep step, GameObject avatar, bool first) => step.Run(BuildContext.Enter(avatar, step.Phase, first));

        [InitializeOnLoadMethod]
        private static void CheckOrder()
        {
            // A cycle is the steps' authors' error: said once per load, and the build stops on it too.
            try { _ = Steps; }
            catch (InvalidOperationException ex) { Debug.LogError("[Orbiters] " + ex.Message); }
        }
    }

    /// <summary>One phase of <see cref="AvatarBuildPipeline"/> as the SDK sees it: a callback at the phase's order.</summary>
    internal abstract class BuildPhaseCallback : IVRCSDKPreprocessAvatarCallback
    {
        internal abstract BuildPhase Phase { get; }
        public int callbackOrder => BuildPhases.Order(Phase);
        public bool OnPreprocessAvatar(GameObject avatarGameObject) => AvatarBuildPipeline.RunPhase(Phase, avatarGameObject);
    }

    internal sealed class BuildStartCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.Start; }
    internal sealed class BuildBeforeVrcFuryCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.BeforeVrcFury; }
    internal sealed class BuildAfterVrcFuryCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.AfterVrcFury; }
    internal sealed class BuildAccessoriesCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.Accessories; }
    internal sealed class BuildAccessoryShapesCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.AccessoryShapes; }
    internal sealed class BuildStructureCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.Structure; }
    internal sealed class BuildBeforeLazyOptimiserCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.BeforeLazyOptimiser; }
    internal sealed class BuildAfterLazyOptimiserCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.AfterLazyOptimiser; }
    internal sealed class BuildSaveDataCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.SaveBuildData; }
    internal sealed class BuildLastCallback : BuildPhaseCallback { internal override BuildPhase Phase => BuildPhase.Last; }
}
