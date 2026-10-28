using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Orbiters.Toolkit.Editor.Net
{
    /// <summary>
    /// Large transfers to and from Orbiters, shared by MCB and My Avatar: downloads to a file with progress, retries and a
    /// checksum, and streaming multipart uploads with progress, cancellation and stall detection. Redirects are followed
    /// one hop at a time, each checked (HTTPS only, plain HTTP only to this computer), and without the account token past
    /// the first (a signed storage URL is its own credential). Call from the editor thread.
    /// </summary>
    public static class OrbitersTransfer
    {
        /// <summary>Seconds without a byte moving before a transfer counts as stalled and is aborted.</summary>
        public const double StallSeconds = 120d;
        /// <summary>Redirects a download follows at most.</summary>
        public const int MaxRedirects = 5;

        public enum End { Done, Cancelled, Stalled }

        public sealed class Result
        {
            public bool Success, Cancelled;
            public long StatusCode;
            public string Error, Body, Sha256;
            public long Bytes;
            /// <summary>A download stopped because it was (or announced it was) larger than allowed.</summary>
            public bool TooLarge;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>Progress of a running transfer: bytes moved and total (0 when unknown).</summary>
        public delegate void Progress(long bytes, long total);

        /// <summary>
        /// Waits for a sent request, reporting progress each editor tick; aborts on cancellation or when no byte moved for
        /// <paramref name="stallSeconds"/>.
        /// </summary>
        public static async Task<End> WaitAsync(UnityWebRequest request, UnityWebRequestAsyncOperation operation, Action<UnityWebRequest> tick,
            CancellationToken cancellation, double stallSeconds = StallSeconds, bool upload = false)
        {
            ulong last = 0;
            double movedAt = EditorApplication.timeSinceStartup;
            var end = End.Done;
            while (!operation.isDone)
            {
                ulong moved = upload ? request.uploadedBytes : request.downloadedBytes;
                if (moved != last) { last = moved; movedAt = EditorApplication.timeSinceStartup; }
                tick?.Invoke(request);
                if (end == End.Done && cancellation.IsCancellationRequested) { end = End.Cancelled; request.Abort(); }
                else if (end == End.Done && stallSeconds > 0 && EditorApplication.timeSinceStartup - movedAt > stallSeconds) { end = End.Stalled; request.Abort(); }
                await Task.Yield();
            }
            return end;
        }

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destination"/> (replaced only once complete and, when given,
        /// matching <paramref name="expectedSha256"/>). Network failures and server errors (5xx, 429) are retried.
        /// <paramref name="expectedBytes"/> (the size Orbiters published, 0 when unknown) must be the size exactly, and
        /// <paramref name="maxBytes"/> (0: no limit) bounds it: a download announcing or bringing more stops at once, before
        /// it fills the disk.
        /// </summary>
        public static async Task<Result> DownloadFileAsync(string url, string destination, string token, Progress progress = null,
            CancellationToken cancellation = default, string expectedSha256 = null, int retries = 2, long expectedBytes = 0, long maxBytes = 0)
        {
            long limit = expectedBytes > 0 && (maxBytes <= 0 || expectedBytes < maxBytes) ? expectedBytes : Math.Max(0, maxBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            string part = destination + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new Result();
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    result = await DownloadOnceAsync(url, part, token, progress, cancellation, limit);
                    if (result.Success || result.Cancelled || attempt >= retries || !Retryable(result)) break;
                    await Task.Delay(1000 * (attempt + 1) * (attempt + 1), cancellation).ContinueWith(_ => { });
                    if (cancellation.IsCancellationRequested) { result = new Result { Cancelled = true, Error = "Cancelled." }; break; }
                }
                if (!result.Success) return result;
                long size = new FileInfo(part).Length;
                if (expectedBytes > 0 && size != expectedBytes)
                {
                    result.Success = false;
                    result.Error = $"The download is {Storage.ArchiveBudget.Format(size)}, not the {Storage.ArchiveBudget.Format(expectedBytes)} Orbiters published. Try again.";
                    return result;
                }
                result.Sha256 = await Task.Run(() => UnityPackageFiles.FileHash(part), cancellation);
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
            catch (OperationCanceledException) { return new Result { Cancelled = true, Error = "Cancelled." }; }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool Retryable(Result result) => !result.TooLarge && (result.StatusCode == 0 || result.StatusCode == 429 || result.StatusCode >= 500);

        /// <summary>
        /// Where a download may be sent on to: HTTPS anywhere, plain HTTP only to this computer (a development server), so
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
        internal static bool PastLimit(long received, long announced, bool encoded, long limit) =>
            limit > 0 && (received > limit || !encoded && announced > limit);

        private static async Task<Result> DownloadOnceAsync(string url, string path, string token, Progress progress, CancellationToken cancellation, long limit)
        {
            var result = new Result();
            UnityWebRequest request = null;
            try
            {
                var end = End.Done;
                // Each hop by hand: the token goes to the first only, and every place it is sent on to is checked.
                for (int hop = 0; ; hop++)
                {
                    request?.Dispose();
                    request = Get(url, path, hop == 0 ? token : null);
                    end = await WaitAsync(request, request.SendWebRequest(), r =>
                    {
                        long total = Total(r);
                        progress?.Invoke((long)r.downloadedBytes, total);
                        // DownloadHandlerFile has no limit of its own: an announced or arriving size past it stops the download
                        // (a redirect's own body does not count; a compressed body's announced size is not its size).
                        if (!result.TooLarge && PastLimit((long)r.downloadedBytes, Redirect(r) ? 0 : total, r.GetResponseHeader("Content-Encoding") != null, limit))
                        {
                            result.TooLarge = true;
                            r.Abort();
                        }
                    }, cancellation);
                    Capture(request, result);
                    // Finished between two looks: what arrived counts as well.
                    if (end == End.Done && !result.TooLarge && !Redirect(request) && PastLimit((long)request.downloadedBytes, 0, true, limit)) result.TooLarge = true;
                    if (end != End.Done || result.TooLarge || !Redirect(request)) break;
                    var target = RedirectTarget(url, request.GetResponseHeader("Location"));
                    result.StatusCode = request.responseCode;
                    if (target == null) { result.Error = "The download was sent on to a place Orbiters does not download from (not HTTPS)."; return result; }
                    if (hop + 1 >= MaxRedirects) { result.Error = "The download was redirected too many times."; return result; }
                    url = target.AbsoluteUri;
                    result.Headers.Clear();
                }
                result.Cancelled = end == End.Cancelled;
                result.StatusCode = request.responseCode;
                result.Bytes = (long)request.downloadedBytes;
                if (result.TooLarge) { result.Error = $"The download is larger than the {Storage.ArchiveBudget.Format(limit)} expected, so it was stopped."; return result; }
                if (end == End.Stalled) { result.Error = $"The download stopped: no data arrived for {StallSeconds:0} seconds."; return result; }
                if (result.Cancelled) { result.Error = "Cancelled."; return result; }
                if (request.result == UnityWebRequest.Result.Success) { result.Success = true; progress?.Invoke(result.Bytes, result.Bytes); return result; }
                result.Error = ErrorMessage(request, File.Exists(path) && new FileInfo(path).Length < 64 * 1024 ? File.ReadAllText(path) : null);
                return result;
            }
            finally { request?.Dispose(); }
        }

        private static bool Redirect(UnityWebRequest request) =>
            request.responseCode >= 300 && request.responseCode < 400 && !string.IsNullOrWhiteSpace(request.GetResponseHeader("Location"));

        private static UnityWebRequest Get(string url, string path, string token)
        {
            // Redirects are followed by hand (DownloadOnceAsync), never by Unity.
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET) { downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true }, timeout = 0, redirectLimit = 0 };
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
            return request;
        }

        private static long Total(UnityWebRequest request) =>
            long.TryParse(request.GetResponseHeader("Content-Length"), out long length) ? length : 0;

        private static void Capture(UnityWebRequest request, Result result)
        {
            var headers = request.GetResponseHeaders();
            if (headers == null) return;
            foreach (var pair in headers) result.Headers[pair.Key] = pair.Value;
        }

        /// <summary>The server's {"error": "..."} message, or the HTTP status.</summary>
        public static string ErrorMessage(UnityWebRequest request, string body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                int key = body.IndexOf("\"error\"", StringComparison.Ordinal);
                if (key >= 0)
                {
                    int start = body.IndexOf('"', body.IndexOf(':', key) + 1);
                    int end = start >= 0 ? body.IndexOf('"', start + 1) : -1;
                    while (end > 0 && body[end - 1] == '\\') end = body.IndexOf('"', end + 1);
                    if (start >= 0 && end > start) return Unescape(body.Substring(start + 1, end - start - 1));
                }
            }
            if (request.responseCode == 0) return "Orbiters could not be reached. Check your connection and try again.";
            return $"Orbiters answered HTTP {request.responseCode}{(string.IsNullOrEmpty(request.error) ? "" : " (" + request.error + ")")}.";
        }

        private static string Unescape(string escaped) => escaped.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\\\", "\\");

        /// <summary>One field or file of a multipart/form-data body.</summary>
        public sealed class Part
        {
            public string Name, Value, FilePath, FileName, ContentType;
            public static Part Field(string name, string value) => new Part { Name = name, Value = value ?? string.Empty };
            public static Part File(string name, string path, string fileName = null, string contentType = "application/octet-stream") =>
                new Part { Name = name, FilePath = path, FileName = fileName ?? Path.GetFileName(path), ContentType = contentType };
        }

        /// <summary>Writes a multipart/form-data body to <paramref name="bodyPath"/>, streaming files with a bounded buffer.</summary>
        public static void WriteMultipart(string bodyPath, string boundary, IEnumerable<Part> parts)
        {
            var utf8 = new UTF8Encoding(false);
            using (var body = new FileStream(bodyPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                void Text(string text) { byte[] bytes = utf8.GetBytes(text); body.Write(bytes, 0, bytes.Length); }
                foreach (var part in parts)
                {
                    Text($"--{boundary}\r\n");
                    if (part.FilePath == null)
                    {
                        Text($"Content-Disposition: form-data; name=\"{part.Name}\"\r\n\r\n");
                        Text(part.Value);
                    }
                    else
                    {
                        Text($"Content-Disposition: form-data; name=\"{part.Name}\"; filename=\"{part.FileName.Replace("\"", "")}\"\r\n");
                        Text($"Content-Type: {part.ContentType}\r\n\r\n");
                        using (var file = System.IO.File.OpenRead(part.FilePath)) file.CopyTo(body, 1024 * 1024);
                    }
                    Text("\r\n");
                }
                Text($"--{boundary}--\r\n");
            }
        }

        /// <summary>
        /// Sends a multipart body (fields and files) streamed from a temporary file. Returns the server's answer; a 4xx/5xx
        /// carries its {"error"} message.
        /// </summary>
        public static async Task<Result> UploadMultipartAsync(string url, string token, IEnumerable<Part> parts, Progress progress = null,
            CancellationToken cancellation = default, string method = UnityWebRequest.kHttpVerbPOST, IDictionary<string, string> headers = null)
        {
            string boundary = "----OrbitersUpload" + Guid.NewGuid().ToString("N");
            string bodyPath = Path.Combine(Path.GetTempPath(), $"orbiters_upload_{Guid.NewGuid():N}.tmp");
            var result = new Result();
            // Read here (a caller's list may be lazy), written on a worker thread: copying a large file never holds up the editor.
            var list = (parts ?? Enumerable.Empty<Part>()).ToList();
            try
            {
                try { await Task.Run(() => WriteMultipart(bodyPath, boundary, list), cancellation); }
                catch (OperationCanceledException) { return new Result { Cancelled = true, Error = "The upload was cancelled." }; }
                long total = new FileInfo(bodyPath).Length;
                using (var request = new UnityWebRequest(url, method))
                {
                    request.uploadHandler = new UploadHandlerFile(bodyPath);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 0;
                    request.SetRequestHeader("Content-Type", $"multipart/form-data; boundary={boundary}");
                    if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                    if (headers != null) foreach (var pair in headers) request.SetRequestHeader(pair.Key, pair.Value);
                    var end = await WaitAsync(request, request.SendWebRequest(), r => progress?.Invoke((long)r.uploadedBytes, total), cancellation, upload: true);
                    Capture(request, result);
                    result.StatusCode = request.responseCode;
                    result.Bytes = (long)request.uploadedBytes;
                    result.Cancelled = end == End.Cancelled;
                    try { result.Body = request.downloadHandler?.text; } catch (Exception) { result.Body = null; }
                    if (result.Cancelled) { result.Error = "The upload was cancelled."; return result; }
                    if (end == End.Stalled) { result.Error = $"The upload timed out: no data was sent for {StallSeconds:0} seconds."; return result; }
                    if (request.result != UnityWebRequest.Result.Success) { result.Error = ErrorMessage(request, result.Body); return result; }
                    result.Success = true;
                    progress?.Invoke(total, total);
                    return result;
                }
            }
            finally
            {
                try { if (File.Exists(bodyPath)) File.Delete(bodyPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>"12.4 MB of 80 MB".</summary>
        public static string Describe(long bytes, long total) =>
            total > 0 ? $"{Storage.ArchiveBudget.Format(bytes)} of {Storage.ArchiveBudget.Format(total)}" : Storage.ArchiveBudget.Format(bytes);

        internal static void LogFailure(string what, Result result)
        {
            if (result == null || result.Success || result.Cancelled) return;
            Debug.LogWarning($"[Orbiters] {what}: {result.Error}");
        }
    }
}
