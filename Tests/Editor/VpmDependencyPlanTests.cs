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
            // The installed copy declares what its version in the listing declares.
            public IReadOnlyDictionary<string, string> InstalledDependencies(string id) =>
                installed.TryGetValue(id, out var v) ? available.FirstOrDefault(c => c.Id == id && c.Version == v)?.Dependencies ?? new Dictionary<string, string>() : new Dictionary<string, string>();
            public IEnumerable<string> InstalledPackages() => installed.Keys;
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

        // ---- Audit 2026-10-28 probes ----

        // VPM-01: a long chain is resolved to its end (it once stopped after 32 rounds and still said it could be applied).
        [Test] public void ALongChainIsResolvedCompletely()
        {
            var catalog = new Catalog();
            for (int i = 0; i < 40; i++)
                if (i < 39) catalog.Add("p" + i, "1.0.0", ("p" + (i + 1), "*")); else catalog.Add("p" + i, "1.0.0");
            var plan = Plan(catalog, ("p0", "*"));
            Assert.That(plan.CanApply, Is.True);
            Assert.That(plan.Changes.Count(), Is.EqualTo(40));
            Assert.That(plan.Steps.First().Id, Is.EqualTo("p39"), "the deepest dependency first");
        }

        // VPM-02: when the newest version leaves another package without a version, an older one is tried.
        [Test] public void AnOlderVersionIsChosenWhenTheNewestCannotWork()
        {
            var catalog = new Catalog().Add("A", "2.0.0", ("B", ">=2.0.0")).Add("A", "1.0.0", ("B", "<2.0.0")).Add("B", "2.0.0").Add("B", "1.0.0");
            var plan = Plan(catalog, ("A", "*"), ("B", "<2.0.0"));
            Assert.That(plan.CanApply, Is.True, string.Join("; ", plan.Steps.Select(s => s.Id + " " + s.Action + " " + s.Reason)));
            Assert.That(plan.Changes.Select(s => s.Id + " " + s.To), Is.EqualTo(new[] { "B 1.0.0", "A 1.0.0" }));
        }

        // VPM-03: steps come dependencies first, whatever order the requirements came in.
        [Test] public void DependenciesComeBeforeWhatNeedsThem()
        {
            var catalog = new Catalog().Add("A", "1.0.0", ("B", "*")).Add("B", "1.0.0");
            Assert.That(Plan(catalog, ("B", "*"), ("A", "*")).Steps.Select(s => s.Id), Is.EqualTo(new[] { "B", "A" }));
        }

        // VPM-04: a package the project keeps still brings what it needs and the project lacks.
        [Test] public void AKeptPackagesMissingDependenciesAreInstalled()
        {
            var catalog = new Catalog().Add("A", "1.0.0", ("B", ">=1.0.0")).Add("B", "1.0.0");
            catalog.installed["A"] = "1.0.0";
            var plan = Plan(catalog, ("A", "*"));
            Assert.That(plan.NothingToDo, Is.False);
            Assert.That(plan.Steps.Select(s => s.Id + " " + s.Action), Is.EqualTo(new[] { "B Install", "A Keep" }));
        }

        // VPM-05: what the project's other packages ask of a package counts before it is changed, and the plan says who.
        [Test] public void OtherInstalledPackagesRangesCount()
        {
            var catalog = new Catalog().Add("Tool", "1.0.0", ("Shared", "<2.0.0")).Add("Shared", "1.5.0").Add("Shared", "2.1.0");
            catalog.installed["Tool"] = "1.0.0";
            catalog.installed["Shared"] = "1.5.0";
            var plan = Plan(catalog, ("Shared", ">=2.0.0"));
            var shared = plan.Steps.Single(s => s.Id == "Shared");
            Assert.That(plan.CanApply, Is.False);
            Assert.That(shared.Action, Is.EqualTo(VpmStepAction.Blocked));
            Assert.That(shared.RequiredBy, Has.Some.Contains("Tool 1.0.0 (installed)"));
            Assert.That(Plan(catalog, ("Shared", ">=1.0.0")).NothingToDo, Is.True, "what is kept stays as it is");
        }

        // A search without an answer ends quickly and says which package could not be chosen.
        [Test] public void AnImpossibleWideSearchEnds()
        {
            var catalog = new Catalog();
            for (int i = 0; i < 12; i++) for (int v = 1; v <= 6; v++) catalog.Add("w" + i, v + ".0.0", ("z", "<1.0.0"));
            catalog.Add("z", "1.0.0");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var plan = Plan(catalog, Enumerable.Range(0, 12).Select(i => ("w" + i, "*")).ToArray());
            Assert.That(plan.CanApply, Is.False);
            Assert.That(plan.Steps.Single(s => s.Id == "z").Action, Is.EqualTo(VpmStepAction.Blocked));
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(5000));
        }
    }
}
#endif
