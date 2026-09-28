#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;

/// <summary>Shared Orbiters account. Uses the existing Unity preferences account store.</summary>
public static class AuthenticationService
{
    [JsonObject(MemberSerialization.OptIn)]
    public class AuthData
    {
        [JsonProperty] public string token;
        [JsonProperty] public string user;
        [JsonProperty] public string username;
        [JsonProperty] public string avatarUrl;
    }

    public static event Action Changed;
    /// <summary>Stores the account a Unity tool signed in with (see <see cref="OrbitersBrowserLogin"/>).</summary>
    public static void SaveAuth(AuthData auth, bool? forceIsDev = null)
    {
        string path = GetAuthFilePath(forceIsDev);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // Keep the shared store format. This is obfuscation, not a credential vault.
        File.WriteAllBytes(path, Transform(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(auth))));
        Changed?.Invoke();
    }
    /// <summary>Magic Sync: reads a website token from the clipboard. Kept for MCB's per-environment developer tools.</summary>
    public static async Task<bool> RegisterAuthForEnv(bool isDev)
    {
        var match = Regex.Match(EditorGUIUtility.systemCopyBuffer ?? "", @"orbit-\w{8}-\w{8}-\w{8}-\d{2}-\d{2}-\d{4}");
        string token = match.Success ? match.Value : "notoken";
        string url = OrbitersEnvironment.ApiUrl("mcb/token", isDev) + "?token=" + Uri.EscapeDataString(token);
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                using var response = await OrbitersApi.Client.GetAsync(url);
                if ((int)response.StatusCode == 425) { await Task.Delay(1000); continue; }
                if (!response.IsSuccessStatusCode) return false;
                var auth = JsonConvert.DeserializeObject<AuthData>(await response.Content.ReadAsStringAsync());
                if (string.IsNullOrWhiteSpace(auth?.token)) return false;
                SaveAuth(auth, isDev);
                return true;
            }
        }
        catch (Exception) { /* Callers show actionable inline authentication feedback. Never log tokens. */ }
        return false;
    }
    public static AuthData GetAuth() => GetAuthForEnv(OrbitersEnvironment.IsDevelopment);
    public static AuthData GetAuthForEnv(bool isDev)
    {
        try
        {
            var path = GetAuthFilePath(isDev);
            return File.Exists(path) ? JsonConvert.DeserializeObject<AuthData>(Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path)))) : null;
        }
        catch (Exception) { return null; }
    }
    public static bool RemoveAuth()
    {
        try { File.Delete(GetAuthFilePath()); Changed?.Invoke(); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static string GetAuthFilePath(bool? forceIsDev = null) => Path.Combine(
        InternalEditorUtility.unityPreferencesFolder, "MCB", (forceIsDev ?? OrbitersEnvironment.IsDevelopment) ? "auth_dev.dat" : "auth.dat");
    private static byte[] Transform(byte[] data)
    {
        byte[] key = Encoding.UTF8.GetBytes("MCBMagicSync");
        for (int i = 0; i < data.Length; i++) data[i] ^= key[i % key.Length];
        return data;
    }
}
#endif
