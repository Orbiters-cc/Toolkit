#if UNITY_EDITOR
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Signs a Unity tool in through the browser: Orbiters creates a one-time link, the browser opens it, the person clicks
/// Connect, and the browser brings a one-time code back to this editor's listener on 127.0.0.1. Traded with the secret
/// only this editor holds, the code gives the editor credential. Nothing to type or copy, and a link sent by someone else
/// connects nothing: its code lands on the computer whose browser clicked Connect.
/// </summary>
public static class OrbitersBrowserLogin
{
    private sealed class Link { public string linkUrl, connectedUrl, exchangeToken; }

    /// <summary>
    /// One sign-in, pinned to the server it started on. Its account is saved only while it is still wanted: not cancelled,
    /// no newer sign-in started since, and the tools still on that server.
    /// </summary>
    internal sealed class Attempt
    {
        private static int newest;
        private readonly int id = Interlocked.Increment(ref newest);
        private readonly CancellationToken cancellation;
        internal readonly bool Development;

        internal Attempt(bool development, CancellationToken cancellation)
        {
            Development = development;
            this.cancellation = cancellation;
        }

        internal string ApiUrl(string scope) => OrbitersEnvironment.ApiUrl(scope, Development);

        internal void ThrowIfStale()
        {
            cancellation.ThrowIfCancellationRequested();
            if (id != Volatile.Read(ref newest)) throw new OperationCanceledException("A newer sign-in started.");
            if (OrbitersEnvironment.IsDevelopment != Development)
                throw new InvalidOperationException("The Orbiters server changed during the sign-in. Click Sign in again.");
        }

        /// <summary>Checks again right before saving: on the editor's main thread nothing runs in between.</summary>
        internal void Save(AuthenticationService.AuthData auth)
        {
            ThrowIfStale();
            AuthenticationService.SaveAuth(auth, Development);
        }
    }

    /// <summary>
    /// The listener the browser comes back to: loopback only, a port the system picks and a random path, so no other page
    /// can feed it a code. The first GET on the path is the arrival; everything else gets a 404.
    /// </summary>
    internal sealed class Callback : IDisposable
    {
        internal readonly int Port;
        internal readonly string Path;
        private readonly TcpListener listener;
        private readonly TaskCompletionSource<Arrival> arrived = new TaskCompletionSource<Arrival>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Callback(int port, string path)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (path == null)
            {
                var bytes = new byte[24];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                path = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            Path = path;
        }

        /// <summary>A listener on a free port and a new path, or (after a script reload) again on the ones the link names.</summary>
        internal static Callback Start(int port = 0, string path = null)
        {
            var callback = new Callback(port, path);
            _ = callback.AcceptAsync();
            return callback;
        }

        internal async Task<Arrival> WaitAsync(CancellationToken cancellation)
        {
            using (cancellation.Register(() => arrived.TrySetCanceled()))
                return await arrived.Task;
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; } // Stopped.
                // Browsers open spare connections that never send anything: each one waits on its own.
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                string target = await ReadTargetAsync(client.GetStream());
                string code = target == null ? null : CodeFor(target, Path);
                if (code != null && arrived.TrySetResult(new Arrival(client, code))) return;
                await Respond(client, "404 Not Found", null);
            }
            catch (Exception) { }
            client.Dispose();
        }

        /// <summary>
        /// The request target of a GET ("/path?query"), or null. Reads the whole head (within ten seconds) so that closing
        /// the connection after the answer does not reset it under the browser.
        /// </summary>
        private static async Task<string> ReadTargetAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            int length = 0;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (length < buffer.Length)
            {
                var read = stream.ReadAsync(buffer, length, buffer.Length - length);
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || await Task.WhenAny(read, Task.Delay(left)) != read)
                {
                    _ = read.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return null;
                }
                int count = await read;
                if (count == 0) return null;
                length += count;
                string head = Encoding.ASCII.GetString(buffer, 0, length);
                if (head.IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0) continue;
                string[] parts = head.Substring(0, head.IndexOf("\r\n", StringComparison.Ordinal)).Split(' ');
                return parts.Length == 3 && parts[0] == "GET" ? parts[1] : null;
            }
            return null;
        }

        /// <summary>The code of a request on <paramref name="path"/>, or null.</summary>
        internal static string CodeFor(string target, string path)
        {
            int query = target.IndexOf('?');
            if (query < 0 || target.Substring(0, query) != "/" + path) return null;
            foreach (string pair in target.Substring(query + 1).Split('&'))
                if (pair.StartsWith("code=", StringComparison.Ordinal) && pair.Length > 5)
                    return Uri.UnescapeDataString(pair.Substring(5));
            return null;
        }

        internal static async Task Respond(TcpClient client, string status, string location)
        {
            string head = "HTTP/1.1 " + status + "\r\n" + (location == null ? "" : "Location: " + location + "\r\n")
                + "Cache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(head);
            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
            client.Client.Shutdown(SocketShutdown.Send);
        }

        public void Dispose()
        {
            listener.Stop();
            arrived.TrySetCanceled();
        }
    }

    /// <summary>The browser's request with the code, held open until the sign-in can tell it where to go.</summary>
    internal sealed class Arrival : IDisposable
    {
        private readonly TcpClient client;
        internal readonly string Code;

        internal Arrival(TcpClient client, string code) { this.client = client; Code = code; }

        internal async Task SendBrowserToAsync(string url)
        {
            try { await Callback.Respond(client, "302 Found", url); }
            catch (Exception) { } // The tab was closed: the sign-in itself is done.
        }

        public void Dispose() => client.Dispose();
    }

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The sign-in waiting for the browser, kept through script reloads for this editor session (SessionState): after a
    /// reload its listener comes back on the same port and path, so Connect clicked meanwhile still reaches the tool.
    /// </summary>
    [Serializable]
    private sealed class Pending
    {
        public int port;
        public string path, linkUrl, connectedUrl, exchangeToken;
        public long until;
        public bool development;
    }

    private const string PendingKey = "Orbiters.Toolkit.BrowserLogin.Pending";
    private static CancellationTokenSource resumed;

    /// <summary>The link of the sign-in that waits for the browser (one a script reload interrupted too), or null.</summary>
    public static string WaitingLink => ReadPending()?.linkUrl;

    /// <summary>The sign-in resumed after a script reload ended: null when it signed in, otherwise why it didn't.</summary>
    public static event Action<Exception> ResumedEnded;

    private static Pending ReadPending()
    {
        string json = UnityEditor.SessionState.GetString(PendingKey, "");
        var pending = json.Length > 0 ? JsonUtility.FromJson<Pending>(json) : null;
        return pending != null && pending.until > DateTime.UtcNow.Ticks ? pending : null;
    }

    private static void Remember(Pending pending) => UnityEditor.SessionState.SetString(PendingKey, pending == null ? "" : JsonUtility.ToJson(pending));

    /// <summary>Stops the sign-in a script reload interrupted (one LoginAsync started stops with its token).</summary>
    public static void CancelWaiting()
    {
        resumed?.Cancel();
        Remember(null);
    }

    [UnityEditor.InitializeOnLoadMethod]
    private static void ResumeAfterReload()
    {
        var pending = ReadPending();
        if (pending == null) { Remember(null); return; }
        Resume(pending, resumed = new CancellationTokenSource());
    }

    private static async void Resume(Pending pending, CancellationTokenSource source)
    {
        Exception failure = null;
        try
        {
            Callback callback;
            try { callback = Callback.Start(pending.port, pending.path); }
            catch (SocketException) { throw new InvalidOperationException("The sign-in was interrupted. Click Sign in again."); }
            using (callback) await FinishAsync(pending, callback, new Attempt(pending.development, source.Token), source.Token);
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            if (resumed == source) resumed = null;
            source.Dispose();
        }
        if (!(failure is OperationCanceledException)) ResumedEnded?.Invoke(failure);
    }

    /// <param name="client">The tool asking, shown on the Connect page ("myavatar", "mcb"...).</param>
    /// <param name="opened">Receives the link once the browser is opened, to open it again.</param>
    public static async Task LoginAsync(string client, Action<string> opened, CancellationToken cancellation)
    {
        CancelWaiting();
        var attempt = new Attempt(OrbitersEnvironment.IsDevelopment, cancellation);
        using var callback = Callback.Start();
        var link = await OrbitersApi.SendAsync<Link>(attempt.ApiUrl("editor-links"), null,
            new { client, device = SystemInfo.deviceName, port = callback.Port, path = callback.Path }, cancellation);
        attempt.ThrowIfStale();
        if (string.IsNullOrEmpty(link?.linkUrl) || string.IsNullOrEmpty(link.exchangeToken) || string.IsNullOrEmpty(link.connectedUrl))
            throw new InvalidOperationException("Orbiters did not return a sign-in link.");
        var pending = new Pending
        {
            port = callback.Port, path = callback.Path, linkUrl = link.linkUrl, connectedUrl = link.connectedUrl, exchangeToken = link.exchangeToken,
            until = (DateTime.UtcNow + Lifetime).Ticks, development = attempt.Development,
        };
        Remember(pending);
        Application.OpenURL(link.linkUrl);
        opened?.Invoke(link.linkUrl);
        await FinishAsync(pending, callback, attempt, cancellation);
    }

    // Waits for the browser until the link's end, trades its code for the credential and sends the browser on.
    private static async Task FinishAsync(Pending pending, Callback callback, Attempt attempt, CancellationToken cancellation)
    {
        try
        {
            Arrival arrival;
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                waiting.CancelAfter(TimeSpan.FromTicks(Math.Max(0, pending.until - DateTime.UtcNow.Ticks)));
                try { arrival = await callback.WaitAsync(waiting.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new TimeoutException("The sign-in took too long. Click Sign in again.");
                }
            }
            using (arrival)
            {
                try
                {
                    attempt.ThrowIfStale();
                    var auth = await OrbitersApi.SendAsync<AuthenticationService.AuthData>(attempt.ApiUrl("editor-links/exchange"), null,
                        new { exchangeToken = pending.exchangeToken, code = arrival.Code }, cancellation);
                    if (string.IsNullOrWhiteSpace(auth?.token)) throw new InvalidOperationException("Orbiters did not return an account.");
                    attempt.Save(auth);
                }
                catch
                {
                    await arrival.SendBrowserToAsync(pending.connectedUrl + (pending.connectedUrl.Contains("?") ? "&" : "?") + "failed=1");
                    throw;
                }
                await arrival.SendBrowserToAsync(pending.connectedUrl);
            }
        }
        finally
        {
            // Only this sign-in's: a newer one may have replaced it meanwhile.
            if (ReadPending()?.path == pending.path) Remember(null);
        }
    }
}
#endif
