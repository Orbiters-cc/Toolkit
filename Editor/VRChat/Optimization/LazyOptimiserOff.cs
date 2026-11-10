using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Editor.VRChat.Build;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Optimization
{
    /// <summary>
    /// Lazy Optimiser (net.euan.lazyoptimiser) runs on every avatar build unless it is turned off, and deletes the objects
    /// it finds no animation showing. It follows m_IsActive only (it looks for a lowercase "m_enabled", which no binding
    /// has), so objects an animation switches on through a component's m_Enabled go: N4N0's capture rig, capture kit and
    /// mobile orb in the compatibility project. Orbiters' behaviour wins: for avatars holding Orbiters components it is
    /// turned off just before its first pass (−100) and given back just after its last (−90), in memory only — its own
    /// switch (the PlayerPrefs key "lazyoptimiser.shouldOptimise") is never written.
    /// </summary>
    public static class LazyOptimiser
    {
        public const string PackageId = "net.euan.lazyoptimiser";
        private const string UtilType = "LazyOptimiser.Util", Switch = "_shouldOptimise", PlayerPrefsKey = "lazyoptimiser.shouldOptimise";

        // Its switch (a bool? that caches the PlayerPrefs value) in each loaded copy of its Util class, and what each held
        // before Orbiters turned it off; empty while Orbiters holds nothing.
        private static readonly List<(FieldInfo Field, object Value)> held = new List<(FieldInfo, object)>();

        private static IEnumerable<FieldInfo> Switches() =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(UtilType, false)).Where(t => t != null)
                .Select(t => t.GetField(Switch, BindingFlags.NonPublic | BindingFlags.Static))
                .Where(f => f != null && (f.FieldType == typeof(bool?) || f.FieldType == typeof(bool)));

        /// <summary>Whether Lazy Optimiser is loaded and set to optimise (its own setting).</summary>
        public static bool On => Switches().Any() && PlayerPrefs.GetInt(PlayerPrefsKey, 1) == 1;

        /// <summary>Avatars Orbiters builds: those holding a component of an Orbiters package (items, My Avatar, MCB).</summary>
        public static bool Holds(GameObject avatar) =>
            avatar != null && avatar.GetComponentsInChildren<Component>(true).Any(c => c != null && (c.GetType().Namespace ?? "").StartsWith("Orbiters.", StringComparison.Ordinal));

        /// <summary>Whether an upload of <paramref name="avatar"/> turns Lazy Optimiser off (it is on, and the avatar is Orbiters').</summary>
        public static bool TurnsOff(GameObject avatar) => On && Holds(avatar);

        /// <summary>Turns it off for this build when <see cref="TurnsOff"/>; what it held comes back with <see cref="Restore"/>.</summary>
        public static bool TurnOff(GameObject avatar)
        {
            if (held.Count > 0 || !Holds(avatar)) return false;
            foreach (var field in Switches())
            {
                held.Add((field, field.GetValue(null)));
                field.SetValue(null, field.FieldType == typeof(bool?) ? (object)(bool?)false : false);
            }
            return held.Count > 0;
        }

        /// <summary>Gives Lazy Optimiser back exactly what it held (an unread switch stays unread).</summary>
        public static void Restore()
        {
            foreach (var (field, value) in held) field.SetValue(null, value);
            held.Clear();
        }

        /// <summary>Whether Orbiters holds the switch now (between the two hooks of a build).</summary>
        public static bool Holding => held.Count > 0;
    }

    internal sealed class LazyOptimiserOffHook : BuildStep
    {
        // Lazy Optimiser's first pass is RemoveUnusedGameObjects at −100.
        public override BuildPhase Phase => BuildPhase.BeforeLazyOptimiser;
        public override bool Run(BuildContext context)
        {
            var avatarGameObject = context.Avatar;
            // A build stopped between the two hooks left it held: given back first.
            LazyOptimiser.Restore();
            if (LazyOptimiser.TurnOff(avatarGameObject))
                Debug.Log("[Orbiters] Lazy Optimiser is off for this upload of “" + avatarGameObject.name + "”: it would delete Orbiters items' parts that animations switch on through a component.");
            return true;
        }
    }

    internal sealed class LazyOptimiserBackHook : BuildStep
    {
        // After its last pass (MergeMeshes, −90).
        public override BuildPhase Phase => BuildPhase.AfterLazyOptimiser;
        public override bool Run(BuildContext context)
        {
            LazyOptimiser.Restore();
            return true;
        }
    }
}
