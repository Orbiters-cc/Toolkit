#if UNITY_EDITOR
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

[assembly: InternalsVisibleTo("Orbiters.Toolkit.Editor.Tests")]

/// <summary>
/// Shared Orbiters account, saved in the Unity preferences folder for the signed-in OS user only: encrypted with DPAPI on
/// Windows, in a file only that user can read elsewhere.
/// </summary>
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
    /// <summary>Tests point this at a temporary folder so they never touch the real sign-in.</summary>
    internal static string FolderOverride;
    // Application-specific DPAPI entropy: another program unprotecting this user's data also needs it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbiters.Toolkit account");

    /// <summary>Stores the account a Unity tool signed in with (see <see cref="OrbitersBrowserLogin"/>).</summary>
    public static void SaveAuth(AuthData auth, bool? forceIsDev = null)
    {
        Write(GetAuthFilePath(forceIsDev), auth);
        DeleteReversibleCopies(Folder);
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
    public static AuthData GetAuthForEnv(bool isDev) => Read(GetAuthFilePath(isDev));
    public static bool RemoveAuth()
    {
        try { File.Delete(GetAuthFilePath()); DeleteReversibleCopies(Folder); Changed?.Invoke(); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static string GetAuthFilePath(bool? forceIsDev = null) =>
        Path.Combine(Folder, (forceIsDev ?? OrbitersEnvironment.IsDevelopment) ? "account_dev.dat" : "account.dat");

    private static string Folder => FolderOverride ?? Path.Combine(InternalEditorUtility.unityPreferencesFolder, "MCB");

    internal static void Write(string path, AuthData auth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(auth));
        try
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                File.WriteAllBytes(path, Dpapi(json, protect: true));
                return;
            }
            // Made private while still empty, so the account is never in a file others can read.
            File.WriteAllBytes(path, Array.Empty<byte>());
            MakePrivate(path);
            File.WriteAllBytes(path, json);
        }
        finally { Array.Clear(json, 0, json.Length); }
    }

    internal static AuthData Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            if (Application.platform == RuntimePlatform.WindowsEditor) data = Dpapi(data, protect: false);
            try { return JsonConvert.DeserializeObject<AuthData>(Encoding.UTF8.GetString(data)); }
            finally { Array.Clear(data, 0, data.Length); }
        }
        catch (Exception) { return null; }
    }

    // Earlier versions kept the account in auth.dat, only XOR-obfuscated with a key anyone can read: not migrated, and
    // deleted when this version signs in or out (not on every load: projects still on an older Toolkit share this folder,
    // and their users would be signed out at each script reload).
    internal static void DeleteReversibleCopies(string folder)
    {
        foreach (string name in new[] { "auth.dat", "auth_dev.dat" })
        {
            try { File.Delete(Path.Combine(folder, name)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // chmod 600: only this user can read or write the file. Run in its folder, so only the file name is on the command line.
    private static void MakePrivate(string path)
    {
        var start = new ProcessStartInfo("/bin/chmod", "600 \"" + Path.GetFileName(path) + "\"")
        {
            WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false, CreateNoWindow = true,
        };
        using (var chmod = Process.Start(start))
        {
            chmod.WaitForExit();
            if (chmod.ExitCode != 0) throw new IOException("Could not make the Orbiters account file private.");
        }
    }

    // DPAPI for the current Windows user (System.Security.Cryptography.ProtectedData is not in Unity's API profile).
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int size;
        public IntPtr data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = default(DataBlob);
        try
        {
            var input = new DataBlob { size = data.Length, data = dataHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { size = Entropy.Length, data = entropyHandle.AddrOfPinnedObject() };
            bool done = protect
                ? CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!done) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.size];
            Marshal.Copy(output.data, result, 0, output.size);
            return result;
        }
        finally
        {
            if (output.data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.size], 0, output.data, output.size);
                LocalFree(output.data);
            }
            dataHandle.Free();
            entropyHandle.Free();
        }
    }
}
#endif
