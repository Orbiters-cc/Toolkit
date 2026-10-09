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
            if (picture) UnityEngine.Object.DestroyImmediate(picture); picture = null; });
    }
    private async void Refresh()
    {
        int current = ++revision;
        if (picture) UnityEngine.Object.DestroyImmediate(picture); picture = null;
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
        Draw("Checking…");
        var source = lifetime; if (source == null) return;
        try
        {
            var status = await OrbitersAccountService.CheckAsync(endpoint, auth.token, source.Token);
            if (source.IsCancellationRequested || current != revision || AuthenticationService.GetAuth()?.token != auth.token) return;
            Draw(status.state); changed?.Invoke(status.state == "connected" && status.aiEnabled);
            try
            {
                var profile = await OrbitersAccountService.ProfileAsync(auth.user, auth.token, source.Token);
                if (source.IsCancellationRequested || current != revision) return;
                auth.username = profile.username; auth.avatarUrl = profile.avatarUrl; Draw(status.state);
            }
            catch (Exception) { if (source.IsCancellationRequested || current != revision) return; }
            if (picture || string.IsNullOrWhiteSpace(auth.avatarUrl)) return;
            string url = OrbitersEnvironment.ResolveApiUrl(auth.avatarUrl);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !OrbitersEnvironment.IsLoopback(uri.Host))) return;
            using var req = UnityWebRequestTexture.GetTexture(url); req.timeout = 8;
            var operation = req.SendWebRequest();
            while (!operation.isDone) { if (source.IsCancellationRequested) { req.Abort(); return; } await System.Threading.Tasks.Task.Yield(); }
            if (source.IsCancellationRequested || current != revision || req.result != UnityWebRequest.Result.Success) return;
            picture = DownloadHandlerTexture.GetContent(req); Draw(status.state);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!source.IsCancellationRequested && current == revision) {
            Draw("disconnected"); changed?.Invoke(false);
            var retry = new Button(Refresh) { text = "Retry connection" }; retry.AddToClassList("mcb-button"); Add(retry);
        } }
    }
}
#endif
