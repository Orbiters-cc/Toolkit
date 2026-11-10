#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Orbiters.Toolkit
{
    /// <summary>What a request is for: it decides its timeout (<see cref="OrbitersHttp.TimeoutOf"/>) and its retries.</summary>
    public enum OrbitersHttpKind
    {
        /// <summary>A call to the Orbiters API (90 s).</summary>
        Api,
        /// <summary>A listing or other public document (a VPM repository, 30 s).</summary>
        Listing,
        /// <summary>A small picture shown in a window (an account picture, 8 s).</summary>
        Picture,
        /// <summary>A file transfer: no total timeout, stopped when no byte moved for <see cref="OrbitersHttp.StallSeconds"/>.</summary>
        Transfer,
    }

    /// <summary>
    /// A request that failed: the server answered an error (<see cref="StatusCode"/>, its {"error"} message as the
    /// message), or no answer came (<see cref="Offline"/>: the network failed or the request timed out). An
    /// InvalidOperationException, as the API client's errors always were.
    /// </summary>
    public sealed class OrbitersHttpException : InvalidOperationException
    {
        /// <summary>The HTTP status, or 0 when no answer came.</summary>
        public int StatusCode { get; }
        /// <summary>No answer came within the request's timeout.</summary>
        public bool TimedOut { get; }
        /// <summary>No answer came at all (unreachable, the connection failed or it timed out).</summary>
        public bool Offline => StatusCode == 0;
        /// <summary>The answer's body (at most 64 KB), or null.</summary>
        public string Body { get; }
        public string Url { get; }
        /// <summary>The server answered but its answer was cut off before its end.</summary>
        public bool CutOff { get; }
        /// <summary>
        /// For logs, not for people: e.g. the custom headers not sent on to another server after a redirect (they looked like
        /// credentials). Part of <see cref="ToString"/>.
        /// </summary>
        public string Diagnostic { get; internal set; }

        public OrbitersHttpException(string message, string url, int statusCode, string body = null, bool timedOut = false, Exception inner = null, bool cutOff = false) : base(message, inner)
        {
            Url = url; StatusCode = statusCode; Body = body; TimedOut = timedOut; CutOff = cutOff;
        }

        public override string ToString() => string.IsNullOrEmpty(Diagnostic) ? base.ToString() : base.ToString() + Environment.NewLine + Diagnostic;
    }

    /// <summary>One field or file of a multipart/form-data body.</summary>
    /// <summary>What <see cref="OrbitersHttp.ConditionalTextAsync"/> got: the text and its validators, or "not changed".</summary>
    public sealed class OrbitersConditionalText
    {
        public bool NotModified;
        public string Text, ETag, LastModified;
    }

    public sealed class OrbitersFormPart
    {
        public string Name, Value, FilePath, FileName, ContentType;
        public static OrbitersFormPart Field(string name, string value) => new OrbitersFormPart { Name = name, Value = value ?? string.Empty };
        public static OrbitersFormPart File(string name, string path, string fileName = null, string contentType = "application/octet-stream") =>
            new OrbitersFormPart { Name = name, FilePath = path, FileName = fileName ?? Path.GetFileName(path), ContentType = contentType };
    }

    /// <summary>The outcome of a file transfer (<see cref="OrbitersHttp.DownloadFileAsync"/>, <see cref="OrbitersHttp.UploadMultipartAsync"/>).</summary>
    public sealed class OrbitersTransferResult
    {
        public bool Success, Cancelled;
        public long StatusCode;
        public string Error, Body, Sha256;
        public long Bytes;
        /// <summary>A download stopped because it was (or announced it was) larger than allowed.</summary>
        public bool TooLarge;
        /// <summary>The server's answer was cut off before its end (an upload may or may not have been received).</summary>
        public bool CutOff;
        /// <summary>For logs, not for people (see <see cref="OrbitersHttpException.Diagnostic"/>).</summary>
        public string Diagnostic;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// The one HTTP client of the Orbiters tools: JSON calls, text and bytes, pictures, file downloads and multipart uploads,
    /// on one HttpClient, with one timeout table (<see cref="TimeoutOf"/>), one error type (<see cref="OrbitersHttpException"/>)
    /// and one retry rule: requests that change nothing (GET, HEAD) are tried again once (twice for listings, transfers as
    /// asked) when no answer came or the server was busy (429, 5xx); others are sent once. <see cref="Offline"/> tells whether
    /// the last request got an answer. Nothing runs on the editor's thread: requests start on the thread pool (DNS lookups
    /// can block), bodies are read and written there, a download is hashed as it arrives and an upload streams its files
    /// straight from disk. Progress is handed to the thread that started the transfer (the editor's), at most ten times a
    /// second. Redirects are followed one hop at a time, each checked (<see cref="RedirectTarget"/>): API calls keep the
    /// token only on the same server, downloads only for the first hop (a signed storage URL is its own credential).
    /// Await from the editor's thread to continue there; <see cref="TextureAsync"/> must be.
    /// </summary>
    public static class OrbitersHttp
    {
        /// <summary>Seconds without a byte moving before a transfer counts as stalled and is stopped.</summary>
        public const double StallSeconds = 120d;
        /// <summary>Redirects a request follows at most.</summary>
        public const int MaxRedirects = 5;
        // StallSeconds, shortened by tests.
        internal static double StallLimitSeconds = StallSeconds;
        private const int ErrorBodyLimit = 64 * 1024;
        /// <summary>The largest answer a text or bytes call reads (files go through <see cref="DownloadFileAsync"/>).</summary>
        public const long MaxBodyBytes = 256L * 1024 * 1024;

        // Redirects are followed by hand, never by the handler: each hop is checked and the token is not sent on blindly.
        internal static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private static volatile bool offline;
        private static long answeredTicks;

        /// <summary>The longest a request of this kind waits for its whole answer (transfers: none, they stop on a stall).</summary>
        public static TimeSpan TimeoutOf(OrbitersHttpKind kind) => kind switch
        {
            OrbitersHttpKind.Api => TimeSpan.FromSeconds(90),
            OrbitersHttpKind.Listing => TimeSpan.FromSeconds(30),
            OrbitersHttpKind.Picture => TimeSpan.FromSeconds(8),
            _ => System.Threading.Timeout.InfiniteTimeSpan,
        };

        /// <summary>How many times a request is tried again after no answer or a busy server (see the class).</summary>
        public static int RetriesOf(HttpMethod method, OrbitersHttpKind kind)
        {
            if (method != HttpMethod.Get && method != HttpMethod.Head) return 0;
            return kind == OrbitersHttpKind.Listing ? 2 : kind == OrbitersHttpKind.Picture ? 0 : 1;
        }

        /// <summary>True when the last request got no answer at all (false once one gets any answer).</summary>
        public static bool Offline => offline;

        /// <summary>When a server last answered (UTC), or DateTime.MinValue.</summary>
        public static DateTime LastAnswered => new DateTime(Interlocked.Read(ref answeredTicks), DateTimeKind.Utc);

        /// <summary>The top-level "error" text of a JSON body ({"error": "..."}), or null.</summary>
        public static string ServerError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try { return JToken.Parse(body) is JObject json && json["error"] is JValue value && value.Type == JTokenType.String ? (string)value : null; }
            catch (JsonException) { return null; }
        }

        /// <summary>"Orbiters answered HTTP 404 (Not Found)." or, without an answer, that Orbiters could not be reached.</summary>
        public static string StatusMessage(long status, string reason = null) =>
            status == 0 ? "Orbiters could not be reached. Check your connection and try again."
                : $"Orbiters answered HTTP {status}{(string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")")}.";

        // ---- JSON, text, bytes, pictures ----

        /// <summary>
        /// Sends <paramref name="payload"/> as JSON (none when null) and reads the answer as <typeparamref name="T"/> (default
        /// for an empty body). On an error answer it throws with the server's {"error"} message, else
        /// <paramref name="failure"/> ({0}: the status) or <see cref="StatusMessage"/>.
        /// </summary>
        public static async Task<T> JsonAsync<T>(HttpMethod method, string url, object payload = null, string token = null, CancellationToken cancellation = default,
            string failure = null, OrbitersHttpKind kind = OrbitersHttpKind.Api, IDictionary<string, string> headers = null)
        {
            string json = payload != null ? JsonConvert.SerializeObject(payload) : null;
            string body = await SendForTextAsync(method, url, json, token, cancellation, failure, kind, headers).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? default : JsonConvert.DeserializeObject<T>(body);
        }

        /// <summary>GET as text; throws <see cref="OrbitersHttpException"/> on an error answer or none.</summary>
        public static Task<string> TextAsync(string url, string token = null, CancellationToken cancellation = default, OrbitersHttpKind kind = OrbitersHttpKind.Api,
            IDictionary<string, string> headers = null) =>
            SendForTextAsync(HttpMethod.Get, url, null, token, cancellation, null, kind, headers);

        /// <summary>
        /// A GET that may answer "not changed": with the validators of the copy the caller keeps (<paramref name="etag"/>,
        /// <paramref name="lastModified"/>; null when none), a 304 answer comes back as <see cref="OrbitersConditionalText.NotModified"/>
        /// without a body; otherwise the text with its new validators. Throws <see cref="OrbitersHttpException"/> as
        /// <see cref="TextAsync"/> does.
        /// </summary>
        public static async Task<OrbitersConditionalText> ConditionalTextAsync(string url, string etag, string lastModified, CancellationToken cancellation = default,
            OrbitersHttpKind kind = OrbitersHttpKind.Listing)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(etag)) headers["If-None-Match"] = etag;
            if (!string.IsNullOrEmpty(lastModified)) headers["If-Modified-Since"] = lastModified;
            try
            {
                return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), url, null, false, cancellation, kind, headers, null,
                    async (response, token) => new OrbitersConditionalText
                    {
                        Text = await ReadTextAsync(response, token).ConfigureAwait(false),
                        ETag = response.Headers.ETag?.ToString(),
                        LastModified = response.Content.Headers.LastModified?.ToString("R"),
                    }).ConfigureAwait(false);
            }
            catch (OrbitersHttpException error) when (error.StatusCode == 304 && headers.Count > 0)
            {
                return new OrbitersConditionalText { NotModified = true, ETag = etag, LastModified = lastModified };
            }
        }

        /// <summary>GET as bytes; throws <see cref="OrbitersHttpException"/> on an error answer or none.</summary>
        public static async Task<byte[]> BytesAsync(string url, string token = null, CancellationToken cancellation = default, OrbitersHttpKind kind = OrbitersHttpKind.Api)
        {
            return await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), url, token, false, cancellation, kind, null, null, ReadBodyAsync).ConfigureAwait(false);
        }

        /// <summary>
        /// A picture (PNG or JPEG) as a texture, fetched off the editor's thread and decoded on it: await from the editor's
        /// thread. Null when the bytes are no picture.
        /// </summary>
        public static async Task<Texture2D> TextureAsync(string url, string token = null, CancellationToken cancellation = default, OrbitersHttpKind kind = OrbitersHttpKind.Picture)
        {
            var bytes = await BytesAsync(url, token, cancellation, kind);
            cancellation.ThrowIfCancellationRequested();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = Path.GetFileName(new Uri(url).AbsolutePath) };
            if (texture.LoadImage(bytes)) return texture;
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        private static async Task<string> SendForTextAsync(HttpMethod method, string url, string json, string token, CancellationToken cancellation, string failure,
            OrbitersHttpKind kind, IDictionary<string, string> headers)
        {
            HttpRequestMessage Make()
            {
                var request = new HttpRequestMessage(method, url);
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return request;
            }
            return await SendAsync(Make, url, token, false, cancellation, kind, headers, failure, ReadTextAsync).ConfigureAwait(false);
        }

        // Sends with the kind's timeout and retries, following redirects, and reads a successful answer's body with
        // `read`, all within the timeout (a body that stops arriving is abandoned like a request that gets no answer); throws
        // OrbitersHttpException otherwise. The caller's cancellation throws OperationCanceledException.
        private static async Task<T> SendAsync<T>(Func<HttpRequestMessage> make, string url, string token, bool tokenFirstHopOnly,
            CancellationToken cancellation, OrbitersHttpKind kind, IDictionary<string, string> headers, string failure,
            Func<HttpResponseMessage, CancellationToken, Task<T>> read)
        {
            var probe = make();
            var method = probe.Method;
            probe.Dispose();
            int retries = RetriesOf(method, kind);
            var stripped = new List<string>();
            for (int attempt = 0; ; attempt++)
            {
                OrbitersHttpException error;
                int answered = 0;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    if (TimeoutOf(kind) != System.Threading.Timeout.InfiniteTimeSpan) timeout.CancelAfter(TimeoutOf(kind));
                    try
                    {
                        using (var response = await FollowAsync(make, url, token, tokenFirstHopOnly, headers, timeout.Token, stripped).ConfigureAwait(false))
                        // Cancelled or timed out while the body comes: the connection is closed, which ends a read that waits.
                        using (timeout.Token.Register(response.Dispose))
                        {
                            answered = (int)response.StatusCode;
                            if (response.IsSuccessStatusCode) return await read(response, timeout.Token).ConfigureAwait(false);
                            var (body, complete) = await ReadLimitedAsync(response, timeout.Token, null).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            int status = (int)response.StatusCode;
                            string message = complete
                                ? ServerError(body) ?? (failure != null ? string.Format(failure, status) : StatusMessage(status, response.ReasonPhrase))
                                : CutOffMessage(status);
                            error = new OrbitersHttpException(message, url, status, body, cutOff: !complete);
                        }
                    }
                    catch (Exception ex) when (Stopped(ex) && cancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellation);
                    }
                    catch (Exception ex) when (Stopped(ex) && timeout.IsCancellationRequested)
                    {
                        error = new OrbitersHttpException($"Orbiters did not answer within {TimeoutOf(kind).TotalSeconds:0} seconds.", url, 0, timedOut: true);
                    }
                    catch (HttpRequestException ex)
                    {
                        error = new OrbitersHttpException(StatusMessage(0), url, 0, inner: ex);
                    }
                    // The connection broke off while the body came.
                    catch (IOException ex)
                    {
                        error = answered != 0 ? new OrbitersHttpException(CutOffMessage(answered), url, answered, inner: ex, cutOff: true)
                            : new OrbitersHttpException(StatusMessage(0), url, 0, inner: ex);
                    }
                }
                if (stripped.Count > 0) error.Diagnostic = "Not sent on to another server after a redirect: " + string.Join(", ", stripped.Distinct()) + ".";
                bool busy = error.StatusCode == 429 || error.StatusCode >= 500;
                // Only requests that change nothing are retried (RetriesOf): a cut-off answer to a POST may mean it was done.
                if (attempt >= retries || !(error.Offline || busy || error.CutOff)) throw error;
                await Task.Delay(1000 * (attempt + 1) * (attempt + 1), cancellation).ConfigureAwait(false);
            }
        }

        // One hop at a time; the token goes on only to the same server (or, for downloads, never past the first hop).
        private static async Task<HttpResponseMessage> FollowAsync(Func<HttpRequestMessage> make, string url, string token, bool tokenFirstHopOnly,
            IDictionary<string, string> headers, CancellationToken cancellation, ICollection<string> stripped = null)
        {
            var first = new Uri(url);
            string current = url;
            for (int hop = 0; ; hop++)
            {
                var request = make();
                request.RequestUri = new Uri(current);
                bool sameServer = Uri.Compare(first, request.RequestUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
                // Credentials (the token, and custom headers carrying one) go where the token may: the first hop, then only the
                // same scheme, host and port (never past the first hop for transfers).
                bool credentials = hop == 0 || !tokenFirstHopOnly && sameServer;
                if (!string.IsNullOrEmpty(token) && credentials) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (headers != null)
                    foreach (var pair in headers)
                        if (credentials || !IsCredential(pair.Key)) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                        else stripped?.Add(pair.Key + " (to " + request.RequestUri.Authority + ")");
                HttpResponseMessage response;
                try
                {
                    // Started on the thread pool: resolving the server's name may block the calling thread.
                    response = await Task.Run(() => Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation), cancellation).ConfigureAwait(false);
                }
                catch (HttpRequestException) { offline = true; request.Dispose(); throw; }
                offline = false;
                Interlocked.Exchange(ref answeredTicks, DateTime.UtcNow.Ticks);
                int status = (int)response.StatusCode;
                var location = response.Headers.Location;
                if (status < 300 || status >= 400 || location == null) return response;
                var target = RedirectTarget(current, location.OriginalString);
                response.Dispose();
                request.Dispose();
                if (target == null) throw new OrbitersHttpException("The request was sent on to a place Orbiters does not download from (not HTTPS).", current, status);
                if (hop + 1 >= MaxRedirects) throw new OrbitersHttpException("The request was redirected too many times.", current, status);
                // 303 asks for the answer with a GET; the others repeat the request.
                if (status == 303)
                {
                    var get = make;
                    make = () => { var r = get(); r.Method = HttpMethod.Get; r.Content = null; return r; };
                }
                current = target.AbsoluteUri;
            }
        }

        // A header that carries a credential: never sent on to another server.
        private static bool IsCredential(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n == "authorization" || n == "proxy-authorization" || n == "cookie" ||
                   n.Contains("token") || n.Contains("auth") || n.Contains("key") || n.Contains("secret") || n.Contains("session") ||
                   n.Contains("password") || n.Contains("cookie") || n.Contains("credential");
        }

        // Stopped by a cancellation or a timeout: cancelled, or the response disposed under a read.
        private static bool Stopped(Exception ex) => ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException;

        private static string CutOffMessage(long status) => $"Orbiters answered (HTTP {status}), but its answer was cut off before its end.";

        // The body ended before the length it announced (a compressed body's announced length is not its length).
        private sealed class CutOffException : IOException
        {
            public CutOffException() : base("The answer was cut off before its end.") { }
        }

        private static bool Short(HttpResponseMessage response, long read) =>
            response.Content.Headers.ContentEncoding.Count == 0 && response.Content.Headers.ContentLength is long announced && read < announced;

        // An error answer's body, at most ErrorBodyLimit bytes, and whether it arrived whole (or up to the limit); stops
        // with the token. `moved` hears each chunk (a transfer's stall watch).
        private static async Task<(string Body, bool Complete)> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellation, Action moved)
        {
            var buffer = new byte[ErrorBodyLimit];
            int read = 0, n;
            try
            {
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    while (read < buffer.Length && (n = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellation).ConfigureAwait(false)) > 0)
                    {
                        read += n;
                        moved?.Invoke();
                    }
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException))
            {
                return (read > 0 ? Encoding.UTF8.GetString(buffer, 0, read) : null, false);
            }
            return (Encoding.UTF8.GetString(buffer, 0, read), read >= buffer.Length || !Short(response, read));
        }

        private static Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellation) => ReadBodyAsync(response, cancellation, null);

        // The whole body, at most MaxBodyBytes, read with the token (the caller closes the response when it fires); throws
        // CutOffException when it ends before the length it announced. `moved` hears each chunk.
        private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellation, Action moved)
        {
            long? announced = response.Content.Headers.ContentLength;
            if (announced > MaxBodyBytes) throw new OrbitersHttpException($"The answer is larger than {MaxBodyBytes / (1024 * 1024)} MB.", response.RequestMessage?.RequestUri?.AbsoluteUri, (int)response.StatusCode);
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var memory = new MemoryStream(announced > 0 ? (int)announced.Value : 16 * 1024))
            {
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                {
                    moved?.Invoke();
                    if (memory.Length + n > MaxBodyBytes) throw new OrbitersHttpException($"The answer is larger than {MaxBodyBytes / (1024 * 1024)} MB.", response.RequestMessage?.RequestUri?.AbsoluteUri, (int)response.StatusCode);
                    memory.Write(buffer, 0, n);
                }
                cancellation.ThrowIfCancellationRequested();
                if (Short(response, memory.Length)) throw new CutOffException();
                return memory.ToArray();
            }
        }

        // The body as text, in the charset it names (UTF-8 otherwise), without a byte order mark.
        private static Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken cancellation) => ReadTextAsync(response, cancellation, null);

        private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken cancellation, Action moved)
        {
            var bytes = await ReadBodyAsync(response, cancellation, moved).ConfigureAwait(false);
            var encoding = Encoding.UTF8;
            string charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrEmpty(charset))
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = Encoding.UTF8; }
            string text = encoding.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        /// <summary>
        /// Where a request may be sent on to: HTTPS anywhere, plain HTTP only to this computer (a development server), so
        /// never from HTTPS down to HTTP elsewhere. Null when <paramref name="location"/> is not such a place.
        /// </summary>
        public static Uri RedirectTarget(string from, string location)
        {
            if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(from, UriKind.Absolute, out Uri source) || !Uri.TryCreate(source, location.Trim(), out Uri target)) return null;
            if (target.Scheme == Uri.UriSchemeHttps) return target;
            return target.Scheme == Uri.UriSchemeHttp && Loopback(target) ? target : null;
        }

        private static bool Loopback(Uri uri) =>
            uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

        /// <summary>True when a transfer of <paramref name="received"/> bytes, announced as <paramref name="announced"/> (0 when not), goes past <paramref name="limit"/> (0: none).</summary>
        public static bool PastLimit(long received, long announced, bool encoded, long limit) =>
            limit > 0 && (received > limit || !encoded && announced > limit);

        // ---- Files ----

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destination"/> (replaced only once complete and, when given,
        /// matching <paramref name="expectedSha256"/>, hashed as it arrives). No answer and busy servers (429, 5xx) are tried
        /// again up to <paramref name="retries"/> times. <paramref name="expectedBytes"/> (0 when unknown) must be the size
        /// exactly, and <paramref name="maxBytes"/> (0: no limit) bounds it: a download announcing or bringing more stops at
        /// once, before it fills the disk. <paramref name="progress"/> gets (bytes, total; 0 when unknown).
        /// </summary>
        public static async Task<OrbitersTransferResult> DownloadFileAsync(string url, string destination, string token, Action<long, long> progress = null,
            CancellationToken cancellation = default, string expectedSha256 = null, int retries = 2, long expectedBytes = 0, long maxBytes = 0)
        {
            var report = Reporter(progress);
            long limit = expectedBytes > 0 && (maxBytes <= 0 || expectedBytes < maxBytes) ? expectedBytes : Math.Max(0, maxBytes);
            string part = destination + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new OrbitersTransferResult();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
                for (int attempt = 0; ; attempt++)
                {
                    result = await DownloadOnceAsync(url, part, token, report, cancellation, limit).ConfigureAwait(false);
                    if (result.Success || result.Cancelled || attempt >= retries || !Retryable(result)) break;
                    try { await Task.Delay(1000 * (attempt + 1) * (attempt + 1), cancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) { result = new OrbitersTransferResult { Cancelled = true, Error = "Cancelled." }; break; }
                }
                if (!result.Success) return result;
                long size = new FileInfo(part).Length;
                if (expectedBytes > 0 && size != expectedBytes)
                {
                    result.Success = false;
                    result.Error = $"The download is {Format(size)}, not the {Format(expectedBytes)} Orbiters published. Try again.";
                    return result;
                }
                if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(result.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = false;
                    result.Error = "The download does not match the file Orbiters published. Try again.";
                    return result;
                }
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(part, destination);
                return result;
            }
            catch (OperationCanceledException) { return new OrbitersTransferResult { Cancelled = true, Error = "Cancelled." }; }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool Retryable(OrbitersTransferResult result) => !result.TooLarge && (result.StatusCode == 0 || result.StatusCode == 429 || result.StatusCode >= 500);

        private static async Task<OrbitersTransferResult> DownloadOnceAsync(string url, string path, string token, Action<long, long, bool> progress, CancellationToken cancellation, long limit)
        {
            var result = new OrbitersTransferResult();
            using (var watch = new StallWatch(cancellation))
            {
                HttpResponseMessage response = null;
                CancellationTokenRegistration abort = default;
                try
                {
                    try { response = await FollowAsync(() => new HttpRequestMessage(HttpMethod.Get, url), url, token, true, null, watch.Token).ConfigureAwait(false); watch.Moved(); }
                    catch (OrbitersHttpException refused)
                    {
                        result.StatusCode = refused.StatusCode;
                        result.Error = refused.Message.Replace("The request was", "The download was");
                        return result;
                    }
                    Capture(response, result);
                    result.StatusCode = (int)response.StatusCode;
                    // Cancelled or stalled while the body comes: the connection is closed, which ends a read that waits.
                    abort = watch.Token.Register(response.Dispose);
                    if (!response.IsSuccessStatusCode)
                    {
                        var (body, complete) = await ReadLimitedAsync(response, watch.Token, watch.Moved).ConfigureAwait(false);
                        watch.Token.ThrowIfCancellationRequested();
                        result.Body = body;
                        result.CutOff = !complete;
                        result.Error = complete ? ServerError(body) ?? StatusMessage(result.StatusCode, response.ReasonPhrase) : CutOffMessage(result.StatusCode);
                        return result;
                    }
                    long total = response.Content.Headers.ContentLength ?? 0;
                    bool encoded = response.Content.Headers.ContentEncoding.Count > 0;
                    if (PastLimit(0, total, encoded, limit)) return TooLarge(result, limit);
                    using (var sha = SHA256.Create())
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[1 << 20];
                        long bytes = 0;
                        int n;
                        while ((n = await body.ReadAsync(buffer, 0, buffer.Length, watch.Token).ConfigureAwait(false)) > 0)
                        {
                            watch.Moved();
                            bytes += n;
                            if (PastLimit(bytes, 0, true, limit)) return TooLarge(result, limit);
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            await file.WriteAsync(buffer, 0, n, watch.Token).ConfigureAwait(false);
                            progress?.Invoke(bytes, total, false);
                        }
                        // Ended before the length it announced: tried again as a download that got no answer.
                        if (Short(response, bytes))
                        {
                            result.CutOff = true;
                            result.Error = CutOffMessage(result.StatusCode);
                            result.StatusCode = 0;
                            return result;
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        result.Sha256 = Hex(sha.Hash);
                        result.Bytes = bytes;
                    }
                    result.Success = true;
                    progress?.Invoke(result.Bytes, result.Bytes, true);
                    return result;
                }
                catch (Exception ex) when (Stopped(ex) && cancellation.IsCancellationRequested) { result.Cancelled = true; result.Error = "Cancelled."; return result; }
                catch (Exception ex) when (Stopped(ex) && watch.Stalled) { result.Error = $"The download stopped: no data arrived for {StallLimitSeconds:0} seconds."; return result; }
                // The connection failed or broke off: tried again as a download that got no answer.
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    result.Error = result.StatusCode == 0 ? StatusMessage(0) : "The download broke off: " + ex.Message;
                    result.StatusCode = 0;
                    return result;
                }
                finally
                {
                    abort.Dispose();
                    response?.Dispose();
                }
            }
        }

        private static OrbitersTransferResult TooLarge(OrbitersTransferResult result, long limit)
        {
            result.TooLarge = true;
            result.Error = $"The download is larger than the {Format(limit)} expected, so it was stopped.";
            return result;
        }

        /// <summary>
        /// Sends a multipart/form-data body (fields and files), streamed from the files as it goes. Returns the server's
        /// answer; an error answer carries its {"error"} message.
        /// </summary>
        public static async Task<OrbitersTransferResult> UploadMultipartAsync(string url, string token, IEnumerable<OrbitersFormPart> parts, Action<long, long> progress = null,
            CancellationToken cancellation = default, string method = "POST", IDictionary<string, string> headers = null)
        {
            var report = Reporter(progress);
            var result = new OrbitersTransferResult();
            // Read here (a caller's list may be lazy); the files are read on the thread pool as the body is sent.
            var list = (parts ?? Enumerable.Empty<OrbitersFormPart>()).ToList();
            using (var watch = new StallWatch(cancellation))
            {
                HttpResponseMessage response = null;
                MultipartBody content = null;
                var stripped = new List<string>();
                try
                {
                    long total = new MultipartBody(list, watch, report).Length;
                    // A new body per hop: a request disposes its body once sent.
                    HttpRequestMessage Make() => new HttpRequestMessage(new HttpMethod(method), url) { Content = content = new MultipartBody(list, watch, report) };
                    try
                    {
                        response = await FollowAsync(Make, url, token, true, headers, watch.Token, stripped).ConfigureAwait(false);
                    }
                    catch (OrbitersHttpException refused)
                    {
                        result.StatusCode = refused.StatusCode;
                        result.Error = refused.Message;
                        return result;
                    }
                    finally
                    {
                        if (stripped.Count > 0) result.Diagnostic = "Not sent on to another server after a redirect: " + string.Join(", ", stripped.Distinct()) + ".";
                    }
                    Capture(response, result);
                    result.StatusCode = (int)response.StatusCode;
                    result.Bytes = content.Sent;
                    // The answer is read within the stall watch too: closed when cancelled or stalled.
                    // Each chunk of it keeps the transfer alive: a slow answer is not a stalled one.
                    watch.Moved();
                    using (watch.Token.Register(response.Dispose))
                    {
                        try { result.Body = await ReadTextAsync(response, watch.Token, watch.Moved).ConfigureAwait(false); }
                        // A cut-off answer is a failure the caller decides about: the upload may or may not have been received, and
                        // it is never sent again by itself.
                        catch (Exception ex) when (!watch.Token.IsCancellationRequested && (ex is IOException || ex is HttpRequestException || ex is OrbitersHttpException))
                        {
                            result.Body = null;
                            result.CutOff = true;
                            result.Error = CutOffMessage(result.StatusCode) + " The upload may or may not have been received.";
                            return result;
                        }
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        result.Error = ServerError(result.Body) ?? StatusMessage(result.StatusCode, response.ReasonPhrase);
                        return result;
                    }
                    result.Success = true;
                    report?.Invoke(total, total, true);
                    return result;
                }
                catch (Exception ex) when (Stopped(ex) && cancellation.IsCancellationRequested) { result.Cancelled = true; result.Error = "The upload was cancelled."; return result; }
                catch (Exception ex) when (Stopped(ex) && watch.Stalled) { result.Error = $"The upload timed out: nothing moved for {StallLimitSeconds:0} seconds."; return result; }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    result.Error = result.StatusCode == 0 ? StatusMessage(0) : "The upload broke off: " + ex.Message;
                    return result;
                }
                finally
                {
                    response?.Dispose();
                    content?.Dispose();
                }
            }
        }

        // The body written as the previous uploader wrote it, byte for byte, from the files themselves: its length is known
        // up front (so it is sent with a Content-Length), and its progress is the bytes handed to the connection.
        private sealed class MultipartBody : HttpContent
        {
            private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
            private readonly List<(byte[] Head, OrbitersFormPart Part, long FileLength)> pieces = new List<(byte[], OrbitersFormPart, long)>();
            private readonly byte[] end;
            private readonly StallWatch watch;
            private readonly Action<long, long, bool> progress;
            public readonly long Length;
            public long Sent;

            public MultipartBody(List<OrbitersFormPart> parts, StallWatch watch, Action<long, long, bool> progress)
            {
                this.watch = watch;
                this.progress = progress;
                string boundary = "----OrbitersUpload" + Guid.NewGuid().ToString("N");
                foreach (var part in parts)
                {
                    string head = part.FilePath == null
                        ? $"--{boundary}\r\nContent-Disposition: form-data; name=\"{part.Name}\"\r\n\r\n{part.Value}"
                        : $"--{boundary}\r\nContent-Disposition: form-data; name=\"{part.Name}\"; filename=\"{part.FileName.Replace("\"", "")}\"\r\nContent-Type: {part.ContentType}\r\n\r\n";
                    long fileLength = part.FilePath != null ? new FileInfo(part.FilePath).Length : 0;
                    var bytes = Utf8.GetBytes(head);
                    pieces.Add((bytes, part, fileLength));
                    Length += bytes.Length + fileLength + 2;
                }
                end = Utf8.GetBytes($"--{boundary}--\r\n");
                Length += end.Length;
                Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                var newline = new byte[] { 13, 10 };
                var buffer = new byte[1 << 20];
                Sent = 0;
                async Task Write(byte[] bytes, int count)
                {
                    await stream.WriteAsync(bytes, 0, count, watch.Token).ConfigureAwait(false);
                    Sent += count;
                    watch.Moved();
                    progress?.Invoke(Sent, Length, false);
                }
                foreach (var (head, part, _) in pieces)
                {
                    await Write(head, head.Length).ConfigureAwait(false);
                    if (part.FilePath != null)
                        using (var file = new FileStream(part.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            int n;
                            while ((n = await file.ReadAsync(buffer, 0, buffer.Length, watch.Token).ConfigureAwait(false)) > 0) await Write(buffer, n).ConfigureAwait(false);
                        }
                    await Write(newline, 2).ConfigureAwait(false);
                }
                await Write(end, end.Length).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = Length;
                return true;
            }
        }

        // Stops a transfer when no byte moved for StallSeconds (checked each second on a timer).
        private sealed class StallWatch : IDisposable
        {
            private readonly CancellationTokenSource source;
            private readonly Timer timer;
            private readonly Stopwatch since = Stopwatch.StartNew();
            private long movedAt;
            public bool Stalled { get; private set; }
            public CancellationToken Token => source.Token;

            public StallWatch(CancellationToken cancellation)
            {
                source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timer = new Timer(_ => Check(), null, 1000, 1000);
            }

            public void Moved() => Interlocked.Exchange(ref movedAt, since.ElapsedMilliseconds);

            private void Check()
            {
                if (Stalled || since.ElapsedMilliseconds - Interlocked.Read(ref movedAt) < StallLimitSeconds * 1000) return;
                Stalled = true;
                try { source.Cancel(); } catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                timer.Dispose();
                source.Dispose();
            }
        }

        // Progress handed to the thread that started the transfer (the editor's), at most ten times a second and always the last.
        private static Action<long, long, bool> Reporter(Action<long, long> progress)
        {
            if (progress == null) return null;
            var context = SynchronizationContext.Current;
            var clock = Stopwatch.StartNew();
            long last = -1000;
            return (bytes, total, final) =>
            {
                long now = clock.ElapsedMilliseconds;
                if (!final && now - Interlocked.Read(ref last) < 100) return;
                Interlocked.Exchange(ref last, now);
                if (context != null) context.Post(_ => progress(bytes, total), null);
                else progress(bytes, total);
            };
        }

        private static void Capture(HttpResponseMessage response, OrbitersTransferResult result)
        {
            foreach (var header in response.Headers) result.Headers[header.Key] = string.Join(", ", header.Value);
            if (response.Content != null)
                foreach (var header in response.Content.Headers) result.Headers[header.Key] = string.Join(", ", header.Value);
        }

        private static string Hex(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        // "12.4 MB", as ArchiveBudget.Format writes sizes (that one is in the editor assembly).
        private static string Format(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.#} GB" :
            bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" :
            bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} bytes";
    }
}
#endif
