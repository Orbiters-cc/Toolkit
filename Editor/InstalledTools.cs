using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Orbiters.Toolkit.Editor
{
    /// <summary>
    /// The avatar tools the project has, by the packages installed (Unity's registered packages, the Creator Companion's
    /// in Packages/ included), not by the types loaded. VRCFury Stub (net.nrzk.vfstub) ships VRCFury's component types and
    /// GUIDs without VRCFury's build: a type lookup takes it for VRCFury, and toggles or armature links set up for VRCFury
    /// then never build. Read once, and again after the project's packages change. Calling into a tool still looks its
    /// types up (<c>VrcFury.Find</c>); this says whether the tool is there to build what it holds.
    /// </summary>
    public static class InstalledTools
    {
        /// <summary>An installed package: its id, the name it shows, its version and where it comes from (Embedded, Local…).</summary>
        public sealed class Package
        {
            public string Id, Name, Version, Source;
            public override string ToString() => Name + " " + Version;
        }

        public const string VrcFuryId = "com.vrcfury.vrcfury", VrcFuryStubId = "net.nrzk.vfstub", ModularAvatarId = "nadena.dev.modular-avatar",
            NdmfId = "nadena.dev.ndmf", AaoId = "com.anatawa12.avatar-optimizer", D4rkId = "d4rkpl4y3r.d4rkavataroptimizer",
            TexTransToolId = "net.rs64.tex-trans-tool";

        /// <summary>Meshia and its forks, each under an id of its own (the first is the original).</summary>
        public static readonly IReadOnlyList<string> MeshiaIds = new[]
        {
            "com.ramtype0.meshia.mesh-simplification", "io.github.drcain1.meshia.mesh-simplification", "com.synthos.meshia",
            "net.kaikoga.meshia.mesh-simplification.ablet",
        };

        /// <summary>VRCFury, and not VRCFury Stub: the stub keeps VRCFury's components without building them (the two can't be installed together).</summary>
        public static bool VrcFury => Has(VrcFuryId) && !Has(VrcFuryStubId);
        /// <summary>VRCFury Stub: VRCFury's components are there, but nothing builds them.</summary>
        public static bool VrcFuryStub => Has(VrcFuryStubId);
        public static bool ModularAvatar => Has(ModularAvatarId);
        public static bool Ndmf => Has(NdmfId);
        public static bool Aao => Has(AaoId);
        public static bool D4rk => Has(D4rkId);
        public static bool TexTransTool => Has(TexTransToolId);
        /// <summary>The installed Meshia (or fork), or null.</summary>
        public static Package Meshia => MeshiaIds.Select(Find).FirstOrDefault(p => p != null);

        public static bool Has(string id) => Find(id) != null;

        /// <summary>The installed package with this id, or null.</summary>
        public static Package Find(string id) => !string.IsNullOrEmpty(id) && Packages().TryGetValue(id, out var package) ? package : null;

        /// <summary>Every installed package, by id.</summary>
        public static IReadOnlyCollection<Package> All => Packages().Values;

        private static Dictionary<string, Package> cached;
        private static Dictionary<string, Package> pretended;

        static InstalledTools() => Events.registeredPackages += _ => cached = null;

        private static Dictionary<string, Package> Packages()
        {
            if (pretended != null) return pretended;
            if (cached != null) return cached;
            var found = new Dictionary<string, Package>(StringComparer.Ordinal);
            foreach (var info in PackageInfo.GetAllRegisteredPackages())
                if (info != null && !string.IsNullOrEmpty(info.name))
                    found[info.name] = new Package
                    {
                        Id = info.name, Name = string.IsNullOrEmpty(info.displayName) ? info.name : info.displayName, Version = info.version,
                        Source = info.source.ToString(),
                    };
            // Before the Package Manager has listed the project's packages (very early in a reload) nothing is kept: asked again later.
            if (found.Count > 0) cached = found;
            return found;
        }

        /// <summary>
        /// Answers from these packages (id, version) instead of the project's until disposed: tests decide each case without
        /// installing anything.
        /// </summary>
        public static IDisposable Pretend(params (string Id, string Version)[] installed)
        {
            var outer = pretended;
            pretended = installed.ToDictionary(p => p.Id, p => new Package { Id = p.Id, Name = p.Id, Version = p.Version, Source = "Pretended" }, StringComparer.Ordinal);
            return new Scope(() => pretended = outer);
        }

        private sealed class Scope : IDisposable
        {
            private Action end;
            public Scope(Action end) => this.end = end;
            public void Dispose() { end?.Invoke(); end = null; }
        }
    }
}
