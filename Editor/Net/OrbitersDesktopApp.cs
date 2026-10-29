using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Orbiters.Toolkit.Editor.Net
{
    /// <summary>
    /// Orbiters - N4N0 on this computer: whether it is installed and runs, starting it in the tray, and installing its
    /// latest release then opening it, for tools that need it (N4N0 on an avatar does nothing without it; My World hands
    /// it a world's captures). The release comes from the feed the app itself updates from (`&lt;feed&gt;/stable.json`, the
    /// development server's feed in the development environment); its setup is checked against the release's SHA-256
    /// before it runs, silently, then the app is started.
    /// </summary>
    public static class OrbitersDesktopApp
    {
        private const string ProductionFeed = "https://orbiters.cc/downloads/desktop";

        /// <summary>A build of Orbiters - N4N0 to use instead of the installed one (tests, a development build), or null.</summary>
        public static string Override;

        /// <summary>The installed app's program (its install folder from its uninstall entry, else the default one), or null.</summary>
        public static string Installed
        {
            get
            {
                if (!string.IsNullOrEmpty(Override) && File.Exists(Override)) return Override;
                string location = null;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\OrbitersDesktop"))
                        location = key?.GetValue("InstallLocation") as string;
                }
                catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { location = null; }
                if (string.IsNullOrEmpty(location))
                    location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Orbiters - N4N0");
                string exe = Path.Combine(location.Trim('"'), "orbiters-desktop.exe");
                return File.Exists(exe) ? exe : null;
            }
        }

        /// <summary>Whether <paramref name="exe"/> runs (its tray service or a window).</summary>
        public static bool Running(string exe) => exe != null && Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0;

        /// <summary>Starts the installed app in the tray, without its window, unless it runs already. False when it is not installed.</summary>
        public static bool StartInTray()
        {
            string exe = Installed;
            if (exe == null) return false;
            if (!Running(exe)) Process.Start(new ProcessStartInfo(exe, "--hidden") { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }

        private static string Feed => OrbitersEnvironment.IsDevelopment ? OrbitersEnvironment.ApiUrl("downloads/desktop") : ProductionFeed;

        /// <summary>
        /// Downloads the latest setup and runs it (installs for this Windows account, then starts Orbiters - N4N0).
        /// <paramref name="status"/> hears each step in words; throws with a reason a person reads.
        /// </summary>
        public static async Task InstallAndOpenAsync(Action<string> status, CancellationToken cancellation = default)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Orbiters", "N4N0 setup");
            Directory.CreateDirectory(folder);
            status?.Invoke("Finding the latest Orbiters - N4N0…");
            string feed = Feed.TrimEnd('/');
            string listing = Path.Combine(folder, "stable.json");
            var read = await OrbitersTransfer.DownloadFileAsync(feed + "/stable.json", listing, null, cancellation: cancellation, maxBytes: 1 << 20);
            if (!read.Success) throw new InvalidOperationException("Orbiters - N4N0's download page did not answer: " + read.Error);
            // `release` is the signed release as text (the app checks the signature; HTTPS and the setup's SHA-256 do here).
            var outer = JObject.Parse(File.ReadAllText(listing));
            var release = outer["release"]?.Type == JTokenType.String ? JObject.Parse((string)outer["release"]) : (JObject)outer["release"] ?? outer;
            string version = (string)release["version"];
            string url = (string)release["installer"]?["url"];
            string sha256 = (string)release["installer"]?["sha256"];
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha256)) throw new InvalidOperationException("Orbiters - N4N0's latest release has no setup to download.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute)) absolute = new Uri(new Uri(feed + "/"), url);

            status?.Invoke($"Downloading Orbiters - N4N0 {version}…");
            string setup = Path.Combine(folder, "OrbitersSetup-" + version + ".exe");
            var got = await OrbitersTransfer.DownloadFileAsync(absolute.ToString(), setup, null,
                (done, total) => status?.Invoke(total > 0 ? $"Downloading Orbiters - N4N0 {version}… {done * 100 / total} %" : $"Downloading Orbiters - N4N0 {version}…"),
                cancellation, sha256, maxBytes: 1L << 30);
            if (!got.Success) throw new InvalidOperationException("Orbiters - N4N0 could not be downloaded: " + got.Error);

            status?.Invoke($"Installing Orbiters - N4N0 {version}…");
            using (var process = Process.Start(new ProcessStartInfo(setup, "--silent") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (process == null) throw new InvalidOperationException("Windows did not start the setup.");
                await Task.Run(() => process.WaitForExit(), cancellation);
                if (process.ExitCode != 0) throw new InvalidOperationException($"The setup stopped (code {process.ExitCode}). Run {setup} to see why.");
            }
            string exe = Installed;
            if (exe == null) throw new InvalidOperationException("The setup finished but Orbiters - N4N0 is not where it installs.");
            // A silent setup leaves starting the app to its caller (a second start shows the running one).
            if (!Running(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            status?.Invoke($"Orbiters - N4N0 {version} is installed and open.");
        }
    }
}
