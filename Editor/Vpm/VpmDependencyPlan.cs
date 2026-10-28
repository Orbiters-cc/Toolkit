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

    public enum VpmStepAction { Keep, Install, Upgrade, Blocked, Missing }

    public sealed class VpmPlanStep
    {
        public string Id, DisplayName, From, To, RepositoryUrl, Reason;
        public VpmStepAction Action;
        /// <summary>The packages (or the asset) asking for it, with their ranges: "Hat (>=1.1000.0)".</summary>
        public List<string> RequiredBy = new List<string>();
        public bool Changes => Action == VpmStepAction.Install || Action == VpmStepAction.Upgrade;
    }

    /// <summary>
    /// Every package change needed to satisfy some requirements, resolved together before anything changes: transitive
    /// dependencies (of kept packages too), the ranges several packages put on the same one, what the project already has
    /// and what its other packages ask of it. Installed packages that already fit stay as they are; one that must change is
    /// shown as an upgrade, and the VRChat SDK is never changed (its upgrade belongs to the Creator Companion).
    /// </summary>
    public sealed class VpmDependencyPlan
    {
        public readonly List<VpmPlanStep> Steps = new List<VpmPlanStep>();
        public IEnumerable<VpmPlanStep> Changes => Steps.Where(s => s.Changes);
        public bool CanApply => Steps.All(s => s.Action != VpmStepAction.Blocked && s.Action != VpmStepAction.Missing);
        public bool NothingToDo => CanApply && !Changes.Any();
        public IEnumerable<string> Repositories => Changes.Select(s => s.RepositoryUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Packages Orbiters never installs or upgrades on its own.</summary>
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
        /// fit what the project's other installed packages ask of it. Steps come dependencies first. When no choice works,
        /// the plan cannot be applied and the package that could not be chosen says why.
        /// </summary>
        public static VpmDependencyPlan Resolve(IEnumerable<Requirement> requirements, IVpmCatalog catalog) =>
            new Solver((requirements ?? Enumerable.Empty<Requirement>()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Id)).ToList(), catalog).Run();

        private sealed class Option
        {
            public string Version, DisplayName, RepositoryUrl;
            /// <summary>The installed (or developed) copy, as it is.</summary>
            public bool Keep, Developed;
            public IReadOnlyDictionary<string, string> Dependencies;
        }

        private sealed class Failure
        {
            public string Id, Reason;
            public VpmStepAction Action;
            public List<string> RequiredBy;
            public Dictionary<string, Option> Chosen;
        }

        private sealed class Solver
        {
            private readonly IVpmCatalog catalog;
            private readonly List<Requirement> roots;
            // What installed packages ask of others, by the package asked for: a version the plan installs has to fit it.
            private readonly Dictionary<string, List<Requirement>> installedAsks = new Dictionary<string, List<Requirement>>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, Option> chosen = new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> order = new List<string>();
            private readonly HashSet<string> discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<Option>> options = new Dictionary<string, List<Option>>(StringComparer.OrdinalIgnoreCase);
            private int attempts;
            private bool exhausted;
            private Failure failure;

            public Solver(List<Requirement> roots, IVpmCatalog catalog)
            {
                this.roots = roots;
                this.catalog = catalog;
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
                foreach (var pair in chosen) steps[pair.Key] = Step(pair.Key, pair.Value);
                if (!solved && failure != null)
                    steps[failure.Id] = new VpmPlanStep { Id = failure.Id, DisplayName = Name(failure.Id), From = catalog.Installed(failure.Id), Action = failure.Action, Reason = failure.Reason, RequiredBy = failure.RequiredBy };

                // Dependencies before the packages needing them (a cycle keeps the order it was found in).
                var plan = new VpmDependencyPlan();
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
                var step = new VpmPlanStep { Id = id, DisplayName = option.DisplayName, From = installed, To = option.Version, RequiredBy = Asks(id).Select(Describe).ToList() };
                if (option.Keep)
                {
                    step.Action = VpmStepAction.Keep;
                    if (option.Developed) step.Reason = "Developed in this project.";
                    return step;
                }
                step.Action = installed == null ? VpmStepAction.Install : VpmStepAction.Upgrade;
                step.RepositoryUrl = option.RepositoryUrl;
                if (step.Action == VpmStepAction.Upgrade) step.Reason = $"Installed {installed} does not fit {string.Join(", ", step.RequiredBy)}.";
                return step;
            }

            private static string Describe(Requirement requirement) => $"{requirement.RequiredBy} ({requirement.Range})";

            private void Discover(string id)
            {
                if (discovered.Add(id)) order.Add(id);
            }

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
                // The first package found that something asks for and has no version yet.
                var asked = new HashSet<string>(roots.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var option in chosen.Values) asked.UnionWith(option.Dependencies.Keys);
                string id = order.FirstOrDefault(p => !chosen.ContainsKey(p) && asked.Contains(p));
                if (id == null) return true;
                var asks = Asks(id);
                var ranges = asks.Select(r => Parse(r.Range)).ToList();
                if (ranges.Any(r => r == null)) return Fail(depth, id, VpmStepAction.Blocked, "A required version range cannot be read.", asks);
                var installedRanges = InstalledAsks(id).Select(r => Parse(r.Range)).Where(r => r != null).ToList();
                // Pre-releases only when a range explicitly asks for one.
                bool prerelease = asks.Any(r => r.Range.Contains("-"));
                bool anyFits = false;
                foreach (var option in Options(id))
                {
                    if (++attempts > MaxAttempts)
                    {
                        exhausted = true;
                        return Fail(depth, id, VpmStepAction.Blocked, "There are too many version combinations to check. Install it with the VRChat Creator Companion.", asks);
                    }
                    if (!option.Developed && !ranges.All(r => Satisfies(r, option.Version, option.Keep || prerelease))) continue;
                    // A version the plan installs must also fit the installed packages using it; what is kept stays as it is.
                    if (!option.Keep && !installedRanges.All(r => Satisfies(r, option.Version))) continue;
                    anyFits = true;
                    if (!FitsChosen(option)) continue;
                    chosen[id] = option;
                    foreach (string dependency in option.Dependencies.Keys) Discover(dependency);
                    if (Search(depth + 1)) return true;
                    chosen.Remove(id);
                    if (exhausted) return false;
                }
                string installed = catalog.Installed(id);
                if (Protected(id))
                    return Fail(depth, id, VpmStepAction.Blocked, installed == null ? "Add it to this project with the VRChat Creator Companion." :
                        $"Needs another version of the VRChat SDK than {installed}: update it with the VRChat Creator Companion.", asks);
                if (installed == null && Options(id).Count == 0)
                    return Fail(depth, id, VpmStepAction.Missing, "Not in the packages Orbiters knows or in your VPM repositories.", asks);
                var everyone = asks.Concat(InstalledAsks(id)).Select(Describe).ToList();
                return Fail(depth, id, VpmStepAction.Blocked, anyFits
                    ? "No version of it works with the versions the other packages need: " + string.Join(", ", everyone) + "."
                    : "No version satisfies every package that needs it: " + string.Join(", ", everyone) + ".", asks.Concat(InstalledAsks(id)).ToList());
            }

            // Its dependencies fit the versions chosen already.
            private bool FitsChosen(Option option)
            {
                foreach (var dependency in option.Dependencies)
                {
                    if (!chosen.TryGetValue(dependency.Key, out var other) || other.Developed) continue;
                    var range = Parse(dependency.Value);
                    if (range == null || !Satisfies(range, other.Version)) return false;
                }
                return true;
            }

            // The deepest point reached says why: the package that could not be chosen there.
            private bool Fail(int depth, string id, VpmStepAction action, string reason, List<Requirement> asks)
            {
                if (failure == null || depth > failure.Chosen.Count)
                    failure = new Failure { Id = id, Action = action, Reason = reason, RequiredBy = asks.Select(Describe).Distinct().ToList(),
                        Chosen = new Dictionary<string, Option>(chosen, StringComparer.OrdinalIgnoreCase) };
                return false;
            }

            // The versions to try, in order: the installed copy first (kept when it fits), then newest first.
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
                    if (!Protected(id))
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

        private static bool Satisfies(SemanticRange range, string version, bool prerelease = true)
        {
            try { return range.IsSatisfied(version, true, prerelease); }
            catch (Exception) { return false; }
        }

        /// <summary>"Install VRCFury 1.1334.0", "Update Modular Avatar 1.10.0 → 1.12.5".</summary>
        public static string Describe(VpmPlanStep step)
        {
            switch (step.Action)
            {
                case VpmStepAction.Install: return $"Install {step.DisplayName} {step.To}";
                case VpmStepAction.Upgrade: return $"Update {step.DisplayName} {step.From} → {step.To}";
                case VpmStepAction.Keep: return $"{step.DisplayName} {step.To} is installed";
                case VpmStepAction.Missing: return $"{step.DisplayName} cannot be installed";
                default: return $"{step.DisplayName} needs your attention";
            }
        }
    }
}
