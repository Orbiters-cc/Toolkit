using System;

namespace Orbiters.Toolkit.Versions
{
    /// <summary>
    /// What every Orbiters tool shows about a published version, whatever else it carries: MCB's custom base versions
    /// (patch files, sources) and My Avatar's gallery releases (packages per platform and base) both expose this to the
    /// shared version timeline.
    /// </summary>
    public interface IVersionRecord
    {
        /// <summary>Server id; 0 for a local, unsubmitted version.</summary>
        int Id { get; }
        string Version { get; }
        string Title { get; }
        /// <summary>"public", "beta", "alpha" (<see cref="VersionScopes"/>), or "unknown".</summary>
        string Scope { get; }
        /// <summary>Release date as the server wrote it (ISO date or timestamp); may be empty.</summary>
        string Date { get; }
        string Changelog { get; }
        int UploaderId { get; }
        string CreatorName { get; }
        bool? CreatorTrusted { get; }
    }

    /// <summary>A plain <see cref="IVersionRecord"/>.</summary>
    [Serializable]
    public class VersionRecord : IVersionRecord
    {
        public int id;
        public string version, title, scope = VersionScopes.Public, date, changelog, creatorName;
        public int uploaderId;
        public bool? creatorTrusted;

        public int Id => id;
        public string Version => version;
        public string Title => title;
        public string Scope => scope;
        public string Date => date;
        public string Changelog => changelog;
        public int UploaderId => uploaderId;
        public string CreatorName => creatorName;
        public bool? CreatorTrusted => creatorTrusted;
    }

    /// <summary>Release scopes, their order (public reaches everyone, alpha the fewest people) and their colours.</summary>
    public static class VersionScopes
    {
        public const string Public = "public", Beta = "beta", Alpha = "alpha", Unknown = "unknown";
        public static readonly string[] All = { Public, Beta, Alpha };

        public static string Normalize(string scope)
        {
            string value = (scope ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(All, value) >= 0 ? value : Unknown;
        }

        public static string Label(string scope)
        {
            switch (Normalize(scope))
            {
                case Public: return "Public";
                case Beta: return "Beta";
                case Alpha: return "Alpha";
                default: return "Unknown";
            }
        }

        /// <summary>Who sees a version of this scope.</summary>
        public static string Audience(string scope)
        {
            switch (Normalize(scope))
            {
                case Public: return "Everyone with access to the asset.";
                case Beta: return "Beta and alpha testers only.";
                case Alpha: return "Alpha testers only.";
                default: return "";
            }
        }
    }
}
