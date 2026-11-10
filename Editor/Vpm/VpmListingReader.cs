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
    /// show every package and version before anything changes. Answers are kept for ten minutes in memory, and each
    /// listing's last copy on disk (<see cref="ListingStore"/>): the repository is asked whether it changed since
    /// (If-None-Match / If-Modified-Since) and only a changed listing is downloaded again.
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
                var stored = await Task.Run(() => ListingStore.Read(url), cancellation);
                OrbitersConditionalText answer;
                // An error answer: nothing listed, and asked again next time.
                try { answer = await OrbitersHttp.ConditionalTextAsync(uri.AbsoluteUri, stored?.etag, stored?.lastModified, cancellation); }
                catch (OrbitersHttpException error) when (!error.Offline) { return packages; }
                if (answer.NotModified && stored == null) return packages;
                packages = await Task.Run(() =>
                {
                    if (!answer.NotModified)
                    {
                        var parsed = Parse(answer.Text, url);
                        if (answer.ETag != null || answer.LastModified != null) ListingStore.Write(url, answer.Text, answer.ETag, answer.LastModified);
                        else ListingStore.Forget(url);
                        return parsed;
                    }
                    // The copy that stood unchanged; one that no longer reads is forgotten, and downloaded whole next time.
                    try { return Parse(stored.body, url); }
                    catch (Exception) { ListingStore.Forget(url); throw; }
                }, cancellation);
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
    /// The last copy of each VPM listing read, with the validators its server sent (ETag, Last-Modified), one file per
    /// listing in the user's local application data (listings are the same for every project). A copy that cannot be
    /// read, or that is another listing's, counts as none.
    /// </summary>
    internal static class ListingStore
    {
        [Serializable]
        internal sealed class Entry
        {
            public string url, etag, lastModified, body;
        }

        private static string folder;

        internal static string Folder
        {
            get => folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiters", "VpmListings");
            // Tests: another folder (null: the user's).
            set => folder = value;
        }

        private static string FileOf(string url) => Path.Combine(Folder, Hashing.Sha256(url.ToLowerInvariant()).Substring(0, 32) + ".json");

        public static Entry Read(string url)
        {
            try
            {
                string file = FileOf(url);
                if (!File.Exists(file)) return null;
                var entry = JsonUtility.FromJson<Entry>(File.ReadAllText(file));
                return entry != null && string.Equals(entry.url, url, StringComparison.OrdinalIgnoreCase) && entry.body != null &&
                       (!string.IsNullOrEmpty(entry.etag) || !string.IsNullOrEmpty(entry.lastModified)) ? entry : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return null; }
        }

        public static void Write(string url, string body, string etag, string lastModified)
        {
            try { Orbiters.Toolkit.Storage.SafePaths.ReplaceFile(FileOf(url), JsonUtility.ToJson(new Entry { url = url, etag = etag, lastModified = lastModified, body = body })); }
            // Without a copy the listing is only downloaded whole next time.
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        public static void Forget(string url)
        {
            try { File.Delete(FileOf(url)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
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
