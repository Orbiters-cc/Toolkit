using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using UnityEditor;
using UnityEngine.TestTools;

// The shared downloader (audit 2026-10-28, SEC-04/SEC-05): redirects are checked hop by hop (HTTPS, or HTTP to this
// computer only) and lose the token after the first, and a download stops as soon as it announces or brings more than
// allowed. Served by a small HTTP server on 127.0.0.1 inside the test.
public sealed class OrbitersTransferTests
{
    [TestCase("https://orbiters.cc/api/x", "https://storage.example.com/blob?sig=1", "https://storage.example.com/blob?sig=1")]
    [TestCase("https://orbiters.cc/api/x", "/other", "https://orbiters.cc/other")]
    [TestCase("http://127.0.0.1:4100/api/x", "http://127.0.0.1:9000/blob", "http://127.0.0.1:9000/blob")]
    [TestCase("https://orbiters.cc/api/x", "http://localhost:9000/blob", "http://localhost:9000/blob")]
    [TestCase("https://orbiters.cc/api/x", "http://[::1]:9000/blob", "http://[::1]:9000/blob")]
    [TestCase("https://orbiters.cc/api/x", "http://storage.example.com/blob?sig=1", null)]
    [TestCase("http://127.0.0.1:4100/api/x", "http://127.0.0.1.example.com/blob", null)]
    [TestCase("https://orbiters.cc/api/x", "ftp://storage.example.com/blob", null)]
    [TestCase("https://orbiters.cc/api/x", "file:///C:/Windows/win.ini", null)]
    [TestCase("https://orbiters.cc/api/x", "", null)]
    public void RedirectsGoOnlyToHttpsOrThisComputer(string from, string location, string expected) =>
        Assert.That(OrbitersTransfer.RedirectTarget(from, location)?.AbsoluteUri, Is.EqualTo(expected == null ? null : new Uri(expected).AbsoluteUri));

    [Test] public void TheLimitCountsAnnouncedAndArrivingBytes()
    {
        Assert.That(OrbitersTransfer.PastLimit(10, 0, false, 0), Is.False, "no limit");
        Assert.That(OrbitersTransfer.PastLimit(10, 100, false, 50), Is.True, "announced past it");
        Assert.That(OrbitersTransfer.PastLimit(10, 100, true, 50), Is.False, "a compressed body's announced size is not its size");
        Assert.That(OrbitersTransfer.PastLimit(51, 0, true, 50), Is.True, "arrived past it");
        Assert.That(OrbitersTransfer.PastLimit(50, 50, false, 50), Is.False, "exactly the limit");
    }

    private Server server;
    private string folder;

    [SetUp] public void SetUp()
    {
        server = new Server();
        folder = Path.Combine(Path.GetTempPath(), "orbiters-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    [TearDown] public void TearDown()
    {
        server.Dispose();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static IEnumerator Wait(Task task)
    {
        double deadline = EditorApplication.timeSinceStartup + 60;
        while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
        Assert.That(task.IsCompleted, Is.True, "The transfer did not finish.");
    }

    [UnityTest] public IEnumerator ADownloadLargerThanAllowedStopsAndLeavesNoFile()
    {
        server.Routes["/announced"] = _ => Server.Ok(new byte[200000]);
        server.Routes["/unannounced"] = _ => Server.Ok(new byte[200000], announce: false);
        foreach (string path in new[] { "/announced", "/unannounced" })
        {
            string destination = Path.Combine(folder, "file" + path.Substring(1));
            var task = OrbitersTransfer.DownloadFileAsync(server.Url + path, destination, null, maxBytes: 1000);
            yield return Wait(task);
            Assert.That(task.Result.Success, Is.False, path);
            Assert.That(task.Result.TooLarge, Is.True, path + ": " + task.Result.Error);
            Assert.That(File.Exists(destination), Is.False, path);
            Assert.That(Directory.GetFiles(folder), Is.Empty, path + ": no part file is left");
        }
    }

    [UnityTest] public IEnumerator ADownloadMustBeThePublishedSize()
    {
        server.Routes["/file"] = _ => Server.Ok(new byte[100]);
        string destination = Path.Combine(folder, "file");
        var shorter = OrbitersTransfer.DownloadFileAsync(server.Url + "/file", destination, null, expectedBytes: 101);
        yield return Wait(shorter);
        Assert.That(shorter.Result.Success, Is.False);
        StringAssert.Contains("not the", shorter.Result.Error);
        var longer = OrbitersTransfer.DownloadFileAsync(server.Url + "/file", destination, null, expectedBytes: 99);
        yield return Wait(longer);
        Assert.That(longer.Result.Success, Is.False);
        Assert.That(longer.Result.TooLarge, Is.True);
        var exact = OrbitersTransfer.DownloadFileAsync(server.Url + "/file", destination, null, expectedBytes: 100, maxBytes: 1 << 20);
        yield return Wait(exact);
        Assert.That(exact.Result.Success, Is.True, exact.Result.Error);
        Assert.That(new FileInfo(destination).Length, Is.EqualTo(100));
    }

    [UnityTest] public IEnumerator RedirectsAreFollowedWithoutTheTokenAndOnlyToAllowedPlaces()
    {
        server.Routes["/api/download"] = _ => Server.Redirect("/storage/blob?sig=abc");
        server.Routes["/storage/blob"] = _ => Server.Ok(Encoding.UTF8.GetBytes("payload"));
        server.Routes["/api/elsewhere"] = _ => Server.Redirect("http://storage.example.invalid/blob?sig=abc");
        string destination = Path.Combine(folder, "file");
        var task = OrbitersTransfer.DownloadFileAsync(server.Url + "/api/download", destination, "secret-token");
        yield return Wait(task);
        Assert.That(task.Result.Success, Is.True, task.Result.Error);
        Assert.That(File.ReadAllText(destination), Is.EqualTo("payload"));
        Assert.That(server.Authorization("/api/download"), Is.EqualTo("Bearer secret-token"));
        Assert.That(server.Authorization("/storage/blob"), Is.Null, "the storage hop never gets the account token");

        var refused = OrbitersTransfer.DownloadFileAsync(server.Url + "/api/elsewhere", Path.Combine(folder, "other"), "secret-token");
        yield return Wait(refused);
        Assert.That(refused.Result.Success, Is.False);
        StringAssert.Contains("not HTTPS", refused.Result.Error);
    }

    // OPT-07: the multipart body is written on a worker thread: the call returns before a large file is copied.
    [UnityTest] public IEnumerator UploadsPrepareTheirBodyOffTheEditorThread()
    {
        server.Routes["/upload"] = request => Server.Ok(Encoding.UTF8.GetBytes("{\"id\":1}"));
        string file = Path.Combine(folder, "package.bin");
        using (var stream = File.Create(file)) stream.SetLength(64L * 1024 * 1024);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var task = OrbitersTransfer.UploadMultipartAsync(server.Url + "/upload", null, new[] { OrbitersTransfer.Part.Field("name", "Hat"), OrbitersTransfer.Part.File("package", file) });
        long returned = watch.ElapsedMilliseconds;
        yield return Wait(task);
        long done = watch.ElapsedMilliseconds;
        Assert.That(task.Result.Success, Is.True, task.Result.Error);
        Assert.That(server.Received("/upload"), Is.GreaterThan(64L * 1024 * 1024));
        UnityEngine.Debug.Log($"[Orbiters test] 64 MB upload: the call returned after {returned} ms, finished after {done} ms.");
        Assert.That(returned, Is.LessThan(Math.Max(50, done / 4)), "the editor thread is not held while the body is written");
    }

    // XPK-18: the shared client hashes a download as it arrives (no second read of the file).
    [UnityTest] public IEnumerator ADownloadIsHashedAsItArrives()
    {
        var bytes = new byte[300000];
        new Random(7).NextBytes(bytes);
        server.Routes["/file"] = _ => Server.Ok(bytes);
        string destination = Path.Combine(folder, "file");
        var task = OrbitersTransfer.DownloadFileAsync(server.Url + "/file", destination, null);
        yield return Wait(task);
        Assert.That(task.Result.Success, Is.True, task.Result.Error);
        Assert.That(task.Result.Sha256, Is.EqualTo(UnityPackageFiles.FileHash(destination)));
        Assert.That(task.Result.Bytes, Is.EqualTo(bytes.Length));
    }

    // The upload streams the same bytes the temporary-file writer wrote: same parts, same length (boundaries are as long).
    [UnityTest] public IEnumerator AnUploadSendsTheSameBodyAsTheFileWriter()
    {
        server.Routes["/upload"] = _ => Server.Answer(200, "{\"id\":1}");
        string file = Path.Combine(folder, "package.bin");
        File.WriteAllBytes(file, new byte[12345]);
        var parts = new[] { OrbitersTransfer.Part.Field("metadata", "{\"name\":\"Hat é\"}"), OrbitersTransfer.Part.File("package", file) };
        var task = OrbitersTransfer.UploadMultipartAsync(server.Url + "/upload", "secret-token", parts);
        yield return Wait(task);
        Assert.That(task.Result.Success, Is.True, task.Result.Error);
        Assert.That(task.Result.Body, Is.EqualTo("{\"id\":1}"));
        string written = Path.Combine(folder, "body.tmp");
        OrbitersTransfer.WriteMultipart(written, "----OrbitersUpload" + Guid.NewGuid().ToString("N"), parts);
        Assert.That(server.Received("/upload"), Is.EqualTo(new FileInfo(written).Length));
        Assert.That(server.Authorization("/upload"), Is.EqualTo("Bearer secret-token"));
    }

    [UnityTest] public IEnumerator ApiErrorsCarryTheServersMessage()
    {
        server.Routes["/api/broken"] = _ => Server.Answer(409, "{\"error\":\"Taken \\u00e9\",\"detail\":{\"error\":\"inner\"}}");
        var task = OrbitersHttp.JsonAsync<object>(System.Net.Http.HttpMethod.Post, server.Url + "/api/broken", new { a = 1 });
        yield return Wait(task);
        var error = task.Exception?.GetBaseException() as OrbitersHttpException;
        Assert.That(error, Is.Not.Null);
        Assert.That(error.StatusCode, Is.EqualTo(409));
        Assert.That(error.Message, Is.EqualTo("Taken é"), "the top-level error, escapes decoded");
        Assert.That(error, Is.InstanceOf<InvalidOperationException>(), "as the API client's errors always were");

        var old = OrbitersApi.SendAsync<object>(server.Url + "/api/broken", null, new { a = 1 });
        yield return Wait(old);
        Assert.That(old.Exception?.GetBaseException(), Is.InstanceOf<InvalidOperationException>());
        Assert.That(old.Exception.GetBaseException().Message, Is.EqualTo("Taken é"));
    }

    [UnityTest] public IEnumerator NoAnswerIsOfflineAndTheOldClientStillThrowsItsNetworkError()
    {
        // A port nothing listens on.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        string url = "http://127.0.0.1:" + port + "/api/x";
        var task = OrbitersHttp.JsonAsync<object>(System.Net.Http.HttpMethod.Post, url, new { a = 1 });
        yield return Wait(task);
        var error = task.Exception?.GetBaseException() as OrbitersHttpException;
        Assert.That(error, Is.Not.Null);
        Assert.That(error.Offline, Is.True);
        Assert.That(OrbitersHttp.Offline, Is.True);

        var old = OrbitersApi.SendAsync<object>(url, null, new { a = 1 });
        yield return Wait(old);
        Assert.That(old.Exception?.GetBaseException(), Is.InstanceOf<System.Net.Http.HttpRequestException>());
    }

    [UnityTest] public IEnumerator ApiRedirectsKeepTheTokenOnlyOnTheSameServer()
    {
        server.Routes["/api/old"] = _ => Server.Redirect("/api/new");
        server.Routes["/api/new"] = _ => Server.Answer(200, "{\"ok\":true}");
        var task = OrbitersHttp.JsonAsync<Dictionary<string, bool>>(System.Net.Http.HttpMethod.Get, server.Url + "/api/old", null, "secret-token");
        yield return Wait(task);
        Assert.That(task.Exception, Is.Null);
        Assert.That(task.Result["ok"], Is.True);
        Assert.That(server.Authorization("/api/new"), Is.EqualTo("Bearer secret-token"));
    }

    [Test] public void ServerErrorsAreReadFromTheTopLevelOnly()
    {
        Assert.That(OrbitersHttp.ServerError("{\"error\":\"a \\\"quote\\\"\"}"), Is.EqualTo("a \"quote\""));
        Assert.That(OrbitersHttp.ServerError("{\"data\":{\"error\":\"nested\"}}"), Is.Null);
        Assert.That(OrbitersHttp.ServerError("<html>502</html>"), Is.Null);
        Assert.That(OrbitersHttp.ServerError(null), Is.Null);
    }

    // A minimal HTTP/1.1 server: one request per connection, answers from Routes, remembers each path's Authorization.
    private sealed class Server : IDisposable
    {
        public readonly ConcurrentDictionary<string, Func<string, byte[]>> Routes = new ConcurrentDictionary<string, Func<string, byte[]>>();
        private readonly ConcurrentDictionary<string, string> authorization = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, long> received = new ConcurrentDictionary<string, long>();
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        public readonly string Url;

        public Server()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(Loop);
        }

        public string Authorization(string path) => authorization.TryGetValue(path, out var value) ? value : null;
        public long Received(string path) => received.TryGetValue(path, out var value) ? value : 0;

        public static byte[] Ok(byte[] body, bool announce = true)
        {
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" + (announce ? "Content-Length: " + body.Length + "\r\n" : "") + "Connection: close\r\n\r\n");
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }

        public static byte[] Answer(int status, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Status\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }

        public static byte[] Redirect(string location) =>
            Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        private async Task Loop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    var head = new StringBuilder();
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int b = stream.ReadByte();
                        if (b < 0) return;
                        head.Append((char)b);
                    }
                    var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string target = lines[0].Split(' ')[1];
                    string path = target.Split('?')[0];
                    long length = 0;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization[path] = line.Substring(14).Trim();
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) long.TryParse(line.Substring(15).Trim(), out length);
                    }
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    while (total < length) { int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - total)); if (n <= 0) break; total += n; }
                    received[path] = total;
                    var answer = Routes.TryGetValue(path, out var route) ? route(target) : Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    stream.Write(answer, 0, answer.Length);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
