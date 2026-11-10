using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using VRC.PackageManagement.Core;
using VRC.PackageManagement.Core.Types;
using VRC.PackageManagement.Core.Types.Packages;
using VRC.PackageManagement.Core.Types.Providers;

namespace Orbiters.Toolkit.Editor.Vpm
{
    /// <summary>
    /// The project's side of a <see cref="VpmDependencyPlan"/>: what it has installed (with the dependencies its copies
    /// declare), the versions Orbiters' known VPM repositories offer (each with its dependencies, so the plan is complete
    /// before any repository is added), every version the user's own VPM repositories offer as a fallback, and applying a
    /// plan the user accepted.
    /// </summary>
    public sealed class VpmProjectCatalog : IVpmCatalog
    {
        private readonly Dictionary<string, string> installed;
        private readonly Dictionary<string, List<VpmCandidate>> known;
        // The packages in Packages/ by name: the VPM dependencies their package.json declares.
        private readonly Dictionary<string, Dictionary<string, string>> copies = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        // The git clones in Packages/ (packages developed here) by name, with their version: read once with the copies.
        private readonly Dictionary<string, string> developed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<VpmCandidate>> repositories;

        /// <param name="knownPackages">Orbiters' approved packages, already limited to their supported versions.</param>
        public VpmProjectCatalog(IEnumerable<VpmCandidate> knownPackages)
        {
            known = VpmCandidates.Newest(knownPackages ?? Enumerable.Empty<VpmCandidate>()).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            try { installed = new UnityProject(ProjectPath).GetInstalledVersions() ?? new Dictionary<string, string>(); }
            catch (Exception ex) { Debug.LogWarning("[Orbiters] Could not read the project's VPM packages: " + ex.Message); installed = new Dictionary<string, string>(); }
            installed = new Dictionary<string, string>(installed, StringComparer.OrdinalIgnoreCase);
            ReadCopies();
        }

        private sealed class Manifest
        {
            [JsonProperty("name")] public string name;
            [JsonProperty("version")] public string version;
            [JsonProperty("vpmDependencies")] public Dictionary<string, string> vpmDependencies;
        }

        private void ReadCopies()
        {
            string packages = Path.Combine(ProjectPath, "Packages");
            if (!Directory.Exists(packages)) return;
            foreach (string folder in Directory.GetDirectories(packages))
            {
                string file = Path.Combine(folder, "package.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(file));
                    if (string.IsNullOrWhiteSpace(manifest?.name)) continue;
                    copies[manifest.name] = new Dictionary<string, string>(manifest.vpmDependencies ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    // VPM never installs a .git folder (VpmDependencies.LocalDevelopmentCopy); the first clone of a name counts.
                    if ((Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"))) && !developed.ContainsKey(manifest.name))
                        developed[manifest.name] = manifest.version;
                }
                catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        public IReadOnlyDictionary<string, string> InstalledDependencies(string id) =>
            Installed(id) != null && copies.TryGetValue(id, out var dependencies) ? dependencies : new Dictionary<string, string>();

        public IEnumerable<string> InstalledPackages() => installed.Keys.Concat(copies.Keys.Where(Developed)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public string Installed(string id)
        {
            if (installed.TryGetValue(id, out var version)) return version;
            return developed.TryGetValue(id, out version) ? version : null;
        }

        public bool Developed(string id) => id != null && developed.ContainsKey(id);

        public string DisplayName(string id) => known.TryGetValue(id, out var list) ? list.FirstOrDefault()?.DisplayName : Local(id, null)?.Title;

        public IEnumerable<VpmCandidate> Versions(string id)
        {
            if (known.TryGetValue(id, out var list) && list.Count > 0) return list;
            // Not curated by Orbiters: every version the VPM repositories offer (the official ones first, then the user's).
            return Repositories().TryGetValue(id, out var offered) ? offered : new List<VpmCandidate>();
        }

        private Dictionary<string, List<VpmCandidate>> Repositories()
        {
            if (repositories != null) return repositories;
            var all = new List<VpmCandidate>();
            try
            {
                foreach (var provider in Repos.GetAll)
                {
                    Dictionary<string, IVRCPackageVersionList> packages;
                    try { packages = provider?.GetAllWithVersions(); }
                    catch (Exception) { continue; }
                    foreach (var versions in packages?.Values ?? Enumerable.Empty<IVRCPackageVersionList>())
                    foreach (var package in versions?.Versions?.Values ?? Enumerable.Empty<IVRCPackage>())
                        if (package != null && !string.IsNullOrWhiteSpace(package.Id))
                            all.Add(new VpmCandidate { Id = package.Id, Version = package.Version, DisplayName = package.Title,
                                Dependencies = new Dictionary<string, string>(package.VPMDependencies ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) });
                }
            }
            catch (Exception ex) { Debug.LogWarning("[Orbiters] Could not read the VPM repositories: " + ex.Message); }
            // Newest first; a version two repositories offer is taken from the first.
            repositories = VpmCandidates.Newest(all).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            return repositories;
        }

        private static IVRCPackage Local(string id, string range)
        {
            try { return string.IsNullOrWhiteSpace(range) ? Repos.GetLatestPackage(id) : Repos.GetPackageWithVersionMatch(id, range); }
            catch (Exception) { return null; }
        }

        internal static string ProjectPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    /// <summary>Where an applied plan is: the step running (1-based) of how many, what it does, and 0..1 overall.</summary>
    public sealed class VpmApplyProgress
    {
        public int Step, Count;
        public string Text;
        public float Fraction;
    }

    public static class VpmApprovalPlanner
    {
        /// <summary>
        /// The plan a user approves before anything changes (<see cref="VpmDependencyPlan.ResolveForApproval"/>): the VRChat
        /// SDK and the tools limiting it may change. When that needs a removal or finds no plan, the installed tools that no
        /// configured repository offers another version of are looked up in the repositories this computer read before
        /// (<see cref="VpmRepositoryHistory"/>), their versions read from those repositories, and the plan solved again; the
        /// one with fewer removals wins (the repository is then added when the plan is applied, as the plan shows).
        /// </summary>
        public static async System.Threading.Tasks.Task<VpmDependencyPlan> PlanAsync(IEnumerable<VpmDependencyPlan.Requirement> requirements,
            IEnumerable<VpmCandidate> known = null, System.Threading.CancellationToken cancellation = default)
        {
            var list = (requirements ?? Enumerable.Empty<VpmDependencyPlan.Requirement>()).ToList();
            var candidates = new List<VpmCandidate>(known ?? Enumerable.Empty<VpmCandidate>());
            var catalog = new VpmProjectCatalog(candidates);
            // The search runs on a worker: the catalog read the project above, on the editor's thread; what it reads later is
            // VPM's repositories (VPM's own library, as ApplyAsync uses it on workers).
            var plan = await System.Threading.Tasks.Task.Run(() => VpmDependencyPlan.ResolveForApproval(list, catalog), cancellation);
            if (plan.CanApply && Removals(plan) == 0) return plan;
            var stranded = catalog.InstalledPackages()
                .Where(id => !catalog.Developed(id) && !VpmDependencyPlan.Protected(id) && !catalog.Versions(id).Any(c => c.Version != catalog.Installed(id)))
                .ToList();
            if (stranded.Count == 0) return plan;
            var offering = await System.Threading.Tasks.Task.Run(() => VpmRepositoryHistory.Offering(stranded), cancellation);
            var extra = new List<VpmCandidate>();
            foreach (string url in offering.Values.SelectMany(u => u).Distinct(StringComparer.OrdinalIgnoreCase))
                extra.AddRange((await VpmListingReader.ReadAsync(url, cancellation))
                    .Where(c => offering.TryGetValue(c.Id, out var urls) && urls.Contains(url, StringComparer.OrdinalIgnoreCase)));
            if (extra.Count == 0) return plan;
            var widerCatalog = new VpmProjectCatalog(candidates.Concat(extra));
            var wider = await System.Threading.Tasks.Task.Run(() => VpmDependencyPlan.ResolveForApproval(list, widerCatalog), cancellation);
            return wider.CanApply && (!plan.CanApply || Removals(wider) < Removals(plan)) ? wider : plan;
        }

        private static int Removals(VpmDependencyPlan plan) => plan.Changes.Count(s => s.Action == VpmStepAction.Remove);
    }

    public static class VpmPlanInstaller
    {
        // One piece of work of an applied plan; it reports a failure by adding to the result's errors.
        private sealed class Work
        {
            public string Text;
            public float At;
            public Action<VpmDependencyInstallResult> Run;
        }

        /// <summary>
        /// Applies an accepted plan: adds the repositories it needs, removes what it removes, installs, updates and downgrades
        /// its packages (dependencies first), then resolves the project. Synchronous with a progress bar; Unity reloads its
        /// scripts afterwards.
        /// </summary>
        public static VpmDependencyInstallResult Apply(VpmDependencyPlan plan)
        {
            var result = Check(plan, out var work);
            if (work == null) return result;
            try
            {
                VpmDependencies.BeginInstall();
                foreach (var piece in work)
                {
                    EditorUtility.DisplayProgressBar("Installing packages", piece.Text, piece.At);
                    piece.Run(result);
                    if (result.Errors.Count > 0) break;
                }
                result.Success = result.Errors.Count == 0;
                if (result.Success) Reload();
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                Debug.LogError("[Orbiters] Package installation failed: " + ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                VpmDependencies.EndInstall();
            }
            return result;
        }

        /// <summary>
        /// The same as <see cref="Apply"/> with the editor responsive: each step (downloads, VPM's resolver) runs off the main
        /// thread and reports to <paramref name="progress"/> on it, Unity does not import anything until it ends, and nothing
        /// reloads unless <paramref name="reload"/> (otherwise call <see cref="Reload"/> once what must survive the reload is saved).
        /// </summary>
        public static async System.Threading.Tasks.Task<VpmDependencyInstallResult> ApplyAsync(VpmDependencyPlan plan, Action<VpmApplyProgress> progress, bool reload = true)
        {
            var result = Check(plan, out var work);
            if (work == null) return result;
            VpmDependencies.BeginInstall();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    var piece = work[i];
                    progress?.Invoke(new VpmApplyProgress { Step = i + 1, Count = work.Count, Text = piece.Text, Fraction = piece.At });
                    await System.Threading.Tasks.Task.Run(() => piece.Run(result));
                    if (result.Errors.Count > 0) break;
                }
                result.Success = result.Errors.Count == 0;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                Debug.LogError("[Orbiters] Package installation failed: " + ex);
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
                VpmDependencies.EndInstall();
            }
            if (result.Success && reload) Reload();
            return result;
        }

        /// <summary>Unity takes in the changed packages (its scripts reload afterwards).</summary>
        public static void Reload()
        {
            UnityEditor.PackageManager.Client.Resolve();
            AssetDatabase.Refresh();
        }

        // What applying the plan does, in order; null (with the reason in the result) when it cannot start.
        private static VpmDependencyInstallResult Check(VpmDependencyPlan plan, out List<Work> work)
        {
            work = null;
            var result = new VpmDependencyInstallResult();
            if (plan == null || !plan.CanApply) { result.Errors.Add("This dependency plan cannot be applied."); return result; }
            // Removals first: a removed tool's range no longer holds the packages updated after it.
            var changes = plan.Changes.OrderBy(s => s.Action == VpmStepAction.Remove ? 0 : 1).ToList();
            if (changes.Count == 0) { result.Success = true; return result; }
            if (VpmDependencies.IsInstalling) { result.Errors.Add("A package install is already running."); return result; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { result.Errors.Add("Unity is compiling or importing assets. Wait for it to finish, then try again."); return result; }
            string path = VpmProjectCatalog.ProjectPath;
            work = new List<Work>();
            var urls = plan.Repositories.ToList();
            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                work.Add(new Work { Text = "Adding the VPM repository " + url + "…", At = 0.05f + 0.15f * i / urls.Count, Run = r =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { r.Errors.Add("Invalid VPM repository URL: " + url); return; }
                    if (Repos.UserRepoExists(uri)) return;
                    if (!Repos.AddRepo(uri, new Dictionary<string, string>())) { r.Errors.Add("VPM could not add the repository " + url + "."); return; }
                    r.AddedRepositoryUrls.Add(url);
                } });
            }
            UnityProject project = null;
            work.Add(new Work { Text = "Reading the VPM repositories…", At = 0.22f, Run = _ =>
            {
                foreach (var provider in Repos.GetAll) (provider as VPMPackageProvider)?.Refresh();
                project = new UnityProject(path);
            } });
            for (int i = 0; i < changes.Count; i++)
            {
                var step = changes[i];
                work.Add(new Work { Text = VpmDependencyPlan.Describe(step) + "…", At = Mathf.Lerp(0.25f, 0.85f, (float)i / changes.Count), Run = r =>
                {
                    if (step.Action == VpmStepAction.Remove)
                    {
                        if (!project.RemoveVPMPackage(step.Id)) r.Errors.Add($"VPM could not remove {step.DisplayName} {step.From}.");
                        return;
                    }
                    if (Repos.GetPackageWithVersionMatch(step.Id, step.To) == null) { r.Errors.Add($"{step.DisplayName} {step.To} is not available from its repository."); return; }
                    if (!project.AddVPMPackage(step.Id, step.To, Repos.GetAll)) { r.Errors.Add($"VPM could not install {step.DisplayName} {step.To}."); return; }
                    r.InstalledPackageIds.Add(step.Id);
                } });
            }
            work.Add(new Work { Text = "Resolving the project's packages…", At = 0.9f, Run = r =>
            {
                if (!VPMProjectManifest.Resolve(path, Repos.GetAll)) { r.Errors.Add("The VPM resolver could not finish resolving the project packages."); return; }
                // The project must have what was reviewed: the resolver may not pick other versions on its own.
                var now = new UnityProject(path).GetInstalledVersions() ?? new Dictionary<string, string>();
                foreach (var step in changes)
                {
                    now.TryGetValue(step.Id, out string version);
                    if (step.Action == VpmStepAction.Remove)
                    {
                        if (version != null) r.Errors.Add($"{step.DisplayName} {version} is still installed after removing it.");
                    }
                    else if (version != step.To) r.Errors.Add($"{step.DisplayName} is {version ?? "not installed"} after installing, not the {step.To} the plan showed.");
                }
            } });
            return result;
        }
    }
}
