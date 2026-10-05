using System;
using System.Collections.Generic;
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
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
                using (var response = await Client.GetAsync(uri, cancellation))
                {
                    if (!response.IsSuccessStatusCode) return packages;
                    packages = Parse(await response.Content.ReadAsStringAsync(), url);
                }
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
