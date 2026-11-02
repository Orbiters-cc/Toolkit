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
            public readonly HashSet<string> developed = new HashSet<string>();
            public string Installed(string id) => installed.TryGetValue(id, out var v) ? v : null;
            public bool Developed(string id) => developed.Contains(id);
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

        // ---- Plans the user approves: the VRChat SDK and the tools pinning it ----

        // The N4N0 sample avatar project (VRChat SDK 3.7.5, three tools pinning it below 3.8) and what its VPM
        // repositories offer (the official and curated listings and hai-vr's, as read on 2026-11-02).
        private static Catalog SampleProject(bool haiVrListing = true)
        {
            var catalog = new Catalog();
            foreach (string v in new[] { "3.7.0", "3.7.5", "3.7.6", "3.8.0", "3.8.1", "3.8.2", "3.9.0", "3.10.0", "3.10.1", "3.10.2", "3.10.3", "3.10.4", "3.10.5", "3.10.6", "3.10.6-beta.4" })
            {
                catalog.Add("com.vrchat.base", v);
                catalog.Add("com.vrchat.avatars", v, ("com.vrchat.base", v));
            }
            catalog.Add("vrchat.blackstartx.gesture-manager", "3.9.3", ("com.vrchat.avatars", ">=3.5.2 <3.8.X"))
                .Add("vrchat.blackstartx.gesture-manager", "3.9.4", ("com.vrchat.avatars", ">=3.5.2 < 3.9.X"))
                .Add("vrchat.blackstartx.gesture-manager", "3.9.5", ("com.vrchat.avatars", ">=3.5.2 < 3.10.X"))
                .Add("vrchat.blackstartx.gesture-manager", "3.9.8", ("com.vrchat.avatars", ">=3.5.2 < 3.11.X"))
                .Add("vrchat.blackstartx.gesture-manager", "3.9.9", ("com.vrchat.avatars", ">=3.10.4 < 3.11.X"));
            catalog.Add("dev.vrlabs.av3manager", "2.0.34", ("com.vrchat.avatars", "3.2 - 3.7"))
                .Add("dev.vrlabs.av3manager", "2.0.35", ("com.vrchat.avatars", "3.2 - 3.8"))
                .Add("dev.vrlabs.av3manager", "2.1.1", ("com.vrchat.avatars", "3.2 - 3.9"))
                .Add("dev.vrlabs.av3manager", "2.1.2", ("com.vrchat.avatars", "3.2 - 3.10"));
            catalog.Add("dev.hai-vr.fuck-osc-config", "1.1.4", ("com.vrchat.avatars", ">=3.1.0 <3.7.0-a"))
                .Add("dev.hai-vr.fuck-osc-config", "1.1.5", ("com.vrchat.avatars", ">=3.1.0 <3.8.0-a"));
            if (haiVrListing) catalog.Add("dev.hai-vr.fuck-osc-config", "1.1.6", ("com.vrchat.avatars", ">=3.1.0 <999.0.0-a"));
            catalog.Add("com.vrcfury.vrcfury", "1.1434.0").Add("com.vrcfury.vrcfury", "1.1436.0").Add("com.vrcfury.vrcfury", "1.1436.1-beta.6");
            catalog.Add("orbiters.toolkit", "0.4.11")
                .Add("orbiters.myavatar", "0.9.19", ("com.vrchat.avatars", ">=3.7.0 <4.0.0"), ("orbiters.toolkit", ">=0.4.11 <0.5.0"))
                .Add("orbiters.mixer", "0.2.4", ("orbiters.toolkit", ">=0.4.11 <0.5.0"))
                .Add("de.thryrallo.vrc.avatar-performance-tools", "1.3.7").Add("d4rkpl4y3r.d4rkavataroptimizer", "3.8.0").Add("com.vrchat.core.vpm-resolver", "0.1.29");
            foreach (var (id, v) in new[] { ("com.vrchat.base", "3.7.5"), ("com.vrchat.avatars", "3.7.5"), ("vrchat.blackstartx.gesture-manager", "3.9.3"), ("dev.vrlabs.av3manager", "2.0.34"),
                         ("dev.hai-vr.fuck-osc-config", "1.1.5"), ("orbiters.toolkit", "0.4.11"), ("orbiters.myavatar", "0.9.19"), ("orbiters.mixer", "0.2.4"),
                         ("de.thryrallo.vrc.avatar-performance-tools", "1.3.7"), ("d4rkpl4y3r.d4rkavataroptimizer", "3.8.0"), ("com.vrchat.core.vpm-resolver", "0.1.29") })
                catalog.installed[id] = v;
            return catalog;
        }

        // What N4N0 1.0.19 declares in the gallery.
        private static readonly (string id, string range)[] N4N0 =
            { ("com.vrchat.base", ">=3.10.3"), ("com.vrchat.avatars", ">=3.10.3"), ("com.vrcfury.vrcfury", ">=1.1434.0"), ("orbiters.toolkit", ">=0.4.10") };

        private static VpmDependencyPlan Approved(Catalog catalog, params (string id, string range)[] requirements) =>
            VpmDependencyPlan.ResolveForApproval(requirements.Select(r => new VpmDependencyPlan.Requirement(r.id, r.range, "N4N0")), catalog);

        private static string Changes(VpmDependencyPlan plan) =>
            string.Join("; ", plan.Changes.Select(s => $"{s.Action} {s.Id} {s.From}->{s.To}")) + " | " + string.Join("; ", plan.Steps.Where(s => !s.Changes && s.Action != VpmStepAction.Keep).Select(s => s.Id + ": " + s.Reason));

        [Test] public void TheSampleProjectUpdatesTheSdkAndTheToolsPinningItForN4N0()
        {
            var catalog = SampleProject();
            Assert.That(Plan(catalog, N4N0).CanApply, Is.False, "Without approval the VRChat SDK is never changed.");
            var plan = Approved(catalog, N4N0);
            Assert.That(plan.CanApply, Is.True, Changes(plan));
            Assert.That(plan.NeedsApproval, Is.True);
            var changes = plan.Changes.ToDictionary(s => s.Id, s => s);
            Assert.That(changes.Keys, Is.EquivalentTo(new[] { "com.vrchat.base", "com.vrchat.avatars", "vrchat.blackstartx.gesture-manager", "dev.vrlabs.av3manager", "dev.hai-vr.fuck-osc-config", "com.vrcfury.vrcfury" }), Changes(plan));
            Assert.That(changes["com.vrchat.avatars"].To, Is.EqualTo("3.10.6"), "The newest release, not a beta.");
            Assert.That(changes["com.vrchat.base"].To, Is.EqualTo("3.10.6"));
            Assert.That(changes["vrchat.blackstartx.gesture-manager"].To, Is.EqualTo("3.9.9"));
            Assert.That(changes["dev.vrlabs.av3manager"].To, Is.EqualTo("2.1.2"));
            Assert.That(changes["dev.hai-vr.fuck-osc-config"].To, Is.EqualTo("1.1.6"));
            Assert.That(changes["com.vrcfury.vrcfury"].Action, Is.EqualTo(VpmStepAction.Install));
            Assert.That(changes["com.vrcfury.vrcfury"].To, Is.EqualTo("1.1436.0"), "A release, not a beta.");
            Assert.That(changes.Values.Where(s => s.Approval).Select(s => s.Id), Is.EquivalentTo(new[] { "com.vrchat.base", "com.vrchat.avatars", "vrchat.blackstartx.gesture-manager", "dev.vrlabs.av3manager", "dev.hai-vr.fuck-osc-config" }));
            var gesture = changes["vrchat.blackstartx.gesture-manager"];
            Assert.That(gesture.Limiting, Is.True);
            Assert.That(gesture.Reason, Is.EqualTo("3.9.3 works only with com.vrchat.avatars >=3.5.2 <3.8.X; 3.9.9 works with com.vrchat.avatars 3.10.6."));
            Assert.That(plan.Steps.Single(s => s.Id == "orbiters.toolkit").Action, Is.EqualTo(VpmStepAction.Keep));
            Assert.That(plan.Steps.Any(s => s.Id.StartsWith("orbiters.myavatar") || s.Id.StartsWith("de.thryrallo")), Is.False, "Tools that allow the new SDK are not touched.");
            // Applied, the project is complete: planning again from the plan's versions changes nothing.
            foreach (var step in plan.Changes) catalog.installed[step.Id] = step.To;
            Assert.That(Approved(catalog, N4N0).NothingToDo, Is.True);
        }

        [Test] public void AnSdkVersionTheInstalledToolsAllowComesFirst()
        {
            // An asset needing only 3.7.6 (local-only contact senders): the SDK moves to 3.7.6 and every tool stays.
            var plan = Approved(SampleProject(), ("com.vrchat.base", ">=3.7.6"), ("com.vrchat.avatars", ">=3.7.6"));
            Assert.That(plan.CanApply, Is.True, Changes(plan));
            Assert.That(plan.Changes.Select(s => s.Id + " " + s.To), Is.EquivalentTo(new[] { "com.vrchat.base 3.7.6", "com.vrchat.avatars 3.7.6" }));
            Assert.That(plan.Scope, Is.EqualTo(VpmPlanScope.Sdk));
        }

        [Test] public void AToolWithoutAWorkingVersionIsRemovedOnlyAsALastResort()
        {
            var plan = Approved(SampleProject(haiVrListing: false), N4N0);
            Assert.That(plan.CanApply, Is.True, Changes(plan));
            var removed = plan.Changes.Single(s => s.Action == VpmStepAction.Remove);
            Assert.That(removed.Id, Is.EqualTo("dev.hai-vr.fuck-osc-config"));
            Assert.That(removed.Approval && removed.Limiting, Is.True);
            Assert.That(removed.Reason, Does.StartWith("1.1.5 works only with com.vrchat.avatars >=3.1.0 <3.8.0-a, and none of its versions works with com.vrchat.avatars 3.10.6."));
            Assert.That(plan.Changes.Count(s => s.Action == VpmStepAction.Remove), Is.EqualTo(1), "The tools with a working version are updated, not removed.");
            Assert.That(VpmDependencyPlan.Describe(removed), Is.EqualTo("Remove dev.hai-vr.fuck-osc-config 1.1.5"));
        }

        [Test] public void AToolThatCannotChangeBlocksAndThePlanSaysWhich()
        {
            var catalog = SampleProject(haiVrListing: false);
            // Developed in the project: never changed, never removed.
            catalog.developed.Add("dev.hai-vr.fuck-osc-config");
            var plan = Approved(catalog, N4N0);
            Assert.That(plan.CanApply, Is.False);
            var blocker = plan.Blocker;
            Assert.That(blocker, Is.Not.Null);
            Assert.That(blocker.Id, Is.EqualTo("com.vrchat.base").Or.EqualTo("com.vrchat.avatars"), Changes(plan));
            Assert.That(blocker.Reason + string.Join(" ", blocker.RequiredBy), Does.Contain("dev.hai-vr.fuck-osc-config"), "It names the package holding the SDK back.");
        }

        [Test] public void AnInstalledToolIsDowngradedWhenOnlyAnOlderVersionWorks()
        {
            // Its newest version went back to sdk 1: only the older 2.0.0 works with sdk 2.
            var catalog = new Catalog().Add("sdk", "1.0.0").Add("sdk", "2.0.0").Add("tool", "1.0.0", ("sdk", "1.x")).Add("tool", "2.0.0", ("sdk", "2.x")).Add("tool", "3.0.0", ("sdk", "1.x"));
            catalog.installed["sdk"] = "1.0.0";
            catalog.installed["tool"] = "3.0.0";
            var plan = VpmDependencyPlan.Resolve(new[] { new VpmDependencyPlan.Requirement("sdk", ">=2.0.0", "Hat") }, catalog, VpmPlanScope.InstalledTools);
            Assert.That(plan.CanApply, Is.True, Changes(plan));
            var tool = plan.Changes.Single(s => s.Id == "tool");
            Assert.That(tool.Action, Is.EqualTo(VpmStepAction.Downgrade));
            Assert.That(VpmDependencyPlan.Describe(tool), Is.EqualTo("Downgrade tool 3.0.0 → 2.0.0"));
        }

        [Test] public void TheRepositoryHistoryFindsWhereAnInstalledToolCameFrom()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpm-history-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "dev.hai-vr.vpm-e7471996.json"),
                    "{\"repo\":{\"url\":\"https://hai-vr.github.io/vpm-listing/index.json\",\"packages\":{\"dev.hai-vr.fuck-osc-config\":{\"versions\":{}}}}}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "plain.json"),
                    "{\"url\":\"https://example.org/index.json\",\"packages\":{\"other\":{\"versions\":{}}}}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "insecure.json"),
                    "{\"url\":\"http://example.org/index.json\",\"packages\":{\"dev.hai-vr.fuck-osc-config\":{}}}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "broken.json"), "{ not json");
                var found = VpmRepositoryHistory.Offering(new[] { "dev.hai-vr.fuck-osc-config", "missing" }, folder);
                Assert.That(found.Keys, Is.EquivalentTo(new[] { "dev.hai-vr.fuck-osc-config" }));
                Assert.That(found["dev.hai-vr.fuck-osc-config"], Is.EqualTo(new[] { "https://hai-vr.github.io/vpm-listing/index.json" }), "https only");
            }
            finally { System.IO.Directory.Delete(folder, true); }
        }

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
