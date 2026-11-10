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
    /// Large transfers to and from Orbiters, shared by MCB and My Avatar, in the shapes they call: downloads and multipart
    /// uploads are <see cref="OrbitersHttp"/>'s (off the editor thread, hashed as they arrive, streamed from disk); the
    /// UnityWebRequest helpers (<see cref="WaitAsync"/>, <see cref="WriteMultipart"/>, <see cref="ErrorMessage"/>) remain for
    /// MCB's own requests until they move to <see cref="OrbitersHttp"/>. Call from the editor thread.
    /// </summary>
    public static class OrbitersTransfer
    {
        /// <summary>Seconds without a byte moving before a transfer counts as stalled and is aborted.</summary>
        public const double StallSeconds = OrbitersHttp.StallSeconds;
        /// <summary>Redirects a download follows at most.</summary>
        public const int MaxRedirects = OrbitersHttp.MaxRedirects;

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

            internal static Result From(OrbitersTransferResult answer)
            {
                var result = new Result
                {
                    Success = answer.Success, Cancelled = answer.Cancelled, StatusCode = answer.StatusCode, Error = answer.Error, Body = answer.Body,
                    Sha256 = answer.Sha256, Bytes = answer.Bytes, TooLarge = answer.TooLarge,
                };
                foreach (var pair in answer.Headers) result.Headers[pair.Key] = pair.Value;
                return result;
            }
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
            CancellationToken cancellation = default, string expectedSha256 = null, int retries = 2, long expectedBytes = 0, long maxBytes = 0) =>
            Result.From(await OrbitersHttp.DownloadFileAsync(url, destination, token, progress == null ? null : new Action<long, long>(progress), cancellation,
                expectedSha256, retries, expectedBytes, maxBytes));

        /// <summary>
        /// Where a download may be sent on to: HTTPS anywhere, plain HTTP only to this computer (a development server), so
        /// never from HTTPS down to HTTP elsewhere. Null when <paramref name="location"/> is not such a place.
        /// </summary>
        public static Uri RedirectTarget(string from, string location) => OrbitersHttp.RedirectTarget(from, location);

        /// <summary>True when a transfer of <paramref name="received"/> bytes, announced as <paramref name="announced"/> (0 when not), goes past <paramref name="limit"/> (0: none).</summary>
        internal static bool PastLimit(long received, long announced, bool encoded, long limit) => OrbitersHttp.PastLimit(received, announced, encoded, limit);

        /// <summary>The server's {"error": "..."} message, or the HTTP status.</summary>
        public static string ErrorMessage(UnityWebRequest request, string body) =>
            OrbitersHttp.ServerError(body) ?? OrbitersHttp.StatusMessage(request.responseCode, request.error);

        /// <summary>One field or file of a multipart/form-data body.</summary>
        public sealed class Part
        {
            public string Name, Value, FilePath, FileName, ContentType;
            public static Part Field(string name, string value) => new Part { Name = name, Value = value ?? string.Empty };
            public static Part File(string name, string path, string fileName = null, string contentType = "application/octet-stream") =>
                new Part { Name = name, FilePath = path, FileName = fileName ?? Path.GetFileName(path), ContentType = contentType };

            internal OrbitersFormPart ToForm() => new OrbitersFormPart { Name = Name, Value = Value, FilePath = FilePath, FileName = FileName, ContentType = ContentType };
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
        /// Sends a multipart body (fields and files), streamed from the files (<see cref="OrbitersHttp.UploadMultipartAsync"/>).
        /// Returns the server's answer; a 4xx/5xx carries its {"error"} message.
        /// </summary>
        public static async Task<Result> UploadMultipartAsync(string url, string token, IEnumerable<Part> parts, Progress progress = null,
            CancellationToken cancellation = default, string method = UnityWebRequest.kHttpVerbPOST, IDictionary<string, string> headers = null) =>
            Result.From(await OrbitersHttp.UploadMultipartAsync(url, token, (parts ?? Enumerable.Empty<Part>()).Select(p => p.ToForm()).ToList(),
                progress == null ? null : new Action<long, long>(progress), cancellation, method, headers));

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
