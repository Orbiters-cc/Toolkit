#if ORBITERS_VPM
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Vpm;

namespace Orbiters.Toolkit.Editor.Tests
{
    // The dependency plan a gallery asset shows before anything changes: resolved together, transitively, without
    // touching what already fits or the VRChat SDK.
    public sealed class VpmDependencyPlanTests
    {
        private sealed class Catalog : IVpmCatalog
        {
            public readonly Dictionary<string, string> installed = new Dictionary<string, string>();
            public readonly List<VpmCandidate> available = new List<VpmCandidate>();
            public string Installed(string id) => installed.TryGetValue(id, out var v) ? v : null;
            public bool Developed(string id) => false;
            public IEnumerable<VpmCandidate> Versions(string id) => VpmCandidates.Newest(available.Where(c => c.Id == id));
            public string DisplayName(string id) => id;
            public Catalog Add(string id, string version, params (string id, string range)[] deps)
            {
                available.Add(new VpmCandidate { Id = id, Version = version, RepositoryUrl = "https://repo/" + id, Dependencies = deps.ToDictionary(d => d.id, d => d.range) });
                return this;
            }
        }

        private static VpmDependencyPlan Plan(Catalog catalog, params (string id, string range)[] requirements) =>
            VpmDependencyPlan.Resolve(requirements.Select(r => new VpmDependencyPlan.Requirement(r.id, r.range, "Hat")), catalog);

        [Test] public void InstallsWhatIsMissingWithItsDependenciesFirst()
        {
            var catalog = new Catalog().Add("nadena.dev.modular-avatar", "1.12.5", ("nadena.dev.ndmf", ">=1.5.0")).Add("nadena.dev.ndmf", "1.5.2").Add("nadena.dev.ndmf", "1.4.0");
            var plan = Plan(catalog, ("nadena.dev.modular-avatar", ">=1.10.0"));
            Assert.That(plan.CanApply, Is.True);
            Assert.That(plan.Changes.Select(s => s.Id + " " + s.To), Is.EqualTo(new[] { "nadena.dev.ndmf 1.5.2", "nadena.dev.modular-avatar 1.12.5" }));
            Assert.That(plan.Repositories, Is.EquivalentTo(new[] { "https://repo/nadena.dev.modular-avatar", "https://repo/nadena.dev.ndmf" }));
        }

        [Test] public void KeepsInstalledPackagesThatFitAndShowsRequiredUpdates()
        {
            var catalog = new Catalog().Add("com.vrcfury.vrcfury", "1.1334.0").Add("com.vrcfury.vrcfury", "1.1200.0");
            catalog.installed["com.vrcfury.vrcfury"] = "1.1200.0";
            Assert.That(Plan(catalog, ("com.vrcfury.vrcfury", ">=1.1000.0")).NothingToDo, Is.True, "An installed version that fits is never updated.");
            var update = Plan(catalog, ("com.vrcfury.vrcfury", ">=1.1300.0"));
            var step = update.Steps.Single();
            Assert.That(step.Action, Is.EqualTo(VpmStepAction.Upgrade));
            Assert.That(step.From + " → " + step.To, Is.EqualTo("1.1200.0 → 1.1334.0"));
        }

        [Test] public void NeverChangesTheVrchatSdkAndReportsConflicts()
        {
            var catalog = new Catalog().Add("com.example.tool", "2.0.0", ("com.vrchat.avatars", ">=3.11.0")).Add("com.vrchat.avatars", "3.11.2");
            catalog.installed["com.vrchat.avatars"] = "3.10.3";
            var plan = Plan(catalog, ("com.example.tool", "*"));
            Assert.That(plan.CanApply, Is.False);
            Assert.That(plan.Steps.Single(s => s.Id == "com.vrchat.avatars").Action, Is.EqualTo(VpmStepAction.Blocked));

            var conflict = new Catalog().Add("a", "1.0.0", ("shared", "<2.0.0")).Add("b", "1.0.0", ("shared", ">=2.0.0")).Add("shared", "1.5.0").Add("shared", "2.1.0");
            var both = Plan(conflict, ("a", "*"), ("b", "*"));
            Assert.That(both.Steps.Single(s => s.Id == "shared").Action, Is.EqualTo(VpmStepAction.Blocked), "No version fits both: nothing is installed.");
            Assert.That(Plan(new Catalog(), ("missing.package", "*")).Steps.Single().Action, Is.EqualTo(VpmStepAction.Missing));
        }
    }
}
#endif
