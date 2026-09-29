using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class VpmDependencyStatus
    {
        public string Id;
        public string DisplayName;
        public string VersionRange;
        public string RepositoryUrl;
        public string Reason;
        public bool IsRequired;
        public bool IsInstalled;
        public string InstalledVersion;
        public bool IsAssumedInstalled;
        public bool IsRepositoryConfigured;
        public bool IsPackageAvailable;
        public string Error;
    }

    public sealed class VpmDependencyInstallResult
    {
        public bool Success;
        public List<string> InstalledPackageIds = new List<string>();
        public List<string> AddedRepositoryUrls = new List<string>();
        public List<string> Errors = new List<string>();

        public string ErrorMessage => Errors == null || Errors.Count == 0 ? null : string.Join("\n", Errors);
    }

    /// <summary>
    /// VPM dependencies of one package, read from its package.json: <c>vpmDependencies</c> (required) and the <c>"orbiters"</c>
    /// block (<c>repositories</c>, <c>dependencyDisplayNames</c>, <c>optionalVpmDependencies</c> with displayName/version/reason).
    /// Installs add the listed repositories to the user's VPM settings when missing, then resolve the project.
    /// </summary>
    public sealed class VpmDependencies
    {
        private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(60);
        private static readonly Dictionary<string, VpmDependencies> instances = new Dictionary<string, VpmDependencies>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, (VpmDependencyStatus status, DateTime at)> statusCache =
            new Dictionary<string, (VpmDependencyStatus, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private Manifest manifest;
        private DateTime manifestLoadedAt;

        public string PackageName { get; }
        /// <summary>Optional dependencies this returns true for count as installed (e.g. packages developed locally outside VPM).</summary>
        public Func<string, bool> AssumeOptionalInstalled { get; set; }

        /// <summary>True while any install runs; only one runs at a time.</summary>
        public static bool IsInstalling { get; private set; }
        /// <summary>Raised when an install starts or ends and when cached statuses are dropped.</summary>
        public static event Action StatusChanged;

        private VpmDependencies(string packageName) { PackageName = packageName; }

        public static VpmDependencies For(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) throw new ArgumentException("A package name is required.", nameof(packageName));
            if (!instances.TryGetValue(packageName, out var instance)) instances[packageName] = instance = new VpmDependencies(packageName);
            return instance;
        }

        /// <summary>The first local package whose package.json lists <paramref name="packageId"/> as an optional dependency, or null.</summary>
        public static VpmDependencies Declaring(string packageId)
        {
            foreach (var existing in instances.Values)
                if (existing.Declares(packageId)) return existing;
            foreach (var info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (info.source != UnityEditor.PackageManager.PackageSource.Embedded && info.source != UnityEditor.PackageManager.PackageSource.Local) continue;
                if (!File.Exists(Path.Combine(info.resolvedPath, "package.json"))) continue;
                var candidate = For(info.name);
                if (candidate.Declares(packageId)) return candidate;
            }
            return null;
        }

        public bool Declares(string packageId) =>
            !string.IsNullOrWhiteSpace(packageId) && LoadManifest().orbiters?.optionalVpmDependencies?.ContainsKey(packageId) == true;

        public List<VpmDependencyStatus> RequiredStatuses() =>
            (LoadManifest().vpmDependencies ?? new Dictionary<string, string>())
                .Select(pair => Cached("required:" + pair.Key + ":" + pair.Value, false, () => BuildStatus(pair.Key, pair.Value, true, null)))
                .ToList();

        public List<VpmDependencyStatus> MissingRequired() => RequiredStatuses().Where(s => s != null && !s.IsInstalled).ToList();

        public bool HasMissingRequired => MissingRequired().Count > 0;

        /// <summary>Status of an optional dependency declared in this package's metadata, null when it is not declared.</summary>
        public VpmDependencyStatus OptionalStatus(string packageId, bool refresh = false)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return null;
            var optionals = LoadManifest().orbiters?.optionalVpmDependencies;
            if (optionals == null || !optionals.TryGetValue(packageId, out var optional)) return null;
            string version = optional?.version;
            return Cached("optional:" + packageId + ":" + version, refresh, () => BuildStatus(packageId, version, false, optional));
        }

        public VpmDependencyInstallResult InstallMissingRequired() => Install(MissingRequired());

        public VpmDependencyInstallResult InstallOptional(string packageId)
        {
            var status = OptionalStatus(packageId, true);
            if (status == null) return new VpmDependencyInstallResult { Errors = { "No optional dependency metadata for " + packageId + " in " + PackageName + "." } };
            return status.IsInstalled ? new VpmDependencyInstallResult { Success = true } : Install(new[] { status });
        }

        /// <summary>Installs the given dependencies synchronously with a progress bar. Unity reloads scripts afterwards.</summary>
        public static VpmDependencyInstallResult Install(IEnumerable<VpmDependencyStatus> statuses)
        {
            var result = new VpmDependencyInstallResult();
            var dependencies = (statuses ?? Enumerable.Empty<VpmDependencyStatus>())
                .Where(s => s != null && !s.IsInstalled)
                .GroupBy(s => s.Id).Select(g => g.First())
                .ToList();
            if (dependencies.Count == 0) { result.Success = true; return result; }
            if (IsInstalling) { result.Errors.Add("A package install is already running."); return result; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                result.Errors.Add("Unity is compiling or importing assets. Wait for it to finish, then try again.");
                return result;
            }

            IsInstalling = true;
            StatusChanged?.Invoke();
            try
            {
                AddMissingRepositories(dependencies, result);
                foreach (var provider in Repos.GetAll) (provider as VPMPackageProvider)?.Refresh();

                string projectPath = ProjectPath;
                var project = new UnityProject(projectPath);
                for (int i = 0; i < dependencies.Count; i++)
                {
                    var dependency = dependencies[i];
                    float progress = Mathf.Lerp(0.35f, 0.8f, dependencies.Count == 1 ? 1f : i / (float)(dependencies.Count - 1));
                    EditorUtility.DisplayProgressBar("Installing packages", "Installing " + dependency.DisplayName + "...", progress);
                    string range = dependency.VersionRange ?? string.Empty;
                    if (AvailablePackage(dependency.Id, range) == null)
                    {
                        result.Errors.Add("Could not find " + dependency.DisplayName + " in the configured VPM repositories.");
                        continue;
                    }
                    if (!project.AddVPMPackage(dependency.Id, range, Repos.GetAll))
                    {
                        result.Errors.Add("VPM could not install " + dependency.DisplayName + ".");
                        continue;
                    }
                    result.InstalledPackageIds.Add(dependency.Id);
                }

                if (result.Errors.Count == 0)
                {
                    EditorUtility.DisplayProgressBar("Installing packages", "Resolving project packages...", 0.9f);
                    if (!VPMProjectManifest.Resolve(projectPath, Repos.GetAll)) result.Errors.Add("The VPM resolver could not finish resolving the project packages.");
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
                result.Success = false;
                result.Errors.Add(ex.Message);
                Debug.LogError("[Orbiters] Package installation failed: " + ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                IsInstalling = false;
                Invalidate();
            }
            return result;
        }

        /// <summary>Drops every cached status (all packages) and raises <see cref="StatusChanged"/>.</summary>
        public static void Invalidate()
        {
            foreach (var instance in instances.Values) { instance.statusCache.Clear(); instance.manifest = null; }
            StatusChanged?.Invoke();
        }

        /// <summary>"Install VRCFury", "Install A and B", "Install A, B and C".</summary>
        public static string InstallLabel(IReadOnlyList<VpmDependencyStatus> dependencies)
        {
            var names = (dependencies ?? Array.Empty<VpmDependencyStatus>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.DisplayName))
                .Select(s => s.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0) return "Install dependencies";
            if (names.Count == 1) return "Install " + names[0];
            return "Install " + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        private static void AddMissingRepositories(IEnumerable<VpmDependencyStatus> dependencies, VpmDependencyInstallResult result)
        {
            var urls = dependencies.Select(s => s.RepositoryUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                EditorUtility.DisplayProgressBar("Installing packages", "Adding VPM repository...", 0.1f + 0.2f * ((i + 1f) / urls.Count));
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { result.Errors.Add("Invalid VPM repository URL: " + url); continue; }
                if (Repos.UserRepoExists(uri)) continue;
                if (!Repos.AddRepo(uri, new Dictionary<string, string>())) { result.Errors.Add("VPM could not add repository: " + url); continue; }
                result.AddedRepositoryUrls.Add(url);
            }
        }

        private VpmDependencyStatus BuildStatus(string packageId, string versionRange, bool required, OptionalDependency optional)
        {
            var status = new VpmDependencyStatus
            {
                Id = packageId,
                VersionRange = versionRange,
                RepositoryUrl = RepositoryUrl(packageId),
                IsRequired = required,
                Reason = optional?.reason,
                DisplayName = DisplayName(packageId, optional)
            };

            if (!required && AssumeOptionalInstalled != null && AssumeOptionalInstalled(packageId))
            {
                status.IsInstalled = status.IsAssumedInstalled = status.IsRepositoryConfigured = status.IsPackageAvailable = true;
                status.InstalledVersion = "assumed installed";
                return status;
            }

            try
            {
                var provider = new UnityProject(ProjectPath).VPMProvider;
                provider.Refresh();
                var installed = provider.GetPackage(packageId);
                var matching = string.IsNullOrWhiteSpace(versionRange) ? installed : provider.GetPackageWithRange(packageId, versionRange);
                status.InstalledVersion = installed?.Version;
                status.IsInstalled = matching != null;
                if (installed != null && matching == null) status.Error = "Installed version " + installed.Version + " does not satisfy " + versionRange + ".";
            }
            catch (Exception ex)
            {
                status.Error = ex.Message;
            }
            if (!status.IsInstalled && LocalDevelopmentCopy(packageId, out string developed))
            {
                // A package developed in this project (a git clone in Packages/) is not tracked by VPM, and must not be:
                // resolving it would replace the clone. It counts as installed whatever its version.
                status.IsInstalled = true;
                status.InstalledVersion = developed + " (local development copy)";
                status.Error = null;
            }
            if (status.IsInstalled) return status;

            try
            {
                if (!string.IsNullOrWhiteSpace(status.RepositoryUrl) && Uri.TryCreate(status.RepositoryUrl, UriKind.Absolute, out var uri))
                    status.IsRepositoryConfigured = Repos.UserRepoExists(uri);
                var available = AvailablePackage(packageId, versionRange ?? string.Empty);
                status.IsPackageAvailable = available != null;
                if (available != null && status.DisplayName == Humanize(packageId) && !string.IsNullOrWhiteSpace(available.Title)) status.DisplayName = available.Title;
            }
            catch
            {
                status.IsPackageAvailable = false;
            }
            return status;
        }

        private static IVRCPackage AvailablePackage(string packageId, string versionRange) =>
            string.IsNullOrWhiteSpace(versionRange) ? Repos.GetLatestPackage(packageId) : Repos.GetPackageWithVersionMatch(packageId, versionRange);

        private string DisplayName(string packageId, OptionalDependency optional)
        {
            if (!string.IsNullOrWhiteSpace(optional?.displayName)) return optional.displayName;
            var names = LoadManifest().orbiters?.dependencyDisplayNames;
            if (names != null && names.TryGetValue(packageId, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
            return Humanize(packageId);
        }

        private string RepositoryUrl(string packageId) =>
            LoadManifest().orbiters?.repositories?
                .FirstOrDefault(r => r != null && !string.IsNullOrWhiteSpace(r.url) && r.packages != null &&
                                     r.packages.Any(id => string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase)))?.url;

        private Manifest LoadManifest()
        {
            if (manifest != null && (DateTime.UtcNow - manifestLoadedAt).TotalSeconds < 10) return manifest;
            try
            {
                // Wherever Unity resolved the package: embedded, cache, or a local folder outside the project.
                string root = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName)?.resolvedPath;
                if (string.IsNullOrEmpty(root)) throw new DirectoryNotFoundException(PackageName + " is not a registered package.");
                string json = File.ReadAllText(Path.Combine(root, "package.json"));
                manifest = JsonConvert.DeserializeObject<Manifest>(json) ?? new Manifest();
            }
            catch (Exception ex)
            {
                Debug.LogError("[Orbiters] Could not read the VPM dependencies of " + PackageName + ": " + ex.Message);
                manifest = new Manifest();
            }
            manifestLoadedAt = DateTime.UtcNow;
            return manifest;
        }

        private VpmDependencyStatus Cached(string key, bool refresh, Func<VpmDependencyStatus> build)
        {
            if (!refresh && statusCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.at < StatusCacheDuration) return cached.status;
            var status = build();
            statusCache[key] = (status, DateTime.UtcNow);
            return status;
        }

        private static string ProjectPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// True when <paramref name="packageId"/> is a git clone in the project's Packages folder (a package being developed
        /// here). VPM never installs a .git folder, so installed copies are not affected.
        /// </summary>
        internal static bool LocalDevelopmentCopy(string packageId, out string version)
        {
            version = null;
            string packages = Path.Combine(ProjectPath, "Packages");
            if (!Directory.Exists(packages)) return false;
            foreach (string folder in Directory.GetDirectories(packages))
            {
                string manifestPath = Path.Combine(folder, "package.json");
                if (!File.Exists(manifestPath)) continue;
                if (!Directory.Exists(Path.Combine(folder, ".git")) && !File.Exists(Path.Combine(folder, ".git"))) continue;
                try
                {
                    var package = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(manifestPath));
                    if (package == null || !package.TryGetValue("name", out var name) || !string.Equals(name as string, packageId, StringComparison.OrdinalIgnoreCase)) continue;
                    version = package.TryGetValue("version", out var v) ? v as string : null;
                    return true;
                }
                catch (JsonException)
                {
                }
            }
            return false;
        }

        private static string Humanize(string packageId)
        {
            string last = packageId?.Split('.').LastOrDefault();
            if (string.IsNullOrWhiteSpace(last)) last = string.IsNullOrWhiteSpace(packageId) ? "dependency" : packageId;
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(last.Replace("-", " ").Replace("_", " "));
        }

        private sealed class Manifest
        {
            [JsonProperty("vpmDependencies")] public Dictionary<string, string> vpmDependencies = new Dictionary<string, string>();
            [JsonProperty("orbiters")] public Metadata orbiters = new Metadata();
        }

        private sealed class Metadata
        {
            [JsonProperty("dependencyDisplayNames")] public Dictionary<string, string> dependencyDisplayNames = new Dictionary<string, string>();
            [JsonProperty("repositories")] public List<Repository> repositories = new List<Repository>();
            [JsonProperty("optionalVpmDependencies")] public Dictionary<string, OptionalDependency> optionalVpmDependencies = new Dictionary<string, OptionalDependency>();
        }

        private sealed class Repository
        {
            [JsonProperty("url")] public string url;
            [JsonProperty("packages")] public List<string> packages = new List<string>();
        }

        private sealed class OptionalDependency
        {
            [JsonProperty("version")] public string version;
            [JsonProperty("displayName")] public string displayName;
            [JsonProperty("reason")] public string reason;
        }
    }
}
