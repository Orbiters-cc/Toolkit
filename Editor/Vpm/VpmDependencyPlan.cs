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
    /// dependencies, the ranges several packages put on the same one, and what the project already has. Installed packages
    /// that already fit stay as they are; one that must change is shown as an upgrade, and the VRChat SDK is never
    /// changed (its upgrade belongs to the Creator Companion).
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

        public static VpmDependencyPlan Resolve(IEnumerable<Requirement> requirements, IVpmCatalog catalog)
        {
            var constraints = new Dictionary<string, List<Requirement>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            void Add(Requirement requirement)
            {
                if (!constraints.TryGetValue(requirement.Id, out var list)) { constraints[requirement.Id] = list = new List<Requirement>(); order.Add(requirement.Id); }
                if (!list.Any(r => r.Range == requirement.Range && r.RequiredBy == requirement.RequiredBy)) list.Add(requirement);
            }
            foreach (var requirement in requirements ?? Enumerable.Empty<Requirement>()) Add(requirement);

            // Decisions add their dependencies' constraints, which can change earlier decisions: repeat until stable.
            var decided = new Dictionary<string, VpmPlanStep>(StringComparer.OrdinalIgnoreCase);
            for (int round = 0; round < 32; round++)
            {
                bool changed = false;
                foreach (string id in order.ToList())
                {
                    if (constraints[id].Count == 0) { decided.Remove(id); continue; }
                    var step = Decide(id, constraints[id], catalog, out var dependencies);
                    if (decided.TryGetValue(id, out var previous) && previous.Action == step.Action && previous.To == step.To &&
                        previous.RequiredBy.SequenceEqual(step.RequiredBy)) continue;
                    decided[id] = step;
                    changed = true;
                    // Another version of this package asks for other things: forget what the previous choice required.
                    foreach (var list in constraints.Values) list.RemoveAll(r => string.Equals(r.Source, id, StringComparison.OrdinalIgnoreCase));
                    foreach (var dependency in dependencies) Add(new Requirement(dependency.Key, dependency.Value, $"{step.DisplayName} {step.To}", id));
                }
                if (!changed) break;
            }
            var plan = new VpmDependencyPlan();
            // Dependencies before the packages needing them: a later requirement is installed first.
            foreach (string id in order.AsEnumerable().Reverse()) if (decided.TryGetValue(id, out var step)) plan.Steps.Add(step);
            return plan;
        }

        private static VpmPlanStep Decide(string id, List<Requirement> requirements, IVpmCatalog catalog, out Dictionary<string, string> dependencies)
        {
            dependencies = new Dictionary<string, string>();
            string installed = catalog.Installed(id);
            var step = new VpmPlanStep { Id = id, DisplayName = catalog.DisplayName(id) ?? id, From = installed,
                RequiredBy = requirements.Select(r => $"{r.RequiredBy} ({r.Range})").ToList() };
            var ranges = requirements.Select(r => Parse(r.Range)).ToList();
            if (ranges.Any(r => r == null)) { step.Action = VpmStepAction.Blocked; step.Reason = "A required version range cannot be read."; return step; }

            if (catalog.Developed(id)) { step.Action = VpmStepAction.Keep; step.To = installed; step.Reason = "Developed in this project."; return step; }
            if (installed != null && ranges.All(r => Satisfies(r, installed))) { step.Action = VpmStepAction.Keep; step.To = installed; return step; }
            if (Protected(id))
            {
                step.Action = VpmStepAction.Blocked;
                step.Reason = installed == null ? "Add it to this project with the VRChat Creator Companion." : $"Needs another version of the VRChat SDK than {installed}: update it with the VRChat Creator Companion.";
                return step;
            }
            var versions = catalog.Versions(id)?.ToList() ?? new List<VpmCandidate>();
            if (versions.Count == 0) { step.Action = VpmStepAction.Missing; step.Reason = "Not in the packages Orbiters knows or in your VPM repositories."; return step; }
            // Pre-releases only when a range explicitly asks for one.
            bool prerelease = requirements.Any(r => r.Range.Contains("-"));
            var chosen = versions.FirstOrDefault(v => ranges.All(r => Satisfies(r, v.Version, prerelease)));
            if (chosen == null)
            {
                step.Action = VpmStepAction.Blocked;
                step.Reason = "No version satisfies every package that needs it: " + string.Join(", ", step.RequiredBy) + ".";
                return step;
            }
            step.Action = installed == null ? VpmStepAction.Install : VpmStepAction.Upgrade;
            step.To = chosen.Version;
            step.RepositoryUrl = chosen.RepositoryUrl;
            if (!string.IsNullOrWhiteSpace(chosen.DisplayName)) step.DisplayName = chosen.DisplayName;
            if (step.Action == VpmStepAction.Upgrade) step.Reason = $"Installed {installed} does not fit {string.Join(", ", step.RequiredBy)}.";
            dependencies = chosen.Dependencies ?? new Dictionary<string, string>();
            return step;
        }

        private static SemanticRange Parse(string value)
        {
            try { return new SemanticRange(string.IsNullOrWhiteSpace(value) ? "*" : value, true); }
            catch (Exception) { return null; }
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
