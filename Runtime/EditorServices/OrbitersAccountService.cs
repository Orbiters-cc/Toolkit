#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class OrbitersAccountService
{
    [Serializable] public sealed class Connection { public string state; public bool aiEnabled = true; }
    [Serializable] public sealed class Profile { public string username, avatarUrl; }
    public static Task<Connection> CheckAsync(string endpoint, string token, CancellationToken cancellation = default) =>
        OrbitersApi.SendAsync<Connection>(OrbitersEnvironment.ApiUrl(endpoint), token, cancellation: cancellation);
    public static Task<Profile> ProfileAsync(string user, string token, CancellationToken cancellation = default) =>
        OrbitersApi.SendAsync<Profile>(OrbitersEnvironment.ApiUrl("mcb/user") + "?u=" + Uri.EscapeDataString(user), token, cancellation: cancellation);

    // ---- The last answers, shared by every account row ----
    // Each inspector (rebuilt at every selection) shows them at once and asks the server again only once they are a few
    // minutes old. They survive script reloads (SessionState, by a hash of the account: never the token itself); the pictures
    // are kept in the Library folder.

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private const string SessionKey = "Orbiters.Account.Answers";

    [Serializable] private sealed class Answer { public string key, state, username, avatarUrl; public bool aiEnabled; public long at; }
    [Serializable] private sealed class Answers { public List<Answer> items = new List<Answer>(); }

    private static Dictionary<string, Answer> answers;
    private static readonly Dictionary<string, Texture2D> Pictures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

    /// <summary>The last state the tool's connection check answered for this account, or null; fresh while a few minutes old.</summary>
    public static Connection CachedConnection(string endpoint, string token, out bool fresh)
    {
        var answer = Find("connection|" + endpoint, token, out fresh);
        return answer != null ? new Connection { state = answer.state, aiEnabled = answer.aiEnabled } : null;
    }

    public static void Remember(string endpoint, string token, Connection connection)
    {
        if (connection == null) Forget("connection|" + endpoint, token);
        else Store("connection|" + endpoint, token, new Answer { state = connection.state, aiEnabled = connection.aiEnabled });
    }

    /// <summary>The account's last profile (name and picture), or null; fresh while a few minutes old.</summary>
    public static Profile CachedProfile(string token, out bool fresh)
    {
        var answer = Find("profile", token, out fresh);
        return answer != null ? new Profile { username = answer.username, avatarUrl = answer.avatarUrl } : null;
    }

    public static void Remember(string token, Profile profile)
    {
        if (profile != null) Store("profile", token, new Answer { username = profile.username, avatarUrl = profile.avatarUrl });
    }

    /// <summary>The picture at this address downloaded earlier (this session or a previous one), or null.</summary>
    public static Texture2D CachedPicture(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Pictures.TryGetValue(url, out var picture) && picture) return picture;
        string file = PicturePath(url);
        try { return File.Exists(file) ? Pictures[url] = Load(File.ReadAllBytes(file)) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Keeps a downloaded picture (in memory and in the Library folder) and returns it, or null when it isn't one.</summary>
    public static Texture2D RememberPicture(string url, byte[] bytes)
    {
        var picture = Load(bytes);
        if (!picture) return null;
        if (Pictures.TryGetValue(url, out var old) && old && old != picture) UnityEngine.Object.DestroyImmediate(old);
        Pictures[url] = picture;
        try
        {
            string file = PicturePath(url);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, bytes);
        }
        catch (Exception) { }
        return picture;
    }

    private static Texture2D Load(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Orbiters account picture" };
        if (texture.LoadImage(bytes)) return texture;
        UnityEngine.Object.DestroyImmediate(texture);
        return null;
    }

    private static string PicturePath(string url) => Path.Combine("Library", "OrbitersToolkit", "AccountPictures", Hash(url) + ".img");

    private static Answer Find(string kind, string token, out bool fresh)
    {
        fresh = false;
        if (string.IsNullOrEmpty(token) || !Load().TryGetValue(Key(kind, token), out var answer)) return null;
        fresh = DateTime.UtcNow - new DateTime(answer.at, DateTimeKind.Utc) < Fresh;
        return answer;
    }

    private static void Store(string kind, string token, Answer answer)
    {
        if (string.IsNullOrEmpty(token)) return;
        answer.key = Key(kind, token);
        answer.at = DateTime.UtcNow.Ticks;
        Load()[answer.key] = answer;
        Save();
    }

    private static void Forget(string kind, string token)
    {
        if (!string.IsNullOrEmpty(token) && Load().Remove(Key(kind, token))) Save();
    }

    // By server too: the development and production accounts have answers of their own.
    private static string Key(string kind, string token) => kind + "|" + Hash(OrbitersEnvironment.ApiUrl("") + "|" + token);

    private static Dictionary<string, Answer> Load()
    {
        if (answers != null) return answers;
        answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        try
        {
            var saved = JsonUtility.FromJson<Answers>(SessionState.GetString(SessionKey, ""));
            if (saved?.items != null) foreach (var answer in saved.items) if (!string.IsNullOrEmpty(answer?.key)) answers[answer.key] = answer;
        }
        catch (Exception) { }
        return answers;
    }

    private static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(new Answers { items = new List<Answer>(Load().Values) }));

    private static string Hash(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var text = new StringBuilder(32);
            for (int i = 0; i < 16; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }
    }
}
#endif
