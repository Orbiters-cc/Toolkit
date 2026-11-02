using System;
using System.Collections.Generic;
using System.Linq;
using SemanticRange = SemanticVersioning.Range;

namespace Orbiters.Toolkit.Editor.Vpm
{
    /// <summary>One package a dependency source offers: its versions newest first, each with its own VPM dependencies.</summary>
    public sealed class VpmCandidate
    {
        public string Id, Version, DisplayName, RepositoryUrl;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the planner reads: what the project has, and what may be installed (Orbiters' known VPM packages first).</summary>
    public interface IVpmCatalog
    {
        /// <summary>The installed version, or null.</summary>
        string Installed(string id);
        /// <summary>A package developed in this project (a git clone in Packages/): never touched.</summary>
        bool Developed(string id);
        /// <summary>Versions that may be installed, newest first.</summary>
        IEnumerable<VpmCandidate> Versions(string id);
        string DisplayName(string id);
        /// <summary>The VPM dependencies (id to range) of the installed or developed copy; empty when there is none.</summary>
        IReadOnlyDictionary<string, string> InstalledDependencies(string id);
        /// <summary>Every package the project has (installed or developed): what they ask of others counts too.</summary>
        IEnumerable<string> InstalledPackages();
    }

    public enum VpmStepAction { Keep, Install, Upgrade, Blocked, Missing, Downgrade, Remove }

    /// <summary>
    /// What a plan may change besides the packages asked for. <see cref="Requested"/>: what is asked for and its dependencies,
    /// never the VRChat SDK nor a package nothing asked for. The others widen it; every change they allow is a step the user
    /// approves in the plan before anything changes (<see cref="VpmPlanStep.Approval"/>).
    /// </summary>
    [Flags]
    public enum VpmPlanScope
    {
        Requested = 0,
        /// <summary>The VRChat SDK's packages (com.vrchat.*) may be updated or downgraded.</summary>
        Sdk = 1,
        /// <summary>An installed tool that does not allow a version the plan chooses may be updated or downgraded to one that does.</summary>
        InstalledTools = 2,
        /// <summary>Last resort: such a tool is removed when none of its versions works.</summary>
        Removal = 4,
    }

    public sealed class VpmPlanStep
    {
        public string Id, DisplayName, From, To, RepositoryUrl, Reason;
        public VpmStepAction Action;
        /// <summary>The packages (or the asset) asking for it, with their ranges: "Hat (>=1.1000.0)".</summary>
        public List<string> RequiredBy = new List<string>();
        /// <summary>
        /// A change only an approved plan makes: the VRChat SDK, a tool nothing asked for (it limits a package the plan
        /// changes), or a removal.
        /// </summary>
        public bool Approval;
        /// <summary>
        /// An installed tool nothing asked for, changed (or removed, or blocking) because it does not allow a version the
        /// plan chooses for another package; <see cref="Reason"/> says which.
        /// </summary>
        public bool Limiting;
        public bool Changes => Action == VpmStepAction.Install || Action == VpmStepAction.Upgrade || Action == VpmStepAction.Downgrade || Action == VpmStepAction.Remove;
    }

    /// <summary>
    /// Every package change needed to satisfy some requirements, resolved together before anything changes: transitive
    /// dependencies (of kept packages too), the ranges several packages put on the same one, what the project already has
    /// and what its other packages ask of it. Installed packages that already fit stay as they are; one that must change is
    /// shown as an upgrade. The VRChat SDK and tools nothing asked for change only in a wider <see cref="VpmPlanScope"/>,
    /// as steps the user approves (<see cref="ResolveForApproval"/>).
    /// </summary>
    public sealed class VpmDependencyPlan
    {
        public readonly List<VpmPlanStep> Steps = new List<VpmPlanStep>();
        /// <summary>What it was allowed to change.</summary>
        public VpmPlanScope Scope;
        public IEnumerable<VpmPlanStep> Changes => Steps.Where(s => s.Changes);
        public bool CanApply => Steps.All(s => s.Action != VpmStepAction.Blocked && s.Action != VpmStepAction.Missing);
        public bool NothingToDo => CanApply && !Changes.Any();
        public IEnumerable<string> Repositories => Changes.Select(s => s.RepositoryUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase);
        /// <summary>It changes the VRChat SDK or a tool nothing asked for, or removes one: the user approves the plan first.</summary>
        public bool NeedsApproval => Changes.Any(s => s.Approval);
        /// <summary>The step that keeps the plan from being applied, or null.</summary>
        public VpmPlanStep Blocker => Steps.FirstOrDefault(s => s.Action == VpmStepAction.Blocked || s.Action == VpmStepAction.Missing);

        /// <summary>Packages Orbiters never installs or upgrades unless the user approves the plan.</summary>
        public static bool Protected(string id) => id != null && (id.StartsWith("com.vrchat.", StringComparison.OrdinalIgnoreCase));

        public sealed class Requirement
        {
            public string Id, Range, RequiredBy;
            /// <summary>The package whose chosen version asks for this one; null for the original requirements.</summary>
            public string Source;
            public Requirement(string id, string range, string requiredBy, string source = null)
            { Id = id; Range = string.IsNullOrWhiteSpace(range) ? "*" : range; RequiredBy = requiredBy; Source = source; }
        }

        /// <summary>Version choices tried at most before a plan gives up (every package's versions, together).</summary>
        public const int MaxAttempts = 20000;

        /// <summary>
        /// Resolves the requirements together: each package needed (asked for, or needed by a version chosen) gets one version
        /// that fits every range on it, trying older versions where the newest leaves another package without one, and the
        /// dependencies of the packages the project keeps are followed too. A package the plan installs or updates also has to
        /// fit what the project's other installed packages ask of it, unless <paramref name="scope"/> lets the plan change
        /// those too. Steps come dependencies first. When no choice works, the plan cannot be applied and the package that
        /// could not be chosen says why.
        /// </summary>
        public static VpmDependencyPlan Resolve(IEnumerable<Requirement> requirements, IVpmCatalog catalog, VpmPlanScope scope = VpmPlanScope.Requested) =>
            new Solver((requirements ?? Enumerable.Empty<Requirement>()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id)).ToList(), catalog, scope).Run();

        /// <summary>The scopes <see cref="ResolveForApproval"/> widens through, the least disruptive first.</summary>
        public static readonly VpmPlanScope[] ApprovalScopes =
        {
            VpmPlanScope.Requested, VpmPlanScope.Sdk, VpmPlanScope.Sdk | VpmPlanScope.InstalledTools,
            VpmPlanScope.Sdk | VpmPlanScope.InstalledTools | VpmPlanScope.Removal,
        };

        /// <summary>
        /// The least disruptive plan that works, for a user who approves it before anything changes: what is asked for alone;
        /// else with the VRChat SDK changed (to a version the installed tools allow, when there is one); else with the tools
        /// limiting it updated or downgraded; and only when nothing else works, with such a tool removed. When none works,
        /// the widest attempt names the package that blocks.
        /// </summary>
        public static VpmDependencyPlan ResolveForApproval(IEnumerable<Requirement> requirements, IVpmCatalog catalog)
        {
            var list = (requirements ?? Enumerable.Empty<Requirement>()).ToList();
            VpmDependencyPlan plan = null;
            foreach (var scope in ApprovalScopes)
            {
                plan = Resolve(list, catalog, scope);
                if (plan.CanApply) return plan;
            }
            return plan;
        }

        private sealed class Option
        {
            public string Version, DisplayName, RepositoryUrl;
            /// <summary>The installed (or developed) copy, as it is.</summary>
            public bool Keep, Developed;
            /// <summary>The installed copy is removed (<see cref="VpmPlanScope.Removal"/>).</summary>
            public bool Remove;
            public IReadOnlyDictionary<string, string> Dependencies;
        }

        private sealed class Failure
        {
            public string Id, Reason;
            public VpmStepAction Action;
            public bool Limiting;
            public List<string> RequiredBy;
            public Dictionary<string, Option> Chosen;
        }

        private sealed class Solver
        {
            private readonly IVpmCatalog catalog;
            private readonly List<Requirement> roots;
            private readonly VpmPlanScope scope;
            // What installed packages ask of others, by the package asked for: a version the plan installs has to fit it.
            private readonly Dictionary<string, List<Requirement>> installedAsks = new Dictionary<string, List<Requirement>>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, Option> chosen = new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> order = new List<string>();
            private readonly HashSet<string> discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<Option>> options = new Dictionary<string, List<Option>>(StringComparer.OrdinalIgnoreCase);
            private int attempts;
            private bool exhausted;
            private HashSet<string> limitingChosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Failure failure;

            public Solver(List<Requirement> roots, IVpmCatalog catalog, VpmPlanScope scope)
            {
                this.roots = roots;
                this.catalog = catalog;
                this.scope = scope;
                foreach (string id in catalog.InstalledPackages() ?? Enumerable.Empty<string>())
                {
                    string version = catalog.Installed(id);
                    foreach (var dependency in catalog.InstalledDependencies(id) ?? new Dictionary<string, string>())
                    {
                        if (!installedAsks.TryGetValue(dependency.Key, out var list)) installedAsks[dependency.Key] = list = new List<Requirement>();
                        list.Add(new Requirement(dependency.Key, dependency.Value, $"{Name(id)} {version} (installed)", id));
                    }
                }
            }

            private string Name(string id) => catalog.DisplayName(id) ?? id;

            public VpmDependencyPlan Run()
            {
                foreach (var root in roots) Discover(root.Id);
                bool solved = Search(0);
                if (!solved) chosen = failure?.Chosen ?? new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
                var steps = new Dictionary<string, VpmPlanStep>(StringComparer.OrdinalIgnoreCase);
                // The tools changed only because they limit another package: they accept a version, they do not ask for it.
                var asked = Asked();
                limitingChosen = new HashSet<string>(chosen.Keys.Where(id => !chosen[id].Keep && !asked.Contains(id) && Conflicts(id).Count > 0), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in chosen) steps[pair.Key] = Step(pair.Key, pair.Value);
                if (!solved && failure != null)
                    steps[failure.Id] = new VpmPlanStep { Id = failure.Id, DisplayName = Name(failure.Id), From = catalog.Installed(failure.Id), Action = failure.Action, Reason = failure.Reason,
                        RequiredBy = failure.RequiredBy, Limiting = failure.Limiting };

                // Dependencies before the packages needing them (a cycle keeps the order it was found in).
                var plan = new VpmDependencyPlan { Scope = scope };
                var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Place(string id)
                {
                    if (!steps.TryGetValue(id, out var step) || !placed.Add(id)) return;
                    if (chosen.TryGetValue(id, out var option)) foreach (string dependency in option.Dependencies.Keys) Place(dependency);
                    plan.Steps.Add(step);
                }
                foreach (var root in roots) Place(root.Id);
                foreach (string id in order) Place(id);
                return plan;
            }

            private VpmPlanStep Step(string id, Option option)
            {
                string installed = catalog.Installed(id);
                var step = new VpmPlanStep { Id = id, DisplayName = option.DisplayName, From = installed, To = option.Version,
                    RequiredBy = Asks(id).Where(r => r.Source == null || !limitingChosen.Contains(r.Source)).Select(Describe).ToList() };
                if (option.Keep)
                {
                    step.Action = VpmStepAction.Keep;
                    if (option.Developed) step.Reason = "Developed in this project.";
                    return step;
                }
                // A tool nothing asked for changes because what its installed copy allows of a package the plan changes does not fit.
                var conflicts = Asked().Contains(id) ? new List<Conflict>() : Conflicts(id);
                if (option.Remove)
                {
                    step.Action = VpmStepAction.Remove;
                    step.To = null;
                    step.Approval = step.Limiting = true;
                    step.RequiredBy = conflicts.Select(c => c.Allowed).ToList();
                    step.Reason = $"{installed} works only with {Join(conflicts.Select(c => c.Allowed))}, and none of its versions works with {Join(conflicts.Select(c => c.Chosen))}. " +
                                  "Add it again with the VRChat Creator Companion once its author supports it.";
                    return step;
                }
                step.Action = installed == null ? VpmStepAction.Install : Compare(option.Version, installed) < 0 ? VpmStepAction.Downgrade : VpmStepAction.Upgrade;
                step.RepositoryUrl = option.RepositoryUrl;
                step.Limiting = conflicts.Count > 0;
                step.Approval = Protected(id) || step.Limiting;
                if (conflicts.Count > 0)
                {
                    step.RequiredBy = conflicts.Select(c => c.Allowed).ToList();
                    step.Reason = $"{installed} works only with {Join(conflicts.Select(c => c.Allowed))}; {option.Version} works with {Join(conflicts.Select(c => c.Chosen))}.";
                }
                else if (installed != null) step.Reason = $"Installed {installed} does not fit {string.Join(", ", step.RequiredBy)}.";
                return step;
            }

            // What the installed copy of a package asks of the packages the plan changes and does not get.
            private sealed class Conflict
            {
                /// <summary>"VRChat SDK - Avatars >=3.5.2 <3.8.X"</summary>
                public string Allowed;
                /// <summary>"VRChat SDK - Avatars 3.10.6", or "VRChat SDK - Avatars removed".</summary>
                public string Chosen;
            }

            private List<Conflict> Conflicts(string id)
            {
                var list = new List<Conflict>();
                foreach (var dependency in catalog.InstalledDependencies(id) ?? new Dictionary<string, string>())
                {
                    if (!chosen.TryGetValue(dependency.Key, out var other) || other.Keep) continue;
                    var range = Parse(dependency.Value);
                    if (other.Remove || range == null || !Satisfies(range, other.Version))
                        list.Add(new Conflict { Allowed = $"{Name(dependency.Key)} {dependency.Value}", Chosen = $"{Name(dependency.Key)} " + (other.Remove ? "removed" : other.Version) });
                }
                return list;
            }

            private static string Join(IEnumerable<string> parts)
            {
                var list = parts.ToList();
                return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
            }

            private static string Describe(Requirement requirement) => $"{requirement.RequiredBy} ({requirement.Range})";

            private void Discover(string id)
            {
                if (discovered.Add(id)) order.Add(id);
            }

            // What the original requirements and the chosen versions ask for.
            private HashSet<string> Asked()
            {
                var asked = new HashSet<string>(roots.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var option in chosen.Values) asked.UnionWith(option.Dependencies.Keys);
                return asked;
            }

            // An installed package outside the plan that the plan may change when it limits a package the plan changes.
            private bool MayChange(string id) => id != null && !catalog.Developed(id) &&
                (Protected(id) ? (scope & VpmPlanScope.Sdk) != 0 : (scope & VpmPlanScope.InstalledTools) != 0);

            // The ranges on a package: the original requirements and what the chosen versions ask.
            private List<Requirement> Asks(string id)
            {
                var asks = roots.Where(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var pair in chosen)
                    if (pair.Value.Dependencies.TryGetValue(id, out string range))
                        asks.Add(new Requirement(id, range, $"{pair.Value.DisplayName} {pair.Value.Version}", pair.Key));
                return asks;
            }

            // What installed packages outside the plan ask of a package.
            private IEnumerable<Requirement> InstalledAsks(string id) =>
                installedAsks.TryGetValue(id, out var list) ? list.Where(r => !chosen.ContainsKey(r.Source)) : Enumerable.Empty<Requirement>();

            private bool Search(int depth)
            {
                var asked = Asked();
                // An installed package the plan may change comes in once a version chosen for another does not fit what it asks.
                var limiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in chosen)
                {
                    if (pair.Value.Keep || !installedAsks.TryGetValue(pair.Key, out var list)) continue;
                    foreach (var ask in list)
                    {
                        if (chosen.ContainsKey(ask.Source) || asked.Contains(ask.Source) || !MayChange(ask.Source)) continue;
                        var range = Parse(ask.Range);
                        if (pair.Value.Remove || range == null || !Satisfies(range, pair.Value.Version)) { limiting.Add(ask.Source); Discover(ask.Source); }
                    }
                }
                // The first package found that something asks for (or that must change) and has no version yet.
                string id = order.FirstOrDefault(p => !chosen.ContainsKey(p) && (asked.Contains(p) || limiting.Contains(p)));
                if (id == null) return true;
                var asks = Asks(id);
                var ranges = asks.Select(r => Parse(r.Range)).ToList();
                if (ranges.Any(r => r == null)) return Fail(depth, id, VpmStepAction.Blocked, "A required version range cannot be read.", asks);
                // What installed packages outside the plan ask of it holds, unless the plan decides their version too (they
                // are asked for, or may change and come in after it).
                var installedRanges = InstalledAsks(id).Where(r => !asked.Contains(r.Source) && !MayChange(r.Source))
                    .Select(r => Parse(r.Range)).Where(r => r != null).ToList();
                // Pre-releases only when a range explicitly asks for one.
                bool prerelease = asks.Any(r => r.Range.Contains("-"));
                bool anyFits = false;
                foreach (var option in Ordered(id, asked))
                {
                    if (++attempts > MaxAttempts)
                    {
                        exhausted = true;
                        return Fail(depth, id, VpmStepAction.Blocked, "There are too many version combinations to check. Install it with the VRChat Creator Companion.", asks);
                    }
                    if (option.Remove)
                    {
                        // Only what nothing needs: no requirement, no chosen version, no other installed package.
                        if (asks.Count > 0 || InstalledAsks(id).Any() || chosen.Values.Any(o => o.Dependencies.ContainsKey(id))) continue;
                    }
                    else if (!option.Developed && !ranges.All(r => Satisfies(r, option.Version, option.Keep || prerelease))) continue;
                    // A version nothing ranges over (a tool the plan changes) is a release, never a pre-release.
                    if (!option.Keep && !option.Remove && !option.Developed && !prerelease && option.Version.Contains("-")) continue;
                    // A version the plan installs must also fit the installed packages using it; what is kept stays as it is.
                    if (!option.Keep && !option.Remove && !installedRanges.All(r => Satisfies(r, option.Version))) continue;
                    anyFits = true;
                    if (!FitsChosen(option)) continue;
                    chosen[id] = option;
                    foreach (string dependency in option.Dependencies.Keys) Discover(dependency);
                    if (Search(depth + 1)) return true;
                    chosen.Remove(id);
                    if (exhausted) return false;
                }
                string installed = catalog.Installed(id);
                if (limiting.Contains(id) && asks.Count == 0)
                {
                    var conflicts = Conflicts(id);
                    limitingFailure = true;
                    return Fail(depth, id, VpmStepAction.Blocked, $"{installed} works only with {Join(conflicts.Select(c => c.Allowed))}, and none of the versions " +
                        $"your VPM repositories offer works with {Join(conflicts.Select(c => c.Chosen))}.", conflicts.Select(c => c.Allowed).ToList());
                }
                if (Protected(id) && (scope & VpmPlanScope.Sdk) == 0)
                    return Fail(depth, id, VpmStepAction.Blocked, installed == null ? "Add it to this project with the VRChat Creator Companion." :
                        $"Needs another version of the VRChat SDK than {installed}: update it with the VRChat Creator Companion.", asks);
                if (installed == null && Options(id).Count == 0)
                    return Fail(depth, id, VpmStepAction.Missing, Protected(id) ? "The VRChat SDK's repository does not offer it: check the Creator Companion's repositories."
                        : "Not in the packages Orbiters knows or in your VPM repositories.", asks);
                var everyone = asks.Concat(InstalledAsks(id)).Select(Describe).ToList();
                return Fail(depth, id, VpmStepAction.Blocked, anyFits
                    ? "No version of it works with the versions the other packages need: " + string.Join(", ", everyone) + "."
                    : "No version satisfies every package that needs it: " + string.Join(", ", everyone) + ".", asks.Concat(InstalledAsks(id)).ToList());
            }

            // Its dependencies fit the versions chosen already (a removed package fits nothing).
            private bool FitsChosen(Option option)
            {
                foreach (var dependency in option.Dependencies)
                {
                    if (!chosen.TryGetValue(dependency.Key, out var other) || other.Developed) continue;
                    if (other.Remove) return false;
                    var range = Parse(dependency.Value);
                    if (range == null || !Satisfies(range, other.Version)) return false;
                }
                return true;
            }

            // The deepest point reached says why: the package that could not be chosen there.
            private bool Fail(int depth, string id, VpmStepAction action, string reason, List<Requirement> asks) =>
                Fail(depth, id, action, reason, asks.Select(Describe).Distinct().ToList());

            private bool limitingFailure;

            private bool Fail(int depth, string id, VpmStepAction action, string reason, List<string> requiredBy)
            {
                if (failure == null || depth > failure.Chosen.Count)
                    failure = new Failure { Id = id, Action = action, Reason = reason, RequiredBy = requiredBy, Limiting = limitingFailure,
                        Chosen = new Dictionary<string, Option>(chosen, StringComparer.OrdinalIgnoreCase) };
                limitingFailure = false;
                return false;
            }

            // Where the plan may change installed tools, the versions that leave more of them as they are come first (newest
            // first among those): an SDK version every installed tool allows before one that needs tools updated, and one
            // needing fewer changes before one needing more, so a removal is only ever the last way out.
            private IEnumerable<Option> Ordered(string id, HashSet<string> asked)
            {
                var list = Options(id);
                if ((scope & VpmPlanScope.InstalledTools) == 0 || !installedAsks.ContainsKey(id)) return list;
                var outside = InstalledAsks(id).Where(r => !asked.Contains(r.Source)).Select(r => Parse(r.Range)).ToList();
                int Breaks(Option o) => o.Keep || o.Remove ? 0 : outside.Count(r => r == null || !Satisfies(r, o.Version));
                return list.Select((o, i) => (o, i)).OrderBy(p => p.o.Keep ? 0 : p.o.Remove ? 2 : 1).ThenBy(p => Breaks(p.o)).ThenBy(p => p.i).Select(p => p.o).ToList();
            }

            // The versions to try, in order: the installed copy first (kept when it fits), then newest first, then (a tool
            // the plan may remove) its removal.
            private List<Option> Options(string id)
            {
                if (options.TryGetValue(id, out var list)) return list;
                list = new List<Option>();
                string installed = catalog.Installed(id);
                var installedDependencies = new Dictionary<string, string>(catalog.InstalledDependencies(id) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                if (catalog.Developed(id)) list.Add(new Option { Version = installed, DisplayName = Name(id), Keep = true, Developed = true, Dependencies = installedDependencies });
                else
                {
                    if (installed != null) list.Add(new Option { Version = installed, DisplayName = Name(id), Keep = true, Dependencies = installedDependencies });
                    if (!Protected(id) || (scope & VpmPlanScope.Sdk) != 0)
                        foreach (var candidate in catalog.Versions(id) ?? Enumerable.Empty<VpmCandidate>())
                        {
                            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Version) || candidate.Version == installed) continue;
                            list.Add(new Option
                            {
                                Version = candidate.Version, RepositoryUrl = candidate.RepositoryUrl,
                                DisplayName = !string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.DisplayName : Name(id),
                                Dependencies = new Dictionary<string, string>(candidate.Dependencies ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                            });
                        }
                    // Never Orbiters' own packages: they are what installs the plan.
                    if (installed != null && (scope & VpmPlanScope.Removal) != 0 && !Protected(id) && !id.StartsWith("orbiters.", StringComparison.OrdinalIgnoreCase))
                        list.Add(new Option { Version = installed, DisplayName = Name(id), Remove = true, Dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) });
                }
                options[id] = list;
                return list;
            }
        }

        // Ranges read once: a search reads the same few again and again.
        [ThreadStatic] private static Dictionary<string, SemanticRange> ranges;

        private static SemanticRange Parse(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "*" : value;
            ranges ??= new Dictionary<string, SemanticRange>(StringComparer.Ordinal);
            if (ranges.TryGetValue(value, out var known)) return known;
            SemanticRange range;
            try { range = new SemanticRange(value, true); }
            catch (Exception) { range = null; }
            if (ranges.Count > 4096) ranges.Clear();
            return ranges[value] = range;
        }

        private static int Compare(string a, string b)
        {
            try { return new SemanticVersioning.Version(a, true).CompareTo(new SemanticVersioning.Version(b, true)); }
            catch (Exception) { return string.CompareOrdinal(a, b); }
        }

        private static bool Satisfies(SemanticRange range, string version, bool prerelease = true)
        {
            try { return range.IsSatisfied(version, true, prerelease); }
            catch (Exception) { return false; }
        }

        /// <summary>"Install VRCFury 1.1334.0", "Update Modular Avatar 1.10.0 → 1.12.5", "Remove Gesture Manager 3.9.3".</summary>
        public static string Describe(VpmPlanStep step)
        {
            switch (step.Action)
            {
                case VpmStepAction.Install: return $"Install {step.DisplayName} {step.To}";
                case VpmStepAction.Upgrade: return $"Update {step.DisplayName} {step.From} → {step.To}";
                case VpmStepAction.Downgrade: return $"Downgrade {step.DisplayName} {step.From} → {step.To}";
                case VpmStepAction.Remove: return $"Remove {step.DisplayName} {step.From}";
                case VpmStepAction.Keep: return $"{step.DisplayName} {step.To} is installed";
                case VpmStepAction.Missing: return $"{step.DisplayName} cannot be installed";
                default: return $"{step.DisplayName} needs your attention";
            }
        }
    }
}
