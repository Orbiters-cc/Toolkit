using System;
using System.Collections.Generic;

namespace Orbiters.Toolkit.Editor.VRChat.Build
{
    /// <summary>
    /// One step of Orbiters' avatar build: it runs on the build copy (an upload's, Build &amp; Test's, VRCFury's Play Mode
    /// copy, an Orbiters hidden build) in its <see cref="Phase"/>, after the steps of that phase it names in
    /// <see cref="After"/> and before those in <see cref="Before"/>. A package adds a step by declaring a class: every
    /// non-abstract subclass with a parameterless constructor is found when scripts load (as the SDK finds its callbacks),
    /// and no package implements the SDK's callback interface itself.
    /// </summary>
    public abstract class BuildStep
    {
        private static readonly Type[] NoSteps = Array.Empty<Type>();

        public abstract BuildPhase Phase { get; }

        /// <summary>Steps of the same phase (or an earlier one) this one must come after, by type.</summary>
        public virtual IReadOnlyList<Type> After => NoSteps;

        /// <summary>Steps of the same phase (or a later one) this one must come before, by type.</summary>
        public virtual IReadOnlyList<Type> Before => NoSteps;

        /// <summary>Who the step belongs to as people know it ("My Avatar", "MCB"...), for progress and errors.</summary>
        public virtual string Owner => "Orbiters Toolkit";

        /// <summary>Changes the build copy. False, or an exception, stops the build, as an SDK callback's does.</summary>
        public abstract bool Run(BuildContext context);

        /// <summary>
        /// The SDK callback order the step runs at (its phase's, <see cref="BuildPhases.Order"/>), for code that compares
        /// it with SDK callbacks' numbers. Inside a phase the steps' declared order decides.
        /// </summary>
        // Named as the SDK's property: tools and tests that read build steps' orders by name keep reading this one.
        public int callbackOrder => BuildPhases.Order(Phase);

        public override string ToString() => GetType().Name + " (" + Phase + ")";
    }
}
