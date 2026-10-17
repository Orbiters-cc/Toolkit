#if UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

public sealed class OrbitersAccountElement : VisualElement
{
    private readonly string endpoint;
    private readonly Action<bool> changed;
    private readonly string reason;
    private readonly bool confirmLogout;
    // Shared with every other row (OrbitersAccountService keeps it): never destroyed here.
    private Texture2D picture;
    private CancellationTokenSource lifetime;
    private int revision;
    /// <param name="endpoint">The tool's connection check, e.g. "myavatar/connection"; its first segment names the tool on the login page.</param>
    /// <param name="reason">Why connecting helps, shown under the login buttons.</param>
    /// <param name="confirmLogout">Ask before signing out (of every Orbiters tool, since they share the account).</param>
    public OrbitersAccountElement(string endpoint, Action<bool> changed, string reason = null, bool confirmLogout = false)
    {
        this.endpoint = endpoint; this.changed = changed; this.reason = reason; this.confirmLogout = confirmLogout;
        RegisterCallback<AttachToPanelEvent>(_ => { lifetime = new CancellationTokenSource(); AuthenticationService.Changed += Refresh; OrbitersEnvironment.Changed += Refresh; Refresh(); });
        RegisterCallback<DetachFromPanelEvent>(_ => { AuthenticationService.Changed -= Refresh; OrbitersEnvironment.Changed -= Refresh; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            picture = null; });
    }
    private static bool Enabled(OrbitersAccountService.Connection connection) => connection.state == "connected" && connection.aiEnabled;
    private static string PictureUrl(string avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
        string url = OrbitersEnvironment.ResolveApiUrl(avatarUrl);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || OrbitersEnvironment.IsLoopback(uri.Host)) ? url : null;
    }
    private async void Refresh()
    {
        int current = ++revision;
        picture = null;
        Clear(); var auth = AuthenticationService.GetAuth();
        if (string.IsNullOrEmpty(auth?.token)) {
            changed?.Invoke(false); var host = new VisualElement(); host.AddToClassList("mcb-auth-host"); host.Add(new OrbitersSignInElement(null, reason, endpoint.Split('/')[0])); Add(host); return;
        }
        var row = new VisualElement(); row.AddToClassList("mcb-account"); Add(row);
        void Draw(string state) {
            row.Clear(); OrbitersAccountView.Populate(row, auth.username ?? auth.user, state, state == "Checking…", picture, OrbitersAccountView.FallbackColor(auth.username ?? auth.user), () => {
                if (confirmLogout && !UnityEditor.EditorUtility.DisplayDialog("Log out", "Log out of your Orbiters account in every Orbiters tool?", "Log out", "Cancel")) return;
                if (!AuthenticationService.RemoveAuth()) Add(new OrbitersNoticeElement("Could not clear the saved account. Check file permissions.", HelpBoxMessageType.Warning));
            });
        }
        // What the last checks answered shows at once (the row, its picture and the tool's state); the server is asked again
        // only once they are a few minutes old.
        var known = OrbitersAccountService.CachedConnection(endpoint, auth.token, out bool connectionFresh);
        var profile = OrbitersAccountService.CachedProfile(auth.token, out bool profileFresh);
        if (profile != null) { auth.username = profile.username ?? auth.username; auth.avatarUrl = profile.avatarUrl ?? auth.avatarUrl; }
        picture = OrbitersAccountService.CachedPicture(PictureUrl(auth.avatarUrl));
        Draw(known?.state ?? "Checking…");
        if (known != null) changed?.Invoke(Enabled(known));
        if (known != null && connectionFresh && profileFresh && (picture || PictureUrl(auth.avatarUrl) == null)) return;
        var source = lifetime; if (source == null) return;
        try
        {
            var status = known;
            if (status == null || !connectionFresh)
            {
                status = await OrbitersAccountService.CheckAsync(endpoint, auth.token, source.Token);
                if (source.IsCancellationRequested || current != revision || AuthenticationService.GetAuth()?.token != auth.token) return;
                OrbitersAccountService.Remember(endpoint, auth.token, status);
                Draw(status.state);
                if (known == null || Enabled(known) != Enabled(status)) changed?.Invoke(Enabled(status));
            }
            if (profile == null || !profileFresh)
                try
                {
                    profile = await OrbitersAccountService.ProfileAsync(auth.user, auth.token, source.Token);
                    if (source.IsCancellationRequested || current != revision) return;
                    OrbitersAccountService.Remember(auth.token, profile);
                    auth.username = profile.username; auth.avatarUrl = profile.avatarUrl;
                    picture = OrbitersAccountService.CachedPicture(PictureUrl(auth.avatarUrl));
                    Draw(status.state);
                }
                catch (Exception) { if (source.IsCancellationRequested || current != revision) return; }
            string url = PictureUrl(auth.avatarUrl);
            if (picture || url == null) return;
            using var req = UnityWebRequest.Get(url); req.timeout = 8;
            var operation = req.SendWebRequest();
            while (!operation.isDone) { if (source.IsCancellationRequested) { req.Abort(); return; } await System.Threading.Tasks.Task.Yield(); }
            if (source.IsCancellationRequested || current != revision || req.result != UnityWebRequest.Result.Success) return;
            picture = OrbitersAccountService.RememberPicture(url, req.downloadHandler.data);
            if (picture) Draw(status.state);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!source.IsCancellationRequested && current == revision) {
            // Not kept: the next row asks again instead of showing an answer that may be out of date.
            OrbitersAccountService.Remember(endpoint, auth.token, null);
            Draw("disconnected"); changed?.Invoke(false);
            var retry = new Button(Refresh) { text = "Retry connection" }; retry.AddToClassList("mcb-button"); Add(retry);
        } }
    }
}
#endif
