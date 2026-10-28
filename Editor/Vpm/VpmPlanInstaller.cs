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
                    if (!string.IsNullOrWhiteSpace(manifest?.name))
                        copies[manifest.name] = new Dictionary<string, string>(manifest.vpmDependencies ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
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
            return VpmDependencies.LocalDevelopmentCopy(id, out version) ? version : null;
        }

        public bool Developed(string id) => VpmDependencies.LocalDevelopmentCopy(id, out _);

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

    public static class VpmPlanInstaller
    {
        /// <summary>
        /// Applies an accepted plan: adds the repositories it needs, installs and updates its packages (dependencies first),
        /// then resolves the project. Synchronous with a progress bar; Unity reloads its scripts afterwards.
        /// </summary>
        public static VpmDependencyInstallResult Apply(VpmDependencyPlan plan)
        {
            var result = new VpmDependencyInstallResult();
            if (plan == null || !plan.CanApply) { result.Errors.Add("This dependency plan cannot be applied."); return result; }
            var changes = plan.Changes.ToList();
            if (changes.Count == 0) { result.Success = true; return result; }
            if (VpmDependencies.IsInstalling) { result.Errors.Add("A package install is already running."); return result; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { result.Errors.Add("Unity is compiling or importing assets. Wait for it to finish, then try again."); return result; }
            try
            {
                VpmDependencies.BeginInstall();
                var urls = plan.Repositories.ToList();
                for (int i = 0; i < urls.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Installing packages", "Adding VPM repository…", 0.05f + 0.2f * (i + 1f) / urls.Count);
                    if (!Uri.TryCreate(urls[i], UriKind.Absolute, out var uri)) { result.Errors.Add("Invalid VPM repository URL: " + urls[i]); continue; }
                    if (Repos.UserRepoExists(uri)) continue;
                    if (!Repos.AddRepo(uri, new Dictionary<string, string>())) { result.Errors.Add("VPM could not add repository: " + urls[i]); continue; }
                    result.AddedRepositoryUrls.Add(urls[i]);
                }
                foreach (var provider in Repos.GetAll) (provider as VPMPackageProvider)?.Refresh();
                var project = new UnityProject(VpmProjectCatalog.ProjectPath);
                for (int i = 0; i < changes.Count && result.Errors.Count == 0; i++)
                {
                    var step = changes[i];
                    EditorUtility.DisplayProgressBar("Installing packages", VpmDependencyPlan.Describe(step) + "…", Mathf.Lerp(0.3f, 0.85f, (i + 1f) / changes.Count));
                    if (Repos.GetPackageWithVersionMatch(step.Id, step.To) == null) { result.Errors.Add($"{step.DisplayName} {step.To} is not available from its repository."); break; }
                    if (!project.AddVPMPackage(step.Id, step.To, Repos.GetAll)) { result.Errors.Add($"VPM could not install {step.DisplayName} {step.To}."); break; }
                    result.InstalledPackageIds.Add(step.Id);
                }
                if (result.Errors.Count == 0)
                {
                    EditorUtility.DisplayProgressBar("Installing packages", "Resolving project packages…", 0.92f);
                    if (!VPMProjectManifest.Resolve(VpmProjectCatalog.ProjectPath, Repos.GetAll)) result.Errors.Add("The VPM resolver could not finish resolving the project packages.");
                }
                if (result.Errors.Count == 0)
                {
                    // The project must have what was reviewed: the resolver may not pick other versions on its own.
                    var now = new UnityProject(VpmProjectCatalog.ProjectPath).GetInstalledVersions() ?? new Dictionary<string, string>();
                    foreach (var step in changes)
                        if (!now.TryGetValue(step.Id, out string version) || version != step.To)
                            result.Errors.Add($"{step.DisplayName} is {version ?? "not installed"} after installing, not the {step.To} the plan showed.");
                }
                if (result.Errors.Count == 0)
                {
                    UnityEditor.PackageManager.Client.Resolve();
                    AssetDatabase.Refresh();
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
                EditorUtility.ClearProgressBar();
                VpmDependencies.EndInstall();
            }
            return result;
        }
    }
}
