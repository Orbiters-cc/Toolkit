using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using static Orbiters.Toolkit.Editor.VRChat.HeldPropRig;

namespace Orbiters.Toolkit.Editor.VRChat
{
    /// <summary>
    /// N4N0's controller input (Orbiters contracts' Docs/orb-input.md): each hand's touchpad and thumbstick, which VRChat
    /// gives avatars none of, as N4N0's local parameters. Orbiters - N4N0 reads them through SteamVR and sets them over
    /// OSC on an avatar that lists them; N4N0 itself uses none: they are there for the avatar's own animators, which read
    /// them by adding parameters of the same names. Local, never saved, at rest by default: they cost no synced bits (an
    /// avatar copies one into a synced parameter of its own, from a state only its wearer runs, to show it to others).
    /// PC only: phones have no SteamVR.
    /// </summary>
    public static partial class OrbInstaller
    {
        /// <summary>Where the thumb is on each hand's touchpad (−1 to 1 each way, 0 untouched), whether it rests there or
        /// presses, and how hard (0 to 1): <c>N4N0/Touchpad/Left/X</c> … <c>N4N0/Touchpad/Right/Force</c>, left hand first.</summary>
        public static readonly IReadOnlyList<string> TouchpadParameters = new[]
        {
            "N4N0/Touchpad/Left/X", "N4N0/Touchpad/Left/Y", "N4N0/Touchpad/Left/Touch", "N4N0/Touchpad/Left/Click", "N4N0/Touchpad/Left/Force",
            "N4N0/Touchpad/Right/X", "N4N0/Touchpad/Right/Y", "N4N0/Touchpad/Right/Touch", "N4N0/Touchpad/Right/Click", "N4N0/Touchpad/Right/Force",
        };

        /// <summary>Each hand's thumbstick (−1 to 1 each way), whether a thumb rests on it and whether it is pressed in:
        /// <c>N4N0/Stick/Left/X</c> … <c>N4N0/Stick/Right/Click</c>.</summary>
        public static readonly IReadOnlyList<string> StickParameters = new[]
        {
            "N4N0/Stick/Left/X", "N4N0/Stick/Left/Y", "N4N0/Stick/Left/Touch", "N4N0/Stick/Left/Click",
            "N4N0/Stick/Right/X", "N4N0/Stick/Right/Y", "N4N0/Stick/Right/Touch", "N4N0/Stick/Right/Click",
        };

        /// <summary>Every controller input parameter (contracts' <c>orb::input::all()</c>, in its order).</summary>
        public static IEnumerable<string> InputParameters => TouchpadParameters.Concat(StickParameters);

        /// <summary>Touch and Click are Bools, the rest Floats.</summary>
        public static bool InputIsBool(string name) => name.EndsWith("/Touch") || name.EndsWith("/Click");

        /// <summary>Adds the controller input parameters to N4N0's expression parameters: local, never saved, at rest.</summary>
        private static void InputParametersOf(List<VRCExpressionParameters.Parameter> parameters)
        {
            foreach (string name in InputParameters)
                parameters.Add(Parameter(name, synced: false, type: InputIsBool(name) ? VRCExpressionParameters.ValueType.Bool : VRCExpressionParameters.ValueType.Float));
        }
    }
}
