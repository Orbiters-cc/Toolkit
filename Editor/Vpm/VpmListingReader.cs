using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.Vpm
{
    /// <summary>
    /// Reads a VPM repository listing (index.json) without adding it to the user's VPM settings, so a dependency plan can
    /// show every package and version before anything changes. Answers are kept for ten minutes.
    /// </summary>
    public static class VpmListingReader
    {
        private static readonly Dictionary<string, (DateTime at, List<VpmCandidate> packages)> Cache =
            new Dictionary<string, (DateTime, List<VpmCandidate>)>(StringComparer.OrdinalIgnoreCase);

        public static async Task<List<VpmCandidate>> ReadAsync(string url, CancellationToken cancellation = default)
        {
            lock (Cache)
                if (Cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.at < TimeSpan.FromMinutes(10)) return cached.packages;
            var packages = new List<VpmCandidate>();
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return packages;
                string json;
                // An error answer: nothing listed, and asked again next time.
                try { json = await OrbitersHttp.TextAsync(uri.AbsoluteUri, null, cancellation, OrbitersHttpKind.Listing); }
                catch (OrbitersHttpException error) when (!error.Offline) { return packages; }
                packages = await Task.Run(() => Parse(json, url), cancellation);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Debug.Log("[Orbiters] Could not read the VPM repository " + url + ": " + ex.Message);
                return packages;
            }
            lock (Cache) Cache[url] = (DateTime.UtcNow, packages);
            return packages;
        }

        /// <summary>Every version of every package in a listing, newest first per package.</summary>
        public static List<VpmCandidate> Parse(string json, string url)
        {
            var result = new List<VpmCandidate>();
            var listing = JObject.Parse(json);
            if (!(listing["packages"] is JObject packages)) return result;
            foreach (var package in packages.Properties())
            {
                if (!(package.Value?["versions"] is JObject versions)) continue;
                foreach (var version in versions.Properties())
                {
                    if (!(version.Value is JObject manifest)) continue;
                    var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (manifest["vpmDependencies"] is JObject deps)
                        foreach (var dep in deps.Properties()) dependencies[dep.Name] = dep.Value?.ToString();
                    result.Add(new VpmCandidate { Id = package.Name, Version = version.Name, RepositoryUrl = url,
                        DisplayName = manifest.Value<string>("displayName") ?? package.Name, Dependencies = dependencies });
                }
            }
            return VpmCandidates.Newest(result);
        }
    }

    /// <summary>
    /// The VPM repositories this computer's Creator Companion has read before, from its cache (also repositories removed
    /// since, and community ones): where a package installed from a repository no longer listed in its settings came
    /// from. Only the URL is taken from the cache; the versions are read from the repository itself.
    /// </summary>
    public static class VpmRepositoryHistory
    {
        /// <summary>The Creator Companion's repository cache (%LOCALAPPDATA%\VRChatCreatorCompanion\Repos).</summary>
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRChatCreatorCompanion", "Repos");

        /// <summary>For each package id, the https URLs of the cached listings offering it (the most recently read first).</summary>
        public static Dictionary<string, List<string>> Offering(IEnumerable<string> ids, string folder = null)
        {
            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            folder ??= Folder;
            if (wanted.Count == 0 || !Directory.Exists(folder)) return found;
            foreach (var file in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                try
                {
                    var listing = JObject.Parse(File.ReadAllText(file.FullName));
                    // VCC keeps { "repo": listing, ... }; a bare listing is read the same.
                    var repo = listing["repo"] as JObject ?? listing;
                    string url = repo.Value<string>("url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !(repo["packages"] is JObject packages)) continue;
                    foreach (var package in packages.Properties())
                    {
                        if (!wanted.Contains(package.Name)) continue;
                        if (!found.TryGetValue(package.Name, out var urls)) found[package.Name] = urls = new List<string>();
                        if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException) { }
            }
            return found;
        }
    }

    public static class VpmCandidates
    {
        /// <summary>Candidates grouped by package, newest version first (loose semantic versions).</summary>
        public static List<VpmCandidate> Newest(IEnumerable<VpmCandidate> candidates) =>
            candidates.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id) && Parse(c.Version) != null)
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.GroupBy(c => c.Version).Select(v => v.First()).OrderByDescending(c => Parse(c.Version)))
                .ToList();

        private static SemanticVersioning.Version Parse(string version)
        {
            try { return new SemanticVersioning.Version(version, true); }
            catch (Exception) { return null; }
        }
    }
}
