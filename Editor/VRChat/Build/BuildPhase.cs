using System;

namespace Orbiters.Toolkit.Editor.VRChat.Build
{
    /// <summary>
    /// Where Orbiters' avatar build steps run among the VRChat SDK's build callbacks: each phase is one SDK callback at a
    /// fixed order (<see cref="BuildPhases.Order"/>), and runs its steps in the order they declare. The orders sit between
    /// those of the other tools' callbacks that a step must come before or after (table in <see cref="BuildPhases"/>):
    /// moving one changes what a step sees, and nothing but the order test checks it.
    /// </summary>
    public enum BuildPhase
    {
        /// <summary>Before every avatar tool: Modular Avatar's NDMF (−11000), My Avatar's menu capture (−11010).</summary>
        Start,
        /// <summary>
        /// Just before VRCFury builds the avatar (−10000): after N4N0 and ReFit prepared theirs (−10200 to −10105, MCB's
        /// captures among them); before My Avatar's Novabeast options (−10060) and face tracking capture (−10050).
        /// </summary>
        BeforeVrcFury,
        /// <summary>Right after VRCFury's build (−10000), before every step that changes the controllers it built (MCB's survey at −9500).</summary>
        AfterVrcFury,
        /// <summary>
        /// Accessories placed for good: after MCB's corrective links (−9000) and mode locks (−8970); before ReFit links
        /// the refitted clothing's shapes (−8960) and shrinks the body under it (−8956).
        /// </summary>
        Accessories,
        /// <summary>After ReFit's links and shrinks (−8956), before MCB applies its versions (−8800): accessories follow the body's shapes.</summary>
        AccessoryShapes,
        /// <summary>
        /// The avatar's structure is final: after MCB's versions (−8800), just before My Avatar's upload optimizer (−1100,
        /// whose step belongs here), Modular Avatar's optimizing phase (−1025) and VRCFury's removal of EditorOnly objects
        /// (−1024, which VRCFury moves there from the SDK's own).
        /// </summary>
        Structure,
        /// <summary>Just before Lazy Optimiser's first pass (−100).</summary>
        BeforeLazyOptimiser,
        /// <summary>Just after Lazy Optimiser's last pass (−90).</summary>
        AfterLazyOptimiser,
        /// <summary>After every avatar tool, Poiyomi's material locks (4, 100) and My Avatar's materials (9000): the build data is written.</summary>
        SaveBuildData,
        /// <summary>Last, with VRCFury's last steps (its parameter compression at int.MaxValue − 100 comes before).</summary>
        Last,
    }

    public static class BuildPhases
    {
        /// <summary>
        /// The SDK callback order each phase runs at. Today's other build callbacks, for reference (all fixed numbers):
        /// Mixer −20001; My Avatar −11010; NDMF −11000; N4N0 −10200 (and MCB's native meshes), −10150, −10140; ReFit −10110,
        /// −10105 (and MCB's version capture); My Avatar −10060, −10050; VRCFury −10000; MCB −9500; My Avatar −9010, −9005;
        /// MCB −9000, −8970; ReFit −8960, −8956; MCB −8800; My Avatar −1100; NDMF −1025; VRCFury −1024; Lazy Optimiser
        /// −100 to −90; Poiyomi 4, 100; My Avatar 9000; VRCFury int.MaxValue − 100 and int.MaxValue.
        /// </summary>
        public static int Order(BuildPhase phase)
        {
            switch (phase)
            {
                case BuildPhase.Start: return -20000;
                case BuildPhase.BeforeVrcFury: return -10100;
                case BuildPhase.AfterVrcFury: return -9999;
                case BuildPhase.Accessories: return -8961;
                case BuildPhase.AccessoryShapes: return -8950;
                case BuildPhase.Structure: return -1101;
                case BuildPhase.BeforeLazyOptimiser: return -101;
                case BuildPhase.AfterLazyOptimiser: return -89;
                case BuildPhase.SaveBuildData: return 10000;
                case BuildPhase.Last: return int.MaxValue;
                default: throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
            }
        }

        /// <summary>Every phase, in the order they run.</summary>
        public static readonly BuildPhase[] All = (BuildPhase[])Enum.GetValues(typeof(BuildPhase));
    }
}
