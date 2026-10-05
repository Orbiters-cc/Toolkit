using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor.VRChat.Refit
{
    /// <summary>What Orbiters knows about an avatar's body model file.</summary>
    public sealed class BaseIdentity
    {
        /// <summary>"custom" (a custom base version's output), "original" (an avatar base's own file) or "none".</summary>
        public string Kind;
        /// <summary>The avatar base (Orbiters' registry): set for custom and original files alike; 0 when unknown.</summary>
        public int AvatarBaseId;
        public string BaseName;
        /// <summary>Custom bases: the custom base asset, its name, version and the blendshapes it adds.</summary>
        public int AssetId;
        public string AssetName, Version;
        public List<string> Shapes = new List<string>();
        public string Hash;

        public bool Known => AvatarBaseId > 0;
        public bool Custom => Kind == "custom";
    }

    /// <summary>
    /// Recognises the avatar base of a body by its model file: the SHA-256 of the body's model file is looked up on the
    /// Orbiters server, which knows the original files of every registered avatar base and the outputs of every released
    /// custom base version. Works for a stock base (an original Rexouium) as well as a custom one. Hashes and answers are
    /// cached in the project's Library folder; files are read on a worker thread.
    /// </summary>
    public static class BaseFingerprint
    {
        private const string CachePath = "Library/Orbiters/base-identify.json";
        private static readonly TimeSpan AnswerLifetime = TimeSpan.FromHours(12);
        private const long MaxFileBytes = 1L << 30;

        [Serializable] private sealed class Cache { public List<FileHash> files = new List<FileHash>(); public List<Answer> answers = new List<Answer>(); }
        [Serializable] private sealed class FileHash { public string path; public long length, modified; public string hash; }
        [Serializable] private sealed class Answer { public string hash; public long checkedAt; public string kind; public int assetId, baseId; public string assetName, version, baseName; public List<string> shapes = new List<string>(); }

        // Server response (POST mcb/custom-bases/identify).
        private sealed class Response { public List<Match> matches = new List<Match>(); }
        private sealed class Match
        {
            public string hash, kind, assetName, version;
            public int? assetId;
            public NamedBase avatarBase;
            public List<string> customBlendshapes = new List<string>();
        }
        private sealed class NamedBase { public int id; public string name; }

        private static Cache cache;
        private static readonly SemaphoreSlim CacheLock = new SemaphoreSlim(1, 1);

        /// <summary>The body's model file as Orbiters knows it, or null (not under Assets, unreadable, offline).</summary>
        public static async Task<BaseIdentity> IdentifyAsync(SkinnedMeshRenderer body, CancellationToken cancellation)
        {
            if (body == null || body.sharedMesh == null) return null;
            string meshPath = AssetDatabase.GetAssetPath(body.sharedMesh);
            if (string.IsNullOrEmpty(meshPath) || !meshPath.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            string fullPath = Path.GetFullPath(meshPath);
            string token = AuthenticationService.GetAuth()?.token;
            string url = OrbitersEnvironment.ApiUrl("mcb/custom-bases/identify");
            var answer = await Task.Run(() => LookUpAsync(fullPath, url, token, cancellation), cancellation);
            if (answer == null) return null;
            return new BaseIdentity
            {
                Kind = answer.kind == "custom" ? "custom" : answer.kind == "default" ? "original" : "none",
                AvatarBaseId = answer.baseId, BaseName = answer.baseName, AssetId = answer.assetId, AssetName = answer.assetName,
                Version = answer.version, Shapes = answer.shapes ?? new List<string>(), Hash = answer.hash,
            };
        }

        private static async Task<Answer> LookUpAsync(string fullPath, string url, string token, CancellationToken cancellation)
        {
            string hash = await HashAsync(fullPath, cancellation);
            if (hash == null) return null;
            long now = DateTime.UtcNow.Ticks;
            await CacheLock.WaitAsync(cancellation);
            try
            {
                var known = Load().answers.FirstOrDefault(a => a.hash == hash);
                if (known != null && now - known.checkedAt < AnswerLifetime.Ticks) return known;
            }
            finally { CacheLock.Release(); }

            Response response;
            try { response = await OrbitersApi.SendAsync<Response>(url, token, new { hashes = new[] { hash } }, cancellation); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Offline or the server does not know the endpoint: nothing recognised, asked again next time.
                Debug.Log("[Orbiters] Could not look up the avatar's base: " + ex.Message);
                return null;
            }
            var match = response?.matches?.FirstOrDefault(m => m.hash == hash && m.kind == "custom") ??
                        response?.matches?.FirstOrDefault(m => m.hash == hash);
            var answer = new Answer
            {
                hash = hash, checkedAt = now, kind = match?.kind ?? "none", assetId = match?.assetId ?? 0, assetName = match?.assetName,
                version = match?.version, baseId = match?.avatarBase?.id ?? 0, baseName = match?.avatarBase?.name,
                shapes = match?.customBlendshapes ?? new List<string>(),
            };
            await CacheLock.WaitAsync(cancellation);
            try
            {
                var data = Load();
                data.answers.RemoveAll(a => a.hash == hash);
                data.answers.Add(answer);
                Save(data);
            }
            finally { CacheLock.Release(); }
            return answer;
        }

        // SHA-256 of the file, lowercase hex, remembered by path, size and modification time.
        private static async Task<string> HashAsync(string fullPath, CancellationToken cancellation)
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return null;
            long modified = info.LastWriteTimeUtc.Ticks;
            await CacheLock.WaitAsync(cancellation);
            try
            {
                var known = Load().files.FirstOrDefault(f => f.path == fullPath);
                if (known != null && known.length == info.Length && known.modified == modified) return known.hash;
            }
            finally { CacheLock.Release(); }
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            cancellation.ThrowIfCancellationRequested();
            await CacheLock.WaitAsync(cancellation);
            try
            {
                var data = Load();
                data.files.RemoveAll(f => f.path == fullPath);
                data.files.Add(new FileHash { path = fullPath, length = info.Length, modified = modified, hash = hash });
                Save(data);
            }
            finally { CacheLock.Release(); }
            return hash;
        }

        private static Cache Load()
        {
            if (cache != null) return cache;
            try { cache = File.Exists(CachePath) ? JsonUtility.FromJson<Cache>(File.ReadAllText(CachePath)) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { cache = null; }
            return cache ??= new Cache();
        }

        private static void Save(Cache data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, JsonUtility.ToJson(data));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
